using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using HoYoShadeHub.Extensions.Dlls;
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
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    /// <summary>
    /// 页面还挂在可视树上吗。
    ///
    /// <para>
    /// <c>MainWindowStateChangedMessage</c> 在**每次窗口激活**时都会发（WM_ACTIVATE），
    /// 而页面卸载后它的 WinRT 对象已经废了 —— 此时再读 <c>DispatcherQueue</c> / 控件会抛
    /// <c>ObjectDisposedException</c> 把整个进程带崩（线上 260924 日志里 5 次崩溃全是这一条）。
    /// 所以处理器在碰页面之前，必须先看这个纯托管标志。
    /// </para>
    /// </summary>
    private bool _isPageAlive;

    public GamePluginPage()
    {
        InitializeComponent();
        Loaded += GamePluginPage_Loaded;

        // 订阅**不放在构造函数里**：页面实例会被复用 / 重建，构造函数里的订阅没有配对的退订点，
        // 页面一释放就成了「僵尸订阅」 —— 见 OnLoaded / OnUnloaded 与 _isPageAlive。
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
        _isPageAlive = true;
        RegisterWindowStateHandler();
        AddonList.ItemsSource = Addons;
        _ = RefreshAsync();
    }

    protected override void OnUnloaded()
    {
        // 先标记、再退订：订阅必须跟页面生命周期成对，
        // 否则窗口每次激活都会摸到已经释放的页面（ObjectDisposedException 崩进程）
        _isPageAlive = false;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        Addons.Clear();
    }

    /// <summary>
    /// 焦点回到启动器时重读 hook 点：游戏 / 插件自己会改这个键，页面得跟上（用户要求）。
    /// 先退订再订阅，页面复用时不会重复挂。
    /// </summary>
    private void RegisterWindowStateHandler()
    {
        WeakReferenceMessenger.Default.Unregister<MainWindowStateChangedMessage>(this);
        WeakReferenceMessenger.Default.Register<MainWindowStateChangedMessage>(this, (_, m) =>
        {
            if (!m.Activate || !_isPageAlive)
            {
                return;
            }

            // 排队期间页面可能已经被卸载，回调里再确认一次
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_isPageAlive)
                {
                    UpdateHookPointUi();
                }
            });
        });
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

            Button_CopyIni.IsEnabled = entry.ReShadeIniPath is not null && !entry.HasReShadeIni;
            Button_OpenGameFolder.IsEnabled = entry.GameDirectory is not null && Directory.Exists(entry.GameDirectory);
        }
        finally
        {
            _isApplying = false;
        }

        // 注入时机（HoYoShade / ReShade 这一步）：按游戏，独立于插件列表是否可用
        UpdateShadeInjectDelayUi();

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

        // DLSS5 Feed：效果开关（LumeniteFX Kernel + DLSS5_Feed）写在预设里，
        // 跟着这个游戏的插件开关同步一次；切游戏时还会走 GameCatalog.SyncDlss5FeedPreset
        _plugins.SyncDlss5FeedPreset(out _);

        // DLSS5 类插件：只要开着就默认从 DllMain 加载（用户要求）
        int synced = _plugins.SyncDlss5LoadFromDllMain();
        if (synced > 0)
        {
            _logger.LogInformation("Auto-added {Count} DLSS5 addons to LoadFromDllMain for {Game}", synced, entry.DisplayName);
        }

        // 需求1：同一个插件装了多个版本时，版本下拉就长在**这张插件卡片里**
        var versionStore = new AddonVersionStore(AppConfig.CacheRoot);
        var tagsByExtension = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // 需求1：DLSS5 Feed 的 create_delay / warmup_rebuild 就在 addon 目录那份 cfg 里，
        // 所有卡片读同一份（只有 Feed 那张卡片会显示这两个输入框）
        Dlss5FeedConfig? feedConfig = Dlss5FeedConfig.Load(_plugins.AddonDirectory);

        foreach (GameAddonState state in _plugins.GetAddons())
        {
            var item = new AddonItemViewModel(state, OnAddonEnabledChanged, OnLoadFromDllMainChanged)
            {
                VersionDispatchQueue = DispatcherQueue,
                HookPointChanged = OnAddonHookPointChanged,
                Dx11SourceChanged = OnAddonDx11SourceChanged,
                EnableHooksChanged = OnAddonEnableHooksChanged,
                ForceHookOffChanged = OnAddonForceHookOffChanged,
                FeedConfigChanged = OnAddonFeedConfigChanged,
            };
            ApplyAddonVersionChoice(item, versionStore, tagsByExtension);
            item.ConfigureDedicatedConfig(
                item.IsRenoDxDlss5Main ? _plugins.GetDlss5HookPoint() : _plugins.GetHookPoint(),
                feedConfig?.CreateDelay ?? Dlss5FeedConfig.DefaultCreateDelay,
                feedConfig?.WarmupRebuild ?? Dlss5FeedConfig.DefaultWarmupRebuild,
                feedConfig?.Path,
                Dx11SourceToIndex(item.IsRenoDxDlss5Main ? _plugins.GetDx11Source() : null),
                item.IsRenoDxDlss5Main && _plugins.IsEnableHooksOn(),
                CurrentGameId is { } gid && AppConfig.GetForceHookOffOnLaunch(gid.GameBiz));
            Addons.Add(item);
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
    ///
    /// <para>
    /// 例外：<b>每游戏插件包</b>（<c>&lt;CacheRoot&gt;\games\&lt;游戏&gt;\Addons</c>）是新系统的
    /// 正常状态，不是「指向了别的 HoYoShade」。这时候只给一句中性说明，按钮收起来 ——
    /// 点它反而会把包路径改回共享目录，破坏「这个游戏用哪一版」。
    /// 宿主根目录**里面**的子目录（例：上游自带的「若DX11崩溃或无效请解压」备用包解压在
    /// Addons 子目录）同理 —— 不算指向别的 HoYoShade，不报「不一致」也不劝人指回。
    /// </para>
    /// </summary>
    private void UpdatePathHint(string? addonDirectory)
    {
        string? hostAddons = _host?.AddonsPath;

        // 这个游戏用的就是专属插件目录（每游戏插件包）—— 正常状态：什么都不用提示
        //（用户要求去掉这条长文案，给插件卡片列表腾地方；「指回当前 HoYoShade」也不适用于这种目录）
        if (GameAddonPack.IsPackDirectory(addonDirectory))
        {
            TextBlock_PathHint.Visibility = Visibility.Collapsed;
            Button_AlignIni.Visibility = Visibility.Collapsed;
            return;
        }

        // 宿主根目录里面的子目录（上游备用包解压在 Addons 子目录那种）不算「指向别的 HoYoShade」
        // —— 对齐器按根对齐，不会动它；按整串相等判会把「指回」变成永远无效的空操作（用户报过）。
        bool differs = !string.IsNullOrWhiteSpace(addonDirectory)
                       && !string.IsNullOrWhiteSpace(hostAddons)
                       && !string.Equals(addonDirectory.TrimEnd('\\', '/'), hostAddons!.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                       && !IsInsideHostRoot(addonDirectory, _host?.RootPath);

        if (!differs)
        {
            TextBlock_PathHint.Visibility = Visibility.Collapsed;
            Button_AlignIni.Visibility = Visibility.Collapsed;
            return;
        }

        TextBlock_PathHint.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
        TextBlock_PathHint.Text = $"⚠ 这个游戏的 ReShade.ini 指向 {addonDirectory}，不是当前 HoYoShade 的插件目录（{hostAddons}）。" +
                                  "上面的红/黄标是按**游戏 ini 指的目录**算的 —— ReShade 运行时就是去那儿找插件和 dll 的。" +
                                  "启动/注入时 Hub 会自动把它指回当前 HoYoShade，也可以现在就点右边的按钮。";
        TextBlock_PathHint.Visibility = Visibility.Visible;
        Button_AlignIni.Visibility = Visibility.Visible;
    }

    /// <summary>路径落在当前 HoYoShade 根目录里面吗（子目录也算；分隔符一律取运行时常量）</summary>
    private static bool IsInsideHostRoot(string? path, string? hostRoot)
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

    /// <summary>插件包标记里记的「扩展 id → tag」，拼成一句给人看的版本说明</summary>
    private static string DescribePackSelections(string? addonDirectory)
    {
        try
        {
            Dictionary<string, string> selections = GameAddonPack.ReadSelections(addonDirectory);
            return selections.Count == 0
                ? string.Empty
                : "（插件版本：" + string.Join("、", selections.Select(kv => $"{kv.Key}={kv.Value}")) + "）";
        }
        catch
        {
            return string.Empty;
        }
    }

    #endregion

    #region 每游戏插件版本（需求1）

    /// <summary>
    /// 把「这个 addon 属于哪个扩展、归档里有几个版本、这个游戏选的是哪一版」填进**这张插件卡片**。
    /// 归档里 &gt;= 2 个版本才显示下拉；只有一个 / 认不出扩展时整条不出现（卡片保持原来那一行）。
    /// </summary>
    private void ApplyAddonVersionChoice(
        AddonItemViewModel item,
        AddonVersionStore store,
        Dictionary<string, List<string>> tagsByExtension)
    {
        try
        {
            if (CurrentGameId is not { } gameId)
            {
                return;
            }

            // addon 文件 → 扩展 id（靠扩展目录里的 addonPatterns 认领）
            string? extensionId = GameCatalog.ExtensionIdOfAddonFile(item.FileName);
            if (string.IsNullOrWhiteSpace(extensionId))
            {
                return;
            }

            if (!tagsByExtension.TryGetValue(extensionId, out List<string>? tags))
            {
                tags = store.InstalledTags(extensionId);
                tagsByExtension[extensionId] = tags;
            }

            item.ConfigureVersionChoice(
                extensionId,
                tags,
                AppConfig.GetPluginVersion(gameId, extensionId));
            item.VersionChanged = OnAddonVersionChanged;
        }
        catch
        {
            // 归档读不出来就整条不显示，不影响插件列表
        }
    }

    /// <summary>用户在这张卡片里换了版本：落盘 + 重拼这个游戏的插件包 + 重新读盘</summary>
    private void OnAddonVersionChanged(AddonItemViewModel item, string? tag)
    {
        if (CurrentGameId is not { } gameId || item.ExtensionId is not { } extensionId)
        {
            return;
        }

        // 下拉是 TwoWay 绑定：刷新列表时**重新赋一次同样的值**也会走到这里。
        // 值没变就立刻返回 —— 否则会变成「刷新 → 重新赋值 → 回调 → 再刷新」的无限循环，
        // 目录在共享 / 专属包之间来回翻、ReShade.ini 被反复写（用户报的切换版本时崩溃就是这个）。
        string? current = AppConfig.GetPluginVersion(gameId, extensionId);
        if (string.Equals(current ?? string.Empty, tag ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppConfig.SetPluginVersion(gameId, extensionId, tag);

        try
        {
            string? pack = GameAddonPackService.Sync(gameId, _entry, _host);
            TextBlock_Status.Text = tag is null
                ? $"「{item.Name}」改回默认版本；这个游戏专属的插件包已收掉。"
                : pack is null
                    ? $"「{item.Name}」选了 {tag}，但没拼出插件包（归档里可能没有这一版）。"
                    : $"「{item.Name}」在这个游戏里改用 {tag}；插件目录已指到专属包：{pack}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sync addon pack for {Game}", gameId.GameBiz);
            TextBlock_Status.Text = "同步插件包失败：" + ex.Message;
        }

        // 这里**只更新「插件目录」那行说明，绝不重建列表**。
        //
        // 这个回调是 ComboBox 改选中项触发的，用户此刻往往正开着下拉框（或刚关）。
        // 这时候 Clear + 重建绑在 ItemsControl / ComboBox 上的集合，是 WinUI 3 已知的崩法：
        // 原生 AV → user32 弹「Exception Processing Message 0xc0000005」→ 点确定进程就没了
        // （用户报的 HoYoShadeHubTrayMenu 系统错误；就算不崩，刷新完下拉也会变成空的）。
        //
        // 换版本只改游戏 ini 的 [ADDON] AddonPath 和专属包目录，插件清单本身没变，
        // 所以不需要整页重读 —— 把那一行说明按现读的 ini 更新一下就够了。
        UpdatePathHint(ResolveCurrentAddonDirectory());
    }

    /// <summary>
    /// 从 ini 现读一次这个游戏实际用的插件目录。
    /// <c>_plugins.AddonDirectory</c> 是建服务时缓存的，换版本（改了 AddonPath）之后会过期。
    /// </summary>
    private string? ResolveCurrentAddonDirectory()
    {
        try
        {
            if (_entry?.ReShadeIniPath is { } ini && File.Exists(ini))
            {
                string? resolved = ReShadeProfile.Load(ini).ResolveAddonDirectory();
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return resolved;
                }
            }
        }
        catch
        {
            // 读不动就用缓存那份
        }

        return _plugins?.AddonDirectory;
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

            // 需求5：开关一动，LoadFromDllMain 会被服务里那条规则自动补上 / 摘掉 ——
            // 卡片上那个勾要跟着盘上的真实状态走，不能停在旧值（展开后看到的才是真的）
            item.RefreshLoadFromDllMain(_plugins.IsLoadFromDllMain(item.FileName));

            // DLSS5 Feed：预设里的效果开关跟着一起改（共享预设，切游戏会再同步）
            if (GamePluginService.IsDlss5FeedAddon(item.FileName))
            {
                _ = _plugins.SyncDlss5FeedPreset(out string? presetNote);
                TextBlock_Status.Text += string.IsNullOrWhiteSpace(presetNote)
                    ? "　已同步 ReShade 预设：LumeniteFX Kernel + DLSS5 Feed 两个效果。"
                    : "　但 ReShade 预设没同步上：" + presetNote;
            }

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

    /// <summary>插件卡片列表的错峰入场（和顶部游戏栏同一套参数：500ms + 45ms 一档）。</summary>
    private void GamePluginPage_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= GamePluginPage_Loaded;   // 只播一次：每次切页回来都重播会腻
        DispatcherQueue.TryEnqueue(() => MotionAnimations.PlayListEntrance(AddonList));
    }

    /// <summary>需求3：卡片右下角那个展开 / 缩回按钮（专属配置）</summary>
    private void Button_ExpandAddon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AddonItemViewModel item })
        {
            return;
        }

        item.IsExpanded = !item.IsExpanded;

        // 通用实现在 MotionAnimations.PlayItemAreaExpand（全局插件页等共用同一套）
        MotionAnimations.PlayItemAreaExpand(AddonList, item, "AddonDedicatedConfig", item.IsExpanded);
    }


    /// <summary>
    /// 卡片里的 HookPoint：RenoDX DLSS 写 <c>[RENODX-DLSS] DirectNeuralRenderingHookPoint</c>，
    /// RenoDX DLSS5 写 <c>[RenoDX.DLSS5] NRHookPoint</c>（需求2：两个插件各写各的键）。
    /// </summary>
    private void OnAddonHookPointChanged(AddonItemViewModel item, int value)
    {
        if (_plugins is null)
        {
            return;
        }

        bool ok = item.IsRenoDxDlss5Main ? _plugins.SetDlss5HookPoint(value) : _plugins.SetHookPoint(value);
        if (!ok)
        {
            TextBlock_Status.Text = "Hook Point 写不进去 —— 要么没有 ReShade.ini，要么对应的那个插件没装。";
            item.RefreshHookPoint(item.IsRenoDxDlss5Main ? _plugins.GetDlss5HookPoint() : _plugins.GetHookPoint());
            return;
        }

        TextBlock_Status.Text = value == 0
            ? $"「{item.Name}」的 HookPoint 已设为 off（盘上写 0，不是删键）。"
            : $"「{item.Name}」的 HookPoint 已设为 {value}。";
        WarnIfGameRunning();
    }

    /// <summary>卡片上的 DX11Source 三态下拉：0 = 默认（删键）/ 1 = native / 2 = foreign</summary>
    private void OnAddonDx11SourceChanged(AddonItemViewModel item, int value)
    {
        if (_plugins is null)
        {
            return;
        }

        string? iniValue = value switch
        {
            1 => "native",
            2 => "foreign",
            _ => null,
        };

        if (_plugins.SetDx11Source(iniValue))
        {
            TextBlock_Status.Text = iniValue is null
                ? "已删掉 [RenoDX.DLSS5] DX11Source 键（回插件默认）。重启游戏生效。"
                : $"已写入 [RenoDX.DLSS5] DX11Source={iniValue}。重启游戏生效。";
            WarnIfGameRunning();
        }
        else
        {
            TextBlock_Status.Text = "DX11Source 写不进去 —— 要么没有 ReShade.ini，要么插件没启用。";
            item.RefreshDx11Source(Dx11SourceToIndex(_plugins.GetDx11Source()));
        }
    }

    /// <summary>DX11Source 的 ini 值 → 下标（0 = 不写键 / 1 = native / 2 = foreign）</summary>
    private static int Dx11SourceToIndex(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "native" => 1,
        "foreign" => 2,
        _ => 0,
    };

    /// <summary>卡片上的 EnableHooks 开关：开 = 写 [RenoDX.DLSS5] EnableHooks=1（Streamline 挂钩），关 = 删键回默认</summary>
    private void OnAddonEnableHooksChanged(AddonItemViewModel item, bool value)
    {
        if (_plugins is null)
        {
            return;
        }

        if (_plugins.SetEnableHooks(value))
        {
            TextBlock_Status.Text = value
                ? "已写入 [RenoDX.DLSS5] EnableHooks=1（Streamline 挂钩）。重启游戏生效。"
                : "已删掉 EnableHooks 键（回插件默认）。重启游戏生效。";
            WarnIfGameRunning();
        }
        else
        {
            TextBlock_Status.Text = "EnableHooks 写不进去 —— 要么没有 ReShade.ini，要么插件没启用。";
            item.RefreshEnableHooks(_plugins.IsEnableHooksOn());
        }
    }

    /// <summary>
    /// 卡片上的「修复」：缺哪个家族就弹哪个的版本清单（Streamline 默认选 2.14.0.0 ——
    /// 新版包里已经没有 sl.interposer.dll），下载装进插件目录，装完就地把红横幅刷掉。
    /// </summary>
    private async void Button_FixDll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AddonItemViewModel card } || _plugins is null)
        {
            return;
        }

        string familyId = card.MissingDllFiles.Any(f => string.Equals(f, "nvngx_dlssnr.dll", StringComparison.OrdinalIgnoreCase))
            ? "dlssnr"
            : "streamline";
        DllFamily? family = DllComponentCatalog.FamilyOf(familyId);
        if (family is null)
        {
            return;
        }

        var catalog = await DllComponentCatalog.LoadAsync();
        IReadOnlyList<DllComponent> versions = catalog.Of(familyId);
        if (versions.Count == 0)
        {
            TextBlock_Status.Text = "拉不到组件清单（多半是网络不通），稍后再试一次。";
            return;
        }

        var combo = new ComboBox { MinWidth = 260, SelectedIndex = 0 };
        foreach (DllComponent component in versions)
        {
            combo.Items.Add(component.Version + (component.Version == family.PreferredVersion ? "  ← 推荐" : string.Empty));
        }

        int preferred = versions
            .Select((component, index) => (component, index))
            .FirstOrDefault(pair => string.Equals(pair.component.Version, family.PreferredVersion, StringComparison.OrdinalIgnoreCase)).index;
        combo.SelectedIndex = preferred < 0 ? 0 : preferred;

        var hint = new TextBlock
        {
            Text = family.Id == "streamline"
                ? "选一个 Streamline 运行时版本装进插件目录。2.14.1.0 起的包里已经没有 sl.interposer.dll，所以默认给你选 2.14.0.0。"
                : "选一个版本装进插件目录。",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(hint);
        panel.Children.Add(combo);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "修复 " + family.DisplayName,
            Content = panel,
            PrimaryButtonText = "下载并安装",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || combo.SelectedIndex < 0)
        {
            return;
        }

        DllComponent chosen = versions[combo.SelectedIndex];
        TextBlock_Status.Text = $"正在下载 {family.DisplayName} {chosen.Version}…";
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
                TextBlock_Status.Text = $"正在下载 {chosen.Version}… "
                    + (p.Percent is { } percent ? $"{percent:F0}%（" : string.Empty)
                    + $"{p.BytesReceived / 1024d / 1024d:F1} MB）");

            DllInstallResult install = await DllInstaller.InstallAsync(_plugins.AddonDirectory, chosen, progress);
            if (!install.Ok)
            {
                TextBlock_Status.Text = "安装失败：" + install.Error;
                return;
            }

            AppConfig.SetInstalledDllVariant(family.Id, chosen.Version);
            AppConfig.SetInstalledDllVariantSize(family.Id, DllInstaller.GetInstalledSize(DllInstaller.Scan(_plugins.AddonDirectory), family));

            // 就地刷红横幅：拿盘上最新状态按文件名对回卡片
            foreach (GameAddonState state in _plugins.GetAddons())
            {
                Addons.FirstOrDefault(a => string.Equals(a.FileName, state.FileName, StringComparison.OrdinalIgnoreCase))
                    ?.RefreshDllStatus(state.DllStatus);
            }

            TextBlock_Status.Text = $"已装好 {family.DisplayName} {chosen.Version}（重启游戏生效）。";
            _logger.LogInformation("DLL fixed from card: {Family} {Version}", family.Id, chosen.Version);
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "修复失败：" + ex.Message;
            _logger.LogWarning(ex, "Fix DLL from card failed");
        }
    }

    /// <summary>卡片上的「永远 off」：启动/注入前把 [RENODX-DLSS] 挂钩点写成 0（按游戏记在 AppConfig）</summary>
    private void OnAddonForceHookOffChanged(AddonItemViewModel item, bool value)
    {
        if (CurrentGameId is not { } gameId)
        {
            return;
        }

        AppConfig.SetForceHookOffOnLaunch(gameId.GameBiz, value);
        TextBlock_Status.Text = value
            ? "已开启：以后从这里启动/注入这个游戏之前，会自动把旧 RenoDX DLSS 的挂钩点写成 0。"
            : "已关闭：启动前不再动挂钩点。";
    }

    /// <summary>
    /// 卡片里的 DLSS5 Feed 延迟（需求1）：写 addon 目录里那份 <c>dlss5-feed.cfg</c>。
    /// 两个键一起写、一起回读，输入框被清空（NaN）就拨回去。
    /// </summary>
    private void OnAddonFeedConfigChanged(AddonItemViewModel item)
    {
        if (_plugins is null)
        {
            return;
        }

        Dlss5FeedConfig? config = Dlss5FeedConfig.Load(_plugins.AddonDirectory);
        if (config is null)
        {
            TextBlock_Status.Text = "找不到 DLSS5 Feed 的插件目录，写不了 dlss5-feed.cfg。";
            return;
        }

        if (double.IsNaN(item.FeedCreateDelay) || double.IsNaN(item.FeedWarmupRebuild))
        {
            item.RefreshFeedConfig(config.CreateDelay, config.WarmupRebuild);
            TextBlock_Status.Text = "DLSS5 Feed 的帧数不能留空（0 = 不等，立刻创建）。";
            return;
        }

        bool ok = config.Save((int)Math.Round(item.FeedCreateDelay), (int)Math.Round(item.FeedWarmupRebuild));
        item.RefreshFeedConfig(config.CreateDelay, config.WarmupRebuild);
        TextBlock_Status.Text = ok
            ? $"DLSS5 Feed：create_delay={config.CreateDelay}、warmup_rebuild={config.WarmupRebuild}（重启游戏才生效）。"
            : "写 dlss5-feed.cfg 失败 —— 文件可能被占用或只读。";
    }

    private void UpdateHookPointUi()
    {
        int dlssHookPoint = Math.Clamp(_plugins?.GetHookPoint() ?? 0, 0, 4);
        int dlss5HookPoint = Math.Clamp(_plugins?.GetDlss5HookPoint() ?? 0, 0, 4);

        _isApplying = true;
        try
        {
            // 卡片里那两份 HookPoint（DLSS / DLSS5）跟着盘上对齐 ——
            // 游戏里插件自己会改这两个键，焦点回到启动器时得跟上
            foreach (AddonItemViewModel card in Addons)
            {
                card.RefreshHookPoint(card.IsRenoDxDlss5Main ? dlss5HookPoint : dlssHookPoint);
            }

            // 卡片上的 DX11Source / EnableHooks 也跟 ini 对齐
            int dx11Index = Dx11SourceToIndex(_plugins is { HasReShadeIni: true } plugins
                ? plugins.GetDx11Source()
                : null);
            bool enableHooks = _plugins is { HasReShadeIni: true }
                ? _plugins.IsEnableHooksOn()
                : false;
            foreach (AddonItemViewModel card in Addons)
            {
                card.RefreshDx11Source(dx11Index);
                card.RefreshEnableHooks(enableHooks);
            }

            // RenoDX DLSS5 已启用但键缺失（老安装 / 键被清过）：自动补上，
            // 免得进游戏插件一直弹「set DX11Source=native」的提示（用户反馈）
            if (_plugins is { HasReShadeIni: true } pluginsAuto
                && pluginsAuto.IsRenoDxDlss5AddonEnabled()
                && !pluginsAuto.IsDx11SourceNative()
                && pluginsAuto.SetDx11SourceNative(true))
            {
                TextBlock_Status.Text = "RenoDX DLSS5 已启用：自动补写 [RenoDX.DLSS5] DX11Source=native（呈现模式需要，进游戏不再提示）。";
            }
        }
        finally
        {
            _isApplying = false;
        }
    }



    /// <summary>「注入时机」输入框：0~30 秒，留空 = 跟随全局默认（按游戏）</summary>
    private void UpdateShadeInjectDelayUi()
    {
        _isApplying = true;
        try
        {
            int? current = AppConfig.GetShadeInjectDelaySeconds(CurrentGameBiz);
            NumberBox_ShadeInjectDelay.Value = current is int value ? value : double.NaN;
            NumberBox_ShadeInjectDelay.PlaceholderText =
                $"跟随全局（{AppConfig.GetDefaultInjectionDelaySeconds(CurrentGameBiz)} 秒）";
        }
        finally
        {
            _isApplying = false;
        }
    }

    private void NumberBox_ShadeInjectDelay_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_isApplying)
        {
            return;
        }

        int? seconds = double.IsNaN(args.NewValue)
            ? null
            : (int)Math.Clamp(Math.Round(args.NewValue), 0, AppConfig.MaxInjectionWarmupSeconds);

        AppConfig.SetShadeInjectDelaySeconds(CurrentGameBiz, seconds);

        TextBlock_Status.Text = seconds is null
            ? "HoYoShade / ReShade 注入时机：跟随全局默认。"
            : seconds <= 0
                ? "HoYoShade / ReShade 注入：立即注入（不等）。"
                : $"HoYoShade / ReShade 注入：等游戏起来 {seconds} 秒后再注入。";
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
        // 初始 context 和「重新检测」用的工厂是同一个 —— 每次检测都重新读盘
        var dialog = new Dlss5CompatDialog(BuildDlss5CompatContext(), BuildDlss5CompatContext)
        {
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }

    /// <summary>
    /// 组装 DLSS5 检测上下文。「重新检测」每次都会重新走一遍（重建 GamePluginService 重读 ini / 插件目录）：
    /// 修复（比如把 ini 路径指回当前 HoYoShade）改的是**盘上的文件**，页面缓存的 service 还是修复前那份 ——
    /// 不重建的话，修完再检还是旧内容，看起来就是「指回了也没用」（群友反馈）。
    /// </summary>
    private Dlss5CompatContext BuildDlss5CompatContext()
    {
        // 没装 HoYoShade 也要能检测：DLSS5 还有「OptiScaler 的 DLSS-NR」那条路，那条不需要 HoYoShade。
        ShadeHost? host = _host ?? PluginHostLocator.Resolve(out _);

        GamePluginService? plugins = _plugins;
        if (_entry is not null)
        {
            try
            {
                plugins = new GamePluginService(
                    _entry,
                    host,
                    PluginHostLocator.AddonNameCachePath,
                    GameCatalog.AddonCandidateNames(),
                    GameCatalog.TagsOfAddonFile);
            }
            catch
            {
                plugins = _plugins;
            }
        }

        return new Dlss5CompatContext
        {
            ShadeHost = host,
            Game = _entry,
            GameId = CurrentGameId,
            Profile = plugins?.Profile,
            ProfileError = plugins?.ProfileError,
            AddonStates = plugins?.GetAddons(),
            HookPoint = plugins?.GetHookPoint() ?? 0,
            PluginService = plugins,
            Delivery = Dlss5CompatContext.ResolveDelivery(CurrentGameId, host),
            OptiScalerDllPath = AppConfig.GetSelectedOptiScalerDll(CurrentGameId),
        };
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

        // 指向每游戏插件包 = 正常状态，别把它改回共享目录（会让这个游戏用错版本）
        try
        {
            if (GameAddonPack.IsPackDirectory(ReShadeProfile.Load(gameIni).AddonPath))
            {
                ShowInfo("不用改",
                    "这个游戏用的是专属插件目录（每游戏插件包）。想改用共享目录里的当前版本，就在插件卡片的「版本」下拉里选回「默认（用全局当前版本）」。",
                    InfoBarSeverity.Informational);
                return;
            }
        }
        catch
        {
            // 读不了 ini 就继续走原来的对齐流程
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
        Version = state.Version;
        CanToggle = state.CanToggle;
        IsDlss5 = state.IsDlss5;
        SelfRegistersInIni = state.SelfRegistersInIni;
        Slug = state.Slug;
        IsFeed = GamePluginService.IsDlss5FeedAddon(state.FileName);
        IsRenoDxDlss5Main = GamePluginService.IsRenoDxDlss5Addon(state.FileName);
        IsRenoDxDlssFamily = state.Slug?.StartsWith("renodx-dlss", StringComparison.OrdinalIgnoreCase) == true;

        // 缺 dll 的标记（红 = 缺必需，黄 = 缺建议）
        DllSeverity = state.DllStatus.Severity;
        DllStatusText = state.DllStatus.Summary;
        MissingDllFiles = state.DllStatus.MissingRequired.Concat(state.DllStatus.MissingRecommended)
            .SelectMany(r => r.Files)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    /// <summary>插件版本（显示在名字右侧）。</summary>
    public string? Version { get; }

    public string VersionText => Version;

    public Visibility VersionVisibility =>
        !string.IsNullOrWhiteSpace(Version) ? Visibility.Visible : Visibility.Collapsed;

    public string MetaText { get; }

    public bool CanToggle { get; }

    /// <summary>DLSS5 那一类插件（驱动版本检查只对它们做）</summary>
    public bool IsDlss5 { get; }

    /// <summary>插件自己会往 ini 里登记加载方式（那种就不显示「从 DllMain 加载」）</summary>
    public bool SelfRegistersInIni { get; }

    [ObservableProperty]
    private bool enabled;

    [ObservableProperty]
    private bool loadFromDllMain;

    public Visibility GloballyDisabledVisibility => CanToggle ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>扩展目录里的 slug（认不出来就是 null）</summary>
    public string? Slug { get; }

    /// <summary>DLSS5 Feed（<c>dlss5-feed.addon64</c>）：专属配置是 cfg 里那两个延迟</summary>
    public bool IsFeed { get; }

    /// <summary>RenoDX DLSS5 主插件（<c>renodx-dlss5.addon64*</c>）：HookPoint 写 <c>[RenoDX.DLSS5] NRHookPoint</c></summary>
    public bool IsRenoDxDlss5Main { get; }

    /// <summary>renodx-dlss* 这一族（DLSS / DLSS5 / Super Anus）：卡片里有 HookPoint + LoadFromDllMain</summary>
    public bool IsRenoDxDlssFamily { get; }

    /// <summary>
    /// 「从 DllMain 加载」显示条件：DLSS5 那一类 + 插件自己不会往 ini 里登记加载方式。
    ///
    /// <para>
    /// 例外（用户要求第 6 条）：<c>renodx-dlss*</c> 这一族即使二进制里带着
    /// <c>LoadFromDllMain</c> 字样（<c>AddonSelfRegistrationDetector</c> 会判成「自己会登记」）
    /// 也要显示 —— 用户实测 RenoDX DLSS 卡片里这条整条不见了，得补回来。
    /// </para>
    /// </summary>
    public Visibility LoadFromDllMainVisibility =>
        (IsDlss5 || IsRenoDxDlssFamily) && (!SelfRegistersInIni || IsRenoDxDlssFamily)
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>能改的时候才可点（关掉插件 / 不相干的插件 / 自己会登记的都是灰的）</summary>
    public bool CanEditLoadFromDllMain =>
        CanToggle && Enabled
        && (IsDlss5 || IsRenoDxDlssFamily)
        && (!SelfRegistersInIni || IsRenoDxDlssFamily);

    // ===================== 需求1~4：展开后的专属配置 =====================

    /// <summary>卡片是展开还是缩回（默认缩回，也就是现在这个样子）</summary>
    [ObservableProperty]
    private bool isExpanded;

    /// <summary>专属配置区显示与否</summary>
    public Visibility ExpandedVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 扩展按钮（收起状态时显示，箭头朝下）。和 <see cref="CollapseButtonVisibility"/> 合起来才是一个
    /// 「展开 / 缩回」按钮 —— 因为按钮内容里挂 x:Bind 会让 XamlCompiler 崩，只能拆成两个静态字形的按钮。
    /// </summary>
    public Visibility ExpandButtonVisibility =>
        ConfigToggleVisibility == Visibility.Visible && !IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>缩回按钮（展开状态时显示，箭头朝上）</summary>
    public Visibility CollapseButtonVisibility =>
        ConfigToggleVisibility == Visibility.Visible && IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>有专属配置才显示扩展按钮（免得点开是空的）</summary>
    public Visibility ConfigToggleVisibility =>
        (IsFeed && HasFeedConfig) || IsRenoDxDlssFamily || LoadFromDllMainVisibility == Visibility.Visible
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>addon 目录里那份 dlss5-feed.cfg 读到了没有</summary>
    public bool HasFeedConfig { get; private set; }

    public Visibility FeedConfigVisibility =>
        IsFeed && HasFeedConfig ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>renodx-dlss* 这一族都显示 HookPoint（DLSS 和 DLSS5 各写各的键）</summary>
    public Visibility HookPointConfigVisibility =>
        IsRenoDxDlssFamily ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>这个数字写到哪个键上（DLSS 和 DLSS5 不是同一个键，也不是同一段）</summary>
    public string HookPointKeyHint => IsRenoDxDlss5Main
        ? "[RenoDX.DLSS5] NRHookPoint"
        : "[RENODX-DLSS] DirectNeuralRenderingHookPoint";

    /// <summary>
    /// DX11Source（呈现模式）只跟 DLSS5 主插件有关：键写在 [RenoDX.DLSS5] 段，
    /// 旧 RenoDX DLSS 插件没这个东西。
    /// </summary>
    public Visibility Dx11SourceVisibility =>
        IsRenoDxDlss5Main ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>DX11Source 可点条件：插件启用 + 可改（门槛和 HookPoint 一致）</summary>
    public bool CanEditDx11Source => CanToggle && Enabled && IsRenoDxDlss5Main;

    /// <summary>
    /// EnableHooks（[RenoDX.DLSS5] EnableHooks=1）：游戏完全走 NVIDIA Streamline 时，
    /// 默认挂钩模式看不见 NR，addon 会弹「NR 需要 Streamline 设置」要求开这个 —— 也只有 DLSS5 主插件用得上。
    /// </summary>
    public Visibility EnableHooksVisibility =>
        IsRenoDxDlss5Main ? Visibility.Visible : Visibility.Collapsed;

    public bool CanEditEnableHooks => CanToggle && Enabled && IsRenoDxDlss5Main;

    /// <summary>
    /// 「永远 off」（启动/注入前把挂钩点写成 0）：只属于旧 RenoDX DLSS —— 键是
    /// [RENODX-DLSS] DirectNeuralRenderingHookPoint；DLSS5 走 Streamline，不吃这个键，所以不显示。
    /// </summary>
    public Visibility ForceHookOffVisibility =>
        IsRenoDxDlssFamily && !IsRenoDxDlss5Main ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 「永远 off」是启动行为（每次启动/注入前写 ini），不依赖插件当前的全局 / 游戏级启用状态 ——
    /// 只要这张卡片认得出是旧 RenoDX DLSS 一族就能勾。
    /// </summary>
    public bool CanEditForceHookOff => IsRenoDxDlssFamily && !IsRenoDxDlss5Main;

    /// <summary>cfg 的完整路径（展开时显示出来，方便自己去改）</summary>
    public string FeedConfigPath { get; private set; } = string.Empty;

    [ObservableProperty]
    private int hookPoint;

    /// <summary>DX11Source 三态：0 = 默认（不写键）/ 1 = native（呈现模式）/ 2 = foreign</summary>
    [ObservableProperty]
    private int dx11Source;

    [ObservableProperty]
    private bool enableHooks;

    /// <summary>「永远 off」：启动/注入这个游戏之前把旧 DLSS 的挂钩点写成 0（按游戏记在 AppConfig）</summary>
    [ObservableProperty]
    private bool forceHookOffOnLaunch;

    [ObservableProperty]
    private double feedCreateDelay;

    [ObservableProperty]
    private double feedWarmupRebuild;

    /// <summary>用户改了 HookPoint（页面写盘；写失败页面会调 <see cref="RefreshHookPoint"/> 拨回来）</summary>
    internal Action<AddonItemViewModel, int>? HookPointChanged { get; set; }

    /// <summary>用户改了 DX11Source（页面写盘；写失败页面会调 <see cref="RefreshDx11Source"/> 拨回来）</summary>
    internal Action<AddonItemViewModel, int>? Dx11SourceChanged { get; set; }

    /// <summary>用户改了 EnableHooks（页面写盘；写失败页面会调 <see cref="RefreshEnableHooks"/> 拨回来）</summary>
    internal Action<AddonItemViewModel, bool>? EnableHooksChanged { get; set; }

    /// <summary>用户改了「永远 off」（页面记到 AppConfig；失败页面会调 <see cref="RefreshForceHookOff"/> 拨回来）</summary>
    internal Action<AddonItemViewModel, bool>? ForceHookOffChanged { get; set; }

    /// <summary>用户改了 Feed 的延迟（页面读这两个属性一起写，失败一起拨回来）</summary>
    internal Action<AddonItemViewModel>? FeedConfigChanged { get; set; }

    /// <summary>程序填专属配置的初值（不算用户改的）</summary>
    public void ConfigureDedicatedConfig(int hookPoint, double feedCreateDelay, double feedWarmupRebuild, string? feedConfigPath, int dx11Source, bool enableHooks, bool forceHookOffOnLaunch)
    {
        _suppress = true;
        try
        {
            HookPoint = Math.Clamp(hookPoint, 0, 4);
            FeedCreateDelay = feedCreateDelay;
            FeedWarmupRebuild = feedWarmupRebuild;
            FeedConfigPath = feedConfigPath ?? string.Empty;
            HasFeedConfig = !string.IsNullOrWhiteSpace(feedConfigPath);
            Dx11Source = Math.Clamp(dx11Source, 0, 2);
            EnableHooks = enableHooks;
            ForceHookOffOnLaunch = forceHookOffOnLaunch;
        }
        finally
        {
            _suppress = false;
        }

        OnPropertyChanged(nameof(FeedConfigVisibility));
        OnPropertyChanged(nameof(ConfigToggleVisibility));
        OnPropertyChanged(nameof(ExpandButtonVisibility));
        OnPropertyChanged(nameof(CollapseButtonVisibility));
    }

    /// <summary>跟盘上对齐 HookPoint（游戏里插件自己也会改这个键）</summary>
    public void RefreshHookPoint(int value)
    {
        _suppress = true;
        try
        {
            HookPoint = Math.Clamp(value, 0, 4);
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>跟盘上对齐 DX11Source（下拉跟着 ini 走；传下标）</summary>
    public void RefreshDx11Source(int value)
    {
        _suppress = true;
        try
        {
            Dx11Source = Math.Clamp(value, 0, 2);
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>跟盘上对齐 EnableHooks</summary>
    public void RefreshEnableHooks(bool value)
    {
        _suppress = true;
        try
        {
            EnableHooks = value;
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>跟 AppConfig 对齐「永远 off」勾选（切游戏重建卡片时填初值）</summary>
    public void RefreshForceHookOff(bool value)
    {
        _suppress = true;
        try
        {
            ForceHookOffOnLaunch = value;
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>跟盘上对齐 Feed 的两个延迟</summary>
    public void RefreshFeedConfig(double createDelay, double warmupRebuild)
    {
        _suppress = true;
        try
        {
            FeedCreateDelay = createDelay;
            FeedWarmupRebuild = warmupRebuild;
        }
        finally
        {
            _suppress = false;
        }
    }

    /// <summary>
    /// 从 ini 重读 <c>LoadFromDllMain</c>：需求5 —— 开关一动，服务里那条规则会把条目
    /// 自动补上 / 摘掉，卡片上这个勾得跟着盘上的真实状态走。
    /// </summary>
    public void RefreshLoadFromDllMain(bool value)
    {
        _suppress = true;
        try
        {
            LoadFromDllMain = value;
        }
        finally
        {
            _suppress = false;
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(ExpandedVisibility));
        OnPropertyChanged(nameof(ExpandButtonVisibility));
        OnPropertyChanged(nameof(CollapseButtonVisibility));
    }

    partial void OnHookPointChanged(int value)
    {
        if (_suppress)
        {
            return;
        }

        HookPointChanged?.Invoke(this, value);
    }

    partial void OnDx11SourceChanged(int value)
    {
        if (_suppress)
        {
            return;
        }

        Dx11SourceChanged?.Invoke(this, value);
    }

    partial void OnEnableHooksChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        EnableHooksChanged?.Invoke(this, value);
    }

    partial void OnForceHookOffOnLaunchChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        ForceHookOffChanged?.Invoke(this, value);
    }

    partial void OnFeedCreateDelayChanged(double value) => NotifyFeedConfigChanged();

    partial void OnFeedWarmupRebuildChanged(double value) => NotifyFeedConfigChanged();

    private void NotifyFeedConfigChanged()
    {
        if (_suppress)
        {
            return;
        }

        FeedConfigChanged?.Invoke(this);
    }

    /// <summary>0 = 没问题，1 = 缺建议（黄），2 = 缺必需（红）</summary>
    public int DllSeverity { get; private set; }

    public string DllStatusText { get; private set; } = string.Empty;

    public bool HasMissingDll => DllSeverity > 0;

    /// <summary>缺的那些 dll 文件名（红黄合起来），修复弹窗用它判断该补哪个家族</summary>
    public IReadOnlyList<string> MissingDllFiles { get; private set; } = [];

    /// <summary>缺件时显示「修复」按钮</summary>
    public Visibility HasMissingDllVisibility =>
        HasMissingDll ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>修复安装之后用盘上最新状态刷这一行（红横幅跟着变绿/消失）</summary>
    internal void RefreshDllStatus(AddonDllStatus status)
    {
        DllSeverity = status.Severity;
        DllStatusText = status.Summary;
        MissingDllFiles = status.MissingRequired.Concat(status.MissingRecommended)
            .SelectMany(r => r.Files)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        OnPropertyChanged(nameof(DllSeverity));
        OnPropertyChanged(nameof(DllStatusText));
        OnPropertyChanged(nameof(DllRequiredVisibility));
        OnPropertyChanged(nameof(DllRecommendedVisibility));
        OnPropertyChanged(nameof(HasMissingDll));
        OnPropertyChanged(nameof(HasMissingDllVisibility));
    }

    public Visibility DllRequiredVisibility => DllSeverity >= 2 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DllRecommendedVisibility => DllSeverity == 1 ? Visibility.Visible : Visibility.Collapsed;

    // ===================== 需求1：这张卡片的「版本」下拉 =====================

    /// <summary>默认项 = 用共享目录里当前生效的那一份</summary>
    public const string DefaultVersionLabel = "默认（用全局当前版本）";

    /// <summary>这个 addon 文件属于哪个扩展（认不出来就是 null）</summary>
    public string? ExtensionId { get; private set; }

    private readonly Dictionary<string, string?> _versionTags = new(StringComparer.Ordinal);

    /// <summary>程序填初值的那一次不算用户选的</summary>
    private bool _suppressVersion;

    /// <summary>下拉选项：第一项是「默认（用全局当前版本）」，后面是归档里的 tag</summary>
    public ObservableCollection<string> VersionOptions { get; } = [];

    /// <summary>归档里 &gt;= 2 个版本才显示下拉（否则整条不出现）</summary>
    public Visibility VersionChoiceVisibility =>
        VersionOptions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>悬停说明：这个游戏用哪一版</summary>
    public string VersionChoiceHint { get; private set; } = string.Empty;

    [ObservableProperty]
    private string? selectedVersionOption;

    /// <summary>用户选中的 tag（「默认」= null）</summary>
    public string? SelectedVersionTag
        => _versionTags.TryGetValue(SelectedVersionOption ?? string.Empty, out string? tag) ? tag : null;

    /// <summary>下拉变化时的回调（页面拿去落盘 + 重拼这个游戏的插件包）</summary>
    internal Action<AddonItemViewModel, string?>? VersionChanged { get; set; }

    /// <summary>
    /// 页面给的分发队列：用来**延迟**把选中项写进 ComboBox。
    /// 立刻写会被随后铺进来的 ItemsSource 顶成 null，界面看起来就是「下拉是空的」（用户反馈）。
    /// </summary>
    internal Microsoft.UI.Dispatching.DispatcherQueue? VersionDispatchQueue { get; set; }

    /// <summary>
    /// 填「版本」下拉。只有一个版本 / 认不出扩展时整条不显示（卡片保持原来那一行）。
    /// </summary>
    public void ConfigureVersionChoice(string? extensionId, IReadOnlyList<string> archivedTags, string? selectedTag)
    {
        ExtensionId = extensionId;
        _suppressVersion = true;
        try
        {
            VersionOptions.Clear();
            _versionTags.Clear();

            if (string.IsNullOrWhiteSpace(extensionId) || archivedTags.Count < 2)
            {
                SelectedVersionOption = null;
                VersionChoiceHint = string.Empty;
            }
            else
            {
                VersionOptions.Add(DefaultVersionLabel);
                _versionTags[DefaultVersionLabel] = null;
                foreach (string tag in archivedTags)
                {
                    _versionTags[tag] = tag;
                    VersionOptions.Add(tag);
                }

                VersionChoiceHint = $"这个游戏用哪一版（归档里 {archivedTags.Count} 个版本）；选完会为它单独拼一份插件目录，其它游戏不受影响。";
            }
        }
        finally
        {
            _suppressVersion = false;
        }

        string? desired = VersionOptions.Count < 2
            ? null
            : selectedTag is { Length: > 0 } && _versionTags.ContainsKey(selectedTag)
                ? selectedTag
                : DefaultVersionLabel;

        SetSelectedVersionOptionSilently(desired);

        // ComboBox 把新的 ItemsSource 铺好之后会把刚设的 SelectedItem 顶掉/清空，界面看起来就是
        // 「刷新后下拉是空的」。所以再用一个分发轮次补设一次（Low：等这一轮的布局 / 绑定回写跑完）。
        if (desired is not null && VersionDispatchQueue is { } queue)
        {
            queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () => SetSelectedVersionOptionSilently(desired));
        }

        OnPropertyChanged(nameof(VersionChoiceVisibility));
    }

    /// <summary>替下拉设选中项，但不触发用户改动回调</summary>
    private void SetSelectedVersionOptionSilently(string? value)
    {
        _suppressVersion = true;
        try
        {
            SelectedVersionOption = value;
        }
        finally
        {
            _suppressVersion = false;
        }
    }

    partial void OnSelectedVersionOptionChanged(string? value)
    {
        // null 一律忽略。
        //
        // 下拉是 TwoWay 绑定（SelectedItem），而刷新列表时会重建卡片：ComboBox 的 ItemsSource
        // 被换掉的那一刻 SelectedItem 会瞬时变成 null，这个 null 顺着绑定写回来 ——
        // 它不是用户的选择（用户选「默认」拿到的是 DefaultVersionLabel 这个字符串），
        // 但会调 VersionChanged(null) → 把用户刚选的版本清成「默认」→ 专属插件包被收掉。
        // 实测日志就是这样：选了版本 275ms 后目录又翻回共享目录（用户报的切版本崩溃）。
        if (_suppressVersion || value is null)
        {
            return;
        }

        VersionChanged?.Invoke(this, SelectedVersionTag);
    }

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

