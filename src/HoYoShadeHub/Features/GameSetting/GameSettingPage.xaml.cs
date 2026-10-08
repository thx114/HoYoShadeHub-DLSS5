using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Display;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using HoYoShadeHub.Core;
using HoYoShadeHub.Features.GameLauncher;
using HoYoShadeHub.Features.GameSelector;
using HoYoShadeHub.Frameworks;
using HoYoShadeHub.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;


namespace HoYoShadeHub.Features.GameSetting;

public sealed partial class GameSettingPage : PageBase
{

    private readonly ILogger<GameSettingPage> _logger = AppConfig.GetLogger<GameSettingPage>();

    private readonly GameLauncherService _gameLauncherService = AppConfig.GetService<GameLauncherService>();



    public GameSettingPage()
    {
        this.InitializeComponent();
    }




    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Image_Emoji.Source = CurrentGameBiz.ToGame().Value switch
        {
            GameBiz.bh3 => new BitmapImage(AppConfig.EmojiAI),
            GameBiz.hk4e => new BitmapImage(AppConfig.EmojiPaimon),
            GameBiz.hkrpg => new BitmapImage(AppConfig.EmojiPom),
            GameBiz.nap => new BitmapImage(AppConfig.EmojiBangboo),
            _ => null,
        };
        if (CurrentGameId.GameBiz == GameBiz.bh3_global)
        {
            CurrentGameBiz = CurrentGameId.Id switch
            {
                "g0mMIvshDb" => GameBiz.bh3_jp,
                "uxB4MC7nzC" => GameBiz.bh3_kr,
                "bxPTXSET5t" => GameBiz.bh3_os,
                "wkE5P5WsIf" => GameBiz.bh3_asia,
                _ => GameBiz.bh3_global,
            };
        }
    }


    protected override async void OnLoaded()
    {
        InitializeResolutionItem();
        await InitializeGameSettingAsync();
    }


    protected override void OnUnloaded()
    {
        if (_displayInformation is not null)
        {
            _displayInformation.AdvancedColorInfoChanged -= _displayInformation_AdvancedColorInfoChanged;
            _displayInformation.Dispose();
            _displayInformation = null!;
        }
    }


    public bool IsBaseSettingEnable { get; set => SetProperty(ref field, value); }

    public bool IsLanguageSettingEnable { get; set => SetProperty(ref field, value); }

    public bool IsGraphicsSettingEnable { get; set => SetProperty(ref field, value); }

    public bool IsApplyButtonEnable { get; set => SetProperty(ref field, value); }

    public string ErrorMessage { get; set => SetProperty(ref field, value); } = Lang.GameSettingPage_SettingNotEffect; // 游戏运行时应用的设置无法生效





    public string? StartArgument
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.SetStartArgument(CurrentGameBiz, value);
            }
        }
    }


    public bool EnableFullScreen
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                IsApplyButtonEnable = true;
            }
        }
    }


    public bool UsePopupWindow
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                IsApplyButtonEnable = true;
            }
        }
    }



    public bool EnableCustomResolution { get; set => SetProperty(ref field, value); }


    public int ResolutionWidth
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                IsApplyButtonEnable = true;
            }
        }
    }


    public int ResolutionHeight
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                IsApplyButtonEnable = true;
            }
        }
    }


    public int LanguageIndex
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                IsApplyButtonEnable = true;
            }
        }
    }


    public int StarRailFpsIndex
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                IsApplyButtonEnable = true;
            }
        }
    }


    public bool EnableGenshinHDR
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                IsApplyButtonEnable = true;
            }
        }
    }


    /// <summary>原神帧率解锁目标值，范围 60-1000。按游戏记，开关在启动页启动选项里（默认 119 = 安全上限）</summary>
    private double _fpsUnlockTargetValue = AppConfig.FpsUnlockWarnThreshold;
    public double FpsUnlockTargetValue
    {
        get => _fpsUnlockTargetValue;
        set
        {
            double clamped = Math.Clamp(Math.Round(value), 60, 1000);
            if (SetProperty(ref _fpsUnlockTargetValue, clamped))
            {
                IsApplyButtonEnable = true;
            }
        }
    }

    // ==================== 帧率解锁数据版本（设置页区块） ====================

    /// <summary>程序填充下拉框时抑制 SelectionChanged（否则会触发一次「切换版本」）</summary>
    private bool _populatingFpsUnlockVersions;

    /// <summary>下拉框条目：一个本地数据版本快照</summary>
    private sealed record FpsUnlockVersionItem(string Hash, string Label)
    {
        public override string ToString() => Label;
    }

    /// <summary>刷新数据版本下拉框 + 同步状态文案 + 版本不匹配警告</summary>
    private async Task RefreshFpsUnlockVersionInfoAsync()
    {
        if (ComboBox_FpsUnlockVersion is null)
        {
            return;
        }

        IReadOnlyList<FpsUnlockDataService.VersionInfo> versions = FpsUnlockDataService.ListVersions();

        _populatingFpsUnlockVersions = true;
        ComboBox_FpsUnlockVersion.Items.Clear();
        foreach (FpsUnlockDataService.VersionInfo v in versions)
        {
            string label = v.ShortHash;
            if (v.UpdatedAt is { } t)
            {
                label += $" · {t.LocalDateTime:yyyy-MM-dd HH:mm}";
            }

            if (v.IsActive)
            {
                label += "（当前）";
            }

            ComboBox_FpsUnlockVersion.Items.Add(new FpsUnlockVersionItem(v.Hash, label));
        }

        int activeIndex = versions.ToList().FindIndex(v => v.IsActive);
        ComboBox_FpsUnlockVersion.SelectedIndex = activeIndex >= 0 ? activeIndex : 0;
        _populatingFpsUnlockVersions = false;

        Version? gameVersion = await _gameLauncherService.GetLocalGameVersionAsync(CurrentGameId);
        string? synced = AppConfig.GetFpsUnlockDataVersion(CurrentGameId);
        FpsUnlockDataService.VersionInfo? active = FpsUnlockDataService.GetActiveVersion();

        string state;
        if (versions.Count == 0)
        {
            state = "还没有同步过解锁数据：游戏启动时会自动从上游拉取（失败则用内置兜底特征）。";
        }
        else
        {
            string dataTime = active?.UpdatedAt is { } at
                ? $"数据更新时间 {at.LocalDateTime:yyyy-MM-dd HH:mm}"
                : "数据更新时间未知";
            string gameText = gameVersion?.ToString() ?? "读不到（config.ini 缺失）";
            string syncedText = string.IsNullOrWhiteSpace(synced) ? "尚未按游戏版本同步" : $"已同步到游戏版本 {synced}";
            state = $"当前游戏版本 {gameText}；{syncedText}；{dataTime}。";
        }

        TextBlock_FpsUnlockSyncState.Text = state;

        // 同步过的游戏版本和现在对不上 → 数据大概率滞后（游戏更新了 / 上游还没适配）
        InfoBar_FpsUnlockMismatch.IsOpen = gameVersion is not null
                                            && !string.IsNullOrWhiteSpace(synced)
                                            && !string.Equals(synced, gameVersion.ToString(), StringComparison.Ordinal);
    }

    private async void ComboBox_FpsUnlockVersion_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_populatingFpsUnlockVersions || ComboBox_FpsUnlockVersion.SelectedItem is not FpsUnlockVersionItem item)
        {
            return;
        }

        if (!FpsUnlockDataService.SetActiveVersion(item.Hash))
        {
            InAppToast.MainWindow?.Error("帧率解锁", "切换数据版本失败：快照文件不在了。", 6000);
            return;
        }

        // 手动钉住这个版本：把同步戳记成当前游戏版本，下次启动不会因为戳不符就重拉覆盖；
        // 24 小时后的后台检查/上游真出新版本时才会自动跟过去。
        Version? gameVersion = await _gameLauncherService.GetLocalGameVersionAsync(CurrentGameId);
        AppConfig.SetFpsUnlockDataVersion(CurrentGameId, gameVersion?.ToString() ?? string.Empty);
        AppConfig.SetFpsUnlockLastCheckTicks(CurrentGameId, DateTime.UtcNow.Ticks);

        await RefreshFpsUnlockVersionInfoAsync();
    }

    private async void Button_FpsUnlockCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        Button_FpsUnlockCheckUpdate.IsEnabled = false;
        try
        {
            Version? gameVersion = await _gameLauncherService.GetLocalGameVersionAsync(CurrentGameId);
            FpsUnlockDataService.UpdateResult result =
                await FpsUnlockDataService.CheckManuallyAsync(CurrentGameId, gameVersion?.ToString() ?? string.Empty);

            string message = result switch
            {
                FpsUnlockDataService.UpdateResult.Updated => "已拉到新的解锁数据并生效。",
                FpsUnlockDataService.UpdateResult.Unchanged => "上游还没有新数据（可能还没适配当前游戏版本，稍后再试）。",
                FpsUnlockDataService.UpdateResult.Failed => "拉取失败：连不上上游 GitHub，检查网络 / 代理。",
                _ => "本地还没有数据。",
            };
            InAppToast.MainWindow?.Information("帧率解锁", message, 8000);
            await RefreshFpsUnlockVersionInfoAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual FPS unlock data check failed");
            InAppToast.MainWindow?.Error("帧率解锁", "检查更新失败：" + ex.Message, 8000);
        }
        finally
        {
            Button_FpsUnlockCheckUpdate.IsEnabled = true;
        }
    }

    public bool HDRNotSupported { get; set => SetProperty(ref field, value); }

    public bool HDRNotEnabled { get; set => SetProperty(ref field, value); }


    private async Task InitializeGameSettingAsync()
    {
        try
        {
            var localVersion = await _gameLauncherService.GetLocalGameVersionAsync(CurrentGameId);

            // config.ini 缺失（拷贝目录 / 清理过）时读不出版本，只要主程序在就算装了（否则会显示「游戏未安装」）
            bool isInstalled = localVersion != null
                               || await _gameLauncherService.IsGameExeExistsAsync(CurrentGameId);
            if (!isInstalled)
            {
                StackPanel_Emoji.Visibility = Visibility.Visible;
                return;
            }
            IsBaseSettingEnable = true;
            if (CurrentGameBiz.ToGame().Value is GameBiz.hk4e or GameBiz.hkrpg)
            {
                IsLanguageSettingEnable = true;
            }
            if (CurrentGameBiz.Game is GameBiz.hkrpg)
            {
                IsGraphicsSettingEnable = true;
                StackPanel_StarRailFPS.Visibility = Visibility.Visible;
                StarRailFpsIndex = GameSettingService.GetStarRailFPSIndex(CurrentGameBiz);
            }
            if (CurrentGameBiz.Game is GameBiz.hk4e)
            {
                IsGraphicsSettingEnable = true;
                StackPanel_GenshinHDR.Visibility = Visibility.Visible;
                StackPanel_GenshinFpsUnlock.Visibility = Visibility.Visible;
                FpsUnlockTargetValue = AppConfig.GetFpsUnlockTarget(CurrentGameId);
                EnableGenshinHDR = AppConfig.EnableGenshinHDR;
                _displayInformation = DisplayInformation.CreateForWindowId(this.XamlRoot.GetAppWindow().Id);
                _displayInformation.AdvancedColorInfoChanged += _displayInformation_AdvancedColorInfoChanged;
                UpdateHdrState(_displayInformation);
                await RefreshFpsUnlockVersionInfoAsync();
            }
            StartArgument = AppConfig.GetStartArgument(CurrentGameBiz);
            UsePopupWindow = AppConfig.GetUsePopupWindow(CurrentGameBiz);
            var resolutionSetting = GameSettingService.GetGameResolutionSetting(CurrentGameBiz);
            if (resolutionSetting != null)
            {
                EnableFullScreen = resolutionSetting.IsFullScreen;
                if (resolutionSetting.Width * resolutionSetting.Height > 0)
                {
                    ResolutionWidth = resolutionSetting.Width;
                    ResolutionHeight = resolutionSetting.Height;
                    EnableCustomResolution = !UpdateResolutionComboBoxSelection(ResolutionWidth, ResolutionHeight);
                }
                else
                {
                    ComboBox_Resolution.SelectedIndex = 0;
                }
            }
            else
            {
                ComboBox_Resolution.SelectedIndex = 0;
            }
            if (IsLanguageSettingEnable)
            {
                var langSetting = GameSettingService.GetGameVoiceLanguageSetting(CurrentGameBiz);
                if (langSetting != null)
                {
                    LanguageIndex = langSetting.Value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize Game Setting");
        }
        finally
        {
            IsApplyButtonEnable = false;
        }
    }



    private void InitializeResolutionItem()
    {
        var display = DisplayArea.GetFromWindowId(this.XamlRoot.ContentIslandEnvironment.AppWindowId, DisplayAreaFallback.Nearest);
        var width = display.OuterBounds.Width;
        var height = display.OuterBounds.Height;
        var list = Resolutions.Where(x => x.Width <= width && x.Height <= height).ToList();
        if (list.Count == 0)
        {
            list.Add((width, height));
        }
        else
        {
            if (list[0].Width != width || list[0].Height != height)
            {
                list.Add((width, height));
            }
        }
        foreach (var item in list)
        {
            ComboBox_Resolution.Items.Add(new ComboBoxItem
            {
                Content = $"{item.Width} × {item.Height}",
            });
        }
    }




    private static List<(int Width, int Height)> Resolutions = new List<(int Width, int Height)>()
    {
        (3840 , 2160),
        (2560 , 1600),
        (2560 , 1440),
        (2048 , 1536),
        (1920 , 1440),
        (1920 , 1200),
        (1920 , 1080),
        (1680 , 1050),
        (1600 , 1200),
        (1440 , 900 ),
        (1280 , 960 ),
        (1280 , 800 ),
        (1280 , 720 ),
        (1152 , 864 ),
        (1024 , 768 ),
        (800  , 600 ),
        (640  , 480 ),
    };



    private void ComboBox_Resolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.FirstOrDefault() is ComboBoxItem item)
        {
            if (item.Content is string str)
            {
                var split = str.Split('×', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (split.Length == 2)
                {
                    if (int.TryParse(split[0], out int width) && int.TryParse(split[1], out int height))
                    {
                        ResolutionWidth = width;
                        ResolutionHeight = height;
                        IsApplyButtonEnable = true;
                    }
                }
            }
        }
    }



    private bool UpdateResolutionComboBoxSelection(int width, int height)
    {
        foreach (ComboBoxItem item in ComboBox_Resolution.Items)
        {
            if (item.Content is string str)
            {
                var split = str.Split('×', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (split.Length == 2)
                {
                    if (int.TryParse(split[0], out int _width) && int.TryParse(split[1], out int _height))
                    {
                        if (width == _width && height == _height)
                        {
                            ComboBox_Resolution.SelectedItem = item;
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }


    [RelayCommand]
    private async Task OpenGenshinHDRLumianceSettingWindow()
    {
        try
        {
            WeakReferenceMessenger.Default.Send(new MainWindowDragRectAdaptToGameIconMessage(true));
            await new GenshinHDRLuminanceSettingDialog { XamlRoot = this.XamlRoot, CurrentGameBiz = this.CurrentGameBiz }.ShowAsync();
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
        finally
        {
            WeakReferenceMessenger.Default.Send(new MainWindowDragRectAdaptToGameIconMessage());
        }
    }



    [RelayCommand]
    private async Task ApplySetting()
    {
        try
        {
            if (IsBaseSettingEnable)
            {
                if (ResolutionWidth <= 0 || ResolutionHeight <= 0)
                {
                    // 分辨率必须大于0
                    ErrorMessage = Lang.GameSettingPage_ResolutionMustBeGreaterThan0;
                    return;
                }
                var model = new GraphicsSettings_PCResolution_h431323223
                {
                    IsFullScreen = EnableFullScreen,
                    Width = ResolutionWidth,
                    Height = ResolutionHeight,
                };
                GameSettingService.SetGameResolutionSetting(CurrentGameBiz, model);
                AppConfig.SetUsePopupWindow(CurrentGameBiz, UsePopupWindow);
            }
            if (IsLanguageSettingEnable)
            {
                GameSettingService.SetGameVoiceLanguageSetting(CurrentGameBiz, LanguageIndex);
            }
            if (IsGraphicsSettingEnable)
            {
                if (CurrentGameBiz.Game is GameBiz.hkrpg)
                {
                    GameSettingService.SetStarRailFPSIndex(CurrentGameBiz, StarRailFpsIndex);
                }
                if (CurrentGameBiz.Game is GameBiz.hk4e)
                {
                    AppConfig.EnableGenshinHDR = EnableGenshinHDR;
                    GameSettingService.SetGenshinEnableHDR(CurrentGameBiz, EnableGenshinHDR);
                    AppConfig.SetFpsUnlockTarget(CurrentGameId, (int)FpsUnlockTargetValue);

                    // 超过安全上限（119）每次保存都提示一次封号风险 —— 用户要的是「每次都提」，
                    // 不做「提示过一次就不再提」的记忆；只提示，不阻止保存。
                    await WarnFpsUnlockTooHighAsync();
                }
            }
            // 游戏运行时应用的设置无法生效
            ErrorMessage = Lang.GameSettingPage_SettingNotEffect;
            IsApplyButtonEnable = false;
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
            _logger.LogError(ex, "Apply Setting");
        }
    }


    /// <summary>
    /// 解锁帧率超过安全上限（<see cref="AppConfig.FpsUnlockWarnThreshold"/> = 119）时提示封号风险。
    /// 只提示、不拦保存；异常自查自吞 —— 一个提示框不该让「应用」整体失败。
    /// </summary>
    private async Task WarnFpsUnlockTooHighAsync()
    {
        int target = (int)FpsUnlockTargetValue;
        if (target <= AppConfig.FpsUnlockWarnThreshold)
        {
            return;
        }

        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "解锁帧率偏高",
                Content = $"目标帧率 {target} fps 超过了 {AppConfig.FpsUnlockWarnThreshold} fps。\n过高的解锁帧率可能导致封号。",
                CloseButtonText = "知道了",
                DefaultButton = ContentDialogButton.Close,
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fps unlock high value warning");
        }
    }



    private DisplayInformation _displayInformation;

    private void _displayInformation_AdvancedColorInfoChanged(DisplayInformation sender, object args)
    {
        UpdateHdrState(sender);
    }


    private void UpdateHdrState(DisplayInformation displayInformation)
    {
        try
        {
            HDRNotEnabled = false;
            HDRNotSupported = false;
            var info = displayInformation.GetAdvancedColorInfo();
            if (!info.IsAdvancedColorKindAvailable(DisplayAdvancedColorKind.HighDynamicRange))
            {
                HDRNotSupported = true;
            }
            else if (info.CurrentAdvancedColorKind is not DisplayAdvancedColorKind.HighDynamicRange)
            {
                HDRNotEnabled = true;
            }
        }
        catch { }
    }



}
