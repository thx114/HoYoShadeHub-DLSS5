using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.WinUI.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starward.Codec.VP9Decoder;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Features.GameLauncher;
using HoYoShadeHub.Features.ViewHost;
using HoYoShadeHub.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI;
using WinRT;


namespace HoYoShadeHub.Features.Background;

[INotifyPropertyChanged]
public sealed partial class AppBackground : UserControl
{

    public static AppBackground Current { get; private set; }


    private readonly ILogger<AppBackground> _logger = AppConfig.GetLogger<AppBackground>();

    private readonly BackgroundService _backgroundService = AppConfig.GetService<BackgroundService>();


    public AppBackground()
    {
        Current = this;
        this.InitializeComponent();
        WeakReferenceMessenger.Default.Register<BackgroundChangedMessage>(this, OnBackgroundChanged);
        WeakReferenceMessenger.Default.Register<MainWindowStateChangedMessage>(this, OnMainWindowStateChanged);
        WeakReferenceMessenger.Default.Register<VideoBgVolumeChangedMessage>(this, OnVideoBgVolumeChanged);
        // 游戏跑起来就把视频背景停掉、显存放掉（用户要求）；游戏退出再reload回来
        WeakReferenceMessenger.Default.Register<GameStartedMessage>(this, OnGameStarted);
        WeakReferenceMessenger.Default.Register<GameExitedMessage>(this, OnGameExited);
        this.Loaded += AppBackground_Loaded;
        this.Unloaded += AppBackground_Unloaded;
    }


    private void AppBackground_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        this.XamlRoot.Changed -= XamlRoot_Changed;
        this.XamlRoot.Changed += XamlRoot_Changed;
    }


    private void AppBackground_Unloaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
    {
        DisposeVideoResource();
        this.XamlRoot?.Changed -= XamlRoot_Changed;
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }

    private void XamlRoot_Changed(Microsoft.UI.Xaml.XamlRoot sender, Microsoft.UI.Xaml.XamlRootChangedEventArgs args)
    {
        if (_lastScale != sender.RasterizationScale)
        {
            _ = UpdateBackgroundAsync();
        }
    }



    public GameId CurrentGameId
    {
        get; set
        {
            if (field is null)
            {
                field = value;
                InitializeBackgroundImage();
            }
            field = value;
            _ = UpdateBackgroundAsync();
        }
    }


    public ImageSource? PlacehoderImageSource { get; set => SetProperty(ref field, value); }

    public ImageSource? BackgroundImageSource
    {
        get; set
        {
            if (value is null && field is not null)
            {
                PlacehoderImageSource = field;
            }
            SetProperty(ref field, value);
        }
    }

    public bool IsUpdateBackgroundRunning { get; set => SetProperty(ref field, value); }

    public GameBackground? CurrentGameBackground { get; private set; }

    private string? _lastBackgroundFile;

    private double _lastScale = 1;


    private void InitializeBackgroundImage()
    {
        try
        {
            var file = BackgroundService.GetCachedBackgroundFile(CurrentGameId);
            if (file != null)
            {
                if (!BackgroundService.FileIsSupportedVideo(file))
                {
                    BackgroundImageSource = new BitmapImage(new Uri(file));
                }
                try
                {
                    string? hex = AppConfig.AccentColor;
                    if (!string.IsNullOrWhiteSpace(hex))
                    {
                        Color color = ColorHelper.ToColor(hex);
                        AccentColorHelper.ChangeAppAccentColor(color);
                    }
                }
                catch { }
            }
            else
            {
                BackgroundImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Image/UI_CutScene_1130320101A.png"));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize background image");
        }

    }



    private CancellationTokenSource? updateBackgroundCts;


    public async Task UpdateBackgroundAsync(GameBackground? background = null)
    {
        try
        {
            IsUpdateBackgroundRunning = true;

            updateBackgroundCts?.Cancel();
            updateBackgroundCts = new();
            CancellationToken cancellationToken = updateBackgroundCts.Token;

            if (CurrentGameId is null)
            {
                DisposeVideoResource();
                BackgroundImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Image/UI_CutScene_1130320101A.png"));
                return;
            }

            for (int i = 0; i < 2; i++)
            {
                bool apiCancelled = false;
                string? filePath = null;
                GameBackground? gameBackground = null;
                try
                {
                    bool timeout = i == 0 && background is null;
                    CancellationToken apiCancellationToken = timeout ? new CancellationTokenSource(1000).Token : CancellationToken.None;
                    CancellationToken downloadCancellationToken = timeout ? new CancellationTokenSource(3000).Token : CancellationToken.None;
                    gameBackground = background ?? await _backgroundService.GetSuggestedGameBackgroundAsync(CurrentGameId, apiCancellationToken);
                    if (gameBackground is null)
                    {
                        filePath = BackgroundService.GetFallbackBackgroundImage(CurrentGameId);
                    }
                    else if (gameBackground.Type is GameBackground.BACKGROUND_TYPE_CUSTOM)
                    {
                        filePath = gameBackground.Background.Url;
                    }
                    else if (gameBackground.Type is GameBackground.BACKGROUND_TYPE_VIDEO && !gameBackground.StopVideo)
                    {
                        filePath = await _backgroundService.GetBackgroundFileAsync(gameBackground.Video.Url, downloadCancellationToken);
                    }
                    else
                    {
                        filePath = await _backgroundService.GetBackgroundFileAsync(gameBackground.Background.Url, downloadCancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    apiCancelled = true;
                    filePath = BackgroundService.GetFallbackBackgroundImage(CurrentGameId);
                }
                catch (Exception ex)
                {
                    filePath = BackgroundService.GetFallbackBackgroundImage(CurrentGameId);
                    _logger.LogError(ex, "Update background image");
                }
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                if (filePath == _lastBackgroundFile)
                {
                    if (BackgroundService.FileIsSupportedVideo(filePath))
                    {
                        continue;
                    }
                    if (_lastScale == this.XamlRoot.GetUIScaleFactor())
                    {
                        continue;
                    }
                }
                DisposeVideoResource();
                BackgroundImageSource = null;
                if (filePath != null)
                {
                    if (gameBackground?.Type is GameBackground.BACKGROUND_TYPE_VIDEO)
                    {
                        await SetVideoBackgroundAsync(gameBackground, filePath, cancellationToken);
                    }
                    else if (BackgroundService.FileIsSupportedVideo(filePath))
                    {
                        StartMediaPlayer(filePath);
                    }
                    else
                    {
                        await ChangeBackgroundImageAsync(filePath, cancellationToken);
                    }
                    _lastBackgroundFile = filePath;
                    _lastScale = this.XamlRoot.GetUIScaleFactor();
                    CurrentGameBackground = gameBackground;
                    if (!apiCancelled && gameBackground?.Type is not GameBackground.BACKGROUND_TYPE_CUSTOM)
                    {
                        AppConfig.SetBg(CurrentGameId.GameBiz, Path.GetFileName(filePath));
                        var list = await _backgroundService.GetGameBackgroundsAsync(CurrentGameId);
                        AppConfig.SetGameBackgroundIds(CurrentGameId.GameBiz, string.Join(',', list.Select(x => x.Id)));
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update background image");
        }
        finally
        {
            IsUpdateBackgroundRunning = false;
        }
    }


    private async Task ChangeBackgroundImageAsync(string file, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var fs = File.OpenRead(file);
        var decoder = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());

        double scale = this.XamlRoot.GetUIScaleFactor();
        int decodeWidth = 0, decodeHeight = 0;
        double windowWidth = ActualWidth * scale, windowHeight = ActualHeight * scale;

        if (decoder.PixelWidth <= windowWidth || decoder.PixelHeight <= windowHeight)
        {
            decodeWidth = (int)decoder.PixelWidth;
            decodeHeight = (int)decoder.PixelHeight;
            var writeableBitmap = new WriteableBitmap(decodeWidth, decodeHeight);
            fs.Position = 0;
            await writeableBitmap.SetSourceAsync(fs.AsRandomAccessStream());
            cancellationToken.ThrowIfCancellationRequested();
            Color? color = AccentColorHelper.GetAccentColor(writeableBitmap.PixelBuffer, decodeWidth, decodeHeight);
            AccentColorHelper.ChangeAppAccentColor(color);
            AppConfig.AccentColor = color?.ToHex() ?? null;
            BackgroundImageSource = writeableBitmap;
        }
        else
        {
            if (windowWidth * decoder.PixelHeight > windowHeight * decoder.PixelWidth)
            {
                decodeWidth = (int)windowWidth;
                decodeHeight = (int)(windowWidth * decoder.PixelHeight / decoder.PixelWidth);
            }
            else
            {
                decodeHeight = (int)windowHeight;
                decodeWidth = (int)(windowHeight * decoder.PixelWidth / decoder.PixelHeight);
            }
            using var soft = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
                                                                  BitmapAlphaMode.Premultiplied,
                                                                  new BitmapTransform
                                                                  {
                                                                      ScaledWidth = (uint)decodeWidth,
                                                                      ScaledHeight = (uint)decodeHeight,
                                                                      InterpolationMode = BitmapInterpolationMode.Fant
                                                                  },
                                                                  ExifOrientationMode.IgnoreExifOrientation,
                                                                  ColorManagementMode.DoNotColorManage);
            var softwareBitmapSource = new SoftwareBitmapSource();
            await softwareBitmapSource.SetBitmapAsync(soft);

            cancellationToken.ThrowIfCancellationRequested();

            using BitmapBuffer bitmapBuffer = soft.LockBuffer(BitmapBufferAccessMode.Read);
            using IMemoryBufferReference memoryBufferReference = bitmapBuffer.CreateReference();
            memoryBufferReference.As<AccentColorHelper.IMemoryBufferByteAccess>().GetBuffer(out nint bufferPtr, out uint capacity);
            Color? color = AccentColorHelper.GetAccentColor(bufferPtr, capacity, decodeWidth, decodeHeight);
            AccentColorHelper.ChangeAppAccentColor(color);
            AppConfig.AccentColor = color?.ToHex() ?? null;
            BackgroundImageSource = softwareBitmapSource;
        }
    }



    #region Video



    private MediaPlayer? _mediaPlayer;

    private CanvasRenderTarget? _videoSurface;

    private CanvasBitmap? _videoOverlayImage;

    private CanvasImageSource? _videoImageSource;

    private int videoBgVolume = AppConfig.VideoBgVolume;

    private SemaphoreSlim _videoSemaphore = new SemaphoreSlim(1, 1);


    private void StartMediaPlayer(string file)
    {
        RegisterVP9Decoder();
        _mediaPlayer = new MediaPlayer
        {
            IsLoopingEnabled = true,
            Volume = videoBgVolume / 100.0,
            IsMuted = false,
            IsVideoFrameServerEnabled = true,
            Source = MediaSource.CreateFromUri(new Uri(file))
        };
        _mediaPlayer.CommandManager.IsEnabled = false;
        _mediaPlayer.SystemMediaTransportControls.IsEnabled = false;
        _mediaPlayer.VideoFrameAvailable += MediaPlayer_VideoFrameAvailable;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
        _mediaPlayer.Play();
    }


    private async Task SetVideoBackgroundAsync(GameBackground gameBackground, string filePath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (BackgroundService.FileIsSupportedVideo(filePath))
        {
            StartMediaPlayer(filePath);
            _ = PrepareVideoOverlayImageAsync(gameBackground.Theme.Url, cancellationToken);
            _ = ChangeAccentColorToImageFileAsync(gameBackground.Background.Url, cancellationToken);
        }
        else
        {
            string overlayPath = await _backgroundService.GetBackgroundFileAsync(gameBackground.Theme.Url, cancellationToken);
            using var fs1 = File.OpenRead(filePath);
            using var bitmap = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), fs1.AsRandomAccessStream(), 96);
            using var fs2 = File.OpenRead(overlayPath);
            using var overlay = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), fs2.AsRandomAccessStream(), 96);
            var imageSource = new CanvasImageSource(CanvasDevice.GetSharedDevice(), bitmap.SizeInPixels.Width, bitmap.SizeInPixels.Height, 96);
            using (var ds = imageSource.CreateDrawingSession(Microsoft.UI.Colors.Transparent))
            {
                ds.DrawImage(bitmap);
                Rect source = new Rect(0, 0, overlay.SizeInPixels.Width, overlay.SizeInPixels.Height);
                Rect dest = new Rect(0, 0, imageSource.SizeInPixels.Width, imageSource.SizeInPixels.Height);
                ds.DrawImage(overlay, dest, source, 1, CanvasImageInterpolation.HighQualityCubic);
            }
            BackgroundImageSource = imageSource;
            if (bitmap.Format is Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized)
            {
                try
                {
                    Color? color = await Task.Run(() =>
                    {
                        Color? color = AccentColorHelper.GetAccentColor(bitmap.GetPixelBytes(), (int)bitmap.SizeInPixels.Width, (int)bitmap.SizeInPixels.Height);
                        return color;
                    });
                    if (color is not null)
                    {
                        AccentColorHelper.ChangeAppAccentColor(color);
                        AppConfig.AccentColor = color?.ToHex() ?? null;
                    }
                }
                catch { }
            }

        }
    }




    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {

    }

    private void MediaPlayer_VideoFrameAvailable(MediaPlayer sender, object args)
    {
        if (_videoSemaphore.CurrentCount == 0)
        {
            return;
        }
        _videoSemaphore.Wait();
        DispatcherQueue?.TryEnqueue(() =>
        {
            try
            {
                int naturalWidth = (int)sender.PlaybackSession.NaturalVideoWidth;
                int naturalHeight = (int)sender.PlaybackSession.NaturalVideoHeight;
                if (naturalWidth <= 0 || naturalHeight <= 0)
                {
                    return;
                }

                // 按**窗口实际大小**渲染，不要把整帧原样拷出来。
                // 以前是 new CanvasRenderTarget(NaturalVideoWidth, NaturalVideoHeight)：源是 4K 时
                // 每帧要拷 3840×2160×4B ≈ 33 MB，30fps 就是 ~1 GB/s 的显存带宽，用户反馈「背景占的显卡太多」。
                // 现在只拷窗口那么大，缩放交给 CopyFrameToVideoSurface 在 GPU 上做。
                (int width, int height) = VideoRenderSize(naturalWidth, naturalHeight);
                if (_videoSurface is null || _videoImageSource is null
                    || (int)_videoSurface.SizeInPixels.Width != width
                    || (int)_videoSurface.SizeInPixels.Height != height)
                {
                    _videoSurface?.Dispose();
                    _videoSurface = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), width, height, 96);
                    _videoImageSource = new CanvasImageSource(CanvasDevice.GetSharedDevice(), width, height, 96);
                    BackgroundImageSource = _videoImageSource;
                }

                sender.CopyFrameToVideoSurface(_videoSurface, new Rect(0, 0, width, height));
                using var ds = _videoImageSource.CreateDrawingSession(Microsoft.UI.Colors.Transparent);
                ds.DrawImage(_videoSurface);
                if (_videoOverlayImage is not null)
                {
                    Rect source = new Rect(0, 0, _videoOverlayImage.SizeInPixels.Width, _videoOverlayImage.SizeInPixels.Height);
                    Rect dest = new Rect(0, 0, _videoImageSource.SizeInPixels.Width, _videoImageSource.SizeInPixels.Height);
                    ds.DrawImage(_videoOverlayImage, dest, source, 1, CanvasImageInterpolation.HighQualityCubic);
                }
            }
            catch { }
            finally
            {
                _videoSemaphore.Release();
            }
        });
    }


    /// <summary>
    /// 视频要渲染成多大：够铺满窗口就行（不放大），省显存带宽。
    /// 背景是 <c>UniformToFill</c> 铺满的，所以取「能盖住窗口」的那个缩放比。
    /// </summary>
    private (int Width, int Height) VideoRenderSize(int naturalWidth, int naturalHeight)
    {
        try
        {
            double scale = XamlRoot?.RasterizationScale ?? 1;
            if (scale <= 0)
            {
                scale = 1;
            }

            double targetWidth = Math.Max(ActualWidth, 1) * scale;
            double targetHeight = Math.Max(ActualHeight, 1) * scale;
            if (targetWidth <= 1 || targetHeight <= 1)
            {
                return (naturalWidth, naturalHeight);
            }

            double factor = Math.Min(Math.Max(targetWidth / naturalWidth, targetHeight / naturalHeight), 1);
            return (Math.Max(2, (int)Math.Round(naturalWidth * factor)),
                    Math.Max(2, (int)Math.Round(naturalHeight * factor)));
        }
        catch
        {
            return (naturalWidth, naturalHeight);
        }
    }



    private async Task PrepareVideoOverlayImageAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            string filePath = await _backgroundService.GetBackgroundFileAsync(url, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            _videoOverlayImage?.Dispose();
            using var fs = File.OpenRead(filePath);
            _videoOverlayImage = await CanvasBitmap.LoadAsync(CanvasDevice.GetSharedDevice(), fs.AsRandomAccessStream(), 96);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prepare video overlay image");
        }
    }


    private async Task ChangeAccentColorToImageFileAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            Color? color = await Task.Run(async () =>
            {
                string filePath = await _backgroundService.GetBackgroundFileAsync(url, cancellationToken);
                if (cancellationToken.IsCancellationRequested)
                {
                    return null;
                }
                using var fs = File.OpenRead(filePath);
                var decoder = await BitmapDecoder.CreateAsync(fs.AsRandomAccessStream());
                var pixelData = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
                Color? color = AccentColorHelper.GetAccentColor(pixelData.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight);
                return color;
            });
            if (color is not null)
            {
                AccentColorHelper.ChangeAppAccentColor(color);
                AppConfig.AccentColor = color?.ToHex() ?? null;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prepare video overlay image");
        }
    }


    private void DisposeVideoResource()
    {
        _mediaPlayer?.Dispose();
        _mediaPlayer = null;
        _videoSurface?.Dispose();
        _videoSurface = null;
        _videoImageSource = null;
        _videoOverlayImage?.Dispose();
        _videoOverlayImage = null;
        UnregisterVP9Decoder();
    }


    private static void RegisterVP9Decoder()
    {
        try
        {
            int hr = VP9Decoder.RegisterVP9DecoderLocal();
        }
        catch { }
    }


    private static void UnregisterVP9Decoder()
    {
        try
        {
            int hr = VP9Decoder.UnregisterVP9DecoderLocal();
        }
        catch { }
    }


    #endregion



    private void OnBackgroundChanged(object _, BackgroundChangedMessage message)
    {
        _ = UpdateBackgroundAsync(message.GameBackground);
    }


    private void OnMainWindowStateChanged(object _, MainWindowStateChangedMessage message)
    {
        try
        {
            // 兜底：游戏退出那一下没收到（注入模式 / Hub 没跟踪进程）时，切回 Hub 也能把背景找回来
            if (message.Activate && _videoReleasedForGame)
            {
                _ = RestoreBackgroundIfGameGoneAsync();
            }

            if (_mediaPlayer is not null)
            {
                var state = _mediaPlayer.PlaybackSession.PlaybackState;
                if (message.Activate && state is not MediaPlaybackState.Playing)
                {
                    _mediaPlayer.Play();
                }
                else if (message.Hide || message.SessionLock)
                {
                    _mediaPlayer.Pause();
                }
            }
        }
        catch { }
    }


    /// <summary>视频背景是不是因为「游戏在跑」被停掉了（关游戏后要恢复，用户报过没恢复）</summary>
    private bool _videoReleasedForGame;

    /// <summary>游戏开始跑：停视频 + 释放显存（玩游戏的时候谁也不看这个背景）</summary>
    private void OnGameStarted(object _, GameStartedMessage message)
    {
        try
        {
            DisposeVideoResource();
            BackgroundImageSource = new BitmapImage(new Uri("ms-appx:///Assets/Image/UI_CutScene_1130320101A.png"));
            _videoReleasedForGame = true;
            _logger.LogInformation("Game started: video background stopped to free GPU memory");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stop video background on game start");
        }
    }



    /// <summary>游戏退出：把背景（含视频）重新拉起来</summary>
    private void OnGameExited(object _, GameExitedMessage message)
    {
        try
        {
            _videoReleasedForGame = false;
            _ = UpdateBackgroundAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restore background after game exit");
        }
    }



    /// <summary>游戏进程确实没了的话，把背景（含视频）重新拉起来</summary>
    private async Task RestoreBackgroundIfGameGoneAsync()
    {
        try
        {
            if (CurrentGameId is not { } gameId)
            {
                return;
            }

            Process? running = null;
            try
            {
                running = await AppConfig.GetService<HoYoShadeHub.Features.GameLauncher.GameLauncherService>()
                    .GetGameProcessAsync(gameId);
            }
            catch
            {
                // 查不到就当没在跑
            }

            if (running is not null)
            {
                return;
            }

            _videoReleasedForGame = false;
            _logger.LogInformation("Restoring background: game is no longer running");
            await UpdateBackgroundAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Restore background fallback");
        }
    }



    private void OnVideoBgVolumeChanged(object _, VideoBgVolumeChangedMessage message)
    {
        try
        {
            videoBgVolume = message.Volume;
            _mediaPlayer?.Volume = message.Volume / 100d;
        }
        catch { }
    }


}
