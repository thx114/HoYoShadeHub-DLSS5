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



    #region 注入模式（只影响当前这个游戏）

    /// <summary>当前游戏的条目 + 游戏表（注入模式的开关存在里面）</summary>
    private GameDiscoveryService? _gameDiscovery;
    private GameEntry? _currentGameEntry;

    private bool _useInjectMode;

    /// <summary>
    /// 「注入模式」开关。**按游戏存**（见 docs/GAMES-AND-INJECT.md §5）：
    /// 勾上之后点开始游戏会走 inject.exe，而不是等 Hub 直接把游戏拉起来。
    /// </summary>
    public bool UseInjectMode
    {
        get => _useInjectMode;
        set
        {
            if (!SetProperty(ref _useInjectMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsShadeInjectMode));
            OnPropertyChanged(nameof(IsWaitProcessMode));
            OnPropertyChanged(nameof(CanUseXxmiInject));

            // 注入模式：游戏由用户自己拉起来，而 XXMI 必须由它自己启动游戏 —— 两者冲突，
            // 所以一开注入模式就把「启用XXMI」关掉并提醒（用户要求）
            if (value && UseXxmiInject && !_isApplyingSavedLaunchOptions)
            {
                UseXxmiInject = false;
                DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Warning("注入模式",
                    "注入模式下不会替你启动游戏，而 XXMI 要自己把游戏拉起来 —— 两者不能同时用，已把「启用XXMI」关掉。", 10000));
            }

            if (_isApplyingSavedLaunchOptions || _gameDiscovery is null || _currentGameEntry is null)
            {
                return;
            }

            try
            {
                GameCatalog.SetInjectMode(_gameDiscovery, _currentGameEntry, value);
                _logger.LogInformation("Inject mode for {Game}: {Value}", _currentGameEntry.DisplayName, value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save inject mode");
            }
        }
    }

    public bool CanUseInjectMode => _currentGameEntry is not null;

    /// <summary>读当前游戏的注入模式（跟着客户端切换走）</summary>
    private void LoadInjectModeForCurrentClient()
    {
        try
        {
            _gameDiscovery = GameCatalog.CreateService();
            _currentGameEntry = GameCatalog.GetOrCreate(_gameDiscovery, CurrentGameId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load game entry");
            _gameDiscovery = null;
            _currentGameEntry = null;
        }

        UseInjectMode = _currentGameEntry?.UseInjectMode ?? false;
        OnPropertyChanged(nameof(CanUseInjectMode));

        // 老配置里注入模式和 XXMI 都开着：注入模式优先，把 XXMI 关掉
        if (UseInjectMode && _useXxmiInject)
        {
            UseXxmiInject = false;
        }
    }

    /// <summary>注入模式开关（用户要求：别再弹「注入模式」介绍提示了，说明都在控件 tooltip 里）</summary>
    private void CheckBox_InjectMode_Changed(object sender, RoutedEventArgs e)
    {
    }



    #endregion



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

            // XXMI：只对 XXMI 支持的游戏显示「启用XXMI」，注入模式下点不了
            UpdateXxmiInjectVisibility();

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
            if (UseStarwardLauncher || LaunchGenshinBlenderPlugin || LaunchZZZBlenderPlugin || UseHoYoShade || UseOpenHoYoShade)
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
            }
        }
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

    /// <summary>帧率解锁目标值（fps），范围 60-1000。按游戏记</summary>
    private int _fpsUnlockTarget = 120;
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

    /// <summary>注入模式下不能同时用 XXMI（XXMI 要自己把游戏拉起来）</summary>
    public bool CanUseXxmiInject => !UseInjectMode;

    /// <summary>XXMI 支持这个游戏才显示「启用XXMI」（不支持的游戏显示它没有意义）</summary>
    public Visibility XxmiInjectVisibility
        => CurrentGameId is { } gameId
           && Features.Xxmi.XxmiLocator.SupportsGame(gameId.GameBiz.Value, _currentGameEntry?.DisplayName)
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void UpdateXxmiInjectVisibility()
    {
        OnPropertyChanged(nameof(XxmiInjectVisibility));

        if (XxmiInjectVisibility != Visibility.Visible && _useXxmiInject)
        {
            UseXxmiInject = false;
        }
    }

    /// <summary>启动时注入 XXMI（交给 XXMI Launcher 命令行，模型替换那套）。按游戏记</summary>
    private bool _useXxmiInject;
    public bool UseXxmiInject
    {
        get => _useXxmiInject;
        set
        {
            if (SetProperty(ref _useXxmiInject, value))
            {
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
        OnPropertyChanged(nameof(ShouldEnableStartButton));
        OnPropertyChanged(nameof(IsShaderOnlyLaunchMode));
        OnPropertyChanged(nameof(IsShadeInjectMode));
        OnPropertyChanged(nameof(IsWaitProcessMode));
        SaveLaunchOptionsForCurrentClient();
        UpdateOptiScalerConflict();
    }

    #region OptiScaler 冲突提示（滚动跑马灯）

    private Storyboard? _optiScalerMarquee;

    /// <summary>HoYoShade / OpenHoYoShade 和 OptiScaler 同时勾上 → 显示滚动提示（用户要求）</summary>
    private void UpdateOptiScalerConflict()
    {
        if (Border_OptiScalerConflict is null)
        {
            return;
        }

        // OptiScaler：勾了「启用OptiScaler」+ 总开关开着 + 本游戏选过构建
        bool optiScalerOn = UseOptiScaler
                            && CurrentGameId is { } optiGameId
                            && AppConfig.GetSelectedOptiScalerDll(optiGameId) is not null;
        bool conflict = optiScalerOn && (UseHoYoShade || UseOpenHoYoShade);
        Border_OptiScalerConflict.Visibility = conflict ? Visibility.Visible : Visibility.Collapsed;

        if (!conflict)
        {
            StopOptiScalerMarquee();
            return;
        }

        // 刚变可见时还没量过尺寸，等一帧再起动画
        DispatcherQueue.TryEnqueue(StartOptiScalerMarquee);
    }

    private void StartOptiScalerMarquee()
    {
        if (Border_OptiScalerConflict is null || Border_OptiScalerConflict.Visibility != Visibility.Visible)
        {
            return;
        }

        StopOptiScalerMarquee();

        double viewport = Grid_OptiScalerConflict.ActualWidth;
        double text = TextBlock_OptiScalerConflict.ActualWidth;

        if (viewport <= 0 || text <= 0)
        {
            return;   // 还没量到，SizeChanged 会再来一次
        }

        // 把画布裁在提示框里，否则跑马灯会溢出去
        Grid_OptiScalerConflict.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, viewport, Grid_OptiScalerConflict.ActualHeight),
        };

        if (text <= viewport)
        {
            return;   // 放得下就不滚
        }

        var animation = new DoubleAnimation
        {
            From = viewport,
            To = -text,
            Duration = new Duration(TimeSpan.FromSeconds(Math.Max(6d, text / 40d))),
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Storyboard.SetTarget(animation, OptiScalerMarqueeTransform);
        Storyboard.SetTargetProperty(animation, "X");

        _optiScalerMarquee = new Storyboard();
        _optiScalerMarquee.Children.Add(animation);
        _optiScalerMarquee.Begin();
    }

    private void StopOptiScalerMarquee()
    {
        try
        {
            _optiScalerMarquee?.Stop();
        }
        catch
        {
            // ignore
        }

        _optiScalerMarquee = null;

        if (OptiScalerMarqueeTransform is not null)
        {
            OptiScalerMarqueeTransform.X = 0;
        }
    }

    private void OptiScalerMarquee_SizeChanged(object sender, SizeChangedEventArgs e) => StartOptiScalerMarquee();

    #endregion

    private void LoadLaunchOptionsForCurrentClient()
    {
        if (CurrentGameId is null)
        {
            return;
        }

        _isApplyingSavedLaunchOptions = true;
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
            _useXxmiInject = useXxmiInject;
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
            OnPropertyChanged(nameof(UseFpsUnlock));
            OnPropertyChanged(nameof(FpsUnlockTarget));
            OnPropertyChanged(nameof(XxmiInjectVisibility));
            OnPropertyChanged(nameof(CanUseXxmiInject));
            OnPropertyChanged(nameof(FpsUnlockVisibility));

            UpdateGameLaunchCheckboxState();

            // 注入模式：跟着当前客户端走，也存进游戏条目
            LoadInjectModeForCurrentClient();
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



    #region Game Server


    private List<GameServerConfig>? _gameServers;
    public List<GameServerConfig>? GameServers
    {
        get => _gameServers;
        set => SetProperty(ref _gameServers, value);
    }

    [ObservableProperty]
    public partial GameServerConfig? SelectedGameServer { get; set; }
    partial void OnSelectedGameServerChanged(GameServerConfig? oldValue, GameServerConfig? newValue)
    {
        if (oldValue is not null && newValue is not null)
        {
            AppConfig.LastGameIdOfBH3Global = newValue.GameId;
            WeakReferenceMessenger.Default.Send(new BH3GlobalGameServerChangedMessage(newValue.GameId));
        }
    }


    /// <summary>
    /// 初始化区服选项，仅崩坏三国际服使用
    /// </summary>
    /// <returns></returns>
    private async Task InitializeGameServerAsync()
    {
        try
        {
            // 自定义游戏 / Hub 不认识的：HoYoPlay 那边没有区服信息
            if (_currentGameEntry?.IsCustom == true || CurrentGameBiz.Value.StartsWith("custom_", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            GameInfo? gameInfo;
            if (CurrentGameBiz == GameBiz.bh3_global)
            {
                gameInfo = await _hoYoPlayService.GetGameInfoAsync(GameId.FromGameBiz(GameBiz.bh3_global)!);
            }
            else
            {
                gameInfo = await _hoYoPlayService.GetGameInfoAsync(CurrentGameId);
            }
            if (gameInfo?.GameServerConfigs?.Count > 0)
            {
                GameServers = gameInfo.GameServerConfigs;
                if (GameServers.FirstOrDefault(x => x.GameId == CurrentGameId.Id) is GameServerConfig config)
                {
                    SelectedGameServer = config;
                }
                else
                {
                    SelectedGameServer = GameServers.FirstOrDefault();
                    if (SelectedGameServer is not null)
                    {
                        CurrentGameId.Id = SelectedGameServer.GameId;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize game server");
        }
    }



    #endregion




    #region Game Version


    private string? _gameInstallPath;
    public string? GameInstallPath
    {
        get => _gameInstallPath;
        set => SetProperty(ref _gameInstallPath, value);
    }

    /// <summary>
    /// 可移动存储设备提示
    /// </summary>
    private bool _isInstallPathRemovableTipEnabled;
    public bool IsInstallPathRemovableTipEnabled
    {
        get => _isInstallPathRemovableTipEnabled;
        set
        {
            if (SetProperty(ref _isInstallPathRemovableTipEnabled, value))
            {
                OnPropertyChanged(nameof(InstalledLocateGameEnabled));
            }
        }
    }

    /// <summary>
    /// 是否显示 DX12 选项
    /// </summary>
    private bool _isDX12OptionVisible;
    public bool IsDX12OptionVisible
    {
        get => _isDX12OptionVisible;
        set => SetProperty(ref _isDX12OptionVisible, value);
    }


    /// <summary>
    /// DX12 配置
    /// </summary>
    private GameDXConfig? _dxConfig;


    /// <summary>
    /// 启用 DX12
    /// </summary>
    private bool _enableDX12;
    public bool EnableDX12
    {
        get => _enableDX12;
        set
        {
            if (SetProperty(ref _enableDX12, value))
            {
                AppConfig.SetEnableDX12(CurrentGameBiz, value);
            }
        }
    }

    /// <summary>
    /// 已安装？定位游戏
    /// </summary>
    public bool InstalledLocateGameEnabled => GameState is GameState.InstallGame && !IsInstallPathRemovableTipEnabled;

    /// <summary>
    /// 预下载按钮是否可用
    /// </summary>
    private bool _isPredownloadButtonEnabled;
    public bool IsPredownloadButtonEnabled
    {
        get => _isPredownloadButtonEnabled;
        set => SetProperty(ref _isPredownloadButtonEnabled, value);
    }

    /// <summary>
    /// 预下载是否完成
    /// </summary>
    private bool _isPredownloadFinished;
    public bool IsPredownloadFinished
    {
        get => _isPredownloadFinished;
        set => SetProperty(ref _isPredownloadFinished, value);
    }


    private Version? localGameVersion;


    private bool isGameExeExists;



    private async void CheckGameVersion()
    {
        try
        {
            // 自定义游戏：Hub 的数据库里没有它，安装目录 = exe 所在目录，状态直接给「开始游戏」
            if (_currentGameEntry is { IsCustom: true } customGame)
            {
                GameInstallPath = customGame.GameDirectory;
                IsInstallPathRemovableTipEnabled = false;
                GameState = GameState.StartGame;
                await CheckGameRunningAsync();
                return;
            }

            GameInstallPath = GameLauncherService.GetGameInstallPath(CurrentGameId, out bool storageRemoved);
            IsInstallPathRemovableTipEnabled = storageRemoved;
            
            // 如果选择了Starward启动器或Blender插件，即使游戏未定位也设置为StartGame状态
            if (UseStarwardLauncher || LaunchGenshinBlenderPlugin || LaunchZZZBlenderPlugin)
            {
                GameState = GameState.StartGame;
                await CheckGameRunningAsync();
                return;
            }
            
            // 常规启动模式：需要检查游戏安装路径
            if (GameInstallPath is null || storageRemoved)
            {
                GameState = GameState.InstallGame;
                return;
            }
            isGameExeExists = await _gameLauncherService.IsGameExeExistsAsync(CurrentGameId);
            localGameVersion = await _gameLauncherService.GetLocalGameVersionAsync(CurrentGameId);
            // 正式服：必须 exe 在 + 能读出本地版本；
            // Beta / 内测 / 创作者体验服经常没有 config.ini（读不出版本），只要 exe 在就允许启动
            bool canStart = isGameExeExists && (localGameVersion != null || CurrentGameBiz.IsBetaServer());

            if (canStart)
            {
                GameState = GameState.StartGame;
            }
            else
            {
                GameState = GameState.InstallGame;
                return;
            }
            _ = CheckDX12ConfigAsync();
            await CheckGameRunningAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check game version");
        }
    }



    /// <summary>
    /// 检查 DX12 配置
    /// </summary>
    private async Task CheckDX12ConfigAsync()
    {
        try
        {
            IsDX12OptionVisible = false;
            EnableDX12 = AppConfig.GetEnableDX12(CurrentGameBiz);

            List<GameDXConfig> dxConfigs = await _hoYoPlayService.GetGameDXConfigsAsync([CurrentGameId]);
            _dxConfig = dxConfigs?.FirstOrDefault(x => x.GameId == CurrentGameId);

            bool gameSupportsDX12 = _dxConfig?.EnableDXSwitch is true || await _hoYoPlayService.IsGameSupportDX12Async(CurrentGameId);
            bool ignoreCheck = gameSupportsDX12 && AppConfig.GetIgnoreDX12Check(CurrentGameBiz);

            if (EnableDX12 && (_dxConfig?.EnableDXSwitch is true || ignoreCheck))
            {
                IsDX12OptionVisible = true;
            }

            if (_dxConfig?.EnableDXSwitch is true || ignoreCheck)
            {
                IsDX12OptionVisible = true;
                if (_dxConfig is null || !_dxConfig.EnableDXSwitch)
                {
                    var refConfig = await _hoYoPlayService.GetGameDX12ReferenceConfigAsync(CurrentGameId);
                    if (_dxConfig is null)
                    {
                        _dxConfig = new GameDXConfig
                        {
                            GameId = CurrentGameId,
                            EnableDXSwitch = true,
                            CmdArgs = !string.IsNullOrWhiteSpace(refConfig?.CmdArgs) ? refConfig.CmdArgs : "-use-d3d12",
                            DX11PreviewImage = refConfig?.DX11PreviewImage ?? "",
                            DX12PreviewImage = refConfig?.DX12PreviewImage ?? "",
                            I18nIntro = !string.IsNullOrWhiteSpace(refConfig?.I18nIntro) ? refConfig.I18nIntro : Lang.GameLauncherPage_ForcedDX12Intro,
                        };
                    }
                    else
                    {
                        _dxConfig.EnableDXSwitch = true;
                        if (string.IsNullOrWhiteSpace(_dxConfig.CmdArgs))
                        {
                            _dxConfig.CmdArgs = !string.IsNullOrWhiteSpace(refConfig?.CmdArgs) ? refConfig.CmdArgs : "-use-d3d12";
                        }
                        if (string.IsNullOrWhiteSpace(_dxConfig.DX11PreviewImage) && !string.IsNullOrWhiteSpace(refConfig?.DX11PreviewImage))
                        {
                            _dxConfig.DX11PreviewImage = refConfig.DX11PreviewImage;
                        }
                        if (string.IsNullOrWhiteSpace(_dxConfig.DX12PreviewImage) && !string.IsNullOrWhiteSpace(refConfig?.DX12PreviewImage))
                        {
                            _dxConfig.DX12PreviewImage = refConfig.DX12PreviewImage;
                        }
                        if (string.IsNullOrWhiteSpace(_dxConfig.I18nIntro))
                        {
                            _dxConfig.I18nIntro = !string.IsNullOrWhiteSpace(refConfig?.I18nIntro) ? refConfig.I18nIntro : Lang.GameLauncherPage_ForcedDX12Intro;
                        }
                    }
                }
            }
            else
            {
                IsDX12OptionVisible = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check DX12 config");
        }
    }


    /// <summary>
    /// 获取 DX12 启动参数
    /// </summary>
    /// <returns></returns>
    public string? GetDX12LaunchArgument()
    {
        if (EnableDX12 && _dxConfig is not null)
        {
            return _dxConfig.CmdArgs;
        }
        return null;
    }


    /// <summary>
    /// 显示 DX12 说明对话框
    /// </summary>
    private async void Hyperlink_DX12Intro_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        if (_dxConfig is not null)
        {
            await new DX12IntroDialog { GameDXConfig = _dxConfig, XamlRoot = this.XamlRoot }.ShowAsync();
        }
    }



    /// <summary>
    /// 定位游戏路径
    /// </summary>
    /// <returns></returns>
    private async Task LocateGameAsync()
    {
        try
        {
            string? folder = await FileDialogHelper.PickFolderAsync(this.XamlRoot);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                if (DriveHelper.GetDriveType(folder) is DriveType.Network && !new Uri(folder).IsUnc)
                {
                    InAppToast.MainWindow?.Warning(null, Lang.InstallGameDialog_MappedNetworkDrivesAreNotSupportedPleaseUseANetworkSharePathStartingWithDoubleBackslashes, 0);
                }
                else
                {
                    // 验证游戏exe是否存在
                    var exeName = await _gameLauncherService.GetGameExeNameAsync(CurrentGameId);

                    // 选成上层目录（例如 miHoYo Launcher 根目录）时，往里面看两层目录名再试一次
                    string? gameFolder = folder;
                    if (!File.Exists(Path.Combine(folder, exeName)))
                    {
                        GameFolderSearchResult nested = GameFolderLocator.Locate(folder, exeName);
                        if (nested.Found)
                        {
                            _logger.LogInformation("Game folder nested under {Picked}: {Found} (depth {Depth}, scanned {Scanned})",
                                folder, nested.Directory, nested.Depth, nested.ScannedDirectories);
                            InAppToast.MainWindow?.Information(null,
                                string.Format(Lang.GameLauncherSettingDialog_GameFoundInNestedFolder, nested.Directory), 6000);
                            gameFolder = nested.Directory;
                        }
                    }

                    if (gameFolder is null || !File.Exists(Path.Combine(gameFolder, exeName)))
                    {
                        // 游戏exe不存在，显示错误提示，不进行定位
                        InAppToast.MainWindow?.Warning(null, string.Format(Lang.GameLauncherSettingDialog_GameExeNotFoundInFolder, exeName), 5000);
                        _logger.LogWarning("Game exe not found in selected folder: {Path}, expected: {ExeName}", folder, exeName);
                        return;
                    }

                    // 验证成功，执行定位
                    GameLauncherService.ChangeGameInstallPath(CurrentGameId, gameFolder);
                    CheckGameVersion();
                    WeakReferenceMessenger.Default.Send(new GameInstallPathChangedMessage());

                    // 定位到的游戏固定到顶部（默认不会自动固定，是"定位过"才固定）
                    WeakReferenceMessenger.Default.Send(new PinGameBizMessage(CurrentGameBiz));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Locate game");
        }
    }



    /// <summary>
    /// 定位游戏路径
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="args"></param>
    private async void Hyperlink_LocateGame_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        await LocateGameAsync();
    }



    private void OnGameInstallPathChanged(object _, GameInstallPathChangedMessage message)
    {
        CheckGameVersion();
    }




    private void OnMainWindowStateChanged(object _, MainWindowStateChangedMessage message)
    {
        try
        {
            if (message.Activate && (message.ElapsedOver(TimeSpan.FromMinutes(10)) || message.IsCrossingHour))
            {
                CheckGameVersion();
            }
        }
        catch { }
    }



    private void OnRemovableStorageDeviceChanged(object _, RemovableStorageDeviceChangedMessage message)
    {
        try
        {
            CheckGameVersion();
        }
        catch { }
    }


    private void OnUseStarwardLauncherChanged(object _, UseStarwardLauncherChangedMessage message)
    {
        try
        {
            // 更新 IsStarwardLauncherCheckboxEnabled 属性
            OnPropertyChanged(nameof(IsStarwardLauncherCheckboxEnabled));
            
            // 如果设置被禁用，并且当前正在使用 Starward，则取消勾选
            if (!message.IsEnabled && UseStarwardLauncher)
            {
                UseStarwardLauncher = false;
            }
            
            _logger.LogInformation("UseStarwardLauncher setting changed to: {IsEnabled}", message.IsEnabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handle UseStarwardLauncherChanged message");
        }
    }



    #endregion




    #region Start Game




    private Timer processTimer;


    [ObservableProperty]
    private partial Process? GameProcess { get; set; }
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

            var service = new GamePluginService(
                _currentGameEntry,
                host,
                PluginHostLocator.AddonNameCachePath,
                GameCatalog.AddonCandidateNames(),
                GameCatalog.TagsOfAddonFile);

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
    /// 启用 OptiScaler 时启动前检查游戏目录自带的 dlssg：存在且版本不是 310.9 就提示。
    /// 用户确认后用构建目录里的 310.9 替换（旧文件改名 .bak）；拒绝则照常启动（多帧生成解锁不可用）。
    /// </summary>
    /// <returns>false 表示中止本次启动</returns>
    private async Task<bool> ConfirmGameDlssgAsync()
    {
        try
        {
            if (!UseOptiScaler || CurrentGameId is not { } gameId)
            {
                return true;
            }

            string? optiDll = AppConfig.GetSelectedOptiScalerDll(gameId);
            string? buildDirectory = Path.GetDirectoryName(optiDll);
            if (string.IsNullOrWhiteSpace(buildDirectory) || !Directory.Exists(buildDirectory))
            {
                return true;
            }

            string? installPath = GameInstallPath;
            if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            {
                return true;
            }

            // BFS 游戏目录放到后台，不在 UI 线程枚举
            string? gameDlssg = await Task.Run(() => OptiScalerRuntime.FindGameDlssg(installPath));
            if (gameDlssg is null || OptiScalerRuntime.IsUnlockDlssg(gameDlssg))
            {
                return true;
            }

            Version? version = OptiScalerRuntime.TryReadFileVersion(gameDlssg);
            string versionText = version is null ? "未知版本" : $"{version.Major}.{version.Minor}";

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "DLSSG 版本过低",
                Content = $"游戏目录里的 nvngx_dlssg.dll 是 {versionText}，不支持多帧生成解锁。\n\n" +
                          "是否使用 310.9 的 nvngx_dlssg.dll？（原文件会备份为 .bak）",
                PrimaryButtonText = "使用 310.9",
                CloseButtonText = "仍然启动",
                DefaultButton = ContentDialogButton.Primary,
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                _logger.LogInformation("Game dlssg {Version} kept by user, launch continues", versionText);
                return true;
            }

            string? error = OptiScalerRuntime.ReplaceGameDlssg(gameDlssg, buildDirectory);
            if (error is not null)
            {
                _logger.LogWarning("{Error}", error);
                InAppToast.MainWindow?.Error("DLSSG", error, 10000);
                return false;
            }

            _logger.LogInformation("Game dlssg replaced with 310.9: {Path}", gameDlssg);
            InAppToast.MainWindow?.Success("DLSSG", "已用 310.9 替换游戏目录的 nvngx_dlssg.dll。", 8000);
            return true;
        }
        catch (Exception ex)
        {
            // 检查本身出错不该拦着人玩游戏
            _logger.LogWarning(ex, "Check game dlssg before launch");
            return true;
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




    private void CheckGameExited()
    {
        try
        {
            if (GameProcess != null)
            {
                if (GameProcess.HasExited)
                {
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
        return processName;
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
        bool WaitForReady = false);

    /// <summary>
    /// 「额外注入 DLL」+「启动 OptiScaler」：等游戏进程出现后把 DLL LoadLibrary 进去。
    /// 用户要的是跟 HoYoShade 的注入器**同时**注入（比如 DLSS Enabler / OptiScaler）。
    /// 游戏是用户自己启动的，所以这里得等（最多 20 分钟；注入器一停就放弃）。
    /// </summary>
    /// <summary>
    /// 注入用的 DLL 名字可改（游戏会按名字认代理 dll）。名字与源文件不同就复制一份
    /// &lt;名字&gt;.dll 到同目录并注入它；名字相同 / 出错就原样返回。
    /// </summary>
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

    private void StartExtraDllInjection(string processName)
    {
        List<InjectDllSpec> specs = [];

        // ① 启动选项里勾的「启用模块」：左侧「模块」页里**这个游戏勾上的**那些（DLSS-NR on AMD 之类）
        if (UseModules && CurrentGameId is { } moduleGameId)
        {
            foreach ((string name, string path) in Features.Modules.ModuleRegistry.ResolveInjectionDlls(moduleGameId))
            {
                if (specs.All(s => !string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    bool waitReady = string.Equals(
                        Path.GetFileName(path),
                        OptiScalerRuntime.FsrBridgeDllName,
                        StringComparison.OrdinalIgnoreCase);
                    specs.Add(new InjectDllSpec(path, name, WaitForReady: waitReady));
                }
            }
        }

        // ② OptiScaler：启动选项勾了「启用OptiScaler」+「全局插件 → OptiScaler」的总开关开着
        //    + 这个游戏在「OptiScaler」页选过构建，三者都满足才注入
        if (UseOptiScaler && CurrentGameId is { } optiGameId)
        {
            string? optiScaler = AppConfig.GetSelectedOptiScalerDll(optiGameId);

            // 用户在「OptiScaler」页可以改注入用的 DLL 名字：不同就复制一份 <名字>.dll 去注入
            optiScaler = EnsureNamedOptiScalerDll(optiScaler, AppConfig.GetOptiScalerDllName(optiGameId));
            if (!string.IsNullOrWhiteSpace(optiScaler)
                && File.Exists(optiScaler)
                && specs.All(s => !string.Equals(s.Path, optiScaler, StringComparison.OrdinalIgnoreCase)))
            {
                string gameKey = optiGameId.GameBiz.ToString();
                string? buildDirectory = Path.GetDirectoryName(optiScaler);

                // ini 按游戏分离：注入前把这个游戏的那份激活为主 ini（首次从当前主 ini 继承）。
                // 必须先 Activate 再钉 OptiDllPath，否则旧 profile（auto）会把修正覆盖掉
                if (buildDirectory is not null
                    && OptiScalerProfiles.Activate(buildDirectory, gameKey))
                {
                    _logger.LogInformation("OptiScaler ini profile activated: {Game} ({Build})", gameKey, buildDirectory);
                }

                // 旧构建（早于 v1.2.1）的 profile / ini 可能还是 OptiDllPath=auto，激活后再钉绝对路径；
                // 游戏退出 Store 时修正后的主 ini 会回写 profile，下次启动即一致
                if (buildDirectory is not null && OptiScalerRuntime.EnsureConfigDllPath(buildDirectory))
                {
                    _logger.LogInformation("OptiScaler ini: OptiDllPath pinned ({Build})", buildDirectory);
                }

                // dlss-unlocked 的 MFG 解锁只认 310.9 签名：把候选里版本最高的 dlssg 钉到 OptiDllPath 根，
                // 避免 BFS 命中游戏目录自带的 310.6.0（unlock unavailable for this runtime）
                if (buildDirectory is not null)
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

                specs.Add(new InjectDllSpec(optiScaler, "OptiScaler", buildDirectory, gameKey));
            }
        }

        // ③ XXMI：照 XXMI 自己的流程（参考它自己的日志）：
        //      SetupHook(d3d11.dll) → 启动游戏（ZZZ 还要带 -use-d3d12）→ 额外注入实例里配的 Extra Libraries
        //      （用户 ZZMI 里那个「d3d12.dll」其实是 OptiScaler）→ WaitForInjection 校验 → Unhook。
        //    现状：3dmloader 的 HookLibrary 需要先把 MI 的 d3d11.dll LoadLibrary 进"注入器进程"找 CBTProc
        //    入口，而 3DMigoto 那份 d3d11.dll 在普通进程里 LoadLibrary 会 ERROR_DLL_INIT_FAILED(1114)，
        //    所以这条路在我们 App 进程里必然返回 200。要在我们这边走通，得把这一步放到一个"干净"的辅助进程里
        //    （见 docs/GAMES-AND-INJECT.md §11 的待办），先不接线，避免误导用户。
        if (UseXxmiInject && CurrentGameId is { } xxmiGameId)
        {
            _logger.LogInformation("XXMI 注入已勾选（{Game}）：当前实现还未接上 Hook（3dmloader 的 HookLibrary 在 App 进程里会 200）",
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

        if (specs.Count == 0)
        {
            return;
        }

        _extraInjectCts?.Cancel();
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
                await InjectExtraDllsAsync(processName, specs, token);
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

    private static long GetFileLengthOrZero(string filePath)
    {
        try
        {
            return File.Exists(filePath) ? new FileInfo(filePath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 等 FSR Bridge 完成初始化：它在 IAT/loader hooks 安装前会写入
    /// <c>Dx11FsrBridge active pid=&lt;pid&gt;</c>。看到本次进程这行后再留 500ms，
    /// 让随后的 loader hooks 装完；后台日志线程按 200ms 批量落盘，轮询间隔 200ms。
    /// 日志被截断（长度小于基线）时从文件头读起。
    /// </summary>
    private static async Task<bool> WaitForFsrBridgeReadyAsync(
        string bridgeDirectory,
        int processId,
        long baselineLength,
        TimeSpan timeout,
        System.Threading.CancellationToken cancellationToken)
    {
        string logPath = Path.Combine(bridgeDirectory, "Dx11FsrBridge.log");
        string marker = "Dx11FsrBridge active pid=" + processId.ToString();
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(logPath))
                {
                    long offset = new FileInfo(logPath).Length >= baselineLength ? baselineLength : 0;
                    using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
                    {
                        if (offset > 0)
                        {
                            stream.Seek(offset, SeekOrigin.Begin);
                        }

                        string tail = reader.ReadToEnd();
                        if (tail.Contains(marker, StringComparison.Ordinal))
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

    private async Task InjectExtraDllsAsync(string processName, IReadOnlyList<InjectDllSpec> specs, System.Threading.CancellationToken cancellationToken)
    {
        try
        {
            Process? target = await DllInjector.WaitForProcessAsync(processName, TimeSpan.FromMinutes(20), cancellationToken);
            if (target is null)
            {
                string label = specs.Count > 0 ? specs[0].Label : "额外注入";
                DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Warning(label,
                    $"等了 20 分钟也没看到 {processName} 起来，这次不注了。", 8000));
                return;
            }

            int pid = target.Id;
            bool anyOk = false;

            foreach (InjectDllSpec spec in specs)
            {
                string name = Path.GetFileName(spec.Path);
                string label = spec.Label;

                // Bridge 的日志会在每次启动时截断；记下注入前的文件长度，就绪检测只看新增内容，
                // 避免上一次运行留下的 "active" 行被误判成本次就绪。
                long baselineLength = 0;
                if (spec.WaitForReady)
                {
                    baselineLength = GetFileLengthOrZero(Path.Combine(
                        Path.GetDirectoryName(spec.Path) ?? string.Empty, "Dx11FsrBridge.log"));
                }

                bool ok = DllInjector.Inject(pid, spec.Path, out string error);
                anyOk |= ok;

                _logger.LogInformation("{Label} injection {Result} ({Dll} -> pid {Pid}) {Error}",
                    label, ok ? "ok" : "failed", spec.Path, pid, error);

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
                        baselineLength,
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

            // 游戏起来了：让背景停掉并释放显存（跟 Hub 自己启动游戏时的行为一致）；
            // 游戏退出再发一条，让背景回来 —— 用户报过「壁纸在游戏关闭后没有恢复」
            if (anyOk)
            {
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
        }
        catch (OperationCanceledException)
        {
            // 注入器被停掉了，正常退出
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Extra DLL injection");
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

        await EnsureFpsUnlockDataAsync(gameId);

        byte[]? shellcode = FpsUnlockDataService.LoadShellcode();
        (byte[] bytes, bool[] mask)? pattern = FpsUnlockDataService.LoadPattern();

        if (shellcode is null || pattern is null)
        {
            shellcode ??= FpsUnlocker.FallbackShellcode;
            pattern ??= (
                [0x8B, 0x0D, 0x00, 0x00, 0x00, 0x00, 0xEB, 0x00, 0x33, 0xC0],
                [true, true, false, false, false, false, true, false, true, true]);
            _logger.LogWarning("FPS unlock: local data missing, using built-in fallback");
        }

        Process? game = await _gameLauncherService.GetGameProcessAsync(gameId, processTimeout);
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

        string detail = string.IsNullOrWhiteSpace(unlocker.LastError)
            ? "特征扫描或注入失败，游戏版本可能已更新。"
            : unlocker.LastError!;
        _logger.LogWarning("FPS unlock failed: {Detail}", detail);
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

            var service = new GamePluginService(
                _currentGameEntry,
                PluginHostLocator.Resolve(out _),
                PluginHostLocator.AddonNameCachePath,
                GameCatalog.AddonCandidateNames(),
                GameCatalog.TagsOfAddonFile);

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
    private async Task StartGameAsync()
    {
        try
        {
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

            // 启用 OptiScaler：游戏目录自带 dlssg 不是 310.9 就先提示，确认后再启动
            if (!await ConfirmGameDlssgAsync())
            {
                return;
            }

            // 启用 OptiScaler：驱动里把 MFG 数量钉死了会盖住 MFG 解锁，先问一下要不要改成 N/A             if (!await ConfirmNvMfgCountAsync())             {                 return;             }

            // 「启动游戏时强制 off」：启动/注入之前把 hook 点写 0（按游戏开关）
            ApplyForceHookOffOnLaunch();

            // 「启用 XXMI 注入」：**按 XXMI 的方式启动** —— 挂起起进程 → 往进程里 Inject 3DMigoto 的
            // d3d11.dll → 恢复线程（注入发生在 D3D 初始化前，且 DllMain 在游戏进程里跑，不会踩 1114）。
            // 这条会自己把游戏起起来（跟 XXMI Launcher 一样），所以后面的正常启动流程直接跳过。
            if (UseXxmiInject && CurrentGameId is { } xxmiGameId
                && !string.IsNullOrWhiteSpace(GameInstallPath) && Directory.Exists(GameInstallPath))
            {
                string xxmiExeName = await _gameLauncherService.GetGameExeNameAsync(xxmiGameId);
                string xxmiExe = Path.Combine(GameInstallPath, xxmiExeName);

                if (!File.Exists(xxmiExe))
                {
                    DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Warning("XXMI",
                        $"游戏目录里找不到 {xxmiExeName}", 10000));
                }
                else
                {
                    // XXMI 有命令行：[XXMI Launcher.exe "<游戏 exe>" -x ZZMI -n]（-n = 不开界面）。
                    // 注入全交给它（ZZMI 那份 d3d11.dll 是受控版，只有它能驱动），我们只负责 ReShade 这头：
                    // 先按用户勾的 HoYoShade 挂上它的注入器，再由 XXMI 在后台把游戏起起来并注入模型替换。
                    if (UseHoYoShade)
                    {
                        await LaunchShaderInjectorOnlyAsync(Path.Combine(AppConfig.UserDataFolder, "HoYoShade"), "HoYoShade");
                    }
                    else if (UseOpenHoYoShade)
                    {
                        await LaunchShaderInjectorOnlyAsync(Path.Combine(AppConfig.UserDataFolder, "OpenHoYoShade"), "OpenHoYoShade");
                    }

                    XxmiInjector.XxmiLaunchResult xxmi = XxmiInjector.LaunchViaXxmiCli(xxmiGameId, xxmiExe);

                    AppConfig.XxmiLastLaunch = xxmi.Message;
                    _logger.LogInformation("XXMI launch: {Message}", xxmi.Message);

                    if (xxmi.Injected)
                    {
                        DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Success("XXMI", xxmi.Message, 8000));
                    }
                    else
                    {
                        DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Warning("XXMI", xxmi.Message, 12000));
                    }

                    // 我们自己的其它注入（OptiScaler / 额外注入 DLL）照旧挂上
                    string? xxmiProcess = await ResolveTargetProcessNameAsync();

                    if (!string.IsNullOrWhiteSpace(xxmiProcess))
                    {
                        StartExtraDllInjection(xxmiProcess);
                    }

                    return;
                }
            }

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
                        InAppToast.MainWindow?.Error($"inject.exe not found in {shadeName}");
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
            _logger.LogInformation("Launching custom game: {Exe}", exe);

            Process? process = Process.Start(new ProcessStartInfo(exe)
            {
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
                InAppToast.MainWindow?.Error(string.Format(Lang.GameLauncher_InjectExeNotFound, shadeName));
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
                InAppToast.MainWindow?.Error($"inject.exe not found in {shadeName}");
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
                        InAppToast.MainWindow?.Error($"inject.exe not found in {shadeName}");
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




    #region Install Game




    private async Task InstallGameAsync()
    {
        await LocateGameAsync();
    }


    private async Task ResumeDownloadAsync()
    {
        await LocateGameAsync();
    }






    #endregion




    #region Predownload




    [RelayCommand]
    private async Task PredownloadAsync()
    {
        // Removed predownload functionality
        await Task.CompletedTask;
    }





    #endregion



    #region Update



    private async Task UpdateGameAsync()
    {
        // Removed update functionality
        await Task.CompletedTask;
    }



    #endregion



    #region Game Install Task (Removed)



    private GameInstallContext? _gameInstallTask;




    private async Task ChangeGameInstallTaskStateAsync()
    {
        // Removed game install task logic
        await Task.CompletedTask;
    }



    private void UpdateGameInstallTask()
    {
        // Removed game install task logic
    }



    #endregion



    #region Drop Background File




    private void RootGrid_DragOver(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            Border_BackgroundDragIn.Opacity = 1;
        }
    }




    private async void RootGrid_Drop(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        Border_BackgroundDragIn.Opacity = 0;
        var defer = e.GetDeferral();
        try
        {
            if ((await e.DataView.GetStorageItemsAsync()).FirstOrDefault() is StorageFile file)
            {
                string? name = await BackgroundService.ChangeCustomBackgroundFileAsync(file);
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }
                AppConfig.SetCustomBg(CurrentGameBiz, name);
                AppConfig.SetEnableCustomBg(CurrentGameBiz, true);
                WeakReferenceMessenger.Default.Send(new BackgroundChangedMessage());
            }
        }
        catch (COMException ex)
        {
            InAppToast.MainWindow?.Error(Lang.GameLauncherSettingDialog_CannotDecodeFile);
            _logger.LogError(ex, "Change custom background failed");
        }
        catch (Exception ex)
        {
            InAppToast.MainWindow?.Error(Lang.GameLauncherSettingDialog_AnUnknownErrorOccurredPleaseCheckTheLogs);
            _logger.LogError(ex, "Change custom background failed");
        }
        defer.Complete();
    }



    private void RootGrid_DragLeave(object sender, Microsoft.UI.Xaml.DragEventArgs e)
    {
        Border_BackgroundDragIn.Opacity = 0;
    }



    #endregion




    #region Game Setting



    [RelayCommand]
    private async Task OpenGameLauncherSettingDialogAsync()
    {
        await new GameLauncherSettingDialog { CurrentGameId = this.CurrentGameId, XamlRoot = this.XamlRoot }.ShowAsync();
        _ = CheckDX12ConfigAsync();
    }




    #endregion



    #region Switch Background Image


    private const string PlayIcon = "\uF5B0";

    private const string PauseIcon = "\uE62E";


    private List<GameBackground> _backgroundImages;
    public List<GameBackground> BackgroundImages
    {
        get => _backgroundImages;
        set => SetProperty(ref _backgroundImages, value);
    }

    private bool _canStopVideo;
    public bool CanStopVideo
    {
        get => _canStopVideo;
        set => SetProperty(ref _canStopVideo, value);
    }

    private string _startStopButtonIcon;
    public string StartStopButtonIcon
    {
        get => _startStopButtonIcon;
        set => SetProperty(ref _startStopButtonIcon, value);
    }


    private int currentBackgroundImageIndex;
    public int CurrentBackgroundImageIndex
    {
        get => currentBackgroundImageIndex;
        set
        {
            if (SetProperty(ref currentBackgroundImageIndex, value))
            {
                ChangeBackgroundImageIndex(value);
            }
        }
    }


    private void OnBackgroundChanged(object _, BackgroundChangedMessage message)
    {
        if (message.GameBackground is null)
        {
            _ = InitializeBackgameImageSwitcherAsync();
        }
    }


    private async Task InitializeBackgameImageSwitcherAsync()
    {
        try
        {
            CanStopVideo = false;
            BackgroundImages = await _backgroundService.GetGameBackgroundsAsync(CurrentGameId);
            if (BackgroundImages.Count > 1)
            {
                Border_SwitchBackgroundImage.Visibility = Visibility.Visible;
                GameBackground? currentBackground = await _backgroundService.GetSuggestedGameBackgroundAsync(CurrentGameId);
                if (currentBackground != null && BackgroundImages.FirstOrDefault(x => x.Id == currentBackground.Id) is GameBackground current)
                {
                    currentBackgroundImageIndex = Math.Clamp(BackgroundImages.IndexOf(current), 0, BackgroundImages.Count - 1);
                    OnPropertyChanged(nameof(CurrentBackgroundImageIndex));
                    CanStopVideo = current.Type is GameBackground.BACKGROUND_TYPE_VIDEO;
                    if (CanStopVideo)
                    {
                        current.StopVideo = currentBackground.StopVideo;
                        StartStopButtonIcon = current.StopVideo ? PlayIcon : PauseIcon;
                    }
                }
            }
            else
            {
                Border_SwitchBackgroundImage.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize background image switcher {GameBiz}", CurrentGameBiz);
        }
    }


    private void ChangeBackgroundImageIndex(int index)
    {
        try
        {
            if (index < 0 || index >= BackgroundImages.Count)
            {
                return;
            }
            GameBackground current = BackgroundImages[index];
            WeakReferenceMessenger.Default.Send(new BackgroundChangedMessage(current));
            CanStopVideo = current.Type is GameBackground.BACKGROUND_TYPE_VIDEO;
            if (CanStopVideo)
            {
                StartStopButtonIcon = current.StopVideo ? PlayIcon : PauseIcon;
            }
        }
        catch { }
    }


    private void Border_SwitchBackgroundImage_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        Border_SwitchBackgroundImage.Opacity = 1;
    }


    private void Border_SwitchBackgroundImage_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        Border_SwitchBackgroundImage.Opacity = 0;
    }


    int _switchBackgroundTotalDelta = 0;

    private void Border_SwitchBackgroundImage_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        _switchBackgroundTotalDelta += delta;
        if (_switchBackgroundTotalDelta <= -120)
        {
            CurrentBackgroundImageIndex++;
            _switchBackgroundTotalDelta = 0;
        }
        else if (_switchBackgroundTotalDelta >= 120)
        {
            CurrentBackgroundImageIndex--;
            _switchBackgroundTotalDelta = 0;
        }
    }


    [RelayCommand]
    private async Task CopyCurrentBackgroundImageAsync()
    {
        try
        {
            string? path = null;
            GameBackground? background = AppBackground.Current.CurrentGameBackground;
            if (background?.Type is GameBackground.BACKGROUND_TYPE_CUSTOM)
            {
                path = background.Background.Url;
            }
            else if (background?.Type is GameBackground.BACKGROUND_TYPE_VIDEO && !background.StopVideo)
            {
                string name = Path.GetFileName(background.Video.Url);
                path = BackgroundService.GetBgFilePath(name);
            }
            else if (background is not null)
            {
                string name = Path.GetFileName(background.Background.Url);
                path = BackgroundService.GetBgFilePath(name);
            }
            if (File.Exists(path))
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                ClipboardHelper.SetStorageItems(DataPackageOperation.Copy, file);
                InAppToast.MainWindow?.Information(Lang.Common_CopiedToClipboard);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy current background image {GameBiz}", CurrentGameBiz);
        }
    }


    [RelayCommand]
    private async Task SaveCurrentBackgroundImageAsync()
    {
        try
        {
            string? path = null;
            GameBackground? background = AppBackground.Current.CurrentGameBackground;
            if (background?.Type is GameBackground.BACKGROUND_TYPE_CUSTOM)
            {
                path = background.Background.Url;
            }
            else if (background?.Type is GameBackground.BACKGROUND_TYPE_VIDEO && !background.StopVideo)
            {
                string name = Path.GetFileName(background.Video.Url);
                path = BackgroundService.GetBgFilePath(name);
            }
            else if (background is not null)
            {
                string name = Path.GetFileName(background.Background.Url);
                path = BackgroundService.GetBgFilePath(name);
            }
            if (File.Exists(path))
            {
                var savePath = await FileDialogHelper.OpenSaveFileDialogAsync(this.XamlRoot, Path.GetFileName(path));
                if (!string.IsNullOrWhiteSpace(savePath))
                {
                    File.Copy(path, savePath, true);
                    var file = await StorageFile.GetFileFromPathAsync(savePath);
                    var options = new FolderLauncherOptions();
                    options.ItemsToSelect.Add(file);
                    await Launcher.LaunchFolderAsync(await file.GetParentAsync(), options);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save as current background image {GameBiz}", CurrentGameBiz);
        }
    }


    [RelayCommand]
    private void StartOrStopVideoBackground()
    {
        try
        {
            GameBackground current = BackgroundImages[CurrentBackgroundImageIndex];
            if (current.Type is GameBackground.BACKGROUND_TYPE_VIDEO)
            {
                current.StopVideo = !current.StopVideo;
                StartStopButtonIcon = current.StopVideo ? PlayIcon : PauseIcon;
                WeakReferenceMessenger.Default.Send(new BackgroundChangedMessage(current));
            }
        }
        catch { }
    }


    #endregion



    #region Cloud Game




    #endregion
    /// <summary>帧率解锁右边的齿轮：直接跳到「游戏设置」页去改目标帧率。</summary>
    private void Button_FpsUnlockSettings_Click(object sender, RoutedEventArgs e)
    {
        WeakReferenceMessenger.Default.Send(new MainViewNavigateMessage(typeof(GameSettingPage)));
    }

}
