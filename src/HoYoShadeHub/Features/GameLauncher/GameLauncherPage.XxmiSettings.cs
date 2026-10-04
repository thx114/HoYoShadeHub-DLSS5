using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Helpers;
using System;
using System.IO;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

public sealed partial class GameLauncherPage
{
    private async void Button_XxmiLaunchSettings_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentGameId is not { } game) return;
        try
        {
            var mode = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            mode.Items.Add("手动模式：XXMI 注入器等待，Hub 启动游戏");
            mode.Items.Add("官方模式：XXMI 启动游戏，Hub 负责其他注入");
            mode.SelectedIndex = AppConfig.GetXxmiLaunchMode(game) == XxmiLaunchMode.Manual ? 0 : 1;
            var path = new TextBox
            {
                Header = "XXMI Launcher 位置（所有游戏共用）",
                Text = AppConfig.XxmiLauncherPath ?? string.Empty,
                PlaceholderText = "留空自动查找；选择 XXMI Launcher.exe",
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            var browse = new Button { Content = "浏览…" };
            browse.Click += async (_, _) =>
            {
                string? picked = await FileDialogHelper.PickSingleFileAsync(XamlRoot, ("XXMI Launcher", ".exe"));
                if (!string.IsNullOrWhiteSpace(picked)) path.Text = picked;
            };
            var error = new TextBlock { TextWrapping = TextWrapping.Wrap };
            var panel = new StackPanel { Spacing = 12, MinWidth = 460 };
            panel.Children.Add(new TextBlock { Text = "启动模式（仅当前游戏）" });
            panel.Children.Add(mode);
            panel.Children.Add(new TextBlock
            {
                Text = "两种模式均先关闭旧 XXMI 实例，再写配置。手动模式唤起注入器后等待约 1 秒，Hub 不会再次调用 XXMI 的启动游戏流程。",
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(path); panel.Children.Add(browse); panel.Children.Add(error);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot, Title = "XXMI 启动设置", Content = panel,
                PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary,
            };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                string selected = path.Text.Trim().Trim('"');
                if (selected.Length > 0 && (!File.Exists(selected) || !Path.GetExtension(selected).Equals(".exe", StringComparison.OrdinalIgnoreCase)))
                { error.Text = "请选择存在的 XXMI Launcher EXE，或留空使用自动查找。"; args.Cancel = true; return; }
                string? config = selected.Length > 0 ? XxmiInjector.FindConfigForLauncher(selected) : null;
                if (selected.Length > 0 && config is null)
                { error.Text = "所选 EXE 的安装目录中没有 XXMI Launcher Config.json，请选择正确的 XXMI 安装。"; args.Cancel = true; return; }
                AppConfig.SetXxmiLaunchMode(game, mode.SelectedIndex == 0 ? XxmiLaunchMode.Manual : XxmiLaunchMode.Official);
                AppConfig.XxmiLauncherPath = selected.Length == 0 ? null : Path.GetFullPath(selected);
                if (config is not null) AppConfig.XxmiRoot = Path.GetDirectoryName(config);
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            InAppToast.MainWindow?.Error("XXMI 设置", ex.Message, 10000);
        }
    }
}
