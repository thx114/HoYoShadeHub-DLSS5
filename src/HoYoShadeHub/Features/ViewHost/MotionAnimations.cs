using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace HoYoShadeHub.Features.ViewHost;

/// <summary>
/// 动效小工具（走 Composition，GPU 侧跑）。
///
/// <para>
/// 时长取 Fluent 2 的官方 token（<c>@fluentui/tokens</c> 的 <c>global/durations</c>）：
/// <c>durationUltraFast 50ms / durationFaster 100ms / durationFast 150ms / durationNormal 200ms /
/// durationGentle 250ms / durationSlow 300ms / durationSlower 400ms / durationUltraSlow 500ms</c>。
/// 曲线用 Fluent 的入场基线 <c>cubic-bezier(0, 0, 0, 1)</c>（同 Microsoft Learn「Timing and easing」里那条
/// decelerate）—— 这里能直接写真正的贝塞尔，不像 XAML 的 EasingFunction 只能用三次幂逼近。
/// </para>
/// <para>
/// 用 Composition 而不是 Storyboard 的原因：首屏会一次进来十几个图标，Composition 的
/// <c>DelayTime</c> 让「一个一个出来」不用起十几个 Storyboard，也不会卡 UI 线程。
/// </para>
/// </summary>
public static class MotionAnimations
{
    /// <summary>入场时长：500ms（Fluent token <c>durationUltraSlow</c>）。比控件动效慢一档，看着更「有感觉」。</summary>
    public const int EntranceDurationMs = 500;

    /// <summary>相邻两项的入场间隔。45ms 大约是一眼能数出「有顺序」的下限，再大就嫌拖。</summary>
    public const int StaggerStepMs = 45;

    /// <summary>入场时从下方多少个像素滑上来。</summary>
    private const float EntranceSlideY = 18f;

    /// <summary>入场时的起始缩放（0.96 → 1，轻微「弹出来」的感觉，不做夸张的回弹）。</summary>
    private const float EntranceFromScale = 0.96f;

    /// <summary>鼠标悬停放大倍数。</summary>
    private const float HoverScale = 1.04f;

    /// <summary>按下缩小的倍数。</summary>
    private const float PressedScale = 0.97f;

    /// <summary>悬停 / 按下的时长（Fluent token：150ms = durationFast，100ms = durationFaster）。</summary>
    private const int HoverDurationMs = 150;

    /// <summary>按下时长。</summary>
    private const int PressedDurationMs = 100;

    private static readonly Vector2 DecelerateControl1 = new(0f, 0f);
    private static readonly Vector2 DecelerateControl2 = new(0f, 1f);

    /// <summary>
    /// 让一组元素「一个接一个」淡入 + 由下往上滑 + 轻微放大。
    /// </summary>
    /// <param name="elements">已经实例化、且在可视化树上的元素（一般取 <c>ItemsControl.ItemsPanelRoot.Children</c>）。</param>
    /// <param name="staggerMs">间隔；传 0 就是一起出现。</param>
    /// <param name="reverse">从最后一项开始（列表自下而上冒出来）。</param>
    public static void PlayStaggeredEntrance(IEnumerable<UIElement>? elements, int staggerMs = StaggerStepMs, bool reverse = false)
    {
        if (elements is null)
        {
            return;
        }

        var list = new List<UIElement>();
        foreach (UIElement element in elements)
        {
            list.Add(element);
        }

        for (int i = 0; i < list.Count; i++)
        {
            int order = reverse ? list.Count - 1 - i : i;
            PlayEntrance(list[i], TimeSpan.FromMilliseconds(order * staggerMs));
        }
    }

    /// <summary>已经挂过悬停 / 按下缩放的元素（列表每次重播入场都会走到挂载点，得幂等）。</summary>
    private static readonly ConditionalWeakTable<FrameworkElement, object> PointerScaleAttached = new();

    /// <summary>
    /// 给一个 <see cref="ItemsControl"/> 里的项跑错峰入场（可选地顺手挂上悬停 / 按下缩放）。
    /// </summary>
    /// <param name="host">列表控件；面板还没实例化（还没排过版）时直接什么都不做。</param>
    /// <param name="reverse">从最后一项开始。</param>
    /// <param name="pointerScale">给每一项挂上悬停放大 / 按下缩小。</param>
    public static void PlayListEntrance(ItemsControl? host, bool reverse = false, bool pointerScale = false)
    {
        if (host?.ItemsPanelRoot is not Panel panel)
        {
            return;
        }

        PlayStaggeredEntrance(panel.Children, reverse: reverse);

        if (!pointerScale)
        {
            return;
        }

        foreach (UIElement child in panel.Children)
        {
            if (child is FrameworkElement element)
            {
                AttachPointerScale(element);
            }
        }
    }

    /// <summary>单个元素的入场（延迟由 <paramref name="delay"/> 决定）。</summary>
    public static void PlayEntrance(UIElement element, TimeSpan delay)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);

        // 想动 Translation 必须先打开，不然只是给 Visual 写了个没人理的属性
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);

        Compositor compositor = visual.Compositor;
        CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(DecelerateControl1, DecelerateControl2);
        TimeSpan duration = TimeSpan.FromMilliseconds(EntranceDurationMs);

        visual.CenterPoint = CenterOf(element);

        // 先落到「起始态」，等 DelayTime 到了再动 —— 不然延迟期间元素是「终点态」，会闪一下
        // Visual 上没有直接可写的 Translation 属性（XAML 会把 Offset 每帧写回去，所以也不能动 Offset），
        // 只能通过 SetIsTranslationEnabled + Properties.InsertVector3 先落起始值，再按名字起动画。
        visual.Opacity = 0f;
        visual.Properties.InsertVector3("Translation", new Vector3(0f, EntranceSlideY, 0f));
        visual.Scale = new Vector3(EntranceFromScale, EntranceFromScale, 1f);

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f, ease);
        opacity.Duration = duration;
        opacity.DelayTime = delay;
        visual.StartAnimation("Opacity", opacity);

        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0f, new Vector3(0f, EntranceSlideY, 0f));
        translation.InsertKeyFrame(1f, Vector3.Zero, ease);
        translation.Duration = duration;
        translation.DelayTime = delay;
        visual.StartAnimation("Translation", translation);

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(0f, new Vector3(EntranceFromScale, EntranceFromScale, 1f));
        scale.InsertKeyFrame(1f, Vector3.One, ease);
        scale.Duration = duration;
        scale.DelayTime = delay;
        visual.StartAnimation("Scale", scale);
    }

    /// <summary>给元素挂上「悬停放大、按下缩小」的手感（纯装饰，不改任何状态）。</summary>
    public static void AttachPointerScale(FrameworkElement element)
    {
        // 同一个元素只挂一次：列表每重播一次入场都会走到这里，不然处理器会越挂越多
        if (!PointerScaleAttached.TryAdd(element, element))
        {
            return;
        }

        AttachCenterPointTracking(element);

        element.PointerEntered += (_, _) => AnimateScale(element, HoverScale, HoverDurationMs);
        element.PointerExited += (_, _) => AnimateScale(element, 1f, HoverDurationMs);
        element.PointerCanceled += (_, _) => AnimateScale(element, 1f, HoverDurationMs);
        element.PointerCaptureLost += (_, _) => AnimateScale(element, 1f, HoverDurationMs);
        element.PointerPressed += (_, _) => AnimateScale(element, PressedScale, PressedDurationMs);
        element.PointerReleased += (_, _) => AnimateScale(element, HoverScale, PressedDurationMs);
    }

    /// <summary>把一个元素平滑缩放到指定倍数。</summary>
    public static void AnimateScale(UIElement element, float scale, int durationMs)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = CenterOf(element);

        Compositor compositor = visual.Compositor;
        CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(DecelerateControl1, DecelerateControl2);

        var animation = compositor.CreateVector3KeyFrameAnimation();
        animation.InsertKeyFrame(1f, new Vector3(scale, scale, 1f), ease);
        animation.Duration = TimeSpan.FromMilliseconds(durationMs);
        visual.StartAnimation("Scale", animation);
    }

    /// <summary>尺寸定下来（或变化）时把缩放中心挪到正中，免得从左上角放大。</summary>
    private static void AttachCenterPointTracking(FrameworkElement element)
    {
        element.SizeChanged += (_, _) => ElementCompositionPreview.GetElementVisual(element).CenterPoint = CenterOf(element);
    }

    private static Vector3 CenterOf(UIElement element)
    {
        Vector2 size = element.ActualSize;
        return new Vector3(size.X / 2f, size.Y / 2f, 0f);
    }
}
