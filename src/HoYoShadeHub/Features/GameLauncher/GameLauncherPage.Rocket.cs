using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

public sealed partial class GameLauncherPage
{
    private async Task PrepareRocketAsync()
    {
        IsRocketMode = false;
        if (CurrentGameId is not { } game) return;
        try
        {
            var result = await RocketLaunchPreparation.PrepareAsync(game, _currentGameEntry);
            // 不管找没找到 Rocket 都弹第三方加载器对话框：框里的 DLL 路径可以直接配 SSMT 等。
            await ShowThirdPartyLoaderDialogAsync(result.DllPaths);
            if (!result.Success)
            {
                _logger.LogWarning("Rocket 联动准备失败 ConfigPath={ConfigPath} BackupPath={BackupPath}: {Status}", result.ConfigPath, result.BackupPath, result.Message);
                InAppToast.MainWindow?.Error("Rocket 联动准备失败", result.Message, 12000);
                return;
            }
            // Reflect the mandatory bridge association without leaving the UI out of sync.
            UseModules = AppConfig.GetUseModulesLaunchOption(game);
            // Waiting is set only after the chain AND Rocket configuration succeed.
            IsRocketMode = true;
            GameState = GameState.StartGame;
            // 「等火箭启动」不是干等：外部启动哨兵（见 StartExternalGameWatch）会自己找到游戏进程，
            // 把状态切到「正在游戏中」并开这一局的日志快照
            _logger.LogInformation("Rocket 模式已准备（不启动游戏、不启用任何 Hub 注入器） ConfigPath={ConfigPath} BackupPath={BackupPath}: {Status}", result.ConfigPath, result.BackupPath, result.Message);
            InAppToast.MainWindow?.Information(RocketIntegration.WaitingText, result.Message, 12000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prepare Rocket external launch");
            InAppToast.MainWindow?.Error("Rocket 联动准备失败", ex.Message, 12000);
        }
    }

    /// <summary>第三方模组加载器对话框：一个显示当前 DLL 路径的输入框 + 一个复制图标按钮 + 使用提示。仅展示/复制，不做任何修改。</summary>
    private async Task ShowThirdPartyLoaderDialogAsync(IReadOnlyList<string>? dllPaths)
    {
        if (dllPaths is null) return;
        string text = dllPaths.Count > 0
            ? string.Join("|", dllPaths)
            : "(没有可显示的 DLL：请先在左侧「模块 / OptiScaler / HoYoShade」选好要加载的内容再准备)";
        var box = new TextBox { Text = text, MinWidth = 520 };
        var copy = new Button { Content = new FontIcon { Glyph = "\uE8C8", FontSize = 14 } };
        ToolTipService.SetToolTip(copy, "复制到剪贴板");
        copy.Click += async (_, _) =>
        {
            try
            {
                ClipboardHelper.SetText(box.Text);
                copy.Content = new FontIcon { Glyph = "\uE8F7", FontSize = 14 };
                await Task.Delay(1500);
                copy.Content = new FontIcon { Glyph = "\uE8C8", FontSize = 14 };
            }
            catch (Exception copyEx) { _logger.LogWarning(copyEx, "Copy third-party loader DLL path"); }
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(box);
        row.Children.Add(copy);
        var hint = new TextBlock
        {
            Text = dllPaths.Count > 1
                ? "请把这些dll按顺序复制到你所用的第三方启动器的额外dll加载位置，它们会加载目前配置的所有东西。可能有一些无法和reshade或opt兼容的需要你手动在第三方启动器排查.."
                : "请把此dll复制到你所用的第三方启动器的额外dll加载位置，此dll会加载目前配置的所有东西。可能有一些无法和reshade或opt兼容的需要你手动在第三方启动器排查..",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 620,
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(row);
        panel.Children.Add(hint);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "第三方模组加载器（Rocket / SSMT 等）",
            Content = panel,
            CloseButtonText = "关闭",
        };
        await dialog.ShowAsync();
    }

    private async void Button_RocketSettings_Click(object sender, RoutedEventArgs e)
    {
        if (UseXxmiInject) return;
        try
        {
            var path = new TextBox
            {
                Header="Rocket 程序或 backing/config.ini（所有游戏共用）",
                Text=RocketIntegration.FindConfigPath(AppConfig.RocketConfigPath) ?? AppConfig.RocketConfigPath ?? "",
                PlaceholderText="选择 Rocket EXE 或 config.ini；程序改名不影响识别",
                MinWidth=450
            };
            var browse = new Button { Content="浏览…" };
            browse.Click += async (_, _) =>
            {
                string? selected=await FileDialogHelper.PickSingleFileAsync(XamlRoot, ("Rocket 程序", ".exe"), ("Rocket 配置", ".ini"));
                if (!string.IsNullOrWhiteSpace(selected)) path.Text=selected;
            };
            var error=new TextBlock { TextWrapping=TextWrapping.Wrap };
            var panel=new StackPanel { Spacing=12 };
            panel.Children.Add(path);panel.Children.Add(browse);
            panel.Children.Add(new TextBlock { Text="只写 Rocket 的第三方 DLL 位置：原神写 FSR 桥 DLL 路径和启用开关；其他游戏（星铁/绝区零/鸣潮/终末地）写「<游戏名>插件DLL列表」，按顺序注入多份 DLL（OptiScaler 在前、ReShade 在后）。不修改 Rocket 的主 DLL、授权、网络或抓包设置。已运行的 Rocket 可能需要重新读取配置；Hub 不会替你关闭或重启它。", TextWrapping=TextWrapping.Wrap, MaxWidth=500 });
            panel.Children.Add(error);
            var dialog=new ContentDialog { XamlRoot=XamlRoot, Title="Rocket 联动设置",Content=panel,
                PrimaryButtonText="保存",CloseButtonText="取消",DefaultButton=ContentDialogButton.Primary };
            dialog.PrimaryButtonClick += (_, args) =>
            {
                string selected=path.Text.Trim().Trim('"');
                try
                {
                    string config=selected.EndsWith(".exe",StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(selected))!,"backing","config.ini")
                        : Path.GetFullPath(selected);
                    if (!File.Exists(config)) throw new FileNotFoundException("所选位置没有 Rocket backing/config.ini。");
                    AppConfig.RocketConfigPath=config;
                    IsRocketMode=false;
                }
                catch(Exception ex) { error.Text=ex.Message;args.Cancel=true; }
            };
            await dialog.ShowAsync();
        }
        catch(Exception ex) { InAppToast.MainWindow?.Error("Rocket 设置",ex.Message,10000); }
    }
}
