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
    #region AI 插帧（NVIDIA Smooth Motion，驱动级帧生成，所有游戏）

    private bool _useSmoothMotion;
    private bool _canUseSmoothMotion;
    private string? _smoothMotionUnavailableReason;
    private bool _isApplyingSmoothMotionState;
    private int _smoothMotionLoadGeneration;

    /// <summary>
    /// 「AI 插帧」开关：驱动配置里的「Smooth Motion - Enable」，和 NVIDIA App 那个开关是同一项。
    /// 勾上写 1、取消写 0；状态以驱动为准（切游戏时读一次回填），不存 Hub 配置。
    /// </summary>
    public bool UseSmoothMotion
    {
        get => _useSmoothMotion;
        set
        {
            if (!SetProperty(ref _useSmoothMotion, value))
            {
                return;
            }

            // 程序回填 / 写失败回弹时不触发写驱动
            if (_isApplyingSavedLaunchOptions || _isApplyingSmoothMotionState)
            {
                return;
            }

            if (_currentGameEntry?.ExePath is not { Length: > 0 } exePath)
            {
                return;
            }

            _ = ApplySmoothMotionAsync(exePath, value);
        }
    }

    /// <summary>没拿到 exe / 读不了驱动配置时置 false，开关禁用（整块隐藏）。</summary>
    public bool CanUseSmoothMotion => _canUseSmoothMotion;

    /// <summary>AI 插帧按钮整块的可见性：定位到游戏 exe 且能读驱动配置才显示。</summary>
    public Microsoft.UI.Xaml.Visibility IsSmoothMotionVisible =>
        _canUseSmoothMotion ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

    /// <summary>控件 tooltip：一句话说明 + 动态状态尾巴。</summary>
    public string SmoothMotionToolTip
    {
        get
        {
            string tail = string.IsNullOrWhiteSpace(_smoothMotionUnavailableReason)
                ? "勾上 = 开（低延迟会设为 Ultra），重启游戏生效。"
                : "　当前不可用：" + _smoothMotionUnavailableReason;
            return "NVIDIA Smooth Motion 驱动级 AI 插帧（RTX 40/50，无需游戏支持），和 NVIDIA App 里的开关同步。" + tail;
        }
    }

    /// <summary>切游戏 / 进页面时回填开关：先压回「不可用」，后台读完驱动再恢复（读驱动不能卡 UI）。</summary>
    private void LoadSmoothMotionForCurrentClient()
    {
        int generation = ++_smoothMotionLoadGeneration;

        _isApplyingSmoothMotionState = true;
        _useSmoothMotion = false;
        _canUseSmoothMotion = false;
        _smoothMotionUnavailableReason = null;
        OnPropertyChanged(nameof(UseSmoothMotion));
        OnPropertyChanged(nameof(CanUseSmoothMotion));
        OnPropertyChanged(nameof(IsSmoothMotionVisible));
        OnPropertyChanged(nameof(SmoothMotionToolTip));
        _isApplyingSmoothMotionState = false;

        string? exePath = _currentGameEntry?.ExePath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            _smoothMotionUnavailableReason = "还没找到这个游戏的 exe。";
            OnPropertyChanged(nameof(SmoothMotionToolTip));
            return;
        }

        string exeName = Path.GetFileName(exePath);
        _ = Task.Run(() =>
        {
            try
            {
                NvDrsInterop.State state = NvDrsInterop.ReadSmoothMotionEnable(exeName);
                _ = DispatcherQueue?.TryEnqueue(() =>
                {
                    if (generation != _smoothMotionLoadGeneration)
                    {
                        return;
                    }

                    // NotStored = 还没存过这条，不代表不能用（打开一次就会写进去）
                    _canUseSmoothMotion = state.Ok || state.NotStored;
                    _isApplyingSmoothMotionState = true;
                    _useSmoothMotion = state.Ok && state.Value != 0;
                    _isApplyingSmoothMotionState = false;
                    _smoothMotionUnavailableReason = _canUseSmoothMotion ? null : state.Error;
                    OnPropertyChanged(nameof(CanUseSmoothMotion));
                    OnPropertyChanged(nameof(IsSmoothMotionVisible));
                    OnPropertyChanged(nameof(UseSmoothMotion));
                    OnPropertyChanged(nameof(SmoothMotionToolTip));
                });
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Load smooth motion state for {Exe}", exeName);
            }
        });
    }

    /// <summary>写驱动配置放后台；开的时候顺手把低延迟设成 Ultra（关时还原）。失败会把勾退回去。</summary>
    private async Task ApplySmoothMotionAsync(string exePath, bool enable)
    {
        try
        {
            string exeName = Path.GetFileName(exePath);
            string gameTitle = _currentGameEntry?.DisplayName ?? exeName;
            string latencyKey = $"smooth_motion_latency_{exeName}";
            string latencyCplKey = $"smooth_motion_latency_cpl_{exeName}";
            string profileTitle = $"HoYoShadeHub - {gameTitle}";

            (NvDrsInterop.State State, string? LatencyNote) result = await Task.Run(() =>
            {
                // 先把 Smooth Motion 总开关写进去：顺带保证驱动里有这个游戏的条目，
                // 低延迟那两项就不用再依赖「这台机器以前配过这个游戏」。
                NvDrsInterop.State write = NvDrsInterop.WriteSmoothMotionEnable(exeName, enable, profileTitle, gameTitle);
                if (!write.Ok)
                {
                    return (write, (string?)null);
                }

                // 跟 NVIDIA App 一样：开插帧时把低延迟设成 Ultra（先记住原值，关时还原）
                string? note = enable
                    ? ApplyUltraLowLatency(exeName, profileTitle, gameTitle, latencyKey, latencyCplKey)
                    : RestoreUltraLowLatency(exeName, profileTitle, gameTitle, latencyKey, latencyCplKey);

                // 开插帧时：「Enabled APIs」若被设成 0，任何 API 都不允许插帧，这里补成全允许
                if (enable)
                {
                    NvDrsInterop.State apis = NvDrsInterop.ReadSmoothMotionApis(exeName);
                    if (apis.Ok && apis.Value == 0)
                    {
                        NvDrsInterop.State fixedApis = NvDrsInterop.WriteDword(
                            exeName,
                            NvDrsInterop.SmoothMotionApisSettingId,
                            NvDrsInterop.SmoothMotionAllApis,
                            "Smooth Motion - Enabled APIs",
                            NvDrsInterop.DescribeSmoothMotionApis,
                            profileTitle,
                            gameTitle);
                        string apiNote = fixedApis.Ok ? "已允许全部 API。" : $"允许的 API 没设成：{fixedApis.Error}";
                        note = string.IsNullOrWhiteSpace(note) ? apiNote : note + " " + apiNote;
                    }
                }

                return (write, note);
            });

            if (result.State.Ok)
            {
                string suffix = string.IsNullOrWhiteSpace(result.LatencyNote) ? string.Empty : " " + result.LatencyNote;
                InAppToast.MainWindow?.Success("AI 插帧", $"已把 {exeName} 的 Smooth Motion 改成 {(enable ? "ON" : "OFF")}（{result.State.Display}），重启游戏生效。{suffix}", 9000);
                _logger.LogInformation("Smooth motion for {Game}: {Value}", gameTitle, enable);
            }
            else
            {
                InAppToast.MainWindow?.Error("AI 插帧", result.State.Error ?? "写入失败", 12000);
                _isApplyingSmoothMotionState = true;
                _useSmoothMotion = !enable;
                _isApplyingSmoothMotionState = false;
                OnPropertyChanged(nameof(UseSmoothMotion));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Apply smooth motion");
        }
    }

    /// <summary>
    /// 开 AI 插帧时把低延迟模式设成 Ultra。
    /// 真正让驱动低延迟调度生效的是「Ultra Low Latency - Enabled」（0x10835000）；
    /// 「Ultra Low Latency - CPL State」（0x0005F543）只是控制面板下拉的镜像值。
    /// 两个都写；写之前先把原值记进 AppConfig，关插帧时还原。
    /// 注意：**不能**因为「读不到原值」就跳过 驱动里没存过这一项（-160）是常态，
    /// 之前就是在这一步直接 return，导致 AI 插帧开了低延迟却一直没动。
    /// </summary>
    private static string? ApplyUltraLowLatency(string exeName, string profileTitle, string gameTitle, string latencyKey, string latencyCplKey)
    {
        NvDrsInterop.State enabledBefore = NvDrsInterop.ReadDword(
            exeName,
            NvDrsInterop.UltraLowLatencyEnabledSettingId,
            NvDrsInterop.UltraLowLatencyEnabledSettingName,
            NvDrsInterop.DescribeBinary);
        AppConfig.SetValue(enabledBefore.Ok ? (int)enabledBefore.Value : -1, latencyKey);

        NvDrsInterop.State cplBefore = NvDrsInterop.ReadDword(
            exeName,
            NvDrsInterop.UltraLowLatencyCplStateSettingId,
            NvDrsInterop.UltraLowLatencyCplStateSettingName,
            NvDrsInterop.DescribeUltraLowLatencyCplState);
        AppConfig.SetValue(cplBefore.Ok ? (int)cplBefore.Value : -1, latencyCplKey);

        string? note = null;
        if (!enabledBefore.Ok || enabledBefore.Value != NvDrsInterop.UltraLowLatencyEnabledOn)
        {
            NvDrsInterop.State forced = NvDrsInterop.WriteDword(
                exeName,
                NvDrsInterop.UltraLowLatencyEnabledSettingId,
                NvDrsInterop.UltraLowLatencyEnabledOn,
                NvDrsInterop.UltraLowLatencyEnabledSettingName,
                NvDrsInterop.DescribeBinary,
                profileTitle,
                gameTitle);
            note = forced.Ok ? "低延迟模式已设为 Ultra。" : $"低延迟模式没设成：{forced.Error}";
        }

        if (!cplBefore.Ok || cplBefore.Value != NvDrsInterop.UltraLowLatencyCplUltra)
        {
            NvDrsInterop.WriteDword(
                exeName,
                NvDrsInterop.UltraLowLatencyCplStateSettingId,
                NvDrsInterop.UltraLowLatencyCplUltra,
                NvDrsInterop.UltraLowLatencyCplStateSettingName,
                NvDrsInterop.DescribeUltraLowLatencyCplState,
                profileTitle,
                gameTitle);
        }

        return note;
    }

    /// <summary>关 AI 插帧时把低延迟模式还原成开之前的值（之前没存过就还原成默认的「关」）。</summary>
    private static string? RestoreUltraLowLatency(string exeName, string profileTitle, string gameTitle, string latencyKey, string latencyCplKey)
    {
        int previous = AppConfig.GetValue(-1, latencyKey);
        uint target = previous >= 0 ? (uint)previous : 0u;

        NvDrsInterop.State back = NvDrsInterop.WriteDword(
            exeName,
            NvDrsInterop.UltraLowLatencyEnabledSettingId,
            target,
            NvDrsInterop.UltraLowLatencyEnabledSettingName,
            NvDrsInterop.DescribeBinary,
            profileTitle,
            gameTitle);

        string? note = back.Ok
            ? (previous >= 0 ? $"低延迟模式已还原成 {previous}。" : "低延迟模式已还原成默认的「关」。")
            : $"低延迟模式没还原成：{back.Error}";
        AppConfig.SetValue(-1, latencyKey);

        // CPL State 只是面板镜像值：之前驱动里没存过就不动它，免得凭空写一条。
        int previousCpl = AppConfig.GetValue(-1, latencyCplKey);
        if (previousCpl >= 0)
        {
            NvDrsInterop.WriteDword(
                exeName,
                NvDrsInterop.UltraLowLatencyCplStateSettingId,
                (uint)previousCpl,
                NvDrsInterop.UltraLowLatencyCplStateSettingName,
                NvDrsInterop.DescribeUltraLowLatencyCplState,
                profileTitle,
                gameTitle);
        }

        AppConfig.SetValue(-1, latencyCplKey);
        return note;
    }

    /// <summary>AI 插帧开关（逻辑都在 UseSmoothMotion 的 setter 里，和注入模式一样不弹介绍提示）</summary>
    private void CheckBox_SmoothMotion_Changed(object sender, RoutedEventArgs e)
    {
    }



    #endregion

}
