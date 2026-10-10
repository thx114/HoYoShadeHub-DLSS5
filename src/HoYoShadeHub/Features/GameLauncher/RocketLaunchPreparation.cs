using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.Modules;
using HoYoShadeHub.Features.OptiScaler;
using HoYoShadeHub.Features.Plugins;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>
/// Files-only preparation. No game/Rocket/XXMI process start, no injector arm,
/// no driver/network changes, no packet capture. Rocket exclusively owns process
/// creation, the MI loader and every DLL it injects.
///
/// <para>
/// 两条路线：
/// <list type="bullet">
/// <item>原神：第三方 DLL 槽放 <c>Dx11FsrBridge.dll</c>，桥在进程内按 chain.txt 拉 OptiScaler/ReShade
/// （NR/帧生成那条集成路线，原样保留）。</item>
/// <item>其他 XXMI 游戏（星铁/绝区零/鸣潮/终末地）：没有 FSR 桥，改用火箭的
/// <c>&lt;游戏名&gt;插件DLL列表</c>——列表顺序就是注入顺序，Hub 只负责写这份列表。</item>
/// </list>
/// </para>
/// </summary>
internal static class RocketLaunchPreparation
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<RocketIntegration.PrepareResult> PrepareAsync(GameId gameId, GameEntry? entry)
    {
        await Gate.WaitAsync();
        try
        {
            if (MultiplayerGameGuard.IsActive) return new(false, MultiplayerGameGuard.BlockReason());

            string? rocketGame = RocketIntegration.GameNameFor(gameId.GameBiz.Value, entry?.DisplayName);
            if (rocketGame is null)
                return new(false, "火箭模式目前支持原神、崩坏：星穹铁道、绝区零、鸣潮、终末地（要有火箭后端，并且 XXMI 支持这个游戏）。");
            if (AppConfig.GetEnableDX12(gameId.GameBiz))
                return new(false, "火箭的模型导入器是 d3d11 代理，需要游戏跑 DX11 路线，请关闭游戏 DX12 启动选项。");

            string? config = RocketIntegration.FindConfigPath(AppConfig.RocketConfigPath);
            if (config is null) return new(false, "找不到 Rocket config.ini，请点启用 Rocket 右侧设置选择位置。");

            var discovery = GameCatalog.CreateService();
            entry ??= GameCatalog.GetOrCreate(discovery, gameId);
            if (entry?.ExePath is not { Length: > 0 } exe || !File.Exists(exe)) return new(false, "请先定位游戏主程序。");
            // Name polling only. Never prepare files against a currently running game.
            if (IsGameRunning(rocketGame, exe)) return new(false, entry.DisplayName + " 正在运行，请自行退出后准备火箭联动。");

            bool optEnabled = AppConfig.GetUseOptiScalerLaunchOption(gameId);
            bool shadeEnabled = AppConfig.GetUseHoYoShadeLaunchOption(gameId) || AppConfig.GetUseOpenHoYoShadeLaunchOption(gameId);
            string? opt = optEnabled ? AppConfig.GetSelectedOptiScalerDll(gameId) : null;
            if (optEnabled && (string.IsNullOrWhiteSpace(opt) || !File.Exists(opt))) return new(false, "请先选择已安装的 OptiScaler 构建。");
            ShadeHost? host = shadeEnabled ? new ShadeHost(Path.Combine(AppConfig.UserDataFolder,
                AppConfig.GetUseHoYoShadeLaunchOption(gameId) ? "HoYoShade" : "OpenHoYoShade")) : null;
            string? shade = host is null ? null : Path.Combine(host.RootPath, "ReShade64.dll");
            if (shadeEnabled && !File.Exists(shade)) return new(false, "找不到所选 HoYoShade 的 ReShade64.dll。");
            if (!optEnabled && !shadeEnabled) return new(false, "请至少启用 OptiScaler 或 HoYoShade。");

            // 原神：FSR 桥独占第三方 DLL 槽（桥自己按 chain.txt 拉其余层），原有路线一行不动。
            if (RocketIntegration.SupportsGame(gameId.GameBiz.Value))
                return PrepareGenshin(gameId, entry, config, host, opt, shade, optEnabled);

            return PrepareOtherGame(gameId, rocketGame, config, opt, shade);
        }
        catch (Exception ex) { return new(false, "准备 Rocket 联动失败：" + ex.Message); }
        finally { Gate.Release(); }
    }

    /// <summary>原神：桥 + GIMI 那条集成路线（RocketMode/NR/帧生成配套），行为与以前一致。</summary>
    private static RocketIntegration.PrepareResult PrepareGenshin(GameId gameId, GameEntry entry, string config,
        ShadeHost? host, string? opt, string? shade, bool optEnabled)
    {
        if (!ModuleRegistry.EnsureGenshinFsrBridgeForLaunch(gameId)) return new(false, "找不到受支持的 FSR 桥，请先安装原神模块。");
        string? bridge = ModuleRegistry.ResolveInjectionDlls(gameId).FirstOrDefault(spec => ModuleRegistry.IsGenshinFsrBridgeDll(spec.DllPath)).DllPath;
        if (string.IsNullOrWhiteSpace(bridge) || !File.Exists(bridge)) return new(false, "当前模块选择中没有可用的 Dx11FsrBridge.dll。");
        string bridgeDirectory = Path.GetDirectoryName(bridge)!;
        // Validate Rocket schema before modifying graphics configuration.
        _ = RocketIntegration.BuildRocketConfiguration(File.ReadAllBytes(config), bridge);
        if (opt is not null)
        {
            string build = Path.GetDirectoryName(opt)!;
            // 火箭模式下 Hub 抓不到进程退出，上一次会话的改动还在主 ini 里：
            // 在 PrepareGenshinEarlyConfiguration 用 profile 盖掉它之前，先收回来（存 profile + 回写配置）。
            OptiScalerPresets.CollectFromRuntime(build);
            if (!OptiScalerRuntime.PrepareGenshinEarlyConfiguration(build, gameId.GameBiz.Value))
                return new(false, "准备 OptiScaler profile/依赖路径失败。");
            if (!OptiScalerRuntime.SetRocketMode(build, true))
                return new(false, "写入 OptiScaler RocketMode=true 失败。");
            OptiScalerRuntime.EnsureFsrBridgeIni(bridgeDirectory);
            OptiScalerRuntime.EnsureFsrBridgeRenderScale(bridgeDirectory);
            if (OptiScalerRuntime.WriteFsrBridgeAutoload(bridgeDirectory, opt) is null
                || !OptiScalerRuntime.EnsureFsrBridgeOptiSidecar(bridgeDirectory, build))
                return new(false, "准备桥的 OptiScaler 配套配置失败。");
        }
        // 用户勾的其他模块也写进桥的清单：火箭模式下 Hub 同样不开外部注入器（火箭只负责把桥注进去），
        // 以前只写图形层，这些模块就静默失效。
        List<string> chainExtraModules = BridgeChainModules.Resolve(gameId, bridge, opt, shade);
        var chain = RocketIntegration.EnsureGraphicsChain(bridge, opt, shade, chainExtraModules);
        if (!chain.Success) return chain;
        if (host is not null)
        {
            GameAddonPackService.Sync(gameId, entry, host);
            bool finalRoute = optEnabled;
            var ini = GameIniBootstrap.Ensure(entry, host, finalRoute);
            if (ini.Failed || ini.MissingTemplate) return new(false, "准备 ReShade 配置失败，尚未配置 Rocket 注入路径。");
            GameAddonPackService.Sync(gameId, entry, host);
            ini = GameIniBootstrap.Ensure(entry, host, finalRoute);
            if (ini.Failed) return new(false, "同步 ReShade 插件配置失败。");
        }
        var result = RocketIntegration.Prepare(config, bridge);
        if (!result.Success) return result;
        if (!File.Exists(Path.Combine(bridgeDirectory, RocketIntegration.BridgeChainFileName)))
            return new(false, "桥加载链被其他程序移除，请重新准备。");
        string addonNote = "";
        if (host is not null && entry.ReShadeIniPath is { } gameIni)
        {
            var profile = ReShadeProfile.Load(gameIni);
            string? addonDirectory = profile.ResolveAddonDirectory();
            bool controllerDisabled = profile.IsDisabled("FsrBridgeDepthAddon.addon64");
            bool controllerMissing = addonDirectory is null ||
                !File.Exists(Path.Combine(addonDirectory, "FsrBridgeDepthAddon.addon64"));
            if (controllerDisabled || controllerMissing)
                addonNote = Environment.NewLine + "你已禁用或未安装深度控制插件：桥深度／NR Input Effects 控制面板不可用；已保留关闭状态，不自动启用。";
        }
        string extraNote = chainExtraModules.Count == 0 ? "" : Environment.NewLine + "额外模块 " + chainExtraModules.Count
            + " 个也在链里（GIMI 之后、OptiScaler 之前）：" + string.Join(" → ", chainExtraModules.Select(Path.GetFileName));
        return result with { Message = RocketIntegration.WaitingText + Environment.NewLine + "桥 DLL：" + bridge + extraNote + addonNote
            + Environment.NewLine + "Rocket 已运行时请在其界面重新读取/确认额外 DLL；Hub 不会自动启动或重启 Rocket。" };
    }

    /// <summary>
    /// 其他游戏：没有 FSR 桥，用火箭的「&lt;游戏名&gt;插件DLL列表」把多份 DLL 交给火箭注入。
    /// 顺序 = 注入顺序：OptiScaler 在前、ReShade 在后。
    /// </summary>
    private static RocketIntegration.PrepareResult PrepareOtherGame(GameId gameId, string rocketGame, string config,
        string? opt, string? shade)
    {
        var dlls = new List<string>();
        // 用户勾的其他模块排最前（和 Hub 批量注入路线一致：模块 → OptiScaler → ReShade）
        dlls.AddRange(BridgeChainModules.Resolve(gameId, opt, shade));
        if (opt is not null) dlls.Add(opt);
        if (shade is not null) dlls.Add(shade);
        if (dlls.Count == 0) return new(false, "请至少启用 OptiScaler 或 HoYoShade。");
        if (opt is not null)
        {
            string build = Path.GetDirectoryName(opt)!;
            // 同上：先把上一次会话的改动收回它的 profile，再激活这次的 profile
            OptiScalerPresets.CollectFromRuntime(build);
            if (!OptiScalerRuntime.PrepareGenshinEarlyConfiguration(build, gameId.GameBiz.Value))
                return new(false, "准备 OptiScaler profile/依赖路径失败。");
            // 非原神没有 GIMI 接管最终 ReShade runtime，别把上一次原神跑留下的 true 带过去。
            if (!OptiScalerRuntime.SetRocketMode(build, false))
                return new(false, "写入 OptiScaler RocketMode=false 失败。");
        }
        var listed = RocketIntegration.PreparePluginList(config, rocketGame, dlls);
        if (!listed.Success) return listed;
        return listed with { Message = RocketIntegration.WaitingText + Environment.NewLine
            + "火箭会按这个顺序注入 " + dlls.Count + " 份 DLL：" + string.Join(" → ", dlls.Select(Path.GetFileName))
            + Environment.NewLine + "Rocket 已运行时请在其界面重新读取/确认额外 DLL；Hub 不会自动启动或重启 Rocket。" };
    }

    private static bool IsGameRunning(string rocketGame, string exe)
    {
        var names = new List<string> { Path.GetFileNameWithoutExtension(exe) };
        if (rocketGame == "原神") names.AddRange(["YuanShen", "GenshinImpact"]);
        foreach (string name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var processes = Process.GetProcessesByName(name);
            bool running = processes.Length != 0;
            foreach (var process in processes) process.Dispose();
            if (running) return true;
        }
        return false;
    }
}
