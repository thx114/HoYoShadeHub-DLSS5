using CommunityToolkit.Mvvm.ComponentModel;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// DLSS5 兼容性检测的弹出窗口：15 条结果 + 每条右侧的「修复」。
///
/// <para>
/// 检测本身在 <see cref="Dlss5CompatibilityCheck"/> 里（纯读盘），这里只管显示和点按钮。
/// </para>
/// </summary>
[INotifyPropertyChanged]
public sealed partial class Dlss5CompatDialog : ContentDialog
{
    private readonly ILogger<Dlss5CompatDialog> _logger = AppConfig.GetLogger<Dlss5CompatDialog>();
    private readonly Dlss5CompatContext _context;

    private bool _isBusy;

    public Dlss5CompatDialog(Dlss5CompatContext context)
    {
        _context = context;
        InitializeComponent();
        _ = RunAsync();
    }

    public ObservableCollection<Dlss5CompatItem> Items { get; } = [];

    public bool IsNotBusy => !_isBusy;

    /// <summary>有没有「能修但还没修」的项</summary>
    public bool CanFixAll => !_isBusy && Items.Any(i => i.CanFix);

    public string TargetText
    {
        get
        {
            string game = _context.Game is { } entry ? entry.DisplayName : "（没有选中游戏）";
            string shade = _context.ShadeHost?.RootPath ?? "（没找到 HoYoShade）";
            return $"游戏：{game}　　HoYoShade：{shade}";
        }
    }

    private async Task RunAsync()
    {
        if (_isBusy)
        {
            return;
        }

        _isBusy = true;
        NotifyBusy();

        try
        {
            Items.Clear();
            TextBlock_Summary.Text = "正在检测";

            List<Dlss5CompatItem> results = await Dlss5CompatibilityCheck.RunAsync(_context);

            // 排序：红（Error）> 黄（Warning）> 提示（Info）> 绿（Ok）；同级保持原编号顺序
            foreach (Dlss5CompatItem item in results
                         .OrderByDescending(i => (int)i.Level)
                         .ThenBy(i => i.Index))
            {
                Items.Add(item);
            }

            UpdateSummary();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DLSS5 compatibility check");
            ShowStatus("检测失败", ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            _isBusy = false;
            NotifyBusy();
        }
    }

    private void UpdateSummary()
    {
        int problems = Items.Count(i => i.Level == Dlss5CheckLevel.Error);
        int warnings = Items.Count(i => i.Level == Dlss5CheckLevel.Warning);
        int fixable = Items.Count(i => i.CanFix);

        TextBlock_Summary.Text = problems == 0 && warnings == 0
            ? "全部通过，可以放心开 DLSS5。"
            : $"问题 {problems} 项、注意 {warnings} 项" + (fixable > 0 ? $"（其中 {fixable} 项可一键修复）" : string.Empty);

        TextBlock_Summary.Foreground = problems > 0
            ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
            : warnings > 0
                ? (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"]
                : (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorSuccessBrush"];
    }

    private void NotifyBusy()
    {
        OnPropertyChanged(nameof(IsNotBusy));
        OnPropertyChanged(nameof(CanFixAll));
    }

    private async void Button_Recheck_Click(object sender, RoutedEventArgs e)
    {
        foreach (Dlss5CompatItem item in Items)
        {
            item.FixResult = string.Empty;
        }

        HideStatus();
        await RunAsync();
    }

    private async void Button_Fix_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: Dlss5CompatItem item })
        {
            return;
        }

        await RunFixAsync(item);
    }

    /// <summary>一键修复：按编号顺序（7  15）修  前面的修好了，后面那条的判断才准</summary>
    private async void Button_FixAll_Click(object sender, RoutedEventArgs e)
    {
        List<Dlss5CompatItem> targets = [.. Items.Where(i => i.CanFix).OrderBy(i => i.Index)];

        if (targets.Count == 0)
        {
            ShowStatus("没有要修的", "15 项里没有可自动修复的问题。", InfoBarSeverity.Informational);
            return;
        }

        var done = new List<string>();
        var skipped = new List<string>();

        foreach (Dlss5CompatItem item in targets)
        {
            string? result = await RunFixAsync(item, silent: true);

            if (result is null)
            {
                skipped.Add(item.HeaderText);
            }
            else
            {
                done.Add(item.HeaderText);
            }
        }

        await RunAsync();

        ShowStatus("一键修复完成",
            $"修了 {done.Count} 项：{string.Join("、", done)}" +
            (skipped.Count > 0 ? $"\n需要你手动处理的 {skipped.Count} 项：{string.Join("、", skipped)}" : string.Empty),
            skipped.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
    }

    /// <summary>修一条；返回文案（null = 这条修不了，需要用户自己动手）</summary>
    private async Task<string?> RunFixAsync(Dlss5CompatItem item, bool silent = false)
    {
        if (item.Fix is not { } fix || item.IsFixing)
        {
            return null;
        }

        item.IsFixing = true;
        item.FixResult = string.Empty;

        try
        {
            string? result = await fix();
            item.FixResult = result ?? "已处理。";

            if (!silent)
            {
                ShowStatus(item.HeaderText, item.FixResult, InfoBarSeverity.Success);
            }

            return item.FixResult;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DLSS5 fix failed: {Item}", item.HeaderText);
            item.FixResult = "修复出错：" + ex.Message;

            if (!silent)
            {
                ShowStatus("修复出错", ex.Message, InfoBarSeverity.Error);
            }

            return item.FixResult;
        }
        finally
        {
            item.IsFixing = false;
            NotifyBusy();
        }
    }

    private void ShowStatus(string title, string message, InfoBarSeverity severity)
    {
        InfoBar_Status.Title = title;
        InfoBar_Status.Message = message;
        InfoBar_Status.Severity = severity;
        InfoBar_Status.IsOpen = true;
    }

    private void HideStatus() => InfoBar_Status.IsOpen = false;
}
