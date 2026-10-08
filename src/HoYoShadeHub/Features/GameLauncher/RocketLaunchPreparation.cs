using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.Modules;
using HoYoShadeHub.Features.Plugins;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>Files-only preparation. No game/Rocket/XXMI process start, no injector arm,
/// no driver/network changes, no packet capture. Rocket exclusively owns GIMI.</summary>
internal static class RocketLaunchPreparation
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static async Task<RocketIntegration.PrepareResult> PrepareAsync(GameId gameId, GameEntry? entry)
    {
        await Gate.WaitAsync();
        try
        {
            if (MultiplayerGameGuard.IsActive) return new(false, MultiplayerGameGuard.BlockReason());
            if (!RocketIntegration.SupportsGame(gameId.GameBiz.Value)) return new(false, "Rocket 桥联动目前只支持原神。");
            if (AppConfig.GetEnableDX12(gameId.GameBiz)) return new(false, "FSR 桥需要原神 DX11 路线，请关闭游戏 DX12 启动选项。");
            string? config = RocketIntegration.FindConfigPath(AppConfig.RocketConfigPath);
            if (config is null) return new(false, "找不到 Rocket config.ini，请点启用 Rocket 右侧设置选择位置。");
            var discovery = GameCatalog.CreateService();
            entry ??= GameCatalog.GetOrCreate(discovery, gameId);
            if (entry?.ExePath is not { Length: > 0 } exe || !File.Exists(exe)) return new(false, "请先定位原神主程序。");
            // Name polling only. Never prepare files against a currently running game.
            foreach (string name in new[] { "YuanShen", "GenshinImpact" })
            {
                var processes = Process.GetProcessesByName(name);
                bool running = processes.Length != 0;
                foreach (var process in processes) process.Dispose();
                if (running) return new(false, "原神正在运行，请自行退出后准备 Rocket 联动。");
            }
            bool optEnabled = AppConfig.GetUseOptiScalerLaunchOption(gameId);
            bool shadeEnabled = AppConfig.GetUseHoYoShadeLaunchOption(gameId) || AppConfig.GetUseOpenHoYoShadeLaunchOption(gameId);
            string? opt = optEnabled ? AppConfig.GetSelectedOptiScalerDll(gameId) : null;
            if (optEnabled && (string.IsNullOrWhiteSpace(opt) || !File.Exists(opt))) return new(false, "请先选择已安装的 OptiScaler 构建。");
            ShadeHost? host = shadeEnabled ? new ShadeHost(Path.Combine(AppConfig.UserDataFolder,
                AppConfig.GetUseHoYoShadeLaunchOption(gameId) ? "HoYoShade" : "OpenHoYoShade")) : null;
            string? shade = host is null ? null : Path.Combine(host.RootPath, "ReShade64.dll");
            if (shadeEnabled && !File.Exists(shade)) return new(false, "找不到所选 HoYoShade 的 ReShade64.dll。");
            if (!optEnabled && !shadeEnabled) return new(false, "请至少启用 OptiScaler 或 HoYoShade。");
            if (!ModuleRegistry.EnsureGenshinFsrBridgeForLaunch(gameId)) return new(false, "找不到受支持的 FSR 桥，请先安装原神模块。");
            string? bridge = ModuleRegistry.ResolveInjectionDlls(gameId).FirstOrDefault(spec => ModuleRegistry.IsGenshinFsrBridgeDll(spec.DllPath)).DllPath;
            if (string.IsNullOrWhiteSpace(bridge) || !File.Exists(bridge)) return new(false, "当前模块选择中没有可用的 Dx11FsrBridge.dll。");
            string bridgeDirectory = Path.GetDirectoryName(bridge)!;
            // Validate Rocket schema before modifying graphics configuration.
            _ = RocketIntegration.BuildRocketConfiguration(File.ReadAllBytes(config), bridge);
            if (opt is not null)
            {
                string build = Path.GetDirectoryName(opt)!;
                if (!OptiScalerRuntime.PrepareGenshinEarlyConfiguration(build, gameId.GameBiz.Value))
                    return new(false, "准备 OptiScaler profile/依赖路径失败。");
                OptiScalerRuntime.EnsureFsrBridgeIni(bridgeDirectory);
                OptiScalerRuntime.EnsureFsrBridgeRenderScale(bridgeDirectory);
                if (OptiScalerRuntime.WriteFsrBridgeAutoload(bridgeDirectory, opt) is null
                    || !OptiScalerRuntime.EnsureFsrBridgeOptiSidecar(bridgeDirectory, build))
                    return new(false, "准备桥的 OptiScaler 配套配置失败。");
            }
            var chain = RocketIntegration.EnsureGraphicsChain(bridge, opt, shade);
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
            return result with { Message = RocketIntegration.WaitingText + Environment.NewLine + "桥 DLL：" + bridge + addonNote
                + Environment.NewLine + "Rocket 已运行时请在其界面重新读取/确认额外 DLL；Hub 不会自动启动或重启 Rocket。" };
        }
        catch (Exception ex) { return new(false, "准备 Rocket 联动失败：" + ex.Message); }
        finally { Gate.Release(); }
    }
}
