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
    #region OptiScaler 冲突提示（滚动跑马灯）

    private Storyboard? _optiScalerMarquee;

    /// <summary>HoYoShade / OpenHoYoShade 和 OptiScaler 同时勾上 → 显示滚动提示（用户要求）</summary>
    private void UpdateOptiScalerConflict()
    {
        if (Border_OptiScalerConflict is null)
        {
            return;
        }

        // OptiScaler：勾了「启用OptiScaler」+ 总开关开着 + 本游戏选过构建
        bool optiScalerOn = UseOptiScaler
                            && CurrentGameId is { } optiGameId
                            && AppConfig.GetSelectedOptiScalerDll(optiGameId) is not null;
        bool conflict = optiScalerOn && (UseHoYoShade || UseOpenHoYoShade);
        Border_OptiScalerConflict.Visibility = conflict ? Visibility.Visible : Visibility.Collapsed;

        if (!conflict)
        {
            StopOptiScalerMarquee();
            return;
        }

        // 刚变可见时还没量过尺寸，等一帧再起动画
        DispatcherQueue.TryEnqueue(StartOptiScalerMarquee);
    }

    private void StartOptiScalerMarquee()
    {
        if (Border_OptiScalerConflict is null || Border_OptiScalerConflict.Visibility != Visibility.Visible)
        {
            return;
        }

        StopOptiScalerMarquee();

        double viewport = Grid_OptiScalerConflict.ActualWidth;
        double text = TextBlock_OptiScalerConflict.ActualWidth;

        if (viewport <= 0 || text <= 0)
        {
            return;   // 还没量到，SizeChanged 会再来一次
        }

        // 把画布裁在提示框里，否则跑马灯会溢出去
        Grid_OptiScalerConflict.Clip = new RectangleGeometry
        {
            Rect = new Rect(0, 0, viewport, Grid_OptiScalerConflict.ActualHeight),
        };

        if (text <= viewport)
        {
            return;   // 放得下就不滚
        }

        var animation = new DoubleAnimation
        {
            From = viewport,
            To = -text,
            Duration = new Duration(TimeSpan.FromSeconds(Math.Max(6d, text / 40d))),
            RepeatBehavior = RepeatBehavior.Forever,
        };

        Storyboard.SetTarget(animation, OptiScalerMarqueeTransform);
        Storyboard.SetTargetProperty(animation, "X");

        _optiScalerMarquee = new Storyboard();
        _optiScalerMarquee.Children.Add(animation);
        _optiScalerMarquee.Begin();
    }

    private void StopOptiScalerMarquee()
    {
        try
        {
            _optiScalerMarquee?.Stop();
        }
        catch
        {
            // ignore
        }

        _optiScalerMarquee = null;

        if (OptiScalerMarqueeTransform is not null)
        {
            OptiScalerMarqueeTransform.X = 0;
        }
    }

    private void OptiScalerMarquee_SizeChanged(object sender, SizeChangedEventArgs e) => StartOptiScalerMarquee();

    #endregion

}
