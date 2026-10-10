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


    private readonly ILogger<GameLauncherPage> _logger ;
    private readonly GameLauncherService _gameLauncherService ;
    private readonly BackgroundService _backgroundService ;
    private readonly HoYoPlayService _hoYoPlayService ;
    private readonly HoYoShadeVersionService _versionService ;

    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _dispatchTimer;
    private bool _isApplyingSavedLaunchOptions;

    /// <summary>
    /// 获取本地化的游戏名称
    /// </summary>
    private string GetGameName(GameBiz gameBiz)
    {
        return gameBiz.ToGame().Value switch
        {
            GameBiz.hk4e => Lang.GameName_GenshinImpact,
            GameBiz.nap => Lang.GameName_ZenlessZoneZero,
            _ => gameBiz.ToString()
        };
    }

    public GameLauncherPage()
    {
        this.InitializeComponent();
        _logger = AppConfig.GetLogger<GameLauncherPage>();
        _gameLauncherService = AppConfig.GetService<GameLauncherService>();
        _backgroundService = AppConfig.GetService<BackgroundService>();
        _hoYoPlayService = AppConfig.GetService<HoYoPlayService>();
        _versionService = new HoYoShadeVersionService(AppConfig.UserDataFolder);
        _dispatchTimer = DispatcherQueue.CreateTimer();
        _dispatchTimer.Interval = TimeSpan.FromMilliseconds(100);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        LoadLaunchOptionsForCurrentClient();
    }









    protected override void OnLoaded()
    {
        InitializeGameFeature();
        CheckGameVersion();
        CheckShadeInstallation();
        _ = InitializeGameServerAsync();
        _ = InitializeBackgameImageSwitcherAsync();
        WeakReferenceMessenger.Default.Register<GameInstallPathChangedMessage>(this, OnGameInstallPathChanged);
        WeakReferenceMessenger.Default.Register<MainWindowStateChangedMessage>(this, OnMainWindowStateChanged);
        WeakReferenceMessenger.Default.Register<RemovableStorageDeviceChangedMessage>(this, OnRemovableStorageDeviceChanged);
        WeakReferenceMessenger.Default.Register<BackgroundChangedMessage>(this, OnBackgroundChanged);
        WeakReferenceMessenger.Default.Register<UseStarwardLauncherChangedMessage>(this, OnUseStarwardLauncherChanged);
        WeakReferenceMessenger.Default.Register<HoYoShadeInstallationChangedMessage>(this, (r, m) => CheckShadeInstallation());
    }



    protected override void OnUnloaded()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _dispatchTimer.Stop();
        BackgroundImages = null!;
    }




    private void InitializeGameFeature()
    {
        GameFeatureConfig feature = GameFeatureConfig.FromGameId(CurrentGameId);
    }


    private async void CheckShadeInstallation()
    {
        try
        {
            // Check HoYoShade installation
            string hoYoShadePath = Path.Combine(AppConfig.UserDataFolder, "HoYoShade");
            IsHoYoShadeInstalled = Directory.Exists(hoYoShadePath) &&
                                   Directory.GetFiles(hoYoShadePath, "*.dll").Length > 0;

            // Check OpenHoYoShade installation
            string openHoYoShadePath = Path.Combine(AppConfig.UserDataFolder, "OpenHoYoShade");
            IsOpenHoYoShadeInstalled = Directory.Exists(openHoYoShadePath) &&
                                       Directory.GetFiles(openHoYoShadePath, "*.dll").Length > 0;

            // Load versions
            try 
            {
                var manifest = await _versionService.LoadManifestAsync();
                HoYoShadeVersion = IsHoYoShadeInstalled ? (manifest.HoYoShade?.Version ?? "") : "";
                OpenHoYoShadeVersion = IsOpenHoYoShadeInstalled ? (manifest.OpenHoYoShade?.Version ?? "") : "";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load shade versions");
                HoYoShadeVersion = "";
                OpenHoYoShadeVersion = "";
            }

            // Uncheck options if shaders are not installed
            if (!IsHoYoShadeInstalled && UseHoYoShade)
            {
                UseHoYoShade = false;
            }
            if (!IsOpenHoYoShadeInstalled && UseOpenHoYoShade)
            {
                UseOpenHoYoShade = false;
            }

            // OptiScaler：本地有装好的构建才让勾（选哪个构建在「OptiScaler」页）
            IsOptiScalerAvailable = HasInstalledOptiScaler();
            if (!IsOptiScalerAvailable && UseOptiScaler)
            {
                UseOptiScaler = false;
            }

            // XXMI / Rocket：Rocket 是原神外部启动准备模式。
            UpdateXxmiInjectVisibility();
            OnPropertyChanged(nameof(RocketVisibility));
            OnPropertyChanged(nameof(CanUseRocket));
        OnPropertyChanged(nameof(CanConfigureXxmi));
            if (!CanUseRocket && UseRocket) UseRocket = false;

            // 帧率解锁：只对原神显示
            UpdateFpsUnlockVisibility();

            // 老配置里的「额外注入 DLL」（每个游戏一个路径）搬进「模块」页（一次性）
            if (CurrentGameId is not null)
            {
                Features.Modules.ModuleRegistry.MigrateLegacyExtraInjectDll(CurrentGameId.GameBiz);
            }

            // Check Blender plugin configurations
            CheckBlenderPluginConfigurations();

            // Check Starward protocol availability
            CheckStarwardProtocolAvailability();

            _logger.LogInformation("HoYoShade installed: {HoYoShade} ({Version}), OpenHoYoShade installed: {OpenHoYoShade} ({OpenVersion})",
                IsHoYoShadeInstalled, HoYoShadeVersion, IsOpenHoYoShadeInstalled, OpenHoYoShadeVersion);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check shade installation");
            IsHoYoShadeInstalled = false;
            IsOpenHoYoShadeInstalled = false;
        }
    }

    private void CheckStarwardProtocolAvailability()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey("starward");
            if (key != null)
            {
                var urlProtocol = key.GetValue("URL Protocol");
                IsStarwardProtocolAvailable = urlProtocol != null;
            }
            else
            {
                IsStarwardProtocolAvailable = false;
            }

            // 读取设置页面的"使用Starward启动器启动公开客户端游戏"开关状态
            bool useStarwardLauncherSetting = AppConfig.UseStarwardLauncher;
            
            // 检查是否为Beta客户端
            bool isBetaClient = CurrentGameBiz.IsBetaServer();

            // If protocol not available OR setting is disabled OR is beta client, and UseStarwardLauncher is enabled, disable it
            if ((!IsStarwardProtocolAvailable || !useStarwardLauncherSetting || isBetaClient) && UseStarwardLauncher)
            {
                UseStarwardLauncher = false;
            }

            // 通知 IsStarwardLauncherCheckboxEnabled 和 Visibility 属性更新
            OnPropertyChanged(nameof(IsStarwardLauncherCheckboxEnabled));
            OnPropertyChanged(nameof(IsStarwardLauncherCheckboxVisible));

            _logger.LogInformation("Starward protocol available: {Available}, Setting enabled: {Setting}, IsBeta: {IsBeta}", 
                IsStarwardProtocolAvailable, useStarwardLauncherSetting, isBetaClient);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check Starward protocol availability");
            IsStarwardProtocolAvailable = false;
            // 即使发生异常也要通知属性更新
            OnPropertyChanged(nameof(IsStarwardLauncherCheckboxEnabled));
            OnPropertyChanged(nameof(IsStarwardLauncherCheckboxVisible));
        }
    }

    private void CheckBlenderPluginConfigurations()
    {
        try
        {
            // Check Genshin Impact Blender Plugin - 隐藏Beta客户端的Blender插件选项
            bool isGenshinGame = CurrentGameBiz.ToGame().Value == GameBiz.hk4e;
            bool isGenshinBetaClient = CurrentGameBiz.IsBetaServer();
            
            // 只有非Beta客户端的原神才显示Blender插件选项
            IsGenshinBlenderPluginVisible = (isGenshinGame && !isGenshinBetaClient) ? Visibility.Visible : Visibility.Collapsed;

            if (isGenshinGame && !isGenshinBetaClient)
            {
                string? genshinPluginPath = AppConfig.GenshinBlenderPluginPath;
                IsGenshinBlenderPluginConfigured = !string.IsNullOrWhiteSpace(genshinPluginPath) &&
                                                   Directory.Exists(genshinPluginPath) &&
                                                   File.Exists(Path.Combine(genshinPluginPath, "client.exe"));

                if (!IsGenshinBlenderPluginConfigured && LaunchGenshinBlenderPlugin)
                {
                    LaunchGenshinBlenderPlugin = false;
                }
            }
            else if (isGenshinBetaClient)
            {
                // Beta客户端强制取消Blender插件选项
                if (LaunchGenshinBlenderPlugin)
                {
                    LaunchGenshinBlenderPlugin = false;
                }
            }

            // Check ZZZ Blender Plugin - 隐藏Beta客户端的Blender插件选项
            bool isZZZGame = CurrentGameBiz.ToGame().Value == GameBiz.nap;
            bool isZZZBetaClient = CurrentGameBiz.IsBetaServer();
            
            // 只有非Beta客户端的绝区零才显示Blender插件选项
            IsZZZBlenderPluginVisible = (isZZZGame && !isZZZBetaClient) ? Visibility.Visible : Visibility.Collapsed;

            if (isZZZGame && !isZZZBetaClient)
            {
                string? zzzPluginPath = AppConfig.ZZZBlenderPluginPath;
                IsZZZBlenderPluginConfigured = !string.IsNullOrWhiteSpace(zzzPluginPath) &&
                                               Directory.Exists(zzzPluginPath) &&
                                               File.Exists(Path.Combine(zzzPluginPath, "loader.exe"));

                if (!IsZZZBlenderPluginConfigured && LaunchZZZBlenderPlugin)
                {
                    LaunchZZZBlenderPlugin = false;
                }
            }
            else if (isZZZBetaClient)
            {
                // Beta客户端强制取消Blender插件选项
                if (LaunchZZZBlenderPlugin)
                {
                    LaunchZZZBlenderPlugin = false;
                }
            }

            _logger.LogInformation("Blender plugins - Genshin configured: {Genshin}, ZZZ configured: {ZZZ}, Genshin Beta: {GenshinBeta}, ZZZ Beta: {ZZZBeta}",
                IsGenshinBlenderPluginConfigured, IsZZZBlenderPluginConfigured, isGenshinBetaClient, isZZZBetaClient);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check blender plugin configurations");
        }
    }


    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstalledLocateGameEnabled))]
    public partial GameState GameState { get; set; }

    // 用于记住用户在启用Blender插件前的启动选项
    private bool _previousEnableGameLaunch = true;
    private bool _previousUseStarwardLauncher = false;

    private bool _enableGameLaunch = true;
    public bool EnableGameLaunch
    {
        get => _enableGameLaunch;
        set
        {
            if (SetProperty(ref _enableGameLaunch, value))
            {
                if (value)
                {
                    // 取消勾选 UseStarwardLauncher
                    if (_useStarwardLauncher)
                    {
                        _useStarwardLauncher = false;
                        OnPropertyChanged(nameof(UseStarwardLauncher));
                        
                        // 重新检查游戏版本以更新 GameState
                        CheckGameVersion();
                    }
                }
                NotifyLaunchModeChanged();
            }
        }
    }

    /// <summary>
    /// 启动按钮是否应该可用 - 只要勾选了任何一个启动选项就可用
    /// 如果选择了Starward启动器或Blender插件，即使游戏未定位也应该可以启动
    /// </summary>
    public bool ShouldEnableStartButton
    {
        get
        {
            // 如果选择了Starward启动器或Blender插件，总是启用启动按钮
            if (UseStarwardLauncher || LaunchGenshinBlenderPlugin || LaunchZZZBlenderPlugin || UseHoYoShade || UseOpenHoYoShade || UseRocket)
            {
                return true;
            }
            
            // 否则只有在选择了启动游戏时才启用
            return EnableGameLaunch;
        }
    }

    public bool IsShaderOnlyLaunchMode =>
        !EnableGameLaunch &&
        !UseStarwardLauncher &&
        !LaunchGenshinBlenderPlugin &&
        !LaunchZZZBlenderPlugin &&
        (UseHoYoShade || UseOpenHoYoShade);

    /// <summary>注入模式 + 勾了 HoYoShade / OpenHoYoShade 之一 → 按钮显示「启动注入器」</summary>
    public bool IsShadeInjectMode => UseInjectMode && (UseHoYoShade || UseOpenHoYoShade);

    /// <summary>Rocket 联动准备模式：Hub 不创建游戏进程。</summary>
    private bool _isRocketWaiting;
    public bool IsRocketMode
    {
        get => _isRocketWaiting;
        private set => SetProperty(ref _isRocketWaiting, value);
    }

    public bool CanUseRocket => CurrentGameId is { GameBiz.Game: GameBiz.hk4e } && !UseXxmiInject;
    public Visibility RocketInstructionVisibility => UseRocket ? Visibility.Visible : Visibility.Collapsed;

    public Visibility RocketVisibility => CurrentGameId is { GameBiz.Game: GameBiz.hk4e }
        ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 注入模式但一个 HoYoShade 都没勾 → 不架 ReShade 注入器，只等游戏进程注「额外注入 DLL」/ OptiScaler。
    /// 用户报过：没勾「启动 HoYoShade」也被注入了 HoYoShade。
    /// </summary>
    public bool IsWaitProcessOnlyMode => UseInjectMode && !UseHoYoShade && !UseOpenHoYoShade;

    private bool _isWaitingForProcess;

    /// <summary>
    /// 运行状态：现在真的在等游戏进程出现。
    /// 以前这个属性只看勾选项，于是「没勾 shade」时按钮一直显示「等游戏进程」，
    /// 看起来像注入器在跑（用户报过）。
    /// </summary>
    public bool IsWaitProcessMode
    {
        get => _isWaitingForProcess;
        private set
        {
            if (_isWaitingForProcess != value)
            {
                _isWaitingForProcess = value;
                OnPropertyChanged(nameof(IsWaitProcessMode));
            }
        }
    }

    private bool _useHoYoShade;
    public bool UseHoYoShade
    {
        get => _useHoYoShade;
        set
        {
            if (SetProperty(ref _useHoYoShade, value))
            {
                if (value)
                {
                    // Uncheck OpenHoYoShade if HoYoShade is checked
                    UseOpenHoYoShade = false;
                    _logger.LogInformation("UseHoYoShade enabled, UseOpenHoYoShade disabled");
                }

                NotifyLaunchModeChanged();
                OnPropertyChanged(nameof(IsShadeLaunchSelected));
                OnPropertyChanged(nameof(CanSkipShadeInjector));
            }
        }
    }

    private bool _useOpenHoYoShade;
    public bool UseOpenHoYoShade
    {
        get => _useOpenHoYoShade;
        set
        {
            if (SetProperty(ref _useOpenHoYoShade, value))
            {
                if (value)
                {
                    // Uncheck HoYoShade if OpenHoYoShade is checked
                    UseHoYoShade = false;
                    _logger.LogInformation("UseOpenHoYoShade enabled, UseHoYoShade disabled");
                }

                NotifyLaunchModeChanged();
                OnPropertyChanged(nameof(IsShadeLaunchSelected));
                OnPropertyChanged(nameof(CanSkipShadeInjector));
            }
        }
    }

    /// <summary>当前勾了任一 shade 运行时（HoYoShade / OpenHoYoShade）——「不用 HoYoShade 注入器」只在这时显示</summary>
    public bool IsShadeLaunchSelected => UseHoYoShade || UseOpenHoYoShade;

    /// <summary>当前游戏是用户自定义添加的——「不用 HoYoShade 注入器」只对自定义游戏显示</summary>
    public bool IsCustomGameEntry => _currentGameEntry?.IsCustom == true;

    /// <summary>开了模块 / OptiScaler / 任一 shade 运行时（插件由它加载）时「不用注入器」可勾</summary>
    public bool CanSkipShadeInjector => UseModules || UseOptiScaler || IsShadeLaunchSelected;

    /// <summary>当前游戏是不是鸣潮（自定义条目里认出来的那个）—— 鸣潮走本体目录的原版 ReShade，不用 HoYoShade</summary>
    private bool IsWutheringWavesCurrent => _currentGameEntry is { } entry && GameCatalog.IsWutheringWaves(entry);

    /// <summary>鸣潮不显示「启用 HoYoShade / 启用 Open HoYoShade」</summary>
    public Visibility HoYoShadeRowVisibility
        => IsWutheringWavesCurrent ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>「不用 HoYoShade 注入器」只对自定义游戏显示，鸣潮除外（它已经没有 HoYoShade 这条路）</summary>
    public Visibility SkipShadeInjectorVisibility
        => IsCustomGameEntry && !IsWutheringWavesCurrent ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>鸣潮才显示原版 ReShade 那一行</summary>
    public Visibility VanillaReShadeVisibility
        => IsWutheringWavesCurrent ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>装了原版 ReShade 才显示「启用 ReShade」勾选框</summary>
    public Visibility VanillaReShadeToggleVisibility
        => _isVanillaReShadeInstalled ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>没装原版 ReShade 时这一行显示「安装 ReShade」按钮</summary>
    public Visibility VanillaReShadeInstallVisibility
        => _isVanillaReShadeInstalled ? Visibility.Collapsed : Visibility.Visible;

    private bool _useVanillaReShade;

    /// <summary>
    /// 启用 ReShade（鸣潮）：勾上 = 游戏本体进程目录里 dxgi.dll 在位，游戏启动时自己加载它。
    /// 勾选状态直接落到文件（启用 / 改名停用），所以一律以磁盘实际状态为准，不另存配置。
    /// </summary>
    public bool UseVanillaReShade
    {
        get => _useVanillaReShade;
        set
        {
            if (SetProperty(ref _useVanillaReShade, value))
            {
                ApplyVanillaReShadeState();
            }
        }
    }

    private bool _isVanillaReShadeInstalled;

    public bool IsVanillaReShadeInstalled
    {
        get => _isVanillaReShadeInstalled;
        set
        {
            if (SetProperty(ref _isVanillaReShadeInstalled, value))
            {
                OnPropertyChanged(nameof(VanillaReShadeToggleVisibility));
                OnPropertyChanged(nameof(VanillaReShadeInstallVisibility));
            }
        }
    }

    /// <summary>鸣潮原版 ReShade 的目标目录 = 本体进程目录（注册 exe 只是启动器壳，装壳目录不会被加载）</summary>
    private string? VanillaReShadeTargetDirectory
        => _currentGameEntry is { } entry
            ? VanillaReShade.ResolveTargetDirectory(entry, entry.GameDirectory)
            : null;

    /// <summary>只读一次磁盘状态刷新界面（不写文件）</summary>
    private void LoadVanillaReShadeForCurrentClient()
    {
        string? target = VanillaReShadeTargetDirectory;

        _isVanillaReShadeInstalled = VanillaReShade.IsInstalled(target);
        _useVanillaReShade = VanillaReShade.IsActive(target);

        OnPropertyChanged(nameof(IsVanillaReShadeInstalled));
        OnPropertyChanged(nameof(UseVanillaReShade));
        OnPropertyChanged(nameof(VanillaReShadeToggleVisibility));
        OnPropertyChanged(nameof(VanillaReShadeInstallVisibility));
    }

    /// <summary>把勾选落到文件：启用 = 改名回 dxgi.dll，停用 = 改名保留（不是本启动器装的一律不碰）</summary>
    private void ApplyVanillaReShadeState()
    {
        if (_isApplyingSavedLaunchOptions || !IsWutheringWavesCurrent)
        {
            return;
        }

        string? target = VanillaReShadeTargetDirectory;

        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        if (!VanillaReShade.SetActive(target, _useVanillaReShade, out string message)
            && !string.IsNullOrWhiteSpace(message))
        {
            InAppToast.MainWindow?.Information("原版 ReShade", message, 6000);
        }

        // 以磁盘实际状态为准回刷：切换失败时勾要弹回原位
        _useVanillaReShade = VanillaReShade.IsActive(target);
        OnPropertyChanged(nameof(UseVanillaReShade));
    }

    /// <summary>鸣潮：下载官方原版 ReShade 并装进游戏本体目录</summary>
    private async void Button_InstallVanillaReShade_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? target = VanillaReShadeTargetDirectory;

            if (string.IsNullOrWhiteSpace(target))
            {
                InAppToast.MainWindow?.Error("原版 ReShade", "认不出游戏本体目录，没法安装。", 8000);
                return;
            }

            // 目录里已经有别人的 dxgi.dll（别的 ReShade / OptiScaler 代理）—— 覆盖前先问一句
            if (VanillaReShade.HasProxyDll(target) && !VanillaReShade.IsInstalled(target))
            {
                ContentDialog confirm = new()
                {
                    XamlRoot = XamlRoot,
                    Title = "原版 ReShade",
                    Content = $"{target} 里已经有一个 dxgi.dll（不是本启动器装的），多半是别的 ReShade 或 OptiScaler 代理。\n\n" +
                              "继续会把它覆盖掉，确定吗？",
                    PrimaryButtonText = "覆盖安装",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                };

                if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                {
                    return;
                }
            }

            InAppToast.MainWindow?.Information("原版 ReShade", "开始下载…", 4000);
            string installed = await VanillaReShade.InstallToGameAsync(target, null, default);
            LoadVanillaReShadeForCurrentClient();
            InAppToast.MainWindow?.Success("原版 ReShade", $"已装进 {installed}（dxgi.dll），进游戏按 Home 开覆盖层。", 8000);
        }
        catch (Exception ex)
        {
            InAppToast.MainWindow?.Error("原版 ReShade", "下载 / 安装失败：" + ex.Message, 10000);
        }
    }

    /// <summary>鸣潮：卸载原版 ReShade（只删本启动器装的那份）</summary>
    private void Button_RemoveVanillaReShade_Click(object sender, RoutedEventArgs e)
    {
        string? target = VanillaReShadeTargetDirectory;

        VanillaReShade.RemoveFromGame(target, out string message);
        LoadVanillaReShadeForCurrentClient();
        InAppToast.MainWindow?.Information("原版 ReShade", message, 8000);
    }

    /// <summary>本地库里有没有装好的 OptiScaler 构建</summary>
    private static bool HasInstalledOptiScaler()
    {
        try
        {
            string root = AppConfig.OptiScalerRootPath;
            if (root.Length == 0)
            {
                return false;
            }

            return new Extensions.Services.OptiScalerLibrary(root).List()
                .Any(b => !string.IsNullOrWhiteSpace(b.DllPath));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>启动时注入「模块」页里开着的模块（DLSS-NR on AMD 之类）。按游戏记</summary>
    private bool _useModules;
    public bool UseModules
    {
        get => _useModules;
        set
        {
            if (SetProperty(ref _useModules, value))
            {
                OnPropertyChanged(nameof(CanSkipShadeInjector));
                NotifyLaunchModeChanged();
            }
        }
    }

    /// <summary>启动时注入 OptiScaler（构建在「OptiScaler」页按游戏选，总开关在「全局插件 → OptiScaler」）。按游戏记</summary>
    private bool _useOptiScaler;
    public bool UseOptiScaler
    {
        get => _useOptiScaler;
        set
        {
            if (SetProperty(ref _useOptiScaler, value))
            {
                OnPropertyChanged(nameof(CanSkipShadeInjector));
                NotifyLaunchModeChanged();
            }
        }
    }

    /// <summary>本地装了 OptiScaler 构建才让勾（没装就灰着）</summary>
    private bool _isOptiScalerAvailable;
    public bool IsOptiScalerAvailable
    {
        get => _isOptiScalerAvailable;
        set => SetProperty(ref _isOptiScalerAvailable, value);
    }

    /// <summary>启动时解锁帧率（原神）。按游戏记</summary>
    private bool _useFpsUnlock;
    public bool UseFpsUnlock
    {
        get => _useFpsUnlock;
        set
        {
            if (SetProperty(ref _useFpsUnlock, value))
            {
                NotifyLaunchModeChanged();
            }
        }
    }

    /// <summary>帧率解锁目标值（fps），范围 60-1000。按游戏记（默认 119 = 安全上限，见 AppConfig）</summary>
    private int _fpsUnlockTarget = AppConfig.FpsUnlockWarnThreshold;
    public int FpsUnlockTarget
    {
        get => _fpsUnlockTarget;
        set => SetProperty(ref _fpsUnlockTarget, Math.Clamp(value, 60, 1000));
    }

    /// <summary>帧率解锁只支持原神</summary>
    public Visibility FpsUnlockVisibility
        => CurrentGameId is { GameBiz.Game: GameBiz.hk4e }
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void UpdateFpsUnlockVisibility()
    {
        OnPropertyChanged(nameof(FpsUnlockVisibility));

        if (FpsUnlockVisibility != Visibility.Visible && _useFpsUnlock)
        {
            UseFpsUnlock = false;
        }
    }

    /// <summary>
    /// 能不能勾「启用XXMI」：
    /// <list type="bullet">
    /// <item>火箭模式独占 GIMI —— 一律不行。</item>
    /// <item>注入模式下 Hub 不替用户启动游戏，只有 XXMI **手动模式**能共存：它只把 XXMI 注入器唤起来等进程
    /// （<c>-x &lt;importer&gt; -n</c>），游戏由用户自己的启动器拉起，两个注入器都只是"等"。</item>
    /// <item>XXMI 官方模式是 XXMI 自己 CreateProcess，Hub 拿不到父句柄 —— 和注入模式直接冲突。</item>
    /// </list>
    /// </summary>
    public bool CanUseXxmiInject => !UseRocket && (!UseInjectMode || IsXxmiManualLaunchMode);

    /// <summary>XXMI 启动模式是不是「手动」（只唤起注入器，不代管游戏进程）</summary>
    private bool IsXxmiManualLaunchMode
        => CurrentGameId is { } gameId && AppConfig.GetXxmiLaunchMode(gameId) == XxmiLaunchMode.Manual;

    /// <summary>XXMI 设置按钮能不能点：火箭模式下不能（注入模式下仍然可以，用来把官方模式改成手动模式）</summary>
    public bool CanConfigureXxmi => !UseRocket;

    /// <summary>XXMI 支持这个游戏才显示「启用XXMI」（不支持的游戏显示它没有意义）</summary>
    public Visibility XxmiInjectVisibility
        => CurrentGameId is { } gameId
           && Features.Xxmi.XxmiLocator.SupportsGame(gameId.GameBiz.Value, _currentGameEntry?.DisplayName)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void UpdateXxmiInjectVisibility()
    {
        OnPropertyChanged(nameof(XxmiInjectVisibility));
        OnPropertyChanged(nameof(RocketVisibility));
        OnPropertyChanged(nameof(CanUseRocket));
        OnPropertyChanged(nameof(CanConfigureXxmi));

        if (XxmiInjectVisibility != Visibility.Visible && _useXxmiInject)
        {
            UseXxmiInject = false;
        }
    }

    private bool _useRocket;
    public bool UseRocket
    {
        get => _useRocket;
        set
        {
            if (value && UseXxmiInject) value = false;
            if (SetProperty(ref _useRocket, value))
            {
                IsRocketMode = false;
                if (value) UseXxmiInject = false;
                OnPropertyChanged(nameof(RocketInstructionVisibility));
                OnPropertyChanged(nameof(IsRocketMode));
                OnPropertyChanged(nameof(CanUseXxmiInject));
                NotifyLaunchModeChanged();
            }
        }
    }

    /// <summary>启动时注入 XXMI（交给 XXMI Launcher 命令行，模型替换那套）。按游戏记</summary>
    private bool _useXxmiInject;
    public bool UseXxmiInject
    {
        get => _useXxmiInject;
        set
        {
            // Enforce exclusion in the setter too, not only the greyed UI.
            if (value && UseRocket) value = false;
            if (SetProperty(ref _useXxmiInject, value))
            {
                OnPropertyChanged(nameof(CanUseRocket));
        OnPropertyChanged(nameof(CanConfigureXxmi));
                NotifyLaunchModeChanged();
            }
        }
    }



    private bool _isHoYoShadeInstalled;
    public bool IsHoYoShadeInstalled
    {
        get => _isHoYoShadeInstalled;
        set => SetProperty(ref _isHoYoShadeInstalled, value);
    }
    
    private string _hoYoShadeVersion;
    public string HoYoShadeVersion
    {
        get => _hoYoShadeVersion;
        set => SetProperty(ref _hoYoShadeVersion, value);
    }

    private bool _isOpenHoYoShadeInstalled;
    public bool IsOpenHoYoShadeInstalled
    {
        get => _isOpenHoYoShadeInstalled;
        set => SetProperty(ref _isOpenHoYoShadeInstalled, value);
    }
    
    private string _openHoYoShadeVersion;
    public string OpenHoYoShadeVersion
    {
        get => _openHoYoShadeVersion;
        set => SetProperty(ref _openHoYoShadeVersion, value);
    }

    // Blender plugin properties
    private bool _launchGenshinBlenderPlugin;
    public bool LaunchGenshinBlenderPlugin
    {
        get => _launchGenshinBlenderPlugin;
        set
        {
            if (SetProperty(ref _launchGenshinBlenderPlugin, value))
            {
                if (value)
                {
                    // 保存当前的启动选项
                    _previousEnableGameLaunch = _enableGameLaunch;
                    _previousUseStarwardLauncher = _useStarwardLauncher;
                    
                    // 取消并禁用两个启动选项
                    _enableGameLaunch = false;
                    _useStarwardLauncher = false;
                    OnPropertyChanged(nameof(EnableGameLaunch));
                    OnPropertyChanged(nameof(UseStarwardLauncher));
                    
                    // 如果当前是"定位游戏"状态，改为"启动游戏"状态
                    if (GameState == GameState.InstallGame)
                    {
                        GameState = GameState.StartGame;
                    }
                    
                    UpdateGameLaunchCheckboxState();
                    _logger.LogInformation("LaunchGenshinBlenderPlugin enabled, both EnableGameLaunch and UseStarwardLauncher disabled, GameState set to StartGame");
                }
                else
                {
                    // 恢复之前的启动选项
                    UpdateGameLaunchCheckboxState();
                    
                    if (_previousUseStarwardLauncher)
                    {
                        _useStarwardLauncher = true;
                        OnPropertyChanged(nameof(UseStarwardLauncher));
                    }
                    else if (_previousEnableGameLaunch)
                    {
                        _enableGameLaunch = true;
                        OnPropertyChanged(nameof(EnableGameLaunch));
                    }
                    
                    // 取消Blender插件时，重新检查游戏版本
                    CheckGameVersion();
                    
                    _logger.LogInformation("LaunchGenshinBlenderPlugin disabled, restored previous launch option");
                }
                NotifyLaunchModeChanged();
            }
        }
    }

    private bool _launchZZZBlenderPlugin;
    public bool LaunchZZZBlenderPlugin
    {
        get => _launchZZZBlenderPlugin;
        set
        {
            if (SetProperty(ref _launchZZZBlenderPlugin, value))
            {
                if (value)
                {
                    // 保存当前的启动选项
                    _previousEnableGameLaunch = _enableGameLaunch;
                    _previousUseStarwardLauncher = _useStarwardLauncher;
                    
                    // 取消并禁用两个启动选项
                    _enableGameLaunch = false;
                    _useStarwardLauncher = false;
                    OnPropertyChanged(nameof(EnableGameLaunch));
                    OnPropertyChanged(nameof(UseStarwardLauncher));
                    
                    // 如果当前是"定位游戏"状态，改为"启动游戏"状态
                    if (GameState == GameState.InstallGame)
                    {
                        GameState = GameState.StartGame;
                    }
                    
                    UpdateGameLaunchCheckboxState();
                    _logger.LogInformation("LaunchZZZBlenderPlugin enabled, both EnableGameLaunch and UseStarwardLauncher disabled, GameState set to StartGame");
                }
                else
                {
                    // 恢复之前的启动选项
                    UpdateGameLaunchCheckboxState();
                    
                    if (_previousUseStarwardLauncher)
                    {
                        _useStarwardLauncher = true;
                        OnPropertyChanged(nameof(UseStarwardLauncher));
                    }
                    else if (_previousEnableGameLaunch)
                    {
                        _enableGameLaunch = true;
                        OnPropertyChanged(nameof(EnableGameLaunch));
                    }
                    
                    // 取消Blender插件时，重新检查游戏版本
                    CheckGameVersion();
                    
                    _logger.LogInformation("LaunchZZZBlenderPlugin disabled, restored previous launch option");
                }
                NotifyLaunchModeChanged();
            }
        }
    }

    private bool _useStarwardLauncher;
    public bool UseStarwardLauncher
    {
        get => _useStarwardLauncher;
        set
        {
            if (SetProperty(ref _useStarwardLauncher, value))
            {
                if (value)
                {
                    // 取消勾选 EnableGameLaunch
                    if (_enableGameLaunch)
                    {
                        _enableGameLaunch = false;
                        OnPropertyChanged(nameof(EnableGameLaunch));
                    }
                    
                    // 如果当前是"定位游戏"状态，改为"启动游戏"状态
                    if (GameState == GameState.InstallGame)
                    {
                        GameState = GameState.StartGame;
                    }
                    
                    _logger.LogInformation("UseStarwardLauncher enabled, EnableGameLaunch disabled, GameState set to StartGame");
                }
                else
                {
                    // 取消Starward时，重新检查游戏版本
                    CheckGameVersion();
                }
                NotifyLaunchModeChanged();
            }
        }
    }

    private void NotifyLaunchModeChanged()
    {
        // A change after preparation must not retain a stale "ready/waiting" label.
        IsRocketMode = false;
        OnPropertyChanged(nameof(ShouldEnableStartButton));
        OnPropertyChanged(nameof(IsShaderOnlyLaunchMode));
        OnPropertyChanged(nameof(IsShadeInjectMode));
        OnPropertyChanged(nameof(IsWaitProcessMode));
        OnPropertyChanged(nameof(IsRocketMode));
        OnPropertyChanged(nameof(CanUseXxmiInject));
        SaveLaunchOptionsForCurrentClient();
        UpdateOptiScalerConflict();
    }


    private void LoadLaunchOptionsForCurrentClient()
    {
        if (CurrentGameId is null)
        {
            return;
        }

        _isApplyingSavedLaunchOptions = true;
        IsRocketMode = false;
        try
        {
            // 启动游戏：界面上那条已经藏了，默认就是启动 —— 不读老设置（老设置里关过的用户不该被卡住）
            bool enableGameLaunch = true;
            bool useStarwardLauncher = AppConfig.GetUseStarwardLaunchOption(CurrentGameId);
            bool useHoYoShade = AppConfig.GetUseHoYoShadeLaunchOption(CurrentGameId);
            bool useOpenHoYoShade = AppConfig.GetUseOpenHoYoShadeLaunchOption(CurrentGameId);
            bool launchGenshinBlenderPlugin = AppConfig.GetLaunchGenshinBlenderPluginOption(CurrentGameId);
            bool launchZZZBlenderPlugin = AppConfig.GetLaunchZZZBlenderPluginOption(CurrentGameId);
            bool useModules = AppConfig.GetUseModulesLaunchOption(CurrentGameId);
            bool useOptiScaler = AppConfig.GetUseOptiScalerLaunchOption(CurrentGameId);
            bool useXxmiInject = AppConfig.GetUseXxmiInjectLaunchOption(CurrentGameId);
            bool useRocket = AppConfig.GetUseRocketLaunchOption(CurrentGameId);
            bool useFpsUnlock = AppConfig.GetUseFpsUnlockLaunchOption(CurrentGameId);
            int fpsUnlockTarget = AppConfig.GetFpsUnlockTarget(CurrentGameId);

            if (useHoYoShade && useOpenHoYoShade)
            {
                useOpenHoYoShade = false;
            }

            if (enableGameLaunch && useStarwardLauncher)
            {
                useStarwardLauncher = false;
            }

            if (launchGenshinBlenderPlugin || launchZZZBlenderPlugin)
            {
                enableGameLaunch = false;
                useStarwardLauncher = false;
            }

            _enableGameLaunch = enableGameLaunch;
            _useStarwardLauncher = useStarwardLauncher;
            _useHoYoShade = useHoYoShade;
            _useOpenHoYoShade = useOpenHoYoShade;
            _launchGenshinBlenderPlugin = launchGenshinBlenderPlugin;
            _launchZZZBlenderPlugin = launchZZZBlenderPlugin;
            _useModules = useModules;
            _useOptiScaler = useOptiScaler;
            // Normalize saved conflicts independently of the previous page state.
            _useRocket = useRocket && CurrentGameId is { GameBiz.Game: GameBiz.hk4e };
            _useXxmiInject = useXxmiInject && !_useRocket;
            _useFpsUnlock = useFpsUnlock;
            _fpsUnlockTarget = fpsUnlockTarget;

            OnPropertyChanged(nameof(EnableGameLaunch));
            OnPropertyChanged(nameof(UseStarwardLauncher));
            OnPropertyChanged(nameof(UseHoYoShade));
            OnPropertyChanged(nameof(UseOpenHoYoShade));
            OnPropertyChanged(nameof(LaunchGenshinBlenderPlugin));
            OnPropertyChanged(nameof(LaunchZZZBlenderPlugin));
            OnPropertyChanged(nameof(UseModules));
            OnPropertyChanged(nameof(UseOptiScaler));
            OnPropertyChanged(nameof(UseXxmiInject));
            OnPropertyChanged(nameof(UseRocket));
            OnPropertyChanged(nameof(IsRocketMode));
            OnPropertyChanged(nameof(RocketVisibility));
            OnPropertyChanged(nameof(CanUseRocket));
        OnPropertyChanged(nameof(CanConfigureXxmi));
            OnPropertyChanged(nameof(RocketInstructionVisibility));
            OnPropertyChanged(nameof(UseFpsUnlock));
            OnPropertyChanged(nameof(FpsUnlockTarget));
            OnPropertyChanged(nameof(XxmiInjectVisibility));
            OnPropertyChanged(nameof(CanUseXxmiInject));
            OnPropertyChanged(nameof(FpsUnlockVisibility));

            UpdateGameLaunchCheckboxState();

            // 注入模式：跟着当前客户端走，也存进游戏条目
            LoadInjectModeForCurrentClient();

            // 鸣潮：HoYoShade 那两项既不显示也不用（改成「启用 ReShade」= 本体目录 dxgi.dll）。
            // 两边同时开就是同一个进程里两份 ReShade（抢钩子 / 崩），所以这里直接清零，落库时写回去。
            OnPropertyChanged(nameof(HoYoShadeRowVisibility));
            OnPropertyChanged(nameof(SkipShadeInjectorVisibility));
            OnPropertyChanged(nameof(VanillaReShadeVisibility));

            if (IsWutheringWavesCurrent)
            {
                _useHoYoShade = false;
                _useOpenHoYoShade = false;
                OnPropertyChanged(nameof(UseHoYoShade));
                OnPropertyChanged(nameof(UseOpenHoYoShade));
                OnPropertyChanged(nameof(IsShadeLaunchSelected));
                OnPropertyChanged(nameof(CanSkipShadeInjector));
            }

            LoadVanillaReShadeForCurrentClient();

            // AI 插帧（NVIDIA Smooth Motion）：读驱动里这个游戏条目的开关状态
            LoadSmoothMotionForCurrentClient();
        }
        finally
        {
            _isApplyingSavedLaunchOptions = false;
        }

        NotifyLaunchModeChanged();
    }

    private void SaveLaunchOptionsForCurrentClient()
    {
        if (_isApplyingSavedLaunchOptions || CurrentGameId is null)
        {
            return;
        }

        AppConfig.SetEnableGameLaunchOption(CurrentGameId, _enableGameLaunch);
        AppConfig.SetUseStarwardLaunchOption(CurrentGameId, _useStarwardLauncher);
        AppConfig.SetUseHoYoShadeLaunchOption(CurrentGameId, _useHoYoShade);
        AppConfig.SetUseOpenHoYoShadeLaunchOption(CurrentGameId, _useOpenHoYoShade);
        AppConfig.SetLaunchGenshinBlenderPluginOption(CurrentGameId, _launchGenshinBlenderPlugin);
        AppConfig.SetLaunchZZZBlenderPluginOption(CurrentGameId, _launchZZZBlenderPlugin);
        AppConfig.SetUseModulesLaunchOption(CurrentGameId, _useModules);
        AppConfig.SetUseOptiScalerLaunchOption(CurrentGameId, _useOptiScaler);
        AppConfig.SetUseXxmiInjectLaunchOption(CurrentGameId, _useXxmiInject);
        AppConfig.SetUseRocketLaunchOption(CurrentGameId, _useRocket);
        AppConfig.SetUseFpsUnlockLaunchOption(CurrentGameId, _useFpsUnlock);
        AppConfig.SetFpsUnlockTarget(CurrentGameId, _fpsUnlockTarget);
    }

    private bool _isStarwardProtocolAvailable;
    public bool IsStarwardProtocolAvailable
    {
        get => _isStarwardProtocolAvailable;
        set => SetProperty(ref _isStarwardProtocolAvailable, value);
    }

    private bool _isGameLaunchCheckboxEnabled = true;
    public bool IsGameLaunchCheckboxEnabled
    {
        get => _isGameLaunchCheckboxEnabled;
        set => SetProperty(ref _isGameLaunchCheckboxEnabled, value);
    }

    private void UpdateGameLaunchCheckboxState()
    {
        // 只有在没有Blender插件被选中时,两个启动选项才可用
        bool blenderPluginActive = LaunchGenshinBlenderPlugin || LaunchZZZBlenderPlugin;
        IsGameLaunchCheckboxEnabled = !blenderPluginActive;
        
        // 同时更新 Starward 启动器选项的可用状态和可见状态
        OnPropertyChanged(nameof(IsStarwardLauncherCheckboxEnabled));
        OnPropertyChanged(nameof(IsStarwardLauncherCheckboxVisible));
    }
    
    /// <summary>
    /// Starward启动器选项是否可用
    /// 条件: 没有Blender插件被选中 AND Starward协议可用 AND  设置中启用了Starward启动器 AND 不是Beta客户端
    /// </summary>
    public bool IsStarwardLauncherCheckboxEnabled
    { 
        get
        {
            // Beta客户端不支持Starward启动器
            bool isBetaClient = CurrentGameBiz.IsBetaServer();
            
            bool result = !LaunchGenshinBlenderPlugin && 
                          !LaunchZZZBlenderPlugin && 
                          IsStarwardProtocolAvailable && 
                          AppConfig.UseStarwardLauncher &&
                          !isBetaClient;
            
            _logger.LogInformation("IsStarwardLauncherCheckboxEnabled: {Result} (Blender: {Blender}, Protocol: {Protocol}, Setting: {Setting}, IsBeta: {IsBeta})", 
                result, 
                LaunchGenshinBlenderPlugin || LaunchZZZBlenderPlugin,
                IsStarwardProtocolAvailable,
                AppConfig.UseStarwardLauncher,
                isBetaClient);
            
            return result;
        }
    }

    /// <summary>
    /// Starward启动器选项是否可见
    /// 公开客户端始终可见，Beta客户端隐藏
    /// </summary>
    public Visibility IsStarwardLauncherCheckboxVisible
    {
        get
        {
            bool isBetaClient = CurrentGameBiz.IsBetaServer();
            return isBetaClient ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private bool _isGenshinBlenderPluginConfigured;
    public bool IsGenshinBlenderPluginConfigured
    {
        get => _isGenshinBlenderPluginConfigured;
        set => SetProperty(ref _isGenshinBlenderPluginConfigured, value);
    }

    private bool _isZZZBlenderPluginConfigured;
    public bool IsZZZBlenderPluginConfigured
    {
        get => _isZZZBlenderPluginConfigured;
        set => SetProperty(ref _isZZZBlenderPluginConfigured, value);
    }

    private Visibility _isGenshinBlenderPluginVisible = Visibility.Collapsed;
    public Visibility IsGenshinBlenderPluginVisible
    {
        get => _isGenshinBlenderPluginVisible;
        set => SetProperty(ref _isGenshinBlenderPluginVisible, value);
    }

    private Visibility _isZZZBlenderPluginVisible = Visibility.Collapsed;
    public Visibility IsZZZBlenderPluginVisible
    {
        get => _isZZZBlenderPluginVisible;
        set => SetProperty(ref _isZZZBlenderPluginVisible, value);
    }

    private void ComboBox_LaunchMode_SelectionChanged(object sender, Microsoft.UI.Xaml.Controls.SelectionChangedEventArgs e)
    {
        // Removed - no longer using ComboBox
    }



    [RelayCommand]
    private async Task ClickStartGameButtonAsync()
    {
        await Task.Delay(1);
        if (UseRocket)
        {
            await PrepareRocketAsync();
            return;
        }
        switch (GameState)
        {
            case GameState.None:
                break;
            case GameState.StartGame:
                await StartGameAsync();
                break;
            case GameState.GameIsRunning:
            case GameState.InstallGame:
                await InstallGameAsync();
                break;
            case GameState.Installing:
            case GameState.UpdateGame:
                await UpdateGameAsync();
                break;
            case GameState.UpdatePlugin:
            case GameState.ResumeDownload:
                await ResumeDownloadAsync();
                break;
            case GameState.ComingSoon:
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// 「关闭游戏」：游戏运行中，点启动按钮左边的小停止按钮直接结束游戏进程（用户要求）。
    /// 收尾走游戏自然退出的同一套清理：停帧率解锁、发 GameExitedMessage（背景/视频回来）、刷新状态。
    /// </summary>
    [RelayCommand]
    private async Task CloseGameAsync()
    {
        if (GameState != GameState.GameIsRunning || IsClosingGame)
        {
            return;
        }

        IsClosingGame = true;
        try
        {
            Process? game = GameProcess is { HasExited: false } tracked
                ? tracked
                : await _gameLauncherService.GetGameProcessAsync(CurrentGameId);

            if (game is null || game.HasExited)
            {
                InAppToast.MainWindow?.Information("关闭游戏", "游戏已经不在运行了。", 5000);
            }
            else
            {
                string name = game.ProcessName;
                bool killed = false;
                try
                {
                    game.Kill(entireProcessTree: true);
                    killed = true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Kill game process failed");
                }

                if (!killed)
                {
                    InAppToast.MainWindow?.Error("关闭游戏", $"结束 {name} 被拒绝（可能被反作弊/权限拦住），请手动退出游戏。", 10000);
                }
                else
                {
                    try
                    {
                        await game.WaitForExitAsync(new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
                        InAppToast.MainWindow?.Success("关闭游戏", $"已关闭 {name}。", 5000);
                    }
                    catch (OperationCanceledException)
                    {
                        InAppToast.MainWindow?.Error("关闭游戏", $"{name} 10 秒内没有退出（可能被反作弊保护），请手动关闭。", 10000);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Close game failed");
            InAppToast.MainWindow?.Error("关闭游戏", "关闭失败：" + ex.Message, 10000);
        }
        finally
        {
            GameProcess = null;
            StopFpsUnlocker();
            // 背景回来 —— 和游戏自然退出同一信号
            WeakReferenceMessenger.Default.Send(new GameExitedMessage());
            GameState = GameState.StartGame;
            IsClosingGame = false;
            // 让 CheckGameVersion 把状态再算一遍（安装/更新提示照常给出）
            DispatcherQueue.TryEnqueue(CheckGameVersion);
        }
    }






































    /// <summary>帧率解锁右边的齿轮：直接跳到「游戏设置」页去改目标帧率。</summary>
    private void Button_FpsUnlockSettings_Click(object sender, RoutedEventArgs e)
    {
        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(GameSettingPage)));
    }

}
