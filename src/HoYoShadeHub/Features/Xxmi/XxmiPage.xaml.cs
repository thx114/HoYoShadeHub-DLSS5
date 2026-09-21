using CommunityToolkit.Mvvm.ComponentModel;
using HoYoShadeHub.Core.HoYoPlay;
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

namespace HoYoShadeHub.Features.Xxmi;

/// <summary>MI 实例下拉项</summary>
public sealed record XxmiInstanceItem(string Importer, string Path)
{
    /// <summary>下拉里就显示实例名（ZZMI / GIMI …），太长反而看不清</summary>
    public string Display => Importer;
}

/// <summary>Mods 列表里的一行</summary>
public sealed partial class XxmiModItem : ObservableObject
{
    internal XxmiModItem(XxmiModEntry entry)
    {
        Name = entry.Name;
        Path = entry.Path;
        IsDirectory = entry.IsDirectory;
        _enabled = entry.Enabled;
    }

    public string Name { get; }

    public string Path { get; set; }

    public bool IsDirectory { get; }

    public string Info => IsDirectory ? "文件夹" : "ini 文件";

    private bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set => SetProperty(ref _enabled, value);
    }
}

/// <summary>
/// 「模型替换（XXMI）」页（左侧一级导航）。
///
/// 上面选 MI 实例（绝区零 = ZZMI、原神 = GIMI、星铁 = SRMI…，可自动查找或手动指定），
/// 下面管这个实例 <c>Mods\</c> 里的 mod：启用/禁用（改名字加/去掉 DISABLED）、打开、删除、导入文件夹 / zip。
/// </summary>
public sealed partial class XxmiPage : PageBase
{
    private readonly ILogger<XxmiPage> _logger = AppConfig.GetLogger<XxmiPage>();
    private readonly ObservableCollection<XxmiModItem> _mods = [];

    private GameId? _gameId;
    private string? _instance;
    private bool _loadingMods;

    public XxmiPage()
    {
        InitializeComponent();
        ListView_Mods.ItemsSource = _mods;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _gameId = e.Parameter as GameId;
        LoadInstance();
    }

    private void LoadInstance()
    {
        string? expected = XxmiLocator.ImporterFor(_gameId?.GameBiz);
        string? instance = XxmiLocator.FindInstance(_gameId?.GameBiz, out string? importer);

        _instance = instance;
        TextBox_Instance.Text = instance ?? string.Empty;

        string importerName = importer ?? expected ?? string.Empty;
        TextBlock_Title.Text = importerName.Length == 0 ? "模型替换" : $"模型替换（{importerName}）";

        List<XxmiInstanceItem> items = [];
        string? root = XxmiLocator.FindRoot();

        if (root is not null)
        {
            foreach (string dir in XxmiLocator.ListInstances(root))
            {
                items.Add(new XxmiInstanceItem(Path.GetFileName(dir), dir));
            }
        }

        if (instance is not null && items.All(i => !string.Equals(i.Path, instance, StringComparison.OrdinalIgnoreCase)))
        {
            items.Insert(0, new XxmiInstanceItem(Path.GetFileName(instance), instance));
        }

        ComboBox_Importer.ItemsSource = items;
        ComboBox_Importer.SelectedItem = items.FirstOrDefault(i => string.Equals(i.Path, instance, StringComparison.OrdinalIgnoreCase));

        if (instance is null)
        {
            TextBlock_Status.Text = expected is null
                ? "这个游戏没有对应的 MI 实例（认识的：ZZMI / GIMI / SRMI / WWMI / HIMI）。"
                : $"没找到 XXMI 的 {expected} 实例。点「自动查找」，或「选择…」手动指定；也可以先用 XXMI Launcher 装一次。";
        }
        else
        {
            TextBlock_Status.Text = $"MI 实例：{instance}";
        }

        LoadMods();

        // 启动器那边「启用 XXMI 注入」跑完会写到这儿，方便确认上次到底有没有注进去
        string? lastLaunch = AppConfig.XxmiLastLaunch;

        if (!string.IsNullOrWhiteSpace(lastLaunch))
        {
            TextBlock_Status.Text += "　上次启动：" + lastLaunch;
        }
    }

    private void LoadMods()
    {
        _loadingMods = true;
        _mods.Clear();

        if (_instance is null)
        {
            TextBlock_ModsCount.Text = string.Empty;
            _loadingMods = false;
            return;
        }

        string modsDirectory = XxmiLocator.ModsDirectory(_instance);

        if (!Directory.Exists(modsDirectory))
        {
            TextBlock_ModsCount.Text = "（还没有 Mods 目录，导入一个 mod 就会自动建）";
            _loadingMods = false;
            return;
        }

        foreach (XxmiModEntry entry in XxmiModManager.List(modsDirectory))
        {
            _mods.Add(new XxmiModItem(entry));
        }

        int enabled = _mods.Count(m => m.Enabled);
        TextBlock_ModsCount.Text = $"共 {_mods.Count} 个 mod（启用 {enabled} / 禁用 {_mods.Count - enabled}）";
        _loadingMods = false;
    }

    private void ComboBox_Importer_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ComboBox_Importer.SelectedItem is not XxmiInstanceItem item)
        {
            return;
        }

        _instance = item.Path;
        TextBox_Instance.Text = item.Path;

        if (_gameId is not null)
        {
            AppConfig.SetXxmiInstance(_gameId.GameBiz, item.Path);
        }

        LoadMods();
    }

    private void Button_AutoFind_Click(object sender, RoutedEventArgs e)
    {
        if (_gameId is not null)
        {
            AppConfig.SetXxmiInstance(_gameId.GameBiz, null);
        }

        LoadInstance();
        TextBlock_Status.Text = _instance is null ? "自动查找没找到 XXMI（可以手动指定 MI 目录）。" : $"自动找到：{_instance}";
    }

    private async void Button_Browse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? folder = await FileDialogHelper.PickFolderAsync(XamlRoot);

            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            if (!XxmiLocator.IsInstance(folder))
            {
                TextBlock_Status.Text = $"{folder} 看起来不是 MI 实例目录（里面没有 d3dx.ini / d3d11.dll）。";
                return;
            }

            if (_gameId is not null)
            {
                AppConfig.SetXxmiInstance(_gameId.GameBiz, folder);
            }

            _instance = folder;
            TextBox_Instance.Text = folder;
            LoadInstance();
            TextBlock_Status.Text = $"已指定：{folder}";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "选择目录失败：" + ex.Message;
        }
    }

    private void Button_OpenInstance_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_instance) && Directory.Exists(_instance))
        {
            OpenInExplorer(_instance);
        }
    }

    private void Button_OpenMods_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_instance))
        {
            return;
        }

        string mods = XxmiLocator.ModsDirectory(_instance);
        Directory.CreateDirectory(mods);
        OpenInExplorer(mods);
    }

    /// <summary>
    /// 打开 XXMI Launcher。模型替换的注入必须由它驱动 —— ZZMI 里那份 d3d11.dll 是"受控版"
    /// （ini 头写着 intended to be loaded by XXMI Launcher），我们用 3dmloader 的 Inject 把它塞进游戏进程
    /// 能成功加载，但它完全不初始化（连 d3d11_log.txt 都不写），所以注入这条路先不做。
    /// </summary>
    private void Button_LaunchXxmi_Click(object sender, RoutedEventArgs e)
    {
        string? root = XxmiLocator.FindRoot();

        if (root is null)
        {
            TextBlock_Status.Text = "找不到 XXMI 安装目录。";
            return;
        }

        string launcher = Path.Combine(root, "Resources", "Bin", "XXMI Launcher.exe");

        if (!File.Exists(launcher))
        {
            TextBlock_Status.Text = $"找不到 {launcher}";
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = launcher, UseShellExecute = true });
            TextBlock_Status.Text = "已打开 XXMI Launcher —— 在它里面点启动，模型替换才会生效。";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "打开 XXMI Launcher 失败：" + ex.Message;
        }
    }

    private void Button_Refresh_Click(object sender, RoutedEventArgs e) => LoadInstance();

    private async void Button_ImportFolder_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_instance))
        {
            TextBlock_Status.Text = "先指定 MI 实例目录。";
            return;
        }

        string? folder = await FileDialogHelper.PickFolderAsync(XamlRoot);

        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        try
        {
            string mods = XxmiLocator.ModsDirectory(_instance);
            Directory.CreateDirectory(mods);
            string imported = XxmiModManager.ImportFolder(mods, folder);
            LoadMods();
            TextBlock_Status.Text = $"已导入：{Path.GetFileName(imported)}";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "导入失败：" + ex.Message;
        }
    }

    private async void Button_ImportZip_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_instance))
        {
            TextBlock_Status.Text = "先指定 MI 实例目录。";
            return;
        }

        string? zip = await FileDialogHelper.PickSingleFileAsync(XamlRoot, ("压缩包", ".zip"), ("所有文件", ".*"));

        if (string.IsNullOrWhiteSpace(zip))
        {
            return;
        }

        try
        {
            string mods = XxmiLocator.ModsDirectory(_instance);
            Directory.CreateDirectory(mods);
            string imported = XxmiModManager.ImportZip(mods, zip);
            LoadMods();
            TextBlock_Status.Text = $"已导入并解压：{Path.GetFileName(imported)}";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "导入失败：" + ex.Message;
        }
    }

    private void ToggleSwitch_Mod_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loadingMods || sender is not FrameworkElement { DataContext: XxmiModItem item } element || element is not ToggleSwitch toggle)
        {
            return;
        }

        try
        {
            string path = XxmiModManager.SetEnabled(item.Path, toggle.IsOn);
            item.Path = path;
            LoadMods();
            TextBlock_Status.Text = $"{item.Name} → {(toggle.IsOn ? "已启用" : "已禁用")}";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "切换失败：" + ex.Message;
            LoadMods();
        }
    }

    private void Button_OpenMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: XxmiModItem item })
        {
            OpenInExplorer(item.IsDirectory ? item.Path : Path.GetDirectoryName(item.Path));
        }
    }

    private async void Button_DeleteMod_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: XxmiModItem item })
        {
            return;
        }

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            Title = "删除这个 mod？",
            Content = item.Name + "（直接删掉，不进回收站）",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            XxmiModManager.Delete(item.Path);
            LoadMods();
            TextBlock_Status.Text = $"已删除：{item.Name}";
        }
        catch (Exception ex)
        {
            TextBlock_Status.Text = "删除失败：" + ex.Message;
        }
    }

    private void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open folder failed: {Path}", path);
        }
    }
}
