using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.GameSelector;
using HoYoShadeHub.Features.ViewHost;
using HoYoShadeHub.Frameworks;
using HoYoShadeHub.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 左侧一级导航「插件」：**跟着顶部那行游戏列表走**的每游戏插件页。
///
/// <para>
/// 为什么按游戏分：插件**文件**是全局一份（都在 HoYoShade 的 <c>reshade-shaders\Addons</c>），
/// 但**启用/禁用是每个游戏一份** —— 写在各游戏目录的 <c>ReShade.ini</c> 的
/// <c>[ADDON] DisabledAddons</c> 里。所以同一个插件在 A 游戏关、B 游戏开是完全正常的。
/// </para>
///
/// <para>
/// 切游戏**不在这里做**：顶部那行游戏列表切换后，MainView 会用新的 GameId 重新导航本页
/// （见 MainView.UpdateNavigationView）。这里只顺着 <see cref="PageBase.CurrentGameId"/> 走。
/// 例外是「添加游戏」——刚加进来的自定义游戏不在顶部列表里，所以加完就地聚焦它，
/// 并给一个「回到当前游戏」按钮退回。
/// </para>
///
/// <para>
/// 全局那点事（装/删扩展包、重命名式全局开关）在左下角的「全局插件」页
/// （<see cref="GlobalPluginPage"/>）。
/// </para>
/// </summary>
public sealed partial class GamePluginPage : PageBase
{
    private readonly ILogger<GamePluginPage> _logger = AppConfig.GetLogger<GamePluginPage>();

    private ShadeHost? _host;
    private GameDiscoveryService? _discovery;
    private GamePluginService? _plugins;
    private GameEntry? _entry;

    /// <summary>正在按代码改控件（别把程序改的当成用户点的）</summary>
    private bool _isApplying;

    private bool _isWorking;
    private bool _isLoaded;

    public GamePluginPage()
    {
        InitializeComponent();

        // 焦点回到启动器时重读 hook 点：游戏/插件自己会改这个键，页面得跟上（用户要求）
        WeakReferenceMessenger.Default.Register<MainWindowStateChangedMessage>(this, (_, m) =>
        {
            if (m.Activate)
            {
                DispatcherQueue.TryEnqueue(UpdateHookPointUi);
            }
        });
    }

    public ObservableCollection<AddonItemViewModel> Addons { get; } = [];

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        // 页面实例被复用时（NavigationCacheMode 打开的话）也要跟着走
        if (_isLoaded)
        {
            _ = RefreshAsync();
        }
    }

    protected override void OnLoaded()
    {
        _isLoaded = true;
        AddonList.ItemsSource = Addons;
        _ = RefreshAsync();
    }

    protected override void OnUnloaded()
    {
        Addons.Clear();
    }

    #region 刷新

    private async Task RefreshAsync()
    {
        if (_isWorking)
        {
            return;
        }

        _isWorking = true;
        TextBlock_Status.Text = "正在检测游戏与插件目录…";

        try
        {
            _host = PluginHostLocator.Resolve(out string reason);
            _discovery = GameCatalog.CreateService();

            // 跟着左上角选中的那个游戏走
            _entry = GameCatalog.GetOrCreate(_discovery, CurrentGameId);

            if (_host is null)
            {
                TextBlock_TargetPath.Text = "未找到 HoYoShade 目录";
                ShowInfo("没有可管理的 HoYoShade 目录", reason, InfoBarSeverity.Warning);
            }
            else
            {
                TextBlock_TargetPath.Text = "插件目录：" + _host.AddonsPath;
                HideInfo();
            }

            if (_entry is null)
            {
                ShowEmptyState("还没有选中游戏。\n点左上角那个游戏按钮选一个；Hub 不认识游戏（比如 WeGame 上的）就用那里的「+」加进来。");
                TextBlock_GameTitle.Text = "没有游戏";
                TextBlock_GameMeta.Text = string.Empty;
                TextBlock_IniPath.Text = string.Empty;
                return;
            }

            LoadGame(_entry);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh game plugin page");
            TextBlock_Status.Text = "刷新失败：" + ex.Message;
        }
        finally
        {
            _isWorking = false;
        }
    }

    /// <summary>读这个游戏的插件列表 + 配置栏</summary>
    private void LoadGame(GameEntry entry)
    {
        try
        {
            _isApplying = true;
            TextBlock_GameTitle.Text = entry.DisplayName;
            TextBlock_GameMeta.Text = GameCatalog.Describe(entry);
            TextBlock_IniPath.Text = entry.ReShadeIniPath is { } gameIni
                ? (entry.HasReShadeIni ? "游戏目录：" + gameIni : "缺少：" + gameIni)
                : "还不知道游戏目录（先「指定主程序…」）";
            TextBlock_HookPointHint.Text = string.Empty;

            Button_CopyIni.IsEnabled = entry.ReShadeIniPath is not null && !entry.HasReShadeIni;
            Button_OpenGameFolder.IsEnabled = entry.GameDirectory is not null && Directory.Exists(entry.GameDirectory);
        }
        finally
        {
            _isApplying = false;
        }

        _plugins = new GamePluginService(
            entry,
            _host,
            PluginHostLocator.AddonNameCachePath,
            GameCatalog.AddonCandidateNames(),
            GameCatalog.TagsOfAddonFile);

        Addons.Clear();

        if (_plugins.ProfileError is { } profileError)
        {
            ShowInfo("读不了这个游戏的 ReShade.ini", profileError, InfoBarSeverity.Error);
            TextBlock_AddonsEmpty.Text = "ReShade.ini 读取失败：" + profileError;
            TextBlock_AddonsEmpty.Visibility = Visibility.Visible;
            UpdatePathHint(null);
            UpdateHookPointUi();
            return;
        }

        if (!_plugins.HasReShadeIni)
        {
            // §5：没有 ReShade.ini 的自定义游戏允许添加，只用于启动/注入
            ShowInfo("该游戏没有 ReShade.ini",
                "插件开关写在这个游戏的 ReShade.ini 里，现在还没有这份文件，所以暂时管不了插件。\n" +
                "点「复制模板到游戏目录」可以先补一份（HoYoShade 自己也会在注入时复制过去），或者直接开「注入模式」启动游戏。",
                InfoBarSeverity.Warning);
            TextBlock_AddonsEmpty.Text = "该游戏没有 ReShade.ini —— 插件开关暂时不可用。";
            TextBlock_AddonsEmpty.Visibility = Visibility.Visible;
            UpdatePathHint(null);
            UpdateHookPointUi();
            return;
        }

        // DLSS5 类插件：只要开着就默认从 DllMain 加载（用户要求）
        int synced = _plugins.SyncDlss5LoadFromDllMain();
        if (synced > 0)
        {
            _logger.LogInformation("Auto-added {Count} DLSS5 addons to LoadFromDllMain for {Game}", synced, entry.DisplayName);
        }

        foreach (GameAddonState state in _plugins.GetAddons())
        {
            Addons.Add(new AddonItemViewModel(state, OnAddonEnabledChanged, OnLoadFromDllMainChanged));
        }

        TextBlock_AddonsEmpty.Text = "插件目录里还没有 addon。到左下角「全局插件」里装一个。";
        TextBlock_AddonsEmpty.Visibility = Addons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePathHint(_plugins.AddonDirectory);
        UpdateDriverWarning();
        HideInfo();
        UpdateHookPointUi();

        int brokenCount = Addons.Count(a => a.Enabled && a.CanToggle && a.HasMissingDll);
        TextBlock_Status.Text = $"{entry.DisplayName}：{Addons.Count} 个插件，启用 {Addons.Count(a => a.Enabled)} 个。" +
                                (synced > 0 ? $"（{synced} 个 DLSS5 插件自动加入 LoadFromDllMain）" : string.Empty) +
                                (brokenCount > 0 ? $"　⚠ {brokenCount} 个缺运行时 dll" : string.Empty) +
                                (entry.ReShadeIniPath is null ? string.Empty : $"　ini：{entry.ReShadeIniPath}");

        _logger.LogInformation("Game plugin page: {Game} -> {Count} addons (enabled {Enabled}), addon dir = {Dir}, ini = {Ini}",
            entry.DisplayName, Addons.Count, Addons.Count(a => a.Enabled),
            _plugins.AddonDirectory ?? "(none)", entry.ReShadeIniPath ?? "(none)");
    }

    /// <summary>
    /// 这个游戏装了 DLSS5 插件时，检查 NVIDIA 驱动版本（用户要求的区间）：
    /// &gt;616.64 红、&lt;616.56 黄、&lt;610.47 红。
    /// </summary>
    private void UpdateDriverWarning()
    {
        try
        {
            bool usesDlss5 = Addons.Any(a => a.IsDlss5);
            if (!usesDlss5)
            {
                TextBlock_DriverWarning.Visibility = Visibility.Collapsed;
                return;
            }

            DriverCheckResult result = NvidiaDriverCheck.Check();
            if (string.IsNullOrWhiteSpace(result.Version))
            {
                // 连驱动版本都读不到就不显示这一行
                TextBlock_DriverWarning.Visibility = Visibility.Collapsed;
                return;
            }

            if (result.Level is DriverCheckLevel.Ok)
            {
                // 区间内也显示，让用户知道当前驱动版本被识别到了
                TextBlock_DriverWarning.Text = $"NVIDIA 驱动 {result.Version}（DLSS5 插件要求区间内）";
                TextBlock_DriverWarning.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
            }
            else
            {
                TextBlock_DriverWarning.Text = "⚠ " + result.Message;
                TextBlock_DriverWarning.Foreground = result.Level is DriverCheckLevel.Error
                    ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
                    : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
            }

            TextBlock_DriverWarning.Visibility = Visibility.Visible;
        }
        catch
        {
            // 查驱动失败不该影响页面
        }
    }



    private void ShowEmptyState(string message)
    {
        _plugins = null;
        Addons.Clear();
        TextBlock_AddonsEmpty.Text = message;
        TextBlock_AddonsEmpty.Visibility = Visibility.Visible;
        UpdatePathHint(null);
        UpdateHookPointUi();
    }

    /// <summary>
    /// 这份 ini 的 <c>AddonPath</c> 和当前 HoYoShade 的插件目录不一致时给一句人话。
    /// 红/黄标是按**这个游戏 ini 指的目录**算的（ReShade 运行时就是这么加载的），
    /// 所以两边对不上时得说清楚是哪一边，附一个「指回当前 HoYoShade」的按钮。
    /// </summary>
    private void UpdatePathHint(string? addonDirectory)
    {
        string? hostAddons = _host?.AddonsPath;
        bool differs = !string.IsNullOrWhiteSpace(addonDirectory)
                       && !string.IsNullOrWhiteSpace(hostAddons)
                       && !string.Equals(addonDirectory.TrimEnd('\\', '/'), hostAddons!.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

        if (!differs)
        {
            TextBlock_PathHint.Visibility = Visibility.Collapsed;
            Button_AlignIni.Visibility = Visibility.Collapsed;
            return;
        }

        TextBlock_PathHint.Text = $"⚠ 这个游戏的 ReShade.ini 指向 {addonDirectory}，不是当前 HoYoShade 的插件目录（{hostAddons}）。" +
                                  "上面的红/黄标是按**游戏 ini 指的目录**算的 —— ReShade 运行时就是去那儿找插件和 dll 的。" +
                                  "启动/注入时 Hub 会自动把它指回当前 HoYoShade，也可以现在就点右边的按钮。";
        TextBlock_PathHint.Visibility = Visibility.Visible;
        Button_AlignIni.Visibility = Visibility.Visible;
    }

    #endregion

    #region 每游戏开关

    /// <summary>
    /// 这个游戏现在在跑吗？在跑的时候改 ReShade.ini 有个坑：
    /// ReShade 退出时会把它内存里的配置写回 ini，可能把我们刚写的开关覆盖掉 ——
    /// 用户反馈「启用插件似乎无效，进去还是灰的」，多半就是这个。
    /// </summary>
    private bool IsGameRunning()
    {
        try
        {
            string? processName = _entry?.ProcessName;
            if (string.IsNullOrWhiteSpace(processName))
            {
                return false;
            }

            using Process? running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(processName))
                .FirstOrDefault();
            return running is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>改动会立刻落盘，但游戏在跑时给一句提醒</summary>
    private void WarnIfGameRunning()
    {
        if (IsGameRunning())
        {
            TextBlock_Status.Text = "⚠ 这个游戏正在运行 —— ReShade 退出时可能把 ReShade.ini 覆盖回去，" +
                                    "这次改动不一定保住；建议关掉游戏再改。";
        }
    }

    private void OnAddonEnabledChanged(AddonItemViewModel item, bool enabled)
    {
        if (_plugins is null)
        {
            return;
        }

        if (_plugins.SetAddonEnabled(item.FileName, enabled))
        {
            _logger.LogInformation("Addon {File} {State} for {Game}", item.FileName, enabled ? "enabled" : "disabled", _plugins.Game.DisplayName);
            TextBlock_Status.Text = $"「{item.Name}」在 {_plugins.Game.DisplayName} 里已{(enabled ? "启用" : "禁用")}。" +
                                    $"（只影响这个游戏，写在 {_plugins.ReShadeIniPath}）";
            WarnIfGameRunning();

            // 神经插帧器：启用时顺带把它要的 nvngx_dlssnr.dll 复制到游戏 exe 旁边（用户要求）
            if (enabled && GamePluginService.IsNeuralInterposer(item.FileName))
            {
                List<string> copied = _plugins.EnsureInterposerDlls();
                TextBlock_Status.Text += copied.Count > 0
                    ? $"　已把 {string.Join("、", copied)} 复制到游戏目录。"
                    : "　游戏目录里已经有一份一样的 nvngx_dlssnr.dll，不用复制。";
            }
        }
        else
        {
            item.RevertEnabled();
            TextBlock_Status.Text = "写入失败 —— 这个游戏的 ReShade.ini 可能被占用或只读。";
        }
    }

    private void OnLoadFromDllMainChanged(AddonItemViewModel item, bool value)
    {
        if (_plugins is null)
        {
            return;
        }

        if (_plugins.SetLoadFromDllMain(item.FileName, value))
        {
            TextBlock_Status.Text = value
                ? $"「{item.Name}」改成从 DllMain 加载。"
                : $"「{item.Name}」不再从 DllMain 加载（空槽位保留，不重排）。";
            WarnIfGameRunning();
        }
        else
        {
            item.RevertLoadFromDllMain();
            TextBlock_Status.Text = "写入失败 —— 这个游戏的 ReShade.ini 可能被占用或只读。";
        }
    }

    private void UpdateHookPointUi()
    {
        bool canEdit = _plugins is { HasReShadeIni: true } && _plugins.CanEditHookPoint();

        _isApplying = true;
        try
        {
            ComboBox_HookPoint.IsEnabled = canEdit;
            ComboBox_HookPoint.SelectedIndex = Math.Clamp(_plugins?.GetHookPoint() ?? 0, 0, 4);

            bool forceOff = CurrentGameId is { } gameId && AppConfig.GetForceHookOffOnLaunch(gameId.GameBiz);
            CheckBox_ForceHookOff.IsChecked = forceOff;
            CheckBox_ForceHookOff.IsEnabled = canEdit;

            bool hookStreamline = _plugins?.Profile?.IsHookStreamlineEnabled() == true;
            CheckBox_HookStreamline.IsChecked = hookStreamline;
            CheckBox_HookStreamline.IsEnabled = canEdit;
        }
        finally
        {
            _isApplying = false;
        }

        TextBlock_HookPointHint.Text = canEdit
            ? string.Empty
            : _plugins is { HasReShadeIni: false }
                ? "该游戏没有 ReShade.ini，改不了。"
                : "当前不允许改：需要先装 RenoDX DLSS 或 RenoDX DLSS5 Super Anus。";
    }



    /// <summary>「启动游戏时强制 off」：存到 AppConfig，启动/注入时生效</summary>
    private void CheckBox_ForceHookOff_Changed(object sender, RoutedEventArgs e)
    {
        if (_isApplying || CurrentGameId is not { } gameId)
        {
            return;
        }

        bool value = CheckBox_ForceHookOff.IsChecked == true;
        AppConfig.SetForceHookOffOnLaunch(gameId.GameBiz, value);
        TextBlock_Status.Text = value
            ? "已开启：以后从这里启动/注入这个游戏之前，会自动把 hook 点写成 0。"
            : "已关闭：启动前不再动 hook 点。";
    }

    private void CheckBox_HookStreamline_Changed(object sender, RoutedEventArgs e)
    {
        if (_isApplying || _plugins is { HasReShadeIni: false })
        {
            return;
        }

        bool value = CheckBox_HookStreamline.IsChecked == true;
        if (_plugins?.Profile is { } profile)
        {
            profile.SetHookStreamline(value);
            profile.Save();
            TextBlock_Status.Text = value
                ? "已写入 [INSTALL] HookStreamline=1：DLSS 插件会跳过 Streamline 的 Present 钩子。"
                : "已写入 HookStreamline=0。";
        }
    }

    private void ComboBox_HookPoint_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isApplying || _plugins is null)
        {
            return;
        }

        int value = Math.Clamp(ComboBox_HookPoint.SelectedIndex, 0, 4);
        if (_plugins.SetHookPoint(value))
        {
            TextBlock_Status.Text = value == 0
                ? "Hook Point 已设为 off（写的是 0，不是删键）。"
                : $"Hook Point 已设为 {value}。";
        }
        else
        {
            TextBlock_Status.Text = "Hook Point 写不进去 —— 要么没有 ReShade.ini，要么没有满足前置条件的插件。";
            UpdateHookPointUi();
        }
    }

    #endregion

    #region 顶部按钮

    private async void Button_Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    /// <summary>
    /// 右上角「DLSS5 兼容性检测」：弹窗列 15 条（显卡 / 启动器 / 游戏目录 / XXMI），
    /// 7/9/10/11/12/13/14/15 带自动修复。
    ///
    /// <para>
    /// 用页面已经解析好的当前游戏 + HoYoShade 宿主，不再重新探测一遍（避免和列表显示的不是同一份）。
    /// </para>
    /// </summary>
    private async void Button_Dlss5CompatCheck_Click(object sender, RoutedEventArgs e)
    {
        ShadeHost? host = _host ?? PluginHostLocator.Resolve(out _);

        if (host is null)
        {
            ShowInfo("DLSS5 兼容性检测", "还没找到 HoYoShade 目录  先到「全局插件」页点「指定目录」。", InfoBarSeverity.Warning);
            return;
        }

        var context = new Dlss5CompatContext
        {
            ShadeHost = host,
            Game = _entry,
            GameId = CurrentGameId,
            Profile = _plugins?.Profile,
            ProfileError = _plugins?.ProfileError,
            AddonStates = _plugins?.GetAddons(),
            HookPoint = _plugins?.GetHookPoint() ?? 0,
            PluginService = _plugins,
        };

        var dialog = new Dlss5CompatDialog(context)
        {
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private async void Button_PickExe_Click(object sender, RoutedEventArgs e)
    {
        if (_discovery is null || _entry is null)
        {
            return;
        }

        try
        {
            string? exe = await FileDialogHelper.PickSingleFileAsync(XamlRoot, ("游戏主程序", ".exe"));
            if (string.IsNullOrWhiteSpace(exe))
            {
                return;
            }

            _discovery.UpdateExePath(_entry, exe);

            if (_entry.IsCustom)
            {
                GameCatalog.RegisterCustomGame(_entry);
                WeakReferenceMessenger.Default.Send(new CustomGameAddedMessage());
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pick game exe");
            ShowInfo("指定主程序失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    #endregion

    #region ReShade.ini 工具

    private async void Button_BuildIni_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重置模板 ReShade.ini",
            Content = "跑一次 HoYoShade\\LauncherResource\\INIBuild.exe，重新生成 HoYoShade 根目录下的模板 ReShade.ini" +
                      "（等于启动器 bat 里的「重置 ReShade.ini」）。\n\n" +
                      "注意：它会按插件目录重写模板里的 DisabledAddons —— 新装的插件默认是关的；" +
                      "已经复制到各游戏目录的那份不会被它改动。",
            PrimaryButtonText = "执行",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        TextBlock_Status.Text = "正在跑 INIBuild.exe…";
        IniBuildResult result = await ReShadeIniBuilder.RunAsync(_host);

        if (result.Ok)
        {
            _logger.LogInformation("INIBuild ok: {Path}", _host.ReShadeIniPath);
            ShowInfo("模板已重新生成", _host.ReShadeIniPath, InfoBarSeverity.Success);
            TextBlock_Status.Text = "模板 ReShade.ini 已重新生成：" + _host.ReShadeIniPath;
        }
        else
        {
            _logger.LogWarning("INIBuild failed: {Reason}", result.FailureReason);
            ShowInfo("生成失败", result.FailureReason ?? "未知原因", InfoBarSeverity.Error);
            TextBlock_Status.Text = "INIBuild 失败：" + result.FailureReason;
        }
    }

    private async void Button_CopyIni_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null || _entry is not { } entry)
        {
            return;
        }

        if (entry.ReShadeIniPath is not { } target)
        {
            TextBlock_Status.Text = "这个游戏还不知道游戏目录（先「指定主程序…」）。";
            return;
        }

        if (File.Exists(target))
        {
            TextBlock_Status.Text = "这个游戏已经有 ReShade.ini 了。";
            return;
        }

        if (!File.Exists(_host.ReShadeIniPath))
        {
            ShowInfo("模板还没有", "HoYoShade 根目录下没有 ReShade.ini，先点「重置模板 ReShade.ini」。", InfoBarSeverity.Warning);
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "复制模板到游戏目录",
            Content = $"把 {_host.ReShadeIniPath}\n\n复制到\n\n{target}\n\n" +
                      "HoYoShade 自己也会在注入时复制过去，这里只是让你现在就能改这个游戏的插件开关。",
            PrimaryButtonText = "复制",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            File.Copy(_host.ReShadeIniPath, target);
            _logger.LogInformation("Copied ReShade.ini template to {Target}", target);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy ReShade.ini");
            ShowInfo("复制失败", ex.Message, InfoBarSeverity.Error);
            return;
        }

        await RefreshAsync();
        TextBlock_Status.Text = "已复制到 " + target;
    }

    #endregion

    #region 打开目录

    private void Button_OpenGameFolder_Click(object sender, RoutedEventArgs e)
    {
        string? path = _entry?.GameDirectory;
        if (path is null || !Directory.Exists(path))
        {
            TextBlock_Status.Text = "还不知道游戏目录。";
            return;
        }

        OpenInExplorer(path);
    }

    /// <summary>「指回当前 HoYoShade」：把这个游戏 ini 里的绝对路径改到当前宿主</summary>
    private void Button_AlignIni_Click(object sender, RoutedEventArgs e)
    {
        if (_entry?.ReShadeIniPath is not { } gameIni || !File.Exists(gameIni))
        {
            ShowInfo("没有 ReShade.ini", "这份 ini 还不存在，等注入时会按当前 HoYoShade 生成一份。", InfoBarSeverity.Warning);
            return;
        }

        if (_host is null)
        {
            ShowInfo("没有 HoYoShade", "先在上面指定一个 HoYoShade 目录。", InfoBarSeverity.Warning);
            return;
        }

        try
        {
            ShadePathAlignResult align = ShadePathAligner.Align(gameIni, _host);
            if (!align.Changed)
            {
                ShowInfo("不用改", "这份 ini 里的路径已经指向当前 HoYoShade 了。", InfoBarSeverity.Success);
                return;
            }

            _logger.LogInformation("ReShade.ini paths re-pointed from {Old} to {New}: {Keys}",
                align.PreviousRoot, _host.RootPath, string.Join(", ", align.ChangedKeys));
            ShowInfo("已指回当前 HoYoShade",
                $"原来指向 {align.PreviousRoot}，改了：{string.Join("、", align.ChangedKeys)}。其余设置（预设、RenoDX 参数、按键）都没动。",
                InfoBarSeverity.Success);
            _ = RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Align ReShade.ini paths");
            ShowInfo("改不动这份 ini", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void Button_OpenAddonFolder_Click(object sender, RoutedEventArgs e)
    {
        string? path = _plugins?.AddonDirectory ?? _host?.AddonsPath;
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            TextBlock_Status.Text = "找不到插件目录。";
            return;
        }

        OpenInExplorer(path);
    }

    private void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open folder {Path}", path);
        }
    }

    #endregion

    #region InfoBar

    private void ShowInfo(string title, string message, InfoBarSeverity severity)
    {
        InfoBar_Status.Title = title;
        InfoBar_Status.Message = message;
        InfoBar_Status.Severity = severity;
        InfoBar_Status.IsOpen = true;
    }

    private void HideInfo() => InfoBar_Status.IsOpen = false;

    #endregion
}

/// <summary>插件列表的一行（某个游戏里的某个 addon）</summary>
public partial class AddonItemViewModel : ObservableObject
{
    private readonly Action<AddonItemViewModel, bool> _onEnabledChanged;
    private readonly Action<AddonItemViewModel, bool> _onLoadFromDllMainChanged;

    /// <summary>构造时塞初值不算「用户改的」</summary>
    private bool _suppress;

    public AddonItemViewModel(
        GameAddonState state,
        Action<AddonItemViewModel, bool> onEnabledChanged,
        Action<AddonItemViewModel, bool> onLoadFromDllMainChanged)
    {
        _onEnabledChanged = onEnabledChanged;
        _onLoadFromDllMainChanged = onLoadFromDllMainChanged;

        FileName = state.FileName;
        Name = state.DisplayName;
        CanToggle = state.CanToggle;
        IsDlss5 = state.IsDlss5;

        // 缺 dll 的标记（红 = 缺必需，黄 = 缺建议）
        DllSeverity = state.DllStatus.Severity;
        DllStatusText = state.DllStatus.Summary;

        var parts = new List<string>();
        parts.Add(string.IsNullOrWhiteSpace(state.Version) ? "版本未知" : "版本 " + state.Version);
        if (!string.IsNullOrWhiteSpace(state.Branch))
        {
            parts.Add(state.Branch!);
        }
        MetaText = string.Join(" · ", parts);

        _suppress = true;
        Enabled = state.Enabled;
        LoadFromDllMain = state.LoadFromDllMain;
        _suppress = false;
    }

    public string FileName { get; }

    public string Name { get; }

    public string MetaText { get; }

    public bool CanToggle { get; }

    /// <summary>DLSS5 那一类插件（驱动版本检查只对它们做）</summary>
    public bool IsDlss5 { get; }

    [ObservableProperty]
    private bool enabled;

    [ObservableProperty]
    private bool loadFromDllMain;

    public Visibility GloballyDisabledVisibility => CanToggle ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>这个游戏关掉插件时，LoadFromDllMain 也就没意义了（ini 里已经摘掉），界面变灰</summary>
    public bool CanEditLoadFromDllMain => CanToggle && Enabled;

    /// <summary>0 = 没问题，1 = 缺建议（黄），2 = 缺必需（红）</summary>
    public int DllSeverity { get; }

    public string DllStatusText { get; }

    public bool HasMissingDll => DllSeverity > 0;

    public Visibility DllRequiredVisibility => DllSeverity >= 2 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DllRecommendedVisibility => DllSeverity == 1 ? Visibility.Visible : Visibility.Collapsed;

    partial void OnEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanEditLoadFromDllMain));

        if (_suppress)
        {
            return;
        }

        _onEnabledChanged(this, value);
    }

    partial void OnLoadFromDllMainChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        _onLoadFromDllMainChanged(this, value);
    }

    /// <summary>写盘失败时把开关拨回去</summary>
    public void RevertEnabled()
    {
        _suppress = true;
        Enabled = !Enabled;
        _suppress = false;
    }

    public void RevertLoadFromDllMain()
    {
        _suppress = true;
        LoadFromDllMain = !LoadFromDllMain;
        _suppress = false;
    }
}
