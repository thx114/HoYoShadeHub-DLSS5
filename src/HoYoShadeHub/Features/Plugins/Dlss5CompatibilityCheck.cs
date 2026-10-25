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
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// DLSS5 兼容性检测，其中 7/9/10/11/12/13/14/15/17/18 带自动修复。
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

    public sealed record Dlss5CompatProgress(double Percent, string Message);

    public static async Task<List<Dlss5CompatItem>> RunAsync(
        Dlss5CompatContext context,
        IProgress<Dlss5CompatProgress>? progress = null)
    {
        var items = new List<Dlss5CompatItem>();
        void Stage(double percent, string message) => progress?.Report(new Dlss5CompatProgress(percent, message));

        Stage(0, "正在准备检测环境…");

        // 1~7 显卡 / 驱动：不需要游戏就能查
        Stage(8, "正在检查显卡、驱动和 Windows 图形设置…");
        items.Add(CheckOverclock());
        AddDriverCheck(items, context);
        items.Add(CheckDynamicVibrance(context));
        items.Add(CheckRtxHdr(context));
        items.Add(CheckAiFrameGeneration(context));
        items.Add(CheckGpuModel());
        items.Add(CheckHardwareAcceleratedGpuScheduling());
        items.Add(CheckDpiScaling(context));

        // 8~12 启动器（当前 HoYoShade）
        Stage(32, "正在检查启动器和 HoYoShade 文件…");
        items.Add(CheckHostChinesePath(context));
        items.Add(CheckEnabledPlugins(context));
        items.Add(CheckHookMode(context));
        items.Add(CheckShadersAndTextures(context));
        items.Add(CheckInjectExe(context));

        // 13~15 游戏目录
        Stage(55, "正在检查游戏目录、ReShade 配置和第三方 DLL…");
        items.Add(CheckIniPaths(context));
        items.Add(CheckThirdPartyDlls(context));
        items.Add(CheckPassCount(context));

        // 16 XXMI（有才检测）
        Stage(72, "正在检查 XXMI 和注入路径…");
        items.Add(CreateXxmiCheck(context));

        // 16 这次到底走哪条路（OptiScaler 那条不需要 HoYoShade，必须让用户知道）
        items.Add(CheckDeliveryPath(context));

        // 18 有选游戏时：NVIDIA 驱动里钉住 MFG 数量会让 OptiScaler 的 MFG 解锁失效
        Stage(84, "正在检查帧生成、覆盖包和插件状态…");
        items.Add(CheckNvMfgCount(context));
        // 18 检测帧生成模型预设（只提示，不再代改驱动）；21 检测 NV 面板是否替换了帧生成模型（多倍帧生成可能闪烁 / 黑屏）
        items.Add(CheckNvDlssFgPreset(context));

        // 19 MSI Afterburner / RTSS 会 hook 游戏呈现，可能和 DLSS5 打架
        items.Add(CheckOverlayConflict(context));
        items.Add(CheckNvidiaInfoOverlay());

        // 20 每游戏插件包：同一插件多版本 / 这个游戏在用哪一版 / 包与共享目录是否同步（按用户要求不再在 DLSS5 兼容性检测中提示）
        // 21 检测 NV 面板是否替换了帧生成模型（多倍帧生成可能闪烁 / 黑屏）
        items.Add(CheckNvAppFgModelOverride(context));

        Stage(100, "检测完成");
        return await Task.FromResult(items);
    }

    private static Dlss5CompatItem CheckHardwareAcceleratedGpuScheduling()
    {
        var item = new Dlss5CompatItem(22, "显卡", "硬件加速 GPU 计划（HAGS）");
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            object? raw = key?.GetValue("HwSchMode");
            if (raw is null)
            {
                item.Level = Dlss5CheckLevel.Info;
                item.Message = "未找到 HwSchMode；当前由 Windows 默认策略决定，无法确认是否开启。";
            }
            else if (Convert.ToInt32(raw) == 2)
            {
                item.Level = Dlss5CheckLevel.Ok;
                item.Message = "已开启（HwSchMode=2）。";
            }
            else if (Convert.ToInt32(raw) == 1)
            {
                item.Level = Dlss5CheckLevel.Warning;
                item.Message = "未开启（HwSchMode=1）。建议在 Windows 设置 → 系统 → 显示 → 图形 → 更改默认图形设置中开启，修改后需重启。";
            }
            else
            {
                item.Level = Dlss5CheckLevel.Info;
                item.Message = $"检测到未识别的 HwSchMode={raw}，请在 Windows 图形设置中手动确认。";
            }
        }
        catch (Exception ex)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "无法读取 HAGS 状态：" + ex.Message;
        }
        return item;
    }

    private static Dlss5CompatItem CheckDpiScaling(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(24, "系统", "当前设备 DPI 缩放与游戏高 DPI 设置");
        try
        {
            uint dpi = GetDpiForSystem();
            if (dpi == 0)
            {
                item.Level = Dlss5CheckLevel.Info;
                item.Message = "无法读取当前设备 DPI 缩放；请自行确认游戏 exe 的高 DPI 设置。";
                return item;
            }

            int scale = (int)Math.Round(dpi * 100d / 96d);
            if (scale <= 100)
            {
                item.Level = Dlss5CheckLevel.Ok;
                item.Message = $"当前设备显示缩放为 {scale}%（{dpi} DPI），无需额外设置。";
                return item;
            }

            string? exe = context.GameExePath;
            if (!string.IsNullOrWhiteSpace(exe) && HasHighDpiOverride(exe))
            {
                item.Level = Dlss5CheckLevel.Ok;
                item.Message = $"当前设备显示缩放为 {scale}%；已检测到此游戏 exe 有高 DPI 缩放替代设置。";
                return item;
            }

            item.Level = Dlss5CheckLevel.Warning;
            item.Message = $"当前设备显示缩放为 {scale}%。建议右键「{(string.IsNullOrWhiteSpace(exe) ? "游戏.exe" : Path.GetFileName(exe))}」→ 属性 → 兼容性 → 更改高 DPI 设置，打开「替代高 DPI 缩放行为」。";
            item.FixLabel = "打开游戏属性";
            item.CanFix = !string.IsNullOrWhiteSpace(exe) && File.Exists(exe);
            if (item.CanFix)
            {
                item.Fix = () =>
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = exe!,
                        Verb = "properties",
                        UseShellExecute = true,
                    });
                    return Task.FromResult<string?>("已打开游戏.exe 属性；请进入「兼容性 → 更改高 DPI 设置」，打开「替代高 DPI 缩放行为」后应用。");
                };
            }

            return item;
        }
        catch (Exception ex)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "读取 DPI 缩放失败：" + ex.Message;
            return item;
        }
    }

    private static bool HasHighDpiOverride(string exePath)
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers");
        string? flags = key?.GetValue(Path.GetFullPath(exePath)) as string;
        return !string.IsNullOrWhiteSpace(flags)
            && (flags.Contains("HIGHDPIAWARE", StringComparison.OrdinalIgnoreCase)
                || flags.Contains("DPIUNAWARE", StringComparison.OrdinalIgnoreCase)
                || flags.Contains("GDIDPISCALING", StringComparison.OrdinalIgnoreCase));
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

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
        var item = new Dlss5CompatItem(2, "显卡", "NV 驱动版本（按 DLSS5 插件要求）");

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
                DriverCheckLevel.Ok => $"驱动版本 {result.Version}，满足 DLSS5 插件要求（>= 616.56）。",
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

    private static Dlss5CompatItem CheckDynamicVibrance(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(3, "显卡", "是否开了 RTX 动态亮丽（RTX Dynamic Vibrance）");

        try
        {
            (bool? on, string where) = ReadToggle(context, NvDrsInterop.RtxDynamicVibranceEnableSettingId, "RTX Dynamic Vibrance - Enable");

            if (on is true)
            {
                item.Level = Dlss5CheckLevel.Warning;
                item.Message = "开着（" + where + "）。它会给画面加饱和度 / 对比度，和 DLSS5 插件的色调映射叠加容易「颜色不对」，排查画面问题先关掉试。";
            }
            else if (on is false)
            {
                item.Level = Dlss5CheckLevel.Ok;
                item.Message = "没有开（" + where + "）。";
            }
            else
            {
                item.Level = Dlss5CheckLevel.Ok;
                item.Message = "驱动里没有这一项（一般等于没开）。画面颜色不对的话，去 NVIDIA app  图形  全局设置再确认一眼。";
            }
        }
        catch (Exception ex)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "检查 RTX 动态亮丽失败：" + ex.Message;
        }

        return item;
    }

    /// <summary>
    /// 读一个开关型设置：先看这个游戏的配置，再看全局配置。
    /// On = null 表示两处都没有这一项（一般等于「没开」），Where 说明读到的位置。
    /// </summary>
    private static (bool? On, string Where) ReadToggle(Dlss5CompatContext context, uint settingId, string settingName)
    {
        if (context.GameExePath is { Length: > 0 } exePath)
        {
            NvDrsInterop.State game = NvDrsInterop.Read(Path.GetFileName(exePath), settingId, settingName, NvDrsInterop.DescribeBinary);
            if (game.Ok && game.Value != 0)
            {
                return (true, "这个游戏的驱动配置");
            }
        }

        NvDrsInterop.State global = NvDrsInterop.ReadGlobal(settingId, settingName, NvDrsInterop.DescribeBinary);
        if (global.Ok)
        {
            return (global.Value != 0, "全局配置");
        }

        return (null, "驱动里没有这一项");
    }

    #endregion

    #region 4 RTX HDR

    /// <summary>
    /// 第 4 条：RTX HDR。
    ///
    /// <para>
    /// 关键事实（用户反馈）：NVIDIA 的 RTX HDR 要求**显示器的 Windows HDR 打开**才有效果。
    /// 开关开着、显示器 HDR 没开的时候，RTX HDR 实际上什么都不做 —— 所以这一条不能只看驱动里的开关，
    /// 必须把显示器的高级颜色状态一起读出来判。
    /// </para>
    /// </summary>
    private static Dlss5CompatItem CheckRtxHdr(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(4, "显卡", "RTX HDR 是否真的生效（要显示器开着 HDR）");

        try
        {
            (bool? rtxHdr, string where) = ReadToggle(context, NvDrsInterop.RtxHdrEnableSettingId, "RTX HDR - Enable");

            // 显示器的高级颜色（Windows HDR）状态。读不到就是空表 → 一律按「读不到」说，不猜。
            IReadOnlyList<DisplayHdrEntry> displays = DisplayHdrState.Read();
            bool? anyDisplayHdr = DisplayHdrState.AnyEnabled(displays);
            string displaysText = DisplayHdrState.Describe(displays);

            switch (RtxHdrCoexistence.Evaluate(rtxHdr, anyDisplayHdr))
            {
                case RtxHdrVerdict.NotEnabled:
                    item.Level = Dlss5CheckLevel.Ok;
                    item.Message = $"没有开（{where}）。显示器：{displaysText}。";
                    return item;

                case RtxHdrVerdict.EnabledWithoutSystemHdr:
                    item.Level = Dlss5CheckLevel.Ok;
                    item.Message = $"显示器未开启 Windows HDR（{displaysText}），RTX HDR 不生效。";
                    return item;

                case RtxHdrVerdict.EnabledWithSystemHdr:
                    item.Level = Dlss5CheckLevel.Warning;
                    item.Message = $"开着（{where}），显示器 HDR 也开着（{displaysText}）—— 是生效的。" +
                                   "但 RTX HDR 会把 SDR 游戏重映射成 HDR，和 DLSS5 插件的色调映射叠加" +
                                   "容易出现「过曝、发灰、颜色不对」，排查画面问题先把它关掉试。";
                    return item;

                case RtxHdrVerdict.EnabledWithUnknownSystemHdr:
                    item.Level = Dlss5CheckLevel.Warning;
                    item.Message = $"RTX HDR 开着（{where}），但读不到显示器 HDR 状态。" +
                                   "RTX HDR 必须显示器的 Windows HDR 打开才有效果，自己到 Windows 设置 → 系统 → 显示 确认一下；" +
                                   "排查画面问题也建议先把它关掉试。";
                    return item;

                case RtxHdrVerdict.UnknownToggleWithSystemHdr:
                    item.Level = Dlss5CheckLevel.Warning;
                    item.Message = $"驱动里读不到 RTX HDR 这一项，但显示器 HDR 是开着的（{displaysText}）。" +
                                   "到 NVIDIA app → 图形 → 全局设置确认 RTX HDR，开着的话先关掉试。";
                    return item;

                default:
                    // UnknownToggleWithoutSystemHdr —— 显示器没开 Windows HDR，RTX HDR 不生效，直接绿。
                    item.Level = Dlss5CheckLevel.Ok;
                    item.Message = $"显示器未开启 Windows HDR（{displaysText}），RTX HDR 不生效。";
                    return item;
            }
        }
        catch (Exception ex)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "检查 RTX HDR 失败：" + ex.Message;
        }

        return item;
    }

    /// <summary>「打开系统 HDR」这一步：改完再读一遍确认，然后把结果说清楚（HDR 一开会黑一下屏，正常）</summary>
    internal static string EnableHdrAndDescribe(string deviceName)
    {
        if (DisplayHdrState.SetEnabled(deviceName, true) is { Length: > 0 } error)
        {
            return error;
        }

        IReadOnlyList<DisplayHdrEntry> after = DisplayHdrState.Read();

        return DisplayHdrState.AnyEnabled(after) is true
            ? $"已给 {deviceName} 打开 Windows HDR（屏幕黑一下是正常的）。当前：{DisplayHdrState.Describe(after)}。"
            : $"已经调用过了，但读回来还是没开：{DisplayHdrState.Describe(after)}。到 Windows 设置 → 系统 → 显示 → HDR 手动确认一下。";
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

            // 驱动配置里的开关（公开 DRS 接口读不到这些，走扩展接口）
            if (context.GameExePath is { Length: > 0 } exePath)
            {
                string exeName = Path.GetFileName(exePath);
                List<string> driver = [];

                NvDrsInterop.State fg = NvDrsInterop.Read(exeName, NvDrsInterop.DlssFgOverrideEnableSettingId, "Enable DLSS-FG override", NvDrsInterop.DescribeBinary);
                if (fg.Ok)
                {
                    driver.Add("DLSS 帧生成覆盖 " + (fg.Value != 0 ? "开" : "关"));
                }

                NvDrsInterop.State sm = NvDrsInterop.Read(exeName, NvDrsInterop.SmoothMotionEnableSettingId, "Smooth Motion - Enable", NvDrsInterop.DescribeBinary);
                if (sm.Ok)
                {
                    driver.Add("Smooth Motion（AI 插帧）" + (sm.Value != 0 ? "开" : "关"));
                }

                NvDrsInterop.State mfg = NvDrsInterop.Read(exeName, NvDrsInterop.DlssgMultiFrameCountSettingId, "Override DLSSG multi-frame count");
                if (mfg.Ok && mfg.Value != 0)
                {
                    driver.Add("多帧生成数量被覆盖为 " + mfg.Value);
                }

                if (driver.Count > 0)
                {
                    parts.Add("驱动配置：" + string.Join("；", driver));
                }
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
        var item = new Dlss5CompatItem(7, "启动器", "启动器是否带中文");

        string? root = context.ShadeHost?.RootPath;
        if (string.IsNullOrWhiteSpace(root))
        {
            // 没装 HoYoShade 不是「错」，只是这条路暂时用不上：
            // 走 OptiScaler 的 DLSS-NR 时根本不需要 HoYoShade（见 Dlss5Delivery）。
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = context.IsOptiScalerOnly
                ? "没装 HoYoShade —— 你现在走的是 OptiScaler 的 DLSS-NR 那条路，不需要它，这一项不用管。"
                : "没装 HoYoShade，这一项暂时查不了。装了之后会在这里检查目录里有没有中文。";
            item.FixLabel = "去安装";
            item.CanFix = true;
            item.Fix = () =>
            {
                WeakReferenceMessenger.Default.Send(new NavigateToReShadeDownloadPageMessage { IsUpdateMode = false });
                return Task.FromResult<string?>("已经打开 HoYoShade 下载页，装完回来点「重新检测」。");
            };
            return item;
        }

        // 只用 OptiScaler、没启用任何插件：中文路径只坑 ReShade 插件 / inject.exe / addon 那套，
        // OptiScaler 的 dll 铺在游戏目录，跟启动器装哪儿没关系 —— 这项直接放。
        if (context.Delivery is Dlss5Delivery.OptiScalerOnly or Dlss5Delivery.Both
            && context.AddonStates is { } states
            && states.All(a => !a.Enabled))
        {
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = $"当前 HoYoShade：{root}\n这个游戏走 OptiScaler、没启用任何插件，启动器目录带中文不影响，不检查这项。";
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

        // 「一个插件都没启用」本身是正常状态，不是问题：
        // 插件是用户自己按需开的，检测工具有没有 DLSS5 插件可开，不是这一条要判的事。
        // 只有确实看到 DLSS5 / RenoDX 系列插件、但一个都没启用时才提示。
        if (enabled.Count == 0)
        {
            if (HasDlss5Addon(context))
            {
                item.Level = Dlss5CheckLevel.Warning;
                item.Message = "目录里有 DLSS5 / RenoDX 插件，但现在一个都没启用。" +
                               "DLSS5 神经渲染要插件本身启用才会加载  到插件列表里把 DLSS5 那个插件打开。";
                return item;
            }

            item.Level = Dlss5CheckLevel.Ok;
            item.Message = "这个游戏现在一个插件都没启用。" +
                           "没装 / 没开插件时 DLSS5 神经渲染自然也不会加载，这一项不算问题；" +
                           "要用的时到插件列表里把插件打开即可。";
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
            // Hook 模式存在 ReShade.ini 里，而这份 ini 是**注入时**才复制到游戏目录的。
            // 还没注入过 = 还没这份 ini，这是正常状态，不是问题（用户明确要求不要报红）。
            if (context.ProfileError is { Length: > 0 } error)
            {
                item.Level = Dlss5CheckLevel.Error;
                item.Message = "这个游戏的 ReShade.ini 读不了，所以看不到 Hook 模式：" + error;
                return item;
            }

            item.Level = Dlss5CheckLevel.Ok;
            item.Message = "还没有 ReShade.ini（这个游戏还没注入过），Hook 模式暂时无从谈起。" +
                           "首次注入时启动器会自动把模板复制到游戏目录，之后这里就能读到 Hook 点了。";
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

    /// <summary>
    /// 这个游戏的插件目录里有没有「DLSS5 那一类」插件（<see cref="GameAddonState.IsDlss5"/>，
    /// 也就是扩展目录里 tags 带 dlss5 的那些，RenoDX DLSS / super-anus 之类）。
    ///
    /// <para>
    /// 第 8 条要靠它区分两种情况：「装了 DLSS5 插件但忘了开」（值得提示）
    /// 和「本来就用不上插件」（不该报）。</para>
    /// </summary>
    private static bool HasDlss5Addon(Dlss5CompatContext context)
    {
        if (context.AddonStates is not { Count: > 0 } states)
        {
            return false;
        }

        return states.Any(a => a.IsDlss5);
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
            // 没装 HoYoShade：走 OptiScaler 那条路时 shader/texture 根本用不到
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = context.IsOptiScalerOnly
                ? "没装 HoYoShade —— OptiScaler 的 DLSS-NR 不经过 ReShade，不需要 shader/Texture，这一项不用管。"
                : "没装 HoYoShade，查不了 shader / Texture。";
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
            // 没装 HoYoShade：OptiScaler 那条路不用 inject.exe / ReShade64.dll。
            // 不算问题（给绿），但用户可能正想装一个，所以挂「去安装」按钮。
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = context.IsOptiScalerOnly
                ? "没装 HoYoShade —— OptiScaler 的 DLSS-NR 不靠 inject.exe 注入，这一项不用管。"
                : "没装 HoYoShade，查不了 inject.exe / ReShade64.dll。想装的话点右边「去安装」。";
            item.FixLabel = "去安装";
            item.CanFix = true;
            item.Fix = () =>
            {
                WeakReferenceMessenger.Default.Send(new NavigateToReShadeDownloadPageMessage { IsUpdateMode = false });
                return Task.FromResult<string?>("已经打开 HoYoShade 下载页，装完回来点「重新检测」。");
            };
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

        // 这一条查的是 ReShade 的 ini 路径。没装 ReShade（HoYoShade）时这份 ini 压根不会出现，
        // 所以不该报黄 —— 给绿，用户想装的话右边有按钮。
        if (!context.HasShadeHost)
        {
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = context.IsOptiScalerOnly
                ? "没装 HoYoShade —— OptiScaler 的 DLSS-NR 不经过 ReShade，没有 ReShade.ini 也就没有这几个路径要查，这一项不用管。"
                : "没装 HoYoShade，没有 ReShade.ini，这几个路径暂时不用查。想装的话点右边「去安装」。";
            item.FixLabel = "去安装";
            item.CanFix = true;
            item.Fix = () =>
            {
                WeakReferenceMessenger.Default.Send(new NavigateToReShadeDownloadPageMessage { IsUpdateMode = false });
                return Task.FromResult<string?>("已经打开 HoYoShade 下载页，装完回来点「重新检测」。");
            };
            return item;
        }

        // 装了 ReShade 却还不知道游戏目录 → 那确实要提醒用户去指定
        if (string.IsNullOrWhiteSpace(ini))
        {
            item.Level = Dlss5CheckLevel.Warning;
            item.Message = "还不知道游戏目录  先「指定主程序」选一下游戏 exe，才能看这份 ini 里的路径。";
            return item;
        }

        if (!File.Exists(ini))
        {
            // 没注入过 = 游戏目录里就不会有 ReShade.ini，这是正常状态。
            // 三个路径是**注入时**由模板复制过去的，这份 ini 还不存在就谈不上「路径对不对」，
            // 所以不报红（用户明确要求）。想提前补一份的话右边「修复」还在。
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = "游戏目录里还没有 ReShade.ini（" + ini + "），这个游戏目前还没注入过。" +
                           "首次注入时启动器会自动把模板复制过去，那三个路径到时候才需要检查。" +
                           "想现在就补一份的话，点右边「补一份 ini」。";
            item.FixLabel = "补一份 ini";
            item.CanFix = true;
            item.IsOptionalFix = true;
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
        lines.Add($"[GENERAL] EffectSearchPaths = {context.Profile.EffectSearchPathsText ?? "（没写）"}");
        lines.Add($"[GENERAL] TextureSearchPaths = {context.Profile.TextureSearchPathsText ?? "（没写）"}");

        // 「每游戏插件包」是新系统的正常状态：AddonPath 本来就该指着包目录，
        // 不能按「和共享目录不一致」报错。顺便把「这个游戏在用哪一版插件」写出来。
        bool usesPack = GameAddonPack.IsPackDirectory(addonPath);
        if (usesPack)
        {
            Dictionary<string, string> packSelections = GameAddonPack.ReadSelections(addonPath);
            lines.Add("[每游戏插件包] " + (packSelections.Count == 0
                ? "专属插件目录（没有单独选过插件版本）"
                : "这个游戏用的插件版本：" + string.Join("、", packSelections.Select(kv => $"{kv.Key} = {kv.Value}"))));
        }

        // AddonPath 存不存在
        if (!string.IsNullOrWhiteSpace(addonPath))
        {
            if (!Directory.Exists(addonPath))
            {
                problems.Add($"AddonPath 指向的目录不存在：{addonPath}");
            }
            else if (!usesPack && hostAddons is { Length: > 0 } && !PathsEqual(addonPath, hostAddons)
                     && !IsInsideCurrentRoot(addonPath, context.ShadeHost?.RootPath))
            {
                problems.Add($"AddonPath（{addonPath}）和当前 HoYoShade 的插件目录（{hostAddons}）不一致  用哪个启动器，ini 就该指到哪");
            }
        }
        else
        {
            problems.Add("没写 [ADDON] AddonPath");
        }

        // 效果 / 纹理搜索路径：两种问题都要报 ——
        //   ① 目录根本不存在；② 目录存在，但指向**别的** HoYoShade / 第三方整合包
        //     （群友的 Seri 案例：reshade-shaders 形状但根不叫 HoYoShade —— 一样爆红，
        //     修复都是把根前缀换回当前 HoYoShade，重复条目顺手去重）。
        string? hostRoot = context.ShadeHost?.RootPath;
        foreach (string? value in (context.Profile.EffectSearchPaths ?? []).Concat(context.Profile.TextureSearchPaths ?? []))
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            if (Path.IsPathFullyQualified(value) && !Directory.Exists(StripSearchPattern(value)))
            {
                problems.Add($"搜索路径不存在：{value}");
                continue;
            }

            string? otherRoot = ShadePathAligner.RootOf(value);
            if (otherRoot is not null && hostRoot is { Length: > 0 } && !PathsEqual(otherRoot, hostRoot))
            {
                problems.Add($"搜索路径指到别的 HoYoShade / 第三方目录：{value}（当前是 {hostRoot}）");
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
        item.FixLabel = usesPack ? "对齐其它路径" : "指回当前 HoYoShade";
        item.CanFix = true;
        item.Fix = () => Task.FromResult(context.AlignIniPaths());
        return item;
    }

    /// <summary>
    /// AddonPath 落在**当前** HoYoShade 根目录里面（例：上游自带的「若DX11崩溃或无效请解压」
    /// 备用包解压成 Addons 的子目录）不算「指错启动器」—— 对齐器按根对齐，本来就不会动它。
    /// 以前这里按「和 hostAddons 整串相等」判定，这种目录会被报「不一致」，但点「指回当前
    /// HoYoShade」永远回「没有需要改的键」（检测与对齐器口径不同，用户报过这条自相矛盾）。
    /// 判断不用反斜杠字面量，分隔符一律取运行时常量。
    /// </summary>
    private static bool IsInsideCurrentRoot(string? path, string? hostRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(hostRoot))
        {
            return false;
        }

        try
        {
            string full = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string root = hostRoot.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                  .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return Path.IsPathFullyQualified(full)
                   && full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
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
        string? instance = XxmiLocator.FindInstance(context.GameBiz, context.GameDisplayName, out string? actualImporter);

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

    #region 16 DLSS5 走哪条路

    /// <summary>
    /// 这次 DLSS5 神经渲染靠哪条路实现。
    ///
    /// <para>
    /// 用户可能<b>只装了 OptiScaler</b>（没装 HoYoShade）—— 那条路是成立的，
    /// 不需要 ReShade/addon。所以这一条要把现状说清楚，免得用户看到 7~11 条
    /// 「没装 HoYoShade」以为必须去装一个。
    /// </para>
    /// </summary>
    private static Dlss5CompatItem CheckDeliveryPath(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(16, "启动器", "DLSS5 走哪条路（是否必须装 HoYoShade）");

        bool hasOpti = context.OptiScalerDllPath is { Length: > 0 };

        switch (context.Delivery)
        {
            case Dlss5Delivery.OptiScalerOnly:
                item.Level = Dlss5CheckLevel.Ok;
                item.Message = "走 OptiScaler 的 DLSS-NR（神经渲染），**不需要 HoYoShade**。" + "\n" +
                               "注入的 dll：" + context.OptiScalerDllPath + "\n" +
                               "上面第 7~11 条关于 HoYoShade / ReShade 的检查对这一条路不适用，显示「没装」是正常的，不用去装。";
                break;

            case Dlss5Delivery.Both:
                item.Level = Dlss5CheckLevel.Ok;
                item.Message = "两条路都可用：HoYoShade（ReShade addon）+ OptiScaler DLSS-NR。" + "\n" +
                               "OptiScaler 注入的 dll：" + context.OptiScalerDllPath + "\n" +
                               "注意：两边抢同一层 DLSS 调用，**同一个游戏不要同时开**，挑一条用。";
                break;

            case Dlss5Delivery.HoYoShadeOnly:
                item.Level = Dlss5CheckLevel.Ok;
                item.Message = "走 HoYoShade（ReShade + addon）。要给游戏换用 OptiScaler 的 DLSS-NR 的话，" + "\n" +
                               "到「OptiScaler」页给这个游戏选一个构建即可（那条路不需要 HoYoShade）。";
                break;

            default:
                item.Level = hasOpti ? Dlss5CheckLevel.Info : Dlss5CheckLevel.Warning;
                item.Message = "还没确定走哪条路：" + "\n" +
                               "· 装 HoYoShade（ReShade addon 那条）—— 点「去安装」；" + "\n" +
                               "· 或者到「OptiScaler」页给这个游戏选一个构建（DLSS-NR 那条，不需要 HoYoShade）。";
                item.FixLabel = "去安装 HoYoShade";
                item.CanFix = true;
                item.Fix = () =>
                {
                    WeakReferenceMessenger.Default.Send(new NavigateToReShadeDownloadPageMessage { IsUpdateMode = false });
                    return Task.FromResult<string?>("已经打开 HoYoShade 下载页。装完回来点「重新检测」。");
                };
                break;
        }

        return item;
    }

    #endregion

    #region 工具

    private static bool HasNonAscii(string text) => text.Any(c => c > 127);

    #endregion

    #region 17 NVIDIA 驱动：DLSS-FG 多帧生成数量

    /// <summary>17. 驱动里这条覆盖项（0x104D6667）会盖住 OptiScaler 的 MFG 解锁，N/A = 写 0。</summary>
    private static Dlss5CompatItem CheckNvMfgCount(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(17, "显卡", "NVIDIA 驱动  DLSS-FG 多帧生成数量（MFG）");

        string? exePath = context.Game?.ExePath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "先选一个游戏。";
            return item;
        }

        string exeName = Path.GetFileName(exePath);
        NvDrsInterop.State state = NvDrsInterop.Read(exeName, NvDrsInterop.DlssgMultiFrameCountSettingId, "Override DLSSG multi-frame count");

        if (!state.Ok)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = $"读不到「{exeName}」的驱动配置：{state.Error}";
            return item;
        }

        if (!state.Overridden || state.Value == 0)
        {
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = $"「{exeName}」没有被覆盖（等于 N/A） 正常：多帧生成数量交给游戏 / 插件自己决定。";
            return item;
        }

        item.Level = Dlss5CheckLevel.Warning;
        item.Message = $"「{exeName}」在驱动里被钉住了多帧生成数量：{state.Display}。" +
                       "这条会盖住 OptiScaler 的 MFG 解锁（游戏里倍数不对 / 解锁看不出效果），点右边「改为 N/A」即可。";
        item.CanFix = true;
        item.FixLabel = "改为 N/A";
        item.Fix = () =>
        {
            NvDrsInterop.State after = NvDrsInterop.WriteDword(exeName, NvDrsInterop.DlssgMultiFrameCountSettingId, 0, "Override DLSSG multi-frame count");
            string text = after.Ok
                ? $"已改为 N/A（{after.Display}） 重启游戏生效。"
                : $"写入失败：{after.Error}";
            return Task.FromResult<string?>(text);
        };

        return item;
    }

    #endregion

    #region 18 NVIDIA 驱动：DLSS-FG 帧生成模型预设

    /// <summary>18. 帧生成模型预设应保持默认（不被覆盖）；只要被修改过，启用帧生成就可能闪烁，报红。</summary>
    private static Dlss5CompatItem CheckNvDlssFgPreset(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(18, "显卡", "NVIDIA 驱动  DLSS-FG 帧生成模型预设");

        string? exePath = context.Game?.ExePath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "先选一个游戏。";
            return item;
        }

        string exeName = Path.GetFileName(exePath);
        NvDrsInterop.State state = NvDrsInterop.Read(exeName, NvDrsInterop.DlssFgPresetSettingId, "Override DLSS-FG preset", NvDrsInterop.DescribeFgPreset);

        if (!state.Ok)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = $"读不到「{exeName}」的驱动配置：{state.Error}";
            return item;
        }

        if (!state.Overridden)
        {
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = $"「{exeName}」没有覆盖 DLSS-FG 预设（{state.Display}），跟随默认 正常。";
            return item;
        }

        item.Level = Dlss5CheckLevel.Error;
        item.Message = $"「{exeName}」的帧生成模型预设被修改过：{state.Display}。"
                       + "优设应该保持默认，被修改后启用帧生成可能遇到闪烁问题；请到 NVIDIA App / 控制面板把 DLSS-FG 预设恢复默认（启动器不再代改）。";

        return item;
    }

    #endregion

    #region 19 MSI Afterburner / RTSS 是否可能冲突

    /// <summary>19. MSI Afterburner / RTSS 会 hook 游戏的呈现，可能和 DLSS5 链路打架。</summary>
    private static Dlss5CompatItem CheckOverlayConflict(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(19, "系统", "RTSS 当前游戏配置 / 叠加层对照")
        { AlternativeLabel = "禁用当前游戏 RTSS 注入" };
        var running = RunningProcesses(["RTSS", "RTSSHooksLoader64", "RTSSHooksLoader", "MSIAfterburner"]);
        string? root = RtssInstallation.FindRoot(out string note);
        string? exe = context.GameExePath is { Length: > 0 } path ? Path.GetFileName(path) : null;
        item.Level = running.Count > 0 ? Dlss5CheckLevel.Warning : Dlss5CheckLevel.Info;
        item.Message = note + "检测到进程不代表已确认冲突。仅更改当前 EXE，不改 Global、OSD、限帧及其他游戏；操作不参与一键修复。";
        if (running.Count > 0) item.Message += "\n相关运行进程：" + string.Join("、", running.Distinct());
        if (root is null || string.IsNullOrWhiteSpace(exe))
        {
            item.Message += root is null ? " 未找到 RTSS 安装目录，可点击指定目录。" : " 请先选择游戏主程序。";
            item.CanFix = root is null;
            item.IsOptionalFix = true;
            item.FixLabel = "指定 RTSS 目录";
            item.PickRtssFolder = root is null;
            return item;
        }
        try
        {
            var state = RtssProfileService.Read(root, exe, RtssInstallation.BackupRoot);
            item.Message += $"\nRTSS：{root}\n当前 profile：{state.Profile}\nEnableHooking={state.Hooking ?? "未设置（继承）"}；EnableOSD={state.Osd ?? "未设置（继承）"}。";
            item.CanFix = true;
            item.IsOptionalFix = true;
            item.FixLabel = "Low 设置说明";
            item.Fix = () => Task.FromResult<string?>($"自动 Low 数值尚未核实；请在 RTSS 选择 {exe}，将 Application detection level 设为 Low，重启游戏对照。EnableHooking=1 不在此被断言为 Low。无 OSD 只表示不显示，不能代替退出 RTSS 排除 hook。未改文件。");
            item.AlternativeFix = () =>
            {
                RtssInstallation.EnsureStopped(exe);
                return Task.FromResult<string?>(RtssProfileService.Apply(root, exe, RtssInstallation.BackupRoot, RtssProfileMode.Disabled));
            };
            if (state.CanRestore) item.RestoreFix = () =>
            {
                RtssInstallation.EnsureStopped(exe);
                return Task.FromResult<string?>(RtssProfileService.Restore(root, exe, RtssInstallation.BackupRoot));
            };
        }
        catch (Exception ex)
        {
            item.Level = Dlss5CheckLevel.Warning;
            item.Message += "\n读取 RTSS profile 失败（未修改）：" + ex.Message;
        }
        return item;
    }

    #endregion

    #region 22 NVIDIA 信息浮窗

    /// <summary>检测正在运行的 NVIDIA 信息浮窗进程；运行中直接红色提醒，避免与 DLSS5 / OptiScaler 抢呈现 hook。</summary>
    private static Dlss5CompatItem CheckNvidiaInfoOverlay()
    {
        var item = new Dlss5CompatItem(23, "显卡", "NVIDIA 信息浮窗 / 游戏内覆盖层");
        List<string> running = RunningProcesses([
            "NVIDIA Share",
            "NVIDIA Overlay",
            "NVIDIA Overlay Helper",
            "NVIDIA Web Helper",
        ]);

        if (running.Count > 0)
        {
            item.Level = Dlss5CheckLevel.Error;
            item.Message = "检测到 NVIDIA 信息浮窗组件正在运行：" + string.Join("、", running.Distinct())
                           + "。请完全退出 NVIDIA App，或关闭「游戏内覆盖层 / 信息浮窗」后再启动游戏；"
                           + "它可能与 ReShade / OptiScaler / DLSS5 抢呈现 hook，导致黑屏。";
            return item;
        }

        item.Level = Dlss5CheckLevel.Ok;
        item.Message = "未检测到正在运行的 NVIDIA 信息浮窗组件。";
        return item;
    }

    #endregion


    #region 20 每游戏插件包（同一插件多版本 / 这个游戏在用哪一版 / 包与共享目录是否同步）

    /// <summary>
    /// 20. 每游戏插件包体检：
    /// <list type="bullet">
    /// <item>这个游戏用的是共享目录还是专属插件包；</item>
    /// <item>选了多版本的插件各在用哪一版；</item>
    /// <item>包目录和共享目录 / 版本归档是否还一致（硬链接在不在、归档版本有没有被删）。</item>
    /// </list>
    /// 一律按**这个游戏真正的插件目录**算（<see cref="Dlss5CompatContext.PluginService"/> 的 AddonDirectory）。
    /// </summary>
    internal static Dlss5CompatItem CheckAddonPack(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(20, "游戏目录", "每游戏插件包：这个游戏在用哪一版 / 和共享目录是否同步");

        if (context.ShadeHost is not { } host)
        {
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = context.IsOptiScalerOnly
                ? "没装 HoYoShade —— 走 OptiScaler 的 DLSS-NR 时没有 ReShade 插件包这一层，这一项不用管。"
                : "没装 HoYoShade，还没有插件包可查。";
            return item;
        }

        if (string.IsNullOrWhiteSpace(context.GameBiz))
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "先选一个游戏，才知道这个游戏用哪一版插件。";
            return item;
        }

        try
        {
            AddonPackAudit audit = GameAddonPackAudit.Inspect(AppConfig.CacheRoot, context.GameBiz!, host.AddonsPath);

            if (!audit.IsPack)
            {
                item.Level = Dlss5CheckLevel.Ok;
                item.Message = "这个游戏没有专属插件包，插件直接用共享目录：" + host.AddonsPath + "\n" +
                               "给某个插件在插件卡片里选了具体版本之后，才会为它拼一份专属插件包。";
                return item;
            }

            var lines = new List<string>
            {
                "这个游戏用的是专属插件包：" + audit.AddonDirectory,
                "共享插件目录：" + audit.SharedAddonsDirectory,
                "包里的文件：" + audit.PackFileCount + " 个",
            };

            lines.Add(audit.Selections.Count == 0
                ? "这个游戏没有给任何插件单独选版本（用共享目录里当前生效的那一份）。"
                : "这个游戏在用的插件版本：" + string.Join("、", audit.Selections.Select(kv => $"{kv.Key} = {kv.Value}")));

            var problems = new List<string>();

            if (audit.MissingArchiveVersions.Count > 0)
            {
                problems.Add("选中的版本在归档里没了（cache\\plugins）：" + string.Join("、", audit.MissingArchiveVersions));
            }

            if (audit.MissingFiles.Count > 0)
            {
                problems.Add("共享目录 / 归档里有、包里缺的文件：" + string.Join("、", audit.MissingFiles.Take(10)));
            }

            if (audit.StaleFiles.Count > 0)
            {
                problems.Add("包里的文件和源对不上（硬链接断了 / 内容旧了）：" + string.Join("、", audit.StaleFiles.Take(10)));
            }

            if (audit.ExtraFiles.Count > 0)
            {
                problems.Add("包里多出来的文件：" + string.Join("、", audit.ExtraFiles.Take(10)));
            }

            if (problems.Count == 0)
            {
                item.Level = Dlss5CheckLevel.Ok;
                item.Message = string.Join("\n", lines) + "\n每游戏插件包和共享目录 / 归档一致（硬链接还在）。";
                return item;
            }

            item.Level = Dlss5CheckLevel.Warning;
            item.Message = string.Join("\n", lines) + "\n " + string.Join("\n ", problems) +
                           "\n点右边「重拼插件包」会按当前选择重新链接一遍（共享目录和归档都不动）。";
            item.FixLabel = "重拼插件包";
            item.CanFix = true;
            item.Fix = () =>
            {
                if (context.GameId is not { } gameId)
                {
                    return Task.FromResult<string?>("不知道当前游戏，重拼不了。");
                }

                string? pack = GameAddonPackService.Sync(gameId, context.Game, host);
                return Task.FromResult<string?>(pack is null
                    ? "没拼出插件包 —— 这个游戏可能已经没有任何单独选版本的插件了。"
                    : "已重拼插件包：" + pack);
            };
            return item;
        }
        catch (Exception ex)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "检查每游戏插件包失败：" + ex.Message;
            return item;
        }
    }

    #endregion

    #region 21 NVIDIA 面板：是否替换了此游戏的帧生成模型

    /// <summary>21. NV 面板（NVIDIA App / 驱动）替换了帧生成模型时，多倍帧生成可能闪烁 / 黑屏。只检测，不代改。</summary>
    private static Dlss5CompatItem CheckNvAppFgModelOverride(Dlss5CompatContext context)
    {
        var item = new Dlss5CompatItem(21, "显卡", "NVIDIA 面板  是否替换了此游戏的帧生成模型");

        string? exePath = context.Game?.ExePath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = "先选一个游戏。";
            return item;
        }

        string exeName = Path.GetFileName(exePath);
        NvDrsInterop.State state = NvDrsInterop.Read(exeName, NvDrsInterop.DlssFgOverrideEnableSettingId, "Enable DLSS-FG override", NvDrsInterop.DescribeBinary);

        if (!state.Ok)
        {
            item.Level = Dlss5CheckLevel.Info;
            item.Message = $"读不到「{exeName}」的驱动配置：{state.Error}";
            return item;
        }

        if (!state.Overridden || state.Value == 0)
        {
            item.Level = Dlss5CheckLevel.Ok;
            item.Message = $"「{exeName}」未被 NV 面板替换帧生成模型 正常。";
            return item;
        }

        item.Level = Dlss5CheckLevel.Warning;
        item.Message = "此游戏已被NV面板替换帧生成模型，在使用多倍帧生成可能出现闪烁和黑屏。"
                       + "建议先到 NVIDIA App 关掉这游戏的「DLSS 覆盖  帧生成」，再用多倍帧生成。";

        return item;
    }

    #endregion

}
