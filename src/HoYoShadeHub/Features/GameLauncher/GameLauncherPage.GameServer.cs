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
    #region Game Server


    private List<GameServerConfig>? _gameServers;
    public List<GameServerConfig>? GameServers
    {
        get => _gameServers;
        set => SetProperty(ref _gameServers, value);
    }

    [ObservableProperty]
    public partial GameServerConfig? SelectedGameServer { get; set; }
    partial void OnSelectedGameServerChanged(GameServerConfig? oldValue, GameServerConfig? newValue)
    {
        if (oldValue is not null && newValue is not null)
        {
            AppConfig.LastGameIdOfBH3Global = newValue.GameId;
            WeakReferenceMessenger.Default.Send(new BH3GlobalGameServerChangedMessage(newValue.GameId));
        }
    }


    /// <summary>
    /// 初始化区服选项，仅崩坏三国际服使用
    /// </summary>
    /// <returns></returns>
    private async Task InitializeGameServerAsync()
    {
        try
        {
            // 自定义游戏 / Hub 不认识的：HoYoPlay 那边没有区服信息
            if (_currentGameEntry?.IsCustom == true || CurrentGameBiz.Value.StartsWith("custom_", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            GameInfo? gameInfo;
            if (CurrentGameBiz == GameBiz.bh3_global)
            {
                gameInfo = await _hoYoPlayService.GetGameInfoAsync(GameId.FromGameBiz(GameBiz.bh3_global)!);
            }
            else
            {
                gameInfo = await _hoYoPlayService.GetGameInfoAsync(CurrentGameId);
            }
            if (gameInfo?.GameServerConfigs?.Count > 0)
            {
                GameServers = gameInfo.GameServerConfigs;
                if (GameServers.FirstOrDefault(x => x.GameId == CurrentGameId.Id) is GameServerConfig config)
                {
                    SelectedGameServer = config;
                }
                else
                {
                    SelectedGameServer = GameServers.FirstOrDefault();
                    if (SelectedGameServer is not null)
                    {
                        CurrentGameId.Id = SelectedGameServer.GameId;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Initialize game server");
        }
    }



    #endregion

}
