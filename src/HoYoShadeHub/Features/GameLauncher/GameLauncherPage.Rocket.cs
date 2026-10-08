using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
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
            if (!result.Success)
            {
                InAppToast.MainWindow?.Error("Rocket 联动准备失败", result.Message, 12000);
                return;
            }
            // Reflect the mandatory bridge association without leaving the UI out of sync.
            UseModules = AppConfig.GetUseModulesLaunchOption(game);
            // Waiting is set only after the chain AND Rocket configuration succeed.
            IsRocketMode = true;
            GameState = GameState.StartGame;
            _logger.LogInformation("Rocket 模式已准备（不启动游戏、不启用任何 Hub 注入器）：{Status}", result.Message);
            InAppToast.MainWindow?.Information(RocketIntegration.WaitingText, result.Message, 12000);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Prepare Rocket external launch");
            InAppToast.MainWindow?.Error("Rocket 联动准备失败", ex.Message, 12000);
        }
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
            panel.Children.Add(new TextBlock { Text="只修改原神的额外 DLL 路径和启用开关，不修改 Rocket 的 GIMI、授权、网络或抓包设置。已运行的 Rocket 可能需要重新读取配置；Hub 不会替你关闭或重启它。", TextWrapping=TextWrapping.Wrap, MaxWidth=500 });
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
