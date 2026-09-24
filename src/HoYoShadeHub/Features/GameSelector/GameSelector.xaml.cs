using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.Win32;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Features.Background;
using HoYoShadeHub.Features.GameLauncher;
using Microsoft.Extensions.Logging;
using HoYoShadeHub.Features.Plugins;
using HoYoShadeHub.Features.HoYoPlay;
using HoYoShadeHub.Features.Setting;
using HoYoShadeHub.Features.ViewHost;
using HoYoShadeHub.Helpers;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Windows.Foundation;


namespace HoYoShadeHub.Features.GameSelector;

[INotifyPropertyChanged]
public sealed partial class GameSelector : UserControl
{


    public event EventHandler<(GameId, bool DoubleTapped)>? CurrentGameChanged;


    private readonly HoYoPlayService _hoyoplayService = AppConfig.GetService<HoYoPlayService>();


    private readonly GameLauncherService _gameLauncherService = AppConfig.GetService<GameLauncherService>();


    private readonly Microsoft.Extensions.Logging.ILogger<GameSelector> _logger = AppConfig.GetLogger<GameSelector>();



    public GameSelector()
    {
        this.InitializeComponent();
        InitializeGameSelector();
        this.Loaded += GameSelector_Loaded;
        WeakReferenceMessenger.Default.Register<LanguageChangedMessage>(this, OnLanguageChanged);
        // 定位到游戏之后，把它固定到顶部（用户要求：定位到的游戏就固定到顶部）
        WeakReferenceMessenger.Default.Register<PinGameBizMessage>(this, (_, message) => DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                PinGameBiz(message.GameBiz);
            }
            catch { }
        }));

        // 「添加游戏」之后，把新加的自定义游戏那张图标补到顶部
        WeakReferenceMessenger.Default.Register<CustomGameAddedMessage>(this, (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                List<GameBizIcon> added = AppendCustomGameIcons();
                if (added.Count > 0)
                {
                    _ = LoadCustomGameIconsAsync(added);
                }

                RefreshCustomGameDisplays();
            }
            catch { }
        }));
        WeakReferenceMessenger.Default.Register<MainWindowStateChangedMessage>(this, OnMainWindowStateChanged);
        WeakReferenceMessenger.Default.Register<MainWindowDragRectAdaptToGameIconMessage>(this, OnMainWindowStateChanged);
    }



    public GameBiz CurrentGameBiz { get; set; }


    public GameId? CurrentGameId { get; set; }


    public ObservableCollection<GameBizIcon> GameBizIcons { get; set => SetProperty(ref field, value); } = new();


    public GameBizIcon? CurrentGameBizIcon { get; set => SetProperty(ref field, value); }


    public bool IsPinned { get; set => SetProperty(ref field, value); }


    private bool ignoreDpiChanged = false;

    private double lastScale = 1;

    /// <summary>
    /// 根据当前语言获取因缘精灵的Logo路径
    /// </summary>
    private string GetHnaLogoPath()
    {
        string language = LanguageUtil.FilterLanguage(CultureInfo.CurrentUICulture.Name);
        return language switch
        {
            "zh-cn" => "ms-appx:///Assets/Image/logo_hna-zh-cn.png",
            "zh-tw" => "ms-appx:///Assets/Image/logo_hna-zh-tw.png",
            _ => "ms-appx:///Assets/Image/logo_hna-en-us.png",
        };
    }


    /// <summary>
    /// 根据当前语言获取星布谷地的Logo路径
    /// </summary>
    private string GetPpLogoPath()
    {
        string language = LanguageUtil.FilterLanguage(CultureInfo.CurrentUICulture.Name);
        return language switch
        {
            "zh-cn" => "ms-appx:///Assets/Image/logo_pp-zh-cn.png",
            "zh-tw" => "ms-appx:///Assets/Image/logo_pp-zh-tw.png",
            _ => "ms-appx:///Assets/Image/logo_pp-en-us.png",
        };
    }


    public void InitializeGameSelector()
    {
        List<GameInfo> gameInfos = GetCachedGameInfos();
        InitializeGameIconsArea(gameInfos);
        InitializeGameServerArea(gameInfos);
        InitializeInstalledGamesCommand.Execute(null);
    }



    private async void GameSelector_Loaded(object sender, RoutedEventArgs e)
    {
        this.XamlRoot.Changed -= XamlRoot_Changed;
        this.XamlRoot.Changed += XamlRoot_Changed;
        await Task.Delay(1000);
        await UpdateGameInfoAsync();
    }



    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args)
    {
        if (ignoreDpiChanged)
        {
            return;
        }
        if (lastScale != sender.RasterizationScale)
        {
            lastScale = sender.RasterizationScale;
            UpdateDragRectangles();
        }
    }





    private async void OnLanguageChanged(object? _, LanguageChangedMessage __)
    {
        this.Bindings.Update();
        await UpdateGameInfoAsync();
    }




    private async void OnMainWindowStateChanged(object? _, MainWindowStateChangedMessage message)
    {
        try
        {
            if (message.Activate && (message.ElapsedOver(TimeSpan.FromMinutes(10)) || message.IsCrossingHour))
            {
                await UpdateGameInfoAsync();
            }
        }
        catch { }
    }



    private void OnMainWindowStateChanged(object? _, MainWindowDragRectAdaptToGameIconMessage message)
    {
        if (!message.IgnoreDpiChanged)
        {
            UpdateDragRectangles();
        }
        ignoreDpiChanged = message.IgnoreDpiChanged;
    }




    private List<GameInfo> GetCachedGameInfos()
    {
        try
        {
            string? json = AppConfig.CachedGameInfo;
            if (!string.IsNullOrWhiteSpace(json))
            {
                return JsonSerializer.Deserialize<List<GameInfo>>(json) ?? [];
            }
        }
        catch { }
        return [];
    }




    #region Game Icon


    /// <summary>
    /// 初始化游戏图标区域
    /// </summary>
    private void InitializeGameIconsArea(List<GameInfo> gameInfos)
    {
        try
        {
            GameBizIcons.CollectionChanged -= GameBizIcons_CollectionChanged;
            GameBizIcons.Clear();

            // 从配置文件读取已选的 GameBiz
            string? bizs = AppConfig.SelectedGameBizs;
            foreach (string str in bizs?.Split(',')?.Distinct() ?? [])
            {
                if (GameBiz.TryParse(str, out GameBiz biz))
                {
                    // 已知的 GameBiz
                    GameBizIcons.Add(new GameBizIcon(biz) { IsPinned = true });
                }
                else if (gameInfos.FirstOrDefault(x => x.GameBiz == biz) is GameInfo info)
                {
                    // 由 HoYoPlay API 获取，但未适配的 GameBiz
                    GameBizIcons.Add(new GameBizIcon(info) { IsPinned = true });
                }
            }

            // 自定义游戏（用户自己挑的 exe）也放进同一行 —— 它们不写在 SelectedGameBizs 里，
            // 每次从 games.json 重算，加了就有、删了就没
            List<GameBizIcon> customIcons = AppendCustomGameIcons();

            // 选取当前游戏
            GameBiz lastSelectedGameBiz = AppConfig.CurrentGameBiz;
            if (GameBizIcons.FirstOrDefault(x => x.GameBiz == lastSelectedGameBiz) is GameBizIcon icon)
            {
                CurrentGameBizIcon = icon;
                CurrentGameBizIcon.IsSelected = true;
                CurrentGameBiz = lastSelectedGameBiz;
            }
            else if (lastSelectedGameBiz.IsKnown())
            {
                CurrentGameBizIcon = new GameBizIcon(lastSelectedGameBiz);
                CurrentGameBizIcon.IsSelected = true;
                CurrentGameBiz = lastSelectedGameBiz;
            }
            else if (gameInfos.FirstOrDefault(x => x.GameBiz == lastSelectedGameBiz) is GameInfo info)
            {
                CurrentGameBizIcon = new GameBizIcon(info);
                CurrentGameBizIcon.IsSelected = true;
                CurrentGameBiz = lastSelectedGameBiz;
            }

            CurrentGameId = CurrentGameBizIcon?.GameId;
            if (CurrentGameId is not null)
            {
                CurrentGameChanged?.Invoke(this, (CurrentGameId, false));
            }

            if (AppConfig.IsGameBizSelectorPinned)
            {
                Pin();
            }

            GameBizIcons.CollectionChanged += GameBizIcons_CollectionChanged;

            if (customIcons.Count > 0)
            {
                _ = LoadCustomGameIconsAsync(customIcons);
            }
        }
        catch { }
        if (GameBizIcons.Count == 0 && CurrentGameBizIcon is null)
        {
            TeachTip_SelectGame.IsOpen = true;
        }
    }



    /// <summary>
    /// 把自定义游戏补进顶部游戏列表（已经在了就跳过）。
    /// 返回这次新加的那些，调用方负责异步补图标。
    /// </summary>
    private List<GameBizIcon> AppendCustomGameIcons()
    {
        var added = new List<GameBizIcon>();

        foreach (GameEntry entry in GameCatalog.CustomEntries())
        {
            // 登记安装路径 —— 启动器页就是靠 AppConfig.GetGameInstallPath(biz) 认路的
            GameCatalog.RegisterCustomGame(entry);

            if (GameBizIcons.Any(x => x.GameBiz == entry.CustomBiz))
            {
                continue;
            }

            var icon = new GameBizIcon(entry)
            {
                // 在图标行里的就必须是「已固定」——不标的话右键菜单会显示「固定到顶部」，
                // 点了又是空操作（用户报过：默认就在顶部，右键还是固定按钮）
                IsPinned = true,
            };
            GameBizIcons.Add(icon);
            added.Add(icon);
        }

        if (added.Count > 0)
        {
            _logger.LogInformation("Game selector: added {Count} custom game icons ({Names})",
                added.Count, string.Join(", ", added.Select(x => x.GameName)));
        }

        return added;
    }



    /// <summary>自定义游戏的图标是从 exe 里现抠的，抠好再换上</summary>
    private async Task LoadCustomGameIconsAsync(IEnumerable<GameBizIcon> icons)
    {
        foreach (GameBizIcon icon in icons)
        {
            if (icon.CustomEntry is not { } entry)
            {
                continue;
            }

            try
            {
                if (await GameIconExtractor.EnsureAsync(entry) is { } uri)
                {
                    icon.GameIcon = uri;
                    RefreshCustomGameDisplays();
                }
            }
            catch { }
        }

        // 抠不出图标（exe 不在 / 缩略图取不到）也必须刷一次：刷新原本只写在「抠到了」的分支里，
        // 于是新加的游戏进不了「选择游戏」面板，用户看到的表现就是「加了却在列表里找不到」。
        RefreshCustomGameDisplays();
    }



    /// <summary>
    /// 游戏图标区域是否可见
    /// </summary>
    public bool GameIconsAreaVisible
    {
        get => Grid_GameIconsArea.Translation == Vector3.Zero;
        set
        {
            if (value)
            {
                Grid_GameIconsArea.Translation = Vector3.Zero;
            }
            else
            {
                Grid_GameIconsArea.Translation = new Vector3(0, -100, 0);
            }
            UpdateDragRectangles();
        }
    }


    /// <summary>
    /// 更新窗口拖拽区域。
    ///
    /// <para>
    /// 左上角那排游戏图标**必须**留在拖拽区（标题栏区域）之外 —— 落在里面的像素会被系统
    /// 当标题栏吃掉，左键和右键都到不了 XAML。窗口尺寸/呈现器一变，MainWindow 会重设一次
    /// 拖拽区，所以它现在改成调这个方法（见 MainWindow.UpdateDragRectangles）。
    /// </para>
    /// </summary>
    /// <returns>算出来了没有（没有就让调用方退回「整条标题栏」）</returns>
    public bool TryUpdateDragRectangles()
    {
        try
        {
            if (XamlRoot is null || Border_CurrentGameIcon is null || Grid_GameIconsArea is null)
            {
                return false;
            }

            // 布局还没跑完时 ActualWidth 是 0，别把整个左上角都算进拖拽区
            double left = Math.Max(Border_CurrentGameIcon.ActualWidth, 56);
            if (GameIconsAreaVisible)
            {
                left += Grid_GameIconsArea.ActualWidth;
            }

            XamlRoot.SetWindowDragRectangles([new Rect(left, 0, 10000, 48)]);
            return true;
        }
        catch
        {
            return false;
        }
    }



    /// <summary>更新窗口拖拽区域（算不出来就算了）</summary>
    public void UpdateDragRectangles() => TryUpdateDragRectangles();



    /// <summary>
    /// 鼠标移入到当前游戏图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Border_CurrentGameIcon_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        // 显示所有游戏图标
        GameIconsAreaVisible = true;
    }



    #region 右键菜单：固定 / 取消固定 / 删 HoYoShade

    /// <summary>固定到顶部（加进图标行）</summary>
    private void GameIcon_Pin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameBizIcon icon })
        {
            return;
        }

        if (GameBizIcons.Any(x => x.GameId == icon.GameId))
        {
            icon.IsPinned = true;
            return;
        }

        icon.IsPinned = true;
        GameBizIcons.Add(icon);
    }

    /// <summary>取消固定（从图标行拿掉）—— 之后还能在左上角按钮的选择界面里找到它</summary>
    private void GameIcon_Unpin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameBizIcon icon })
        {
            return;
        }

        if (GameBizIcons.FirstOrDefault(x => x.GameId == icon.GameId) is { } existing)
        {
            GameBizIcons.Remove(existing);
            icon.IsPinned = false;
            InAppToast.MainWindow?.Information("已取消固定",
                $"要去掉的是顶部这一行；「{icon.GameName}」还在左上角按钮的展开页里，右键它的图标可以再固定回来。", 8000);
        }

        icon.IsPinned = false;
    }

    /// <summary>删掉这个客户端的 HoYoShade / ReShade 改动（= 游戏设置里那个「卸载/还原」）</summary>
    private async void GameIcon_UninstallShade_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameBizIcon icon })
        {
            return;
        }

        try
        {
            var dialog = new UninstallReShadeClientDialog { XamlRoot = this.XamlRoot };
            await dialog.ShowAsync();

            if (!dialog.IsConfirmed)
            {
                return;
            }

            string? installPath = GameLauncherService.GetGameInstallPath(icon.GameId);
            ReShadeClientUninstallResult result = await ReShadeClientUninstaller.UninstallAsync(installPath);

            if (result.Ok)
            {
                InAppToast.MainWindow?.Success("卸载/还原完成",
                    $"{icon.GameName}：删掉了 {result.DeletedFiles.Count} 项（{installPath}）", 8000);
            }
            else
            {
                InAppToast.MainWindow?.Error("卸载/还原失败", result.Error ?? "未知原因", 8000);
            }
        }
        catch (Exception ex)
        {
            InAppToast.MainWindow?.Error("卸载/还原失败", ex.Message, 8000);
            Debug.WriteLine(ex);
        }
    }

    #endregion



    /// <summary>
    /// 添加自定义游戏：左上角那个按钮点开的选择界面里。
    ///
    /// 选一个 exe 就够 —— 拿到 exe 路径 = 拿到游戏目录 = 顺带定位到那个游戏的 ReShade.ini，
    /// 于是「启动 / 注入 / 插件开关」三件事共用同一份数据（见 docs/GAMES-AND-INJECT.md §2.1）。
    /// </summary>
    private async void Button_AddCustomGame_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? exe = await FileDialogHelper.PickSingleFileAsync(XamlRoot, ("游戏主程序", ".exe"));
            if (string.IsNullOrWhiteSpace(exe))
            {
                // 用户取消 和 选择器没弹出来，都走这里。记一条，免得线上只报「加不了游戏」却查不出是哪一种。
                _logger.LogInformation("Add custom game: no exe picked (cancelled, or the picker never showed)");
                return;
            }

            _logger.LogInformation("Add custom game: picked {Exe}", exe);

            GameDiscoveryService service = GameCatalog.CreateService();
            AddCustomResult result = service.AddCustom(exe);

            _logger.LogInformation("Add custom game: added={Added}, biz={Biz}, error={Error}",
                result.Added, result.Entry?.CustomBiz.Value, result.Error);

            if (result.Entry is null)
            {
                InAppToast.MainWindow?.Error("添加游戏", result.Error ?? "添加失败", 6000);
                return;
            }

            GameCatalog.RegisterCustomGame(result.Entry);

            List<GameBizIcon> added = AppendCustomGameIcons();
            if (added.Count > 0)
            {
                _ = LoadCustomGameIconsAsync(added);
            }

            // 加完直接选中它
            if (GameBizIcons.FirstOrDefault(x => x.GameBiz == result.Entry.CustomBiz) is { } icon)
            {
                if (CurrentGameBizIcon is not null && !ReferenceEquals(CurrentGameBizIcon, icon))
                {
                    CurrentGameBizIcon.IsSelected = false;
                }

                CurrentGameBizIcon = icon;
                CurrentGameBiz = icon.GameBiz;
                CurrentGameId = icon.GameId;
                icon.IsSelected = true;
                AppConfig.CurrentGameBiz = icon.GameBiz;
            }

            HideFullBackground();
            UpdateDragRectangles();

            InAppToast.MainWindow?.Information("添加游戏",
                result.Entry.HasReShadeIni
                    ? $"{result.Entry.DisplayName}：找到 ReShade.ini，插件开关可以用。"
                    : $"{result.Entry.DisplayName}：这个目录里没有 ReShade.ini，可能没装 HoYoShade/ReShade —— 只能用于启动和注入。",
                6000);

            if (CurrentGameId is not null)
            {
                CurrentGameChanged?.Invoke(this, (CurrentGameId, false));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Add custom game failed");
            Debug.WriteLine(ex);
            InAppToast.MainWindow?.Error("添加游戏", ex.Message, 6000);
        }
    }



    /// <summary>
    /// 给自定义游戏换背景图 —— Hub 没有它的官方背景，只能用户自己给。
    ///
    /// 存的是同一套 <c>custom_bg_{biz}</c> 设置，所以启动器页的背景、选择界面的卡片
    /// 会一起跟着换（<c>BackgroundChangedMessage</c> 那边也会刷新）。
    /// </summary>
    private async void Button_ChangeGameBackground_Click(object sender, RoutedEventArgs e)
    {
        // 从卡片菜单进来时 DataContext 是 GameBizDisplay；从顶部图标行右键进来时是 GameBizIcon
        GameBiz? biz = sender switch
        {
            FrameworkElement { DataContext: GameBizDisplay display } => display.GameInfo?.GameBiz,
            FrameworkElement { DataContext: GameBizIcon icon } => icon.GameBiz,
            _ => null,
        };

        if (biz is not { } info || string.IsNullOrWhiteSpace(info.Value))
        {
            return;
        }

        try
        {
            BackgroundService service = AppConfig.GetService<BackgroundService>();
            string? name = await service.ChangeCustomBackgroundFileAsync(XamlRoot);
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            AppConfig.SetCustomBg(info, name);
            AppConfig.SetEnableCustomBg(info, true);
            WeakReferenceMessenger.Default.Send(new BackgroundChangedMessage());

            RefreshCustomGameDisplays();

            // 关掉弹出的这个菜单
            if (VisualTreeHelper.GetOpenPopupsForXamlRoot(this.XamlRoot).FirstOrDefault() is Popup popup)
            {
                popup.IsOpen = false;
            }
        }
        catch (COMException)
        {
            InAppToast.MainWindow?.Error(Lang.GameLauncherSettingDialog_CannotDecodeFile);
        }
        catch (Exception ex)
        {
            InAppToast.MainWindow?.Error(Lang.GameLauncherSettingDialog_AnUnknownErrorOccurredPleaseCheckTheLogs);
            Debug.WriteLine(ex);
        }
    }



    /// <summary>
    /// 鼠标移出当前游戏图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Border_CurrentGameIcon_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (FullBackgroundVisible || IsPinned)
        {
            // 当前游戏图标被固定或者全屏显示时，不隐藏所有游戏图标
            return;
        }
        if (sender is UIElement ele)
        {
            var postion = e.GetCurrentPoint(sender as UIElement).Position;
            if (postion.X > ele.ActualSize.X - 1 && postion.Y > 0 && postion.Y < ele.ActualSize.Y)
            {
                // 从右侧移出，此时进入到所有游戏图标区域，不隐藏
                return;
            }
        }
        // 其他方向移出，隐藏所有游戏图标
        GameIconsAreaVisible = false;
    }



    /// <summary>
    /// 鼠标移出所有游戏图标区域
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIconsArea_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (FullBackgroundVisible || IsPinned)
        {
            return;
        }
        GameIconsAreaVisible = false;
    }



    /// <summary>
    /// 点击没有被选择的游戏图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIcon_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon icon)
        {
            if (CurrentGameBizIcon is not null)
            {
                CurrentGameBizIcon.IsSelected = false;
            }

            CurrentGameBizIcon = icon;
            CurrentGameBiz = icon.GameBiz;
            CurrentGameId = icon.GameId;
            icon.IsSelected = true;

            CurrentGameChanged?.Invoke(this, (icon.GameId, false));
            AppConfig.CurrentGameBiz = icon.GameBiz;
        }
    }



    /// <summary>
    /// 双击没有被选择的游戏图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIcon_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon icon)
        {
            if (CurrentGameBizIcon is not null)
            {
                CurrentGameBizIcon.IsSelected = false;
            }

            CurrentGameBizIcon = icon;
            CurrentGameBiz = icon.GameBiz;
            CurrentGameId = icon.GameId;
            icon.IsSelected = true;
            HideFullBackground();

            CurrentGameChanged?.Invoke(this, (icon.GameId, true));
            AppConfig.CurrentGameBiz = icon.GameBiz;
        }
    }


    /// <summary>
    /// 鼠标移入到游戏图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIcon_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon icon)
        {
            if (!icon.IsSelected)
            {
                icon.MaskOpacity = 0;
            }
        }
    }



    /// <summary>
    /// 鼠标移出游戏图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIcon_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon icon)
        {
            if (!icon.IsSelected)
            {
                icon.MaskOpacity = 1;
            }
        }
    }



    /// <summary>
    /// 游戏图标区域大小变化
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameIconsArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDragRectangles();
    }



    /// <summary>
    /// 固定游戏待选区
    /// </summary>
    [RelayCommand]
    private void Pin()
    {
        IsPinned = !IsPinned;
        if (IsPinned)
        {
            GameIconsAreaVisible = true;
        }
        else
        {
            // 避免在固定时更换当前游戏，取消固定后，左上角的图标不改变的问题
            var temp = CurrentGameBizIcon;
            CurrentGameBizIcon = null;
            CurrentGameBizIcon = temp;
            if (!FullBackgroundVisible)
            {
                GameIconsAreaVisible = false;
            }
        }
        AppConfig.IsGameBizSelectorPinned = IsPinned;
    }



    /// <summary>
    /// 待选游戏变更时，保存配置
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void GameBizIcons_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        try
        {
            var sb = new StringBuilder();
            foreach (var icon in GameBizIcons)
            {
                // 自定义游戏不进 SelectedGameBizs —— 它们的身份由 games.json 管，写进去下次也认不出来
                if (icon.IsCustom)
                {
                    continue;
                }

                sb.Append(icon.GameBiz);
                sb.Append(',');
            }
            AppConfig.SelectedGameBizs = sb.ToString().TrimEnd(',');
        }
        catch { }
    }



    #endregion





    #region Full Background 黑色半透明背景


    public bool FullBackgroundVisible => Border_FullBackground.Opacity > 0;


    [RelayCommand]
    private void ShowFullBackground()
    {
        Border_FullBackground.Opacity = 1;
        Border_FullBackground.IsHitTestVisible = true;
        Border_FullBackground.Visibility = Visibility.Visible;
        Border_Pin.Opacity = 1;
        Border_Pin.IsHitTestVisible = true;
        Border_Pin.Visibility = Visibility.Visible;
        GameIconsAreaVisible = true;
    }


    private void HideFullBackground()
    {
        Border_FullBackground.Opacity = 0;
        Border_FullBackground.IsHitTestVisible = false;
        Border_FullBackground.Visibility = Visibility.Collapsed;
        Border_Pin.Opacity = 0;
        Border_Pin.IsHitTestVisible = false;
        Border_Pin.Visibility = Visibility.Collapsed;
        if (!IsPinned)
        {
            GameIconsAreaVisible = false;
        }
    }


    private void Border_FullBackground_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        _isGameBizDisplayPressed = false;
        var position = e.GetPosition(sender as UIElement);
        if (position.X <= Border_CurrentGameIcon.ActualWidth && position.Y <= Border_CurrentGameIcon.ActualHeight)
        {
            Border_FullBackground.Opacity = 0;
            Border_FullBackground.IsHitTestVisible = false;
            Border_FullBackground.Visibility = Visibility.Collapsed;
            Border_Pin.Opacity = 0;
            Border_Pin.IsHitTestVisible = false;
            Border_Pin.Visibility = Visibility.Collapsed;
        }
        else
        {
            HideFullBackground();
        }
    }


    #endregion





    #region Game Server



    public List<GameBizDisplay> GameBizDisplays { get; set => SetProperty(ref field, value); }




    /// <summary>
    /// 初始化游戏服务器选择区域
    /// </summary>
    private void InitializeGameServerArea(List<GameInfo> gameInfos)
    {
        try
        {
            var list = new List<GameBizDisplay>();

            if (LanguageUtil.FilterLanguage(CultureInfo.CurrentUICulture.Name) is "zh-cn")
            {
                // 当前语言为简体中文时，游戏信息显示从中国官服获取的内容
                foreach (var info in gameInfos)
                {
                    if (info.GameBiz.IsChinaServer() && !info.IsBilibiliServer())
                    {
                        list.Add(new GameBizDisplay { GameInfo = info });
                    }
                }
            }
            else
            {
                // 当前语言不为简体中文时，游戏信息显示从国际服获取的内容
                foreach (var info in gameInfos)
                {
                    if (info.GameBiz.IsGlobalServer())
                    {
                        list.Add(new GameBizDisplay { GameInfo = info });
                    }
                }
            }

            // 手动添加星布谷地（因为它不在HoYoPlay API中）
            // 将其插入到列表的开头，成为第一个游戏
            var ppDisplay = new GameBizDisplay 
            { 
                GameInfo = new GameInfo 
                { 
                    Id = "pp_cbt1",
                    GameBiz = GameBiz.pp_cbt1,
                    Display = new GameInfoDisplay
                    {
                        Name = HoYoShadeHub.Core.Localization.CoreLang.Game_PetitPlanet,
                        Icon = new GameImage { Url = "ms-appx:///Assets/Image/icon_pp.jpg" },
                        Background = new GameImage { Url = "ms-appx:///Assets/Image/background_pp.png" },
                        Logo = new GameImage { Url = GetPpLogoPath() },  // 使用语言特定的Logo
                        Thumbnail = new GameImage { Url = "ms-appx:///Assets/Image/background_pp.png" },
                    },
                    DisplayStatus = GameInfoDisplayStatus.LAUNCHER_GAME_DISPLAY_STATUS_AVAILABLE
                }
            };
            list.Insert(0, ppDisplay);  // 插入到列表开头，成为第一个

            // 手动添加崩坏：因缘精灵（因为它可能不在HoYoPlay API中）
            // 将其插入到第二个位置
            var hnaDisplay = new GameBizDisplay 
            { 
                GameInfo = new GameInfo 
                { 
                    Id = "hna_cbt1",
                    GameBiz = GameBiz.hna_cbt1,
                    Display = new GameInfoDisplay
                    {
                        Name = HoYoShadeHub.Core.Localization.CoreLang.Game_NexusAnima,
                        Icon = new GameImage { Url = "ms-appx:///Assets/Image/icon_hna.jpg" },
                        Background = new GameImage { Url = "ms-appx:///Assets/Image/background_hna.png" },
                        Logo = new GameImage { Url = GetHnaLogoPath() },  // 使用语言特定的Logo
                        Thumbnail = new GameImage { Url = "ms-appx:///Assets/Image/background_hna.png" },
                    },
                    DisplayStatus = GameInfoDisplayStatus.LAUNCHER_GAME_DISPLAY_STATUS_AVAILABLE
                }
            };
            list.Insert(1, hnaDisplay);  // 插入到第二个位置

            // 分类每个游戏的服务器信息
            foreach (var item in list)
            {
                string game = item.GameInfo.GameBiz.Game;
                
                // 根据游戏类型确定要检查的服务器后缀
                List<string> suffixes = new() { "_cn", "_global", "_bilibili" };
                
                // 崩坏三需要额外添加测试服
                if (game == GameBiz.bh3)
                {
                    suffixes.Add("_beta");
                }
                
                // 原神需要额外添加测试服
                if (game == GameBiz.hk4e)
                {
                    suffixes.Add("_cn_beta");
                    suffixes.Add("_os_beta");
                }
                
                // 崩坏：星穹铁道需要额外添加测试服
                if (game == GameBiz.hkrpg)
                {
                    suffixes.Add("_beta");
                }
                
                // 绝区零需要额外添加测试服
                if (game == GameBiz.nap)
                {
                    suffixes.Add("_beta_prebeta");
                    suffixes.Add("_beta_postbeta");
                }
                
                // 星布谷地（仅有第一次内测）
                if (game == GameBiz.pp)
                {
                    suffixes.Clear(); // 星布谷地没有正式服，只有内测
                    suffixes.Add("_cbt1");
                }
                
                // 崩坏：因缘精灵（仅有第一次内测）
                if (game == GameBiz.hna)
                {
                    suffixes.Clear(); // 因缘精灵没有正式服，只有内测
                    suffixes.Add("_cbt1");
                }
                
                foreach (string suffix in suffixes)
                {
                    GameBiz biz = game + suffix;
                    if (biz.IsKnown())
                    {
                        var server = new GameBizIcon(biz)
                        {
                            IsPinned = GameBizIcons.Any(x => x.GameBiz == biz),
                        };
                        item.Servers.Add(server);
                    }
                    else if (gameInfos.FirstOrDefault(x => x.GameBiz == biz) is GameInfo info)
                    {
                        var server = new GameBizIcon(info)
                        {
                            IsPinned = GameBizIcons.Any(x => x.GameBiz == biz),
                        };
                        item.Servers.Add(server);
                    }
                }
            }

            // 自定义游戏：也给它一张卡片（背景用默认背景图，图标用从 exe 抠出来的）
            foreach (GameEntry entry in GameCatalog.CustomEntries())
            {
                list.Add(BuildCustomGameDisplay(entry));
            }

            GameBizDisplays = new(list);
        }
        catch { }
    }



    /// <summary>
    /// 把一个 GameBiz 固定到顶部图标行（已经在了就只标一下状态）。
    /// 图标行是靠 <c>SelectedGameBizs</c> 持久化的，所以固定之后重启还在。
    /// </summary>
    private void PinGameBiz(GameBiz biz)
    {
        if (string.IsNullOrWhiteSpace(biz.Value))
        {
            return;
        }

        if (GameBizIcons.FirstOrDefault(x => x.GameBiz == biz) is { } existing)
        {
            existing.IsPinned = true;
            return;
        }

        GameBizIcon? icon = GameBizDisplays?
            .FirstOrDefault(d => d.GameInfo?.GameBiz == biz)?
            .Servers.FirstOrDefault(s => s.GameBiz == biz);

        icon ??= biz.IsKnown() ? new GameBizIcon(biz) : null;

        if (icon is null)
        {
            return;
        }

        icon.IsPinned = true;
        GameBizIcons.Add(icon);

        _logger.LogInformation("Pinned {GameBiz} to the game icon row", biz.Value);
    }



    /// <summary>自定义游戏在选择界面里的那张卡片</summary>
    private GameBizDisplay BuildCustomGameDisplay(GameEntry entry)
    {
        string icon = GameIconExtractor.GetCached(entry) ?? GameIconExtractor.FallbackIcon;

        // 用户自己设过背景就用它（跟已知游戏走的是同一套 custom_bg_{biz} 设置）
        string background = GameIconExtractor.CustomBackground;
        var gameId = new GameId { Id = entry.CustomBizValue, GameBiz = entry.CustomBiz };
        if (BackgroundService.TryGetCustomBgFilePath(gameId, out string? customBg) && customBg is not null)
        {
            // 卡片用的是 CachedImage，播不了视频 —— 是视频就换成随包的静帧（蓝色星原那种动图背景）
            background = new Uri(GameCatalog.CardBackgroundFor(entry, customBg) ?? customBg).AbsoluteUri;
        }

        var display = new GameBizDisplay
        {
            IsCustom = true,
            GameInfo = new GameInfo
            {
                Id = entry.CustomBizValue,
                GameBiz = entry.CustomBiz,
                Display = new GameInfoDisplay
                {
                    Name = entry.DisplayName,
                    Icon = new GameImage { Url = icon },
                    Logo = new GameImage { Url = icon },
                    Background = new GameImage { Url = background },
                    Thumbnail = new GameImage { Url = background },
                },
                DisplayStatus = GameInfoDisplayStatus.LAUNCHER_GAME_DISPLAY_STATUS_AVAILABLE,
            },
        };

        // 卡片点开就是服务器列表 —— 自定义游戏只有「它自己」这一项
        display.Servers.Add(GameBizIcons.FirstOrDefault(x => x.IsCustom && x.GameBiz == entry.CustomBiz)
                            ?? new GameBizIcon(entry));

        return display;
    }



    /// <summary>把新加的自定义游戏补进选择界面（已经在了就跳过）</summary>
    private void RefreshCustomGameDisplays()
    {
        // 自定义那几张整条重来（背景 / 图标都可能刚变），已知游戏的原样留着
        List<GameBizDisplay> displays = [.. (GameBizDisplays ?? []).Where(d => !d.IsCustom)];

        foreach (GameEntry entry in GameCatalog.CustomEntries())
        {
            displays.Add(BuildCustomGameDisplay(entry));
        }

        GameBizDisplays = displays;
    }



    /// <summary>
    /// 更新所有游戏服务器信息，更新游戏图标
    /// </summary>
    /// <returns></returns>
    private async Task UpdateGameInfoAsync()
    {
        try
        {
            List<GameInfo> gameInfos = await _hoyoplayService.UpdateGameInfoListAsync();
            InitializeGameServerArea(gameInfos);
            foreach (GameBizIcon icon in GameBizIcons)
            {
                if (icon.GameBiz.IsKnown())
                {
                    icon.UpdateInfo();
                }
                else
                {
                    GameInfo info = await _hoyoplayService.GetGameInfoAsync(icon.GameId);
                    icon.UpdateInfo(info);
                }
            }
            await InitializeInstalledGamesCommand.ExecuteAsync(null);
        }
        catch { }
    }



    /// <summary>
    /// 游戏服务器选择区域是否可见
    /// </summary>
    private bool _isGameBizDisplayPressed = false;


    /// <summary>
    /// 鼠标点击游戏服务器选择区域，显示游戏服务器选择菜单
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameBizDisplay_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (sender is FrameworkElement ele)
        {
            e.Handled = true;
            _isGameBizDisplayPressed = !_isGameBizDisplayPressed;
            FlyoutBase.GetAttachedFlyout(ele)?.ShowAt(ele, new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.Bottom,
                ShowMode = FlyoutShowMode.Transient,
            });
        }
    }


    /// <summary>
    /// 鼠标移入到游戏服务器选择区域
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Grid_GameBizDisplay_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement ele && _isGameBizDisplayPressed)
        {
            FlyoutBase.GetAttachedFlyout(ele)?.ShowAt(ele, new FlyoutShowOptions
            {
                Placement = FlyoutPlacementMode.Bottom,
                ShowMode = FlyoutShowMode.Transient,
            });
        }
    }


    /// <summary>
    /// 点击服务器图标
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Button_GameServer_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon server)
            {
                if (CurrentGameBizIcon is not null)
                {
                    CurrentGameBizIcon.IsSelected = false;
                }

                if (GameBizIcons.FirstOrDefault(x => x.GameId == server.GameId) is GameBizIcon icon)
                {
                    CurrentGameBizIcon = icon;
                    CurrentGameBiz = icon.GameBiz;
                    CurrentGameId = icon.GameId;
                    icon.IsSelected = true;
                }
                else
                {
                    CurrentGameBizIcon = server;
                    server.IsSelected = true;
                }

                CurrentGameChanged?.Invoke(this, (server.GameId, false));
                // 关闭弹出的服务器选择菜单
                if (VisualTreeHelper.GetOpenPopupsForXamlRoot(this.XamlRoot).FirstOrDefault() is Popup popup)
                {
                    popup.IsOpen = false;
                }
                HideFullBackground();
                _isGameBizDisplayPressed = false;
                AppConfig.CurrentGameBiz = server.GameBiz;
            }
        }
        catch { }
    }


    /// <summary>
    /// 固定游戏服务器图标到待选区
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Button_PinGameBiz_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is GameBizIcon server)
        {
            var biz = server.GameBiz;
            if (GameBizIcons.FirstOrDefault(x => x.GameBiz == biz) is GameBizIcon icon)
            {
                GameBizIcons.Remove(icon);
                server.IsPinned = false;
            }
            else
            {
                GameBizIcons.Add(server);
                server.IsPinned = true;
            }
        }
    }




    #endregion





    #region Installed Games


    /// <summary>
    /// 已安装游戏的实际占用空间
    /// </summary>
    public string? InstalledGamesActualSize { get; set => SetProperty(ref field, value); }


    /// <summary>
    /// 已安装游戏的总空间
    /// </summary>
    public string? InstalledGamesSavedSize { get; set => SetProperty(ref field, value); }




    public ObservableCollection<GameBizIcon> InstalledGames { get; set; } = new();



    private CancellationTokenSource? _initializeInstalledGamesCancellationTokenSource;


    /// <summary>
    /// 初始化已安装游戏列表，计算已安装游戏的实际占用空间
    /// </summary>
    /// <returns></returns>
    [RelayCommand]
    private async Task InitializeInstalledGamesAsync()
    {
        const double GB = 1 << 30;
        try
        {
            _initializeInstalledGamesCancellationTokenSource?.Cancel();
            _initializeInstalledGamesCancellationTokenSource = new();
            CancellationToken token = _initializeInstalledGamesCancellationTokenSource.Token;

            InstalledGames.Clear();
            InstalledGamesActualSize = null;
            InstalledGamesSavedSize = null;
            List<FileInfo> files = new();

            foreach (GameBizDisplay display in GameBizDisplays)
            {
                List<FileInfo> _duplicateFiles = new();
                int serverCount = 0;
                foreach (GameBizIcon server in display.Servers)
                {
                    string? installPath = GameLauncherService.GetGameInstallPath(server.GameId);
                    if (Directory.Exists(installPath))
                    {
                        server.InstallPath = installPath;
                        var _files = new DirectoryInfo(installPath).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
                        server.TotalSize = _files.Sum(x => x.Length);
                        InstalledGames.Add(server);
                        if (_files.Count() > 0)
                        {
                            serverCount++;
                            _duplicateFiles.AddRange(_files);
                        }
                    }
                }
                if (serverCount > 1)
                {
                    files.AddRange(_duplicateFiles);
                }
            }
            long totalSize = InstalledGames.Sum(x => x.TotalSize);
            InstalledGamesActualSize = $"{totalSize / GB:F2}GB";

            if (token.IsCancellationRequested)
            {
                return;
            }

            if (files.Count > 0)
            {
                (long fileSize, long actualSize) = await Task.Run(() =>
                {
                    long size = 0;
                    Dictionary<string, long> dic = new();
                    foreach (var file in files)
                    {
                        size += file.Length;
                        using var handle = File.OpenHandle(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var idInfo = Kernel32.GetFileInformationByHandleEx<Kernel32.FILE_ID_INFO>(handle, Kernel32.FILE_INFO_BY_HANDLE_CLASS.FileIdInfo);
                        var idInfoBytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref idInfo, 1));
                        dic[Convert.ToHexString(idInfoBytes)] = file.Length;
                    }
                    return (size, dic.Values.Sum());
                }, token);

                if (token.IsCancellationRequested)
                {
                    return;
                }

                InstalledGamesActualSize = $"{(totalSize - fileSize + actualSize) / GB:F2}GB";
                InstalledGamesSavedSize = $"{(fileSize - actualSize) / GB:F2}GB";
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }




    /// <summary>
    /// 从注册表自动搜索已安装的游戏
    /// </summary>
    /// <summary>
    /// 从注册表自动搜索已安装的游戏。
    ///
    /// <para>
    /// 两条硬性要求（都是用户踩出来的）：
    /// <list type="number">
    /// <item><b>只增不减</b>：搜不到东西时绝不能把 SelectedGameBizs 清掉 —— 原来是直接覆盖，
    /// DB 一重建（缓存列表空）就变成「点一下自动查找，顶部只剩自定义游戏」；</item>
    /// <item><b>不依赖 DB 缓存</b>：GetCachedGameInfos() 为空（DB 被重建过）就退化成遍历所有已知 biz，
    /// 靠注册表里的 GameInstallPath 找回来。</item>
    /// </list>
    /// </para>
    /// </summary>
    [RelayCommand]
    public void AutoSearchInstalledGames()
    {
        try
        {
            var found = new List<string>(SelectedBizsFromConfig());
            int before = found.Count;

            foreach (GameBiz gameBiz in CandidateGameBizs())
            {
                if (TryFindInstallPath(gameBiz) is { } path)
                {
                    AppConfig.SetGameInstallPath(gameBiz, path);
                    AddFoundBiz(found, gameBiz);
                }
            }

            // 手动检查测试服（它们可能不在 gameInfos 里）
            foreach (GameBiz betaBiz in new[]
                     {
                         GameBiz.bh3_beta,
                         GameBiz.hk4e_cn_beta,
                         GameBiz.hk4e_os_beta,
                         GameBiz.hkrpg_beta,
                         GameBiz.nap_beta_prebeta,
                         GameBiz.nap_beta_postbeta,
                         GameBiz.pp_cbt1,
                         GameBiz.hna_cbt1,
                     })
            {
                if (TryFindInstallPath(betaBiz) is { } path)
                {
                    AppConfig.SetGameInstallPath(betaBiz, path);
                    AddFoundBiz(found, betaBiz);
                }
            }

            if (found.Count == before)
            {
                InAppToast.MainWindow?.Information("自动查找",
                    "没找到新的已安装游戏。装好的游戏也可以点左上角那个按钮，自己指定它的目录。", 8000);
                return;
            }

            AppConfig.SelectedGameBizs = string.Join(',', found);
            InitializeGameSelector();
            if (!IsPinned)
            {
                Pin();
            }

            InAppToast.MainWindow?.Information("自动查找",
                $"找到了 {found.Count - before} 个游戏，已经放到顶部。", 6000);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }



    /// <summary>config 里已经选中的那些 biz</summary>
    private static IEnumerable<string> SelectedBizsFromConfig() =>
        (AppConfig.SelectedGameBizs ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>搜哪些 biz：优先 DB 缓存里的游戏信息，缓存空就遍历所有已知 biz</summary>
    private IEnumerable<GameBiz> CandidateGameBizs()
    {
        bool any = false;

        foreach (GameInfo item in GetCachedGameInfos())
        {
            any = true;
            GameBiz gameBiz = item.GameBiz;
            yield return item.IsBilibiliServer() ? $"{gameBiz.Game}_bilibili" : gameBiz;
        }

        if (!any)
        {
            foreach (GameBiz gameBiz in GameBiz.AllGameBizs)
            {
                yield return gameBiz;
            }
        }
    }

    private static void AddFoundBiz(List<string> found, GameBiz gameBiz)
    {
        if (!found.Contains(gameBiz.Value, StringComparer.OrdinalIgnoreCase))
        {
            found.Add(gameBiz.Value);
        }
    }

    /// <summary>这个 biz 的安装目录：已配的路径 → 注册表里的 GameInstallPath；都没有返回 null</summary>
    private static string? TryFindInstallPath(GameBiz gameBiz)
    {
        string? path = GameLauncherService.GetGameInstallPath(gameBiz);
        if (!string.IsNullOrWhiteSpace(path)
            && (Directory.Exists(path) || AppConfig.GetGameInstallPathRemovable(gameBiz)))
        {
            return path;
        }

        string key = string.Empty;
        if (gameBiz.Server is "cn")
        {
            key = $@"HKEY_CURRENT_USER\Software\miHoYo\HYP\1_1\{gameBiz}";
        }
        else if (gameBiz.Server is "global")
        {
            key = $@"HKEY_CURRENT_USER\Software\Cognosphere\HYP\1_0\{gameBiz}";
        }
        else if (gameBiz.IsBetaServer())
        {
            key = gameBiz.GetGameRegistryKey();
        }

        if (string.IsNullOrWhiteSpace(key) || key == "HKEY_CURRENT_USER")
        {
            return null;
        }

        path = Registry.GetValue(key, "GameInstallPath", null) as string;
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// 防止点击已安装游戏列表时，触发 <see cref="Border_FullBackground_Tapped(object, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs)"/>
    /// </summary>
    /// <param name="sender"></param>
    /// <param name="e"></param>
    private void Expander_InstalledGamesActualSize_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        e.Handled = true;
    }



    #endregion







}


