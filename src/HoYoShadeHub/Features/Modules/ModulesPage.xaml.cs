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

    /// <summary>这个模块装着的所有版本（新 → 旧）；只有走 Release 的内置模块才有</summary>
    public IReadOnlyList<string> Versions { get; init; } = [];

    /// <summary>装了版本就给下拉（只有一个也显示 —— 让人看得见当前注入的是哪个版本）</summary>
    public Visibility VersionPickerVisibility =>
        Versions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>版本下拉旁边的说明（只有一个版本时不再说那句「去全局插件换版本」）</summary>
    public string VersionHint => Versions.Count > 1
        ? "这个游戏注入哪一个版本（多个版本可以同时装着）"
        : string.Empty;

    /// <summary>只有多个版本时才显示上面那句</summary>
    public Visibility VersionHintVisibility =>
        Versions.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>版本变化回调（下拉走 TwoWay 绑定，改下面的属性就等于用户选了）</summary>
    public Action<ModuleChoiceItem, string?>? OnVersionChanged { get; init; }

    private string? _selectedVersion;

    /// <summary>这个游戏用哪个版本</summary>
    public string? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (SetProperty(ref _selectedVersion, value))
            {
                OnVersionChanged?.Invoke(this, value);
            }
        }
    }

    // ===================== 注入时机（按模块；和「全局插件 → 模块」共用同一个值） =====================

    /// <summary>留空时输入框显示的占位文案</summary>
    public string InjectDelayPlaceholder { get; init; } = "跟随全局";

    /// <summary>右边那句说明</summary>
    public string InjectDelayHint { get; init; } = string.Empty;

    /// <summary>注入时机变化回调（输入框走 TwoWay 绑定）</summary>
    public Action<ModuleChoiceItem, int?>? OnInjectDelayChanged { get; init; }

    private double _injectDelayValue = double.NaN;

    /// <summary>NaN = 留空 = 跟随全局默认</summary>
    public double InjectDelayValue
    {
        get => _injectDelayValue;
        set
        {
            if (SetProperty(ref _injectDelayValue, value))
            {
                int? seconds = double.IsNaN(value)
                    ? null
                    : (int)Math.Clamp(Math.Round(value), 0, AppConfig.MaxInjectionWarmupSeconds);
                OnInjectDelayChanged?.Invoke(this, seconds);
            }
        }
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

                IReadOnlyList<string> versions = entry.Definition is { } definition
                    ? ModuleRegistry.InstalledTags(definition)
                    : [];

                // 这个游戏选过的版本；选的版本已经被删了就退回最新那份
                string? selected = entry.Definition is { } def
                    ? AppConfig.GetModuleVersion(_gameId, def.Id)
                    : null;

                if (selected is null || !versions.Contains(selected, StringComparer.OrdinalIgnoreCase))
                {
                    selected = versions.FirstOrDefault();
                }

                // 注入时机（按模块，和「全局插件 → 模块」共用）；没单独设就跟随全局默认
                int? injectDelay = AppConfig.GetModuleInjectDelaySeconds(entry.Key);
                int globalDefault = AppConfig.GetDefaultInjectionDelaySeconds(_gameId.GameBiz);

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
                    Versions = versions,
                    // 回调要在 SelectedVersion 之前挂好（初始赋值会触发它，Load 里用 _loading 挡住）
                    OnVersionChanged = OnModuleVersionChanged,
                    SelectedVersion = selected,
                    InjectDelayPlaceholder = $"跟随全局（{globalDefault} 秒）",
                    OnInjectDelayChanged = OnModuleInjectDelayChanged,
                    InjectDelayHint = injectDelay is null
                        ? $"跟随全局默认（{globalDefault} 秒）"
                        : injectDelay <= 0 ? "立即注入" : $"等 {injectDelay} 秒",
                    InjectDelayValue = injectDelay is int delay ? delay : double.NaN,
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

    /// <summary>这个游戏换模块版本（下拉是 TwoWay 绑定，改 SelectedVersion 就进这里）</summary>
    private void OnModuleVersionChanged(ModuleChoiceItem item, string? version)
    {
        if (_loading || _gameId is null || string.IsNullOrWhiteSpace(item.Key))
        {
            return;
        }

        // 下拉是 TwoWay 绑定：刷新列表时重建卡片，ComboBox 的 ItemsSource 被换掉那一刻
        // SelectedItem 会瞬时变成 null 并写回来 —— 那不是用户的选择，但会把用户刚选的版本清掉。
        // 值没变就什么都不做（同样的自反馈也会在插件页那边造成刷新循环 + 原生崩溃）。
        string? current = AppConfig.GetModuleVersion(_gameId, item.Key);
        if (string.Equals(current ?? string.Empty, version ?? string.Empty, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        AppConfig.SetModuleVersion(_gameId, item.Key, version);
        TextBlock_Status.Text = string.IsNullOrWhiteSpace(version)
            ? $"「{item.Name}」改用默认版本（模块目录里最新装的那份）。"
            : $"「{item.Name}」这个游戏改用 {version}。";
    }

    /// <summary>某个模块的「注入时机」改了（按模块存，和「全局插件 → 模块」共用）</summary>
    private void OnModuleInjectDelayChanged(ModuleChoiceItem item, int? seconds)
    {
        if (_loading || string.IsNullOrWhiteSpace(item.Key))
        {
            return;
        }

        AppConfig.SetModuleInjectDelaySeconds(item.Key, seconds);
        TextBlock_Status.Text = seconds is null
            ? $"「{item.Name}」的注入时机：跟随全局默认。"
            : seconds <= 0
                ? $"「{item.Name}」：立即注入（不等）。"
                : $"「{item.Name}」：等游戏起来 {seconds} 秒后再注入。";
    }

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
