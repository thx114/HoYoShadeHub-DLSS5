using CommunityToolkit.Mvvm.ComponentModel;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Frameworks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace HoYoShadeHub.Features.OptiScaler;

/// <summary>OptiScaler 构建列表里的一行（单选）</summary>
public sealed partial class OptiScalerBuildItemViewModel : ObservableObject
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public string TagsText { get; init; } = string.Empty;

    public string? DllPath { get; init; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public Visibility DllMissingVisibility
        => string.IsNullOrWhiteSpace(DllPath) ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// 「OptiScaler」页（左侧一级导航）：**只选这个游戏用哪个构建**（单选）。
///
/// <para>
/// 总开关（全局开/关）和下载 / 删除都在「全局插件 → OptiScaler」里 —— 跟插件、模块一套风格。
/// 没给这个游戏选过构建就不会注入它。
/// </para>
/// </summary>
public sealed partial class OptiScalerPage : PageBase
{
    private readonly ILogger<OptiScalerPage> _logger = AppConfig.GetLogger<OptiScalerPage>();
    private readonly ObservableCollection<OptiScalerBuildItemViewModel> _items = [];

    private GameId? _gameId;
    private bool _loading;

    public OptiScalerPage()
    {
        InitializeComponent();
        ListView_Builds.ItemsSource = _items;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _gameId = e.Parameter as GameId;

        if (_gameId is not null)
        {
            OptiScalerMigration.Run(_gameId);
        }

        Load();
    }

    private OptiScalerLibrary? CreateLibrary()
    {
        string root = AppConfig.OptiScalerRootPath;
        return root.Length == 0 ? null : new OptiScalerLibrary(root);
    }

    private void Load()
    {
        _loading = true;
        try
        {
            _items.Clear();

            string gameName = _gameId?.GameBiz.Value ?? string.Empty;
            TextBlock_Game.Text = string.IsNullOrWhiteSpace(gameName) ? "（没选游戏）" : $"这个游戏：{gameName}";

            OptiScalerLibrary? library = CreateLibrary();
            string? selectedId = _gameId is null ? null : AppConfig.GetSelectedOptiScalerId(_gameId);

            if (library is not null)
            {
                foreach (OptiScalerBuild build in library.List())
                {
                    _items.Add(new OptiScalerBuildItemViewModel
                    {
                        Id = build.Id,
                        Title = $"{build.SourceId} · {build.Version}",
                        Detail = string.IsNullOrWhiteSpace(build.DllPath)
                            ? $"目录：{build.Directory}"
                            : $"注入：{build.DllPath}",
                        DllPath = build.DllPath,
                        IsSelected = string.Equals(build.Id, selectedId, StringComparison.OrdinalIgnoreCase),
                    });
                }
            }

            TextBlock_Count.Text = _items.Count == 0
                ? "一个构建都没有 —— 先到左下角「全局插件 → OptiScaler」里下一个。"
                : $"{_items.Count} 个构建；已装 {_items.Count(i => !string.IsNullOrWhiteSpace(i.DllPath))} 个。";

            // 没选过就不自动替他选：没选 = 这个游戏不注入
            ListView_Builds.SelectedItem = _items.FirstOrDefault(i => i.IsSelected);

            TextBlock_Status.Text = ListView_Builds.SelectedItem is null
                ? "这个游戏还没选构建 —— 上面选一个才会注入。"
                : $"这个游戏用：{((OptiScalerBuildItemViewModel)ListView_Builds.SelectedItem).Title}";
        }
        finally
        {
            _loading = false;
        }
    }

    private void Button_Refresh_Click(object sender, RoutedEventArgs e) => Load();

    private void ListView_Builds_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _gameId is null)
        {
            return;
        }

        foreach (OptiScalerBuildItemViewModel item in _items)
        {
            item.IsSelected = ReferenceEquals(item, ListView_Builds.SelectedItem);
        }

        if (ListView_Builds.SelectedItem is not OptiScalerBuildItemViewModel selected)
        {
            return;
        }

        AppConfig.SetSelectedOptiScalerId(_gameId, selected.Id);

        // 在这里选构建 = 明确要用它 —— 顺手把启动选项里的「启用OptiScaler」也勾上
        AppConfig.SetUseOptiScalerLaunchOption(_gameId, true);

        TextBlock_Status.Text = $"这个游戏改用：{selected.Title}";

        // 「全局插件 → OptiScaler」那边也用 state.json 里那个「当前构建」，同步一下省得两边不一致
        try
        {
            CreateLibrary()?.Select(selected.Id);
        }
        catch
        {
            // 同步失败不影响这边的选择
        }
    }

    private void Button_ClearChoice_Click(object sender, RoutedEventArgs e)
    {
        if (_gameId is null)
        {
            return;
        }

        AppConfig.SetSelectedOptiScalerId(_gameId, null);
        AppConfig.SetUseOptiScalerLaunchOption(_gameId, false);
        _loading = true;
        try
        {
            ListView_Builds.SelectedItem = null;
            foreach (OptiScalerBuildItemViewModel item in _items)
            {
                item.IsSelected = false;
            }
        }
        finally
        {
            _loading = false;
        }

        TextBlock_Status.Text = "这个游戏不再注入 OptiScaler。";
    }

    private void Button_OpenLibrary_Click(object sender, RoutedEventArgs e)
        => OpenFolder(AppConfig.OptiScalerRootPath);

    private void OpenFolder(string? path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Open OptiScaler folder {Path}", path);
        }
    }
}
