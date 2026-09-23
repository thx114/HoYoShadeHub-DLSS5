using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Features.GameLauncher;
using HoYoShadeHub.Features.RPC;
using HoYoShadeHub.Features.UrlProtocol;
using HoYoShadeHub.Frameworks;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.System;


namespace HoYoShadeHub.Features.Setting;

public sealed partial class AdvancedSetting : PageBase
{

    private readonly ILogger<AdvancedSetting> _logger = AppConfig.GetLogger<AdvancedSetting>();


    private readonly RpcService _rpcService = AppConfig.GetService<RpcService>();


    public AdvancedSetting()
    {
        this.InitializeComponent();
    }



    protected override void OnLoaded()
    {
        _ = GetRpcServerStateAsync();
        CheckUrlProtocol();
        RefreshFpsUnlockStatus();
    }

    #region 帧率解锁数据更新

    [ObservableProperty]
    private bool _isCheckingFpsUnlockUpdate;

    public bool CanCheckFpsUnlockUpdate => !IsCheckingFpsUnlockUpdate;

    partial void OnIsCheckingFpsUnlockUpdateChanged(bool value)
        => OnPropertyChanged(nameof(CanCheckFpsUnlockUpdate));

    [ObservableProperty]
    private string? _fpsUnlockStatusText;

    private void RefreshFpsUnlockStatus()
    {
        DateTimeOffset? updatedAt = FpsUnlockDataService.GetUpdatedAt();
        FpsUnlockStatusText = updatedAt is { } at
            ? $"数据更新于 {at.LocalDateTime:yyyy-MM-dd HH:mm}"
            : "暂无本地数据";
    }

    private async void Button_CheckFpsUnlockUpdate_Click(object sender, RoutedEventArgs e)
    {
        GameId gameId = GameId.FromGameBiz(GameBiz.hk4e_cn)
                       ?? GameId.FromGameBiz(GameBiz.hk4e_global);

        if (gameId is null)
        {
            FpsUnlockStatusText = "找不到原神游戏标识";
            return;
        }

        string gameVersion = string.Empty;
        try
        {
            GameLauncherService launcher = AppConfig.GetService<GameLauncherService>();
            gameVersion = (await launcher.GetLocalGameVersionAsync(gameId))?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Read game version for manual fps unlock check");
        }

        IsCheckingFpsUnlockUpdate = true;
        FpsUnlockStatusText = "正在拉取上游数据…";

        FpsUnlockDataService.UpdateResult result;
        try
        {
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
            result = await FpsUnlockDataService.CheckManuallyAsync(gameId, gameVersion, cts.Token);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Manual fps unlock data check failed");
            result = FpsUnlockDataService.UpdateResult.Failed;
        }

        IsCheckingFpsUnlockUpdate = false;

        FpsUnlockStatusText = result switch
        {
            FpsUnlockDataService.UpdateResult.Updated => "已更新到最新数据，帧率解锁恢复启用",
            FpsUnlockDataService.UpdateResult.Unchanged => "已是最新数据（上游未发布新版本）",
            _ => "检查失败，请确认网络/代理后重试",
        };

        _logger.LogInformation("Manual FPS unlock data check: {Result}", result);
    }

    #endregion





    #region URL Protocol



    [ObservableProperty]
    public bool _EnableUrlProtocol;


    partial void OnEnableUrlProtocolChanged(bool value)
    {
        try
        {
            if (value)
            {
                UrlProtocolService.RegisterProtocol();
            }
            else
            {
                UrlProtocolService.UnregisterProtocol();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Enable url protocol changed");
        }
    }



    private async void CheckUrlProtocol()
    {
        try
        {
            var status = await Launcher.QueryUriSupportAsync(new Uri("hoyoshadehub://"), LaunchQuerySupportType.Uri);
#pragma warning disable MVVMTK0034 // Direct field reference to [ObservableProperty] backing field
            _EnableUrlProtocol = status is LaunchQuerySupportStatus.Available;
#pragma warning restore MVVMTK0034 // Direct field reference to [ObservableProperty] backing field
            OnPropertyChanged(nameof(EnableUrlProtocol));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Check url protocol");
        }
    }




    [RelayCommand]
    private async Task TestUrlProtocolAsync()
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri("hoyoshadehub://test"));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Test url protocol");
        }
    }


    #endregion




    #region RPC


    public bool KeepRpcServerRunningInBackground
    {
        get;
        set
        {
            if (SetProperty(ref field, value))
            {
                AppConfig.KeepRpcServerRunningInBackground = value;
                SetRpcServerRunning(value);
            }
        }
    } = AppConfig.KeepRpcServerRunningInBackground;



    private void SetRpcServerRunning(bool value)
    {
        try
        {
            _rpcService.KeepRunningOnExited(value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Set rpc server running");
        }
    }



    public int RPCServerProcessId { get; set => SetProperty(ref field, value); }



    private async Task GetRpcServerStateAsync()
    {
        try
        {
            RPCServerProcessId = 0;
            StackPanel_RpcState_NotRunning.Visibility = Visibility.Collapsed;
            StackPanel_RpcState_Running.Visibility = Visibility.Collapsed;
            StackPanel_RpcState_CannotConnect.Visibility = Visibility.Collapsed;
            if (RpcService.CheckRpcServerRunning())
            {
                var info = await _rpcService.GetRpcServerInfoAsync(DateTime.UtcNow.AddSeconds(3));
                RPCServerProcessId = info.ProcessId;
                StackPanel_RpcState_Running.Visibility = Visibility.Visible;
            }
            else
            {
                StackPanel_RpcState_NotRunning.Visibility = Visibility.Visible;
            }
        }
        catch (RpcException ex) when (ex.Status is { StatusCode: StatusCode.DeadlineExceeded })
        {
            int sessionId = Process.GetCurrentProcess().SessionId;
            var process = Process.GetProcessesByName("HoYoShadeHub.RPC").FirstOrDefault(x => x.SessionId == sessionId);
            if (process != null)
            {
                RPCServerProcessId = process.Id;
                StackPanel_RpcState_CannotConnect.Visibility = Visibility.Visible;
            }
            else
            {
                StackPanel_RpcState_NotRunning.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Get rpc server state");
        }
    }


    [RelayCommand]
    private async Task RunRpcServerAsync()
    {
        try
        {
            await _rpcService.EnsureRpcServerRunningAsync();
            await GetRpcServerStateAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Run rpc server");
        }
    }



    [RelayCommand]
    private async Task StopRpcServerAsync()
    {
        try
        {
            await _rpcService.StopRpcServerAsync(DateTime.UtcNow.AddSeconds(3));
            await Task.Delay(1000);
            await GetRpcServerStateAsync();
        }
        catch (RpcException ex) when (ex.Status is { StatusCode: StatusCode.DeadlineExceeded })
        {
            try
            {
                var p = Process.GetProcessById(RPCServerProcessId);
                p.Kill();
                await Task.Delay(1000);
                await GetRpcServerStateAsync();
            }
            catch { }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Stop rpc server");
        }
    }





    #endregion


}
