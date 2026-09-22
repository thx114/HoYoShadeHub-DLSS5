using CommunityToolkit.Mvvm.Messaging;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.GameSelector;
using HoYoShadeHub.Features.ViewHost;
using HoYoShadeHub.Features.Xxmi;
using HoYoShadeHub.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// DLSS5 兼容性检测：15 条检查（用户列的清单），7/9/10/11/12/13/14/15 带自动修复。
///
/// <para>
/// 检测卡片右上角「指定主程序」右边那个按钮弹出来的窗口就是跑这个。
/// 所有会改盘的动作（写入 ini、改名 dll、删超链接）都在<b>这里</b>，页面只负责显示。
/// </para>
/// </summary>
internal class Dlss5CompatibilityCheck
{
    private static readonly ILogger Logger = AppConfig.GetLogger<Dlss5CompatibilityCheck>();

    /// <summary>13/15：常见的第三方注入型 dll（reshade/3dmigoto/optiscaler 那一挂）</summary>
    private static readonly string[] ThirdPartyDllHints =
    [
        "d3d11.dll", "dxgi.dll", "d3d12.dll", "d3d9.dll", "opengl32.dll", "version.dll",
        "winmm.dll", "wininet.dll", "dinput8.dll", "dsound.dll", "dxgi.dll", "ReShade64.dll",
        "ReShade32.dll", "OptiScaler.dll", "nvngx.dll", "dlssg_to_fsr3_amd_is_better.dll",
        "fakenvapi.dll", "dlss-enabler-upscaler.dll", "dbghelp.dll", "winhttp.dll",
    ];

    /// <summary>13：我们自己的东西，不算「第三方」</summary>
    private static readonly string[] OwnDllHints =
    [
        "renodx", "hysx", "hoyoshade",
    ];

    /// <summary>14：这个键超过 3 就算高</summary>
    public const int MaxDirectNeuralRenderingPassCount = 3;

    public static async Task<List<Dlss5CompatItem>> RunAsync(Dlss5CompatContext context)
    {
        var items = new List<Dlss5CompatItem>();

        // 1~6 显卡 / 驱动：不需要游戏就能查
        items.Add(CheckOverclock());
        AddDriverCheck(items, context);
        items.Add(CheckDynamicVibrance());
        items.Add(CheckRtxHdr());
        items.Add(CheckAiFrameGeneration(context));
        items.Add(CheckGpuModel());

        // 7~11 启动器（当前 HoYoShade）
        items.Add(CheckHostChinesePath(context));
        items.Add(CheckEnabledPlugins(context));
        items.Add(CheckHookMode(context));
        items.Add(CheckShadersAndTextures(context));
        items.Add(CheckInjectExe(context));

        // 12~14 游戏目录
        items.Add(CheckIniPaths(context));
        items.Add(CheckThirdPartyDlls(context));
        items.Add(CheckPassCount(context));

        // 15 XXMI（有才检测）
        items.Add(CreateXxmiCheck(context));

        // 逐条跑修复用的探测（这些检查本身是同步读盘，快速；放 Task.Run 外面更简单）
        return await Task.FromResult(items);
    }

    #region 1 显卡是否超频

    private static Dlss5CompatItem CheckOverclock()
    {
        var item = new Dlss5CompatItem(1, "显卡", "显卡是否超频");

        List<string> found = [];

        // MSI Afterburner / RivaTuner（最常用的超频工具），进程或安装目录都算
        found.AddRange(RunningProcesses(["MSIAfterburner", "RTSS", "RivaTuner"]));
        found.AddRange(InstalledUnder(
            [
                (Environment.SpecialFolder.ProgramFilesX86, "MSI Afterburner"),
                (Environment.SpecialFolder.ProgramFiles, "MSI Afterburner"),
                (Environment.SpecialFolder.ProgramFilesX86, "RivaTuner Statistics Server"),
            ]));

        // EVGA Precision X1 / ASUS GPU Tweak / 影驰魔盘 之类
        found.AddRange(RunningProcesses(["PrecisionX", "PrecisionX_x64", "GPUTweakII", "GPUTweakIII", "ASUSGPUTweak", "GalaxXtremeTuner", "FireStorm"]));

        if (found.Count == 0)
        {
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = "没有检测到常见的显卡超频工具（MSI Afterburner / RTSS / EVGA Precision / GPU Tweak 等）。" +
                           "用驱动自带面板（NV App  性能  自动调优）超频的查不出来，麻烦自己确认一下。";
            return item;
        }

        item.Level = Dlss5CheckLevel.Warning;
        item.Message = "检测到可能在做超频/调压的工具：" + string.Join("、", found.Distinct()) +
                       "。DLSS5 神经渲染对显存/核心稳定性很敏感，超频不稳会表现为花屏、掉帧、插件挂不上；" +
                       "出问题先把超频退回默认再试。";
        return item;
    }

    private static List<string> RunningProcesses(IEnumerable<string> names)
    {
        List<string> result = [];

        foreach (string name in names)
        {
            try
            {
                if (Process.GetProcessesByName(name).Length > 0)
                {
                    result.Add(name + "（正在运行）");
                }
            }
            catch
            {
                // ignore
            }
        }

        return result;
    }

    private static List<string> InstalledUnder(IEnumerable<(Environment.SpecialFolder Folder, string Sub)> candidates)
    {
        List<string> result = [];

        foreach ((Environment.SpecialFolder folder, string sub) in candidates)
        {
            try
            {
                string root = Environment.GetFolderPath(folder);
                if (root.Length > 0 && Directory.Exists(Path.Combine(root, sub)))
                {
                    result.Add(sub);
                }
            }
            catch
            {
                // ignore
            }
        }

        return result;
    }

    #endregion

    #region 2 驱动（区间检查，跟插件页同一套）

    private static void AddDriverCheck(List<Dlss5CompatItem> items, Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(2, "显卡", "NV 驱动版本（按 DLSS5 插件要求区间）");

        try
        {
            DriverCheckResult result = NvidiaDriverCheck.Check();
            if (string.IsNullOrWhiteSpace(result.Version))
            {
                item.Level = Dlss5CheckLevel.Info;
                item.Message = "读不到 NVIDIA 驱动版本  没装 N 卡驱动，或者注册表里没有卸载项。";
                items.Add(item);
                return;
            }

            item.Message = result.Level switch
            {
                DriverCheckLevel.Ok => $"驱动版本 {result.Version}，在 DLSS5 插件要求的区间内（616.56 ~ 616.64）。",
                DriverCheckLevel.Warning => " " + result.Message,
                _ => " " + result.Message,
            };
            item.Level = result.Level switch
            {
                DriverCheckLevel.Ok => Dlss5CheckLevel.Ok,
                DriverCheckLevel.Warning => Dlss5CheckLevel.Warning,
                _ => Dlss5CheckLevel.Error,
            };

            if (context.ForThisGameUsedGlobalDriverCheck(out string? globalBiz))
            {
                item.Message += $"\n（这条按全局配置查的  这个游戏用了「使用全局」，全局那次是在 {globalBiz} 上配的）";
            }
        }
        catch (Exception ex)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "驱动检查失败：" + ex.Message;
        }

        items.Add(item);
    }

    #endregion

    #region 3 RTX 动态亮丽

    private static Dlss5CompatItem CheckDynamicVibrance()
    {
        var item = new Dlss5CompatItem(3, "显卡", "是否开了 RTX 动态亮丽（RTX Dynamic Vibrance）");

        try
        {
            // 事实核查（2026-09-22 实机）：NV App 的「RTX 动态亮丽」开关**不落盘**到我们能读的地方。
            //   NvBackend\*.json / config.xml / backend.log 里都搜不到 vibrance；
            //   真身在 DRS 数据库（nvdrswr.lk 独占锁）和 NV App 内部存储，没有只读 API。
            // 所以这里**只给确认路径**，不假装读到了。
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "检测不到（NV App 不落盘这个开关）。" +
                           "要确认的话：NVIDIA app  图形  全局设置  找「RTX 动态亮丽」，看它是开还是关。" +
                           "它会给整个画面加饱和度/对比度，和 DLSS5 插件的色调映射叠加容易「颜色不对」" +
                           "排查画面问题建议先把它关掉试。" +
                           "（NVIDIA app：" + NvidiaAppSettings.DescribeNvidiaApp() + "）";
        }
        catch (Exception ex)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "检查 RTX 动态亮丽失败：" + ex.Message;
        }

        return item;
    }

    #endregion

    #region 4 RTX HDR

    private static Dlss5CompatItem CheckRtxHdr()
    {
        var item = new Dlss5CompatItem(4, "显卡", "是否开了 RTX HDR");

        try
        {
            bool? systemHdr = NvidiaAppSettings.TryReadSystemHdr();
            string? app = NvidiaAppSettings.DescribeNvidiaApp();

            // NV App 的「RTX HDR」开关本身不落盘（和动态亮丽同理，见 CheckDynamicVibrance 注释）。
            // 能读到的只有**系统级 HDR**（注册表 VideoSettings），它是个有用的间接信号：
            // 系统 HDR 关着的话，RTX HDR 通常也不会在游戏里生效。
            if (systemHdr is bool on)
            {
                item.Level = on ? Dlss5CheckLevel.Warning : Dlss5CheckLevel.Ok;
                item.Message = on
                    ? "系统 HDR 是**开着**的。RTX HDR 会把 SDR 游戏重映射成 HDR，和 DLSS5 插件的色调映射叠加" +
                      "容易出现「过曝、发灰、颜色不对」排查画面问题先把它关掉试。" +
                      "另外 NV App 里的 RTX HDR 开关读不到，要到 NVIDIA app  图形  全局设置里自己看一眼。" +
                      "（" + app + "）"
                    : "系统 HDR 是关着的。NV App 的 RTX HDR 开关读不到（不落盘），" +
                      "但系统 HDR 关着时它一般也不会在游戏里生效，基本可以放心。" +
                      "（" + app + "）";
                return item;
            }

            item.Level = Dlss5CheckLevel.Info;
            item.Message = "读不到 HDR 状态。到 Windows 设置  系统  显示  HDR 看系统 HDR，" +
                           "再到 NVIDIA app  图形  全局设置看 RTX HDR。" +
                           "两者任一开着都可能让 DLSS5 插件的画面偏色。" +
                           "（" + app + "）";
        }
        catch (Exception ex)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "检查 RTX HDR 失败：" + ex.Message;
        }

        return item;
    }

    #endregion

    #region 5 AI 插帧（这个游戏开没开）

    private static Dlss5CompatItem CheckAiFrameGeneration(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(5, "显卡", "这个游戏是否开了 AI 插帧（DLSS-G / FG）");

        try
        {
            var parts = new List<string>();

            // NV App 的 DLSS 覆盖开关：DLSS_Override / FG 相关
            string? overrideState = NvidiaAppSettings.TryReadDlssOverrideState(context.GameExePath);
            if (overrideState is { Length: > 0 })
            {
                parts.Add("NV App 覆盖状态：" + overrideState);
            }

            // 游戏自己的图形设置：直接读注册表里的 FPS / 帧生成相关键（米哈游那几个游戏都存这里）
            if (context.GameBiz is { Length: > 0 } biz)
            {
                string? gameSetting = GameFrameGenerationSetting.TryRead(biz);
                if (gameSetting is { Length: > 0 })
                {
                    parts.Add(gameSetting);
                }
            }

            // OptiScaler / 模块里有没有挂帧生成
            if (AppConfig.IsOptiScalerBuildEnabled(AppConfig.GetSelectedOptiScalerId(context.GameId) ?? string.Empty)
                && AppConfig.GetSelectedOptiScalerDll(context.GameId) is { Length: > 0 } opti)
            {
                parts.Add($"启动时会注入 OptiScaler（{Path.GetFileName(opti)}），如果你在它的 ini 里开了帧生成，这一项也会算「开了插帧」");
            }

            if (parts.Count == 0)
            {
                item.Level = Dlss5CheckLevel.Info;
                item.Message = "读不到这个游戏的插帧状态（NV App 覆盖记录 / 游戏图形设置里都没有）。" +
                               "DLSS5 神经渲染本身不生成额外帧，和 DLSS-G 叠着用没问题；但如果画面撕裂/重影，" +
                               "先到游戏里把「帧生成」关掉对比一下。";
                return item;
            }

            item.Level = Dlss5CheckLevel.Ok;
            item.Message = string.Join("\n", parts);
        }
        catch (Exception ex)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "检查插帧失败：" + ex.Message;
        }

        return item;
    }

    #endregion

    #region 6 显卡型号

    /// <summary>
    /// 读显卡信息用的注册表键。
    /// /!\ HKLM 用 RegistryView.Registry64 打开**需要管理员权限**，普通权限下会抛
    /// SecurityException（用户报过「Requested registry access is not allowed」）。
    /// 所以优先用 RegistryView.Default（权限要求最低），失败再退回 64 位视图。
    /// </summary>
    private static RegistryKey? OpenGpuRegistryKey()
    {
        const string path = "SYSTEM\\CurrentControlSet\\Control\\Class\\{4d36e968-e325-11ce-bfc1-08002be10318}";

        foreach (RegistryView view in new[] { RegistryView.Default, RegistryView.Registry64 })
        {
            try
            {
                RegistryKey? key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view).OpenSubKey(path);
                if (key is not null)
                {
                    return key;
                }
            }
            catch
            {
                // 视图/权限不支持就试下一个
            }
        }

        return null;
    }

    private static Dlss5CompatItem CheckGpuModel()
    {
        var item = new Dlss5CompatItem(6, "显卡", "当前显卡型号");

        try
        {
            List<string> gpus = [];

            using RegistryKey? controllers = OpenGpuRegistryKey();

            if (controllers is not null)
            {
                foreach (string name in controllers.GetSubKeyNames())
                {
                    // 只有 4 位数字子键是显示适配器实例；同键下的 Properties 子键 ACL 拒绝普通用户
                    // 读取（OpenSubKey 直接抛 SecurityException），Configuration 也不是适配器
                    if (name.Length != 4 || !name.All(char.IsDigit))
                    {
                        continue;
                    }

                    try
                    {
                        using RegistryKey? key = controllers.OpenSubKey(name);
                        string? desc = key?.GetValue("DriverDesc") as string;
                        if (!string.IsNullOrWhiteSpace(desc))
                        {
                            gpus.Add(desc.Trim());
                        }
                    }
                    catch
                    {
                        // 单个适配器键读不了就跳过
                    }
                }
            }

            if (gpus.Count == 0)
            {
                item.Level = Dlss5CheckLevel.Info;
                item.Message = "读不到显卡型号。";
                return item;
            }

            bool hasNvidia = gpus.Any(g => g.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
            item.Level = hasNvidia ? Dlss5CheckLevel.Ok : Dlss5CheckLevel.Error;
            item.Message = "本机显卡：" + string.Join(" / ", gpus.Distinct()) +
                           (hasNvidia
                               ? "。DLSS5 神经渲染只在 NVIDIA RTX 卡上跑（需要 Tensor Core）。"
                               : "。 没看到 NVIDIA 显卡  DLSS5 神经渲染需要 RTX 卡，这个环境下插件不会工作。");
        }
        catch (Exception ex)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "检查显卡型号失败：" + ex.Message;
        }

        return item;
    }

    #endregion

    #region 7 HoYoShade 目录带中文

    private static Dlss5CompatItem CheckHostChinesePath(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(7, "启动器", "HoYoShade 当前目录是否带中文");

        string? root = context.ShadeHost?.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            item.Level = Dlss5CheckLevel.Error;
            item.Message = "没找到当前 HoYoShade 目录，先到「全局插件」页「指定目录」。";
            return item;
        }

        item.Message = "当前 HoYoShade：" + root;

        if (!HasNonAscii(root))
        {
            item.Level = Dlss5CheckLevel.Ok;
            return item;
        }

        item.Level = Dlss5CheckLevel.Error;
        item.Message += "\n 路径里有非 ASCII 字符（中文等）。ReShade / inject.exe / 部分 addon 对这种路径支持很差，" +
                        "典型表现是「注入成功但插件不加载」。";

        string target = Path.Combine(AppConfig.UserDataFolder, ShadeHostLocator.HoYoShadeFolderName);

        // 已经就在默认位置、只是上层路径带中文（例如用户名是中文）时，没有安全的一键方案
        if (string.Equals(root.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            item.Message += $"\n它已经在默认位置了：{target}。要彻底避开中文，只能把整个便携包挪到一个纯英文目录" +
                            "（例如 D:\\APPS\\HoYoShadeHub）再重装一次。";
            return item;
        }

        item.Message += "\n点右边「修复」可以把 HoYoShade 目录重命名成纯 ASCII 名字，并把所有非 ASCII 的名字记进映射表，" +
                        "之后插件页 / 启动器仍然能按映射表找到它。";
        item.FixLabel = "改名成纯 ASCII";
        item.CanFix = true;
        item.Fix = () => Task.FromResult(AsciiPathHelper.TryMakeShadeRootAscii(root));
        return item;
    }

    #endregion

    #region 8 当前启用插件与版本号

    private static Dlss5CompatItem CheckEnabledPlugins(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(8, "启动器", "当前启用插件与版本号");

        if (context.AddonStates is null)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = context.ProfileError ?? "不知道当前游戏，读不到插件列表。";
            return item;
        }

        List<GameAddonState> enabled = [.. context.AddonStates.Where(a => a.Enabled)];

        if (enabled.Count == 0)
        {
            item.Level = Dlss5CheckLevel.Warning;
            item.Message = "这个游戏现在一个插件都没启用。DLSS5 神经渲染要插件本身启用才会加载  " +
                           "到插件列表里把 DLSS5 那个插件打开。";
            return item;
        }

        item.Level = Dlss5CheckLevel.Ok;
        item.Message = "启用中（" + enabled.Count + " 个）：\n" + string.Join("\n", enabled.Select(a =>
            $"- {a.DisplayName}　{a.Version ?? "版本未知"}" +
            (a.Branch is { Length: > 0 } branch ? $"（{branch} 分支）" : string.Empty) +
            (a.GloballyDisabled ? "　 文件被全局禁用（改名成 .addon64x）" : string.Empty) +
            (a.LoadFromDllMain ? "　LoadFromDllMain" : string.Empty)));

        return item;
    }

    #endregion

    #region 9 插件 Hook 模式

    private static Dlss5CompatItem CheckHookMode(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(9, "启动器", "当前插件 Hook 模式");

        if (context.Profile is null)
        {
            item.Level = Dlss5CheckLevel.Error;
            item.Message = context.ProfileError ?? "这个游戏没有 ReShade.ini，Hook 模式写在里面。";
            return item;
        }

        int hook = context.HookPoint;
        item.Message = $"ini 里现在是 {DescribeHook(hook)}（读的是 [RENODX-DLSS] DirectNeuralRenderingHookStage / DirectNeuralRenderingHookPoint）。";

        if (hook >= 0 && hook <= 4)
        {
            item.Level = Dlss5CheckLevel.Ok;
            return item;
        }

        item.Level = Dlss5CheckLevel.Error;
        item.Message += $"\n Hook 点在 4 以上（{hook}）：RenoDX DLSS 只认 0~4，越界会让插件加载失败 / 游戏起不来。";
        item.FixLabel = "改成 4";
        item.CanFix = true;
        item.Fix = () => Task.FromResult(context.SetHookPoint(4));
        return item;
    }

    private static string DescribeHook(int hook) => hook switch
    {
        0 => "off（0）",
        1 or 2 or 3 or 4 => hook.ToString(),
        _ => hook.ToString(),
    };

    #endregion

    #region 10 基础 shader / texture

    private static Dlss5CompatItem CheckShadersAndTextures(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(10, "启动器", "reshade-shaders 是否有基础 shader 和 Texture");

        string? root = context.ShadeHost?.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            item.Level = Dlss5CheckLevel.Error;
            item.Message = "没找到当前 HoYoShade 目录。";
            return item;
        }

        string shaders = Path.Combine(root, "reshade-shaders", "Shaders");
        string textures = Path.Combine(root, "reshade-shaders", "Textures");

        int shaderCount = CountFiles(shaders, "*.fx", "*.fxh");
        int textureCount = CountFiles(textures, "*.png", "*.jpg", "*.jpeg", "*.dds", "*.bmp", "*.tga");

        item.Message = $"Shaders：{shaderCount} 个（{shaders}）\nTextures：{textureCount} 个（{textures}）";

        if (shaderCount > 0 && textureCount > 0)
        {
            item.Level = Dlss5CheckLevel.Ok;
            return item;
        }

        item.Level = Dlss5CheckLevel.Error;
        item.Message += "\n " + (shaderCount == 0 ? "Shaders 目录是空的（或不存在）" : "Textures 目录是空的（或不存在）") +
                        "  多半是「快速安装只装必要」跳过了效果包，或者装完被清理过。" +
                        "点右边「修复」会打开官方下载页，勾上 Shaders + Textures 再装一次。";
        item.FixLabel = "去补装";
        item.CanFix = true;
        item.Fix = () =>
        {
            WeakReferenceMessenger.Default.Send(new NavigateToReShadeDownloadPageMessage { IsUpdateMode = false });
            return Task.FromResult<string?>("已经打开 HoYoShade 下载页：勾上 Shaders / Textures 装一次。");
        };
        return item;
    }

    private static int CountFiles(string directory, params string[] patterns)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }

        try
        {
            return patterns
                .SelectMany(p => Directory.EnumerateFiles(directory, p, SearchOption.AllDirectories))
                .Select(Path.GetFileName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
        }
        catch
        {
            return 0;
        }
    }

    #endregion

    #region 11 inject.exe 还在不在

    private static Dlss5CompatItem CheckInjectExe(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(11, "启动器", "inject.exe 是否还健在");

        string? root = context.ShadeHost?.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            item.Level = Dlss5CheckLevel.Error;
            item.Message = "没找到当前 HoYoShade 目录。";
            return item;
        }

        string inject = Path.Combine(root, "inject.exe");
        string reshade = Path.Combine(root, "ReShade64.dll");

        item.Message = "inject.exe：" + (File.Exists(inject) ? new FileInfo(inject).Length / 1024 + " KB" : "不存在") +
                       "\nReShade64.dll：" + (File.Exists(reshade) ? new FileInfo(reshade).Length / 1024 + " KB" : "不存在");

        if (File.Exists(inject) && File.Exists(reshade))
        {
            item.Level = Dlss5CheckLevel.Ok;
            return item;
        }

        item.Level = Dlss5CheckLevel.Error;
        item.Message += "\n " + (File.Exists(inject) ? "ReShade64.dll 没了" : "inject.exe 没了") +
                        "  没有它们就注不进游戏。点右边「修复」会打开 HoYoShade 下载页重装一次。";
        item.FixLabel = "去重装";
        item.CanFix = true;
        item.Fix = () =>
        {
            WeakReferenceMessenger.Default.Send(new NavigateToReShadeDownloadPageMessage { IsUpdateMode = true });
            return Task.FromResult<string?>("已经打开 HoYoShade 下载页，点「开始更新」把 inject.exe / ReShade64.dll 补回来。");
        };
        return item;
    }

    #endregion

    #region 12 ReShade.ini 里的三个路径

    private static Dlss5CompatItem CheckIniPaths(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(12, "游戏目录", "ReShade.ini 里 AddonPath / EffectSearchPaths / TextureSearchPaths 是否正确");

        string? ini = context.GameReShadeIniPath;
        if (string.IsNullOrWhiteSpace(ini) || !File.Exists(ini))
        {
            item.Level = Dlss5CheckLevel.Error;
            item.Message = "游戏目录里没有 ReShade.ini（" + (ini ?? "游戏目录未知") + "）。注入时会自动复制模板过去，也可以点右边「修复」现在补一份。";
            item.FixLabel = "补一份 ini";
            item.CanFix = true;
            item.Fix = () => Task.FromResult(context.CopyTemplateIni());
            return item;
        }

        if (context.Profile is null)
        {
            item.Level = Dlss5CheckLevel.Error;
            item.Message = "ReShade.ini 读不了：" + (context.ProfileError ?? "未知原因");
            return item;
        }

        string? addonPath = context.Profile.AddonPath;
        string? hostAddons = context.ShadeHost?.AddonsPath;
        var problems = new List<string>();
        var lines = new List<string>();

        lines.Add($"[ADDON] AddonPath = {addonPath ?? "（没写）"}");
        lines.Add($"[GENERAL] EffectSearchPaths / TextureSearchPaths = {context.Profile.EffectSearchPathsText ?? "（没写）"}");

        // AddonPath 存不存在
        if (!string.IsNullOrWhiteSpace(addonPath))
        {
            if (!Directory.Exists(addonPath))
            {
                problems.Add($"AddonPath 指向的目录不存在：{addonPath}");
            }
            else if (hostAddons is { Length: > 0 } && !PathsEqual(addonPath, hostAddons))
            {
                problems.Add($"AddonPath（{addonPath}）和当前 HoYoShade 的插件目录（{hostAddons}）不一致  用哪个启动器，ini 就该指到哪");
            }
        }
        else
        {
            problems.Add("没写 [ADDON] AddonPath");
        }

        // 效果/纹理搜索路径
        foreach (string? value in context.Profile.EffectSearchPaths ?? [])
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (Path.IsPathFullyQualified(value) && !Directory.Exists(StripSearchPattern(value)))
            {
                problems.Add($"搜索路径不存在：{value}");
            }
        }

        if (problems.Count == 0)
        {
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = string.Join("\n", lines) + "\n路径都对得上当前 HoYoShade。";
            return item;
        }

        item.Level = Dlss5CheckLevel.Error;
        item.Message = string.Join("\n", lines) + "\n " + string.Join("\n ", problems);
        item.FixLabel = "指回当前 HoYoShade";
        item.CanFix = true;
        item.Fix = () => Task.FromResult(context.AlignIniPaths());
        return item;
    }

    /// <summary>
    /// ReShade 的 EffectSearchPaths / TextureSearchPaths 写法是「目录\**」或「目录\*」，
    /// 直接 Directory.Exists 整串必然为 false（用户报过误报「搜索路径不存在」）。
    /// 这里把尾部的通配符段去掉再判断。
    /// </summary>
    private static string StripSearchPattern(string value)
    {
        string path = value.Trim();
        while (path.Length > 0 && (path.EndsWith("\\*") || path.EndsWith("\\**")))
        {
            int cut = path.LastIndexOf('\\');
            if (cut <= 0) { break; }
            path = path.Substring(0, cut);
        }
        return path;
    }
    private static bool PathsEqual(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    #endregion

    #region 13 第三方 dll / 超链接

    private static Dlss5CompatItem CheckThirdPartyDlls(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(13, "游戏目录", "是否有常见的第三方 dll 或者超链接（软链接）");

        if (string.IsNullOrWhiteSpace(context.GameDirectory) || !Directory.Exists(context.GameDirectory))
        {
            item.Level = Dlss5CheckLevel.Warning;
            item.Message = "还不知道游戏目录  先「指定主程序」选一下游戏 exe。";
            return item;
        }

        List<GameDirSuspiciousEntry> suspicious = GameDirScanner.Scan(context.GameDirectory);

        if (suspicious.Count == 0)
        {
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = "游戏目录（+ 一级子目录）里没看到常见的第三方注入 dll，也没有软/硬链接文件。";
            return item;
        }

        item.Level = Dlss5CheckLevel.Warning;
        item.Message = "游戏目录里发现这些可疑项：\n" + string.Join("\n", suspicious.Take(20).Select(s => "- " + s.Describe())) +
                       (suspicious.Count > 20 ? $"\n 还有 {suspicious.Count - 20} 项" : string.Empty) +
                       "\n和 ReShade / DLSS5 同时在游戏目录里的注入型 dll 会互相抢钩子（典型：d3d11.dll / dxgi.dll / version.dll）。";

        item.FixLabel = "自动清理";
        item.CanFix = true;
        item.Fix = () => GameDirScanner.FixAsync(context.GameDirectory, suspicious);
        return item;
    }

    #endregion

    #region 14 DirectNeuralRenderingPassCount

    private static Dlss5CompatItem CheckPassCount(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(14, "游戏目录", "ini 内 DirectNeuralRenderingPassCount 是否过高（超过 3）");

        if (context.Profile is null)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = context.ProfileError ?? "没有 ReShade.ini，读不到这个键。";
            return item;
        }

        int? count = context.Profile.GetDirectNeuralRenderingPassCount();

        if (count is null)
        {
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = "没写 DirectNeuralRenderingPassCount（用插件默认值）。";
            return item;
        }

        item.Message = $"ini 里现在是 {count}（建议  {MaxDirectNeuralRenderingPassCount}）。";

        if (count <= MaxDirectNeuralRenderingPassCount)
        {
            item.Level = Dlss5CheckLevel.Ok;
            return item;
        }

        item.Level = Dlss5CheckLevel.Warning;
        item.Message += "\n 每多一个 pass 就多一层神经渲染，显存/耗时线性上涨，画质收益很小；超过 3 很容易掉帧甚至爆显存。";
        item.FixLabel = $"改成 {MaxDirectNeuralRenderingPassCount}";
        item.CanFix = true;
        item.Fix = () => Task.FromResult(context.SetPassCount(MaxDirectNeuralRenderingPassCount));
        return item;
    }

    #endregion

    #region 15 XXMI

    private static Dlss5CompatItem CreateXxmiCheck(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(15, "XXMI", "对应的 MI 是否有常见第三方 dll 或者超链接（软链接）");

        string? importer = XxmiLocator.ImporterForGame(context.GameBiz, context.GameDisplayName);
        string? instance = XxmiLocator.FindInstance(context.GameBiz, out string? actualImporter);

        if (instance is null)
        {
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = importer is null
                ? "这个游戏没有对应的 MI 实例（XXMI 不支持/用不上），不检测。"
                : $"没找到 XXMI（{importer}）实例  没装就跳过这一项。装了的话到「模型替换」页指定一下 XXMI 根目录。";
            return item;
        }

        List<GameDirSuspiciousEntry> suspicious = GameDirScanner.Scan(instance);

        item.Message = $"{actualImporter ?? importer}：{instance}";

        if (suspicious.Count == 0)
        {
            item.Level = Dlss5CheckLevel.Ok;
            item.Message += "\n没看到常见的第三方 dll 或软/硬链接。";
            return item;
        }

        item.Level = Dlss5CheckLevel.Warning;
        item.Message += "\n发现这些可疑项：\n" + string.Join("\n", suspicious.Take(20).Select(s => "- " + s.Describe()));
        item.FixLabel = "自动清理";
        item.CanFix = true;
        item.Fix = () => GameDirScanner.FixAsync(instance, suspicious);
        return item;
    }

    #endregion

    #region 工具

    private static bool HasNonAscii(string text) => text.Any(c => c > 127);

    #endregion
}
