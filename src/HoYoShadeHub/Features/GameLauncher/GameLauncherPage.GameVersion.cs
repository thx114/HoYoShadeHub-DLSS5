using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Core.HoYoShade;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.GameSetting;
using HoYoShadeHub.Features.GameSelector;
using HoYoShadeHub.Features.Background;
using HoYoShadeHub.Features.HoYoPlay;
using HoYoShadeHub.Features.Overlay;
using HoYoShadeHub.Features.OptiScaler;
using HoYoShadeHub.Features.Plugins;
using HoYoShadeHub.Features.Setting;
using HoYoShadeHub.Features.ViewHost;
using HoYoShadeHub.Frameworks;
using HoYoShadeHub.Helpers;
using HoYoShadeHub.RPC.GameInstall;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Timers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;


namespace HoYoShadeHub.Features.GameLauncher;

public sealed partial class GameLauncherPage : PageBase
{
    #region Game Version


    private string? _gameInstallPath;
    public string? GameInstallPath
    {
        get => _gameInstallPath;
        set => SetProperty(ref _gameInstallPath, value);
    }

    /// <summary>
    /// 可移动存储设备提示
    /// </summary>
    private bool _isInstallPathRemovableTipEnabled;
    public bool IsInstallPathRemovableTipEnabled
    {
        get => _isInstallPathRemovableTipEnabled;
        set
        {
            if (SetProperty(ref _isInstallPathRemovableTipEnabled, value))
            {
                OnPropertyChanged(nameof(InstalledLocateGameEnabled));
            }
        }
    }

    /// <summary>
    /// 是否显示 DX12 选项
    /// </summary>
    private bool _isDX12OptionVisible;
    public bool IsDX12OptionVisible
    {
        get => _isDX12OptionVisible;
        set
        {
            if (SetProperty(ref _isDX12OptionVisible, value))
            {
                // 悬停说明跟着游戏配置走（说明按钮已去掉，介绍全在 tooltip 里）
                OnPropertyChanged(nameof(Dx12IntroText));
            }
        }
    }

    /// <summary>DX12 选项的悬停说明（游戏自己的介绍文案）</summary>
    public string Dx12IntroText =>
        !string.IsNullOrWhiteSpace(_dxConfig?.I18nIntro)
            ? _dxConfig!.I18nIntro
            : "以 DX12 启动这个游戏；异常或闪退就关掉。";


    /// <summary>
    /// DX12 配置
    /// </summary>
    private GameDXConfig? _dxConfig;


    /// <summary>
    /// 启用 DX12
    /// </summary>
    private bool _enableDX12;
    public bool EnableDX12
    {
        get => _enableDX12;
        set
        {
            if (SetProperty(ref _enableDX12, value))
            {
                AppConfig.SetEnableDX12(CurrentGameBiz, value);
            }
        }
    }

    /// <summary>
    /// 已安装？定位游戏
    /// </summary>
    public bool InstalledLocateGameEnabled => GameState is GameState.InstallGame && !IsInstallPathRemovableTipEnabled;

    /// <summary>
    /// 预下载按钮是否可用
    /// </summary>
    private bool _isPredownloadButtonEnabled;
    public bool IsPredownloadButtonEnabled
    {
        get => _isPredownloadButtonEnabled;
        set => SetProperty(ref _isPredownloadButtonEnabled, value);
    }

    /// <summary>
    /// 预下载是否完成
    /// </summary>
    private bool _isPredownloadFinished;
    public bool IsPredownloadFinished
    {
        get => _isPredownloadFinished;
        set => SetProperty(ref _isPredownloadFinished, value);
    }


    private Version? localGameVersion;


    private bool isGameExeExists;



    private async void CheckGameVersion()
    {
        try
        {
            // 自定义游戏：Hub 的数据库里没有它，安装目录 = exe 所在目录，状态直接给「开始游戏」
            if (_currentGameEntry is { IsCustom: true } customGame)
            {
                GameInstallPath = customGame.GameDirectory;
                IsInstallPathRemovableTipEnabled = false;
                GameState = GameState.StartGame;
                _ = CheckDX12ConfigAsync();
                await CheckGameRunningAsync();
                return;
            }

            GameInstallPath = GameLauncherService.GetGameInstallPath(CurrentGameId, out bool storageRemoved);
            IsInstallPathRemovableTipEnabled = storageRemoved;
            
            // 如果选择了Starward启动器或Blender插件，即使游戏未定位也设置为StartGame状态
            if (UseStarwardLauncher || LaunchGenshinBlenderPlugin || LaunchZZZBlenderPlugin)
            {
                GameState = GameState.StartGame;
                await CheckGameRunningAsync();
                return;
            }
            
            // 常规启动模式：需要检查游戏安装路径
            if (GameInstallPath is null || storageRemoved)
            {
                GameState = GameState.InstallGame;
                return;
            }
            isGameExeExists = await _gameLauncherService.IsGameExeExistsAsync(CurrentGameId);
            localGameVersion = await _gameLauncherService.GetLocalGameVersionAsync(CurrentGameId);
            // 正式服：exe 在就算装了。config.ini 缺失（拷贝过来的目录 / 清理过）时读不出版本，也不挡启动，
            // 版本只影响 FPS 解锁数据同步等旁路逻辑，那些调用点都已容忍 null
            bool canStart = isGameExeExists;

            if (canStart)
            {
                GameState = GameState.StartGame;
            }
            else
            {
                GameState = GameState.InstallGame;
                return;
            }
            _ = CheckDX12ConfigAsync();
            await CheckGameRunningAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check game version");
        }
    }



    /// <summary>
    /// 检查 DX12 配置
    /// </summary>
    private async Task CheckDX12ConfigAsync()
    {
        try
        {
            IsDX12OptionVisible = false;
            EnableDX12 = AppConfig.GetEnableDX12(CurrentGameBiz);

            // 自定义游戏：HoYoPlay 那边没有它的 DX 配置。鸣潮认得出来就本地给一份 —— 和绝区零一样显示 DX12 开关，
            // 启动项是 -dx12（DX11 是默认；WWMI 给 DX11 固定传 -dx11，Steam 侧官方答复 DX12 就是 -dx12）。
            if (_currentGameEntry is { IsCustom: true } customEntry)
            {
                IsDX12OptionVisible = GameCatalog.IsWutheringWaves(customEntry);
                if (IsDX12OptionVisible)
                {
                    _dxConfig = new GameDXConfig
                    {
                        EnableDXSwitch = true,
                        CmdArgs = "-dx12",
                        DX11PreviewImage = "",
                        DX12PreviewImage = "",
                        I18nIntro = "以 DX12 启动鸣潮，可体验光线追踪、DLSS 4 等画质增强；若游戏出现异常或闪退，请关闭此选项。",
                    };
                }
                return;
            }

            List<GameDXConfig> dxConfigs = await _hoYoPlayService.GetGameDXConfigsAsync([CurrentGameId]);
            _dxConfig = dxConfigs?.FirstOrDefault(x => x.GameId == CurrentGameId);

            bool gameSupportsDX12 = _dxConfig?.EnableDXSwitch is true || await _hoYoPlayService.IsGameSupportDX12Async(CurrentGameId);
            bool ignoreCheck = gameSupportsDX12 && AppConfig.GetIgnoreDX12Check(CurrentGameBiz);

            if (EnableDX12 && (_dxConfig?.EnableDXSwitch is true || ignoreCheck))
            {
                IsDX12OptionVisible = true;
            }

            if (_dxConfig?.EnableDXSwitch is true || ignoreCheck)
            {
                IsDX12OptionVisible = true;
                if (_dxConfig is null || !_dxConfig.EnableDXSwitch)
                {
                    var refConfig = await _hoYoPlayService.GetGameDX12ReferenceConfigAsync(CurrentGameId);
                    if (_dxConfig is null)
                    {
                        _dxConfig = new GameDXConfig
                        {
                            GameId = CurrentGameId,
                            EnableDXSwitch = true,
                            CmdArgs = !string.IsNullOrWhiteSpace(refConfig?.CmdArgs) ? refConfig.CmdArgs : "-use-d3d12",
                            DX11PreviewImage = refConfig?.DX11PreviewImage ?? "",
                            DX12PreviewImage = refConfig?.DX12PreviewImage ?? "",
                            I18nIntro = !string.IsNullOrWhiteSpace(refConfig?.I18nIntro) ? refConfig.I18nIntro : Lang.GameLauncherPage_ForcedDX12Intro,
                        };
                    }
                    else
                    {
                        _dxConfig.EnableDXSwitch = true;
                        if (string.IsNullOrWhiteSpace(_dxConfig.CmdArgs))
                        {
                            _dxConfig.CmdArgs = !string.IsNullOrWhiteSpace(refConfig?.CmdArgs) ? refConfig.CmdArgs : "-use-d3d12";
                        }
                        if (string.IsNullOrWhiteSpace(_dxConfig.DX11PreviewImage) && !string.IsNullOrWhiteSpace(refConfig?.DX11PreviewImage))
                        {
                            _dxConfig.DX11PreviewImage = refConfig.DX11PreviewImage;
                        }
                        if (string.IsNullOrWhiteSpace(_dxConfig.DX12PreviewImage) && !string.IsNullOrWhiteSpace(refConfig?.DX12PreviewImage))
                        {
                            _dxConfig.DX12PreviewImage = refConfig.DX12PreviewImage;
                        }
                        if (string.IsNullOrWhiteSpace(_dxConfig.I18nIntro))
                        {
                            _dxConfig.I18nIntro = !string.IsNullOrWhiteSpace(refConfig?.I18nIntro) ? refConfig.I18nIntro : Lang.GameLauncherPage_ForcedDX12Intro;
                        }
                    }
                }
            }
            else
            {
                IsDX12OptionVisible = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check DX12 config");
        }
    }


    /// <summary>
    /// 获取 DX12 启动参数
    /// </summary>
    /// <returns></returns>
    public string? GetDX12LaunchArgument()
    {
        if (EnableDX12 && _dxConfig is not null)
        {
            return _dxConfig.CmdArgs;
        }
        return null;
    }


    /// <summary>
    /// 显示 DX12 说明对话框
    /// </summary>
    private async void Hyperlink_DX12Intro_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        if (_dxConfig is not null)
        {
            await new DX12IntroDialog { GameDXConfig = _dxConfig, XamlRoot = this.XamlRoot }.ShowAsync();
        }
    }



    /// <summary>
    /// 定位游戏路径
    /// </summary>
    /// <returns></returns>
    private async Task LocateGameAsync()
    {
        try
        {
            string? folder = await FileDialogHelper.PickFolderAsync(this.XamlRoot);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                if (DriveHelper.GetDriveType(folder) is DriveType.Network && !new Uri(folder).IsUnc)
                {
                    InAppToast.MainWindow?.Warning(null, Lang.InstallGameDialog_MappedNetworkDrivesAreNotSupportedPleaseUseANetworkSharePathStartingWithDoubleBackslashes, 0);
                }
                else
                {
                    // 验证游戏exe是否存在
                    var exeName = await _gameLauncherService.GetGameExeNameAsync(CurrentGameId);

                    // 选成上层目录（例如 miHoYo Launcher 根目录）时，往里面看两层目录名再试一次
                    string? gameFolder = folder;
                    if (!File.Exists(Path.Combine(folder, exeName)))
                    {
                        GameFolderSearchResult nested = GameFolderLocator.Locate(folder, exeName);
                        if (nested.Found)
                        {
                            _logger.LogInformation("Game folder nested under {Picked}: {Found} (depth {Depth}, scanned {Scanned})",
                                folder, nested.Directory, nested.Depth, nested.ScannedDirectories);
                            InAppToast.MainWindow?.Information(null,
                                string.Format(Lang.GameLauncherSettingDialog_GameFoundInNestedFolder, nested.Directory), 6000);
                            gameFolder = nested.Directory;
                        }
                    }

                    if (gameFolder is null || !File.Exists(Path.Combine(gameFolder, exeName)))
                    {
                        // 游戏exe不存在，显示错误提示，不进行定位
                        InAppToast.MainWindow?.Warning(null, string.Format(Lang.GameLauncherSettingDialog_GameExeNotFoundInFolder, exeName), 5000);
                        _logger.LogWarning("Game exe not found in selected folder: {Path}, expected: {ExeName}", folder, exeName);
                        return;
                    }

                    // 验证成功，执行定位
                    GameLauncherService.ChangeGameInstallPath(CurrentGameId, gameFolder);
                    CheckGameVersion();
                    WeakReferenceMessenger.Default.Send(new GameInstallPathChangedMessage());

                    // 定位到的游戏固定到顶部（默认不会自动固定，是"定位过"才固定）
                    WeakReferenceMessenger.Default.Send(new PinGameBizMessage(CurrentGameBiz));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Locate game");
        }
    }



    /// <summary>
    /// 定位游戏路径
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="args"></param>
    private async void Hyperlink_LocateGame_Click(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args)
    {
        await LocateGameAsync();
    }



    private void OnGameInstallPathChanged(object _, GameInstallPathChangedMessage message)
    {
        CheckGameVersion();
    }




    private void OnMainWindowStateChanged(object _, MainWindowStateChangedMessage message)
    {
        try
        {
            if (message.Activate && (message.ElapsedOver(TimeSpan.FromMinutes(10)) || message.IsCrossingHour))
            {
                CheckGameVersion();
            }
        }
        catch { }
    }



    private void OnRemovableStorageDeviceChanged(object _, RemovableStorageDeviceChangedMessage message)
    {
        try
        {
            CheckGameVersion();
        }
        catch { }
    }


    private void OnUseStarwardLauncherChanged(object _, UseStarwardLauncherChangedMessage message)
    {
        try
        {
            // 更新 IsStarwardLauncherCheckboxEnabled 属性
            OnPropertyChanged(nameof(IsStarwardLauncherCheckboxEnabled));
            
            // 如果设置被禁用，并且当前正在使用 Starward，则取消勾选
            if (!message.IsEnabled && UseStarwardLauncher)
            {
                UseStarwardLauncher = false;
            }
            
            _logger.LogInformation("UseStarwardLauncher setting changed to: {IsEnabled}", message.IsEnabled);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Handle UseStarwardLauncherChanged message");
        }
    }



    #endregion

}
