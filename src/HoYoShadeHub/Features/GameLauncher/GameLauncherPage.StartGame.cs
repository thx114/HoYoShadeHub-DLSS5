using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Core.HoYoShade;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.GameSetting;
using HoYoShadeHub.Features.GameSelector;
using HoYoShadeHub.Features.Background;
using HoYoShadeHub.Features.HoYoPlay;
using HoYoShadeHub.Features.Overlay;
using HoYoShadeHub.Features.OptiScaler;
using HoYoShadeHub.Features.Plugins;
using HoYoShadeHub.Features.Modules;
using HoYoShadeHub.Features.Setting;
using HoYoShadeHub.Features.ViewHost;
using HoYoShadeHub.Frameworks;
using HoYoShadeHub.Helpers;
using HoYoShadeHub.RPC.GameInstall;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;


namespace HoYoShadeHub.Features.GameLauncher;

public sealed partial class GameLauncherPage : PageBase
{
    #region Start Game




    private Timer processTimer;


    [ObservableProperty]
    private partial Process? GameProcess { get; set; }

    /// <summary>「关闭游戏」进行中（防连点，控件侧禁用关闭按钮）</summary>
    [ObservableProperty]
    public partial bool IsClosingGame { get; set; }
    partial void OnGameProcessChanged(Process? oldValue, Process? newValue)
    {
        processTimer?.Stop();
        if (processTimer is null)
        {
            processTimer = new(1000);
            processTimer.Elapsed += (_, _) => CheckGameExited();
        }
        if (newValue != null)
        {
            processTimer?.Start();
            RunningGameInfo = $"{newValue.ProcessName}.exe ({newValue.Id})";
            RunningGameService.AddRuninngGame(CurrentGameBiz, newValue);
        }
        else
        {
            RunningGameInfo = null;
            _logger.LogInformation("Game process exited");
        }
    }

    

    private string? _runningGameInfo;
    public string? RunningGameInfo
    {
        get => _runningGameInfo;
        set => SetProperty(ref _runningGameInfo, value);
    }



    /// <summary>
    /// 启动前检查：这个游戏**开着**的 DLSS5 插件，需要的运行时 dll 在不在插件目录里。
    /// 缺了就弹窗 —— 用户要求「启动游戏如果没有这个文件也弹窗」。
    /// </summary>
    /// <returns>false 表示用户选择不启动</returns>
    private async Task<bool> ConfirmDlssRuntimeAsync()
    {
        try
        {
            if (_currentGameEntry is not { ReShadeIniPath: { } ini } || !File.Exists(ini))
            {
                return true;
            }

            ShadeHost? host = ShadeHostLocator.FromShadeRoot(Path.Combine(AppConfig.UserDataFolder, "HoYoShade"));
            if (host is null)
            {
                return true;
            }

            var service = GamePluginServiceFactory.Create(_currentGameEntry, host);

            List<GameAddonState> broken =
            [
                .. service.GetAddons().Where(a => a.Enabled && a.CanToggle && a.DllStatus.HasMissingRequired)
            ];

            if (broken.Count == 0)
            {
                return true;
            }

            string names = string.Join("、", broken.Select(a => a.DisplayName));
            string missing = string.Join("、", broken.SelectMany(a => a.DllStatus.MissingRequired).SelectMany(r => r.Files).Distinct());

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "DLSS5 缺运行时文件",
                Content = $"「{names}」是 DLSS5 插件，但插件目录里没有 {missing} —— 这样启动它们加载不了。\n\n" +
                          "要先去「DLL 配置」里装一下吗？（装完再启动就行）",
                PrimaryButtonText = "仍然启动",
                SecondaryButtonText = "去 DLL 配置",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Secondary,
            };

            ContentDialogResult result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Secondary)
            {
                WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(DllConfigPage)));
                return false;
            }

            return result == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            // 检查本身出错不该拦着人玩游戏
            _logger.LogWarning(ex, "Check DLSS runtime before launch");
            return true;
        }
    }

    /// <summary>
    /// 原神启用 OptiScaler 时，确保 FSR Bridge 作为前置模块存在且已加入本游戏。
    /// 单独启用 Bridge 不会写 OptiScaler autoload；是否加载 Opti 仍由启动页开关决定。
    /// </summary>
    private async Task<bool> ConfirmGenshinFsrBridgeForOptiAsync()
    {
        try
        {
            if (!UseOptiScaler || CurrentGameId is not { } gameId || !ModuleRegistry.IsGenshin(gameId))
            {
                return true;
            }

            ModuleDefinition? bridge = ModuleRegistry.Find("genshin-fsr-bridge");
            string? bridgeDll = ModuleRegistry.EnsureBundledGenshinFsrBridge();
            bool ready = bridge is not null
                && bridge.Enabled
                && bridgeDll is not null
                && ModuleRegistry.IsUsed(gameId, bridge.Id);
            if (!ready && bridgeDll is not null && ModuleRegistry.EnsureGenshinFsrBridgeForLaunch(gameId))
            {
                UseModules = true;
                _logger.LogInformation("Installed Genshin FSR Bridge associated with {Game} before OptiScaler", gameId.GameBiz);
                return true;
            }

            if (ready)
            {
                // 以前只勾了模块页、但关了启动页「启用模块」时，不能让 OptiScaler
                // 走回原神的外部注入路径；Opti 开启时把它联动打开。
                UseModules = true;
                return true;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "OptiScaler 需要 Genshin FSR Bridge",
                Content = "原神启用了 OptiScaler，但 FSR Bridge 模块没有启用到本游戏。FSR Bridge 必须先于 OptiScaler 加载，是否现在安装并启用？",
                PrimaryButtonText = "安装并启用",
                CloseButtonText = "取消启动",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return false;
            }

            if (!ModuleRegistry.EnsureGenshinFsrBridgeForLaunch(gameId))
            {
                await new ContentDialog
                {
                    XamlRoot = XamlRoot,
                    Title = "FSR Bridge 缺失",
                    Content = "已安装模块或覆盖包中没有受支持的 Dx11FsrBridge.dll，请重新导入原神覆盖包或下载 FSR Bridge。",
                    CloseButtonText = "确定",
                }.ShowAsync();
                return false;
            }

            UseModules = true;
            _logger.LogInformation("Genshin FSR Bridge enabled for {Game} before OptiScaler", gameId.GameBiz);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check Genshin FSR Bridge before OptiScaler launch; refusing unsafe external OptiScaler injection");
            return false;
        }
    }


    /// <summary>
    /// 启用 OptiScaler 时启动前查「NVIDIA 驱动里的 DLSS-FG 多帧生成数量」（设置 ID 0x104D6667）。
    /// 被驱动钉住数量时 MFG 解锁会看不出效果，问一下要不要顺手改成 N/A（写 0 = 不覆盖）。
    /// 读不到 / 没覆盖 / 已经是 N/A / 写失败  一律放行，不拦着人玩游戏。
    /// </summary>
    /// <returns>false 表示中止本次启动（目前总会返回 true）</returns>
    private async Task<bool> ConfirmNvMfgCountAsync()
    {
        try
        {
            if (!UseOptiScaler || CurrentGameId is not { } gameId)
            {
                return true;
            }

            GameEntry? entry = GameCatalog.GetOrCreate(GameCatalog.CreateService(), gameId);
            if (entry?.ExePath is not { Length: > 0 } exePath)
            {
                return true;
            }

            string exeName = Path.GetFileName(exePath);
            NvDrsMfgCount.State state = await Task.Run(() => NvDrsMfgCount.Read(exeName));
            if (!state.Ok || !state.Overridden || state.Value == NvDrsMfgCount.NaValue)
            {
                return true;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "驱动在钉多帧生成数量",
                Content = $"NVIDIA 驱动里「{exeName}」的多帧生成数量被设成 {state.Display}。\\n\\n"
                          + "这条覆盖会盖住 OptiScaler 的 MFG 解锁（游戏里倍数不对 / 看不出效果）。\\n"
                          + "要不要顺手改成 N/A（不再覆盖）？",
                PrimaryButtonText = "改为 N/A 并启动",
                CloseButtonText = "仍然启动",
                DefaultButton = ContentDialogButton.Primary,
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                _logger.LogInformation("NV MFG count {Value} kept by user, launch continues", state.Value);
                return true;
            }

            NvDrsMfgCount.State after = await Task.Run(() => NvDrsMfgCount.SetToNa(exeName));
            if (after.Ok)
            {
                InAppToast.MainWindow?.Success("驱动配置", $"已把 {exeName} 的 MFG 数量改成 N/A，重启游戏生效。", 8000);
            }
            else
            {
                InAppToast.MainWindow?.Error("驱动配置", after.Error ?? "写入失败", 10000);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Check NV MFG count before launch");
            return true;
        }
    }

    private async Task<bool> CheckGameRunningAsync()
    {
        try
        {
            GameProcess = await _gameLauncherService.GetGameProcessAsync(CurrentGameId);
            if (GameProcess != null)
            {
                GameState = GameState.GameIsRunning;
                _logger.LogInformation("Game is running ({name}, {pid})", GameProcess.ProcessName, GameProcess.Id);
                return true;
            }
        }
        catch { }
        return false;
    }




    /// <summary>
    /// 兜底保存 OptiScaler 的游戏 profile。
    ///
    /// <para>
    /// 正常注入路径会在稳定进程上挂 Exited 事件，但原神可能由提权进程、
    /// 反作弊或启动器重启路径结束；这时 Exited 事件不一定能回到页面对象。
    /// 如果只依赖 HookInjectedTarget，游戏内 Save 的 FGInput/FGOutput 会在下次
    /// Activate 时被旧 profile 覆盖。轮询发现进程退出时再保存一次，覆盖这些路径。
    /// </para>
    /// </summary>
    private void StoreCurrentOptiScalerProfileOnExit(string reason)
    {
        try
        {
            if (CurrentGameId is not { } gameId
                || AppConfig.GetSelectedOptiScalerDll(gameId) is not { Length: > 0 } optiDll
                || Path.GetDirectoryName(optiDll) is not { Length: > 0 } buildDirectory)
            {
                return;
            }

            string gameKey = gameId.GameBiz.ToString();
            if (OptiScalerProfiles.Store(buildDirectory, gameKey))
            {
                _logger.LogInformation(
                    "OptiScaler ini profile stored on game-exit fallback: {Game} ({Build}, reason={Reason})",
                    gameKey, buildDirectory, reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Store OptiScaler profile on game exit fallback");
        }
    }

    private void CheckGameExited()
    {
        try
        {
            if (GameProcess != null)
            {
                if (GameProcess.HasExited)
                {
                    StoreCurrentOptiScalerProfileOnExit("process-poll");
                    DispatcherQueue.TryEnqueue(CheckGameVersion);
                    GameProcess = null;
                    StopFpsUnlocker();
                    // 游戏没了：让背景（含视频）回来
                    WeakReferenceMessenger.Default.Send(new GameExitedMessage());
                }
            }
        }
        catch { }
    }




    /// <summary>
    /// 检查Blender插件注入进程是否正在运行
    /// </summary>
    /// <returns>如果检测到注入进程正在运行，返回进程名；否则返回null</returns>
    private string? CheckBlenderPluginInjectionProcessRunning()
    {
        try
        {
            // Check for Genshin Blender plugin (client.exe)
            var clientProcesses = Process.GetProcessesByName("client");
            if (clientProcesses.Length > 0)
            {
                _logger.LogWarning("Detected running Genshin Blender plugin injection process (client.exe)");
                return "client.exe";
            }

            // Check for ZZZ Blender plugin (loader.exe)
            var loaderProcesses = Process.GetProcessesByName("loader");
            if (loaderProcesses.Length > 0)
            {
                _logger.LogWarning("Detected running ZZZ Blender plugin injection process (loader.exe)");
                return "loader.exe";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking for Blender plugin injection processes");
        }
        return null;
    }



    // 注入器状态挂成 static：页面切走再切回来（实例被重建）也得记得上一个注入器是谁，
    // 否则「再次启动游戏先停掉上一个」会失效、提示条上的「停止」也可能点空。

    /// <summary>「额外注入 DLL」那个等待/注入任务的取消源（停注入器时一起停）</summary>
    private static System.Threading.CancellationTokenSource? _extraInjectCts;

    /// <summary>帧率解锁器实例（游戏退出/重新启动时释放）</summary>
    private static FpsUnlocker? _fpsUnlocker;

    /// <summary>注入模式：当前这个 inject.exe（再启动游戏要先把它停掉）</summary>
    private static Process? _injectorProcess;

    /// <summary>「已启动 HoYoShade 注入器」那条常驻提示（注入器一停就收掉）</summary>
    private static InfoBar? _injectorToast;

    /// <summary>挂上常驻提示：标题右边一个红色「停止」，点它就把注入器停掉</summary>
    private void ShowInjectorToast(string shadeName)
    {
        CloseInjectorToast();

        try
        {
            _injectorToast = InAppToast.MainWindow?.ShowSticky(
                string.Format(Lang.GameLauncher_InjectorStarted, shadeName),
                "停止",
                () => StopInjector("手动停止"));

            // 注入器自己退了（注入完 / 出错）就把提示收掉，别一直挂着
            if (_injectorProcess is { } process)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => DispatcherQueue.TryEnqueue(() =>
                {
                    if (_injectorProcess is { } current && current.Id == process.Id)
                    {
                        _injectorProcess = null;
                    }

                    CloseInjectorToast();
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Show injector toast");
        }
    }

    /// <summary>这个游戏的进程名（条目里的 exe 名 → Hub 映射 → 内置表）</summary>
    /// <summary>「没勾 shade、只等进程起来注 DLL」的常驻提示：和注入器那条一样带「停止」</summary>
    private void ShowWaitProcessToast(string processName)
    {
        CloseInjectorToast();

        try
        {
            _injectorToast = InAppToast.MainWindow?.ShowSticky(
                $"等待 {processName} 启动  起来后注入「额外注入 DLL」/ OptiScaler",
                "停止",
                () => StopExtraInjection("手动停止"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Show wait-process toast");
        }
    }

    /// <summary>停掉「等游戏进程起来再注入」这件事（取消等待 + 收提示条）</summary>
    private void StopExtraInjection(string reason)
    {
        try
        {
            _logger.LogInformation("Stop extra DLL injection wait: {Reason}", reason);
            _extraInjectCts?.Cancel();
            _extraInjectCts = null;
            IsWaitProcessMode = false;
            CloseInjectorToast();
            InAppToast.MainWindow?.Information("注入模式", "已停止等待游戏进程。", 5000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stop extra injection wait");
        }
    }

    private async Task<string?> ResolveTargetProcessNameAsync()
    {
        string? processName = _currentGameEntry?.ProcessName;

        if (string.IsNullOrWhiteSpace(processName) && CurrentGameId is not null)
        {
            try
            {
                processName = await _gameLauncherService.GetGameExeNameAsync(CurrentGameId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Resolve game exe name");
            }
        }

        processName ??= KnownProcessNames.ForBiz(CurrentGameId?.GameBiz ?? default);

        // 鸣潮：注册 exe 是启动器壳（Wuthering Waves.exe），要注/等的是它拉起的 Client-Win64-Shipping
        return ShadeBlacklistBypass.RemapInjectProcessName(_currentGameEntry, processName);
    }

    /// <summary>
    /// 这个游戏的进程当前是否在本会话运行。进程名按游戏条目 → 游戏库 → 内置表解析。
    /// </summary>
    private async Task<bool> IsTargetGameRunningAsync()
    {
        string? processName = await ResolveTargetProcessNameAsync();
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        string name = Path.GetFileNameWithoutExtension(processName);

        try
        {
            int currentSessionId = Process.GetCurrentProcess().SessionId;
            return Process.GetProcessesByName(name)
                .Any(p => p.SessionId == currentSessionId);
        }
        catch
        {
            return false;
        }
    }



    /// <summary>
    /// 要注入的一个 DLL（label 只用在提示文案上）。
    /// BuildDirectory：OptiScaler 构建目录，用于按游戏切换 ini；null 表示普通模块。
    /// GameKey：该构建对应的游戏标识（OptiDllPath ini profile 名）。
    /// </summary>
    private sealed record InjectDllSpec(
        string Path,
        string Label,
        string? BuildDirectory = null,
        string? GameKey = null,
        bool WaitForReady = false,
        int DelaySeconds = 0,
        // 注入前等目标进程加载某个模块（如 ReShade64.dll）：把 shade 先注 / OptiScaler 后注定死成
        // 确定顺序，治两条注入路径抢跑导致的 NR 吃不到原生 DLSS 数据（崩铁卡旧帧案）
        string? WaitForModule = null,
        bool WaitForWindow = false);

    /// <summary>
    /// 「额外注入 DLL」+「启动 OptiScaler」：等游戏进程出现后把 DLL LoadLibrary 进去。
    /// 用户要的是跟 HoYoShade 的注入器**同时**注入（比如 DLSS Enabler / OptiScaler）。
    /// 游戏是用户自己启动的，所以这里得等（最多 20 分钟；注入器一停就放弃）。
    /// </summary>
    /// <summary>
    /// 注入用的 DLL 名字可改（游戏会按名字认代理 dll）。名字与源文件不同就复制一份
    /// &lt;名字&gt;.dll 到同目录并注入它；名字相同 / 出错就原样返回。
    /// </summary>
    private static bool BridgeSupportsInProcessOpti(string bridgePath)
    {
        try
        {
            var version = FileVersionInfo.GetVersionInfo(bridgePath);
            return BridgeCompatibility.IsSupported(new Version(version.FileMajorPart, version.FileMinorPart,
                version.FileBuildPart, version.FilePrivatePart));
        }
        catch
        {
            return false;
        }
    }

    private static string EnsureNamedOptiScalerDll(string? dllPath, string? dllName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dllPath) || string.IsNullOrWhiteSpace(dllName))
            {
                return dllPath ?? string.Empty;
            }

            string name = dllName.Trim();
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                name += ".dll";
            }

            if (string.Equals(Path.GetFileName(dllPath), name, StringComparison.OrdinalIgnoreCase))
            {
                return dllPath;
            }

            string directory = Path.GetDirectoryName(dllPath) ?? string.Empty;
            if (directory.Length == 0 || !File.Exists(dllPath))
            {
                return dllPath;
            }

            string target = Path.Combine(directory, name);
            File.Copy(dllPath, target, overwrite: true);
            return File.Exists(target) ? target : dllPath;
        }
        catch
        {
            return dllPath ?? string.Empty;
        }
    }

    /// <param name="shadeReShadeDll">
    /// inject.exe 黑名单绕行（鸣潮）：shade 本体的 ReShade64.dll，排最前注入；
    /// null 表示走正常 inject.exe 路径，不由这里注 shade。
    /// </param>
    private void StartExtraDllInjection(string processName, string? shadeReShadeDll = null, string? shadeName = null, string? shadeWaitForModule = null, int shadeDelaySeconds = 0, bool includeGameExtras = true, int? targetProcessId = null)
    {
        List<InjectDllSpec> specs = [];

        // 原神的 Bridge 是 OptiScaler 的进程内宿主。Opti 已启用时，启动前自动关联 Bridge，
        // 避免模块页勾选但每游戏 used-keys 仍是旧配置，导致 Bridge 注入后不再 autoload Opti。
        if (includeGameExtras && UseOptiScaler && CurrentGameId is { } genshin
            && ModuleRegistry.IsGenshin(genshin))
        {
            if (ModuleRegistry.EnsureGenshinFsrBridgeForLaunch(genshin))
            {
                UseModules = true;
                _logger.LogInformation("原神 OptiScaler 启动前自动关联 Genshin FSR Bridge：{Game}",
                    genshin.GameBiz);
            }
            else
            {
                _logger.LogWarning("原神 OptiScaler 启动前未能关联 Genshin FSR Bridge：{Game}",
                    genshin.GameBiz);
            }
        }

        // ⓪ 黑名单绕行（鸣潮）：shade 本体最先注，OptiScaler / 模块跟在后面
        if (!string.IsNullOrWhiteSpace(shadeReShadeDll) && File.Exists(shadeReShadeDll))
        {
            // XXMI 场景：等 3DMigoto 的 d3d11 落模块表再注（3DMigoto 先、ReShade 后定序）；
            // 鸣潮绕行等其它调用方不传，保持立刻注
            specs.Add(new InjectDllSpec(shadeReShadeDll, shadeName ?? "HoYoShade", DelaySeconds: shadeDelaySeconds,
                WaitForModule: shadeWaitForModule,
                WaitForWindow: string.Equals(shadeWaitForModule, "WINDOW", StringComparison.OrdinalIgnoreCase)));
        }

        // ① 启动选项里勾的「启用模块」：左侧「模块」页里**这个游戏勾上的**那些（DLSS-NR on AMD 之类）
        if (includeGameExtras && UseModules && CurrentGameId is { } moduleGameId)
        {
            foreach ((string key, string name, string path) in Features.Modules.ModuleRegistry.ResolveInjectionDlls(moduleGameId))
            {
                if (specs.All(s => !string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    bool waitReady = string.Equals(
                        Path.GetFileName(path),
                        OptiScalerRuntime.FsrBridgeDllName,
                        StringComparison.OrdinalIgnoreCase)
                        && BridgeSupportsInProcessOpti(path);

                    // 每个模块自己的「注入时机」；没单独设就用全局预热默认
                    int delay = AppConfig.GetModuleInjectDelayEffective(key, moduleGameId.GameBiz);
                    specs.Add(new InjectDllSpec(path, name, WaitForReady: waitReady, DelaySeconds: delay));
                }
            }
        }

        // ①' 桥在场、这次却没要用 OptiScaler：把上次留下的 autoload 清单撤走。
        //     否则桥启动时仍会照它把 OptiScaler 拉回进程里 —— 表现就是「明明关掉了 opt 还自带 opt」。
        //     撤走时先备份一份（只留第一份），重新勾上 opt 会在下面 ② 里写回去。
        InjectDllSpec? bridgeSpecForCleanup = specs.FirstOrDefault(s =>
            string.Equals(Path.GetFileName(s.Path), OptiScalerRuntime.FsrBridgeDllName, StringComparison.OrdinalIgnoreCase));
        bool optiScalerWanted = includeGameExtras && UseOptiScaler
                                && CurrentGameId is { } wantedOptiGame
                                && !string.IsNullOrWhiteSpace(AppConfig.GetSelectedOptiScalerDll(wantedOptiGame));
        if (bridgeSpecForCleanup is not null
            && OptiScalerRuntime.EnsureFsrBridgeRenderScale(Path.GetDirectoryName(bridgeSpecForCleanup.Path), 0.6f))
        {
            _logger.LogInformation("原神 FSR Bridge 渲染精度已固定为 0.6：{Directory}",
                Path.GetDirectoryName(bridgeSpecForCleanup.Path));
        }

        if (bridgeSpecForCleanup is not null
            && !optiScalerWanted
            && OptiScalerRuntime.RemoveFsrBridgeAutoload(
                Path.GetDirectoryName(bridgeSpecForCleanup.Path), out string? autoloadBackup))
        {
            _logger.LogInformation(
                "已撤走 FSR Bridge 的 OptiScaler autoload 清单（本次没启用 OptiScaler）：备份 {Backup}",
                autoloadBackup);
        }

        // ② OptiScaler：启动选项勾了「启用OptiScaler」+「全局插件 → OptiScaler」的总开关开着
        //    + 这个游戏在「OptiScaler」页选过构建，三者都满足才注入
        if (includeGameExtras && UseOptiScaler && CurrentGameId is { } optiGameId)
        {
            string? optiScaler = AppConfig.GetSelectedOptiScalerDll(optiGameId);

            // 用户在「OptiScaler」页可以改注入用的 DLL 名字：不同就复制一份 <名字>.dll 去注入
            optiScaler = EnsureNamedOptiScalerDll(optiScaler, AppConfig.GetOptiScalerDllName(optiGameId));
            if (!string.IsNullOrWhiteSpace(optiScaler)
                && File.Exists(optiScaler)
                && specs.All(s => !string.Equals(s.Path, optiScaler, StringComparison.OrdinalIgnoreCase)))
            {
                string gameKey = optiGameId.GameBiz.ToString();
                bool isGenshin = ModuleRegistry.IsGenshin(optiGameId);
                string? buildDirectory = Path.GetDirectoryName(optiScaler);

                // ini 按游戏分离：注入前把这个游戏的那份激活为主 ini（首次从当前主 ini 继承）。
                // 必须先 Activate 再钉 OptiDllPath，否则旧 profile（auto）会把修正覆盖掉
                if (buildDirectory is not null
                    && OptiScalerProfiles.Activate(buildDirectory, gameKey))
                {
                    _logger.LogInformation("OptiScaler ini profile activated: {Game} ({Build})", gameKey, buildDirectory);
                }

                if (isGenshin && buildDirectory is not null
                    && OptiScalerRuntime.EnsureGenshinNativeGuides(buildDirectory))
                {
                    _logger.LogInformation("原神 native DX11 NR guide 修正已在 profile 激活后启用 ({Build})", buildDirectory);
                }

                // 旧构建（早于 v1.2.1）的 profile / ini 可能还是 OptiDllPath=auto，激活后再钉绝对路径；
                // 游戏退出 Store 时修正后的主 ini 会回写 profile，下次启动即一致
                if (buildDirectory is not null && OptiScalerRuntime.EnsureConfigDllPath(buildDirectory))
                {
                    _logger.LogInformation("OptiScaler ini: OptiDllPath pinned ({Build})", buildDirectory);
                }

                // dlss-unlocked 的 MFG 解锁只认 310.9 签名：把候选里版本最高的 dlssg 钉到 OptiDllPath 根，
                // 避免 BFS 命中游戏目录自带的 310.6.0（unlock unavailable for this runtime）
                if (buildDirectory is not null && !isGenshin)
                {
                    string? dlssg = OptiScalerRuntime.EnsureDlssgForUnlock(buildDirectory,
                    [
                        Path.Combine(buildDirectory, "OptiScaler", OptiScalerRuntime.StreamlineFolderName),
                        buildDirectory,
                        Path.Combine(buildDirectory, "OptiScaler"),
                    ]);
                    if (dlssg is not null)
                    {
                        _logger.LogInformation("OptiScaler dlssg for MFG unlock: {Dlssg}", dlssg);
                    }
                }
                else if (isGenshin)
                {
                    _logger.LogInformation("Genshin: preserve test layout; do not copy nvngx_dlssg.dll to OptiScaler component root");
                }

                // 桥在 specs 里时，原神 mhyprot 会拒绝外部注入 OptiScaler（VirtualAllocEx 拒绝访问）。
                // 改由已注入的桥在进程内 LoadLibraryW 加载 OptiScaler（不走外部注入 API，mhyprot 拦不到）：
                // 写 sidecar 到桥 DLL 同目录，桥 initialize() 末尾读它并起线程加载。
                // 没桥的游戏（星铁/绝区零）仍走外部注入，行为不变。
                InjectDllSpec? bridgeSpec = specs.FirstOrDefault(s =>
                    string.Equals(Path.GetFileName(s.Path), OptiScalerRuntime.FsrBridgeDllName, StringComparison.OrdinalIgnoreCase));
                // shade 走 inject.exe 时（正常启动路径），OptiScaler 必须排在 ReShade64 之后注：
                // 两条路径各自抢跑，OptiScaler 先 hook 上 D3D11/DXGI 会让 NR 吃不到原生 DLSS 数据
                // （崩铁卡旧帧案，时好时坏 = 竞态）。黑名单绕行时 shade 是本列表 spec[0] 顺序已保证，不用等。
                string? waitForShadeModule = shadeReShadeDll is null && (UseHoYoShade || UseOpenHoYoShade)
                    ? "ReShade64.dll"
                    : null;
                if (bridgeSpec is not null)
                {
                    string? bridgeDirectory = Path.GetDirectoryName(bridgeSpec.Path);
                    string? autoloadTarget = OptiScalerRuntime.WriteFsrBridgeAutoload(bridgeDirectory, optiScaler);
                    if (isGenshin && bridgeDirectory is not null
                        && OptiScalerRuntime.EnsureFsrBridgeOptiSidecar(bridgeDirectory, buildDirectory))
                    {
                        _logger.LogInformation(
                            "FSR Bridge OptiScaler sidecar synchronized: {Directory}",
                            Path.Combine(Directory.GetParent(bridgeDirectory)?.FullName ?? bridgeDirectory, "OptiScaler"));
                    }

                    bool bridgeOwnsOpti = BridgeSupportsInProcessOpti(bridgeSpec.Path);
                    if (autoloadTarget is not null && bridgeOwnsOpti)
                    {
                        // v2.2 Bridge consumes autoload.txt.
                        int bridgeIndex = specs.FindIndex(s =>
                            string.Equals(Path.GetFileName(s.Path), OptiScalerRuntime.FsrBridgeDllName,
                                StringComparison.OrdinalIgnoreCase));
                        if (bridgeIndex >= 0)
                        {
                            specs[bridgeIndex] = specs[bridgeIndex] with
                            { BuildDirectory = buildDirectory, GameKey = gameKey };
                        }

                        _logger.LogInformation(
                            "OptiScaler 将由 FSR Bridge 进程内加载：{File} => {Target}; profile={Game}",
                            OptiScalerRuntime.FsrBridgeAutoloadName, autoloadTarget, gameKey);
                    }
                    else if (isGenshin)
                    {
                        // The tested v2.3.1 Bridge does not read autoload.txt.
                        // Its reference fps_config.json injects Bridge first and
                        // OptiScaler second, so reproduce that order here.
                        specs.Add(new InjectDllSpec(optiScaler, "OptiScaler", buildDirectory, gameKey,
                            DelaySeconds: AppConfig.GetOptiScalerInjectDelayEffective(optiGameId.GameBiz)));
                        _logger.LogInformation(
                            "v2.3.1 Bridge 不支持 autoload，改为 Bridge 就绪后单独注入 OptiScaler：{Target}",
                            optiScaler);
                    }
                    else if (!isGenshin)
                    {
                        _logger.LogWarning(
                            "写 FSR Bridge autoload 清单失败，回退外部注入：{Directory}", bridgeDirectory);
                        specs.Add(new InjectDllSpec(optiScaler, "OptiScaler", buildDirectory, gameKey,
                            DelaySeconds: AppConfig.GetOptiScalerInjectDelayEffective(optiGameId.GameBiz),
                            WaitForModule: waitForShadeModule));
                    }
                    else
                    {
                        // 原神不能在 Bridge 失败时偷偷退回外部注入：mhyprot 会拒绝，
                        // 更糟时会把游戏打崩。宁可不注 Opti，也不走不安全路径。
                        _logger.LogError(
                            "FSR Bridge autoload 写入失败，已跳过原神 OptiScaler 外部注入：{Directory}", bridgeDirectory);
                    }
                }
                else if (!isGenshin)
                {
                    // 没桥的其他游戏：照旧外部注入。
                    specs.Add(new InjectDllSpec(optiScaler, "OptiScaler", buildDirectory, gameKey,
                        DelaySeconds: AppConfig.GetOptiScalerInjectDelayEffective(optiGameId.GameBiz),
                        WaitForModule: waitForShadeModule));
                }
                else
                {
                    // 原神 + OptiScaler 必须由 Bridge 进程内加载，不能退回外部注入。
                    _logger.LogError("原神启用了 OptiScaler，但 FSR Bridge 不在注入列表中；已跳过 OptiScaler 注入");
                }
            }
        }

        // Local last-injection trial: XXMI is appended after ordinary graphics injection operations.
        if (UseXxmiInject && CurrentGameId is { } xxmiGameId)
        {
            _logger.LogInformation("XXMI 已启用（{Game}）：模型注入由手动启动的 XXMI 负责，Hub 负责正常图形注入和游戏启动",
                xxmiGameId.GameBiz);
        }

        // 桥的 ini 必须和它**被注入的那个 DLL** 同目录（桥按 DLL 所在目录找 ini）。
        // 模块装的时候会补一份，但手动放的 / 从别的构建目录解析出来的路径不一定有，这里再兜一次。
        foreach (InjectDllSpec spec in specs)
        {
            if (string.Equals(Path.GetFileName(spec.Path), OptiScalerRuntime.FsrBridgeDllName, StringComparison.OrdinalIgnoreCase))
            {
                OptiScalerRuntime.EnsureFsrBridgeIni(Path.GetDirectoryName(spec.Path) ?? string.Empty);
            }
        }

        // 无条件先停掉上一次武装的注入任务：开关全关的启动（空 specs）也必须清掉残留，
        // 否则上一个带 shade 的任务会继续跨启动注入（20 分钟预算 + 3 次重试）
        _extraInjectCts?.Cancel();

        if (specs.Count == 0)
        {
            return;
        }

        _extraInjectCts = new System.Threading.CancellationTokenSource();
        System.Threading.CancellationToken token = _extraInjectCts.Token;

        foreach (InjectDllSpec spec in specs)
        {
            _logger.LogInformation("{Label} injection armed: {Dll} -> {Process}", spec.Label, spec.Path, processName);
        }

        IsWaitProcessMode = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await InjectExtraDllsAsync(processName, specs, token, targetProcessId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Extra DLL injection task");
            }
            finally
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    IsWaitProcessMode = false;

                    // 没勾 shade 时这条常驻提示就是「等进程」用的，注入一结束就收掉
                    if (IsWaitProcessOnlyMode)
                    {
                        CloseInjectorToast();
                    }
                });
            }
        });
    }


    /// <summary>
    /// 等 FSR Bridge 完成初始化：它在 IAT/loader hooks 安装前会写入
    /// <c>Dx11FsrBridge active pid=&lt;pid&gt;</c>。看到本次进程这行后再留 500ms，
    /// 让随后的 loader hooks 装完；后台日志线程按 200ms 批量落盘，轮询间隔 200ms。
    ///
    /// ⚠️ 2026-09-26 修复 baselineLength 陷阱：桥 <c>truncate_on_start=1</c> 每次启动
    /// 截断日志重写，marker 在文件开头。旧逻辑用「注入前文件长度」做 offset，
    /// 当新文件增长超过该长度时会 seek 到新文件**中间**、跳过开头的 marker →
    /// 30 秒超时误报 "not seen"（启动器日志与桥日志 pid 相同时仍说没看到）。
    /// marker 本身含 pid，旧运行的 marker 是旧 pid 不会误匹配本次 → baselineLength
    /// 多余且有害，直接读整个文件找本次 pid 的 marker。
    /// </summary>
    private static async Task<bool> WaitForFsrBridgeReadyAsync(
        string bridgeDirectory,
        int processId,
        TimeSpan timeout,
        System.Threading.CancellationToken cancellationToken)
    {
        string logPath = Path.Combine(bridgeDirectory, "Dx11FsrBridge.log");
        string marker = "Dx11FsrBridge active pid=" + processId.ToString();
        // 兜底就绪线：桥的新版日志器（2026-09-26 重编）可能在启动器采完 baseline **之前**
        // 就初始化完毕 —— pid 精确行落在 baseline 之前会被永远裁掉，30 秒必超时，
        // OptiScaler 于是错过反作弊窗口（VirtualAllocEx 拒绝访问）。下面这几行只在本次
        // 会话真正跑起来之后才会出现在 baseline 之后的内容里，任一出现 = hook 已装好。
        string[] fallbackMarkers = ["draw_indexed_hook_active", "draw_hook_active", "hook_ready mode=feature_query"];
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 游戏在等待期退出（mhyprot 锁定后 OptiScaler 注入会被拒，用户常在此退游戏）
            // → 立即返回 false，让外层换新 pid 重试，不傻等满超时。
            if (!DllInjector.IsProcessAlive(processId))
                return false;

            try
            {
                if (File.Exists(logPath))
                {
                    using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                    {
                        // 读整个文件：marker 含 pid，旧运行的 marker 不会误匹配本次 pid。
                        string tail = reader.ReadToEnd();
                        if (tail.Contains(marker, StringComparison.Ordinal)
                            || fallbackMarkers.Any(m => tail.Contains(m, StringComparison.Ordinal)))
                        {
                            await Task.Delay(500, cancellationToken);
                            return true;
                        }
                    }
                }
            }
            catch (IOException)
            {
                // 文件正被 bridge 切走/重开，下一轮再读
            }

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    /// <summary>
    /// 等游戏进程 → 注入 → <b>确认目标 pid 还活着</b>；中途丢了就等一个新同名进程重注。
    ///
    /// <para>
    /// 为什么需要这一步：有些游戏（星铁、反作弊壳、自身更新）会先起一个壳进程再重启本体 ——
    /// 注入 API 明明成功，但那个 pid 随即退出，DLL 跟着一起没了，用户看到的就是「注入失败」。
    /// 所以每次注入后隔几秒确认目标还在；没了就等一个**新的、没注过**的同名进程重注，
    /// 最多 3 次，共用 20 分钟预算。已经注过的 pid 不会再注第二次。
    /// </para>
    /// </summary>
    private async Task InjectExtraDllsAsync(string processName, IReadOnlyList<InjectDllSpec> specs,
        System.Threading.CancellationToken cancellationToken, int? targetProcessId = null)
    {
        // A process created by Hub's direct early launch cannot be mistaken for
        // a launcher shell that will restart itself. Don't arm another 20-minute
        // wait when that exact game process dies during startup.
        int maxAttempts = targetProcessId.HasValue ? 1 : 3;
        TimeSpan budget = TimeSpan.FromMinutes(20);
        TimeSpan survivalCheck = TimeSpan.FromSeconds(8);
        DateTime deadline = DateTime.UtcNow + budget;

        string firstLabel = specs.Count > 0 ? specs[0].Label : "额外注入";
        var injectedPids = new HashSet<int>();
        var failureNotes = new List<string>();

        try
        {
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    NotifyInjectionFailed(firstLabel, failureNotes,
                        $"没等到进程：{budget.TotalMinutes:F0} 分钟预算内没有出现可注入的 {processName}。");
                    return;
                }

                // ① 等一个还没注过的同名进程
                Process? target = null;
                if (targetProcessId is { } exactPid)
                {
                    try
                    {
                        target = Process.GetProcessById(exactPid);
                        if (target.HasExited) target = null;
                    }
                    catch (ArgumentException) { }
                    if (target is null)
                    {
                        NotifyInjectionFailed(firstLabel, failureNotes,
                            $"启动器创建的游戏进程（pid {exactPid}）已经退出；已停止本次注入，不再等待新进程。");
                        return;
                    }
                }
                else
                    target = await DllInjector.WaitForProcessAsync(processName, remaining, cancellationToken, injectedPids);
                if (target is null)
                {
                    _logger.LogWarning("{Label} injection failed: no {Process} process within budget (attempt {Attempt})",
                        firstLabel, processName, attempt);
                    NotifyInjectionFailed(firstLabel, failureNotes,
                        $"没等到进程：{budget.TotalMinutes:F0} 分钟内没有出现（没注过的）{processName}。");
                    return;
                }

                int pid = target.Id;
                injectedPids.Add(pid);
                _logger.LogInformation("{Label} injection attempt {Attempt}/{Max}: {Process} (pid {Pid})",
                    firstLabel, attempt, maxAttempts, processName, pid);

                // ② 每一项按自己的「注入时机」注（spec.DelaySeconds）
                bool attemptOk = false;
                bool warmupLost = false;
                foreach (InjectDllSpec spec in specs)
                {
                    string name = Path.GetFileName(spec.Path);
                    string label = spec.Label;
                    // 「注入时机」：这一项单独设过就用自己的秒数，没设过用全局预热默认。
                    // 进程在等待期间退了 → 跳出，外层换新 pid 重试（新进程也要重新等）。
                    if (!await WaitForInjectionSteadyAsync(target, pid, processName, label, spec.DelaySeconds, cancellationToken))
                    {
                        warmupLost = true;
                        break;
                    }

                    // 「模块排序」：shade 走 inject.exe 时 OptiScaler 排在它之后注 ——
                    // 等 ReShade64.dll 真正落进进程模块表。两条注入路径并行抢跑会让
                    // hook 顺序随机（OptiScaler 先 hook 上 → NR 吃不到原生 DLSS 数据，
                    // 崩铁卡旧帧案）。超时 / 进程没了记警告照样注，退化为旧行为。
                    // XXMI 场景：等游戏主窗口出现（= 设备创建已跑完、3DMigoto 建链完成）再注 ReShade，
                    // 避开 3DMigoto 设备创建路径与 ReShade 钩子同场的固定偏移崩溃
                    if (spec.WaitForWindow)
                    {
                        var windowDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(120);
                        while (DateTime.UtcNow < windowDeadline
                               && !cancellationToken.IsCancellationRequested)
                        {
                            try
                            {
                                if (target.HasExited) { warmupLost = true; break; }
                                if (target.MainWindowHandle != 0) break;
                            }
                            catch { break; }
                            await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                        }
                        _logger.LogInformation("{Label} injection: game window up (pid {Pid}), injecting", label, pid);
                    }

                    if (!string.IsNullOrWhiteSpace(spec.WaitForModule))
                    {
                        bool moduleSeen = await DllInjector.WaitForModuleAsync(
                            pid, spec.WaitForModule, TimeSpan.FromSeconds(60), cancellationToken);
                        if (moduleSeen)
                        {
                            _logger.LogInformation(
                                "{Label} injection ordered after {Module} (pid {Pid})",
                                label, spec.WaitForModule, pid);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "{Label} injection ordering: {Module} not loaded within 60s (pid {Pid}); injecting anyway",
                                label, spec.WaitForModule, pid);
                        }
                    }

                    bool ok = DllInjector.Inject(pid, spec.Path, out string error);
                    attemptOk |= ok;

                    _logger.LogInformation("{Label} injection {Result} ({Dll} -> pid {Pid}) {Error}",
                        label, ok ? "ok" : "failed", spec.Path, pid, error);

                    if (!ok)
                    {
                        failureNotes.Add($"{name}（pid {pid}）：{error}");
                    }

                    DispatcherQueue?.TryEnqueue(() =>
                    {
                        if (ok)
                        {
                            InAppToast.MainWindow?.Success(label, $"已把 {name} 注入 {processName}（pid {pid}）。", 8000);
                        }
                        else
                        {
                            InAppToast.MainWindow?.Error($"{label} 注入失败", $"{name}：{error}", 12000);
                        }
                    });

                    // OptiScaler 依赖 Bridge 先把 ffxFsr2* 垫片和 D3D11 hook 装好；
                    // 等 Bridge 日志出现本次进程的 active 行后再注入排在后面的 DLL。
                    if (spec.WaitForReady && ok)
                    {
                        bool ready = await WaitForFsrBridgeReadyAsync(
                            Path.GetDirectoryName(spec.Path) ?? string.Empty,
                            pid,
                            TimeSpan.FromSeconds(30),
                            cancellationToken);

                        if (ready)
                        {
                            _logger.LogInformation("FsrBridge ready before next injection (pid {Pid})", pid);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "FsrBridge readiness marker not seen within timeout; continuing next injection (pid {Pid})",
                                pid);
                        }
                    }
                }

                if (warmupLost)
                {
                    _logger.LogWarning(
                        "{Label} target-exited-retry: {Process} (pid {Pid}) 在预热等待中就退了，换一个新进程重试（{Attempt}/{Max}）",
                        firstLabel, processName, pid, attempt, maxAttempts);

                    if (attempt < maxAttempts)
                    {
                        DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Information(firstLabel,
                            $"{processName}（pid {pid}）还没稳就退了 —— 正在等新进程重试（{attempt + 1}/{maxAttempts}）。", 8000));
                        continue;
                    }

                    NotifyInjectionFailed(firstLabel, failureNotes,
                        $"目标进程中途退出：连续 {maxAttempts} 次都在预热阶段就退了。");
                    return;
                }

                // ④ 目标存活确认：壳进程 / 更新重启会让 pid 在几秒内消失
                if (targetProcessId.HasValue)
                    await Task.WhenAny(target.WaitForExitAsync(cancellationToken), Task.Delay(survivalCheck, cancellationToken));
                else
                    await Task.Delay(survivalCheck, cancellationToken);

                if (DllInjector.IsProcessAlive(pid))
                {
                    _logger.LogInformation("{Label} injection finished on stable target (pid {Pid}, ok {Ok})",
                        firstLabel, pid, attemptOk);

                    if (attemptOk)
                    {
                        HookInjectedTarget(target, processName, pid, specs);
                    }
                    else
                    {
                        NotifyInjectionFailed(firstLabel, failureNotes,
                            $"注入失败：目标进程还在（pid {pid}），但 DLL 没注进去 —— 多半是权限（游戏提权 / 反作弊）或 LoadLibraryW 返回 0。");
                    }

                    return;
                }

                if (targetProcessId.HasValue)
                {
                    int? exitCode = null;
                    try { if (target.HasExited) exitCode = target.ExitCode; } catch { }
                    _logger.LogWarning("Early game exited during startup: pid {Pid}, exit code {ExitCode}; no process-name retry", pid, exitCode);
                    NotifyInjectionFailed(firstLabel, failureNotes,
                        $"游戏进程（pid {pid}）在启动阶段退出（退出码 {exitCode?.ToString() ?? "未知"}）；已停止注入等待。这不是 XXMI 等待用户启动。");
                    return;
                }

                // 目标中途退出：记清楚是「进程换了」而不是「注入 API 失败」
                _logger.LogWarning(
                    "{Label} target-exited-retry: {Process} (pid {Pid}) 在注入后 {Seconds}s 内退出，换一个新进程重试（{Attempt}/{Max}）",
                    firstLabel, processName, pid, (int)survivalCheck.TotalSeconds, attempt, maxAttempts);

                // 重试只在日志里记，不弹通知（用户反馈这条提示很吵）
                if (attempt < maxAttempts)
                {
                    continue;
                }

                NotifyInjectionFailed(firstLabel, failureNotes,
                    $"目标进程中途退出：连续 {maxAttempts} 次注入后 {processName} 都换了 / 退了（进程换了，不是注入 API 失败）。");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            // 注入器被停掉了，正常退出
            _logger.LogInformation("{Label} injection wait stopped by user", firstLabel);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Extra DLL injection");
            NotifyInjectionFailed(firstLabel, failureNotes, "注入时出错：" + ex.Message);
        }
    }

    /// <summary>注入成功且目标稳定：让背景释放显存，并挂上「游戏退出」的钩子</summary>
    private void HookInjectedTarget(Process target, string processName, int pid, IReadOnlyList<InjectDllSpec> specs)
    {
        // 游戏起来了：让背景停掉并释放显存（跟 Hub 自己启动游戏时的行为一致）；
        // 游戏退出再发一条，让背景回来 —— 用户报过「壁纸在游戏关闭后没有恢复」
        DispatcherQueue?.TryEnqueue(() => WeakReferenceMessenger.Default.Send(new GameStartedMessage()));

        try
        {
            target.EnableRaisingEvents = true;
            target.Exited += (_, _) =>
            {
                _logger.LogInformation("Injected game exited: {Process} (pid {Pid})", processName, pid);

                // OptiScaler ini 按游戏分离：把叠加层 Save 的主 ini 回写到该游戏 profile
                foreach (InjectDllSpec spec in specs)
                {
                    if (spec.BuildDirectory is not null && spec.GameKey is not null
                        && OptiScalerProfiles.Store(spec.BuildDirectory, spec.GameKey))
                    {
                        _logger.LogInformation("OptiScaler ini profile stored: {Game} ({Build})",
                            spec.GameKey, spec.BuildDirectory);

                        // 用户要求：挂着某份「配置」进游戏改完，那份配置也要跟着动。
                        // 这里把刚回写的 profiles\<游戏>.ini 同步回配置（没挂配置就是 null，啥也不做）
                        string? followed = OptiScalerPresets.Follow(spec.BuildDirectory, spec.GameKey);
                        if (followed is not null)
                        {
                            _logger.LogInformation("OptiScaler config synced back: {Config}", followed);
                        }
                    }
                }

                DispatcherQueue?.TryEnqueue(() => WeakReferenceMessenger.Default.Send(new GameExitedMessage()));
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hook injected game exit");
        }
    }

    /// <summary>最终失败：一条提示写清楚失败在哪一步（没等到进程 / 权限 / LoadLibrary 0 / 目标退出）</summary>
    private void NotifyInjectionFailed(string label, IReadOnlyList<string> notes, string reason)
    {
        string detail = notes.Count == 0 ? string.Empty : "\n" + string.Join("\n", notes);
        DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Error($"{label} 注入失败", reason + detail, 12000));
    }

    /// <summary>预热时窗口最多比「存活阈值」晚出现多少秒；一直取不到窗口就按时间放行</summary>
    private const int InjectionWindowGraceSeconds = 6;

    /// <summary>
    /// 注入前「等进程稳一稳」：等它存活到配置的秒数，**并且主窗口已经出现**；
    /// 窗口一直取不到就只按时间放行。返回 false = 进程在等待期间退了（外层换新 pid 重试）。
    ///
    /// <para>
    /// 为什么需要：星铁 / 反作弊壳在刚起的那几百毫秒还在初始化自己的模块，这时候用
    /// <c>LoadLibraryW</c> 去 hook 游戏自己的 DLL 会偶发把它带崩（用户实测：注入记 ok，
    /// 但 <c>veritas.log</c> 里连本次条目都没有，进程随即消失）。
    /// </para>
    /// </summary>
    /// <param name="delaySeconds">这一项自己的「注入时机」秒数（0 = 立即注入；没单独设过就是全局预热默认）</param>
    private async Task<bool> WaitForInjectionSteadyAsync(
        Process target,
        int processId,
        string processName,
        string label,
        int delaySeconds,
        System.Threading.CancellationToken cancellationToken)
    {
        int thresholdSeconds = Math.Clamp(delaySeconds, 0, AppConfig.MaxInjectionWarmupSeconds);

        if (thresholdSeconds <= 0)
        {
            return DllInjector.IsProcessAlive(processId);
        }

        DateTime aliveSinceUtc = DateTime.UtcNow;
        try
        {
            aliveSinceUtc = target.StartTime.ToUniversalTime();
        }
        catch
        {
            // 读不到启动时间就从「现在」算
        }

        // 「只按时间」的放行点：进程已存活 >= 阈值 + 宽限（窗口一直不出现时用它）
        DateTime timeOnlyDeadlineUtc = aliveSinceUtc + TimeSpan.FromSeconds(thresholdSeconds + InjectionWindowGraceSeconds);
        DateTime nextLogUtc = DateTime.MinValue;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!DllInjector.IsProcessAlive(processId))
            {
                return false;
            }

            DateTime now = DateTime.UtcNow;
            double aliveSeconds = Math.Max(0, (now - aliveSinceUtc).TotalSeconds);
            bool hasWindow = HasMainWindow(target);

            if (now >= nextLogUtc)
            {
                _logger.LogInformation(
                    "{Label} injection waiting for steady state (alive {Seconds}s / window={Window})",
                    label, Math.Round(aliveSeconds, 1), hasWindow);
                nextLogUtc = now.AddSeconds(2);
            }

            if (aliveSeconds >= thresholdSeconds && (hasWindow || now >= timeOnlyDeadlineUtc))
            {
                _logger.LogInformation("{Label} injection steady after {Seconds}s (window={Window}); injecting now",
                    label, Math.Round(aliveSeconds, 1), hasWindow);
                return true;
            }

            await Task.Delay(200, cancellationToken);
        }
    }

    /// <summary>按进程名找现在在跑的那个（找不到返回 null）</summary>
    private static Process? FindRunningProcess(string processName)
    {
        try
        {
            string name = Path.GetFileNameWithoutExtension(processName);
            return string.IsNullOrWhiteSpace(name) ? null : Process.GetProcessesByName(name).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>目标进程主窗口出现了没有（取不到就当没有，调用方按时间兜底）</summary>
    private static bool HasMainWindow(Process process)
    {
        try
        {
            process.Refresh();
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }



    /// <summary>把常驻提示收掉（定时器会把关掉的条目摘掉）</summary>
    private void CloseInjectorToast()
    {
        try
        {
            if (_injectorToast is not null)
            {
                _injectorToast.IsOpen = false;
                _injectorToast = null;
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>停掉正在跑的注入器（再点一次开始游戏、或者点提示条上的「停止」）</summary>
    /// <summary>
    /// 启动帧率解锁：崩溃停用拦截 → 按游戏版本同步上游数据 → 等真实游戏进程 → 扫描注入。
    /// fire-and-forget（游戏启动后异步进行）。
    /// </summary>
    private async Task StartFpsUnlockAsync(TimeSpan processTimeout, TimeSpan moduleTimeout)
    {
        if (CurrentGameId is not { } gameId || !UseFpsUnlock)
        {
            return;
        }

        StopFpsUnlocker();

        byte[]? shellcode;
        (byte[] bytes, bool[] mask)? pattern;

        try
        {
            await EnsureFpsUnlockDataAsync(gameId);
        }
        catch (Exception ex)
        {
            // fire-and-forget 没人接异常：数据同步失败要留痕 + 提示，不能静默不生效
            _logger.LogError(ex, "FPS unlock: sync upstream data failed");
            DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Error("帧率解锁",
                "同步帧率解锁数据失败：" + ex.Message, 8000));
            return;
        }

        try
        {
            shellcode = FpsUnlockDataService.LoadShellcode();
            pattern = FpsUnlockDataService.LoadPattern();
        }
        catch (Exception ex)
        {
            // meta.json 损坏会在这里抛 —— 丢给下面的内置兜底，别让解锁静默失败
            _logger.LogError(ex, "FPS unlock: load shellcode/pattern failed, use fallback");
            shellcode = null;
            pattern = null;
        }

        if (shellcode is null || pattern is null)
        {
            shellcode ??= FpsUnlocker.FallbackShellcode;
            pattern ??= (
                [0x8B, 0x0D, 0x00, 0x00, 0x00, 0x00, 0xEB, 0x00, 0x33, 0xC0],
                [true, true, false, false, false, false, true, false, true, true]);
            _logger.LogWarning("FPS unlock: local data missing, using built-in fallback");
        }

        Process? game;
        try
        {
            game = await _gameLauncherService.GetGameProcessAsync(gameId, processTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FPS unlock: waiting for game process failed");
            return;
        }

        if (game is null)
        {
            _logger.LogWarning("FPS unlock: game process not found within {Timeout}", processTimeout);
            DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Error("帧率解锁",
                $"没在 {Math.Round(processTimeout.TotalSeconds)} 秒内等到游戏进程，这次不解锁了。", 8000));
            return;
        }

        FpsUnlocker unlocker = new(shellcode, pattern.Value);
        bool ok;
        try
        {
            ok = await unlocker.AttachAsync(game, FpsUnlockTarget, moduleTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FPS unlock attach failed");
            ok = false;
        }

        if (ok)
        {
            _fpsUnlocker = unlocker;
            _logger.LogInformation("FPS unlock armed: {Target} fps (pid {Pid})", FpsUnlockTarget, game.Id);
            DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Success("帧率解锁",
                $"已把帧率上限同步为 {FpsUnlockTarget} fps（pid {game.Id}）。", 8000));
            return;
        }

        if (unlocker.SkippedGameExited)
        {
            // 游戏在等主模块 / 附加期间被关了 —— 正常流程，不算失败，不弹错误
            _logger.LogInformation("FPS unlock skipped: game exited before attach (pid {Pid})", game.Id);
            unlocker.Dispose();
            return;
        }

        string detail = string.IsNullOrWhiteSpace(unlocker.LastError)
            ? "特征扫描或注入失败，游戏版本可能已更新。"
            : unlocker.LastError!;
        _logger.LogWarning("FPS unlock failed: {Detail}", detail);
        if (unlocker.PatternNotFound)
        {
            // 特征对不上 = 本地数据大概率滞后于游戏版本；清掉同步戳，下次启动重新拉上游
            AppConfig.SetFpsUnlockDataVersion(gameId, null);
            detail += " 已清除同步记录，下次启动会重新检查上游数据。";
        }

        DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Error("帧率解锁", detail, 10000));
        unlocker.Dispose();
    }

    /// <summary>
    /// 按游戏版本同步上游数据：版本变动 / 本地无数据时拉取；
    /// 否则按 24 小时节流做后台静默检查。
    /// </summary>
    private async Task EnsureFpsUnlockDataAsync(GameId gameId)
    {
        Version? gameVersion = await _gameLauncherService.GetLocalGameVersionAsync(gameId);
        string versionText = gameVersion?.ToString() ?? string.Empty;
        string? syncedVersion = AppConfig.GetFpsUnlockDataVersion(gameId);
        bool needFetch = !FpsUnlockDataService.HasLocalData()
                         || !string.Equals(syncedVersion, versionText, StringComparison.Ordinal);

        if (!needFetch)
        {
            DateTime lastCheck = new(AppConfig.GetFpsUnlockLastCheckTicks(gameId), DateTimeKind.Utc);
            if (DateTime.UtcNow - lastCheck < TimeSpan.FromHours(24))
            {
                return;
            }
        }

        AppConfig.SetFpsUnlockLastCheckTicks(gameId, DateTime.UtcNow.Ticks);

        FpsUnlockDataService.UpdateResult result;
        try
        {
            result = await FpsUnlockDataService.UpdateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FPS unlock data update failed");
            return;
        }

        _logger.LogInformation("FPS unlock data update: {Result} (game {Version})", result, versionText);

        if (result is FpsUnlockDataService.UpdateResult.Updated)
        {
            AppConfig.SetFpsUnlockDataVersion(gameId, versionText);
        }
        else if (!FpsUnlockDataService.HasLocalData())
        {
            DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Warning("帧率解锁",
                "没拉取到帧率解锁数据，这次使用内置兜底数据；可到设置页手动检查更新。", 8000));
        }
    }

    private static void StopFpsUnlocker()
    {
        _fpsUnlocker?.Dispose();
        _fpsUnlocker = null;
    }

    private void StopInjector(string reason)
    {
        Process? process = _injectorProcess;
        _injectorProcess = null;

        try
        {
            _extraInjectCts?.Cancel();
            _extraInjectCts = null;
            StopFpsUnlocker();

            if (process is not null && !process.HasExited)
            {
                _logger.LogInformation("Stopping previous injector (pid {Pid}): {Reason}", process.Id, reason);
                process.Kill(entireProcessTree: true);
                InAppToast.MainWindow?.Information("注入模式", "已停止上一个注入器。", 4000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stop injector");
        }
        finally
        {
            process?.Dispose();
            CloseInjectorToast();
        }
    }



    /// <summary>
    /// 「启动游戏时强制 off」（用户要求）：启动/注入之前把 hook 点写成 0，
    /// 免得上次调好之后忘了改回来、游戏起不来。
    /// </summary>
    private void ApplyForceHookOffOnLaunch()
    {
        try
        {
            if (CurrentGameId is not { } gameId || _currentGameEntry is null
                || !AppConfig.GetForceHookOffOnLaunch(gameId.GameBiz))
            {
                return;
            }

            var service = GamePluginServiceFactory.Create(
                _currentGameEntry,
                PluginHostLocator.Resolve(out _));

            if (service.HasReShadeIni && service.SetHookPoint(0))
            {
                _logger.LogInformation("Force hook point off before launch ({Game})", gameId.GameBiz);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Force hook point off before launch");
        }
    }



    [RelayCommand]
    /// <summary>
    /// 启动前：把游戏目录自带的 <c>nvngx_dlssg.dll</c> 换成 310.9（OptiScaler 的 MFG 解锁只认这个版本）。
    ///
    /// <para>
    /// OptiScaler 从**游戏 exe 目录**开始广度优先找 dlssg，游戏自带那份（典型 310.6.0）永远先被命中 ——
    /// 光把 310.9 放进 OptiScaler 构建目录没用，多帧生成不生效。这里做版本检测 + 替换：
    /// 原文件先备份（<c>.hysx\game-dll-backup</c>），失败会把路径和原因弹出来。
    /// </para>
    ///
    /// <para>
    /// 必须放在 <see cref="StartGameAsync"/> 这个统一入口：注入模式 / 非注入模式 / Starward / 自定义游戏
    /// 各条路径都会走到（以前只在注入流程里做，走别的路径的游戏比如绝区零就漏了）。
    /// </para>
    /// </summary>
    private void EnsureGameDlssgForMfg()
    {
        if (!UseOptiScaler || CurrentGameId is not { } gameId)
        {
            return;
        }

        string? optiDll = AppConfig.GetSelectedOptiScalerDll(gameId);

        if (string.IsNullOrWhiteSpace(optiDll) || Path.GetDirectoryName(optiDll) is not { Length: > 0 } buildDirectory)
        {
            _logger.LogInformation("Game dlssg: 这个游戏还没选 OptiScaler 构建，跳过。");
            return;
        }

        string? unlockDll = OptiScalerRuntime.FindUnlockDlssg(buildDirectory);

        if (unlockDll is null)
        {
            _logger.LogInformation("Game dlssg: 构建目录里没有 310.9 的 dlssg，跳过（{Build}）", buildDirectory);
            return;
        }

        // 游戏目录：优先用当前条目的 exe；biz 游戏（绝区零这种）没有条目路径时用安装路径兜底
        string? gameDirectory = _currentGameEntry?.ExePath is { Length: > 0 } exe
            ? Path.GetDirectoryName(exe)
            : GameInstallPath;

        if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
        {
            _logger.LogInformation("Game dlssg: 还不知道游戏目录，跳过。");
            return;
        }

        string backupRoot = string.IsNullOrWhiteSpace(AppConfig.UserDataFolder)
            ? Path.Combine(TemporaryFolder.Path, "HoYoShadeHub-game-dll-backup")
            : Path.Combine(AppConfig.UserDataFolder, ".hysx", "game-dll-backup");

        OptiScalerRuntime.GameDlssgSwapResult swap = OptiScalerRuntime.ReplaceGameDlssg(gameDirectory, unlockDll, backupRoot);

        if (swap.Replaced > 0)
        {
            _logger.LogInformation("Game dlssg replaced: {Message}", swap.Message);
            DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Success("DLSSG 已替换", swap.Message, 8000));
        }
        else if (!swap.Ok)
        {
            _logger.LogWarning("Game dlssg replace failed: {Message}", swap.Message);
            DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Error("DLSSG 替换失败", swap.Message, 15000));
        }
        else
        {
            _logger.LogInformation("Game dlssg check: {Message}", swap.Message);
        }
    }

    /// <summary>
    /// 启动器自身目录（正在跑的这个 exe 所在目录）是不是落在游戏安装目录里面。
    /// 游戏目录会被反作弊扫盘，启动器这一堆 DLL 不能待在里面。
    /// </summary>
    private static bool LauncherInsideGameDirectory(string? gameInstallPath)
    {
        if (string.IsNullOrWhiteSpace(gameInstallPath) || !Directory.Exists(gameInstallPath))
        {
            return false;
        }

        string launcher = Path.GetFullPath(AppContext.BaseDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string game = Path.GetFullPath(gameInstallPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return launcher.Equals(game, StringComparison.OrdinalIgnoreCase)
               || launcher.StartsWith(game + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 覆盖包 auto.json 里标了 <c>runOnLaunch</c> 的自定义动作：启动 / 注入前自动执行（不弹窗，结果进日志）。
    /// 失败不挡启动 —— 动作是增强，不是门槛。
    /// </summary>
    private async Task RunPackAutoActionsOnLaunchAsync()
    {
        try
        {
            if (CurrentGameId is not { } gameId)
            {
                return;
            }

            string packRoot = GameAddonPack.PackDirectory(AppConfig.CacheRoot, gameId.GameBiz.Value);

            // 没同意过（或 auto.json 内容变了还没重新确认）→ 一律不跑；已同意但被用户单独禁用的动作也跳过
            string consentHash = PackActionConsent.ComputeHash(packRoot);
            if (!PackActionConsent.IsConsented(gameId.GameBiz.Value, consentHash))
            {
                return;
            }

            HashSet<string> disabled = PackActionConsent.DisabledActions(gameId.GameBiz.Value, consentHash);
            List<PackAutoAction> actions = [.. PackAutoActionFile.Load(packRoot)
                .Where(a => a.RunOnLaunch && !disabled.Contains(a.Name))];
            if (actions.Count == 0)
            {
                return;
            }

            ShadeHost? host = PluginHostLocator.Resolve(out _);
            GamePluginService? plugins = null;
            if (_currentGameEntry is not null)
            {
                try
                {
                    plugins = GamePluginServiceFactory.Create(_currentGameEntry, host);
                }
                catch
                {
                    // 读不出插件状态就只跑不依赖 ini 的步骤
                }
            }

            ILogger logger = AppConfig.GetLogger<GameLauncherPage>();
            var context = new LauncherActionContext
            {
                GameId = CurrentGameId,
                GameBiz = CurrentGameBiz,
                Entry = _currentGameEntry,
                Host = host,
                Plugins = plugins,
                LauncherPage = this,
                PackRoot = packRoot,
                XamlRoot = XamlRoot,
                Interactive = false,
                Report = text => logger.LogInformation("{Text}", text),
            };

            foreach (PackAutoAction action in actions)
            {
                string summary = await LauncherActionRunner.RunAsync(action, context);
                logger.LogInformation("runOnLaunch「{Name}」：{Summary}", action.Name, summary);
            }
        }
        catch (Exception ex)
        {
            AppConfig.GetLogger<GameLauncherPage>().LogWarning(ex, "Run pack auto actions on launch");
        }
    }

    /// <summary>Observe optional model injection without holding up the graphics launch pipeline.</summary>
    private async Task ObserveXxmiInjectionAsync(int pid, GameId gameId, string? displayName)
    {
        try
        {
            string? graphicsDll = XxmiInjector.FindLoader(gameId, displayName);
            if (string.IsNullOrWhiteSpace(graphicsDll))
            {
                _logger.LogWarning("XXMI 模型代理路径缺失；游戏图形组件已按正常流程处理");
                return;
            }
            bool loaded = await DllInjector.WaitForModuleAsync(pid, graphicsDll,
                TimeSpan.FromSeconds(15), System.Threading.CancellationToken.None);
            if (loaded)
                _logger.LogInformation("XXMI 模型代理加载观察成功：pid {Pid}, path {Dll}；不等于模型效果已验证", pid, graphicsDll);
            else if (DllInjector.IsProcessAlive(pid))
            {
                _logger.LogWarning("XXMI 模型代理未确认：pid {Pid}；未取消其他图形组件加载", pid);
                DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Warning("XXMI 模型注入未确认",
                    "游戏已启动，但未检测到 GIMI 模型代理。请检查 XXMI 日志；OptiScaler/ReShade 的加载未因此取消。", 10000));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Observe optional XXMI injection; normal graphics pipeline unaffected");
        }
    }

    private async Task StartGameAsync()
    {
        try
        {
            // 多人/反作弊游戏在跑：注入链一律暂停，避免封号（关闭该游戏后自动恢复）
            if (MultiplayerGameGuard.IsActive)
            {
                _logger.LogWarning("{Reason}", MultiplayerGameGuard.BlockReason());
                DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Error(
                    "已暂停启动",
                    MultiplayerGameGuard.BlockReason(),
                    12000));
                return;
            }

            // 启动器自身装在游戏目录里：反作弊会扫游戏盘，启动器这批 DLL 会被扫到 —— 直接拒绝启动
            if (LauncherInsideGameDirectory(GameInstallPath))
            {
                DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Error(
                    "启动器位置不对",
                    "启动器不可安装在游戏目录内，会被反作弊扫盘，请移出启动器",
                    12000));
                return;
            }

            // 非注入模式：游戏进程已经在跑就阻止再启动（用户要求），避免双开 / 注入到旧实例
            if (!UseInjectMode && await IsTargetGameRunningAsync())
            {
                string? processName = await ResolveTargetProcessNameAsync();
                string name = string.IsNullOrWhiteSpace(processName) ? "游戏" : processName;
                DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Warning(
                    "游戏已经在运行",
                    $"检测到 {name} 的进程还在，先退出它再启动。",
                    8000));
                return;
            }

            // 缺 DLSS5 的运行时（nvngx_dlssnr.dll）就先弹窗问一下：装了也白装，插件根本加载不了
            if (!await ConfirmDlssRuntimeAsync())
            {
                return;
            }

            // Apply imported module/build selections before checking dependencies.
            await RunPackAutoActionsOnLaunchAsync();

            // OptiScaler + 原神：先确保 FSR Bridge 模块存在并加入本游戏，Bridge 会再按当前 Opti 构建写 autoload。
            if (!await ConfirmGenshinFsrBridgeForOptiAsync())
            {
                return;
            }

            // 启用 OptiScaler：驱动里把 MFG 数量钉死了会盖住 MFG 解锁，先问一下要不要改成 N/A
            if (!await ConfirmNvMfgCountAsync())
            {
                return;
            }

            // 「启动游戏时强制 off」：启动/注入之前把 hook 点写 0（按游戏开关）
            ApplyForceHookOffOnLaunch();


            // 游戏目录自带的 nvngx_dlssg.dll 换成 310.9（MFG 解锁只认它）。
            // **放统一入口**：以前挂在注入流程里，走别的启动路径的游戏（绝区零）就漏了。
            EnsureGameDlssgForMfg();

            // Local recovery baseline: official XXMI owns game startup, as in
            // the coexistence run identified by the user. Hub arms its injectors first.
            if (UseXxmiInject && !UseInjectMode && CurrentGameId is { } officialGame)
            {
                string? closeError = await XxmiInjector.CloseExistingLaunchersAsync(System.Threading.CancellationToken.None);
                if (closeError is not null)
                {
                    _logger.LogWarning("XXMI 旧实例未退出；不写新配置或启动另一实例：{Error}", closeError);
                    InAppToast.MainWindow?.Error("XXMI 旧实例关闭失败", closeError, 10000);
                    return;
                }
                string exeName = await _gameLauncherService.GetGameExeNameAsync(officialGame);
                string exe = Path.Combine(GameInstallPath, exeName);
                if (!File.Exists(exe)) throw new FileNotFoundException("游戏主程序不存在", exe);
                string configError = XxmiInjector.PrepareManualMode(officialGame, _currentGameEntry?.DisplayName, exeName, manual: AppConfig.GetXxmiLaunchMode(officialGame) == XxmiLaunchMode.Manual);
                if (!string.IsNullOrWhiteSpace(configError))
                {
                    InAppToast.MainWindow?.Error("XXMI 配置失败", configError, 10000);
                    return;
                }
                bool manualXxmi = AppConfig.GetXxmiLaunchMode(officialGame) == XxmiLaunchMode.Manual;
                if (manualXxmi)
                {
                    var armed = await XxmiInjector.ArmForManualLaunchAsync(officialGame,
                        _currentGameEntry?.DisplayName, exe, System.Threading.CancellationToken.None);
                    AppConfig.XxmiLastLaunch = armed.Message;
                    if (!armed.Armed)
                    {
                        InAppToast.MainWindow?.Error("XXMI 手动启动失败", armed.Message, 10000);
                        return;
                    }
                }
                string? processName = await ResolveTargetProcessNameAsync();
                if (string.IsNullOrWhiteSpace(processName)) throw new InvalidOperationException("未确定游戏进程名");
                if (UseHoYoShade || UseOpenHoYoShade)
                {
                    string shadeName = UseHoYoShade ? "HoYoShade" : "OpenHoYoShade";
                    string shadePath = Path.Combine(AppConfig.UserDataFolder, shadeName);
                    var ready = await InjectorHelper.StartAndWaitForReadyAsync(
                        Path.Combine(shadePath, "inject.exe"), exeName, shadePath, _logger, shadeName);
                    if (!ready.success)
                    {
                        InAppToast.MainWindow?.Error("HoYoShade 注入器准备失败", $"退出码 {ready.exitCode}", 10000);
                        return;
                    }
                }
                // This prepares profiles, runtime paths and Bridge autoload before
                // the official launcher starts its game, never a second Hub process.
                StartExtraDllInjection(processName);
                Process? target;
                if (manualXxmi)
                {
                    _logger.LogInformation("XXMI 模式=手动：一秒交接与 Hub 注入器准备完成，游戏由 Hub 创建");
                    target = await _gameLauncherService.StartGameAsync(officialGame, GameInstallPath);
                }
                else
                {
                    var launch = XxmiInjector.LaunchOfficialBaseline(officialGame, _currentGameEntry?.DisplayName, exe);
                    AppConfig.XxmiLastLaunch = launch.Message;
                    if (!launch.Started)
                    {
                        _extraInjectCts?.Cancel();
                        InAppToast.MainWindow?.Error("XXMI 官方启动", launch.Message, 10000);
                        return;
                    }
                    _logger.LogInformation("XXMI 模式=官方：Hub 图形注入器已准备，游戏只由 XXMI 创建");
                    target = await DllInjector.WaitForProcessAsync(processName, TimeSpan.FromSeconds(15),
                        System.Threading.CancellationToken.None);
                }
                if (target is not null)
                {
                    GameProcess = target; GameState = GameState.GameIsRunning;
                    WeakReferenceMessenger.Default.Send(new GameStartedMessage());
                    _logger.LogInformation("XXMI 官方启动检测到游戏：pid {Pid}；模型和 FG 状态仍待日志确认", target.Id);
                    if (UseFpsUnlock) _ = StartFpsUnlockAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
                }
                else
                {
                    _extraInjectCts?.Cancel();
                    InAppToast.MainWindow?.Warning("XXMI 启动未检测到游戏", "15秒内没有检测到原神进程；已停止本次 Hub 额外注入。请检查 XXMI 日志。", 10000);
                }
                return;
            }

            // Keep early graphics loading for Genshin without XXMI. Manual XXMI
            // shares the ordinary injector-ready -> game-start path; don't race
            // its GIMI hook with a separate immediate batch LoadLibrary branch.
            if (GenshinLaunchRouting.UseEarlyGraphicsLaunch(UseInjectMode, UseStarwardLauncher, UseXxmiInject)
                && CurrentGameId is { } earlyGenshin
                && ModuleRegistry.IsGenshin(earlyGenshin)
                && UseOptiScaler
                && !string.IsNullOrWhiteSpace(GameInstallPath))
            {
                string? earlyProcessName = await _gameLauncherService.GetGameExeNameAsync(earlyGenshin);
                string earlyExe = Path.Combine(GameInstallPath!, earlyProcessName);
                string? bridgePath = ModuleRegistry.ResolveInjectionDlls(earlyGenshin)
                    .FirstOrDefault(x => string.Equals(Path.GetFileName(x.DllPath),
                        OptiScalerRuntime.FsrBridgeDllName, StringComparison.OrdinalIgnoreCase)).DllPath;
                string? optiPath = EnsureNamedOptiScalerDll(
                    AppConfig.GetSelectedOptiScalerDll(earlyGenshin),
                    AppConfig.GetOptiScalerDllName(earlyGenshin));

                if (bridgePath is not null && File.Exists(bridgePath)
                    && !string.IsNullOrWhiteSpace(optiPath) && File.Exists(optiPath))
                {
                    string? buildDirectory = Path.GetDirectoryName(optiPath);
                    if (buildDirectory is null || !OptiScalerRuntime.PrepareGenshinEarlyConfiguration(
                            buildDirectory, earlyGenshin.GameBiz.Value))
                    {
                        _logger.LogError("原神早期启动配置准备失败；尚未创建游戏进程 ({Build})", buildDirectory);
                        InAppToast.MainWindow?.Error("原神启动配置", "无法准备 OptiScaler 游戏配置，请检查包内配置和目录权限。", 10000);
                        return;
                    }
                    string bridgeDirectory = Path.GetDirectoryName(bridgePath)!;
                    OptiScalerRuntime.EnsureFsrBridgeIni(bridgeDirectory);
                    OptiScalerRuntime.EnsureFsrBridgeRenderScale(bridgeDirectory, 0.6f);
                    OptiScalerRuntime.WriteFsrBridgeAutoload(bridgeDirectory, optiPath);
                    _logger.LogInformation("原神早期配置已准备：profile={Game}，OptiDllPath={Runtime}；随后才创建进程并注入",
                        earlyGenshin.GameBiz.Value, Path.Combine(buildDirectory, "OptiScaler"));
                    _logger.LogInformation("原神：Bridge/OptiScaler 早期加载，ReShade 后置注入");

                    string? ffx12Path = Path.GetFullPath(Path.Combine(
                        Path.GetDirectoryName(bridgePath) ?? string.Empty, "..", "AMD",
                        "amd_fidelityfx_upscaler_dx12.dll"));
                    if (!File.Exists(ffx12Path))
                    {
                        ffx12Path = null;
                    }

                    string earlyArguments = AppConfig.GetStartArgument(earlyGenshin.GameBiz)?.Trim() ?? string.Empty;
                    if (AppConfig.GetUsePopupWindow(earlyGenshin.GameBiz))
                        earlyArguments += " -popupwindow";
                    if (AppConfig.GetEnableDX12(earlyGenshin.GameBiz))
                        earlyArguments += " -use-d3d12";
                    _logger.LogInformation("原神早期启动使用用户参数：{Arguments}", earlyArguments.Trim());
                    GenshinEarlyLaunch.Result early = await GenshinEarlyLaunch.StartAsync(
                        earlyExe, earlyArguments.Trim(), GameInstallPath!, bridgePath, ffx12Path, optiPath,
                        text => _logger.LogInformation("{Text}", text));
                    if (early.Process is null)
                    {
                        _logger.LogError("原神早期启动失败：{Error}", early.Error);
                        InAppToast.MainWindow?.Error("原神早期启动失败", early.Error ?? "未知错误", 12000);
                        return;
                    }

                    GameState = GameState.GameIsRunning;
                    GameProcess = early.Process;
                    WeakReferenceMessenger.Default.Send(new GameStartedMessage());
                    if (UseFpsUnlock)
                    {
                        _ = StartFpsUnlockAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
                    }

                    // OptiScaler/Bridge are already in the process. Load ReShade
                    // afterwards in the same normal launch, matching the user's
                    // previously working injection-mode test without re-injecting
                    // Bridge or OptiScaler.
                    if (UseHoYoShade || UseOpenHoYoShade)
                    {
                        string shadeName = UseHoYoShade ? "HoYoShade" : "OpenHoYoShade";
                        string shadeDll = Path.Combine(AppConfig.UserDataFolder, shadeName, "ReShade64.dll");
                        if (File.Exists(shadeDll))
                        {
                            int shadeDelay = 0;
                            _logger.LogInformation(
                                "原神早期 Bridge/OptiScaler 已稳定，安排正常流程后置注入 {ShadeName}（delay={Delay}s）",
                                shadeName, shadeDelay);
                            StartExtraDllInjection(earlyProcessName, shadeDll, shadeName,
                                shadeDelaySeconds: shadeDelay, includeGameExtras: false, targetProcessId: early.Process.Id);
                        }
                    }
                    if (UseXxmiInject)
                    {
                        _logger.LogInformation("XXMI 为可选模型注入；其状态不阻止 Bridge/OptiScaler/ReShade 正常加载");
                        _ = ObserveXxmiInjectionAsync(early.Process.Id, earlyGenshin, _currentGameEntry?.DisplayName);
                    }
                    return;
                }
            }

            if (UseXxmiInject)
                _logger.LogInformation("XXMI 手动交接完成：使用正常 Hub 注入器准备及游戏启动路径；跳过原神立即批量图形加载分支");

            // 「额外注入 DLL」不依赖注入模式：普通模式下 Hub 自己起游戏，进程一出现就注（用户要求）
            if (!UseInjectMode)
            {
                string? extraProcess = await ResolveTargetProcessNameAsync();
                if (!string.IsNullOrWhiteSpace(extraProcess))
                {
                    StartExtraDllInjection(extraProcess);
                }
            }

            bool launchingBlenderPlugin = LaunchGenshinBlenderPlugin || LaunchZZZBlenderPlugin;
            bool useShader = UseHoYoShade || UseOpenHoYoShade;
            bool useStarward = UseStarwardLauncher;

            // Check if Blender plugin injection process is already running
            string? runningInjectionProcess = CheckBlenderPluginInjectionProcessRunning();
            if (runningInjectionProcess != null && launchingBlenderPlugin)
            {
                // Blender plugin injection process is already running
                if (useShader)
                {
                    // Both Blender plugin and shader selected - block both
                    _logger.LogWarning("Blender plugin injection process ({ProcessName}) is already running. Blocking launch of both Blender plugin and HoYoShade/OpenHoYoShade.", runningInjectionProcess);
                    InAppToast.MainWindow?.Warning(null, string.Format(Lang.GameLauncher_BlenderPluginInjectionProcessAlreadyRunningWithShader, runningInjectionProcess), 5000);
                }
                else
                {
                    // Only Blender plugin selected - block it
                    _logger.LogWarning("Blender plugin injection process ({ProcessName}) is already running. Blocking launch of Blender plugin.", runningInjectionProcess);
                    InAppToast.MainWindow?.Warning(null, string.Format(Lang.GameLauncher_BlenderPluginInjectionProcessAlreadyRunning, runningInjectionProcess), 5000);
                }
                return;
            }

            // Case -1: 自定义游戏（Hub 不认识它）
            // 不走 HoYoPlay 那套：直接起 exe；勾了注入模式就只架注入器，游戏交给它自己的启动器。
            if (_currentGameEntry is { IsCustom: true })
            {
                if (UseInjectMode)
                {
                    await StartGameWithInjectModeAsync();
                }
                else
                {
                    await StartCustomGameAsync();
                }
                return;
            }

            // Case 0: 注入模式（这个游戏单独勾的）
            // 对齐 HoYoShade 启动器 bat 的做法：先补 ReShade.ini，再起 inject.exe 等进程，最后照常启动游戏。
            // 和 Case 5 的区别：不依赖 Hub 的游戏数据库，自定义游戏 / 装在别处也能用；
            // 而且会先跑一次 INIBuild 把模板 ReShade.ini 补上（bat 里那一步）。
            if (UseInjectMode && !launchingBlenderPlugin && !useStarward)
            {
                await StartGameWithInjectModeAsync();
                return;
            }

            // Case 1: Both Blender plugin and shader are selected
            if (launchingBlenderPlugin && useShader)
            {
                string shadePath = "";
                string shadeName = "";
                
                if (UseHoYoShade)
                {
                    shadePath = Path.Combine(AppConfig.UserDataFolder, "HoYoShade");
                    shadeName = "HoYoShade";
                }
                else if (UseOpenHoYoShade)
                {
                    shadePath = Path.Combine(AppConfig.UserDataFolder, "OpenHoYoShade");
                    shadeName = "OpenHoYoShade";
                }

                if (!string.IsNullOrEmpty(shadePath))
                {
                    if (!Directory.Exists(shadePath))
                    {
                        _logger.LogWarning("{ShadeName} directory not found at {Path}", shadeName, shadePath);
                        InAppToast.MainWindow?.Error($"{shadeName} not installed");
                        return;
                    }

                    string injectExePath = Path.Combine(shadePath, "inject.exe");
                    if (!File.Exists(injectExePath))
                    {
                        _logger.LogWarning("inject.exe not found in {ShadeName} at {Path}", shadeName, injectExePath);
                        ToastInjectExeMissing();
                        return;
                    }

                    var gameExeName = await _gameLauncherService.GetGameExeNameAsync(CurrentGameId);

                    // Start injector and wait for ready
                    _logger.LogInformation("Starting {ShadeName} injector and waiting for ready signal...", shadeName);

                    var (success, exitCode, injectorProcess) = await InjectorHelper.StartAndWaitForReadyAsync(
                        injectExePath, gameExeName, shadePath, _logger, shadeName);

                    if (!success)
                    {
                        if (InjectorErrorCodes.IsInjectorError(exitCode))
                        {
                            string errorMessage = InjectorHelper.GetErrorMessage(exitCode, shadeName);
                            _logger.LogError("{ShadeName} validation failed (exit code: {ExitCode}), Blender plugin launch CANCELLED", 
                                shadeName, exitCode);
                            InAppToast.MainWindow?.Error(errorMessage, null, 8000);
                        }
                        else
                        {
                            _logger.LogError("{ShadeName} injector failed to start or timed out", shadeName);
                            InAppToast.MainWindow?.Error($"Failed to start {shadeName} injector");
                        }
                        return;
                    }

                    _logger.LogInformation("{ShadeName} injector is ready", shadeName);
                    InAppToast.MainWindow?.Success($"{shadeName} injector started");
                }

                // Launch Blender plugin (which will start the game)
                _logger.LogInformation("Launching Blender plugin...");
                
                Process? blenderPluginProcess = null;

                if (LaunchGenshinBlenderPlugin)
                {
                    blenderPluginProcess = await LaunchGenshinBlenderPluginAsync();
                }
                else if (LaunchZZZBlenderPlugin)
                {
                    blenderPluginProcess = await LaunchZZZBlenderPluginAsync();
                }

                _logger.LogInformation("Blender plugin launched");

                return;
            }

            // Case 2: Use Starward launcher (with optional shader)
            if (useStarward)
            {
                await LaunchGameViaStarwardAsync(useShader);
                return;
            }

            // Case 3: Only Blender plugin
            if (launchingBlenderPlugin)
            {
                if (LaunchGenshinBlenderPlugin)
                {
                    await LaunchGenshinBlenderPluginAsync();
                }

                if (LaunchZZZBlenderPlugin)
                {
                    await LaunchZZZBlenderPluginAsync();
                }
                return;
            }

            // Case 4: Shader-only launch (injector only, no game launch)
            if (useShader && !EnableGameLaunch)
            {
                bool started = false;

                if (UseHoYoShade)
                {
                    string hoYoShadePath = Path.Combine(AppConfig.UserDataFolder, "HoYoShade");
                    started = await LaunchShaderInjectorOnlyAsync(hoYoShadePath, "HoYoShade");
                }
                else if (UseOpenHoYoShade)
                {
                    string openHoYoShadePath = Path.Combine(AppConfig.UserDataFolder, "OpenHoYoShade");
                    started = await LaunchShaderInjectorOnlyAsync(openHoYoShadePath, "OpenHoYoShade");
                }

                if (started)
                {
                    InAppToast.MainWindow?.Warning(null, Lang.GameLauncher_ShaderOnlyLaunchNotice, 5000);
                }

                return;
            }

            // Case 5: Normal game launch
            Process? process = null;

            if (UseHoYoShade)
            {
                string hoYoShadePath = Path.Combine(AppConfig.UserDataFolder, "HoYoShade");
                process = await LaunchGameWithShadeAsync(hoYoShadePath, "HoYoShade");
            }
            else if (UseOpenHoYoShade)
            {
                string openHoYoShadePath = Path.Combine(AppConfig.UserDataFolder, "OpenHoYoShade");
                process = await LaunchGameWithShadeAsync(openHoYoShadePath, "OpenHoYoShade");
            }
            else
            {
                process = await _gameLauncherService.StartGameAsync(CurrentGameId);
            }

            if (process is not null)
            {
                GameState = GameState.GameIsRunning;
                GameProcess = process;
                WeakReferenceMessenger.Default.Send(new GameStartedMessage());

                if (UseFpsUnlock)
                {
                    _ = StartFpsUnlockAsync(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
                }
            }
        }
        catch (FileNotFoundException)
        {
            CheckGameVersion();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start game");
        }
    }

    /// <summary>
    /// 注入模式：把 HoYoShade 启动器 bat 里「额外步骤 + inject.exe」那两件事搬进来。
    ///
    /// <list type="number">
    /// <item>这个游戏没有 ReShade.ini → 先跑一次 <c>LauncherResource\INIBuild.exe</c> 补模板
    /// （inject.exe 注入时会把模板复制到游戏目录）；</item>
    /// <item>起 <c>inject.exe &lt;进程名&gt;</c> 并等它的 <c>HOYOSHADE_READY:9999</c> 标记；</item>
    /// <item>结束 —— <b>不替用户启动游戏</b>。</item>
    /// </list>
    ///
    /// 第 3 步刻意不做：HoYoShade 启动器 bat 里写得很直白 ——
    /// 「你必须要使用一个游戏启动器来启动游戏（无论是官方启动器还是第三方启动器），
    /// 不能直接双击运行进程/进程快捷方式以启动游戏，否则会注入失败。」
    /// 所以注入模式下 Hub 只把注入器架好，游戏由用户自己的启动器（HoYoPlay / WeGame 等）拉起。
    ///
    /// <b>已知限制</b>：bat 会提权，我们不做 —— 游戏以管理员启动时注入会失败。
    /// </summary>
    private async Task StartGameWithInjectModeAsync()
    {
        try
        {
            // HoYoShade 会在启动游戏时把插件重新部署回原版 —— 汉化过的在这儿再打一遍，
            // 不然进游戏就是半中半英 / 全英文（用户实测）。
            string? i18nNote = await Features.Plugins.AddonLocalizationJob.ReapplyAsync();

            if (i18nNote is not null)
            {
                _logger.LogInformation("Addon i18n before launch: {Note}", i18nNote);
                // 也弹一条：用户要能看见「启动前确实把中文补回去了（包 / 共享目录副本都补了）」，
                // 不然进了游戏还是英文根本不知道卡在哪一步。
                InAppToast.MainWindow?.Success("插件汉化", i18nNote + "。", 6000);
            }

            // 一个 HoYoShade 都没勾：**不要**默认注 HoYoShade（用户报过「没勾也被注入了 HoYoShade」）。
            // 这时注入模式只干一件事：等游戏进程出现，把「额外注入 DLL」/ OptiScaler 注进去。
            if (!UseHoYoShade && !UseOpenHoYoShade)
            {
                string? onlyExtraProcess = await ResolveTargetProcessNameAsync();

                if (string.IsNullOrWhiteSpace(onlyExtraProcess))
                {
                    _logger.LogWarning("Inject mode without shade: unknown process name for {Game}", CurrentGameId?.GameBiz);
                    InAppToast.MainWindow?.Error("注入模式", "不知道这个游戏的进程名 —— 先到「插件」页用「指定主程序…」选一下游戏 exe。", 8000);
                    return;
                }

                StopInjector("要重新注入");
                _logger.LogInformation("Inject mode without HoYoShade/OpenHoYoShade: only extra DLL / OptiScaler for {Process}", onlyExtraProcess);
                StartExtraDllInjection(onlyExtraProcess);

                if (UseFpsUnlock)
                {
                    _ = StartFpsUnlockAsync(TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(60));
                }

                // 常驻提示 + 红色「停止」（用户要求：和「已启动 HoYoShade 注入器」那条一致）
                ShowWaitProcessToast(onlyExtraProcess);
                return;
            }

            bool useOpen = UseOpenHoYoShade && !UseHoYoShade;
            string shadeName = useOpen ? "OpenHoYoShade" : "HoYoShade";
            string shadePath = Path.Combine(AppConfig.UserDataFolder, shadeName);

            if (!Directory.Exists(shadePath))
            {
                _logger.LogWarning("{ShadeName} directory not found at {Path}", shadeName, shadePath);
                InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_ShaderNotInstalled, shadeName));
                return;
            }

            string injectExePath = Path.Combine(shadePath, "inject.exe");
            if (!File.Exists(injectExePath))
            {
                _logger.LogWarning("inject.exe not found at {Path}", injectExePath);
                InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_InjectExeNotFound, shadeName));
                return;
            }

            // 1. 该游戏没有 ReShade.ini → 先让 INIBuild 生成模板（就是 bat 里的第一步）
            ShadeHost? host = ShadeHostLocator.FromShadeRoot(shadePath);
            if (host is not null && _currentGameEntry?.ReShadeIniPath is { } gameIni && !File.Exists(gameIni))
            {
                IniBuildResult? build = await ReShadeIniBuilder.EnsureAsync(host, gameIni);

                if (build is null)
                {
                    _logger.LogInformation("ReShade.ini already present for {Game}", _currentGameEntry?.DisplayName);
                }
                else if (build.Ok)
                {
                    _logger.LogInformation("INIBuild produced the ReShade.ini template for {Game}", _currentGameEntry?.DisplayName);
                }
                else
                {
                    // 没有模板也能注入（游戏目录里可能本来就有 ini），所以只警告不中断
                    _logger.LogWarning("INIBuild failed: {Reason}", build.FailureReason);
                    InAppToast.MainWindow?.Warning("ReShade.ini", build.FailureReason ?? "INIBuild 失败", 6000);
                }
            }

            // 1.5 游戏 ini 里的绝对路径如果指向别的 HoYoShade（换过启动器 / 换过用户数据目录），指回来。
            //     用户的原话：「应该用哪个启动器，ini 就对应到哪才对」。
            AlignGameIniPaths(shadePath);

            // 2. 进程名：优先用游戏条目里的 exe 文件名（用户自己选的），其次 Hub 的映射，最后内置表
            string? processName = _currentGameEntry?.ProcessName;
            if (string.IsNullOrWhiteSpace(processName) && CurrentGameId is not null)
            {
                try
                {
                    processName = await _gameLauncherService.GetGameExeNameAsync(CurrentGameId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Resolve game exe name for inject mode");
                }
            }

            processName ??= KnownProcessNames.ForBiz(CurrentGameId?.GameBiz ?? default);

            if (string.IsNullOrWhiteSpace(processName))
            {
                _logger.LogWarning("Inject mode: unknown process name for {Game}", CurrentGameId?.GameBiz);
                InAppToast.MainWindow?.Error("注入模式", "不知道这个游戏的进程名 —— 先到「插件」页用「指定主程序…」选一下游戏 exe。", 8000);
                return;
            }

            // 2.4 鸣潮：注册 exe 是启动器壳，真正要注的是 Client-Win64-Shipping（inject.exe 黑名单进程，见 2.6）
            processName = ShadeBlacklistBypass.RemapInjectProcessName(_currentGameEntry, processName);

            // 2.6 inject.exe 硬编码黑名单进程（鸣潮 Client-Win64-Shipping）：inject.exe 一律拒注（退 1002），
            //     改由 Hub 自己的 DllInjector 等进程注 ReShade64.dll —— 和 OptiScaler 同一套外部注入，没有黑名单。
            //     用户在启动选项勾了「不用 HoYoShade 注入器」时也走这条（实测/绕开 inject.exe 行为差异）。
            //     ini 也不能只放注册 exe 旁边：ReShade64.dll 从真身进程 exe 目录找 ReShade.ini，要在真身目录补一份。
            if (ShadeBlacklistBypass.IsBlacklisted(processName) || _currentGameEntry?.SkipShadeInjector == true)
            {
                string shadeDll = Path.Combine(shadePath, "ReShade64.dll");
                if (!File.Exists(shadeDll))
                {
                    _logger.LogWarning("ReShade64.dll not found in {ShadeName} at {Path}", shadeName, shadeDll);
                    InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_InjectExeNotFound, shadeName));
                    return;
                }

                // 真身目录（鸣潮 = <安装>\Wuthering Waves Game\Binaries\Win64）缺 ReShade.ini 时从宿主模板补
                if (host is not null && ShadeBlacklistBypass.RealGameDirectory(_currentGameEntry) is { } realDir)
                {
                    string realIni = Path.Combine(realDir, "ReShade.ini");
                    if (!File.Exists(realIni) && File.Exists(host.ReShadeIniPath))
                    {
                        try
                        {
                            File.Copy(host.ReShadeIniPath, realIni);
                            _logger.LogInformation("ReShade.ini bootstrapped to real game directory: {Path}", realIni);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Bootstrap ReShade.ini to real game directory");
                        }
                    }
                }

                StopInjector("要重新注入");
                _logger.LogInformation(
                    "Inject mode: {Process} is on inject.exe's hardcoded blacklist — Hub injects {ShadeName} ReShade64.dll itself",
                    processName, shadeName);
                StartExtraDllInjection(processName, shadeDll, shadeName);
                ShowWaitProcessToast(processName);

                if (UseFpsUnlock)
                {
                    _ = StartFpsUnlockAsync(TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(60));
                }

                return;
            }

            // 2.5 插件（HoYoShade / ReShade）注入时机：游戏**已经在跑**时，先等它稳一稳再起 inject.exe
            //     （用户要「注入时机」能调）。没在跑就照旧立刻架好 —— inject.exe 自己等进程，
            //     晚架会错过 ReShade 的早期 hook，所以这一档只在游戏已经起来时才生效。
            int shadeDelay = AppConfig.GetShadeInjectDelayEffective(CurrentGameBiz);
            if (shadeDelay > 0)
            {
                Process? alreadyRunning = FindRunningProcess(processName);
                if (alreadyRunning is not null)
                {
                    using (alreadyRunning)
                    {
                        await WaitForInjectionSteadyAsync(
                            alreadyRunning, alreadyRunning.Id, processName, shadeName, shadeDelay,
                            System.Threading.CancellationToken.None);
                    }
                }
            }

            // 3. 再点一次开始游戏 / 换游戏：先把上一个注入器停掉（用户要求）
            StopInjector("要重新注入");

            _logger.LogInformation("Inject mode: starting {ShadeName} injector for {Process}", shadeName, processName);
            var (success, exitCode, injectorProcess) = await InjectorHelper.StartAndWaitForReadyAsync(
                injectExePath, processName, shadePath, _logger, shadeName);

            if (!success)
            {
                if (InjectorErrorCodes.IsInjectorError(exitCode))
                {
                    string errorMessage = InjectorHelper.GetErrorMessage(exitCode, shadeName);
                    _logger.LogError("{ShadeName} validation failed (exit code: {ExitCode})", shadeName, exitCode);
                    InAppToast.MainWindow?.Error(errorMessage, null, 8000);
                }
                else
                {
                    _logger.LogError("{ShadeName} injector failed to start or timed out", shadeName);
                    InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_InjectorStartFailed, shadeName, "timeout"));
                }
                return;
            }

            // 4. 「已启动 HoYoShade 注入器」：常驻提示（注入器在跑就一直挂着），
            //    标题右边一个红色「停止」；不再替用户启动游戏（见 GAMES-AND-INJECT.md §8.7）。
            _injectorProcess = injectorProcess;
            ShowInjectorToast(shadeName);

            // 额外的 DLL（OptiScaler / DLSS Enabler 那套）也等这个进程
            StartExtraDllInjection(processName);

            if (UseFpsUnlock)
            {
                _ = StartFpsUnlockAsync(TimeSpan.FromMinutes(20), TimeSpan.FromSeconds(60));
            }
        }
        catch (FileNotFoundException)
        {
            CheckGameVersion();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Start game with inject mode");
            InAppToast.MainWindow?.Error("注入模式启动失败：" + ex.Message, null, 8000);
        }
    }

    /// <summary>
    /// 把当前游戏那份 <c>ReShade.ini</c> 里的绝对路径对到正在用的这个 HoYoShade。
    ///
    /// <para>
    /// HoYoShade 的 INIBuild 写出来的是**绝对路径**模板，inject.exe 又只在游戏目录没有 ini 时才复制，
    /// 所以换过 HoYoShade（换启动器、换用户数据目录）之后老 ini 会一直指着旧目录：插件页按它算出来的
    /// 「缺 nvngx_dlssnr.dll」是真的，游戏运行时也确实加载不到。这里在启动/注入前对一次。
    /// </para>
    /// </summary>
    private void AlignGameIniPaths(string shadePath)
    {
        try
        {
            if (_currentGameEntry?.ReShadeIniPath is not { } gameIni || !File.Exists(gameIni))
            {
                return;
            }

            ShadeHost? host = ShadeHostLocator.FromShadeRoot(shadePath);
            if (host is null)
            {
                return;
            }

            // 需求3：启动/注入前先把「每游戏插件包」拼好（这个游戏给插件选过版本才会有）。
            // 顺序很重要：拼包会把 AddonPath 指到包目录，紧接着的 Align 会认出 pack.json 并跳过它，
            // 不会把每游戏选版本悄悄改回共享目录。
            GameAddonPackService.Sync(CurrentGameId, _currentGameEntry, host);

            ShadePathAlignResult align = ShadePathAligner.Align(gameIni, host);
            if (!align.Changed)
            {
                return;
            }

            _logger.LogInformation("ReShade.ini paths re-pointed from {Old} to {New}: {Keys}",
                align.PreviousRoot, host.RootPath, string.Join(", ", align.ChangedKeys));
            InAppToast.MainWindow?.Information("ReShade.ini",
                $"这份 ini 原来指向 {align.PreviousRoot}，已改成当前 HoYoShade：{host.RootPath}（{string.Join("、", align.ChangedKeys)}）",
                8000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Align ReShade.ini paths");
        }
    }

    /// <summary>自定义游戏：不注入，直接起它自己的 exe</summary>
    private async Task StartCustomGameAsync()
    {
        try
        {
            if (_currentGameEntry?.ExePath is not { } exe || !File.Exists(exe))
            {
                InAppToast.MainWindow?.Error("启动失败", "这个自定义游戏还没有指定主程序 —— 到「插件」页用「指定主程序…」选一下 exe。", 8000);
                return;
            }

            string? directory = Path.GetDirectoryName(exe);
            // 勾了「使用 DX12 启动」就带 -dx12（鸣潮的 DX12 就是这个启动项）
            string arguments = AppConfig.GetEnableDX12(CurrentGameBiz) ? "-dx12" : "";
            _logger.LogInformation("Launching custom game: {Exe} (args: {Args})", exe, arguments);

            Process? process = Process.Start(new ProcessStartInfo(exe)
            {
                Arguments = arguments,
                WorkingDirectory = string.IsNullOrWhiteSpace(directory) ? AppContext.BaseDirectory : directory,
                UseShellExecute = true,
            });

            if (process is null)
            {
                InAppToast.MainWindow?.Error("启动失败", exe, 8000);
                return;
            }

            GameState = GameState.GameIsRunning;
            GameProcess = process;
            WeakReferenceMessenger.Default.Send(new GameStartedMessage());
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launch custom game");
            InAppToast.MainWindow?.Error("启动失败：" + ex.Message, null, 8000);
        }
    }



    private async Task<bool> LaunchShaderInjectorOnlyAsync(string shadePath, string shadeName)
    {
        try
        {
            if (!Directory.Exists(shadePath))
            {
                _logger.LogWarning("{ShadeName} directory not found at {Path}", shadeName, shadePath);
                InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_ShaderNotInstalled, shadeName));
                return false;
            }

            string injectExePath = Path.Combine(shadePath, "inject.exe");
            if (!File.Exists(injectExePath))
            {
                _logger.LogWarning("inject.exe not found in {ShadeName} at {Path}", shadeName, injectExePath);
                ToastInjectExeMissing();
                return false;
            }

            var gameExeName = await _gameLauncherService.GetGameExeNameAsync(CurrentGameId);
            var startInfo = new ProcessStartInfo
            {
                FileName = injectExePath,
                Arguments = gameExeName,
                WorkingDirectory = shadePath,
                // inject.exe 是控制台程序：以前 UseShellExecute = true 会冒一个黑框（用户反馈过），
                // 改成不经过 shell + 隐藏窗口。
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            };

            Process? process = Process.Start(startInfo);
            if (process is null)
            {
                _logger.LogError("Failed to start {ShadeName} injector in shader-only mode", shadeName);
                InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_InjectorStartFailed, shadeName, "unknown error"));
                return false;
            }

            InAppToast.MainWindow?.Success(string.Format(Lang.GameLauncher_InjectorStarted, shadeName));
            _logger.LogInformation("{ShadeName} injector launched in shader-only mode (PID: {Pid})", shadeName, process.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launch {ShadeName} injector in shader-only mode", shadeName);
            InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_InjectorStartFailed, shadeName, ex.Message));
            return false;
        }
    }

    private async Task<Process?> LaunchGenshinBlenderPluginAsync()
    {
        try
        {
            string? pluginPath = AppConfig.GenshinBlenderPluginPath;
            if (string.IsNullOrWhiteSpace(pluginPath) || !Directory.Exists(pluginPath))
            {
                _logger.LogWarning("Genshin Blender plugin path not configured");
                InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_BlenderPluginNotConfigured, GetGameName(GameBiz.hk4e)));
                return null;
            }

            string clientExePath = Path.Combine(pluginPath, "client.exe");
            if (!File.Exists(clientExePath))
            {
                _logger.LogWarning("client.exe not found in {Path}", pluginPath);
                InAppToast.MainWindow?.Error(Lang.GameLauncher_ClientExeNotFound);
                return null;
            }

            _logger.LogInformation("Launching Genshin Blender plugin: {Path}", clientExePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = clientExePath,
                WorkingDirectory = pluginPath,
                UseShellExecute = true
            };

            Process? process = Process.Start(startInfo);
            InAppToast.MainWindow?.Success(string.Format(Lang.GameLauncher_BlenderPluginStarted, GetGameName(GameBiz.hk4e)));
            _logger.LogInformation("Genshin Blender plugin launched successfully (PID: {Pid})", process?.Id ?? -1);

            return process;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launch Genshin Blender plugin");
            InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_BlenderPluginStartFailed, GetGameName(GameBiz.hk4e), ex.Message));
            return null;
        }
    }

    private async Task<Process?> LaunchZZZBlenderPluginAsync()
    {
        try
        {
            string? pluginPath = AppConfig.ZZZBlenderPluginPath;
            if (string.IsNullOrWhiteSpace(pluginPath) || !Directory.Exists(pluginPath))
            {
                _logger.LogWarning("ZZZ Blender plugin path not configured");
                InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_BlenderPluginNotConfigured, GetGameName(GameBiz.nap)));
                return null;
            }

            string loaderExePath = Path.Combine(pluginPath, "loader.exe");
            if (!File.Exists(loaderExePath))
            {
                _logger.LogWarning("loader.exe not found in {Path}", pluginPath);
                InAppToast.MainWindow?.Error(Lang.GameLauncher_LoaderExeNotFound);
                return null;
            }

            _logger.LogInformation("Launching ZZZ Blender plugin: {Path}", loaderExePath);

            var startInfo = new ProcessStartInfo
            {
                FileName = loaderExePath,
                WorkingDirectory = pluginPath,
                UseShellExecute = true
            };

            Process? process = Process.Start(startInfo);
            InAppToast.MainWindow?.Success(string.Format(Lang.GameLauncher_BlenderPluginStarted, GetGameName(GameBiz.nap)));
            _logger.LogInformation("ZZZ Blender plugin launched successfully (PID: {Pid})", process?.Id ?? -1);

            return process;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launch ZZZ Blender plugin");
            InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_BlenderPluginStartFailed, GetGameName(GameBiz.nap), ex.Message));
            return null;
        }
    }

    private async Task<Process?> LaunchGameWithShadeAsync(string shadePath, string shadeName)
    {
        try
        {
            if (!Directory.Exists(shadePath))
            {
                _logger.LogWarning("{ShadeName} directory not found at {Path}", shadeName, shadePath);
                InAppToast.MainWindow?.Error($"{shadeName} not installed");
                return null;
            }

            // Check if inject.exe exists
            string injectExePath = Path.Combine(shadePath, "inject.exe");
            if (!File.Exists(injectExePath))
            {
                _logger.LogWarning("inject.exe not found in {ShadeName} at {Path}", shadeName, injectExePath);
                ToastInjectExeMissing();
                return null;
            }

            // 这份 ini 里的绝对路径如果指向别的 HoYoShade，先指回当前这个 —— 否则游戏加载的是别人家的插件目录
            AlignGameIniPaths(shadePath);

            var gameInstallPath = GameLauncherService.GetGameInstallPath(CurrentGameId);
            if (string.IsNullOrWhiteSpace(gameInstallPath))
            {
                _logger.LogWarning("Game install path not found");
                InAppToast.MainWindow?.Error("Game not found");
                return null;
            }

            var gameExeName = await _gameLauncherService.GetGameExeNameAsync(CurrentGameId);
            var gameExePath = Path.Combine(gameInstallPath, gameExeName);

            if (!File.Exists(gameExePath))
            {
                _logger.LogWarning("Game exe not found: {Path}", gameExePath);
                throw new FileNotFoundException("Game exe not found", gameExeName);
            }

            // 黑名单绕行（鸣潮）：inject.exe 拒注的进程改由 Hub 自己的 DllInjector 等进程注 ReShade64.dll
            //（同 OptiScaler 那套外部注入）。用户在启动选项勾了「不用 HoYoShade 注入器」时也走这条。
            gameExeName = ShadeBlacklistBypass.RemapInjectProcessName(_currentGameEntry, gameExeName);
            if (ShadeBlacklistBypass.IsBlacklisted(gameExeName) || _currentGameEntry?.SkipShadeInjector == true)
            {
                string shadeDll = Path.Combine(shadePath, "ReShade64.dll");
                if (!File.Exists(shadeDll))
                {
                    _logger.LogWarning("ReShade64.dll not found in {ShadeName} at {Path}", shadeName, shadeDll);
                    InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_InjectExeNotFound, shadeName));
                    return null;
                }

                // 没人替我们拷 ini 了（这是 inject.exe 的活）：游戏目录缺 ReShade.ini 时从宿主模板补
                if (_currentGameEntry?.ReShadeIniPath is { } gameIni && !File.Exists(gameIni))
                {
                    string template = Path.Combine(shadePath, "ReShade.ini");
                    if (File.Exists(template))
                    {
                        try { File.Copy(template, gameIni); }
                        catch (Exception copyEx) { _logger.LogWarning(copyEx, "Bootstrap ReShade.ini for blacklist bypass"); }
                    }
                }

                _logger.LogInformation(
                    "{GameExe} {Reason} —— 跳过 inject.exe，改由 Hub 注入 {ShadeName} ReShade64.dll",
                    gameExeName,
                    _currentGameEntry?.SkipShadeInjector == true && !ShadeBlacklistBypass.IsBlacklisted(gameExeName)
                        ? "按启动选项不用 HoYoShade 注入器"
                        : "在 HoYoShade 注入器黑名单里",
                    shadeName);

                var bypassProcess = await _gameLauncherService.StartGameAsync(CurrentGameId, gameInstallPath);
                if (bypassProcess is null)
                {
                    InAppToast.MainWindow?.Error(Lang.GameLauncher_GameLaunchFailed);
                    return null;
                }

                InAppToast.MainWindow?.Success(string.Format(Lang.GameLauncher_LaunchedWithShader, shadeName));
                // 会取代启动流程前面已经 arm 的那次（无 shade spec），以这份带 ReShade64.dll 的为准
                StartExtraDllInjection(Path.GetFileNameWithoutExtension(gameExeName), shadeDll, shadeName);
                return bypassProcess;
            }

            // Start injector and wait for it to be ready
            _logger.LogInformation("Starting {ShadeName} injector and waiting for ready signal...", shadeName);
            
            var (success, exitCode, injectorProcess) = await InjectorHelper.StartAndWaitForReadyAsync(
                injectExePath, gameExeName, shadePath, _logger, shadeName);
            
            if (!success)
            {
                // Validation failed or timeout
                if (InjectorErrorCodes.IsInjectorError(exitCode))
                {
                    string errorMessage = InjectorHelper.GetErrorMessage(exitCode, shadeName);
                    _logger.LogError("{ShadeName} validation failed (exit code: {ExitCode})", shadeName, exitCode);
                    InAppToast.MainWindow?.Error(errorMessage, null, 8000);
                }
                else
                {
                    _logger.LogError("{ShadeName} injector failed to start or timed out", shadeName);
                    InAppToast.MainWindow?.Error($"Failed to start {shadeName} injector");
                }
                return null;
            }
            
            _logger.LogInformation("{ShadeName} injector is ready, launching game...", shadeName);

            // Injector is ready and waiting for game process - now launch the game
            var gameProcess = await _gameLauncherService.StartGameAsync(CurrentGameId, gameInstallPath);

            if (gameProcess != null)
            {
                InAppToast.MainWindow?.Success(string.Format(Lang.GameLauncher_LaunchedWithShader, shadeName));
                _logger.LogInformation("Game launched with {ShadeName}, process: {Name} ({Id})",
                    shadeName, gameProcess.ProcessName, gameProcess.Id);
                return gameProcess;
            }
            else
            {
                _logger.LogWarning("Failed to start game process");
                InAppToast.MainWindow?.Error(Lang.GameLauncher_GameLaunchFailed);
                // Kill injector since game didn't start
                try { injectorProcess?.Kill(); } catch { }
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launch game with {ShadeName}", shadeName);
            InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_LaunchWithShaderFailed, shadeName, ex.Message));
            return null;
        }
    }


    /// <summary>
    /// 通过 Starward 启动器启动游戏
    /// </summary>
    /// <param name="useShader">是否同时启动 HoYoShade/OpenHoYoShade</param>
    private async Task LaunchGameViaStarwardAsync(bool useShader)
    {
        try
        {
            var gameInstallPath = GameLauncherService.GetGameInstallPath(CurrentGameId);
            Process? injectorProcess = null;
            
            // 如果需要使用 shader，先启动注入器并等待就绪
            if (useShader)
            {
                string shadePath = "";
                string shadeName = "";
                
                if (UseHoYoShade)
                {
                    shadePath = Path.Combine(AppConfig.UserDataFolder, "HoYoShade");
                    shadeName = "HoYoShade";
                }
                else if (UseOpenHoYoShade)
                {
                    shadePath = Path.Combine(AppConfig.UserDataFolder, "OpenHoYoShade");
                    shadeName = "OpenHoYoShade";
                }

                if (!string.IsNullOrEmpty(shadePath))
                {
                    if (!Directory.Exists(shadePath))
                    {
                        _logger.LogWarning("{ShadeName} directory not found at {Path}", shadeName, shadePath);
                        InAppToast.MainWindow?.Error($"{shadeName} not installed");
                        return;
                    }

                    string injectExePath = Path.Combine(shadePath, "inject.exe");
                    if (!File.Exists(injectExePath))
                    {
                        _logger.LogWarning("inject.exe not found in {ShadeName} at {Path}", shadeName, injectExePath);
                        ToastInjectExeMissing();
                        return;
                    }

                    var gameExeName = await _gameLauncherService.GetGameExeNameAsync(CurrentGameId);

                    // Start injector and wait for ready
                    _logger.LogInformation("Starting {ShadeName} injector and waiting for ready signal...", shadeName);
                    
                    var (success, exitCode, process) = await InjectorHelper.StartAndWaitForReadyAsync(
                        injectExePath, gameExeName, shadePath, _logger, shadeName);
                    
                    if (!success)
                    {
                        if (InjectorErrorCodes.IsInjectorError(exitCode))
                        {
                            string errorMessage = InjectorHelper.GetErrorMessage(exitCode, shadeName);
                            _logger.LogError("{ShadeName} validation failed (exit code: {ExitCode})", shadeName, exitCode);
                            InAppToast.MainWindow?.Error(errorMessage, null, 8000);
                        }
                        else
                        {
                            _logger.LogError("{ShadeName} injector failed to start or timed out", shadeName);
                            InAppToast.MainWindow?.Error($"Failed to start {shadeName} injector");
                        }
                        return;
                    }
                    
                    injectorProcess = process;
                    _logger.LogInformation("{ShadeName} injector is ready", shadeName);
                }
            }

            // Launch via Starward
            string starwardUrl = BuildStarwardProtocolUrl(CurrentGameBiz, gameInstallPath);
            
            _logger.LogInformation("Launching game via Starward: {Url}", starwardUrl);

            bool success2 = await Launcher.LaunchUriAsync(new Uri(starwardUrl));
            
            if (success2)
            {
                _logger.LogInformation("Successfully launched game via Starward");
                InAppToast.MainWindow?.Success(Lang.GameLauncher_StarwardLaunchSuccess);
            }
            else
            {
                _logger.LogWarning("Failed to launch game via Starward");
                InAppToast.MainWindow?.Error(Lang.GameLauncher_StarwardLaunchFailed);
                // Kill injector since game didn't start
                try { injectorProcess?.Kill(); } catch { }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Launch game via Starward");
            InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_StarwardLaunchError, ex.Message));
        }
    }

    /// <summary>
    /// 构建 Starward URL 协议命令
    /// </summary>
    /// <param name="gameBiz">游戏区服</param>
    /// <param name="installPath">游戏安装路径（可选）</param>
    /// <returns>Starward URL 协议字符串</returns>
    private string BuildStarwardProtocolUrl(GameBiz gameBiz, string? installPath = null)
    {
        // 根据 UrlProtocol.md 文档，格式为：
        // starward://startgame/{game_biz}?install_path={install_path}
        
        string url = $"starward://startgame/{gameBiz}";
        
        if (!string.IsNullOrWhiteSpace(installPath))
        {
            // URL 编码安装路径
            string encodedPath = Uri.EscapeDataString(installPath);
            url += $"?install_path={encodedPath}";
        }
        
        return url;
    }


    #endregion

    /// <summary>
    /// inject.exe 不见了（多半是杀毒软件删的）：弹一条**常驻**错误条（不自动消失），
    /// 右侧给高亮「去修复」按钮 —— 点一下跳到 HoYoShade 更新页，把 inject.exe / ReShade64.dll 补回来。
    /// </summary>
    private static void ToastInjectExeMissing()
    {
        var fixButton = new Button
        {
            Content = "去修复",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
        };
        fixButton.Click += (_, _) =>
            WeakReferenceMessenger.Default.Send(new NavigateToReShadeDownloadPageMessage { IsUpdateMode = true });

        var infoBar = new InfoBar
        {
            Severity = InfoBarSeverity.Error,
            Title = "inject.exe文件不存在，可能被杀毒软件删除，检查安全软件或安全中心",
            ActionButton = fixButton,
            IsOpen = true,
        };
        // duration = 0：不自动关，用户看完自己点叉（30 秒清理定时器会摘掉关掉的条目）。
        InAppToast.MainWindow?.Show(infoBar);
    }

}

