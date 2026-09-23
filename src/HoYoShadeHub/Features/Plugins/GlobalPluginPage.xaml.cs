using CommunityToolkit.Mvvm.ComponentModel;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.I18n;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.Modules;
using HoYoShadeHub.Frameworks;
using HoYoShadeHub.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// **全局**插件页（左下角，设置上方）。
///
/// 管的是全局那一份 HoYoShade：&lt;用户数据目录&gt;\HoYoShade。三个标签页，都是同一套
/// 「上面 当前（已装的东西，可点开换版本 / 开关），下面 可下载（目录里还没装的，紧凑一行）」：
/// <list type="bullet">
/// <item>「插件」：当前 = 已装的扩展包（全局开关 + 展开换版本 + 打开目录 / 删除），
/// 外加一小截「没匹配到目录条目的插件文件」；可下载 = 目录里还没装的扩展包。</item>
/// <item>「OptiScaler」：当前 = 已经下载到本地的构建（展开换版本）；可下载 = 还没装过的来源。
/// 总开关（<see cref="AppConfig.OptiScalerEnabled"/>）也挪到这里。</item>
/// <item>「模块」：当前 = 装好了的模块（内置 + 手动加的 DLL）；可下载 = 远端 / 内置模块目录里还没装的。</item>
/// </list>
///
/// 三个标签页的条目都只来自云端目录（plugins.json / optiscaler.json / modules.json）+ 内置表，
/// 以后加新东西不用改这个页面。按游戏的开关在左侧「插件」/「模块」/「OptiScaler」页里。
/// </summary>
public sealed partial class GlobalPluginPage : PageBase
{
    private readonly ILogger<GlobalPluginPage> _logger = AppConfig.GetLogger<GlobalPluginPage>();

    private ExtensionManagerService? _manager;

    private bool _isWorking;

    /// <summary>
    /// 当前正在跑的下载任务。同一时间只允许一个，所以放页面级字段而不是每个按钮各管各的。
    /// 界面上的「暂停」「取消」两个按钮都打到它身上。
    /// </summary>
    private DownloadJob? _downloadJob;

    /// <summary>这次进页面已经查过更新了没有（别每次刷新都打一遍网络）</summary>
    private bool _updatesChecked;

    private string _search = string.Empty;

    /// <summary>当前标签页：plugins / optiscaler / modules</summary>
    private string _tab = "plugins";


    /// <summary>OptiScaler 来源的重叠层（打开页面时读一次，RefreshOptiScaler 复用）</summary>
    private List<OptiScalerSource>? _optiScalerOverlay;

    public GlobalPluginPage()
    {
        InitializeComponent();
    }

    /// <summary>全部扩展条目（过滤前的）</summary>
    public ObservableCollection<PluginItemViewModel> Items { get; } = [];

    /// <summary>「当前」：已经装了的扩展包</summary>
    public ObservableCollection<PluginItemViewModel> InstalledItems { get; } = [];

    /// <summary>「可下载」：目录里还没装的扩展包</summary>
    public ObservableCollection<PluginItemViewModel> VisibleItems { get; } = [];

    /// <summary>「当前」里那截「没匹配到目录条目的插件文件」</summary>
    public ObservableCollection<AddonFileItemViewModel> OrphanAddonFiles { get; } = [];

    /// <summary>OptiScaler「当前」：已经下载到本地的构建</summary>
    public ObservableCollection<OptiScalerBuildItemViewModel> OptiScalerBuilds { get; } = [];

    /// <summary>OptiScaler「可下载」：还没装过的来源</summary>
    public ObservableCollection<OptiScalerSourceItemViewModel> OptiScalerSources { get; } = [];

    /// <summary>模块「当前」：装好了的模块</summary>
    public ObservableCollection<ModuleItemViewModel> CurrentModules { get; } = [];

    /// <summary>模块「可下载」：还没装的模块</summary>
    public ObservableCollection<ModuleDownloadItemViewModel> AvailableModules { get; } = [];

    // 下面几个是过滤 / 搜索前的全量，ApplyFilter 从它们算可见列表
    private List<AddonFileItemViewModel> _allAddonFiles = [];
    private List<OptiScalerBuildItemViewModel> _allOptiScalerBuilds = [];
    private List<OptiScalerSourceItemViewModel> _allOptiScalerSources = [];
    private List<ModuleItemViewModel> _allModules = [];
    private List<ModuleDownloadItemViewModel> _allModuleDownloads = [];

    protected override void OnLoaded()
    {
        InstalledPluginList.ItemsSource = InstalledItems;
        OrphanAddonFileList.ItemsSource = OrphanAddonFiles;
        PluginList.ItemsSource = VisibleItems;
        OptiScalerBuildList.ItemsSource = OptiScalerBuilds;
        OptiScalerSourceList.ItemsSource = OptiScalerSources;
        ModuleList.ItemsSource = CurrentModules;
        ModuleDownloadList.ItemsSource = AvailableModules;


        PluginDownloadProxy.Apply();

        _ = RefreshCatalogThenPageAsync();
    }

    /// <summary>进页面先看远端目录该不该更新（每天最多一次），再刷新列表。</summary>
    private async Task RefreshCatalogThenPageAsync()
    {
        try
        {
            RemoteCatalogResult result = await RemoteCatalogService.RefreshIfDueAsync();
            if (result.Fetched)
            {
                _logger.LogInformation("Remote catalog: {Message}", result.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refresh remote catalog");
        }

        // 云端目录：模块（catalog/modules.json）和 OptiScaler（catalog/optiscaler.json）。
        // 插件那边有它自己的 ExtraCatalogFiles 机制，这里不动。
        ModuleCatalogFile.Apply(RemoteCatalogService.ModulesCachePath);
        _optiScalerOverlay = OptiScalerCatalog.LoadFile(RemoteCatalogService.OptiScalerCachePath);

        await RefreshAsync();
    }

    protected override void OnUnloaded()
    {
        Items.Clear();
        InstalledItems.Clear();
        VisibleItems.Clear();
        OrphanAddonFiles.Clear();
        OptiScalerBuilds.Clear();
        OptiScalerSources.Clear();
        CurrentModules.Clear();
        AvailableModules.Clear();
    }

    #region 刷新

    /// <summary>
    /// 挨个查已装插件在 GitHub 上有没有新版本。
    /// 只对装了账本的条目查，串行跑、失败就跳过；查完在卡片上挂「有新版本 xxx」的徽标。
    /// </summary>
    private async Task CheckUpdatesAsync()
    {
        if (_manager is null || _updatesChecked)
        {
            return;
        }

        _updatesChecked = true;

        foreach (PluginItemViewModel item in Items.Where(i => i.Installed is not null).ToList())
        {
            try
            {
                string? tag = await _manager.CheckUpdateAsync(item.Manifest);
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    item.UpdateTag = tag;
                    _logger.LogInformation("Update available for {Id}: {Tag}", item.Manifest.Id, tag);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Check update for {Id}", item.Manifest.Id);
            }
        }
    }

    private async Task RefreshAsync()
    {
        if (_isWorking)
        {
            return;
        }

        _isWorking = true;

        try
        {
            await ReloadPluginDataAsync();
        }
        finally
        {
            _isWorking = false;
        }
    }

    /// <summary>重读插件数据（不带 _isWorking 锁，供已在 RunAsync 内的流程装完调用）。</summary>
    private async Task ReloadPluginDataAsync()
    {
        TextBlock_Status.Text = "正在读取插件目录…";

        try
        {
            _manager = PluginHostLocator.ResolveManager(out string reason);

            if (_manager is null)
            {
                Items.Clear();
                _allAddonFiles = [];
                _allOptiScalerBuilds = [];
                _allOptiScalerSources = [];
                _allModules = [];
                _allModuleDownloads = [];
                TextBlock_TargetPath.Text = "未找到 HoYoShade 目录";
                ShowInfo("没有可管理的 HoYoShade 目录", reason, InfoBarSeverity.Warning);
                TextBlock_Status.Text = string.Empty;
                ApplyFilter();
                return;
            }

            TextBlock_TargetPath.Text = _manager.Host.RootPath;

            var statuses = await _manager.GetStatusAsync();
            List<ExtensionStatus> plugins = [.. statuses.Where(s => IsPluginExtension(s.Manifest))];

            // 盘上的 addon 文件：账本只记 Hub 自己装的，用户自己放进去的要靠 addonPatterns 认领
            string addonsPath = _manager.Host.AddonsPath;
            List<AddonFileInfo> diskFiles = AddonFileInfo.ScanDirectory(addonsPath);
            Dictionary<string, List<AddonFileInfo>> claimed =
                ExtensionAddonMatcher.Match(plugins.Select(s => s.Manifest), diskFiles);

            Items.Clear();
            foreach (ExtensionStatus status in plugins)
            {
                List<string> addonFiles = ResolveAddonFiles(status);
                int ledgerCount = addonFiles.Count;

                if (claimed.TryGetValue(status.Manifest.Id, out List<AddonFileInfo>? owned))
                {
                    foreach (AddonFileInfo info in owned)
                    {
                        string full = Path.Combine(addonsPath, info.FileName);
                        if (!addonFiles.Contains(full, StringComparer.OrdinalIgnoreCase))
                        {
                            addonFiles.Add(full);
                        }
                    }
                }

                Items.Add(new PluginItemViewModel(
                    status,
                    addonFiles,
                    ResolveInstalledVersion(addonFiles),
                    foundOnDiskOnly: !status.IsInstalled && addonFiles.Count > ledgerCount,
                    AddonDllChecker.Check(addonsPath, status.Manifest.Tags),
                    OnGlobalToggleRequested));
            }

            RefreshAddonFiles();
            RefreshOptiScaler();
            RefreshModules();

            ShadeHostDiagnostics diagnostics = _manager.Diagnose();
            if (!diagnostics.ShadeDllExists)
            {
                ShowInfo("这个目录看起来不是 HoYoShade",
                    "找不到 ReShade64.dll，请确认选的是 <用户数据目录>\\HoYoShade。",
                    InfoBarSeverity.Error);
            }
            else if (!diagnostics.AddonPathConfigured)
            {
                ShowInfo("ReShade.ini 里没有 addon 路径",
                    "ReShade 只从 ReShade.ini 的 [ADDON] AddonPath 指定目录加载 *.addon64。安装任意 addon 时会自动补上。",
                    InfoBarSeverity.Warning);
            }
            else
            {
                HideInfo();
            }

            ApplyFilter();

            _logger.LogInformation("Global plugin page: {Extensions} plugin extensions, {Addons} addon files, shade host = {Host}",
                Items.Count, _allAddonFiles.Count, _manager.Host.RootPath);

            // 后台查一遍更新（每进一次页面查一次），有新的就在卡片上挂徽标
            _ = CheckUpdatesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh plugins");
            TextBlock_Status.Text = "刷新失败：" + ex.Message;
        }
    }

    /// <summary>这个扩展装过的 addon 文件（绝对路径，且现在还在盘上）</summary>
    private List<string> ResolveAddonFiles(ExtensionStatus status)
    {
        if (_manager is null || status.Installed is null)
        {
            return [];
        }

        var files = new List<string>();

        foreach (string relative in status.Installed.Files.Select(f => f.Path))
        {
            if (AddonFileInfo.Parse(Path.GetFileName(relative)) is null)
            {
                continue;
            }

            try
            {
                string absolute = _manager.Host.ResolveRelative(relative);

                if (File.Exists(absolute))
                {
                    files.Add(absolute);
                    continue;
                }

                // 全局关掉之后文件被改名成 .addon64x 了，账本里记的还是老名字 ——
                // 得把改名后那份也认出来，否则开关会消失、版本也读不到
                string? directory = Path.GetDirectoryName(absolute);
                if (directory is not null)
                {
                    string disabled = Path.Combine(directory, AddonFileSwitcher.DisabledNameOf(Path.GetFileName(absolute)));
                    if (File.Exists(disabled))
                    {
                        files.Add(disabled);
                    }
                }
            }
            catch
            {
                // 路径越界之类，跳过
            }
        }

        return files;
    }

    /// <summary>
    /// 这个扩展**盘上那些 addon 文件**的版本号（文件名括号里的，没有就读 PE）。
    /// 目录里写的 <c>version</c> 基本都是 "latest"，光看它等于没有版本信息。
    /// </summary>
    private static string? ResolveInstalledVersion(IEnumerable<string> addonFiles)
    {
        foreach (string file in addonFiles)
        {
            string? version = AddonVersionResolver.Resolve(file, AddonFileInfo.Parse(Path.GetFileName(file))?.Version);
            if (!string.IsNullOrWhiteSpace(version))
            {
                return version;
            }
        }

        return null;
    }

    /// <summary>
    /// 全局开关：把这个扩展装的那几个 addon 全部改名（.addon64 ↔ .addon64x）。
    /// </summary>
    private void OnGlobalToggleRequested(PluginItemViewModel item, bool enabled)
    {
        int ok = 0;
        string? error = null;

        foreach (string file in item.GlobalAddonFiles)
        {
            AddonToggleResult result = AddonFileSwitcher.SetEnabled(file, enabled);
            if (result.Ok)
            {
                ok++;
            }
            else
            {
                error ??= result.Error;
            }
        }

        if (ok == 0)
        {
            item.RevertGlobalEnabled();
            TextBlock_Status.Text = "全局开关失败：" + (error ?? "没有可改名的文件");
            return;
        }

        _logger.LogInformation("Global toggle: {Extension} -> {State} ({Count} files)", item.Manifest.Id, enabled, ok);

        // 全局禁用之后，各游戏 ReShade.ini 里那些指向它的 DisabledAddons / LoadFromDllMain 条目
        // 就是死条目了（改名之后 ReShade 根本扫不到）—— 顺手清掉，重新启用时该重新勾
        string purgeNote = string.Empty;
        if (!enabled)
        {
            AddonReferenceCleanResult clean = PurgeAddonReferences(item.GlobalAddonFiles.Select(Path.GetFileName).ToList());
            if (clean.Changed)
            {
                purgeNote = $"　已从 {clean.ChangedGames.Count} 个游戏的 ini 里摘掉它（{string.Join("、", clean.ChangedGames)}）。";
            }
        }

        TextBlock_Status.Text = $"「{item.Name}」已全局{(enabled ? "启用" : "禁用")}（改了 {ok} 个文件的后缀）。" +
                                (error is null ? string.Empty : "（部分失败：" + error + "）") +
                                purgeNote;

        // 「插件文件」那截也要跟着变
        RefreshAddonFiles();
    }

    /// <summary>
    /// 只认「会往插件目录里装 addon」的扩展包。
    /// 滤镜 / 预设那种（比如官方预设合集）先不进这个页面 —— 用户明确要求「只管理插件」。
    /// </summary>
    private static bool IsPluginExtension(ExtensionManifest manifest) =>
        manifest.Rules.Any(rule =>
            (rule.To ?? string.Empty).Replace('\\', '/').Contains("Addons", StringComparison.OrdinalIgnoreCase));

    /// <summary>重建「当前」的插件文件列表（哪些文件已经归到扩展卡片下面，由 ApplyFilter 再算）</summary>
    private void RefreshAddonFiles()
    {
        _allAddonFiles = [];

        string? directory = _manager?.Host.AddonsPath;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            ApplyFilter();
            return;
        }

        var cache = AddonNameCache.Load(PluginHostLocator.AddonNameCachePath ?? string.Empty);
        List<string> candidates = [.. GameCatalog.AddonCandidateNames()];

        List<AddonFileInfo> infos = AddonFileInfo.ScanDirectory(directory);

        // 「缺 dll」必须看目录里的**所有文件**：nvngx_dlssnr.dll / sl.*.dll 不是 addon，
        // 只看 addon 文件名会永远报「缺 dll」（用户反馈：实际不缺却显示缺）
        List<string> allNames = [.. Directory.EnumerateFiles(directory).Select(Path.GetFileName)!];

        foreach (AddonFileInfo info in infos)
        {
            // 这个文件属于哪个插件族 → 要不要 DLSS5 那套 dll
            AddonDllStatus dllStatus = AddonDllChecker.CheckFiles(allNames, GameCatalog.TagsOfAddonFile(info.FileName));
            _allAddonFiles.Add(new AddonFileItemViewModel(info, directory, cache, candidates, dllStatus, OnAddonFileToggleRequested));
        }

        ApplyFilter();
    }

    /// <summary>
    /// 「插件文件」那一截的删除：二级确认 → 删文件 → 顺手把各游戏 ini 里指向它的条目清掉。
    /// </summary>
    private async void Button_DeleteAddonFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AddonFileItemViewModel item })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除插件文件",
            Content = $"确定删掉这个文件吗？\n\n{item.FileName}\n\n" +
                      "文件会从插件目录里真正删除（不进回收站）；各游戏 ReShade.ini 里指向它的 " +
                      "DisabledAddons / LoadFromDllMain 条目也会一起清掉。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunAsync(async () =>
        {
            try
            {
                string fullPath = item.FullPath;
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }

                AddonReferenceCleanResult clean = PurgeAddonReferences([Path.GetFileName(fullPath)]);
                TextBlock_Status.Text = $"已删除 {Path.GetFileName(fullPath)}" +
                                        (clean.Changed ? $"，并清了 {clean.ChangedGames.Count} 个游戏 ini 里的残留条目。" : "。");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Delete addon file");
                ShowInfo("删除失败", ex.Message, InfoBarSeverity.Error);
                return;
            }

            RefreshAddonFiles();
            await Task.CompletedTask;
        });
    }

    /// <summary>「插件文件」那一截的开关：改这一个文件的后缀</summary>
    private void OnAddonFileToggleRequested(AddonFileItemViewModel item, bool enabled)
    {
        AddonToggleResult result = AddonFileSwitcher.SetEnabled(item.FullPath, enabled);

        if (!result.Ok)
        {
            item.Revert();
            _logger.LogWarning("Global addon toggle failed: {Error}", result.Error);
            TextBlock_Status.Text = "改名失败：" + result.Error;
            ShowInfo("改名失败", result.Error ?? string.Empty, InfoBarSeverity.Error);
            return;
        }

        _logger.LogInformation("Global addon toggle: {Name} -> {Path}", item.FileName, result.Path);

        item.MarkToggled(enabled, Path.GetFileName(result.Path));
        TextBlock_Status.Text = $"「{item.Name}」已{(enabled ? "全局启用" : "全局禁用")}（{Path.GetFileName(result.Path)}）。";

        // 扩展包那边的开关状态也跟着变
        foreach (PluginItemViewModel extension in Items)
        {
            extension.RefreshGlobalEnabled();
        }
    }

    #endregion

    #region 视图切换 / 过滤

    private void RadioButtons_View_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 事件可能在 InitializeComponent 解析到一半就触发，这时后面的控件还是 null
        if (Grid_Plugins is null || Grid_OptiScaler is null || Grid_Modules is null || TextBlock_Count is null)
        {
            return;
        }

        if (RadioButtons_View.SelectedItem is not RadioButton { Tag: string tag })
        {
            return;
        }

        _tab = tag;

        // 切到哪一页就把哪一页的本地数据重读一遍（都是读本地目录，不打网络）
        if (tag == "optiscaler")
        {
            RefreshOptiScaler();
        }
        else if (tag == "modules")
        {
            RefreshModules();
        }
        else
        {
            RefreshAddonFiles();
        }

        ApplyFilter();
    }

    private void TextBox_Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = TextBox_Search.Text?.Trim() ?? string.Empty;
        ApplyFilter();
    }

    private bool MatchSearch(params string?[] values)
    {
        if (string.IsNullOrWhiteSpace(_search))
        {
            return true;
        }

        foreach (string? value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Contains(_search, StringComparison.CurrentCultureIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>三个标签页共用：搜索 / 空状态 / 计数都在这里收口</summary>
    private void ApplyFilter()
    {
        if (TextBlock_PluginsCurrentEmpty is null || TextBlock_Count is null)
        {
            return;
        }

        UpdatePluginLists();
        UpdateOptiScalerLists();
        UpdateModuleLists();
        UpdateTabVisibility();
        UpdateCount();
    }

    private void UpdateTabVisibility()
    {
        if (Grid_Plugins is null || Grid_OptiScaler is null || Grid_Modules is null)
        {
            return;
        }

        Grid_Plugins.Visibility = _tab == "plugins" ? Visibility.Visible : Visibility.Collapsed;
        Grid_OptiScaler.Visibility = _tab == "optiscaler" ? Visibility.Visible : Visibility.Collapsed;
        Grid_Modules.Visibility = _tab == "modules" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCount()
    {
        TextBlock_Count.Text = _tab switch
        {
            "optiscaler" => $"当前 {OptiScalerBuilds.Count} · 可下载 {OptiScalerSources.Count}",
            "modules" => $"当前 {CurrentModules.Count} · 可下载 {AvailableModules.Count}",
            _ => $"当前 {InstalledItems.Count + OrphanAddonFiles.Count} · 可下载 {VisibleItems.Count}",
        };
    }

    /// <summary>插件页：当前 = 已装扩展（+ 没归属的插件文件），可下载 = 还没装的扩展</summary>
    private void UpdatePluginLists()
    {
        // 已经归到某个扩展卡片下面的插件文件，不再单独列一遍
        HashSet<string> owned = new(StringComparer.OrdinalIgnoreCase);
        foreach (PluginItemViewModel item in Items)
        {
            foreach (string file in item.GlobalAddonFiles)
            {
                owned.Add(Path.GetFileName(file));
            }
        }

        bool hasSearch = !string.IsNullOrWhiteSpace(_search);

        InstalledItems.Clear();
        foreach (PluginItemViewModel item in Items)
        {
            if (item.IsInstalled && MatchSearch(item.Name, item.Description, item.Manifest.Id))
            {
                InstalledItems.Add(item);
            }
        }

        VisibleItems.Clear();
        foreach (PluginItemViewModel item in Items)
        {
            if (!item.IsInstalled && MatchSearch(item.Name, item.Description, item.Manifest.Id))
            {
                VisibleItems.Add(item);
            }
        }

        OrphanAddonFiles.Clear();
        foreach (AddonFileItemViewModel file in _allAddonFiles)
        {
            if (!owned.Contains(file.FileName) && MatchSearch(file.Name, file.FileName, file.Slug))
            {
                OrphanAddonFiles.Add(file);
            }
        }

        // 展开的卡片如果被搜索过滤掉了，收起来
        foreach (PluginItemViewModel item in Items)
        {
            if (!InstalledItems.Contains(item) && !VisibleItems.Contains(item))
            {
                item.IsExpanded = false;
            }
        }

        TextBlock_PluginsCurrentEmpty.Text = hasSearch
            ? "没有符合搜索条件的插件。"
            : _manager is null ? "没有可管理的 HoYoShade 目录。" : "插件目录里没有插件。";
        TextBlock_PluginsCurrentEmpty.Visibility =
            InstalledItems.Count == 0 && OrphanAddonFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        TextBlock_OrphanCaption.Visibility = OrphanAddonFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        TextBlock_PluginsAvailableEmpty.Text = hasSearch
            ? "没有符合搜索条件的插件。"
            : "目录里的插件都已经装上了。";
        TextBlock_PluginsAvailableEmpty.Visibility = VisibleItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>OptiScaler 页：当前 = 本地构建，可下载 = 还没装过的来源</summary>
    private void UpdateOptiScalerLists()
    {
        bool hasSearch = !string.IsNullOrWhiteSpace(_search);

        OptiScalerBuilds.Clear();
        foreach (OptiScalerBuildItemViewModel build in _allOptiScalerBuilds)
        {
            if (MatchSearch(build.Title, build.Build.Version, build.Build.SourceId, build.Build.AssetName))
            {
                OptiScalerBuilds.Add(build);
            }
        }

        OptiScalerSources.Clear();
        foreach (OptiScalerSourceItemViewModel source in _allOptiScalerSources)
        {
            if (MatchSearch(source.Name, source.Description, source.Repository, source.TagsText))
            {
                OptiScalerSources.Add(source);
            }
        }

        TextBlock_OptiScalerCurrentEmpty.Text = hasSearch
            ? "没有符合搜索条件的 OptiScaler。"
            : AppConfig.OptiScalerRootPath.Length == 0
                ? "还没读到用户数据目录，装不了 OptiScaler。"
                : "还没有下载任何 OptiScaler。先在下面「可下载」里拉一个。";
        TextBlock_OptiScalerCurrentEmpty.Visibility = OptiScalerBuilds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        TextBlock_OptiScalerAvailableEmpty.Text = hasSearch
            ? "没有符合搜索条件的来源。"
            : "可下载的来源都已经装过一份了。";
        TextBlock_OptiScalerAvailableEmpty.Visibility = OptiScalerSources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>模块页：当前 = 装好的模块，可下载 = 目录里还没装的模块</summary>
    private void UpdateModuleLists()
    {
        bool hasSearch = !string.IsNullOrWhiteSpace(_search);

        CurrentModules.Clear();
        foreach (ModuleItemViewModel module in _allModules)
        {
            if (MatchSearch(module.Name, module.Description, module.Key, module.DllPath, module.TagsText))
            {
                CurrentModules.Add(module);
            }
        }

        AvailableModules.Clear();
        foreach (ModuleDownloadItemViewModel module in _allModuleDownloads)
        {
            if (MatchSearch(module.Name, module.Description, module.Module.Id, module.TagsText))
            {
                AvailableModules.Add(module);
            }
        }

        TextBlock_ModulesCurrentEmpty.Text = hasSearch
            ? "没有符合搜索条件的模块。"
            : "还没有装任何模块。先在下面「可下载」里拉一个，或「添加 DLL…」。";
        TextBlock_ModulesCurrentEmpty.Visibility = CurrentModules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        TextBlock_ModulesAvailableEmpty.Text = hasSearch
            ? "没有符合搜索条件的模块。"
            : "目录里能装的模块都已经装上了。";
        TextBlock_ModulesAvailableEmpty.Visibility = AvailableModules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    #endregion

    /// <summary>汉化备份目录：&lt;用户数据目录&gt;.hysxi18n-backup</summary>
    internal static string I18nBackupDirectory => string.IsNullOrWhiteSpace(AppConfig.UserDataFolder)
        ? Path.Combine(Path.GetTempPath(), "HoYoShadeHub-i18n-backup")
        : Path.Combine(AppConfig.UserDataFolder, ".hysx", AddonLocalizer.BackupFolderName);

    /// <summary>用户 / 远端翻译表目录：&lt;用户数据目录&gt;.hysxi18n</summary>
    internal static string I18nTableDirectory => string.IsNullOrWhiteSpace(AppConfig.UserDataFolder)
        ? string.Empty
        : Path.Combine(AppConfig.UserDataFolder, ".hysx", "i18n");

    #region 插件文件汉化（入口已挪到设置 → 实验性功能，这里保留逻辑）

    /// <summary>插件汉化：按翻译表把这行插件里的英文界面文本原地换成中文（先自动备份）</summary>
    private async void Button_LocalizeAddon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AddonFileItemViewModel item })
        {
            return;
        }

        List<AddonI18nTable> tables = AddonLocalizer.LoadTables(I18nTableDirectory);
        AddonI18nTable? table = AddonLocalizer.SelectTable(tables, item.FileName);

        if (table is null)
        {
            ShowInfo("没有这个插件的翻译表",
                $"{item.FileName} 还没有对应翻译表。可以自己往 {I18nTableDirectory} 放一个 json（和内置的 i18n.builtin.json 同格式，slug 填插件文件名前缀）。",
                InfoBarSeverity.Warning);
            return;
        }

        try
        {
            // 已经汉化过：先还原成原文再按当前表来一遍 —— 否则英文原文已经被换掉了，
            // 第二次点会大面积「DLL 里没有」，用户看到的还是上一版翻译。
            string prefix = string.Empty;
            if (AddonLocalizer.BackupPathOf(item.FullPath, I18nBackupDirectory) is not null)
            {
                AddonLocalizeResult undo = AddonLocalizer.Restore(item.FullPath, I18nBackupDirectory);
                prefix = undo.Ok ? "已还原上次的汉化，" : string.Empty;
            }

            // 同一份插件在别的 HoYoShade 目录里可能还有副本（便携版 / 默认装各一份），
            // 游戏读哪一份取决于它用哪个启动器 —— 所以一起打上。
            TextBlock_Status.Text = $"正在找 {item.FileName} 的副本…";
            List<string> copies = await Task.Run(() => AddonLocalizationJob.FindCopies(item.FileName));
            List<string> targets =
            [
                item.FullPath,
                .. copies.Where(p => !string.Equals(p, item.FullPath, StringComparison.OrdinalIgnoreCase)),
            ];

            List<string> details = [];
            int total = 0;

            foreach (string target in targets)
            {
                AddonLocalizeResult one = AddonLocalizer.Apply(target, table, I18nBackupDirectory);
                AddonLocalizationJob.Remember(target);
                total += one.Applied;
                details.Add($"{target} → {one.Applied} 条");
            }

            TextBlock_Status.Text = $"{prefix}汉化 {targets.Count} 份副本、共 {total} 条（读取中：{string.Join("；", details)}）。重启游戏生效，随时可以「还原」";
            _logger.LogInformation("Localize addon {File}: {Prefix}{Total} 条 / {Count} 份 → {Paths}", item.FileName, prefix, total, targets.Count, string.Join(" | ", targets));
        }
        catch (Exception ex)
        {
            // 插件正被游戏加载时文件是锁着的，Move/替换会抛 IOException —— 提示清楚，别让用户以为点过了
            ShowInfo("汉化失败",
                $"{item.FileName} 写不进去，多半是插件正被游戏占用（先退出游戏再点一次）。{ex.Message}",
                InfoBarSeverity.Error);
            _logger.LogWarning(ex, "Localize addon {File} failed", item.FileName);
        }

        RefreshAddonFiles();
    }

    /// <summary>把插件还原成备份里的原版</summary>
    private void Button_RestoreAddon_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: AddonFileItemViewModel item })
        {
            return;
        }

        try
        {
            List<string> targets =
            [
                item.FullPath,
                .. AddonLocalizationJob.FindCopies(item.FileName)
                    .Where(p => !string.Equals(p, item.FullPath, StringComparison.OrdinalIgnoreCase)),
            ];

            List<string> details = [];

            foreach (string target in targets)
            {
                AddonLocalizeResult one = AddonLocalizer.Restore(target, I18nBackupDirectory);
                AddonLocalizationJob.Forget(target);
                details.Add($"{Path.GetFileName(target)} → {one.Message}");
            }

            TextBlock_Status.Text = $"{item.FileName}：{string.Join("；", details)}";
        }
        catch (Exception ex)
        {
            ShowInfo("还原失败",
                $"{item.FileName} 写不进去，多半是插件正被游戏占用（先退出游戏再点一次）。{ex.Message}",
                InfoBarSeverity.Error);
            _logger.LogWarning(ex, "Restore addon {File} failed", item.FileName);
        }

        RefreshAddonFiles();
    }

    /// <summary>把这个页面上所有「能对上翻译表」的插件都汉化一遍（含别的 HoYoShade 目录里的同名副本）</summary>
    private async void Button_LocalizeAllAddons_Click(object sender, RoutedEventArgs e)
    {
        List<AddonI18nTable> tables = AddonLocalizer.LoadTables(I18nTableDirectory);
        List<AddonFileItemViewModel> targets = [.. _allAddonFiles.Where(f => AddonLocalizer.SelectTable(tables, f.FileName) is not null)];

        if (targets.Count == 0)
        {
            ShowInfo("没有可汉化的插件", "这个目录里没有能对上翻译表的插件文件。", InfoBarSeverity.Warning);
            return;
        }

        // 一个插件可能有好几个 HoYoShade 目录各一份，而且界面可能是两个插件叠着画的
        // （用户实测：renodx-dlss 汉化了、dlss5-super-anus 没有 → 中文下面漏出英文），所以一次全打。
        TextBlock_Status.Text = $"正在汉化 {targets.Count} 个插件…";

        int copies = 0;
        int entries = 0;
        List<string> details = [];

        foreach (AddonFileItemViewModel item in targets)
        {
            AddonI18nTable table = AddonLocalizer.SelectTable(tables, item.FileName)!;
            List<string> found = await Task.Run(() => AddonLocalizationJob.FindCopies(item.FileName));
            List<string> paths =
            [
                item.FullPath,
                .. found.Where(p => !string.Equals(p, item.FullPath, StringComparison.OrdinalIgnoreCase)),
            ];

            int applied = 0;

            foreach (string path in paths)
            {
                try
                {
                    if (AddonLocalizer.BackupPathOf(path, I18nBackupDirectory) is not null)
                    {
                        AddonLocalizer.Restore(path, I18nBackupDirectory);
                    }

                    applied += AddonLocalizer.Apply(path, table, I18nBackupDirectory).Applied;
                    AddonLocalizationJob.Remember(path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Localize addon {Path} failed", path);
                }
            }

            copies += paths.Count;
            entries += applied;
            details.Add($"{item.FileName} → {applied} 条");
        }

        TextBlock_Status.Text = $"全部汉化完成：{targets.Count} 个插件 / {copies} 份副本 / {entries} 条。{string.Join("；", details)}（重启游戏生效，可逐个「还原」）";
        _logger.LogInformation("Localize all addons: {Detail}", string.Join(" | ", details));

        RefreshAddonFiles();
    }

    #endregion

    #region OptiScaler（下载 + 管理；总开关也在这里）

    private OptiScalerDownloader? _optiScalerDownloader;

    /// <summary>每个来源拉过的版本 tag（装完刷新时直接用，不再打网络）</summary>
    private readonly Dictionary<string, List<ExtensionVersion>> _optiScalerVersions = new(StringComparer.OrdinalIgnoreCase);

    private OptiScalerLibrary OptiLib => new(AppConfig.OptiScalerRootPath);

    private OptiScalerDownloader OptiDownloads => _optiScalerDownloader ??= new OptiScalerDownloader();

    private void ToggleSwitch_OptiScalerBuildEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: OptiScalerBuildItemViewModel item } toggle)
        {
            return;
        }

        AppConfig.SetOptiScalerBuildEnabled(item.Build.Id, toggle.IsOn);
        TextBlock_Status.Text = toggle.IsOn
            ? $"「{item.Title}」的全局开关：已打开。"
            : $"「{item.Title}」的全局开关：已关闭。";
    }

    /// <summary>
    /// 扫一遍本地 OptiScaler 库 + 填上「可下载」的来源。
    /// 库目录：&lt;用户数据目录&gt;\OptiScaler（跟 HoYoShade 目录平级，不进插件目录）。
    /// </summary>
    private void RefreshOptiScaler()
    {
        // 刷新会重建整个列表，先记住哪些卡片是展开的，重建后还给它展开
        HashSet<string> expandedBuilds = [.. _allOptiScalerBuilds.Where(b => b.IsExpanded).Select(b => b.Id)];
        HashSet<string> expandedSources = [.. _allOptiScalerSources.Where(s => s.IsExpanded).Select(s => s.Source.Id)];

        _allOptiScalerBuilds = [];
        _allOptiScalerSources = [];

        string root = AppConfig.OptiScalerRootPath;

        if (root.Length == 0)
        {
            ApplyFilter();
            return;
        }

        OptiScalerLibrary library = OptiLib;
        OptiScalerBuild? selected = library.GetSelected();
        List<OptiScalerBuild> all = library.List();

        // 内置来源 + 远端目录覆盖（catalog/optiscaler.json，缓存在 .hysxcatalog）
        List<OptiScalerSource> sources = OptiScalerCatalog.MergeWithBuiltin(
            _optiScalerOverlay ?? OptiScalerCatalog.LoadFile(RemoteCatalogService.OptiScalerCachePath));

        var byId = new Dictionary<string, OptiScalerSource>(StringComparer.OrdinalIgnoreCase);
        foreach (OptiScalerSource source in sources)
        {
            byId[source.Id] = source;
        }

        // 「当前」= 本地已经下载好的构建（展开可以换版本）
        foreach (OptiScalerBuild build in all)
        {
            byId.TryGetValue(build.SourceId, out OptiScalerSource? source);

            var vm = new OptiScalerBuildItemViewModel(build, selected?.Id, source)
            {
                IsExpanded = expandedBuilds.Contains(build.Id),
            };

            if (source is not null && _optiScalerVersions.TryGetValue(source.Id, out List<ExtensionVersion>? cached))
            {
                vm.ApplyVersions(cached);
            }

            _allOptiScalerBuilds.Add(vm);
        }

        // 「可下载」= 还没装过任何构建的来源（已经装过的在「当前」里，不重复列）
        foreach (OptiScalerSource source in sources)
        {
            bool installed = all.Any(b => string.Equals(b.SourceId, source.Id, StringComparison.OrdinalIgnoreCase));
            if (installed)
            {
                continue;
            }

            var vm = new OptiScalerSourceItemViewModel(source)
            {
                IsExpanded = expandedSources.Contains(source.Id),
            };

            if (_optiScalerVersions.TryGetValue(source.Id, out List<ExtensionVersion>? cached))
            {
                vm.ApplyVersions(cached);
            }

            _allOptiScalerSources.Add(vm);
        }

        ApplyFilter();
    }

    /// <summary>
    /// 构建卡片 / 来源卡片共用：点标题行展开 / 收起（默认只占紧凑一行）。
    /// 展开时顺手拉一次版本列表，好让用户直接换版本。
    /// </summary>
    private void Button_OptiScalerHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IOptiScalerVersionHost item })
        {
            return;
        }

        item.IsExpanded = !item.IsExpanded;

        if (item.IsExpanded && item.Versions.Count == 0 && item.CanInteract && item.HasVersionsSource)
        {
            _ = LoadOptiScalerVersionsAsync(item);
        }
    }

    /// <summary>
    /// 把 nvngx_dlssnr.dll 从「DLL 配置」装好的插件目录复制到构建目录。
    /// 各分支手册（wilsjo2 的 INSTALL-DLSSNR.md 第 3 步等）都要求它躺在包旁边。
    /// </summary>
    private void Button_PlaceOptiScalerNrdll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OptiScalerBuildItemViewModel item })
        {
            return;
        }

        NrdllPlaceResult result = OptiScalerRuntime.EnsureNrdll(
            item.Build.Directory,
            _manager?.Host.AddonsPath,
            OptiScalerBuilds
                .Where(v => !string.Equals(v.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                .Select(v => v.Build.Directory));

        TextBlock_Status.Text = result.Message;
        _logger.LogInformation("OptiScaler nrdll: {Status} ({Source} -> {Target})",
            result.Status, result.SourcePath, result.TargetPath);

        // 同时补 FGOutput=DLSSG 需要的 Streamline 整套
        if (!OptiScalerRuntime.HasStreamline(item.Build.Directory))
        {
            List<string> sl = OptiScalerRuntime.EnsureStreamline(
                item.Build.Directory,
                _manager?.Host.AddonsPath);
            if (sl.Count > 0)
            {
                TextBlock_Status.Text += $" 已补 Streamline：{string.Join("、", sl)}。";
            }
        }

        // FSR-only 游戏（原神）要的替代实现：OptiScaler README 明确写「FSR2-only 游戏需要
        // 手动提供 nvngx_dlss.dll」。它找的是 Util::DllPath()（OptiScaler 自己所在目录），
        // 所以文件要落在构建目录，而不是游戏目录。
        List<string> replacements = OptiScalerRuntime.EnsureUpscalerReplacements(
            item.Build.Directory,
            _manager?.Host.AddonsPath,
            OptiScalerBuilds
                .Where(v => !string.Equals(v.Id, item.Id, StringComparison.OrdinalIgnoreCase))
                .Select(v => v.Build.Directory));

        if (replacements.Count > 0)
        {
            TextBlock_Status.Text += $" 已补上采样替代实现：{string.Join("\u3001", replacements)}。";
        }
        else if (!OptiScalerRuntime.HasUpscalerReplacements(item.Build.Directory))
        {
            ShowInfo("\u7f3a nvngx_dlss.dll",
                "\u6784\u5efa\u76ee\u5f55\u91cc\u6ca1\u6709 nvngx_dlss.dll\u3002\u53ea\u6709 FSR \u7684\u6e38\u620f\uff08\u539f\u795e\uff09\u5fc5\u987b\u9760\u5b83\u505a\u66ff\u4ee3\u5b9e\u73b0\uff0c"
                + "\u5426\u5219 OptiScaler \u4f1a\u62a5\u300cCan't find nvngx.dll and libxess.dll and FSR inputs\u300d\u3002"
                + "\u5148\u5230\u300cDLL \u914d\u7f6e\u300d\u88c5\u4e00\u4efd\uff0c\u6216\u628a\u5b83\u653e\u5230\u63d2\u4ef6\u76ee\u5f55\u3002",
                InfoBarSeverity.Warning);
        }

        if (OptiScalerRuntime.EnsureConfigDllPath(item.Build.Directory))
        {
            _logger.LogInformation("OptiScaler ini: OptiDllPath pinned to absolute path under {Directory}", item.Build.Directory);
        }

        if (!result.Ok)
        {
            ShowInfo("缺 nvngx_dlssnr.dll", result.Message, InfoBarSeverity.Warning);
        }

        RefreshOptiScaler();
    }

    private void Button_OpenOptiScalerBuild_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OptiScalerBuildItemViewModel item })
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(item.Build.Directory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open OptiScaler folder");
            TextBlock_Status.Text = "打不开目录：" + ex.Message;
        }
    }

    private async void Button_DeleteOptiScalerBuild_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OptiScalerBuildItemViewModel item })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除这个 OptiScaler",
            Content = $"确定删掉这个构建吗？\n\n{item.Id}\n\n{item.Build.Directory}\n\n" +
                      "整个目录（含解压出来的所有文件）会被真正删除；如果它是当前启用的那个，删除后就没有启用的 OptiScaler 了。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (OptiLib.Delete(item.Id))
        {
            TextBlock_Status.Text = $"已删除 OptiScaler 构建：{item.Id}";
        }

        RefreshOptiScaler();
    }

    /// <summary>点开版本下拉时自动拉一次版本（跟扩展包那边一样，点下拉自动获取）。</summary>
    private async void ComboBox_OptiScalerVersions_DropDownOpened(object sender, object e)
    {
        if (sender is not FrameworkElement { DataContext: IOptiScalerVersionHost item }
            || item.Versions.Count > 0
            || !item.CanInteract
            || !item.HasVersionsSource)
        {
            return;
        }

        await LoadOptiScalerVersionsAsync(item);
    }

    private async Task LoadOptiScalerVersionsAsync(IOptiScalerVersionHost item)
    {
        item.CanInteract = false;
        item.StatusText = "正在获取版本…";

        try
        {
            List<ExtensionVersion> versions = await OptiDownloads.ListVersionsAsync(item.Source);

            _optiScalerVersions[item.Source.Id] = versions;
            item.ApplyVersions(versions);

            if (versions.Count == 0)
            {
                item.StatusText = "没拿到任何版本 —— 仓库可能已改名 / 删除，或者网络不通。";
            }
            else if (item.SelectedVersion is not null)
            {
                item.StatusText = $"共 {versions.Count} 个版本，默认选中 {item.TagOf(item.SelectedVersion)}。";
            }
            else
            {
                item.StatusText = $"共 {versions.Count} 个版本（第一个是最新的）。";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "List OptiScaler versions from {Repo}", item.Source.Repository);
            item.StatusText = "获取版本失败：" + ex.Message;
        }
        finally
        {
            item.CanInteract = true;
        }
    }

    /// <summary>「下载 / 切换 / 重装」：按选中的 tag 装这个来源的某个版本。</summary>
    private async void Button_DownloadOptiScaler_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IOptiScalerVersionHost item } || _isWorking)
        {
            return;
        }

        string? tag = item.TagOf(item.SelectedVersion);
        if (string.IsNullOrWhiteSpace(tag))
        {
            ShowInfo("还没选版本", "点开「版本」下拉，选一个版本。", InfoBarSeverity.Warning);
            return;
        }

        await RunAsync(() => DownloadOptiScalerVersionAsync(item, tag));
    }

    /// <summary>一个 release 里通常有好几个 zip（主包 / rtx40-mfg / 补丁），让用户挑。</summary>
    private async Task DownloadOptiScalerVersionAsync(IOptiScalerVersionHost item, string tag)
    {
        item.CanInteract = false;
        item.StatusText = $"正在查 {tag} 里的压缩包…";

        string finalStatus = string.Empty;

        // 一个可取消 / 可暂停的下载任务。进度显示仍用下面这个 progress（它还兼着写 item.StatusText）
        // 但取消令牌和暂停信号从这里来  之前没有它，所以下载根本停不下来。
        using var job = BeginDownloadJob($"\u6b63\u5728\u4e0b\u8f7d OptiScaler\uff08{tag}\uff09");

        var progress = new Progress<DownloadProgress>(p =>
        {
            string text = p.Percent is null
                ? $"{p.BytesReceived / 1024d / 1024d:F1} MB"
                : $"{p.Percent.Value:F0}%　{p.BytesReceived / 1024d / 1024d:F1} MB";
            TextBlock_Status.Text = $"正在下载 OptiScaler（{tag}）… {text}";
            ProgressBar_Overall.IsIndeterminate = p.Percent is null;
            ProgressBar_Overall.Value = p.Percent ?? 0;
        });

        try
        {
            List<string> assets = await OptiDownloads.ListAssetsAsync(item.Source, tag);

            if (assets.Count == 0)
            {
                item.StatusText = $"{tag} 里没有可下载的 .zip / .exe（7z 之类暂不支持）。";
                return;
            }

            string? asset = assets.Count == 1 ? assets[0] : await PickOptiScalerAssetAsync(item.Source, tag, assets);
            if (string.IsNullOrWhiteSpace(asset))
            {
                item.StatusText = "已取消。";
                return;
            }

            bool installer = OptiScalerDownloader.IsInstaller(asset);
            item.StatusText = installer ? $"正在下载安装程序 {asset}…" : $"正在下载 {asset}…";

            OptiScalerLibrary library = OptiLib;
            string targetDirectory = library.DirectoryFor(item.Source.Id, tag);
            bool setupDeclined = false;

            OptiScalerBuild build = await OptiDownloads.InstallAsync(
                item.Source,
                tag,
                asset,
                library,
                progress,
                confirmBeforeRun: async _ =>
                {
                    // 运行它自己的安装程序之前先提醒一句（用户要求）
                    bool run = await ConfirmOptiScalerSetupAsync(targetDirectory);
                    setupDeclined = !run;
                    return run;
                },
                cancellationToken: job.Token,
                pauseToken: job.Pause);

            // 各分支手册都要求把 nvngx_dlssnr.dll 放在包旁边 —— 装完顺手从插件目录补一份
            NrdllPlaceResult nrdll = OptiScalerRuntime.EnsureNrdll(
                build.Directory,
                _manager?.Host.AddonsPath,
                OptiScalerBuilds.Select(v => v.Build.Directory));

            // FGOutput=DLSSG 还需要 OptiScaler/streamline 整套 + nvngx_dlssg.dll，一并补齐
            List<string> streamline = OptiScalerRuntime.EnsureStreamline(
                build.Directory,
                _manager?.Host.AddonsPath);

            // ini 默认 OptiDllPath=auto 按游戏 exe 目录解析，外部注入要钉成数据目录的绝对路径
            if (OptiScalerRuntime.EnsureConfigDllPath(build.Directory))
            {
                _logger.LogInformation("OptiScaler ini: OptiDllPath pinned under {Directory}", build.Directory);
            }

            // dlss-unlocked 的 MFG 解锁只认 310.9 签名：本地没有就下载一份，分发到构建目录 2 个位置，
            // 避免运行时 BFS 命中游戏目录旧版（310.6.0 → unlock unavailable）
            DlssgEnsureResult unlockDlssg = await OptiScalerRuntime.EnsureDlssgForUnlockAsync(
                build.Directory,
                [
                    _manager?.Host.AddonsPath,
                    Path.Combine(build.Directory, "OptiScaler", OptiScalerRuntime.StreamlineFolderName),
                    build.Directory,
                    Path.Combine(build.Directory, "OptiScaler"),
                ],
                cancellationToken: job.Token);
            _logger.LogInformation("OptiScaler MFG unlock dlssg: ok={Ok} - {Message}",
                unlockDlssg.Ok, unlockDlssg.Message);

            finalStatus = installer
                ? setupDeclined
                    ? $"安装程序已下载（{build.SizeBytes / 1024d / 1024d:F1} MB），还没运行 —— 点「打开目录」可以自己双击它。"
                    : build.DllPath is null
                        ? "安装程序跑完了，但没在目录里找到可注入的 dll（version.dll / dxgi.dll 之类）。"
                        : $"装好了：注入目标 = {Path.GetFileName(build.DllPath)}。{nrdll.Message}到启动器页勾「启动 OptiScaler」即可。"
                : $"已装好 {build.Id}（{build.SizeBytes / 1024d / 1024d:F1} MB）。" +
                  (build.DllPath is null ? "注意：包里没找到 OptiScaler.dll。" : string.Empty) +
                  (unlockDlssg.Ok ? string.Empty : $"（{unlockDlssg.Message}）");

            // 全库只有这一个构建：还没选过的游戏默认用它
            int autoEnabled = AutoEnableSoleOptiScalerBuild();
            if (autoEnabled > 0)
            {
                finalStatus += $"　已为 {autoEnabled} 个游戏默认启用此 OptiScaler。";
            }

            item.StatusText = finalStatus;
            TextBlock_Status.Text = $"OptiScaler「{build.Id}」安装完成：{build.Directory}";
            _logger.LogInformation("OptiScaler installed: {Id} from {Asset}", build.Id, asset);

            RefreshOptiScaler();

            // 刷新之后列表是新的对象了，把状态挪到对应那一行（来源可能已经变成「当前」里的构建）
            string newId = OptiScalerLibrary.MakeId(item.Source.Id, tag);
            OptiScalerBuildItemViewModel? target =
                _allOptiScalerBuilds.FirstOrDefault(b => string.Equals(b.Id, newId, StringComparison.OrdinalIgnoreCase))
                ?? (item is OptiScalerBuildItemViewModel old
                    ? _allOptiScalerBuilds.FirstOrDefault(b => string.Equals(b.Id, old.Id, StringComparison.OrdinalIgnoreCase))
                    : null);

            if (target is not null)
            {
                target.StatusText = finalStatus;
                target.IsExpanded = true;
            }

            ApplyFilter();
        }
        finally
        {
            if (job.IsCancellationRequested)
            {
                item.StatusText = "\u5df2\u53d6\u6d88\u4e0b\u8f7d\u3002";
                TextBlock_Status.Text = "\u5df2\u53d6\u6d88\u4e0b\u8f7d\u3002";
            }

            item.CanInteract = true;
            ProgressBar_Overall.IsIndeterminate = false;
            Button_CancelDownload.IsEnabled = true;
            EndDownloadJob();
        }
    }

    /// <summary>
    /// 运行来源自己的安装程序**之前**先提醒一句（用户要求）：
    /// 它的默认安装目录就是启动目录（构建目录），装完那里会有 version.dll 供注入。
    /// </summary>
    private async Task<bool> ConfirmOptiScalerSetupAsync(string targetDirectory)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "即将启动 OptiScaler 安装程序",
            Content = "这个来源用的是它自己的安装程序。\n\n" +
                      "请安装到默认的目录（别改）：\n" + targetDirectory + "\n\n" +
                      "装完这里会有 version.dll —— 之后到启动器页勾上「启动 OptiScaler」，启动游戏时就会把它注进去。",
            PrimaryButtonText = "启动安装程序",
            CloseButtonText = "先不装",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>一个 release 里有多个 zip 时，弹个下拉让用户选（名字里带 Patch 的会提示）。</summary>
    private async Task<string?> PickOptiScalerAssetAsync(OptiScalerSource source, string tag, List<string> assets)
    {
        var combo = new ComboBox
        {
            MinWidth = 420,
            ItemsSource = assets,
            SelectedIndex = OptiScalerDownloader.PickAsset(assets) is { } preferred
                ? Math.Max(0, assets.IndexOf(preferred))
                : 0,
        };

        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{source.Name} 的 {tag} 里有 {assets.Count} 个包，选一个下载：",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(combo);

        if (assets.Any(OptiScalerDownloader.LooksLikePatch))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "名字里带 Patch 的是补丁包，要先装主包再覆盖它。",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.7,
            });
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "选择要下载的包",
            Content = panel,
            PrimaryButtonText = "下载",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary ? combo.SelectedItem as string : null;
    }

    private void Hyperlink_OptiScalerRepo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IOptiScalerVersionHost item })
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/" + item.Source.Repository) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open OptiScaler repo");
        }
    }

    #endregion

    #region 模块（下载 / 部署 / 全局开关）

    /// <summary>模块「当前 / 可下载」两截：当前 = 装好的（内置 + 手动 DLL），可下载 = 目录里还没装的。</summary>
    private void RefreshModules()
    {
        HashSet<string> expanded = [.. _allModules.Where(m => m.IsExpanded).Select(m => m.Key)];

        _allModules = [];
        _allModuleDownloads = [];

        foreach (ModuleEntry entry in ModuleRegistry.List())
        {
            // 没装的内置模块不放在「当前」；手动加的条目即使文件已经不在了也留着，
            // 好让用户还能把它从列表里删掉
            if (entry.IsBuiltin && string.IsNullOrWhiteSpace(entry.DllPath))
            {
                continue;
            }

            _allModules.Add(new ModuleItemViewModel(entry, OnModuleToggleRequested)
            {
                IsExpanded = expanded.Contains(entry.Key),
            });
        }

        foreach (ModuleDefinition module in ModuleRegistry.All())
        {
            if (ModuleRegistry.ResolveDllPath(module) is not null)
            {
                continue;
            }

            _allModuleDownloads.Add(new ModuleDownloadItemViewModel(module));
        }

        ApplyFilter();
    }

    private void OnModuleToggleRequested(ModuleItemViewModel item, bool enabled)
    {
        if (item.IsBuiltin)
        {
            AppConfig.SetModuleEnabled(item.Key, enabled);
        }
        else
        {
            AppConfig.SetManualModuleDllEnabled(item.Key, enabled);
        }

        TextBlock_Status.Text = $"模块「{item.Name}」已全局{(enabled ? "启用" : "禁用")}。";
    }

    private void Button_ModuleHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModuleItemViewModel item })
        {
            item.IsExpanded = !item.IsExpanded;
        }
    }

    private void Button_ModuleDownloadHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModuleDownloadItemViewModel item })
        {
            bool expand = !item.IsExpanded;
            item.IsExpanded = expand;

            if (expand && !item.VersionsLoaded && !item.Module.IsDirect)
            {
                _ = LoadModuleVersionsAsync(item);
            }
        }
    }

    /// <summary>拉这个模块能装的版本（GitHub tag，新 → 旧）铺进下拉</summary>
    private async Task LoadModuleVersionsAsync(ModuleDownloadItemViewModel item)
    {
        try
        {
            var source = new OptiScalerSource
            {
                Id = item.Module.Id,
                Name = item.Module.Name,
                Repository = item.Module.Repository,
                TagPattern = item.Module.TagPattern,
            };

            List<ExtensionVersion> versions = await Task.Run(() =>
                new OptiScalerDownloader().ListVersionsAsync(source));
            item.ApplyVersions(versions);
        }
        catch (Exception ex)
        {
            item.VersionsLoaded = true;
            item.StatusText = "拉版本列表失败：" + ex.Message;
            _logger.LogWarning(ex, "List module versions for {Id}", item.Module.Id);
        }
    }

    /// <summary>手动加一个要注入的 DLL（内置模块表里没有的）。</summary>
    private async void Button_AddModuleDll_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? file = await FileDialogHelper.PickSingleFileAsync(XamlRoot, ("DLL", ".dll"), ("所有文件", ".*"));
            if (string.IsNullOrWhiteSpace(file))
            {
                return;
            }

            List<string> list = [.. AppConfig.GetManualModuleDlls()];
            if (!list.Contains(file, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(file);
            }

            AppConfig.SetManualModuleDlls(list);
            AppConfig.SetManualModuleDllEnabled(file, true);
            TextBlock_Status.Text = $"已添加模块 DLL：{file}";
            RefreshModules();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add manual module dll");
            ShowInfo("添加失败", ex.Message, InfoBarSeverity.Error);
        }
    }

    private void Button_OpenModulesFolder_Click(object sender, RoutedEventArgs e)
    {
        string root = AppConfig.ModulesRootPath;
        if (root.Length == 0)
        {
            TextBlock_Status.Text = "还没读到用户数据目录。";
            return;
        }

        try
        {
            Directory.CreateDirectory(root);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open modules folder");
            TextBlock_Status.Text = "打不开目录：" + ex.Message;
        }
    }

    private void Button_RefreshModules_Click(object sender, RoutedEventArgs e)
    {
        RefreshModules();
        TextBlock_Status.Text = "模块列表已刷新。";
    }

    private void Button_OpenModuleFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModuleItemViewModel item })
        {
            return;
        }

        string? directory = item.HasDll ? Path.GetDirectoryName(item.DllPath) : null;

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            directory = ModuleRegistry.Find(item.Key)?.Directory;
        }

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            TextBlock_Status.Text = "这个模块还没有目录 —— 先下载一份，或者文件已经不在了。";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open module folder");
            TextBlock_Status.Text = "打不开目录：" + ex.Message;
        }
    }

    private async void Button_DeleteModule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModuleItemViewModel item })
        {
            return;
        }

        string title = item.IsBuiltin ? "删除模块" : "删除手动模块";
        string body = item.IsBuiltin
            ? $"确定删掉这个模块吗？\n\n{item.Name}\n\n模块目录（含下载的所有文件）会被真正删除，每个游戏对它的勾选与全局开关一并清掉；以后想再用，在下面「可下载」里重装即可。"
            : $"确定把这个手动模块删掉吗？\n\n{item.Key}\n\n会同时移除列表记录，并删除该 DLL 文件。";

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = body,
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        if (item.IsBuiltin)
        {
            if (!ModuleRegistry.DeleteInstalled(item.Key))
            {
                TextBlock_Status.Text = $"删除失败：{item.Name}（目录可能被占用，游戏还开着吗）";
                return;
            }

            TextBlock_Status.Text = $"已删除模块：{item.Name}";
        }
        else
        {
            string? deleteWarning = null;
            try
            {
                if (File.Exists(item.Key))
                {
                    File.Delete(item.Key);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Delete manual module file {Path}", item.Key);
                deleteWarning = $"记录已移除，但 DLL 文件没删掉：{ex.Message}";
            }

            List<string> list = [.. AppConfig.GetManualModuleDlls()
                .Where(p => !string.Equals(p, item.Key, StringComparison.OrdinalIgnoreCase))];
            AppConfig.SetManualModuleDlls(list);

            TextBlock_Status.Text = deleteWarning ?? $"已删除手动模块：{item.Name}";
        }

        RefreshModules();
    }

    /// <summary>「下载 / 更新」：走 OptiScaler 那套下载器把模块装进模块目录。</summary>
    private async void Button_DownloadModule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ModuleDownloadItemViewModel item } || _isWorking)
        {
            return;
        }

        await RunAsync(async () =>
        {
            item.CanInteract = false;
            item.StatusText = "正在下载 / 安装…";

            try
            {
                string? tag = item.TagOf(item.SelectedVersion);
                if (!item.Module.IsDirect && string.IsNullOrWhiteSpace(tag))
                {
                    item.StatusText = "先在「版本」里选一个。";
                    return;
                }

                string result = await ModuleRegistry.InstallAsync(
                    item.Module,
                    progress: null,
                    confirmBeforeRun: async _ => await ConfirmModuleSetupAsync(item.Module),
                    tag: tag);

                item.StatusText = "已装好：" + result;
                TextBlock_Status.Text = $"模块「{item.Name}」安装完成（{result}）。到左侧「模块」页勾选后，启动游戏时会注入。";
                _logger.LogInformation("Module installed: {Id} {Result}", item.Module.Id, result);
            }
            catch (Exception ex)
            {
                item.StatusText = "安装失败：" + ex.Message;
                _logger.LogError(ex, "Install module {Id}", item.Module.Id);
                ShowInfo("模块安装失败", ex.Message, InfoBarSeverity.Error);
            }
            finally
            {
                item.CanInteract = true;
            }

            RefreshModules();
            await Task.CompletedTask;
        });
    }

    /// <summary>模块来源自己的安装程序：运行前先弹一句「装到模块目录」。</summary>
    private async Task<bool> ConfirmModuleSetupAsync(ModuleDefinition module)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "即将运行模块安装程序",
            Content = $"「{module.Name}」用的是它自己的安装程序。\n\n" +
                      "请装到模块目录（默认即可，别改）：\n" + module.Directory + "\n\n" +
                      $"装完那里会有 {module.DllHint}；之后的注入由 Hub 负责。",
            PrimaryButtonText = "启动安装程序",
            CloseButtonText = "先不装",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    #endregion

    #region 顶部按钮

    private async void Button_Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void Button_Dlss5CompatCheck_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await ShowDlss5CompatibilityAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DLSS5 compatibility check");
            ShowInfo("DLSS5 检测出错", ex.Message, InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// 右上角「DLSS5 兼容性检测」：弹窗列 15 条（显卡 / 启动器 / 游戏目录 / XXMI），
    /// 7/9/10/11/12/13/14/15 带自动修复。
    ///
    /// <para>
    /// 这里检的是**全局那一份 HoYoShade** + 当前选中的那个游戏（能拿到就一起检游戏目录那几条）。
    /// 只看启动器（7~11）也行  没选游戏时 12~14 会明说「不知道游戏目录」。
    /// </para>
    /// </summary>
    private async Task ShowDlss5CompatibilityAsync()
    {
        ShadeHost? host = _manager?.Host ?? PluginHostLocator.Resolve(out _);

        if (host is null)
        {
            ShowInfo("DLSS5 兼容性检测", "还没找到 HoYoShade 目录  先点「指定目录」指一下再检测。", InfoBarSeverity.Warning);
            return;
        }

        var context = new Dlss5CompatContext
        {
            ShadeHost = host,
        };

        // 顺带把当前选中的游戏一起检（游戏目录那 12~14 条要它）
        try
        {
            GameEntry? entry = GameCatalog.GetOrCreate(GameCatalog.CreateService(), CurrentGameId);

            if (entry is not null)
            {
                var plugins = new GamePluginService(
                    entry,
                    host,
                    PluginHostLocator.AddonNameCachePath,
                    GameCatalog.AddonCandidateNames(),
                    GameCatalog.TagsOfAddonFile);

                context = new Dlss5CompatContext
                {
                    ShadeHost = host,
                    Game = entry,
                    GameId = CurrentGameId,
                    Profile = plugins.Profile,
                    ProfileError = plugins.ProfileError,
                    AddonStates = plugins.GetAddons(),
                    HookPoint = plugins.GetHookPoint(),
                    PluginService = plugins,
                };
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DLSS5 check: 读当前游戏失败，只检启动器那几条");
        }

        var dialog = new Dlss5CompatDialog(context)
        {
            XamlRoot = XamlRoot,
        };

        await dialog.ShowAsync();
    }

    private async void Button_PickFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folder = await FileDialogHelper.PickFolderAsync(XamlRoot);
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            if (ShadeHostLocator.FromShadeRoot(folder) is null)
            {
                ShowInfo("这个目录不是 HoYoShade",
                    $"在 {folder} 里找不到 ReShade64.dll。请选到 HoYoShade 这一层（通常在用户数据目录下）。",
                    InfoBarSeverity.Error);
                return;
            }

            PluginHostLocator.ManualShadeRoot = folder;
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pick shade folder");
            TextBlock_Status.Text = "选择目录失败：" + ex.Message;
        }
    }

    private void Button_OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = _manager?.Host.RootPath;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open shade folder");
        }
    }

    /// <summary>「当前」插件卡片上的「打开目录」：打开插件目录（addon 文件都在那里）。</summary>
    private void Button_OpenPluginFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? path = _manager?.Host.AddonsPath;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                TextBlock_Status.Text = "找不到插件目录。";
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open addons folder");
            TextBlock_Status.Text = "打不开目录：" + ex.Message;
        }
    }

    private async void Button_InstallLocal_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking)
        {
            return;
        }

        await RunAsync(InstallLocalAsync);
    }

    /// <summary>
    /// 本地安装：GitHub 原始 zip / 单个 addon / 模块 DLL 都收。
    /// 自动识别类型，OptiScaler 缺的 dlssnr / streamline / dlssg 由安装器补齐。
    /// </summary>
    private async Task InstallLocalAsync()
    {
        string? file = await FileDialogHelper.PickSingleFileAsync(
            XamlRoot,
            ("支持的包", ".zip"),
            ("ReShade 插件", ".addon64"),
            ("ReShade 插件", ".addon32"),
            ("DLL", ".dll"),
            ("所有文件", ".*"));

        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        // 标准 hysx 扩展包（带 manifest.json）走原有扩展安装流水线
        ExtensionManifest? manifest = ReadManifestFromPackage(file);
        if (manifest is not null)
        {
            manifest.Source = new ExtensionSource { Type = ExtensionSourceType.Local, Url = file };
            await RunInstallAsync(manifest);
            return;
        }

        var installer = new LocalPackageInstaller(
            AppConfig.OptiScalerRootPath,
            _manager?.Host.AddonsPath ?? string.Empty,
            AppConfig.ModulesRootPath,
            OptiScalerBuilds.Select(b => b.Build.Directory));

        LocalPackageInstallResult result = await installer.InstallAsync(file);

        switch (result.Kind)
        {
            case LocalPackageKind.Addon:
                await ReloadPluginDataAsync();
                break;
            case LocalPackageKind.OptiScaler:
                RefreshOptiScaler();
                int auto = AutoEnableSoleOptiScalerBuild();
                if (auto > 0)
                {
                    TextBlock_Status.Text = result.Summary + $"；已为 {auto} 个游戏默认启用。";
                    return;
                }
                break;
            case LocalPackageKind.Module:
                RefreshModules();
                break;
        }

        TextBlock_Status.Text = result.Summary;
    }

    /// <summary>拖文件经过：zip / addon / dll 显示复制光标。</summary>
    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)
            ? DataPackageOperation.Copy
            : DataPackageOperation.None;

        e.DragUIOverride.Caption = "松开以本地安装";
        e.DragUIOverride.IsCaptionVisible = true;
    }

    /// <summary>拖放：取第一个文件，走和「本地安装」完全相同的流程。</summary>
    private async void Grid_Drop(object sender, DragEventArgs e)
    {
        if (_isWorking || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
        {
            return;
        }

        await RunAsync(async () =>
        {
            var items = await e.DataView.GetStorageItemsAsync();
            string? file = items.FirstOrDefault()?.Path;

            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
            {
                return;
            }

            ExtensionManifest? manifest = ReadManifestFromPackage(file);
            if (manifest is not null)
            {
                manifest.Source = new ExtensionSource { Type = ExtensionSourceType.Local, Url = file };
                await RunInstallAsync(manifest);
                return;
            }

            var installer = new LocalPackageInstaller(
                AppConfig.OptiScalerRootPath,
                _manager?.Host.AddonsPath ?? string.Empty,
                AppConfig.ModulesRootPath,
                OptiScalerBuilds.Select(b => b.Build.Directory));

            LocalPackageInstallResult result = await installer.InstallAsync(file);

            switch (result.Kind)
            {
                case LocalPackageKind.Addon:
                    await ReloadPluginDataAsync();
                    break;
                case LocalPackageKind.OptiScaler:
                    RefreshOptiScaler();
                    int auto = AutoEnableSoleOptiScalerBuild();
                    if (auto > 0)
                    {
                        TextBlock_Status.Text = result.Summary + $"；已为 {auto} 个游戏默认启用。";
                        return;
                    }
                    break;
                case LocalPackageKind.Module:
                    RefreshModules();
                    break;
            }

            TextBlock_Status.Text = result.Summary;
        });
    }

    #endregion

    #region 列表操作

    /// <summary>点标题行 = 展开/收起详情（默认只占一行）；已装的卡片展开时顺手拉一次版本</summary>
    private void Button_PluginHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PluginItemViewModel item })
        {
            return;
        }

        bool expand = !item.IsExpanded;

        foreach (PluginItemViewModel other in Items)
        {
            other.IsExpanded = false;
        }

        item.IsExpanded = expand;

        if (expand && item.IsInstalled && !item.VersionsLoaded)
        {
            _ = LoadVersionsAsync(item);
        }
    }

    /// <summary>plugin version dropdown handler</summary>
    private async void ComboBox_PluginVersions_DropDownOpened(object sender, object e)
    {
        if (sender is not FrameworkElement { DataContext: PluginItemViewModel item })
        {
            return;
        }

        await LoadVersionsAsync(item);
    }

    /// <summary>拉这个插件能装的版本（GitHub tag，新 → 旧）+ 顺便看看有没有新版本</summary>
    private async Task LoadVersionsAsync(PluginItemViewModel item)
    {
        if (_manager is null || item.VersionsLoaded)
        {
            return;
        }

        try
        {
            item.VersionsHint = "正在获取版本列表…";
            List<ExtensionVersion> versions = await Task.Run(
                () => _manager.ListVersionsAsync(item.Manifest, 30));

            item.ApplyVersions(versions);

            item.VersionsLoaded = true;
            item.VersionsHint = versions.Count == 0
                ? "这个来源列不出可选版本（不是 GitHub Release 或网络不通），只能装最新。"
                : $"共 {versions.Count} 个版本可装（默认选中当前装着的那个）。";

            if (versions.Count > 0 && item.Installed is not null)
            {
                string latest = versions[0].Tag;
                bool same = string.Equals(item.Installed.ResolvedTag, latest, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(item.Installed.Version, latest, StringComparison.OrdinalIgnoreCase);
                item.UpdateTag = same ? null : latest;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "List versions for {Id}", item.Manifest.Id);
            item.VersionsLoaded = true;
            item.VersionsHint = "拉版本列表失败：" + ex.Message;
        }
    }

    /// <summary>装下拉里选的那个版本（当前已装的也是走这条路 = 重装）</summary>
    private async void Button_InstallVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PluginItemViewModel item })
        {
            return;
        }

        string? tag = item.TagOf(item.SelectedVersion);
        if (string.IsNullOrWhiteSpace(tag))
        {
            TextBlock_Status.Text = "先在「版本」里选一个。";
            return;
        }

        await RunInstallAsync(item.Manifest, tag);
    }

    private void Hyperlink_Homepage_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not HyperlinkButton { Content: string url } || string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open homepage {Url}", url);
        }
    }

    private async void Button_Install_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: PluginItemViewModel item })
        {
            await RunInstallAsync(item.Manifest);
        }
    }

    private async void Button_Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: PluginItemViewModel item })
        {
            return;
        }

        if (_manager is null || _isWorking)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "删除插件",
            Content = $"确定删除「{item.Name}」吗？\n会连这个插件的**其它版本**（包括被改名禁用的 .addon64x）一起删掉，免得目录里还剩一份；" +
                      "其它插件和你自己放的东西不动。被改动过的文件（哈希对不上）仍然保留。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunAsync(async () =>
        {
            ExtensionUninstallResult result = await _manager.UninstallAsync(item.Manifest.Id);

            var parts = new List<string> { $"删除 {result.DeletedFiles.Count} 个文件" };
            if (result.DeletedStaleFiles.Count > 0)
            {
                parts.Add($"连其它版本一起删了 {result.DeletedStaleFiles.Count} 个：{string.Join("、", result.DeletedStaleFiles.Take(3))}{(result.DeletedStaleFiles.Count > 3 ? " …" : string.Empty)}");
            }
            if (result.KeptOverwrittenFiles.Count > 0)
            {
                parts.Add($"{result.KeptOverwrittenFiles.Count} 个是原有文件，保留");
            }
            if (result.SkippedModifiedFiles.Count > 0)
            {
                parts.Add($"{result.SkippedModifiedFiles.Count} 个已改动，保留");
            }
            if (result.MissingFiles.Count > 0)
            {
                parts.Add($"{result.MissingFiles.Count} 个已不存在");
            }

            // 插件文件没了，各游戏 ini 里指向它的 DisabledAddons / LoadFromDllMain 条目就是死条目
            var deletedNames = result.DeletedFiles
                .Concat(result.DeletedStaleFiles)
                .Select(Path.GetFileName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (deletedNames.Count > 0)
            {
                AddonReferenceCleanResult clean = PurgeAddonReferences(deletedNames);
                if (clean.Changed)
                {
                    parts.Add($"顺手清了 {clean.ChangedGames.Count} 个游戏 ini 里的残留条目");
                }
            }

            TextBlock_Status.Text = $"「{item.Name}」已删除：" + string.Join("；", parts);
            await RefreshAsync();
        });
    }

    #endregion

    #region 安装流程

    /// <summary>
    /// 「清理失效条目」：扫所有游戏的 ini，把指向「插件目录里已经不存在的文件」的
    /// DisabledAddons / LoadFromDllMain 条目删掉。
    /// </summary>
    private async void Button_CleanStaleReferences_Click(object sender, RoutedEventArgs e)
    {
        if (_manager is null)
        {
            return;
        }

        await RunAsync(async () =>
        {
            GameDiscoveryService discovery = GameCatalog.CreateService();
            List<GameEntry> games = discovery.DiscoverAll(GameCatalog.KnownCandidates());

            string? addonsDirectory = _manager.Host.AddonsPath;
            AddonReferenceCleanResult result = AddonReferenceCleaner.RemoveStale(games, addonsDirectory);

            TextBlock_Status.Text = result.Changed
                ? $"清理完成：{result.GamesScanned} 个游戏里改了 {result.ChangedGames.Count} 个（{string.Join("、", result.ChangedGames)}），共摘掉 {result.RemovedEntries.Count} 条失效条目。"
                : $"清理完成：{result.GamesScanned} 个游戏都没有失效条目。";
            await Task.CompletedTask;
        });
    }

    /// <summary>
    /// 把插件文件名从所有游戏的 ReShade.ini 里摘掉（DisabledAddons + LoadFromDllMain）。
    /// </summary>
    private AddonReferenceCleanResult PurgeAddonReferences(IEnumerable<string?> fileNames)
    {
        try
        {
            List<string> names = [.. fileNames.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n!)];
            if (names.Count == 0)
            {
                return new AddonReferenceCleanResult(0, [], []);
            }

            GameDiscoveryService discovery = GameCatalog.CreateService();
            List<GameEntry> games = discovery.DiscoverAll(GameCatalog.KnownCandidates());
            AddonReferenceCleanResult result = AddonReferenceCleaner.RemoveFileNames(games, names);

            _logger.LogInformation("Purged addon references {Names}: {Games} game(s) changed",
                string.Join(", ", names), result.ChangedGames.Count);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Purge addon references");
            return new AddonReferenceCleanResult(0, [], []);
        }
    }

    private async Task RunInstallAsync(ExtensionManifest manifest, string? tagOverride = null)
    {
        if (_manager is null || _isWorking)
        {
            return;
        }

        PluginItemViewModel? item = Items.FirstOrDefault(
            i => string.Equals(i.Manifest.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));

        await RunAsync(async () =>
        {
            using var job = BeginDownloadJob($"\u6b63\u5728\u83b7\u53d6\u300c{manifest.Name}\u300d");

            var progress = new Progress<DownloadProgress>(p =>
            {
                string text = p.Percent is null
                    ? $"{p.BytesReceived / 1024d / 1024d:F1} MB"
                    : $"{p.Percent.Value:F0}%　{p.BytesReceived / 1024d / 1024d:F1} MB";
                TextBlock_Status.Text = $"正在获取「{manifest.Name}」… {text}";
                if (item is not null)
                {
                    item.ProgressPercent = p.Percent ?? 0;
                }
            });

            if (item is not null)
            {
                item.IsBusy = true;
                item.StatusText = "正在下载…";
            }

            try
            {
                ExtensionInstallResult result = await _manager.InstallAsync(manifest, progress, job.Token, tagOverride, job.Pause);

                var parts = new List<string> { $"安装 {result.InstalledFiles.Count} 个文件" };
                if (result.RemovedStaleFiles.Count > 0)
                {
                    parts.Add($"清掉同族旧文件 {result.RemovedStaleFiles.Count} 个：{string.Join("、", result.RemovedStaleFiles.Take(3))}{(result.RemovedStaleFiles.Count > 3 ? " …" : string.Empty)}");
                }
                if (result.OverwrittenFiles.Count > 0)
                {
                    parts.Add($"覆盖 {result.OverwrittenFiles.Count} 个");
                }
                if (result.ReShadeIniUpdated)
                {
                    parts.Add("补齐了 ReShade.ini 的 [ADDON] AddonPath");
                }
                if (result.BackupDirectory is not null)
                {
                    parts.Add("原文件已备份");
                }
                parts.AddRange(result.Warnings);

                TextBlock_Status.Text = $"「{manifest.Name}」安装完成：" + string.Join("；", parts);
                if (item is not null)
                {
                    // 刚装完，更新提示先撤掉（RefreshAsync 之后会按新账本重算）
                    item.UpdateTag = null;
                }
            }
            finally
            {
                if (item is not null)
                if (job.IsCancellationRequested)
                {
                    TextBlock_Status.Text = $"\u5df2\u53d6\u6d88\u5b89\u88c5\u300c{manifest.Name}\u300d\u3002";
                }

                {
                    item.IsBusy = false;

                Button_CancelDownload.IsEnabled = true;
                EndDownloadJob();
                }
            }

            await ReloadPluginDataAsync();
        });
    }

    private static ExtensionManifest? ReadManifestFromPackage(string file)
    {
        using var archive = ZipFile.OpenRead(file);
        var entry = archive.Entries.FirstOrDefault(
            e => string.Equals(Path.GetFileName(e.FullName), "manifest.json", StringComparison.OrdinalIgnoreCase)
                 || string.Equals(Path.GetFileName(e.FullName), "hysx.json", StringComparison.OrdinalIgnoreCase));

        if (entry is null)
        {
            return null;
        }

        using Stream stream = entry.Open();
        return JsonSerializer.Deserialize<ExtensionManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });
    }

    /// <summary>
    /// 库里只有一个 OptiScaler 构建时，给还没选过构建的游戏默认选中它并启用。
    /// 下载完成（远端 / 本地安装）后调用；已经有多个构建或用户已选过的不动。
    /// </summary>
    /// <returns>这次被默认启用的游戏数量</returns>
    private static int AutoEnableSoleOptiScalerBuild()
    {
        string root = AppConfig.OptiScalerRootPath;
        if (root.Length == 0)
        {
            return 0;
        }

        var library = new OptiScalerLibrary(root);
        List<OptiScalerBuild> builds = library.List();
        if (builds.Count != 1)
        {
            return 0;
        }

        OptiScalerBuild sole = builds[0];
        if (string.IsNullOrWhiteSpace(sole.DllPath))
        {
            return 0;
        }

        int count = 0;

        foreach (GameBiz biz in GameBiz.AllGameBizs)
        {
            if (GameId.FromGameBiz(biz) is not { } gameId)
            {
                continue;
            }

            // 用户已经给这个游戏选过（含手动清空为 null 无法区分，罕见情况一并视为已决定）
            if (AppConfig.GetSelectedOptiScalerId(gameId) is not null)
            {
                continue;
            }

            AppConfig.SetSelectedOptiScalerId(gameId, sole.Id);
            AppConfig.SetUseOptiScalerLaunchOption(gameId, true);
            count++;
        }

        if (count > 0)
        {
            try
            {
                library.Select(sole.Id);
            }
            catch
            {
                // state.json 同步失败不影响每游戏选择
            }
        }

        return count;
    }

    /// <summary>串行化操作 + 统一收尾</summary>
    #region 下载任务（取消 / 暂停）

    /// <summary>
    /// Starts a cancellable download task and shows the pause / cancel buttons. 
    /// The returned job owns a CancellationTokenSource -- always dispose it, otherwise the
    /// token source leaks and a stale cancel button handler stays wired up.
    /// </summary>
    private DownloadJob BeginDownloadJob(string label)
    {
        // 上一单还没收尾就先收掉，避免两个任务抢同一组按钮
        EndDownloadJob();

        var job = DownloadJob.Start(ProgressBar_Overall, TextBlock_Status, Button_CancelDownload, label);
        job.PauseStateChanged += OnDownloadPauseStateChanged;
        Button_PauseDownload.Content = "\u6682\u505c";
        Button_PauseDownload.Visibility = Visibility.Visible;
        Button_CancelDownload.Visibility = Visibility.Visible;
        _downloadJob = job;
        return job;
    }

    /// <summary>Hides the download controls. Does not dispose -- callers own the job.</summary>
    private void EndDownloadJob()
    {
        if (_downloadJob is null)
        {
            return;
        }

        Button_PauseDownload.Visibility = Visibility.Collapsed;
        Button_CancelDownload.Visibility = Visibility.Collapsed;
        Button_PauseDownload.Content = "\u6682\u505c";
        _downloadJob = null;
    }

    private void OnDownloadPauseStateChanged(object? sender, EventArgs e)
    {
        if (_downloadJob is null)
        {
            return;
        }

        Button_PauseDownload.Content = _downloadJob.IsPaused ? "\u7ee7\u7eed" : "\u6682\u505c";
    }

    private void Button_PauseDownload_Click(object sender, RoutedEventArgs e)
    {
        _downloadJob?.TogglePause();
    }

    private void Button_CancelDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadJob is null)
        {
            return;
        }

        _downloadJob.Cancel();
        Button_CancelDownload.IsEnabled = false;
    }

    /// <summary>True when the user pressed cancel on the running download.</summary>
    private bool IsDownloadCancelled => _downloadJob?.IsCancellationRequested == true;

    #endregion

    private async Task RunAsync(Func<Task> action)
    {
        if (_isWorking)
        {
            return;
        }

        _isWorking = true;
        ProgressBar_Overall.IsIndeterminate = true;
        ProgressBar_Overall.Visibility = Visibility.Visible;

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Plugin operation failed");
            TextBlock_Status.Text = "操作失败：" + ex.Message;
            ShowInfo("操作失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _isWorking = false;
            ProgressBar_Overall.IsIndeterminate = false;
            ProgressBar_Overall.Visibility = Visibility.Collapsed;
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


/// <summary>列表里的一行（一个可安装的插件扩展包）：「当前」的已装卡片和「可下载」的紧凑行都用它。</summary>
public partial class PluginItemViewModel : ObservableObject
{
    /// <summary>版本下拉里给「当前装着的版本」加的后缀</summary>
    public const string CurrentSuffix = " (当前)";

    private readonly Action<PluginItemViewModel, bool>? _onGlobalToggle;
    private bool _suppressGlobal;

    public PluginItemViewModel(
        ExtensionStatus status,
        IReadOnlyList<string>? globalAddonFiles = null,
        string? installedAddonVersion = null,
        bool foundOnDiskOnly = false,
        AddonDllStatus? dllStatus = null,
        Action<PluginItemViewModel, bool>? onGlobalToggle = null)
    {
        _onGlobalToggle = onGlobalToggle;
        GlobalAddonFiles = globalAddonFiles ?? [];
        Manifest = status.Manifest;
        Installed = status.Installed;
        FoundOnDiskOnly = foundOnDiskOnly;
        DllSeverity = dllStatus?.Severity ?? 0;
        DllStatusText = dllStatus?.Summary ?? string.Empty;
        Name = status.Manifest.Name;
        Description = status.Manifest.Description ?? string.Empty;

        // 版本要显示「盘上这个文件是哪个版本」——
        // 目录里写的基本都是 "latest"，光看它等于没版本
        string installedVersion = installedAddonVersion
                                  ?? status.Installed?.ResolvedTag
                                  ?? status.Installed?.Version
                                  ?? string.Empty;
        CurrentVersion = installedVersion;
        VersionText = BuildVersionText(status, installedVersion);
        // 盘上有文件（哪怕不是 Hub 装的）就算「已装」—— 装按钮变「重装」，卡片也才有全局开关
        IsInstalled = status.IsInstalled || foundOnDiskOnly;
        ActionText = IsInstalled ? "重装" : "安装";
        StatusText = BuildStatusText(status);
        Tags = [.. status.Manifest.Tags ?? []];
        Homepage = status.Manifest.Homepage ?? string.Empty;
        FilesText = BuildFilesText(status.Installed);

        SetGlobalEnabledSilently(AreEnabled(GlobalAddonFiles));
    }

    /// <summary>盘上当前装着的版本（版本下拉默认选它）</summary>
    public string CurrentVersion { get; }

    /// <summary>列表里铺进下拉的版本（GitHub tag，新 → 旧）—— 展开卡片时才去拉。</summary>
    public ObservableCollection<string> Versions { get; } = [];

    private readonly Dictionary<string, string> _versionTags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 铺进下拉。显示文本带上发布日期：GitHub 自己的列表是按 release 的创建时间排的，
    /// 光看 tag 顺序会觉得「乱」，把时间摆出来就一眼能看明白为什么是这个顺序；
    /// 当前装着的那个再带「(当前)」后缀并默认选中。
    /// </summary>
    public void ApplyVersions(IReadOnlyList<ExtensionVersion> versions)
    {
        Versions.Clear();
        _versionTags.Clear();

        foreach (ExtensionVersion version in versions)
        {
            string display = DisplayOf(version);
            _versionTags[display] = version.Tag;
            Versions.Add(display);
        }

        string? match = !string.IsNullOrWhiteSpace(CurrentVersion)
            ? Versions.FirstOrDefault(v => string.Equals(TagOf(v), CurrentVersion, StringComparison.OrdinalIgnoreCase))
            : null;
        SelectedVersion = match ?? Versions.FirstOrDefault();
    }

    /// <summary>下拉里显示的文本：tag · 发布日期，当前版本再带「(当前)」</summary>
    private string DisplayOf(ExtensionVersion version)
    {
        string text = version.Published is { } published
            ? $"{version.Tag}  ·  {published.ToLocalTime():yyyy-MM-dd}"
            : version.Tag;

        return string.Equals(version.Tag, CurrentVersion, StringComparison.OrdinalIgnoreCase)
            ? text + CurrentSuffix
            : text;
    }

    /// <summary>下拉里选的文本还原成 tag</summary>
    public string? TagOf(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
        {
            return null;
        }

        return _versionTags.TryGetValue(display, out string? tag)
            ? tag
            : display.Replace(CurrentSuffix, string.Empty).Trim();
    }

    [ObservableProperty]
    private string? selectedVersion;

    /// <summary>版本列表拉过了没有</summary>
    [ObservableProperty]
    private bool versionsLoaded;

    [ObservableProperty]
    private string versionsHint = string.Empty;

    /// <summary>GitHub 上有更新的 tag（null = 没查 / 已是最新）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateBadgeVisibility))]
    [NotifyPropertyChangedFor(nameof(UpdateBadgeText))]
    private string? updateTag;

    public Visibility UpdateBadgeVisibility => UpdateTag is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

    public string UpdateBadgeText => UpdateTag is { Length: > 0 } ? $"有新版本 {UpdateTag}" : string.Empty;

    /// <summary>这个扩展装过的 addon 文件（全局开关动它们，后缀改名）</summary>
    public IReadOnlyList<string> GlobalAddonFiles { get; }

    /// <summary>文件在盘上，但账本里没有（不是 Hub 装的，用户自己从 Discord / GitHub 拿的）</summary>
    public bool FoundOnDiskOnly { get; }

    public Visibility DiskOnlyHintVisibility =>
        FoundOnDiskOnly ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>0 = 没问题，1 = 缺建议（黄），2 = 缺必需（红）</summary>
    public int DllSeverity { get; }

    public string DllStatusText { get; }

    public Visibility DllRequiredVisibility => DllSeverity >= 2 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DllRecommendedVisibility => DllSeverity == 1 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>装了 addon 才有全局开关</summary>
    public Visibility GlobalSwitchVisibility => GlobalAddonFiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private bool globalEnabled;

    partial void OnGlobalEnabledChanged(bool value)
    {
        if (_suppressGlobal)
        {
            return;
        }

        _onGlobalToggle?.Invoke(this, value);
    }

    /// <summary>写盘失败时拨回去</summary>
    public void RevertGlobalEnabled() => SetGlobalEnabledSilently(!GlobalEnabled);

    public void SetGlobalEnabledSilently(bool value)
    {
        _suppressGlobal = true;
        GlobalEnabled = value;
        _suppressGlobal = false;
    }

    /// <summary>重新按盘上的状态刷新开关（别的不相关操作改了文件时用）</summary>
    public void RefreshGlobalEnabled() => SetGlobalEnabledSilently(AreEnabled(GlobalAddonFiles));

    private static bool AreEnabled(IReadOnlyList<string> files)
    {
        // 只要还有没被改名禁用的，就算「开着」
        foreach (string file in files)
        {
            if (!AddonFileSwitcher.IsDisabledName(Path.GetFileName(file)))
            {
                return true;
            }
        }

        return false;
    }

    public ExtensionManifest Manifest { get; }

    public InstalledExtension? Installed { get; }

    /// <summary>目录里写的标签（hdr / tonemap / miHoYo…），显示在标题右边</summary>
    public ObservableCollection<string> Tags { get; }

    public string Homepage { get; }

    public string FilesText { get; }

    [ObservableProperty]
    private string name = string.Empty;

    [ObservableProperty]
    private string description = string.Empty;

    [ObservableProperty]
    private string versionText = string.Empty;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private string actionText = "安装";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandedVisibility))]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool isExpanded;

    [ObservableProperty]
    private bool isInstalled;

    /// <summary>收起时标题行右边的摘要</summary>
    public string SummaryText => IsInstalled
        ? (string.IsNullOrWhiteSpace(CurrentVersion) ? "已安装" : $"已装 {CurrentVersion}")
        : (IsRealVersion(Manifest.Version) ? $"版本 {Manifest.Version}" : "可下载");

    /// <summary>展开箭头：收起朝下，展开朝上</summary>
    public string ExpandGlyph => IsExpanded ? "\uE70E" : "\uE70D";

    /// <summary>展开时给一句解释：文件是盘上捡到的，Hub 没账本</summary>
    public string DiskOnlyHint =>
        "addons 目录里有这个插件的文件，但账本里没有记录 —— 应该是你自己从 Discord / GitHub 放的。" +
        "点「重装」可以让 Hub 接管（覆盖前会先备份原文件）。";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyVisibility))]
    [NotifyPropertyChangedFor(nameof(CanInteract))]
    private bool isBusy;

    [ObservableProperty]
    private double progressPercent;

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public Visibility InstalledVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>版本徽章：装着的版本直接显示在未展开的卡片上。</summary>
    public string VersionBadgeText => CurrentVersion;

    public Visibility VersionBadgeVisibility =>
        !string.IsNullOrWhiteSpace(CurrentVersion) ? Visibility.Visible : Visibility.Collapsed;

    public Visibility ExpandedVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    public Visibility HomepageVisibility => string.IsNullOrWhiteSpace(Homepage) ? Visibility.Collapsed : Visibility.Visible;

    public bool CanInteract => !IsBusy;

    private static string BuildStatusText(ExtensionStatus status)
    {
        var parts = new List<string>();

        if (status.Installed is not null)
        {
            parts.Add($"安装于 {status.Installed.InstalledAt.LocalDateTime:yyyy-MM-dd HH:mm}");
            if (!string.IsNullOrWhiteSpace(status.Installed.ResolvedTag))
            {
                parts.Add(status.Installed.ResolvedTag!);
            }
        }

        if (!string.IsNullOrWhiteSpace(status.Manifest.Author))
        {
            parts.Add("作者：" + status.Manifest.Author);
        }

        return string.Join(" · ", parts);
    }

    /// <summary>目录里那套 version 基本都是占位的（latest / main），别显示成版本号</summary>
    private static bool IsRealVersion(string? version) =>
        !string.IsNullOrWhiteSpace(version)
        && version is not ("latest" or "main" or "nightly" or "master");

    private static string BuildVersionText(ExtensionStatus status, string installedVersion)
    {
        var parts = new List<string>();

        if (status.IsInstalled)
        {
            parts.Add(string.IsNullOrWhiteSpace(installedVersion) ? "已安装" : $"已装 {installedVersion}");
            if (IsRealVersion(status.Manifest.Version))
            {
                parts.Add($"目录 {status.Manifest.Version}");
            }
        }
        else if (IsRealVersion(status.Manifest.Version))
        {
            parts.Add($"版本 {status.Manifest.Version}");
        }
        else
        {
            parts.Add("未安装 · 装的时候从 GitHub release 取最新");
        }

        return string.Join(" · ", parts);
    }

    private static string BuildFilesText(InstalledExtension? installed)
    {
        if (installed is null || installed.Files.Count == 0)
        {
            return string.Empty;
        }

        string files = string.Join(Environment.NewLine,
            installed.Files.Take(30).Select(f => $"· {f.Path}  ({f.Size / 1024d:F1} KB)"));

        return $"文件清单（{installed.Files.Count}）：{Environment.NewLine}{files}";
    }
}

/// <summary>插件目录里没匹配到目录条目的插件文件（一行一个，保留逐个改名开关）。</summary>
public partial class AddonFileItemViewModel : ObservableObject
{
    private readonly Action<AddonFileItemViewModel, bool>? _onToggle;
    private readonly string? _version;
    private readonly string? _branch;

    private bool _suppress;

    public AddonFileItemViewModel(
        AddonFileInfo info,
        string directory,
        AddonNameCache cache,
        IReadOnlyList<string> candidateNames,
        AddonDllStatus? dllStatus = null,
        Action<AddonFileItemViewModel, bool>? onToggle = null)
    {
        _onToggle = onToggle;
        _branch = info.Branch;
        DllSeverity = dllStatus?.Severity ?? 0;
        DllStatusText = dllStatus?.Summary ?? string.Empty;

        Name = AddonNameResolver.Resolve(
            Path.Combine(directory, info.FileName),
            info.FileName,
            learnedName: cache.Get(info.FileName),
            knownName: AddonNameResolver.GetKnownInternalName(info.Slug),
            candidateNames: candidateNames,
            fallback: info.Slug);

        Slug = info.Slug;
        Directory = directory;
        FileName = info.FileName;
        GloballyDisabled = info.IsRenamedDisabled;

        // 名字里没版本就读 PE（dlss5-bridge 就是这样：文件名没版本，PE 里是 1.4.13-pre7）
        _version = AddonVersionResolver.Resolve(Path.Combine(directory, info.FileName), info.Version);

        _suppress = true;
        Enabled = !GloballyDisabled;
        _suppress = false;
    }

    public string Name { get; }

    public string Slug { get; }

    public string Directory { get; }

    public string? Version => _version;

    /// <summary>0 = 没问题，1 = 缺建议（黄），2 = 缺必需（红）</summary>
    public int DllSeverity { get; }

    public string DllStatusText { get; }

    public Visibility DllRequiredVisibility => DllSeverity >= 2 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility DllRecommendedVisibility => DllSeverity == 1 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>当前文件名（改名之后会变）</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullPath))]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    [NotifyPropertyChangedFor(nameof(DisabledVisibility))]
    private string fileName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MetaText))]
    [NotifyPropertyChangedFor(nameof(DisabledVisibility))]
    private bool globallyDisabled;

    public string FullPath => Path.Combine(Directory, FileName);

    public string MetaText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(_version))
            {
                parts.Add($"版本 {_version}");
            }
            if (!string.IsNullOrWhiteSpace(_branch))
            {
                parts.Add(_branch!);
            }
            parts.Add(GloballyDisabled ? "已重命名禁用（.addon64x）" : "已加载");
            parts.Add(FileName);
            return string.Join(" · ", parts);
        }
    }

    public Visibility DisabledVisibility => GloballyDisabled ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>汉化过（有备份）才显示「还原」</summary>
    public Visibility RestoreVisibility
        => AddonLocalizer.BackupPathOf(FullPath, GlobalPluginPage.I18nBackupDirectory) is not null
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>全局开/关（= 文件名后缀有没有那个 x）</summary>
    [ObservableProperty]
    private bool enabled;

    partial void OnEnabledChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        _onToggle?.Invoke(this, value);
    }

    /// <summary>写盘失败时拨回去</summary>
    public void Revert()
    {
        _suppress = true;
        Enabled = !Enabled;
        _suppress = false;
    }

    /// <summary>改名成功：把这一行的显示也更新掉</summary>
    public void MarkToggled(bool enabled, string newFileName)
    {
        _suppress = true;
        Enabled = enabled;
        _suppress = false;

        FileName = newFileName;
        GloballyDisabled = !enabled;
    }
}

/// <summary>
/// OptiScaler 那一栏「可下载的来源」和「当前里的构建」共用的一套换版本能力：
/// 头行点开、下拉列版本、选一个下载 / 切换。
/// </summary>
public interface IOptiScalerVersionHost
{
    OptiScalerSource Source { get; }

    ObservableCollection<string> Versions { get; }

    string? SelectedVersion { get; }

    string StatusText { get; set; }

    bool CanInteract { get; set; }

    bool IsExpanded { get; set; }

    /// <summary>来源仓库可用（能列版本）</summary>
    bool HasVersionsSource { get; }

    void ApplyVersions(IReadOnlyList<ExtensionVersion> versions);

    string? TagOf(string? display);
}

/// <summary>「OptiScaler」那一栏「当前」里的一行：一个已经下载到本地的构建。</summary>
public sealed partial class OptiScalerBuildItemViewModel : ObservableObject, IOptiScalerVersionHost
{
    private readonly Dictionary<string, string> _versionTags = new(StringComparer.OrdinalIgnoreCase);

    public OptiScalerBuildItemViewModel(OptiScalerBuild build, string? selectedId, OptiScalerSource? source)
    {
        Build = build;
        Enabled = string.Equals(build.Id, selectedId, StringComparison.OrdinalIgnoreCase);
        Source = source ?? new OptiScalerSource
        {
            Id = build.SourceId,
            Name = build.SourceId,
            Repository = string.Empty,
            Description = string.Empty,
        };
        CurrentVersion = build.Version;
        IsGloballyEnabled = AppConfig.IsOptiScalerBuildEnabled(build.Id);
    }

    public OptiScalerBuild Build { get; }

    public OptiScalerSource Source { get; }

    public string Id => Build.Id;

    /// <summary>来源名字：内置表里找得到就用那个（更完整），否则退回本地记的 id</summary>
    public string SourceName => OptiScalerCatalog.Find(Build.SourceId)?.Name ?? Source.Name;

    public string Title => $"{Build.Version}　·　{SourceName}";

    /// <summary>来源带的标签（跟插件卡片的 tags 同一套）</summary>
    public IReadOnlyList<string> Tags => Source.Tags is { Length: > 0 } tags ? tags : Array.Empty<string>();

    public string TagsText => Tags.Count > 0 ? string.Join(" ", Tags) : string.Empty;

    public string Repository => Source.Repository;

    public Visibility RepoVisibility => string.IsNullOrWhiteSpace(Repository) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>资产名 · 大小 · dll · 目录</summary>
    public string MetaText
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(Build.AssetName))
            {
                parts.Add(Build.AssetName!);
            }

            parts.Add($"{Build.SizeBytes / 1024d / 1024d:F1} MB");
            parts.Add(InstallerOnly
                ? "安装程序（装到游戏目录，不参与注入）"
                : Build.DllPath is null ? "包里没找到 OptiScaler.dll" : Path.GetFileName(Build.DllPath));
            parts.Add(Build.Directory);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>这个构建是来源自己的安装程序（目录里没有 OptiScaler.dll，靠它自己铺到游戏目录）</summary>
    public bool InstallerOnly => Build.DllPath is null
                                 && Build.AssetName?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true;

    public Visibility EnabledVisibility => Enabled ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoDllVisibility => Build.DllPath is null && !InstallerOnly ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>构建目录里有没有神经渲染运行时（各 OptiScaler 分支的手册都要求放在包旁边）</summary>
    public bool HasNrdll => OptiScalerRuntime.HasNrdll(Build.Directory);

    /// <summary>有可注入的 dll、但缺 nvngx_dlssnr.dll → 黄字提示 + 「放入」按钮</summary>
    public Visibility MissingNrdllVisibility
        => Build.DllPath is not null && !HasNrdll ? Visibility.Visible : Visibility.Collapsed;

    public bool CanOpenFolder => Directory.Exists(Build.Directory);

    /// <summary>只读：true = 当前使用的那个构建（状态由左侧新的 OptiScaler 页面写下）</summary>
    public bool Enabled { get; }

    /// <summary>这个构建装的是哪个 tag（下拉默认选它）</summary>
    public string CurrentVersion { get; }

    public ObservableCollection<string> Versions { get; } = [];

    public bool HasVersionsSource => !string.IsNullOrWhiteSpace(Source.Repository) && !string.IsNullOrWhiteSpace(Source.Id);

    public Visibility VersionSwitchVisibility => HasVersionsSource ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>收起时标题行右边的摘要（资产名，没有就版本号）</summary>
    public string SummaryText => string.IsNullOrWhiteSpace(Build.AssetName) ? Build.Version : Build.AssetName!;

    /// <summary>版本徽章：直接显示在未展开卡片上。</summary>
    public string VersionBadgeText => Build.Version;

    public Visibility VersionBadgeVisibility =>
        !string.IsNullOrWhiteSpace(Build.Version) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>展开箭头：收起朝下，展开朝上</summary>
    public string ExpandGlyph => IsExpanded ? "\uE70E" : "\uE70D";

    public Visibility ExpandedVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private string? selectedVersion;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private bool canInteract = true;

    /// <summary>这个构建自己的全局开关（AppConfig 里按构建 id 记；关掉后哪个游戏都不注入）</summary>
    [ObservableProperty]
    private bool isGloballyEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandedVisibility))]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool isExpanded;

    /// <summary>选中的是不是当前这个构建：是 = 重装，不是 = 切到这个版本</summary>
    public string ActionText
    {
        get
        {
            string? tag = TagOf(SelectedVersion);
            if (string.IsNullOrWhiteSpace(tag))
            {
                return "下载并安装";
            }

            return string.Equals(tag, Build.Version, StringComparison.OrdinalIgnoreCase)
                ? "重装这个版本"
                : "切换到这个版本";
        }
    }

    /// <summary>铺进下拉，并默认选中当前这个构建的版本</summary>
    public void ApplyVersions(IReadOnlyList<ExtensionVersion> versions)
    {
        Versions.Clear();
        _versionTags.Clear();

        foreach (ExtensionVersion version in versions)
        {
            string display = DisplayOf(version);
            _versionTags[display] = version.Tag;
            Versions.Add(display);
        }

        string? match = Versions.FirstOrDefault(v => string.Equals(TagOf(v), CurrentVersion, StringComparison.OrdinalIgnoreCase));
        SelectedVersion = match ?? Versions.FirstOrDefault();
    }

    private string DisplayOf(ExtensionVersion version)
    {
        string text = version.Published is { } published
            ? $"{version.Tag}  ·  {published.ToLocalTime():yyyy-MM-dd}"
            : version.Tag;

        return string.Equals(version.Tag, CurrentVersion, StringComparison.OrdinalIgnoreCase)
            ? text + OptiScalerSourceItemViewModel.CurrentSuffix
            : text;
    }

    public string? TagOf(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
        {
            return null;
        }

        return _versionTags.TryGetValue(display, out string? tag)
            ? tag
            : display.Replace(OptiScalerSourceItemViewModel.CurrentSuffix, string.Empty).Trim();
    }
}

/// <summary>「OptiScaler」那一栏「可下载」里的一行：一个还没装过的来源（GitHub 仓库）。</summary>
public sealed partial class OptiScalerSourceItemViewModel : ObservableObject, IOptiScalerVersionHost
{
    /// <summary>下拉里给「当前版本」加的后缀</summary>
    public const string CurrentSuffix = " (当前)";

    public OptiScalerSourceItemViewModel(OptiScalerSource source)
    {
        Source = source;
    }

    public OptiScalerSource Source { get; }

    public string Name => Source.Name;

    public string Description => Source.Description;

    public string Repository => Source.Repository;

    /// <summary>来源带的标签（跟插件卡片的 tags 同一套）</summary>
    public IReadOnlyList<string> Tags => Source.Tags is { Length: > 0 } tags ? tags : Array.Empty<string>();

    public string TagsText => Tags.Count > 0 ? string.Join(" ", Tags) : string.Empty;

    public string Homepage => Source.Homepage ?? string.Empty;

    public Visibility HomepageVisibility => string.IsNullOrWhiteSpace(Homepage) ? Visibility.Collapsed : Visibility.Visible;

    public bool HasVersionsSource => !string.IsNullOrWhiteSpace(Source.Repository);

    /// <summary>这个来源能装的版本（下拉里显示的文本，当前版本带后缀）</summary>
    public ObservableCollection<string> Versions { get; } = [];

    private readonly Dictionary<string, string> _versionTags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这个来源已经装过的版本 tag</summary>
    public HashSet<string> InstalledVersions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>「当前版本」：当前启用的那个，或这个来源最近装的那个</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SummaryText))]
    private string? currentVersion;

    /// <summary>「可下载」的卡片默认收起，点标题行才展开</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandedVisibility))]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool isExpanded;

    public Visibility ExpandedVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>展开箭头：收起朝下，展开朝上</summary>
    public string ExpandGlyph => IsExpanded ? "\uE70E" : "\uE70D";

    /// <summary>收起时标题行右边的状态</summary>
    public string SummaryText => CurrentVersion is { Length: > 0 } version
        ? $"当前 {version}"
        : InstalledVersions.Count > 0 ? "已下载" : "可下载";

    /// <summary>把版本列表铺进下拉，并默认选中当前版本（没装过就选最新的）</summary>
    public void ApplyVersions(IReadOnlyList<ExtensionVersion> versions)
    {
        Versions.Clear();
        _versionTags.Clear();

        foreach (ExtensionVersion version in versions)
        {
            string display = DisplayOf(version);
            _versionTags[display] = version.Tag;
            Versions.Add(display);
        }

        string? match = CurrentVersion is { Length: > 0 } current
            ? Versions.FirstOrDefault(v => string.Equals(
                _versionTags.TryGetValue(v, out string? tag) ? tag : v,
                current,
                StringComparison.OrdinalIgnoreCase))
            : null;
        SelectedVersion = match ?? Versions.FirstOrDefault();
    }

    /// <summary>下拉里显示的文本：tag · 发布日期，当前版本再带「(当前)」</summary>
    private string DisplayOf(ExtensionVersion version)
    {
        string text = version.Published is { } published
            ? $"{version.Tag}  ·  {published.ToLocalTime():yyyy-MM-dd}"
            : version.Tag;

        return string.Equals(version.Tag, CurrentVersion, StringComparison.OrdinalIgnoreCase)
            ? text + CurrentSuffix
            : text;
    }

    /// <summary>从下拉文本还原出版本 tag</summary>
    public string? TagOf(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
        {
            return string.Empty;
        }

        return _versionTags.TryGetValue(display, out string? tag)
            ? tag
            : display.Replace(CurrentSuffix, string.Empty).Trim();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    private string? selectedVersion;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private bool canInteract = true;

    /// <summary>这个构建自己的全局开关（AppConfig 里按构建 id 记；关掉后哪个游戏都不注入）</summary>
    [ObservableProperty]
    private bool isGloballyEnabled;

    /// <summary>选中的版本已经装过 → 「更新到此版本」，否则「下载并安装」</summary>
    public string ActionText
        => InstalledVersions.Contains(TagOf(SelectedVersion) ?? string.Empty) ? "更新到此版本" : "下载并安装";
}

/// <summary>「模块」那一栏「当前」里的一行：一个装好了的模块（内置模块，或用户手动加的 DLL）。</summary>
public sealed partial class ModuleItemViewModel : ObservableObject
{
    private readonly Action<ModuleItemViewModel, bool>? _onToggle;
    private bool _suppress;

    public ModuleItemViewModel(ModuleEntry entry, Action<ModuleItemViewModel, bool>? onToggle = null)
    {
        _onToggle = onToggle;
        Key = entry.Key;
        Name = entry.Name;
        Description = entry.Description;
        Tags = entry.Tags is { Length: > 0 } tags ? tags : Array.Empty<string>();
        Homepage = entry.Homepage ?? string.Empty;
        DllPath = entry.DllPath ?? string.Empty;
        IsBuiltin = entry.IsBuiltin;
        Version = HasDll ? ReadDllVersion(DllPath) : null;

        _suppress = true;
        GloballyEnabled = entry.GloballyEnabled;
        _suppress = false;
    }

    /// <summary>模块 DLL 的 PE 版本。</summary>
    public string? Version { get; }

    public string VersionBadgeText => Version ?? string.Empty;

    public Visibility VersionBadgeVisibility =>
        !string.IsNullOrWhiteSpace(Version) ? Visibility.Visible : Visibility.Collapsed;

    private static string? ReadDllVersion(string path)
    {
        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion is { } text
                   && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>内置模块 = 模块 id；手动加的 = DLL 全路径</summary>
    public string Key { get; }

    public string Name { get; }

    public string Description { get; }

    public IReadOnlyList<string> Tags { get; }

    public string TagsText => Tags.Count > 0 ? string.Join(" ", Tags) : string.Empty;

    public string Homepage { get; }

    public string DllPath { get; }

    public bool IsBuiltin { get; }

    public string KindText => IsBuiltin ? "内置" : "手动";

    public bool HasDll => !string.IsNullOrWhiteSpace(DllPath);

    public Visibility MissingDllVisibility => HasDll ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>已安装模块（内置/远端目录/手动）都给删：真正删文件</summary>
    public Visibility DeleteVisibility => Visibility.Visible;

    public Visibility HomepageVisibility => string.IsNullOrWhiteSpace(Homepage) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>收起时标题行右边的摘要</summary>
    public string SummaryText => HasDll ? Path.GetFileName(DllPath) : "文件不见了";

    public string DetailText => HasDll
        ? $"注入：{DllPath}"
        : "记录里的文件已经不在了 —— 可以删掉这条，或者重新「添加 DLL…」。";

    /// <summary>展开箭头：收起朝下，展开朝上</summary>
    public string ExpandGlyph => IsExpanded ? "\uE70E" : "\uE70D";

    public Visibility ExpandedVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    private bool globallyEnabled;

    partial void OnGloballyEnabledChanged(bool value)
    {
        if (_suppress)
        {
            return;
        }

        _onToggle?.Invoke(this, value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandedVisibility))]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool isExpanded;
}

/// <summary>「模块」那一栏「可下载」里的一行：目录里一个还没装的模块。</summary>
public sealed partial class ModuleDownloadItemViewModel : ObservableObject
{
    public ModuleDownloadItemViewModel(ModuleDefinition module)
    {
        Module = module;
    }

    public ModuleDefinition Module { get; }

    public string Name => Module.Name;

    public string Description => Module.Description;

    public IReadOnlyList<string> Tags => Module.Tags is { Length: > 0 } tags ? tags : Array.Empty<string>();

    public string TagsText => Tags.Count > 0 ? string.Join(" ", Tags) : string.Empty;

    public string DetailText => $"注入目标：{Module.DllHint}　·　目录：{Module.Directory}";

    /// <summary>主页链接（没有主页就退回仓库地址）</summary>
    public string LinkText => !string.IsNullOrWhiteSpace(Module.Homepage)
        ? Module.Homepage!
        : "https://github.com/" + Module.Repository;

    /// <summary>展开箭头：收起朝下，展开朝上</summary>
    public string ExpandGlyph => IsExpanded ? "\uE70E" : "\uE70D";

    public Visibility ExpandedVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandedVisibility))]
    [NotifyPropertyChangedFor(nameof(ExpandGlyph))]
    private bool isExpanded;

    [ObservableProperty]
    private string statusText = string.Empty;

    [ObservableProperty]
    private bool canInteract = true;

    /// <summary>版本下拉里的显示文本（tag · 发布日期）</summary>
    public ObservableCollection<string> Versions { get; } = [];

    private readonly Dictionary<string, string> _versionTags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>版本列表拉过了没有</summary>
    public bool VersionsLoaded { get; set; }

    public string? TagOf(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
        {
            return null;
        }

        return _versionTags.TryGetValue(display, out string? tag) ? tag : display.Trim();
    }

    /// <summary>把版本列表铺进下拉，默认选中最新（第一个）</summary>
    public void ApplyVersions(IReadOnlyList<ExtensionVersion> versions)
    {
        VersionsLoaded = true;
        Versions.Clear();
        _versionTags.Clear();

        foreach (ExtensionVersion version in versions)
        {
            string display = version.Published is { } published
                ? $"{version.Tag}  ·  {published.ToLocalTime():yyyy-MM-dd}"
                : version.Tag;
            _versionTags[display] = version.Tag;
            Versions.Add(display);
        }

        SelectedVersion = Versions.FirstOrDefault();
    }

    [ObservableProperty]
    private string? selectedVersion;
}
