using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using NuGet.Versioning;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoShade;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.GameLauncher;
using HoYoShadeHub.Features.GameSetting;
using HoYoShadeHub.Features.Modules;
using HoYoShadeHub.Features.OptiScaler;
using HoYoShadeHub.Features.Plugins;
using HoYoShadeHub.Features.Xxmi;
using HoYoShadeHub.Features.RPC;
using HoYoShadeHub.Features.Screenshot;
using HoYoShadeHub.Features.Setting;
using HoYoShadeHub.Features.Update;
using HoYoShadeHub.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;


namespace HoYoShadeHub.Features.ViewHost;

[INotifyPropertyChanged]
public sealed partial class MainView : UserControl, IShellFileDropTarget
{


    private readonly ILogger<MainView> _logger = AppConfig.GetLogger<MainView>();
    private readonly UiLifetime _lifetime = new();
    private bool _startupChecksStarted;
    private bool _navigationQueued;
    private nint _shellDropWindow;
    private bool _installingDroppedOverlay;
    private (Type? Page, object? Parameter, NavigationTransitionInfo? Transition)? _pendingNavigation;
    private (Type Page, string? Id, string? Biz)? _lastNavigation;


    public GameId? CurrentGameId { get; private set => SetProperty(ref field, value); }


    private GameFeatureConfig CurrentGameFeatureConfig { get; set; }



    /// <summary>当前那份 MainView（窗口算拖拽区时要问它左上角那排游戏图标占多宽）</summary>
    public static MainView? Current { get; private set; }


    public MainView()
    {
        this.InitializeComponent();
        MainView_Frame.IsNavigationStackEnabled = false;
        Current = this;
        InitializeMainView();
    }



    /// <summary>让 GameSelector 重新算窗口拖拽区；算不出来返回 false</summary>
    public bool TryUpdateWindowDragRectangles() => GameSelector?.TryUpdateDragRectangles() == true;



    private void InitializeMainView()
    {
        this.Loaded += MainView_Loaded;
        this.Unloaded += MainView_Unloaded;
        GameId? gameId = GameSelector.CurrentGameId;
        if (gameId?.GameBiz == GameBiz.bh3_global)
        {
            string? id = AppConfig.LastGameIdOfBH3Global;
            if (!string.IsNullOrWhiteSpace(id))
            {
                gameId.Id = id;
            }
        }
        CurrentGameId = gameId;
        CurrentGameFeatureConfig = GameFeatureConfig.FromGameId(CurrentGameId);
        UpdateNavigationView();
    }




    private void MainView_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        if (!_lifetime.Enter()) return;
        Current = this;
        WeakReferenceMessenger.Default.Register<MainViewNavigateMessage>(this, OnMainViewNavigateMessageReceived);
        _shellDropWindow = XamlRoot.GetWindowHandle();
        ShellFileDrop.RegisterWindowTarget(_shellDropWindow, this);
        WeakReferenceMessenger.Default.Register<BH3GlobalGameServerChangedMessage>(this, OnBH3GlobalGameServerChanged);
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (_, _) =>
        {
            long generation = _lifetime.Generation;
            DispatcherQueue.TryEnqueue(() => { if (_lifetime.IsCurrent(generation)) Bindings.Update(); });
        });
        UpdateNavigationView();
        if (_startupChecksStarted) return;
        _startupChecksStarted = true;
        // 版本更新引导：窗口已经显示之后**直接弹**（不能再放到插件页导航里，那样会把页面卡死）
        _ = CacheMigrationService.PromptIfNeededAsync(XamlRoot, _logger);
        CheckSystemProxy(_lifetime.Token);
        // 「显示主窗口」全局快捷键按用户要求删掉了（不再注册；设置页里那个输入框也隐藏了）
        // HotkeyManager.InitializeHotkey(this.XamlRoot.GetWindowHandle());
        _ = CheckUpdateOrShowRecentUpdateContentAsync(_lifetime.Token);
        _ = CheckFrameworkUpdatesOnStartupAsync(_lifetime.Token);
        AppConfig.GetService<RpcService>().TrySetEnviromentAsync();
    }

    private void MainView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_lifetime.Exit()) return;
        ShellFileDrop.UnregisterWindowTarget(_shellDropWindow, this);
        WeakReferenceMessenger.Default.UnregisterAll(this);
        _navigationQueued = false;
        _pendingNavigation = null;
        if (ReferenceEquals(Current, this)) Current = null;
    }




    private void GameSelector_CurrentGameChanged(object? sender, (GameId?, bool DoubleTapped) e)
    {
        if (e.Item1 is null)
        {
            CurrentGameId = null;
            CurrentGameFeatureConfig = GameFeatureConfig.FromGameId(null);
            UpdateNavigationView();
            return;
        }

        if (e.Item1.GameBiz == GameBiz.bh3_global)
        {
            // 崩坏3国际服区服
            string? id = AppConfig.LastGameIdOfBH3Global;
            if (!string.IsNullOrWhiteSpace(id))
            {
                e.Item1.Id = id;
            }
        }
        CurrentGameId = e.Item1;
        CurrentGameFeatureConfig = GameFeatureConfig.FromGameId(CurrentGameId);
        UpdateNavigationView();

    }


    private async Task CheckFrameworkUpdatesOnStartupAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!AppConfig.AutoCheckFrameworkUpdateOnStartup)
            {
                return;
            }

#if CI || DEBUG
            return;
#endif
#pragma warning disable CS0162
            await Task.Delay(800, cancellationToken);
#pragma warning restore CS0162

            var versionService = new HoYoShadeVersionService(AppConfig.UserDataFolder);
            var updateService = new HoYoShadeUpdateService(versionService);

            int serverIndex = AppConfig.HoYoShadeFrameworkDownloadServer;
            string? proxyUrl = CloudProxyManager.GetProxyUrl(serverIndex);
            if (serverIndex == -1)
            {
                proxyUrl = CloudProxyManager.GetProxyUrl(0);
            }

            var hoYoShadeRelease = await updateService.CheckHoYoShadeUpdateAsync(AppConfig.EnableHoYoShadePreviewChannel, proxyUrl);
            cancellationToken.ThrowIfCancellationRequested();
            if (hoYoShadeRelease != null)
            {
                InAppToast.MainWindow?.Information("HoYoShade", string.Format(GetLangString("FileSettingPage_NewVersionAvailableFormat", "New version available: {0}"), hoYoShadeRelease.TagName), 8000);
            }

            var openHoYoShadeRelease = await updateService.CheckOpenHoYoShadeUpdateAsync(AppConfig.EnableHoYoShadePreviewChannel, proxyUrl);
            cancellationToken.ThrowIfCancellationRequested();
            if (openHoYoShadeRelease != null)
            {
                InAppToast.MainWindow?.Information("OpenHoYoShade", string.Format(GetLangString("FileSettingPage_NewVersionAvailableFormat", "New version available: {0}"), openHoYoShadeRelease.TagName), 8000);
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) _logger.LogError(ex, "Check framework updates on startup");
        }
    }


    private static string GetLangString(string key, string fallback)
    {
        return Lang.ResourceManager.GetString(key, Lang.Culture) ?? fallback;
    }



    private void OnBH3GlobalGameServerChanged(object _, BH3GlobalGameServerChangedMessage message)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            long generation = _lifetime.Generation;
            DispatcherQueue.TryEnqueue(() => { if (_lifetime.IsCurrent(generation)) OnBH3GlobalGameServerChanged(_, message); });
            return;
        }
        if (CurrentGameId?.GameBiz == GameBiz.bh3_global)
        {
            CurrentGameId.Id = message.GameId;
            OnPropertyChanged(nameof(CurrentGameId));
            NavigateTo(typeof(GameLauncherPage), CurrentGameId, new SuppressNavigationTransitionInfo());
        }
    }




    #region Navigation





    private void UpdateNavigationView()
    {
        NavigationViewItem_Launcher.Visibility = CurrentGameFeatureConfig.SupportedPages.Contains(nameof(GameLauncherPage)).ToVisibility();
        NavigationViewItem_GameSetting.Visibility = CurrentGameFeatureConfig.SupportedPages.Contains(nameof(GameSettingPage)).ToVisibility();
        NavigationViewItem_Screenshot.Visibility = CurrentGameFeatureConfig.SupportedPages.Contains(nameof(ScreenshotPage)).ToVisibility();

        // 「模型替换」跟着游戏显示对应的 MI 实例名（绝区零 → ZZMI；自定义游戏按名字认 → 鸣潮 → WWMI）
        GameEntry? gameEntry = CurrentGameId is null ? null
            : Features.Plugins.GameCatalog.GetOrCreate(Features.Plugins.GameCatalog.CreateService(), CurrentGameId);
        string? gameName = gameEntry?.DisplayName;
        string? importer = CurrentGameId is null ? null : Features.Xxmi.XxmiLocator.ImporterForGame(CurrentGameId.GameBiz, gameName);
        TextBlock_XxmiNav.Text = "Mod";

        MaybePromptVanillaReShade(gameEntry);

        if (CurrentGameId is null)
        {
            NavigateTo(typeof(BlankPage));
        }
        else if (MainView_Frame.SourcePageType?.Name is not nameof(SettingPage))
        {
            NavigateTo(MainView_Frame.SourcePageType);
        }
    }

    /// <summary>问过「要不要装原版 ReShade」的游戏 id，一个游戏只问一次（选了「不用」不再烦）</summary>
    private static readonly HashSet<string> _vanillaPrompted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>鸣潮只准备 Hub 缓存中的 ReShade 注入 DLL，不在游戏目录部署代理。</summary>
    private async void MaybePromptVanillaReShade(GameEntry? entry)
    {
        try
        {
            if (XamlRoot is null || CurrentGameId is not { } gameId || entry is null
                || Features.Xxmi.XxmiLocator.ImporterForGame(gameId.GameBiz, entry.DisplayName) is not "WWMI"
                || !_vanillaPrompted.Add(gameId.Id))
            {
                return;
            }

            string? target = VanillaReShade.ResolveTargetDirectory(entry, entry.GameDirectory);

            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target)
                || VanillaReShade.IsInjectionInstalled(target) || VanillaReShade.HasProxyDll(target))
            {
                return;
            }

            string body = "是否下载原版 ReShade 到 Hub 缓存，用于鸣潮客户端直接注入？\n\n" +
                          "不会往游戏目录安装 dxgi.dll，也不会调用 inject.exe。注入可能被游戏保护拒绝，不保证兼容。";

            ContentDialog dialog = new()
            {
                XamlRoot = XamlRoot,
                Title = "原版 ReShade",
                Content = body,
                PrimaryButtonText = "下载并安装",
                CloseButtonText = "不用",
                DefaultButton = ContentDialogButton.Primary,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            InAppToast.MainWindow?.Information("原版 ReShade", "开始下载…", 4000);
            string installed = await VanillaReShade.InstallForInjectionAsync(target, CancellationToken.None);
            InAppToast.MainWindow?.Success("原版 ReShade", $"ReShade 注入副本已准备：{installed}。启动器中可切换注入开关。", 8000);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            InAppToast.MainWindow?.Error("原版 ReShade", "下载 / 安装失败：" + ex.Message, 10000);
        }
    }



    private void NavigationView_ItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
    {
        try
        {
            if (args.InvokedItemContainer?.IsSelected ?? false)
            {
                return;
            }
            if (args.IsSettingsInvoked)
            {
                NavigateTo(typeof(SettingPage));
            }
            else
            {
                if (args.InvokedItemContainer is NavigationViewItem item)
                {
                    var type = item.Tag switch
                    {
                        nameof(GameLauncherPage) => typeof(GameLauncherPage),
                        nameof(GameSettingPage) => typeof(GameSettingPage),
                        nameof(ScreenshotPage) => typeof(ScreenshotPage),
                        // 按游戏的插件页
                        nameof(GamePluginPage) => typeof(GamePluginPage),
                        // 插件的运行时 dll（nvngx_dlssnr.dll / sl.*.dll）
                        nameof(DllConfigPage) => typeof(DllConfigPage),
                        // 左下角的全局插件页
                        nameof(GlobalPluginPage) => typeof(GlobalPluginPage),
                        // 模型替换（XXMI）：MI 实例 + Mods 管理
                        nameof(XxmiPage) => typeof(XxmiPage),
                        // 模块：要注入游戏进程的独立 DLL（DLSS-NR on AMD 那类）
                        nameof(ModulesPage) => typeof(ModulesPage),
                        // OptiScaler：总开关 + 每个游戏选一个构建
                        nameof(OptiScalerPage) => typeof(OptiScalerPage),
                        _ => null,
                    };
                    NavigateTo(type);
                }
            }
        }
        catch (Exception ex)
        {
            // 以前这里是空的 catch{}，导航失败时什么都不留，排查起来只能靠猜。
            // 页面加载/XAML 出错时必须能看见原因。
            AppConfig.GetLogger<MainView>().LogError(ex, "NavigationView item invoked failed");
        }
    }



    public Task OnShellFilesDroppedAsync(IReadOnlyList<string> paths) => OnShellFilesDroppedAsync(paths, null);

    public async Task OnShellFilesDroppedAsync(IReadOnlyList<string> paths, Windows.Foundation.Point? screenPoint)
    {
        if (_installingDroppedOverlay) return;
        var originalPageTarget = ShellFileDrop.GetPageTarget(_shellDropWindow);
        var overlays = await Task.Run(() => paths.Where(p => File.Exists(p)
            && LocalPackageInstaller.DetectKind(p) == LocalPackageKind.Overlay).ToArray());
        if (overlays.Length == 0)
        {
            if (originalPageTarget is { } target && ReferenceEquals(target, ShellFileDrop.GetPageTarget(_shellDropWindow)))
                await target.OnShellFilesDroppedAsync(paths, screenPoint);
            return;
        }
        _installingDroppedOverlay = true;
        try
        {
            ExecuteNavigation(typeof(GlobalPluginPage), null, new SuppressNavigationTransitionInfo());
            if (MainView_Frame.Content is not GlobalPluginPage page) return;
            for (int i = 0; !page.IsReadyForShellDrop && i < 300; i++)
            {
                await Task.Delay(100);
                if (!ReferenceEquals(MainView_Frame.Content, page)) return;
            }
            foreach (string overlay in overlays)
            {
                if (!ReferenceEquals(MainView_Frame.Content, page) || !page.IsReadyForShellDrop) return;
                await page.OnShellFilesDroppedAsync([overlay]);
            }
        }
        finally { _installingDroppedOverlay = false; }
    }

    private void NavigateTo(Type? page, object? param = null, NavigationTransitionInfo? infoOverride = null)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            long generation = _lifetime.Generation;
            DispatcherQueue.TryEnqueue(() => { if (_lifetime.IsCurrent(generation)) NavigateTo(page, param, infoOverride); });
            return;
        }
        _pendingNavigation = (page, param, infoOverride);
        if (!_lifetime.IsActive || _navigationQueued) return;
        _navigationQueued = true;
        long attachment = _lifetime.Generation;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            if (!_lifetime.IsCurrent(attachment)) return;
            _navigationQueued = false;
            var request = _pendingNavigation;
            _pendingNavigation = null;
            if (request is null) return;
            try { ExecuteNavigation(request.Value.Page, request.Value.Parameter, request.Value.Transition); }
            catch (Exception ex) { _logger.LogError(ex, "Navigate main frame"); }
        })) _navigationQueued = false;
    }

    private void ExecuteNavigation(Type? page, object? param, NavigationTransitionInfo? infoOverride)
    {
        page ??= typeof(GameLauncherPage);
        if (page.Name is nameof(BlankPage) && CurrentGameId is null)
        {

        }
        else if (page.Name is not nameof(SettingPage)
                 and not nameof(GamePluginPage)
                 and not nameof(DllConfigPage)
                 and not nameof(GlobalPluginPage)
                 and not nameof(XxmiPage)
                 and not nameof(ModulesPage)
                 and not nameof(OptiScalerPage)
                 && !CurrentGameFeatureConfig.SupportedPages.Contains(page.Name))
        {
            page = typeof(GameLauncherPage);
        }
        if (page == typeof(GameLauncherPage) && param is null && CurrentGameId is null)
        {
            page = typeof(BlankPage);
        }
        // 帧内跳转（消息 / 齿轮按钮那种）也要把左侧导航栏的选中项跟上，
        // 不然页面换了、导航栏还停在旧项，用户回不去主界面（帧率解锁跳游戏设置报过这个）
        object? selected = page.Name switch
        {
            nameof(GameLauncherPage) => NavigationViewItem_Launcher,
            nameof(GameSettingPage) => NavigationViewItem_GameSetting,
            nameof(ScreenshotPage) => NavigationViewItem_Screenshot,
            nameof(GamePluginPage) => NavigationViewItem_Plugins,
            nameof(OptiScalerPage) => NavigationViewItem_OptiScaler,
            nameof(ModulesPage) => NavigationViewItem_Modules,
            nameof(XxmiPage) => NavigationViewItem_Xxmi,
            nameof(DllConfigPage) => NavigationViewItem_DllConfig,
            nameof(GlobalPluginPage) => NavigationViewItem_GlobalPlugins,
            nameof(SettingPage) => MainView_NavigationView.SettingsItem,
            _ => null,
        };
        object? parameter = param ?? CurrentGameId;
        var game = parameter as GameId;
        var key = (page, game?.Id, game?.GameBiz.Value);
        if (_lastNavigation == key && MainView_Frame.SourcePageType == page) return;
        if (!ContentTransitionController.AnimationsEnabled()) infoOverride = new SuppressNavigationTransitionInfo();
        if (!MainView_Frame.Navigate(page, parameter, infoOverride)) return;
        _lastNavigation = key;
        if (selected is not null && (selected is not NavigationViewItem item || item.Visibility == Visibility.Visible))
        {
            MainView_NavigationView.SelectedItem = selected;
        }
        if (page.Name is nameof(BlankPage) or nameof(GameLauncherPage))
        {
            Border_OverlayMask.Opacity = 0;
        }
        else
        {
            Border_OverlayMask.Opacity = 1;
        }
    }



    private void OnMainViewNavigateMessageReceived(object _, MainViewNavigateMessage message)
    {
        NavigateTo(message.Page);
    }




    #endregion




    private async Task CheckUpdateOrShowRecentUpdateContentAsync(CancellationToken cancellationToken)
    {
        try
        {
#if CI || DEBUG
            return;
#endif
#pragma warning disable CS0162 // 检测到无法访问的代码
            await Task.Delay(500, cancellationToken);
#pragma warning restore CS0162 // 检测到无法访问的代码
            
            // Check if there's a pending framework update to show changelog for
            string? pendingFrameworkVersion = AppConfig.GetValue<string>(null, "PendingFrameworkUpdateVersion");
            string? pendingFrameworkName = AppConfig.GetValue<string>(null, "PendingFrameworkUpdateName");
            if (!string.IsNullOrEmpty(pendingFrameworkVersion))
            {
                _logger.LogInformation("Found pending framework update version: {Version}, showing changelog", pendingFrameworkVersion);
                
                // Clear the pending version and name
                AppConfig.SetValue<string>(null, "PendingFrameworkUpdateVersion");
                AppConfig.SetValue<string>(null, "PendingFrameworkUpdateName");
                
                // Show update window in "changelog only" mode (NewVersion = null)
                // Pass the pending info to the window so it knows what to load
                new UpdateWindow 
                { 
                    PendingFrameworkUpdateVersion = pendingFrameworkVersion,
                    PendingFrameworkUpdateName = pendingFrameworkName
                }.Activate();
                return;
            }
            
            if (NuGetVersion.TryParse(AppConfig.AppVersion, out var appVersion))
            {
                _ = NuGetVersion.TryParse(AppConfig.LastAppVersion, out var lastVersion);
                if (appVersion != lastVersion)
                {
                    // 只在「便携版」才弹更新内容窗口（渠道不限：GitHub 是我们默认的渠道）：
                    // 开发实例每次换版本号都会走到这里，其实并没有更新，弹出来只会烦人（用户反馈过）。
                    // 开发实例的版本号约定是 9.9.x（发布版是 1.x），所以大版本 ≥ 9 的一律当开发实例，
                    // 不弹（否则 build\HoYoShadeHub\ 里因为有个 launcher exe，会被认成便携版）。
                    bool isDevInstance = appVersion.Major >= 9;

                    if (!isDevInstance
                        && AppConfig.ShowUpdateContentAfterUpdateRestart
                        && AppConfig.IsPortable)
                    {
                        new UpdateWindow().Activate();
                    }
                    else
                    {
                        AppConfig.LastAppVersion = AppConfig.AppVersion;
                    }
                    return;
                }
            }

            if (!AppConfig.AutoCheckLauncherUpdateOnStartup)
            {
                return;
            }

            // 自动检查「一天一次」：24 小时内查过就直接跳过（手动检查在「关于」页，不受这个限制）。
            // 只有真查到东西、并且确实有新版本时才会弹窗口。
            if (DateTimeOffset.UtcNow - AppConfig.LastUpdateCheckUtc < TimeSpan.FromHours(24))
            {
                return;
            }

            // 渠道决定问谁：GitHub 渠道绝对不能拿官方（上游）的元数据来提示 ——
            // 上游版本号比我们高，会一路劝用户换成上游包。官方渠道才走 RPC 元数据。
            if (AppConfig.UpdateChannel == 1)
            {
                await CheckGithubUpdateAsync(cancellationToken);
                return;
            }

            var release = await AppConfig.GetService<UpdateService>().CheckUpdateAsync(false);
            cancellationToken.ThrowIfCancellationRequested();
            AppConfig.LastUpdateCheckUtc = DateTimeOffset.UtcNow;

            if (release != null)
            {
                new UpdateWindow { NewVersion = release }.Activate();
            }
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) _logger.LogError(ex, "Check update");
        }
    }




    /// <summary>
    /// GitHub 渠道的启动检查：查本仓库的 release，有比当前新的就提示一句，
    /// 一键更新/退回在「设置 → 关于 → 更新渠道（GitHub）」里（那条路已经有版本列表和安装器）。
    /// </summary>
    private async Task CheckGithubUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            List<GithubVersionInfo> versions = await new GithubUpdateService().ListVersionsAsync();
            cancellationToken.ThrowIfCancellationRequested();
            AppConfig.LastUpdateCheckUtc = DateTimeOffset.UtcNow;

            GithubVersionInfo? newer = versions.FirstOrDefault(v => v.Note.Contains("比当前新", StringComparison.Ordinal));
            if (newer is null)
            {
                return;
            }

            _logger.LogInformation("GitHub update available: {Version}", newer.Tag);
            InAppToast.MainWindow?.Information("有新版本",
                $"{newer.Tag}（本仓库）—— 到「设置 → 关于」里一键更新（那里也能退回旧版本）。", 15000);
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) _logger.LogWarning(ex, "Check GitHub update");
        }
    }

    private async void CheckSystemProxy(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(1500, cancellationToken);
            Uri? proxy = HttpClient.DefaultProxy.GetProxy(new Uri("https://hoyosha.de/"));
            if (proxy is not null)
            {
                InAppToast.MainWindow?.Information(Lang.MainView_CheckSystemProxy_SystemProxyIsEnabled, proxy.ToString(), 5000);
            }
        }
        catch { }
    }





}



file static class BoolToVisibilityExtension
{

    public static Visibility ToVisibility(this bool value)
    {
        return value ? Visibility.Visible : Visibility.Collapsed;
    }

}
