using CommunityToolkit.Mvvm.ComponentModel;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Frameworks;
using HoYoShadeHub.Helpers;
using HoYoShadeHub.Features.ViewHost;
using HoYoShadeHub.Features.OptiScaler;
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
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 「DLL 配置」页（左侧一级导航）。
///
/// <para>
/// 为什么需要单独一页：DLSS5 那几个插件光有 <c>.addon64</c> 是跑不起来的 —— 还要一整套运行时
/// 躺在**同一个目录**里（ReShade 只从 addons 目录加载）：
/// </para>
/// <list type="bullet">
/// <item><c>nvngx_dlssnr.dll</c> —— DLSS5 的神经渲染运行时，缺了插件直接加载不了（必需）；</item>
/// <item><c>sl.*.dll</c> —— Streamline 一整套，缺了大概率不出画面（建议）。</item>
/// </list>
///
/// <para>
/// 清单来自用户指定的 <c>RankFTW/RHI</c> 的 <c>dlss_manifest.json</c>（见 docs/RESHADE-INI.md §4），
/// 下载后解压进 <c>&lt;HoYoShade&gt;\reshade-shaders\Addons</c>。
/// </para>
/// </summary>
public sealed partial class DllConfigPage : PageBase
{
    private readonly ILogger<DllConfigPage> _logger = AppConfig.GetLogger<DllConfigPage>();

    private ShadeHost? _host;
    private DllCatalog? _catalog;
    private bool _isWorking;
    private bool _buildingCards;
    private readonly HashSet<string> _nrUpdates = new(StringComparer.OrdinalIgnoreCase);

    public DllConfigPage()
    {
        InitializeComponent();

        // 组件族列表错峰入场（和其它页面同一套 500ms + 45ms）
        Loaded += (_, _) => DispatcherQueue.TryEnqueue(() => MotionAnimations.PlayListEntrance(ItemsControl_Families));
    }

    public ObservableCollection<InstalledDllViewModel> Installed { get; } = [];

    public ObservableCollection<DllFamilyViewModel> Families { get; } = [];
    public ObservableCollection<InstalledDllViewModel> OtherInstalled { get; } = [];
    public ObservableCollection<NrVersionCardViewModel> NrVersions { get; } = [];

    protected override void OnLoaded()
    {

        // 先把「下载服务器」这个设置推给 HysxHttp，再去拉组件清单。
        // 不推的话 ProxyUrl 是 null，清单会直连 raw.githubusercontent.com ——
        // 国内基本连不上，报出来就是一句 SSL connection could not be established。
        PluginDownloadProxy.Apply();

        _ = RefreshAsync();
    }

    protected override void OnUnloaded()
    {
        Installed.Clear();
        Families.Clear();
        OtherInstalled.Clear();
        NrVersions.Clear();
    }

    #region 刷新

    private async Task RefreshAsync()
    {
        if (_isWorking)
        {
            return;
        }

        _isWorking = true;
        ProgressBar_Overall.IsIndeterminate = true;
        ProgressBar_Overall.Visibility = Visibility.Visible;
        TextBlock_Status.Text = "正在读插件目录与组件清单…";
        TextBlock_RuntimeOk.Visibility = Visibility.Collapsed;

        try
        {
            _host = PluginHostLocator.Resolve(out string reason);

            if (_host is null)
            {
                Installed.Clear();
                Families.Clear();
                TextBlock_TargetPath.Text = "未找到 HoYoShade 目录";
                ShowInfo("没有可管理的 HoYoShade 目录", reason, InfoBarSeverity.Warning);
                TextBlock_Status.Text = string.Empty;
                return;
            }

            TextBlock_TargetPath.Text = _host.AddonsPath;
            // 本地版本先显示，云端刷新失败或耗时不应让已下载内容消失。
            var recoveryErrors = await Task.Run(() => NrRuntimeLibrary.RecoverContentVersions(AppConfig.CacheRoot));
            foreach (string recoveryError in recoveryErrors) _logger.LogWarning("NR version recovery: {Error}", recoveryError);
            BuildNrCards();
            await RemoteCatalogService.RefreshIfDueAsync();
            await LoadInstalledAsync();

            _catalog ??= await DllComponentCatalog.LoadAsync();
            BuildFamilies();

            if (_catalog.Error is { } error)
            {
                ShowInfo("拉组件清单失败（还能用内置的那几条）", error, InfoBarSeverity.Warning);
            }
            else
            {
                UpdateStatusInfo();
            }

            _logger.LogInformation("DLL page: {Installed} dlls, {Families} families, host = {Host}",
                Installed.Count, Families.Count, _host.RootPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Refresh DLL page");
            TextBlock_Status.Text = "刷新失败：" + ex.Message;
        }
        finally
        {
            _isWorking = false;
            ProgressBar_Overall.IsIndeterminate = false;
            ProgressBar_Overall.Visibility = Visibility.Collapsed;
            TextBlock_Status.Text = string.Empty;   // 「正在读插件目录与组件清单…」别挂在底部不走
        }
    }

    private async Task LoadInstalledAsync()
    {
        string? directory = _host?.AddonsPath;
        var dlls = await Task.Run(() => DllInstaller.Scan(directory));

        Installed.Clear();
        foreach (InstalledDll dll in dlls)
        {
            Installed.Add(new InstalledDllViewModel(dll));
        }


    }

    private void BuildFamilies()
    {
        // 先记住用户已经选过的版本，刷新之后别丢
        var selected = Families.ToDictionary(f => f.Family.Id, f => f.SelectedVersion, StringComparer.OrdinalIgnoreCase);

        Families.Clear();
        if (_catalog is null)
        {
            return;
        }

        foreach (DllFamily family in DllComponentCatalog.Families)
        {
            if (family.Id.Equals("dlssnr", StringComparison.OrdinalIgnoreCase)) continue;
            var archive = new DllVersionStore(AppConfig.CacheRoot);
            IReadOnlyList<DllComponent> components = _catalog.Of(family.Id).Concat(
                archive.ListVersions(family.Id).Where(v => !_catalog.Of(family.Id).Any(c => c.Version.Equals(v.Version, StringComparison.OrdinalIgnoreCase)))
                    .Select(v => new DllComponent(family.Id, v.Version, ""))).ToArray();
            if (components.Count == 0)
            {
                continue;
            }

            // 盘上这一份是哪个变体：记账 + 字节数 / 归档同名同大小 / 数字段（见 DllVariantResolver 的注释）
            var store = new DllVersionStore(AppConfig.CacheRoot);
            var vm = new DllFamilyViewModel(
                family,
                components,
                Installed.Select(i => i.Dll).ToList(),
                AppConfig.GetInstalledDllVariant(family.Id),
                AppConfig.GetInstalledDllVariantSize(family.Id),
                store);
            if ((!vm.HasInstalled || vm.IsCurrentKnown)
                && selected.TryGetValue(family.Id, out string? previous)
                && vm.Versions.Contains(previous))
            {
                vm.SelectedVersion = previous;
            }
            else if (!vm.HasInstalled
                && DllSelectionPolicy.SelectDefault(family, components, OptiScalerPresetCatalog.GpuClass) is { } defaultComponent)
            {
                vm.SelectComponent(defaultComponent);
            }

            // 需求6：把归档里这一类 dll 装过的版本铺出来（每行一个删除）
            try
            {
                vm.SetInstalledVersions(store.ListVersions(family.Id)
                    .OrderByDescending(stored => stored.Version.Contains("Lecram", StringComparison.OrdinalIgnoreCase))
                    .Select(stored => new InstalledDllVersionRow(
                        family.Id,
                        stored.Version,
                        stored.Version,
                        // 「使用中」只认准了的那一版：以前这里用 SameNumbers，同数字段的会被一起标上
                        // （装了 Lecram 结果 310.8.0 那行也显示「使用中」）
                        isCurrent: vm.IsCurrentKnown && string.Equals(stored.Version, vm.CurrentVersion, StringComparison.OrdinalIgnoreCase))));
            }
            catch
            {
                vm.SetInstalledVersions([]);
            }

            if (vm.HasInstalled && !vm.InstalledVersionRows.Any(r => r.IsCurrent))
                vm.InstalledVersionRows.Insert(0, new InstalledDllVersionRow(family.Id,
                    vm.CurrentVersion ?? "未知版本", vm.IsCurrentKnown ? vm.CurrentVersion : "PE " + vm.CurrentVersion + "（变体未确认）", true, false));
            Families.Add(vm);
        }
        OtherInstalled.Clear();
        foreach (var dll in Installed.Where(d => !HysxFileLink.IsNeuralRuntime(d.FileName)
            && !DllComponentCatalog.Families.Any(f => DllInstaller.Matches(d.FileName, f)))) OtherInstalled.Add(dll);
        BuildNrCards();
    }

    private void BuildNrCards()
    {
        _buildingCards = true;
        try
        {
            NrVersions.Clear();
            var store = new DllVersionStore(AppConfig.CacheRoot);
            var components = _catalog?.Of("dlssnr") ?? [];
            var versions = DllFingerprintCatalog.Fingerprints.Where(f => f.Family.Equals("dlssnr", StringComparison.OrdinalIgnoreCase)).Select(f => f.Version)
                .Concat(components.Select(c => c.Version)).Concat(store.ListVersions("dlssnr").Select(v => v.Version))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            bool downloadHeadingShown = false;
            foreach (string version in versions.OrderByDescending(v => store.FilePath("dlssnr", v, NrRuntimeLibrary.FileName).Length > 0))
            {
                var component = components.FirstOrDefault(c => c.Version.Equals(version, StringComparison.OrdinalIgnoreCase));
                bool installed = store.FilePath("dlssnr", version, NrRuntimeLibrary.FileName).Length > 0;
                NrVersions.Add(new NrVersionCardViewModel(version, component, installed,
                    AppConfig.IsNrVersionEnabled(version), _nrUpdates.Contains(version), !installed && !downloadHeadingShown));
                if (!installed) downloadHeadingShown = true;
            }
        }
        finally { _buildingCards = false; }
    }

    /// <summary>DLSS5 那两样齐了没</summary>
    private AddonDllStatus CurrentDlss5Status() =>
        AddonDllChecker.Check(_host?.AddonsPath, [DlssDllRequirements.Dlss5Tag]);

    private void UpdateStatusInfo()
    {
        AddonDllStatus status = CurrentDlss5Status();

        if (status.Severity == 2)
        {
            TextBlock_RuntimeOk.Visibility = Visibility.Collapsed;
            ShowInfo("DLSS5 插件缺必需文件",
                status.Summary + " —— DLSS5 那几个插件现在起不来。点右上角「安装必要组件」可以一次装上。",
                InfoBarSeverity.Error);
        }
        else if (status.Severity == 1)
        {
            TextBlock_RuntimeOk.Visibility = Visibility.Collapsed;
            ShowInfo("Streamline 不全",
                status.Summary + " —— 插件能加载，但可能不出画面。",
                InfoBarSeverity.Warning);
        }
        else
        {
            // 齐了：标题右侧的常驻绿字（不再用占一整行的 InfoBar）
            InfoBar_Status.IsOpen = false;
            TextBlock_RuntimeOk.Visibility = Visibility.Visible;
        }
    }

    #endregion

    #region 安装

    private async void Button_InstallDll_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DllFamilyViewModel family } || _host is null)
        {
            return;
        }

        if (family.SelectedComponent is not { } component)
        {
            return;
        }

        var store = new DllVersionStore(AppConfig.CacheRoot);
        if (!store.Has(component.Family, component.Version))
        {
            await RunInstallAsync(component);
            return;
        }
        if (_isWorking) return;
        _isWorking = true;
        try
        {
            foreach (string source in Directory.EnumerateFiles(store.DirectoryFor(component.Family, component.Version), "*.dll"))
                HysxFileLink.LinkOrCopy(source, Path.Combine(_host.AddonsPath, Path.GetFileName(source)));
            AppConfig.SetInstalledDllVariant(component.Family, component.Version);
            AppConfig.SetInstalledDllVariantSize(component.Family,
                DllInstaller.GetInstalledSize(DllInstaller.Scan(_host.AddonsPath), family.Family));
            GameAddonPackService.SyncAll();
            await LoadInstalledAsync();
            BuildFamilies();
            UpdateStatusInfo();
        }
        catch (Exception ex) { ShowInfo("DLL 切换失败", ex.Message, InfoBarSeverity.Error); }
        finally { _isWorking = false; }
    }

    private void Toggle_NrGlobal_Toggled(object sender, RoutedEventArgs e)
    {
        if (_buildingCards || sender is not ToggleSwitch { DataContext: NrVersionCardViewModel row } toggle) return;
        bool expected = AppConfig.IsNrVersionEnabled(row.Version);
        if (toggle.IsOn == expected) return;
        if (_isWorking) { toggle.IsOn = expected; return; }
        try
        {
            AppConfig.SetNrVersionEnabled(row.Version, toggle.IsOn);
            BuildNrCards();
        }
        catch (Exception ex)
        {
            toggle.IsOn = expected;
            ShowInfo("NR 可选状态保存失败", ex.Message, InfoBarSeverity.Error);
        }
    }
    private void Button_OpenNr_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: NrVersionCardViewModel row })
            Process.Start(new ProcessStartInfo("explorer.exe", new DllVersionStore(AppConfig.CacheRoot).DirectoryFor("dlssnr", row.Version)) { UseShellExecute = true });
    }

    private async void Button_DownloadNr_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking || sender is not FrameworkElement { DataContext: NrVersionCardViewModel { Component: { } component } }) return;
        _isWorking = true;
        ProgressBar_Overall.Visibility = Visibility.Visible;
        ProgressBar_Overall.IsIndeterminate = false;
        try
        {
            var progress = new Progress<DownloadProgress>(p =>
            {
                ProgressBar_Overall.Value = p.Percent ?? 0;
                TextBlock_Status.Text = $"正在下载 {component.Version}… {p.BytesReceived / 1024d / 1024d:F1} MB";
            });
            await NrRuntimeLibrary.DownloadVersionAsync(AppConfig.CacheRoot, component, progress);
            _nrUpdates.Remove(component.Version);
            ShowInfo("NR 已加入版本库", component.Version + "；可在 OPT 或游戏插件卡片内选择。", InfoBarSeverity.Success);
        }
        catch (Exception ex) { ShowInfo("NR 下载失败", ex.Message, InfoBarSeverity.Error); }
        finally { BuildNrCards(); _isWorking = false; ProgressBar_Overall.Visibility = Visibility.Collapsed; }
    }

    private async void Button_CheckNrUpdate_Click(object sender, RoutedEventArgs e)
        => await CheckNrUpdatesAsync((sender as FrameworkElement)?.DataContext is NrVersionCardViewModel row ? row.Version : null);

    private async Task CheckNrUpdatesAsync(string? version = null)
    {
        if (_isWorking) return;
        _isWorking = true;
        try
        {
            var result = await RemoteCatalogService.RefreshAsync(force: true);
            if (!result.DllFingerprintsUpdated)
            {
                ShowInfo("NR 更新检查未完成", result.Message + " 保留上次有效目录。", InfoBarSeverity.Warning);
                return;
            }
            _catalog = await DllComponentCatalog.LoadAsync();
            var store = new DllVersionStore(AppConfig.CacheRoot);
            var updates = await Task.Run(() => DllFingerprintCatalog.Fingerprints
                .Where(f => f.Family.Equals("dlssnr", StringComparison.OrdinalIgnoreCase))
                .GroupBy(f => f.Version, StringComparer.OrdinalIgnoreCase)
                .Where(group =>
                {
                    string file = store.FilePath("dlssnr", group.Key, NrRuntimeLibrary.FileName);
                    if (file.Length == 0) return false;
                    try { DllFingerprintCatalog.ValidateKnownFile("dlssnr", group.Key, file); return false; }
                    catch { return true; }
                }).Select(group => group.Key).ToArray());
            _nrUpdates.Clear();
            foreach (string update in updates) _nrUpdates.Add(update);
            await LoadInstalledAsync();
            BuildFamilies();
            ShowInfo("NR 更新检查完成", version is not null
                ? _nrUpdates.Contains(version) ? version + " 有更新，可点击更新下载。" : version + " 未发现内容更新；新版本已列在版本库。"
                : $"发现 {_nrUpdates.Count} 个本地版本内容更新；所有可下载版本已刷新。", InfoBarSeverity.Informational);
        }
        catch (Exception ex) { ShowInfo("检查更新失败", ex.Message, InfoBarSeverity.Warning); }
        finally { _isWorking = false; }
    }

    private void Button_OpenDllVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: InstalledDllVersionRow row })
            Process.Start(new ProcessStartInfo("explorer.exe", new DllVersionStore(AppConfig.CacheRoot).DirectoryFor(row.FamilyId, row.Version)) { UseShellExecute = true });
    }

    private async void Button_ConsolidateNr_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking || _host is null) return;
        _isWorking = true;
        try
        {
            var errors = await Task.Run(() => NrRuntimeLibrary.Consolidate(AppConfig.CacheRoot,
                _host.AddonsPath, AppConfig.OptiScalerRootPath, AppConfig.CacheRoot));
            ShowInfo(errors.Count == 0 ? "NR 硬链接整理完成" : "部分 NR 未能整理",
                errors.Count == 0 ? "受管目录中相同内容的 NR 已共用硬链接。" : string.Join(Environment.NewLine, errors),
                errors.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }
        finally { _isWorking = false; }
    }

    private async void Button_ImportNr_Click(object sender, RoutedEventArgs e)
    {
        if (_isWorking)
        {
            ShowInfo("正在处理 DLL 任务", "请等待当前刷新或下载完成后再导入 NR。", InfoBarSeverity.Informational);
            return;
        }
        _isWorking = true;
        string temporary = Path.Combine(AppConfig.CacheRoot, "nr-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            _logger.LogInformation("Opening NR import picker");
            string? file = await FileDialogHelper.PickSingleFileAsync(XamlRoot, ("NR DLL", ".dll"), ("ZIP", ".zip"));
            if (file is null) return;
            string version = await Task.Run(() =>
            {
                Directory.CreateDirectory(temporary);
                string source = Path.Combine(temporary, NrRuntimeLibrary.FileName);
                if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    using var zip = ZipFile.OpenRead(file);
                    var entries = zip.Entries.Where(x => Path.GetFileName(x.FullName).Equals(NrRuntimeLibrary.FileName, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (entries.Length != 1) throw new IOException("ZIP 必须包含唯一的 nvngx_dlssnr.dll。");
                    entries[0].ExtractToFile(source);
                }
                else File.Copy(file, source);
                string hash = NrRuntimeLibrary.Hash(source);
                var fingerprint = DllFingerprintCatalog.Find("dlssnr", new FileInfo(source).Length, hash);
                if (!DllFingerprintCatalog.ValidatePe(source, fingerprint, out string peError))
                    throw new IOException("NR PE 验证失败：" + peError);
                string label = fingerprint?.Version ?? ("自定义-" + hash[..16]);
                var store = new DllVersionStore(AppConfig.CacheRoot);
                store.Archive("dlssnr", label, [source]);
                string stored = store.FilePath("dlssnr", label, NrRuntimeLibrary.FileName);
                if (stored.Length == 0 || NrRuntimeLibrary.Hash(stored) != hash) throw new IOException("NR 导入校验失败。");
                return label;
            });
            BuildNrCards();
            ShowInfo("NR 已加入版本库", version + "；可在游戏插件或 OptiScaler 卡片中选择。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "NR import failed");
            ShowInfo("导入失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _isWorking = false;
            HysxUtil.TryDeleteDirectory(temporary);
        }
    }

    /// <summary>「安装必要组件」：缺 nvngx_dlssnr.dll 就装默认那版，缺 Streamline 就装最新那版</summary>
    private async void Button_FixDlss5_Click(object sender, RoutedEventArgs e)
    {
        if (_host is null || _catalog is null)
        {
            return;
        }

        var toInstall = new List<DllComponent>();
        AddonDllStatus status = CurrentDlss5Status();

        if (status.MissingRequired.Any(r => r.Files.Contains("nvngx_dlssnr.dll", StringComparer.OrdinalIgnoreCase))
            && DllComponentCatalog.FamilyOf("dlssnr") is { } nrFamily
            && DllSelectionPolicy.SelectDefault(nrFamily, _catalog.Of("dlssnr"), OptiScalerPresetCatalog.GpuClass) is { } nr)
        {
            toInstall.Add(nr);
        }

        // Streamline 缺了也是红（必需级）。**别拿清单第一条** —— 那是最新版，
        // 2.14.1.0 起的包里已经没有 sl.interposer.dll，装了等于白装。优先 2.14.0.0。
        DllFamily? slFamily = DllComponentCatalog.FamilyOf("streamline");
        var slVersions = _catalog.Of("streamline");
        bool slMissing = status.MissingRequired.Concat(status.MissingRecommended)
            .Any(r => r.Files.Contains("sl.interposer.dll", StringComparer.OrdinalIgnoreCase));
        if (slMissing && slVersions.Count > 0)
        {
            toInstall.Add(
                slVersions.FirstOrDefault(c => string.Equals(c.Version, slFamily?.PreferredVersion, StringComparison.OrdinalIgnoreCase))
                ?? slVersions[0]);
        }

        if (toInstall.Count == 0)
        {
            TextBlock_Status.Text = "已经齐了，不用补。";
            return;
        }

        foreach (DllComponent component in toInstall)
        {
            await RunInstallAsync(component, quiet: true);
        }

        await RefreshAsync();
    }

    private async Task RunInstallAsync(DllComponent component, bool quiet = false)
    {
        if (_host is null || _isWorking)
        {
            return;
        }

        _isWorking = true;
        ProgressBar_Overall.IsIndeterminate = false;
        ProgressBar_Overall.Value = 0;
        ProgressBar_Overall.Visibility = Visibility.Visible;

        try
        {
            DllFamily? family = DllComponentCatalog.FamilyOf(component.Family);
            TextBlock_Status.Text = $"正在下载 {family?.DisplayName ?? component.Family} {component.Version}…";

            var progress = new Progress<DownloadProgress>(p =>
            {
                if (p.Percent is { } percent)
                {
                    ProgressBar_Overall.Value = percent;
                    TextBlock_Status.Text = $"正在下载 {component.Version}… {percent:F0}%（{p.BytesReceived / 1024d / 1024d:F1} MB）";
                }
                else
                {
                    TextBlock_Status.Text = $"正在下载 {component.Version}… {p.BytesReceived / 1024d / 1024d:F1} MB";
                }
            });

            DllInstallResult result = await DllInstaller.InstallAsync(_host.AddonsPath, component, progress);

            if (result.Ok)
            {
                _logger.LogInformation("DLL installed: {Family} {Version} -> {Files}", component.Family, component.Version, string.Join(",", result.Installed));

                // 记下装的是哪个变体：PE 版本号里没有 SF / SF-v2 / Lecram 这种信息，不记就分不出来
                AppConfig.SetInstalledDllVariant(component.Family, component.Version);

                // 连字节数一起记：Lecram 那份的 PE 是 310.8.3.0，和清单里的 310.8.Lecram 数字段都对不上，
                // 只靠版本号会回退成 310.8.0（用户反馈：下的明明是 Lecram，识别成 50 系）
                if (family is not null)
                {
                    AppConfig.SetInstalledDllVariantSize(
                        family.Id,
                        DllInstaller.GetInstalledSize(DllInstaller.Scan(_host.AddonsPath), family));
                }

                // 需求6：顺带把这一版归档到 <CacheRoot>\dlls\<family>\<version>\（硬链接优先，跨盘复制）。
                // 归档失败绝不能让安装失败 —— DllVersionStore 的方法都不抛。
                try
                {
                    var store = new DllVersionStore(AppConfig.CacheRoot);
                    int archived = store.Archive(
                        component.Family,
                        component.Version,
                        result.Installed.Select(name => Path.Combine(_host.AddonsPath, name)));

                    _logger.LogInformation("Runtime dll archived: {Family} {Version} -> {Count} files",
                        component.Family, component.Version, archived);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Archive runtime dll {Family} {Version}", component.Family, component.Version);
                }

                string extra = result.Overwritten.Count > 0 ? $"（覆盖了 {string.Join("、", result.Overwritten)}）" : string.Empty;
                var linkErrors = await Task.Run(() => NrRuntimeLibrary.Consolidate(AppConfig.CacheRoot,
                    _host.AddonsPath, AppConfig.OptiScalerRootPath, AppConfig.CacheRoot));
                if (linkErrors.Count > 0) throw new IOException(string.Join(Environment.NewLine, linkErrors));
                GameAddonPackService.SyncAll();
                TextBlock_Status.Text = $"{family?.DisplayName ?? component.Family} {component.Version} 装好了：{string.Join("、", result.Installed)}{extra}";
                if (!quiet)
                {
                    ShowInfo("装好了", TextBlock_Status.Text, InfoBarSeverity.Success);
                }
            }
            else
            {
                _logger.LogWarning("DLL install failed: {Error}", result.Error);
                TextBlock_Status.Text = "安装失败：" + result.Error;
                ShowInfo("安装失败", result.Error ?? string.Empty, InfoBarSeverity.Error);
            }

            await LoadInstalledAsync();
            BuildFamilies();
            UpdateStatusInfo();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Install DLL");
            TextBlock_Status.Text = "安装失败：" + ex.Message;
        }
        finally
        {
            _isWorking = false;
            ProgressBar_Overall.IsIndeterminate = false;
            ProgressBar_Overall.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 需求6：删掉归档里的一个 dll 版本。
    /// 只删 <c>&lt;CacheRoot&gt;\dlls\&lt;family&gt;\&lt;version&gt;\</c>，共享 Addons 目录里
    /// 当前生效的那一份不动（否则 ReShade 直接少文件）。
    /// </summary>
    private void Button_DeleteDllVersion_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InstalledDllVersionRow row })
        {
            return;
        }

        try
        {
            var store = new DllVersionStore(AppConfig.CacheRoot);
            if (store.DeleteVersion(row.FamilyId, row.Version))
            {
                _logger.LogInformation("Archived runtime dll deleted: {Family} {Version}", row.FamilyId, row.Version);
                TextBlock_Status.Text = $"已删掉归档里的 {row.FamilyId} {row.Version}（共享目录里现在用的那份没动）。";
                BuildFamilies();
            }
            else
            {
                TextBlock_Status.Text = $"归档里没有 {row.FamilyId} {row.Version}，可能已经被删了。";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Delete archived dll version {Family} {Version}", row.FamilyId, row.Version);
            TextBlock_Status.Text = "删除失败：" + ex.Message;
        }
    }

    #endregion

    #region 顶部按钮

    private async void Button_Refresh_Click(object sender, RoutedEventArgs e)
    {
        await CheckNrUpdatesAsync();
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
                    $"在 {folder} 里找不到 ReShade64.dll。请选到 HoYoShade 这一层。",
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
            string? path = _host?.AddonsPath;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open addons folder");
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

    #endregion
}

/// <summary>左边列表里的一个 dll</summary>
public sealed class InstalledDllViewModel
{
    public InstalledDllViewModel(InstalledDll dll)
    {
        Dll = dll;
        FileName = dll.FileName;
        SizeText = dll.SizeText;
        VersionText = string.IsNullOrWhiteSpace(dll.Version)
            ? "版本未知"
            : "版本 " + DllVersion.Normalize(dll.Version);
    }

    public InstalledDll Dll { get; }

    public string FileName { get; }

    public string SizeText { get; }

    public string VersionText { get; }
}

/// <summary>右边一类组件</summary>
public partial class DllFamilyViewModel : ObservableObject
{
    private readonly IReadOnlyList<DllComponent> _components;

    public DllFamilyViewModel(
        DllFamily family,
        IReadOnlyList<DllComponent> components,
        IReadOnlyList<InstalledDll> installed,
        string? recordedVariant = null,
        long recordedSize = 0,
        DllVersionStore? store = null)
    {
        Family = family;
        _components = components;
        Title = family.DisplayName;
        Files = installed.Where(d => DllInstaller.Matches(d.FileName, family)).Select(d => new InstalledDllViewModel(d)).ToArray();

        string requirement = family.Level == DllRequirementLevel.Required ? "缺了插件起不来" : "缺了可能不出画面";
        Note = $"{family.FilePattern} · {requirement} —— {family.Note}";
        RequiredBadgeVisibility = family.Level == DllRequirementLevel.Required ? Visibility.Visible : Visibility.Collapsed;

        // 版本列表：带上备注（RTX40 / SF / Lecram 这种）。Lecram 固定在顶部（用户要求）。
        Versions = [.. components.Select(DisplayOf)
            .OrderByDescending(v => v.Contains("Lecram", StringComparison.OrdinalIgnoreCase))];

        // 盘上这一份是哪个变体：记账 + 字节数 / 归档同名同大小 / 数字段兜底（见 DllVariantResolver 的注释）。
        // 以前只用 SameNumbers：Lecram 的 PE 是 310.8.3.0，数字段对不上就回退成 310.8.0（「识别成 50 系」）。
        DllVariantProbe probe = DllVariantResolver.Identify(
            store,
            family,
            InstalledDlls(installed, family).FirstOrDefault(),
            recordedVariant,
            recordedSize);

        CurrentVersion = probe.Variant ?? DllInstaller.GetInstalledVersion(installed, family);

        bool hasAny = InstalledDlls(installed, family).Any();
        HasInstalled = hasAny;
        IsCurrentKnown = probe.Known;
        InstalledText = !hasAny
            ? "还没装"
            : probe.Known
                ? $"已装：{probe.Display}"
                : string.IsNullOrWhiteSpace(probe.Display)
                    ? "已装（读不出版本）"
                    : $"已装：PE {probe.Display}（变体未确认）";

        // 只有当前文件能和清单准确对上时才自动选中；未知 NR 保持空选，避免回退到第一条 50xx。
        DllComponent? match = components.FirstOrDefault(c => string.Equals(c.Version, probe.Variant, StringComparison.OrdinalIgnoreCase))
                              ?? (family.Id.Equals("dlssnr", StringComparison.OrdinalIgnoreCase) ? null
                                  : components.FirstOrDefault(c => DllVersion.IsSame(c.Version, CurrentVersion)));
        if (match is not null)
        {
            SelectedVersion = DisplayOf(match);
        }
        else if (!hasAny && !family.Id.Equals("dlssnr", StringComparison.OrdinalIgnoreCase))
        {
            SelectedVersion = DisplayOf(components.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Note)) ?? components[0]);
        }
        else
        {
            SelectedVersion = string.Empty;
        }
    }

    public DllFamily Family { get; }
    public IReadOnlyList<InstalledDllViewModel> Files { get; }

    public string Title { get; }

    public string Note { get; }

    public string InstalledText { get; }

    public bool HasInstalled { get; }

    public bool IsCurrentKnown { get; }

    /// <summary>盘上现在这一版（PE 版本 + 记账变体），给「使用中」角标用</summary>
    public string? CurrentVersion { get; }

    // ===================== 需求6：这一类 dll 的已安装版本（每行一个删除） =====================

    public ObservableCollection<InstalledDllVersionRow> InstalledVersionRows { get; } = [];

    public void SetInstalledVersions(IEnumerable<InstalledDllVersionRow> rows)
    {
        InstalledVersionRows.Clear();
        foreach (InstalledDllVersionRow row in rows)
        {
            InstalledVersionRows.Add(row);
        }

        OnPropertyChanged(nameof(InstalledVersionsSummary));
    }

    public string InstalledVersionsSummary => InstalledVersionRows.Count == 0
        ? "已安装版本：（还没有归档；装 / 重装一次就会留档）"
        : $"已安装版本：{InstalledVersionRows.Count} 个";

    public Visibility RequiredBadgeVisibility { get; }

    public ObservableCollection<string> Versions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedComponent))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private string selectedVersion = string.Empty;

    public DllComponent? SelectedComponent => _components.FirstOrDefault(c => DisplayOf(c) == SelectedVersion);

    public bool CanInstall => SelectedComponent is not null;

    public void SelectComponent(DllComponent component) => SelectedVersion = DisplayOf(component);

    private static string DisplayOf(DllComponent component) =>
        string.IsNullOrWhiteSpace(component.Note) ? component.Version : $"{component.Version}（{component.Note}）";

    private static IEnumerable<InstalledDll> InstalledDlls(IEnumerable<InstalledDll> installed, DllFamily family) =>
        installed.Where(d => DllInstaller.Matches(d.FileName, family));
}

/// <summary>
/// 「DLL 配置」页「已安装版本」列表里的一行（需求6）：归档里的一份版本 + 删除。
/// 数据模板里只能挂 Click，所以删除动作由页面处理器读这一行来做。
/// </summary>
public sealed class NrVersionCardViewModel(string version, DllComponent? component, bool installed, bool available, bool update, bool showDownloadHeading = false)
{
    public string Version { get; } = version;
    public DllComponent? Component { get; } = component;
    public bool IsInstalled { get; } = installed;
    public bool IsAvailable { get; } = available;
    public string StatusText => update ? "有更新" : IsInstalled ? IsAvailable ? "已下载 · 可供选择" : "已下载 · 已从可选列表关闭" : "未下载";
    public string DownloadText => update ? "更新" : "下载";
    public Visibility DownloadVisibility => Component is not null && (!IsInstalled || update) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InstalledActionsVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DownloadHeadingVisibility => showDownloadHeading ? Visibility.Visible : Visibility.Collapsed;
    public Microsoft.UI.Xaml.Media.Brush BarBrush => CatalogRowVisual.Bar(IsInstalled && IsAvailable);
}

public sealed class InstalledDllVersionRow
{
    public InstalledDllVersionRow(string familyId, string version, string? displayText, bool isCurrent, bool archived = true)
    {
        FamilyId = familyId;
        Version = version;
        DisplayText = string.IsNullOrWhiteSpace(displayText) ? version : displayText;
        IsCurrent = isCurrent;
        IsArchived = archived;
    }

    public Microsoft.UI.Xaml.Media.Brush BarBrush => CatalogRowVisual.Bar(IsCurrent);

    public bool IsArchived { get; }

    public string FamilyId { get; }

    public string Version { get; }

    public string DisplayText { get; }

    public bool IsCurrent { get; }

    public Visibility CurrentVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
}
