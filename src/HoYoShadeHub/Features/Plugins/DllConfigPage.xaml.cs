using CommunityToolkit.Mvvm.ComponentModel;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.Models;
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

    public DllConfigPage()
    {
        InitializeComponent();
    }

    public ObservableCollection<InstalledDllViewModel> Installed { get; } = [];

    public ObservableCollection<DllFamilyViewModel> Families { get; } = [];

    protected override void OnLoaded()
    {
        InstalledList.ItemsSource = Installed;

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

        TextBlock_InstalledEmpty.Visibility = Installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            IReadOnlyList<DllComponent> components = _catalog.Of(family.Id);
            if (components.Count == 0)
            {
                continue;
            }

            var vm = new DllFamilyViewModel(
                family,
                components,
                Installed.Select(i => i.Dll).ToList(),
                AppConfig.GetInstalledDllVariant(family.Id));
            if (selected.TryGetValue(family.Id, out string? previous) && vm.Versions.Contains(previous))
            {
                vm.SelectedVersion = previous;
            }

            Families.Add(vm);
        }
    }

    /// <summary>DLSS5 那两样齐了没</summary>
    private AddonDllStatus CurrentDlss5Status() =>
        AddonDllChecker.Check(_host?.AddonsPath, [DlssDllRequirements.Dlss5Tag]);

    private void UpdateStatusInfo()
    {
        AddonDllStatus status = CurrentDlss5Status();

        if (status.Severity == 2)
        {
            ShowInfo("DLSS5 插件缺必需文件",
                status.Summary + " —— DLSS5 那几个插件现在起不来。点右上角「安装必要组件」可以一次装上。",
                InfoBarSeverity.Error);
        }
        else if (status.Severity == 1)
        {
            ShowInfo("Streamline 不全",
                status.Summary + " —— 插件能加载，但可能不出画面。",
                InfoBarSeverity.Warning);
        }
        else
        {
            ShowInfo("DLSS5 需要的运行时齐了", "nvngx_dlssnr.dll 和 Streamline 都在插件目录里。", InfoBarSeverity.Success);
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

        await RunInstallAsync(component);
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
            && _catalog.Of("dlssnr").FirstOrDefault() is { } nr)
        {
            toInstall.Add(nr);
        }

        if (status.MissingRecommended.Count > 0 && _catalog.Of("streamline").FirstOrDefault() is { } sl)
        {
            toInstall.Add(sl);
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

                // 记下装的是哪个变体：PE 版本号里没有 SF / SF-v2 这种信息，不记就分不出来
                AppConfig.SetInstalledDllVariant(component.Family, component.Version);

                string extra = result.Overwritten.Count > 0 ? $"（覆盖了 {string.Join("、", result.Overwritten)}）" : string.Empty;
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

    #endregion

    #region 顶部按钮

    private async void Button_Refresh_Click(object sender, RoutedEventArgs e)
    {
        _catalog = null;
        await RefreshAsync();
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
        string? recordedVariant = null)
    {
        Family = family;
        _components = components;
        Title = family.DisplayName;

        string requirement = family.Level == DllRequirementLevel.Required ? "缺了插件起不来" : "缺了可能不出画面";
        Note = $"{family.FilePattern} · {requirement} —— {family.Note}";
        RequiredBadgeVisibility = family.Level == DllRequirementLevel.Required ? Visibility.Visible : Visibility.Collapsed;

        // 版本列表：带上备注（RTX40 / SF 这种）
        Versions = [.. components.Select(DisplayOf)];

        string? currentVersion = DllInstaller.GetInstalledVersion(installed, family);

        // PE 版本分不出变体（SF / SF-v2 / RTX40）——装的时候记过账就用记账那份
        if (!string.IsNullOrWhiteSpace(recordedVariant)
            && DllVersion.SameNumbers(recordedVariant, currentVersion))
        {
            currentVersion = recordedVariant;
        }

        bool hasAny = InstalledDlls(installed, family).Any();
        InstalledText = hasAny
            ? (string.IsNullOrWhiteSpace(currentVersion) ? "已装（读不出版本）" : $"已装：{DllVersion.Normalize(currentVersion)}")
            : "还没装";

        // 默认选：装了就选对得上的那个；没装就选「清单里本来就有」的那条
        // （我硬编码补进来的 RTX40 / SF 变体带 Note，不抢默认位）
        DllComponent? match = components.FirstOrDefault(c => DllVersion.IsSame(c.Version, currentVersion))
                              ?? components.FirstOrDefault(c => string.IsNullOrWhiteSpace(c.Note))
                              ?? components[0];
        SelectedVersion = DisplayOf(match);
    }

    public DllFamily Family { get; }

    public string Title { get; }

    public string Note { get; }

    public string InstalledText { get; }

    public Visibility RequiredBadgeVisibility { get; }

    public ObservableCollection<string> Versions { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedComponent))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private string selectedVersion = string.Empty;

    public DllComponent? SelectedComponent => _components.FirstOrDefault(c => DisplayOf(c) == SelectedVersion);

    public bool CanInstall => SelectedComponent is not null;

    private static string DisplayOf(DllComponent component) =>
        string.IsNullOrWhiteSpace(component.Note) ? component.Version : $"{component.Version}（{component.Note}）";

    private static IEnumerable<InstalledDll> InstalledDlls(IEnumerable<InstalledDll> installed, DllFamily family) =>
        installed.Where(d => DllInstaller.Matches(d.FileName, family));
}
