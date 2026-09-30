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
    #region Switch Background Image


    private const string PlayIcon = "\uF5B0";

    private const string PauseIcon = "\uE62E";


    private List<GameBackground> _backgroundImages;
    public List<GameBackground> BackgroundImages
    {
        get => _backgroundImages;
        set => SetProperty(ref _backgroundImages, value);
    }

    private bool _canStopVideo;
    public bool CanStopVideo
    {
        get => _canStopVideo;
        set => SetProperty(ref _canStopVideo, value);
    }

    private string _startStopButtonIcon;
    public string StartStopButtonIcon
    {
        get => _startStopButtonIcon;
        set => SetProperty(ref _startStopButtonIcon, value);
    }


    private int currentBackgroundImageIndex;
    public int CurrentBackgroundImageIndex
    {
        get => currentBackgroundImageIndex;
        set
        {
            if (SetProperty(ref currentBackgroundImageIndex, value))
            {
                ChangeBackgroundImageIndex(value);
            }
        }
    }


    private void OnBackgroundChanged(object _, BackgroundChangedMessage message)
    {
        if (message.GameBackground is null)
        {
            _ = InitializeBackgameImageSwitcherAsync();
        }
    }


    private async Task InitializeBackgameImageSwitcherAsync()
    {
        try
        {
            CanStopVideo = false;
            BackgroundImages = await _backgroundService.GetGameBackgroundsAsync(CurrentGameId);
            if (BackgroundImages.Count > 1)
            {
                Border_SwitchBackgroundImage.Visibility = Visibility.Visible;
                GameBackground? currentBackground = await _backgroundService.GetSuggestedGameBackgroundAsync(CurrentGameId);
                if (currentBackground != null && BackgroundImages.FirstOrDefault(x => x.Id == currentBackground.Id) is GameBackground current)
                {
                    currentBackgroundImageIndex = Math.Clamp(BackgroundImages.IndexOf(current), 0, BackgroundImages.Count - 1);
                    OnPropertyChanged(nameof(CurrentBackgroundImageIndex));
                    CanStopVideo = current.Type is GameBackground.BACKGROUND_TYPE_VIDEO;
                    if (CanStopVideo)
                    {
                        current.StopVideo = currentBackground.StopVideo;
                        StartStopButtonIcon = current.StopVideo ? PlayIcon : PauseIcon;
                    }
                }
            }
            else
            {
                Border_SwitchBackgroundImage.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize background image switcher {GameBiz}", CurrentGameBiz);
        }
    }


    private void ChangeBackgroundImageIndex(int index)
    {
        try
        {
            if (index < 0 || index >= BackgroundImages.Count)
            {
                return;
            }
            GameBackground current = BackgroundImages[index];
            WeakReferenceMessenger.Default.Send(new BackgroundChangedMessage(current));
            CanStopVideo = current.Type is GameBackground.BACKGROUND_TYPE_VIDEO;
            if (CanStopVideo)
            {
                StartStopButtonIcon = current.StopVideo ? PlayIcon : PauseIcon;
            }
        }
        catch { }
    }


    private void Border_SwitchBackgroundImage_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        Border_SwitchBackgroundImage.Opacity = 1;
    }


    private void Border_SwitchBackgroundImage_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        Border_SwitchBackgroundImage.Opacity = 0;
    }


    int _switchBackgroundTotalDelta = 0;

    private void Border_SwitchBackgroundImage_PointerWheelChanged(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        _switchBackgroundTotalDelta += delta;
        if (_switchBackgroundTotalDelta <= -120)
        {
            CurrentBackgroundImageIndex++;
            _switchBackgroundTotalDelta = 0;
        }
        else if (_switchBackgroundTotalDelta >= 120)
        {
            CurrentBackgroundImageIndex--;
            _switchBackgroundTotalDelta = 0;
        }
    }


    [RelayCommand]
    private async Task CopyCurrentBackgroundImageAsync()
    {
        try
        {
            string? path = null;
            GameBackground? background = AppBackground.Current.CurrentGameBackground;
            if (background?.Type is GameBackground.BACKGROUND_TYPE_CUSTOM)
            {
                path = background.Background.Url;
            }
            else if (background?.Type is GameBackground.BACKGROUND_TYPE_VIDEO && !background.StopVideo)
            {
                string name = Path.GetFileName(background.Video.Url);
                path = BackgroundService.GetBgFilePath(name);
            }
            else if (background is not null)
            {
                string name = Path.GetFileName(background.Background.Url);
                path = BackgroundService.GetBgFilePath(name);
            }
            if (File.Exists(path))
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                ClipboardHelper.SetStorageItems(DataPackageOperation.Copy, file);
                InAppToast.MainWindow?.Information(Lang.Common_CopiedToClipboard);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Copy current background image {GameBiz}", CurrentGameBiz);
        }
    }


    [RelayCommand]
    private async Task SaveCurrentBackgroundImageAsync()
    {
        try
        {
            string? path = null;
            GameBackground? background = AppBackground.Current.CurrentGameBackground;
            if (background?.Type is GameBackground.BACKGROUND_TYPE_CUSTOM)
            {
                path = background.Background.Url;
            }
            else if (background?.Type is GameBackground.BACKGROUND_TYPE_VIDEO && !background.StopVideo)
            {
                string name = Path.GetFileName(background.Video.Url);
                path = BackgroundService.GetBgFilePath(name);
            }
            else if (background is not null)
            {
                string name = Path.GetFileName(background.Background.Url);
                path = BackgroundService.GetBgFilePath(name);
            }
            if (File.Exists(path))
            {
                var savePath = await FileDialogHelper.OpenSaveFileDialogAsync(this.XamlRoot, Path.GetFileName(path));
                if (!string.IsNullOrWhiteSpace(savePath))
                {
                    File.Copy(path, savePath, true);
                    var file = await StorageFile.GetFileFromPathAsync(savePath);
                    var options = new FolderLauncherOptions();
                    options.ItemsToSelect.Add(file);
                    await Launcher.LaunchFolderAsync(await file.GetParentAsync(), options);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Save as current background image {GameBiz}", CurrentGameBiz);
        }
    }


    [RelayCommand]
    private void StartOrStopVideoBackground()
    {
        try
        {
            GameBackground current = BackgroundImages[CurrentBackgroundImageIndex];
            if (current.Type is GameBackground.BACKGROUND_TYPE_VIDEO)
            {
                current.StopVideo = !current.StopVideo;
                StartStopButtonIcon = current.StopVideo ? PlayIcon : PauseIcon;
                WeakReferenceMessenger.Default.Send(new BackgroundChangedMessage(current));
            }
        }
        catch { }
    }


    #endregion

}
