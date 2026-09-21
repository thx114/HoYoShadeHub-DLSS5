using CommunityToolkit.Mvvm.ComponentModel;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.I18n;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
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

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// **全局**插件页（左下角，设置上方）。
///
/// 管的是全局那一份 HoYoShade：&lt;用户数据目录&gt;\HoYoShade —— 所有游戏共用同一套 addon。
/// 两个视图：
/// <list type="bullet">
/// <item>「插件」：装 / 重装 / 删除（只列**会装 addon 的**扩展包 —— 滤镜/预设那类先不进这个页面）；</item>
/// <item>「插件文件」：**全局**开关 —— 重命名 <c>.addon64 ↔ .addon64x</c>，影响所有游戏（会明确警告）。
/// 按游戏的开关在「插件」页（<see cref="GamePluginPage"/>），写的是各游戏自己的 ReShade.ini。</item>
/// </list>
///
/// 卡片默认只占一行，点一下才展开描述 / 状态 / 文件清单。
/// </summary>
public sealed partial class GlobalPluginPage : PageBase
{
    private readonly ILogger<GlobalPluginPage> _logger = AppConfig.GetLogger<GlobalPluginPage>();

    private ExtensionManagerService? _manager;

    private bool _isWorking;

    /// <summary>这次进页面已经查过更新了没有（别每次刷新都打一遍网络）</summary>
    private bool _updatesChecked;

    private string _filter = "all";
    private string _search = string.Empty;

    public GlobalPluginPage()
    {
        InitializeComponent();
    }

    /// <summary>全部条目（过滤前的）</summary>
    public ObservableCollection<PluginItemViewModel> Items { get; } = [];

    /// <summary>当前显示的条目（过滤 + 搜索之后）</summary>
    public ObservableCollection<PluginItemViewModel> VisibleItems { get; } = [];

    /// <summary>插件文件（全局开关那一栏）</summary>
    public ObservableCollection<AddonFileItemViewModel> AddonFiles { get; } = [];

    /// <summary>OptiScaler 那一栏：已下载的构建（同一时间只有一个「启用中」）</summary>
    public ObservableCollection<OptiScalerBuildItemViewModel> OptiScalerBuilds { get; } = [];

    /// <summary>OptiScaler 那一栏：可下载的来源（3 个社区分支）</summary>
    public ObservableCollection<OptiScalerSourceItemViewModel> OptiScalerSources { get; } = [];

    protected override void OnLoaded()
    {
        PluginList.ItemsSource = VisibleItems;
        AddonFileList.ItemsSource = AddonFiles;
        OptiScalerBuildList.ItemsSource = OptiScalerBuilds;
        OptiScalerSourceList.ItemsSource = OptiScalerSources;
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

        await RefreshAsync();
    }

    protected override void OnUnloaded()
    {
        Items.Clear();
        VisibleItems.Clear();
        AddonFiles.Clear();
        OptiScalerBuilds.Clear();
        OptiScalerSources.Clear();
    }

    #region 刷新

    /// <summary>
    /// 挨个查已装插件在 GitHub 上有没有新版本（用户要求「没有检测插件更新的功能」）。
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
        TextBlock_Empty.Visibility = Visibility.Collapsed;
        TextBlock_Status.Text = "正在读取插件目录…";

        try
        {
            _manager = PluginHostLocator.ResolveManager(out string reason);

            if (_manager is null)
            {
                Items.Clear();
                AddonFiles.Clear();
                ApplyFilter();
                TextBlock_TargetPath.Text = "未找到 HoYoShade 目录";
                TextBlock_Empty.Text = reason;
                TextBlock_Empty.Visibility = Visibility.Visible;
                ShowInfo("没有可管理的 HoYoShade 目录", reason, InfoBarSeverity.Warning);
                TextBlock_Status.Text = string.Empty;
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
                Items.Count, AddonFiles.Count, _manager.Host.RootPath);

            // 后台查一遍更新（每进一次页面查一次），有新的就在卡片上挂徽标
            _ = CheckUpdatesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh plugins");
            TextBlock_Status.Text = "刷新失败：" + ex.Message;
        }
        finally
        {
            _isWorking = false;
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
    /// **影响所有游戏** —— 这是「全局」和「按游戏」两种手段里的前一种。
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

        TextBlock_Status.Text = $"「{item.Name}」已全局{(enabled ? "启用" : "禁用")}（改了 {ok} 个文件的后缀）—— 所有游戏都会受影响。" +
                                (error is null ? string.Empty : "（部分失败：" + error + "）") +
                                purgeNote;

        // 「插件文件」那一栏也要跟着变
        RefreshAddonFiles();
    }

    /// <summary>
    /// 只认「会往插件目录里装 addon」的扩展包。
    /// 滤镜 / 预设那种（比如官方预设合集）先不进这个页面 —— 用户明确要求「只管理插件」。
    /// </summary>
    private static bool IsPluginExtension(ExtensionManifest manifest) =>
        manifest.Rules.Any(rule =>
            (rule.To ?? string.Empty).Replace('\\', '/').Contains("Addons", StringComparison.OrdinalIgnoreCase));

    /// <summary>过滤 + 搜索 → 重建可见列表</summary>
    private void ApplyFilter()
    {
        IEnumerable<PluginItemViewModel> query = Items;

        query = _filter switch
        {
            "installed" => query.Where(i => i.IsInstalled),
            "available" => query.Where(i => !i.IsInstalled),
            _ => query,
        };

        if (!string.IsNullOrWhiteSpace(_search))
        {
            query = query.Where(i =>
                i.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
                || i.Description.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
                || i.Manifest.Id.Contains(_search, StringComparison.OrdinalIgnoreCase));
        }

        List<PluginItemViewModel> visible = [.. query];

        VisibleItems.Clear();
        foreach (PluginItemViewModel item in visible)
        {
            VisibleItems.Add(item);
        }

        // 展开的卡片如果被过滤掉了，收起它，免得下次回来还是摊开的
        foreach (PluginItemViewModel hidden in Items.Except(visible))
        {
            hidden.IsExpanded = false;
        }

        int installed = Items.Count(i => i.IsInstalled);
        TextBlock_Count.Text = Items.Count == 0 ? string.Empty : $"共 {Items.Count} 个插件，已安装 {installed} 个";
        TextBlock_Status.Text = Items.Count == 0
            ? string.Empty
            : $"{VisibleItems.Count} / {Items.Count} 个显示中";

        bool empty = VisibleItems.Count == 0;
        TextBlock_Empty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (empty)
        {
            TextBlock_Empty.Text = Items.Count == 0
                ? (_manager is null ? "没有可管理的 HoYoShade 目录。" : "插件目录是空的。")
                : "没有符合当前过滤条件的插件。";
        }
    }

    #endregion

    #region 视图切换

    private void RadioButtons_View_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RadioButtons_View.SelectedItem is not RadioButton { Tag: string tag })
        {
            return;
        }

        bool files = tag == "files";
        bool optiScaler = tag == "optiscaler";

        Grid_Extensions.Visibility = files || optiScaler ? Visibility.Collapsed : Visibility.Visible;
        Grid_AddonFiles.Visibility = files ? Visibility.Visible : Visibility.Collapsed;
        Grid_OptiScaler.Visibility = optiScaler ? Visibility.Visible : Visibility.Collapsed;

        // 「全部 / 已安装 / 可下载」是扩展包专用的过滤，另外两栏用不上
        Visibility filterVisibility = files || optiScaler ? Visibility.Collapsed : Visibility.Visible;
        RadioButtons_Filter.Visibility = filterVisibility;
        TextBlock_Count.Visibility = filterVisibility;

        if (files)
        {
            RefreshAddonFiles();
        }
        else if (optiScaler)
        {
            RefreshOptiScaler();
        }
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

    #region 插件文件（全局开关）

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
        List<AddonFileItemViewModel> targets = [.. AddonFiles.Where(f => AddonLocalizer.SelectTable(tables, f.FileName) is not null)];

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

    private void RefreshAddonFiles()
    {
        AddonFiles.Clear();

        string? directory = _manager?.Host.AddonsPath;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            TextBlock_AddonFilesEmpty.Text = "找不到插件目录（HoYoShade\\reshade-shaders\\Addons）。";
            TextBlock_AddonFilesEmpty.Visibility = Visibility.Visible;
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
            AddonFiles.Add(new AddonFileItemViewModel(info, directory, cache, candidates, dllStatus, OnAddonFileToggleRequested));
        }

        TextBlock_AddonFilesEmpty.Text = "插件目录里没有 addon 文件。";
        TextBlock_AddonFilesEmpty.Visibility = AddonFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// 「插件文件」那一栏的删除：二级确认 → 删文件 → 顺手把各游戏 ini 里指向它的条目清掉。
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



    /// <summary>「插件文件」那一栏的开关：改这一个文件的后缀</summary>
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
        TextBlock_Status.Text = $"「{item.Name}」已{(enabled ? "全局启用" : "全局禁用")}（{Path.GetFileName(result.Path)}）—— 影响所有游戏。";

        // 扩展包那边的开关状态也跟着变
        foreach (PluginItemViewModel extension in Items)
        {
            extension.RefreshGlobalEnabled();
        }
    }

    #endregion

    #region OptiScaler（下载 + 单选启用）

    private OptiScalerDownloader? _optiScalerDownloader;

    /// <summary>刷新期间别响应 RadioButton 的 Checked（否则会把选择反复写盘）</summary>
    private bool _isRefreshingOptiScaler;

    /// <summary>每个来源拉过的版本 tag（装完刷新时直接用，不再打网络）</summary>
    private readonly Dictionary<string, List<ExtensionVersion>> _optiScalerVersions = new(StringComparer.OrdinalIgnoreCase);

    private OptiScalerLibrary OptiLib => new(AppConfig.OptiScalerRootPath);

    private OptiScalerDownloader OptiDownloads => _optiScalerDownloader ??= new OptiScalerDownloader();

    /// <summary>
    /// 扫一遍本地 OptiScaler 库 + 填上三个来源。
    /// 库目录：&lt;用户数据目录&gt;\OptiScaler（跟 HoYoShade 目录平级，不进插件目录）。
    /// </summary>
    private void RefreshOptiScaler()
    {
        _isRefreshingOptiScaler = true;

        try
        {
            OptiScalerBuilds.Clear();
            OptiScalerSources.Clear();

            string root = AppConfig.OptiScalerRootPath;

            if (root.Length == 0)
            {
                TextBlock_OptiScalerEmpty.Text = "还没读到用户数据目录，装不了 OptiScaler。";
                TextBlock_OptiScalerEmpty.Visibility = Visibility.Visible;
                return;
            }

            OptiScalerLibrary library = OptiLib;
            OptiScalerBuild? selected = library.GetSelected();
            List<OptiScalerBuild> all = library.List();

            foreach (OptiScalerBuild build in all)
            {
                OptiScalerBuilds.Add(new OptiScalerBuildItemViewModel(build, selected?.Id));
            }

            // 内置来源 + 远端目录覆盖（catalog/optiscaler.json，缓存在 .hysxcatalog）
            List<OptiScalerSource> sources = OptiScalerCatalog.MergeWithBuiltin(
                OptiScalerCatalog.LoadFile(RemoteCatalogService.OptiScalerCachePath));

            foreach (OptiScalerSource source in sources)
            {
                List<OptiScalerBuild> mine =
                    [.. all.Where(b => string.Equals(b.SourceId, source.Id, StringComparison.OrdinalIgnoreCase))];

                // 「当前版本」：优先当前启用的那个；没启用过就用这个来源最近装的那个
                string? current = null;
                if (selected is not null && string.Equals(selected.SourceId, source.Id, StringComparison.OrdinalIgnoreCase))
                {
                    current = selected.Version;
                }
                else if (mine.Count > 0)
                {
                    current = mine[0].Version;
                }

                var vm = new OptiScalerSourceItemViewModel(source) { CurrentVersion = current };
                foreach (OptiScalerBuild build in mine)
                {
                    vm.InstalledVersions.Add(build.Version);
                }

                // 之前拉过版本列表就直接用（装完/删完重新刷新时不用再打一遍网络）
                if (_optiScalerVersions.TryGetValue(source.Id, out List<ExtensionVersion>? cached))
                {
                    vm.ApplyVersions(cached);
                }

                OptiScalerSources.Add(vm);
            }

            TextBlock_OptiScalerEmpty.Text = "还没有下载任何 OptiScaler。先在下面「可下载」里拉一个。";
            TextBlock_OptiScalerEmpty.Visibility = OptiScalerBuilds.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            _isRefreshingOptiScaler = false;
        }
    }

    /// <summary>单选：点了某一行的「启用」就把 state.json 改成它（其它行自动取消）。</summary>
    private void RadioButton_OptiScalerBuild_Checked(object sender, RoutedEventArgs e)
    {
        if (_isRefreshingOptiScaler || sender is not FrameworkElement { DataContext: OptiScalerBuildItemViewModel item })
        {
            return;
        }

        try
        {
            OptiLib.Select(item.Id);

            foreach (OptiScalerBuildItemViewModel vm in OptiScalerBuilds)
            {
                vm.Enabled = string.Equals(vm.Id, item.Id, StringComparison.OrdinalIgnoreCase);
            }

            TextBlock_Status.Text = $"当前启用 OptiScaler：{item.Id}";
            _logger.LogInformation("OptiScaler selected: {Id}", item.Id);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Select OptiScaler build");
            ShowInfo("启用失败", ex.Message, InfoBarSeverity.Error);
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

    /// <summary>点开版本下拉时自动拉一次版本（用户要求：跟扩展包那边一样，点下拉自动获取）。</summary>
    private async void ComboBox_OptiScalerVersions_DropDownOpened(object sender, object e)
    {
        if (sender is not FrameworkElement { DataContext: OptiScalerSourceItemViewModel item }
            || item.Versions.Count > 0
            || !item.CanInteract)
        {
            return;
        }

        await LoadOptiScalerVersionsAsync(item);
    }

    private async Task LoadOptiScalerVersionsAsync(OptiScalerSourceItemViewModel item)
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
            else if (item.SelectedVersion is { } selected)
            {
                item.StatusText = $"共 {versions.Count} 个版本，默认选中 {item.TagOf(selected)}。";
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

    /// <summary>「下载并安装」：一个 release 里通常有好几个 zip（主包 / rtx40-mfg / 补丁），让用户挑。</summary>
    private async void Button_DownloadOptiScaler_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: OptiScalerSourceItemViewModel item } || _isWorking)
        {
            return;
        }

        string tag = item.TagOf(item.SelectedVersion);
        if (string.IsNullOrWhiteSpace(tag))
        {
            ShowInfo("还没选版本", "点开「版本」下拉，选一个版本。", InfoBarSeverity.Warning);
            return;
        }

        await RunAsync(async () =>
        {
            item.CanInteract = false;
            item.StatusText = $"正在查 {tag} 里的压缩包…";

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
                    });

                // 各分支手册都要求把 nvngx_dlssnr.dll 放在包旁边 —— 装完顺手从插件目录补一份
                NrdllPlaceResult nrdll = OptiScalerRuntime.EnsureNrdll(
                    build.Directory,
                    _manager?.Host.AddonsPath,
                    OptiScalerBuilds.Select(v => v.Build.Directory));

                // 之前一个都没启用、而且这个构建里真有 dll 时，装完直接启用它（省一步）
                if (library.GetSelected() is null && build.DllPath is not null)
                {
                    library.Select(build.Id);
                }

                item.StatusText = installer
                    ? setupDeclined
                        ? $"安装程序已下载（{build.SizeBytes / 1024d / 1024d:F1} MB），还没运行 —— 点「打开目录」可以自己双击它。"
                        : build.DllPath is null
                            ? "安装程序跑完了，但没在目录里找到可注入的 dll（version.dll / dxgi.dll 之类）。"
                            : $"装好了：注入目标 = {Path.GetFileName(build.DllPath)}。{nrdll.Message}到启动器页勾「启动 OptiScaler」即可。"
                    : $"已装好 {build.Id}（{build.SizeBytes / 1024d / 1024d:F1} MB）。" +
                      (build.DllPath is null ? "注意：包里没找到 OptiScaler.dll。" : string.Empty);
                TextBlock_Status.Text = $"OptiScaler「{build.Id}」安装完成：{build.Directory}";
                _logger.LogInformation("OptiScaler installed: {Id} from {Asset}", build.Id, asset);

                RefreshOptiScaler();

                OptiScalerSourceItemViewModel? refreshed =
                    OptiScalerSources.FirstOrDefault(s => string.Equals(s.Source.Id, item.Source.Id, StringComparison.OrdinalIgnoreCase));
                if (refreshed is not null)
                {
                    refreshed.StatusText = item.StatusText;
                }
            }
            finally
            {
                item.CanInteract = true;
                ProgressBar_Overall.IsIndeterminate = false;
            }
        });
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
        if (sender is not FrameworkElement { DataContext: OptiScalerSourceItemViewModel item })
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

    #region 顶部按钮

    private async void Button_Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

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

    private async void Button_InstallLocal_Click(object sender, RoutedEventArgs e)
    {
        if (_manager is null)
        {
            return;
        }

        try
        {
            string? file = await FileDialogHelper.PickSingleFileAsync(XamlRoot, ("插件包", ".zip"));
            if (string.IsNullOrWhiteSpace(file))
            {
                return;
            }

            ExtensionManifest? manifest = ReadManifestFromPackage(file);
            if (manifest is null)
            {
                ShowInfo("这个包里没有清单",
                    "压缩包根目录（或任意子目录）需要有一个 manifest.json，描述 id / 名字 / rules。",
                    InfoBarSeverity.Error);
                return;
            }

            manifest.Source = new ExtensionSource { Type = ExtensionSourceType.Local, Url = file };
            await RunInstallAsync(manifest);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Install local package");
            TextBlock_Status.Text = "安装本地包失败：" + ex.Message;
        }
    }

    #endregion

    #region 列表操作

    private void RadioButtons_Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RadioButtons_Filter.SelectedItem is RadioButton { Tag: string tag })
        {
            _filter = tag;
        }

        ApplyFilter();
    }

    private void TextBox_Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _search = TextBox_Search.Text?.Trim() ?? string.Empty;
        ApplyFilter();
    }

    /// <summary>点卡片 = 展开/收起详情（默认只占一行）</summary>
    private void PluginList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PluginItemViewModel item)
        {
            return;
        }

        bool expand = !item.IsExpanded;

        foreach (PluginItemViewModel other in Items)
        {
            other.IsExpanded = false;
        }

        item.IsExpanded = expand;

        if (expand && !item.VersionsLoaded)
        {
            _ = LoadVersionsAsync(item);
        }
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
                : $"共 {versions.Count} 个版本可装（默认最新）。";

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

    /// <summary>装下拉里选的那个版本</summary>
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

            // 插件文件没了，各游戏 ini 里指向它的 DisabledAddons / LoadFromDllMain 条目就是死条目（用户要求永久去掉）
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
    /// DisabledAddons / LoadFromDllMain 条目删掉（用户要求：检查是否有不存在的插件在里面）。
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
    /// 用户要求：插件被删除/全局禁用之后，这些条目也要跟着没。
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
                ExtensionInstallResult result = await _manager.InstallAsync(manifest, progress, default, tagOverride);

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
                {
                    item.IsBusy = false;
                }
            }

            await RefreshAsync();
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

    /// <summary>串行化操作 + 统一收尾</summary>
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

/// <summary>列表里的一行（一个可安装的插件扩展包）</summary>
public partial class PluginItemViewModel : ObservableObject
{
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
        IsInstalled = status.IsInstalled;

        // 版本要显示「盘上这个文件是哪个版本」——
        // 目录里写的基本都是 "latest"，光看它等于没版本
        string installedVersion = installedAddonVersion
                                  ?? status.Installed?.ResolvedTag
                                  ?? status.Installed?.Version
                                  ?? string.Empty;
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

    /// <summary>可装版本（GitHub tag，新 → 旧）—— 点开卡片时才去拉。
    /// 下拉里显示的是「tag · 发布日期」，真正用的 tag 在 <see cref="_versionTags"/> 里。</summary>
    public ObservableCollection<string> Versions { get; } = [];

    private readonly Dictionary<string, string> _versionTags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 铺进下拉。显示文本带上发布日期：GitHub 自己的列表是按 release 的创建时间排的，
    /// 光看 tag 顺序会觉得「乱」，把时间摆出来就一眼能看明白为什么是这个顺序。
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
    }

    /// <summary>下拉里选的文本还原成 tag</summary>
    public string? TagOf(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
        {
            return null;
        }

        return _versionTags.TryGetValue(display, out string? tag) ? tag : display.Trim();
    }

    private static string DisplayOf(ExtensionVersion version)
        => version.Published is { } published
            ? $"{version.Tag}  ·  {published.ToLocalTime():yyyy-MM-dd}"
            : version.Tag;

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
    private bool isExpanded;

    [ObservableProperty]
    private bool isInstalled;

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

/// <summary>插件文件那一栏的一行</summary>
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

/// <summary>「OptiScaler」那一栏的一行：一个已经下载到本地的构建。</summary>
public sealed partial class OptiScalerBuildItemViewModel : ObservableObject
{
    private bool? _enabled;

    public OptiScalerBuildItemViewModel(OptiScalerBuild build, string? selectedId)
    {
        Build = build;
        _enabled = string.Equals(build.Id, selectedId, StringComparison.OrdinalIgnoreCase);
    }

    public OptiScalerBuild Build { get; }

    public string Id => Build.Id;

    public string Title => $"{Build.Version}　·　{OptiScalerCatalog.Find(Build.SourceId)?.Name ?? Build.SourceId}";

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

    public Visibility EnabledVisibility => Enabled == true ? Visibility.Visible : Visibility.Collapsed;

    public Visibility NoDllVisibility => Build.DllPath is null && !InstallerOnly ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>构建目录里有没有神经渲染运行时（各 OptiScaler 分支的手册都要求放在包旁边）</summary>
    public bool HasNrdll => OptiScalerRuntime.HasNrdll(Build.Directory);

    /// <summary>有可注入的 dll、但缺 nvngx_dlssnr.dll → 黄字提示 + 「放入」按钮</summary>
    public Visibility MissingNrdllVisibility
        => Build.DllPath is not null && !HasNrdll ? Visibility.Visible : Visibility.Collapsed;

    public bool CanOpenFolder => Directory.Exists(Build.Directory);

    /// <summary>单选：true = 当前启用的那个（RadioButton 绑的就是它）</summary>
    public bool? Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(EnabledVisibility));
            }
        }
    }
}

/// <summary>「OptiScaler」那一栏的一行：一个可下载的来源（GitHub 仓库）。</summary>
public sealed partial class OptiScalerSourceItemViewModel : ObservableObject
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

    /// <summary>这个来源能装的版本（下拉里显示的文本，当前版本带后缀）</summary>
    public ObservableCollection<string> Versions { get; } = [];

    private readonly Dictionary<string, string> _versionTags = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>这个来源已经装过的版本 tag</summary>
    public HashSet<string> InstalledVersions { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>「当前版本」：当前启用的那个，或这个来源最近装的那个</summary>
    public string? CurrentVersion { get; set; }

    /// <summary>把版本列表铺进下拉，并默认选中当前版本（用户要求）</summary>
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

        if (CurrentVersion is { Length: > 0 } current)
        {
            SelectedVersion = Versions.FirstOrDefault(v => string.Equals(
                _versionTags.TryGetValue(v, out string? tag) ? tag : v,
                current,
                StringComparison.OrdinalIgnoreCase));
        }
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
    public string TagOf(string? display)
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

    /// <summary>选中的版本已经装过 → 「更新到此版本」，否则「下载并安装」</summary>
    public string ActionText
        => InstalledVersions.Contains(TagOf(SelectedVersion)) ? "更新到此版本" : "下载并安装";
}
