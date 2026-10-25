using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Extensions.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

internal static class NrVersionPicker
{
    public static void RefreshInline(DependencyObject root, string? selected)
    {
        if (root is ComboBox { Name: "ComboBox_NrVersion" } combo)
        {
            Fill(combo, selected);
            return;
        }
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            RefreshInline(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), selected);
    }

    public static void Fill(ComboBox combo, string? selected)
    {
        combo.Tag = true;
        try
        {
            var versions = NrRuntimeLibrary.SelectableVersions(new DllVersionStore(AppConfig.CacheRoot).ListVersions("dlssnr")
                .Where(v => File.Exists(Path.Combine(v.Directory, NrRuntimeLibrary.FileName)))
                .Select(v => v.Version), AppConfig.IsNrVersionEnabled).ToList();
            combo.Items.Clear();
            combo.Items.Add(new ComboBoxItem { Content = "NR：默认" });
            foreach (var version in versions) combo.Items.Add(new ComboBoxItem { Content = "NR：" + version, Tag = version });
            int index = selected is null ? 0 : versions.FindIndex(v => v.Equals(selected, StringComparison.OrdinalIgnoreCase)) + 1;
            if (selected is not null && index == 0)
            {
                combo.Items.Add(new ComboBoxItem { Content = "NR：" + selected + "（不可选）", Tag = selected, IsEnabled = false });
                index = combo.Items.Count - 1;
            }
            combo.SelectedIndex = index;
        }
        finally { combo.Tag = false; }
    }

    public static async Task<(bool Apply, string? Version)> ShowAsync(XamlRoot root, string? selected, string? currentPath)
    {
        RemoteCatalogService.ApplyDllFingerprints();
        var versions = NrRuntimeLibrary.SelectableVersions(new DllVersionStore(AppConfig.CacheRoot).ListVersions("dlssnr")
            .Where(v => File.Exists(Path.Combine(v.Directory, NrRuntimeLibrary.FileName)))
            .Select(v => v.Version), AppConfig.IsNrVersionEnabled).ToList();
        var labels = new List<string> { "默认（插件 / 构建自带）" };
        labels.AddRange(versions);
        var combo = new ComboBox { ItemsSource = labels, MinWidth = 280,
            SelectedIndex = selected is null ? 0 : versions.FindIndex(v => v.Equals(selected, StringComparison.OrdinalIgnoreCase)) is int index && index >= 0 ? index + 1 : -1 };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = "仅对当前游戏生效；重启游戏后加载所选 NR。", TextWrapping = TextWrapping.Wrap });
        if (currentPath is not null && File.Exists(currentPath))
        {
            var family = DllComponentCatalog.FamilyOf("dlssnr");
            var installed = DllInstaller.Scan(Path.GetDirectoryName(currentPath)).FirstOrDefault(d => d.FileName.Equals(NrRuntimeLibrary.FileName, StringComparison.OrdinalIgnoreCase));
            if (family is not null && installed is not null)
            {
                var probe = DllVariantResolver.Identify(new DllVersionStore(AppConfig.CacheRoot), family, installed, null, 0);
                panel.Children.Add(new TextBlock { Text = "当前文件：" + probe.Display + (probe.Known ? "" : "（变体未确认）"), TextWrapping = TextWrapping.Wrap });
            }
        }
        panel.Children.Add(combo);
        if (selected is not null && !versions.Contains(selected, StringComparer.OrdinalIgnoreCase))
            panel.Children.Add(new TextBlock { Text = "当前选择：" + selected + "（已从可选列表关闭或归档缺失）。现有选择保留；可启用此版本，或在此改选其他版本。", TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { XamlRoot = root, Title = "选择这个游戏的 NR 版本", Content = panel,
            PrimaryButtonText = "应用", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = combo.SelectedIndex >= 0 };
        combo.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = combo.SelectedIndex >= 0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return (false, null);
        return (true, combo.SelectedIndex > 0 ? versions[combo.SelectedIndex - 1] : null);
    }
}
