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
    #region 注入模式（只影响当前这个游戏）

    /// <summary>当前游戏的条目 + 游戏表（注入模式的开关存在里面）</summary>
    private GameDiscoveryService? _gameDiscovery;
    private GameEntry? _currentGameEntry;

    private bool _useInjectMode;

    /// <summary>
    /// 「注入模式」开关。**按游戏存**（见 docs/GAMES-AND-INJECT.md §5）：
    /// 勾上之后点开始游戏会走 inject.exe，而不是等 Hub 直接把游戏拉起来。
    /// </summary>
    public bool UseInjectMode
    {
        get => _useInjectMode;
        set
        {
            if (!SetProperty(ref _useInjectMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsShadeInjectMode));
            OnPropertyChanged(nameof(IsWaitProcessMode));
            OnPropertyChanged(nameof(CanUseXxmiInject));

            // 注入模式：游戏由用户自己拉起来，而 XXMI 必须由它自己启动游戏 —— 两者冲突，
            // 所以一开注入模式就把「启用XXMI」关掉并提醒（用户要求）
            if (value && UseXxmiInject && !_isApplyingSavedLaunchOptions)
            {
                UseXxmiInject = false;
                DispatcherQueue?.TryEnqueue(() => InAppToast.MainWindow?.Warning("注入模式",
                    "注入模式下不会替你启动游戏，而 XXMI 要自己把游戏拉起来 —— 两者不能同时用，已把「启用XXMI」关掉。", 10000));
            }

            if (_isApplyingSavedLaunchOptions || _gameDiscovery is null || _currentGameEntry is null)
            {
                return;
            }

            try
            {
                GameCatalog.SetInjectMode(_gameDiscovery, _currentGameEntry, value);
                _logger.LogInformation("Inject mode for {Game}: {Value}", _currentGameEntry.DisplayName, value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save inject mode");
            }
        }
    }

    public bool CanUseInjectMode => _currentGameEntry is not null;

    private bool _skipShadeInjector;

    /// <summary>
    /// 「跳过 HoYoShade 注入器」开关，按游戏存：勾上后启动时不走 inject.exe，
    /// 改由 Hub 自己的 DllInjector 等游戏进程注 ReShade64.dll（和鸣潮黑名单绕行同一条路径）。
    /// 用于实测/绕开 inject.exe 的行为差异。
    /// </summary>
    public bool SkipShadeInjector
    {
        get => _skipShadeInjector;
        set
        {
            if (!SetProperty(ref _skipShadeInjector, value))
            {
                return;
            }

            if (_isApplyingSavedLaunchOptions || _gameDiscovery is null || _currentGameEntry is null)
            {
                return;
            }

            try
            {
                GameCatalog.SetSkipShadeInjector(_gameDiscovery, _currentGameEntry, value);
                _logger.LogInformation("Skip HoYoShade injector for {Game}: {Value}", _currentGameEntry.DisplayName, value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Save skip-shade-injector");
            }
        }
    }

    /// <summary>读当前游戏的注入模式（跟着客户端切换走）</summary>
    private void LoadInjectModeForCurrentClient()
    {
        try
        {
            _gameDiscovery = GameCatalog.CreateService();
            _currentGameEntry = GameCatalog.GetOrCreate(_gameDiscovery, CurrentGameId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Load game entry");
            _gameDiscovery = null;
            _currentGameEntry = null;
        }

        UseInjectMode = _currentGameEntry?.UseInjectMode ?? false;
        SkipShadeInjector = _currentGameEntry?.SkipShadeInjector ?? false;
        OnPropertyChanged(nameof(CanUseInjectMode));

        // 老配置里注入模式和 XXMI 都开着：注入模式优先，把 XXMI 关掉
        if (UseInjectMode && _useXxmiInject)
        {
            UseXxmiInject = false;
        }
    }

    /// <summary>注入模式开关（用户要求：别再弹「注入模式」介绍提示了，说明都在控件 tooltip 里）</summary>
    private void CheckBox_InjectMode_Changed(object sender, RoutedEventArgs e)
    {
    }



    #endregion

}
