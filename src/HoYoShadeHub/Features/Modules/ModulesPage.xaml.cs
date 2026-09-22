using CommunityToolkit.Mvvm.ComponentModel;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Frameworks;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace HoYoShadeHub.Features.Modules;

/// <summary>「这个游戏用不用这个模块」的一行</summary>
public sealed partial class ModuleChoiceItem : ObservableObject
{
    public string Key { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    public string Detail { get; init; } = string.Empty;

    public string TagsText { get; init; } = string.Empty;

    /// <summary>没装 / 全局关掉 → 勾都点不了</summary>
    public bool CanUse { get; init; }

    public string MissingText { get; init; } = string.Empty;

    public Visibility MissingVisibility
        => string.IsNullOrWhiteSpace(MissingText) ? Visibility.Collapsed : Visibility.Visible;

    private bool _used;
    public bool Used
    {
        get => _used;
        set => SetProperty(ref _used, value);
    }
}

/// <summary>
/// 「模块」页（左侧一级导航）：只干一件事 —— **这个游戏用哪些模块**（可以多选）。
/// 下载 / 删除 / 全局开关在「全局插件 → 模块」里。
/// </summary>
public sealed partial class ModulesPage : PageBase
{
    private readonly ILogger<ModulesPage> _logger = AppConfig.GetLogger<ModulesPage>();
    private readonly ObservableCollection<ModuleChoiceItem> _items = [];

    private GameId? _gameId;
    private bool _loading;

    public ModulesPage()
    {
        InitializeComponent();
        ListView_Modules.ItemsSource = _items;
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _gameId = e.Parameter as GameId;

        if (_gameId is not null)
        {
            // 老配置「额外注入 DLL」搬进模块列表（一次性、按游戏）；OptiScaler 那边挪过来的 DLSS-NR 也接上
            ModuleRegistry.MigrateLegacyExtraInjectDll(_gameId.GameBiz);
            Features.OptiScaler.OptiScalerMigration.Run(_gameId);

            // 远端模块目录（catalog/modules.json）的缓存，打开页面就能用
            ModuleCatalogFile.Apply(Features.Plugins.RemoteCatalogService.ModulesCachePath);
        }

        Load();
    }

    private void Load()
    {
        _loading = true;
        try
        {
            _items.Clear();

            if (_gameId is null)
            {
                TextBlock_Count.Text = string.Empty;
                TextBlock_Status.Text = "先在上面选一个游戏。";
                return;
            }

            foreach (ModuleEntry entry in ModuleRegistry.List())
            {
                // 还没装的模块不在这一页出现（用户要求）—— 要装就去「全局插件 → 模块」
                if (string.IsNullOrWhiteSpace(entry.DllPath))
                {
                    continue;
                }

                _items.Add(new ModuleChoiceItem
                {
                    Key = entry.Key,
                    Name = entry.Name,
                    Description = entry.Description,
                    TagsText = entry.Tags is { Length: > 0 } ? string.Join(" · ", entry.Tags) : string.Empty,
                    Detail = $"注入：{entry.DllPath}",
                    CanUse = entry.GloballyEnabled,
                    Used = entry.GloballyEnabled && ModuleRegistry.IsUsed(_gameId, entry.Key),
                    MissingText = entry.GloballyEnabled ? string.Empty : "在「全局插件 → 模块」里被全局关掉了。",
                });
            }

            int used = _items.Count(i => i.Used);
            TextBlock_Count.Text = _items.Count == 0
                ? "还没有模块 —— 先到「全局插件 → 模块」里下载一个。"
                : $"{_items.Count} 个模块，这个游戏用 {used} 个。";

            TextBlock_Status.Text = _items.Count == 0
                ? string.Empty
                : "还要在「开始游戏」页勾上「启用模块」，启动游戏时才会注进去。";
        }
        finally
        {
            _loading = false;
        }
    }

    private void Button_Refresh_Click(object sender, RoutedEventArgs e) => Load();

    /// <summary>注意：这里用 Click 而不是 Toggled —— Toggled 写在 DataTemplate 里会让 WinUI 的
    /// XamlTypeInfo 生成器直接崩（MarkupCompilePass1 静默 exit 1）。Click 时 IsChecked 已经改好了。</summary>
    private void CheckBox_Module_Click(object sender, RoutedEventArgs e)
    {
        if (_loading || _gameId is null || sender is not FrameworkElement { DataContext: ModuleChoiceItem item })
        {
            return;
        }

        ModuleRegistry.SetUsed(_gameId, item.Key, item.Used);
        TextBlock_Status.Text = item.Used
            ? $"这个游戏会用：{item.Name}"
            : $"这个游戏不用：{item.Name}";
    }
}
