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
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;


namespace HoYoShadeHub.Features.ViewHost;

[INotifyPropertyChanged]
public sealed partial class MainView : UserControl
{


    private readonly ILogger<MainView> _logger = AppConfig.GetLogger<MainView>();


    public GameId? CurrentGameId { get; private set => SetProperty(ref field, value); }


    private GameFeatureConfig CurrentGameFeatureConfig { get; set; }



    /// <summary>当前那份 MainView（窗口算拖拽区时要问它左上角那排游戏图标占多宽）</summary>
    public static MainView? Current { get; private set; }


    public MainView()
    {
        this.InitializeComponent();
        Current = this;
        InitializeMainView();
    }



    /// <summary>让 GameSelector 重新算窗口拖拽区；算不出来返回 false</summary>
    public bool TryUpdateWindowDragRectangles() => GameSelector?.TryUpdateDragRectangles() == true;



    private void InitializeMainView()
    {
        this.Loaded += MainView_Loaded;
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
        WeakReferenceMessenger.Default.Register<MainViewNavigateMessage>(this, OnMainViewNavigateMessageReceived);
        WeakReferenceMessenger.Default.Register<BH3GlobalGameServerChangedMessage>(this, OnBH3GlobalGameServerChanged);
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, (_, _) => this.DispatcherQueue.TryEnqueue(() => this.Bindings.Update()));
    }




    private void MainView_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        // 版本更新引导：窗口已经显示之后**直接弹**（不能再放到插件页导航里，那样会把页面卡死）
        _ = CacheMigrationService.PromptIfNeededAsync(XamlRoot, _logger);
        CheckSystemProxy();
        // 「显示主窗口」全局快捷键按用户要求删掉了（不再注册；设置页里那个输入框也隐藏了）
        // HotkeyManager.InitializeHotkey(this.XamlRoot.GetWindowHandle());
        _ = CheckUpdateOrShowRecentUpdateContentAsync();
        _ = CheckFrameworkUpdatesOnStartupAsync();
        AppConfig.GetService<RpcService>().TrySetEnviromentAsync();
    }




    private void GameSelector_CurrentGameChanged(object? sender, (GameId, bool DoubleTapped) e)
    {
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

        // DLSS5 Feed 的效果开关写在（可能多个游戏共用的）ReShade 预设里，
        // 切游戏时按当前游戏重新同步一次 —— 别让上一个游戏的效果留在别的游戏上。
        GameId? switchedGameId = CurrentGameId;
        _ = Task.Run(() => GameCatalog.SyncDlss5FeedPreset(switchedGameId));
    }


    private async Task CheckFrameworkUpdatesOnStartupAsync()
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
            await Task.Delay(800);
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
            if (hoYoShadeRelease != null)
            {
                InAppToast.MainWindow?.Information("HoYoShade", string.Format(GetLangString("FileSettingPage_NewVersionAvailableFormat", "New version available: {0}"), hoYoShadeRelease.TagName), 8000);
            }

            var openHoYoShadeRelease = await updateService.CheckOpenHoYoShadeUpdateAsync(AppConfig.EnableHoYoShadePreviewChannel, proxyUrl);
            if (openHoYoShadeRelease != null)
            {
                InAppToast.MainWindow?.Information("OpenHoYoShade", string.Format(GetLangString("FileSettingPage_NewVersionAvailableFormat", "New version available: {0}"), openHoYoShadeRelease.TagName), 8000);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check framework updates on startup");
        }
    }


    private static string GetLangString(string key, string fallback)
    {
        return Lang.ResourceManager.GetString(key, Lang.Culture) ?? fallback;
    }



    private void OnBH3GlobalGameServerChanged(object _, BH3GlobalGameServerChangedMessage message)
    {
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

        // 「模型替换」跟着游戏显示对应的 MI 实例名（绝区零 → ZZMI）
        string? importer = CurrentGameId is null ? null : Features.Xxmi.XxmiLocator.ImporterFor(CurrentGameId.GameBiz);
        TextBlock_XxmiNav.Text = importer is null ? "模型替换" : $"模型替换（{importer}）";

        if (CurrentGameId is null)
        {
            NavigateTo(typeof(BlankPage));
        }
        else if (MainView_Frame.SourcePageType?.Name is not nameof(SettingPage))
        {
            NavigateTo(MainView_Frame.SourcePageType);
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



    private void NavigateTo(Type? page, object? param = null, NavigationTransitionInfo? infoOverride = null)
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
        if (page.Name is nameof(GameLauncherPage))
        {
            MainView_NavigationView.SelectedItem = NavigationViewItem_Launcher;
        }
        MainView_Frame.Navigate(page, param ?? CurrentGameId, infoOverride);
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




    private async Task CheckUpdateOrShowRecentUpdateContentAsync()
    {
        try
        {
#if CI || DEBUG
            return;
#endif
#pragma warning disable CS0162 // 检测到无法访问的代码
            await Task.Delay(500);
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
                await CheckGithubUpdateAsync();
                return;
            }

            var release = await AppConfig.GetService<UpdateService>().CheckUpdateAsync(false);
            AppConfig.LastUpdateCheckUtc = DateTimeOffset.UtcNow;

            if (release != null)
            {
                new UpdateWindow { NewVersion = release }.Activate();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check update");
        }
    }




    /// <summary>
    /// GitHub 渠道的启动检查：查本仓库的 release，有比当前新的就提示一句，
    /// 一键更新/退回在「设置 → 关于 → 更新渠道（GitHub）」里（那条路已经有版本列表和安装器）。
    /// </summary>
    private async Task CheckGithubUpdateAsync()
    {
        try
        {
            List<GithubVersionInfo> versions = await new GithubUpdateService().ListVersionsAsync();
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
            _logger.LogWarning(ex, "Check GitHub update");
        }
    }

    private async void CheckSystemProxy()
    {
        try
        {
            await Task.Delay(1500);
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
