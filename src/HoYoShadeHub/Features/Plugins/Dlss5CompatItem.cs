using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>检测项的宿主页面（每游戏插件页 / 全局插件页都实现它）</summary>
public interface IDlss5CompatHost
{
    /// <summary>弹兼容性检测窗口（右上角「DLSS5 兼容性检测」按钮）</summary>
    Task ShowDlss5CompatibilityAsync();
}

/// <summary>兼容性检测里一条的严重程度</summary>
public enum Dlss5CheckLevel
{
    Ok = 0,
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// DLSS5 兼容性检测里的一条结果。
/// 15 条里有 8 条（7/9/10/11/12/13/14/15）带自动修复：检测到问题时右侧出现「修复」按钮。
/// </summary>
public sealed partial class Dlss5CompatItem : ObservableObject
{
    public Dlss5CompatItem(int index, string group, string title)
    {
        Index = index;
        Group = group;
        Title = title;
    }

    /// <summary>1~15，按用户列的顺序</summary>
    public int Index { get; }

    /// <summary>分组：显卡 / 启动器 / 游戏目录 / XXMI</summary>
    public string Group { get; }

    public string Title { get; }

    /// <summary>标题行：`1. 显卡是否超频`</summary>
    public string HeaderText => $"{Index}. {Title}";

    public string GroupText => Group;

    [ObservableProperty]
    private string message = "正在检测";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusGlyph))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private Dlss5CheckLevel level = Dlss5CheckLevel.Info;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FixVisibility))]
    private bool canFix;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FixVisibility))]
    private bool isFixing;

    [ObservableProperty]
    private string fixLabel = "修复";

    /// <summary>检测到问题（黄/红）且这一条有自动修复时才显示按钮</summary>
    public Visibility FixVisibility => CanFix && !IsFixing ? Visibility.Visible : Visibility.Collapsed;

    public string StatusGlyph => Level switch
    {
        Dlss5CheckLevel.Ok => "\uE73E",      // CheckMark
        Dlss5CheckLevel.Warning => "\uE7BA", // Warning
        Dlss5CheckLevel.Error => "\uEA39",   // ErrorBadge
        _ => "\uE946",                        // Info
    };

    public string StatusText => Level switch
    {
        Dlss5CheckLevel.Ok => "正常",
        Dlss5CheckLevel.Warning => "注意",
        Dlss5CheckLevel.Error => "问题",
        _ => "提示",
    };

    public Brush StatusBrush
    {
        get
        {
            string key = Level switch
            {
                Dlss5CheckLevel.Ok => "SystemFillColorSuccessBrush",
                Dlss5CheckLevel.Warning => "SystemFillColorCautionBrush",
                Dlss5CheckLevel.Error => "SystemFillColorCriticalBrush",
                _ => "TextFillColorSecondaryBrush",
            };

            try
            {
                if (Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush brush)
                {
                    return brush;
                }
            }
            catch
            {
                // 主题资源拿不到就退回灰色，不能让检测结果画不出来
            }

            return new SolidColorBrush(Microsoft.UI.Colors.Gray);
        }
    }

    /// <summary>点「修复」时跑的东西（返回一句补充说明，可为空）</summary>
    internal Func<Task<string?>>? Fix { get; set; }

    /// <summary>修复完显示的结果文案</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FixResultVisibility))]
    private string fixResult = string.Empty;

    public Visibility FixResultVisibility => string.IsNullOrWhiteSpace(FixResult) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>修复中显示转圈</summary>
    public Visibility FixingVisibility => IsFixing ? Visibility.Visible : Visibility.Collapsed;

    partial void OnIsFixingChanged(bool value) => OnPropertyChanged(nameof(FixingVisibility));
}