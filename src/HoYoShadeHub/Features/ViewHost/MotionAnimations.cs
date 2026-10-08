using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
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

    /// <summary>
    /// 换一屏内容（切视图 / 切标签页）的时长：175ms。
    /// 位移是一整个内容区宽度（整屏推走），所以时长要短才不发飘；175ms ≈ 半屏宽度的一点几毫秒一像素，
    /// 看着是「唰」一下换过去、最后一小段明显收住。
    /// </summary>
    public const int ViewSwitchDurationMs = 175;

    private static readonly Vector2 DecelerateControl1 = new(0f, 0f);
    private static readonly Vector2 DecelerateControl2 = new(0f, 1f);

    /// <summary>
    /// 换视图时内容左右平移的「兜底」位移（px）：正常取元素自己的实际宽度（整屏推走，滑到看不见为止），
    /// 只有元素还没排过版（宽度读成 0）时才用这个值顶上。
    /// </summary>
    private const float ViewSlideFallbackOffset = 800f;

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

    /// <summary>
    /// 换一屏内容时的淡入（只动透明度）。
    /// </summary>
    /// <remarks>
    /// 不用 <see cref="PlayEntrance"/>：那个会把缩放中心设在元素中心，而刚 <c>Visible</c> 的元素还没排过版
    /// （<c>ActualSize</c> 还是 0），中心会落到左上角，看着像从左上方「长出来」；换视图又是高频操作，
    /// 时长也取快一档的 <see cref="ViewSwitchDurationMs"/> 而不是整页入场那 500ms。
    /// </remarks>
    public static void PlayViewFadeIn(UIElement element)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        Compositor compositor = visual.Compositor;
        CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(DecelerateControl1, DecelerateControl2);

        // 先落到「起始态」再起动画：不然第一帧会先闪一下终点态
        visual.Opacity = 0f;

        var opacity = compositor.CreateScalarKeyFrameAnimation();
        opacity.InsertKeyFrame(0f, 0f);
        opacity.InsertKeyFrame(1f, 1f, ease);
        opacity.Duration = TimeSpan.FromMilliseconds(ViewSwitchDurationMs);
        visual.StartAnimation("Opacity", opacity);
    }

    /// <summary>
    /// 换一屏内容的「整屏左右平移」：新的一屏从行进方向的反侧**屏外**滑到正中，
    /// 旧的一屏朝行进方向滑到**屏外**（位移取各自的实际宽度，所以是滑到看不见为止，不是挪一小段），
    /// 两边同一条缓出曲线、同一个时长，看过去就是一整块内容被推走。
    /// </summary>
    /// <param name="incoming">要露出来的那一屏（调用方得先把它设成 <c>Visible</c>）。</param>
    /// <param name="outgoing">
    /// 正在退场的那一屏。它动画结束后会停在 <c>Visible</c> 且透明度 0 上，<b>由调用方负责收掉</b> ——
    /// 这里不塌它：塌了就播不出场，而且切视图的代次只有调用方清楚。
    /// </param>
    /// <param name="forward">
    /// true = 标签栏里「往右点」（1 → 2）：旧的一屏往左走、新的一屏从右边进来；false 反过来。
    /// </param>
    /// <remarks>
    /// 调用方得把两屏共用的父容器裁一下（<c>UIElement.Clip</c>）：WinUI 默认不按边界裁剪子元素，
    /// 整屏位移会把滑到一半的内容画到旁边的导航栏 / 窗口边上。
    /// </remarks>
    public static void PlayViewSlide(UIElement incoming, UIElement? outgoing, bool forward)
    {
        Visual incomingVisual = ElementCompositionPreview.GetElementVisual(incoming);
        Compositor compositor = incomingVisual.Compositor;

        // 缓出（decelerate），没有缓入：起步就是全速、结尾明显收住。
        // 用仓库里入场动画一直用的那条 cubic-bezier(0, 0, 0, 1)，换视图和整页入场手感一致。
        CubicBezierEasingFunction ease = compositor.CreateCubicBezierEasingFunction(DecelerateControl1, DecelerateControl2);
        TimeSpan duration = TimeSpan.FromMilliseconds(ViewSwitchDurationMs);

        // 位移 = 各自的实际宽度：新的一屏从「屏外」滑进来，旧的一屏滑到「屏外」为止
        float incomingTravel = TravelDistance(incoming);
        float outgoingTravel = outgoing is null ? incomingTravel : TravelDistance(outgoing);
        float enterFrom = forward ? incomingTravel : -incomingTravel;
        float exitTo = forward ? -outgoingTravel : outgoingTravel;

        // 三屏在同一个 Grid 格里叠着，谁后写在 XAML 里谁在上面。显式定序：
        // 入场的那屏压在最上面，退场那屏垫底。
        Canvas.SetZIndex(incoming, 1);

        // 想动 Translation 必须先打开（同 PlayEntrance）：WinUI 默认不把这个属性接到渲染上，
        // 不开的话写进去的值 / 起的动画只是躺在属性集里，画面一动不动 ——
        // 表现就是「只有淡入、没有平移」，Opacity 不受影响所以很容易看漏。
        ElementCompositionPreview.SetIsTranslationEnabled(incoming, true);

        // 不做淡入淡出，只动位置：整屏位移时两屏始终首尾相接（像推一格胶片），
        // 一起淡的话中段两屏同时半透明，看着是「两层幽灵」而不是「一整块内容被推走」。
        // 进出本来就藏在裁剪框外 —— 起点时新的一屏整块在框外，终点时旧的一屏整块在框外。
        incomingVisual.Opacity = 1f;

        // 先落到起始态再起动画：不然第一帧会先闪一下终点态
        incomingVisual.Properties.InsertVector3("Translation", new Vector3(enterFrom, 0f, 0f));

        var translation = compositor.CreateVector3KeyFrameAnimation();
        translation.InsertKeyFrame(0f, new Vector3(enterFrom, 0f, 0f));
        translation.InsertKeyFrame(1f, Vector3.Zero, ease);
        translation.Duration = duration;
        incomingVisual.StartAnimation("Translation", translation);

        if (outgoing is null || ReferenceEquals(outgoing, incoming))
        {
            return;
        }

        Visual outgoingVisual = ElementCompositionPreview.GetElementVisual(outgoing);
        Canvas.SetZIndex(outgoing, 0);
        ElementCompositionPreview.SetIsTranslationEnabled(outgoing, true);
        outgoingVisual.Opacity = 1f;
        outgoingVisual.Properties.InsertVector3("Translation", Vector3.Zero);

        var outgoingTranslation = compositor.CreateVector3KeyFrameAnimation();
        outgoingTranslation.InsertKeyFrame(0f, Vector3.Zero);
        outgoingTranslation.InsertKeyFrame(1f, new Vector3(exitTo, 0f, 0f), ease);
        outgoingTranslation.Duration = duration;
        outgoingVisual.StartAnimation("Translation", outgoingTranslation);
    }

    /// <summary>
    /// 一屏要滑出去（滑进来）的位移量：等于它自己的实际宽度，也就是「滑到看不见为止」。
    /// 还没排过版（宽度读成 0）时用 <see cref="ViewSlideFallbackOffset"/> 顶上 —— 宁可滑得远一点，也别几乎没动。
    /// </summary>
    private static float TravelDistance(UIElement element)
        => element is FrameworkElement { ActualWidth: > 0 } framework
            ? (float)framework.ActualWidth
            : ViewSlideFallbackOffset;

    /// <summary>
    /// 把 <see cref="PlayViewSlide"/> 在元素上留下的合成态（透明度 0 / 位移）清干净。
    /// 退场那一屏被收掉（<c>Collapsed</c>）时调用：下次它再当入场方时虽然会重设起始态，
    /// 但中间这段时间它是个「藏着却全透明」的元素，视觉树上看不出差别、排查时很误导。
    /// </summary>
    public static void ResetViewAnimation(UIElement element)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(element);
        visual.StopAnimation("Opacity");
        visual.StopAnimation("Translation");
        visual.Opacity = 1f;
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
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

    // ==================== 卡片展开 / 收起（Height + Opacity，驱动兄弟卡平滑挪动） ====================

    /// <summary>
    /// 卡片展开 / 收起动画：只动配置区的 <c>Height</c> + <c>Opacity</c>。
    /// 因为高度逐帧在变，面板每帧都重新排版 —— 下面那张（几张）卡的位置自然跟着走，这就是「兄弟卡让位」的动画。
    /// </summary>
    /// <para>
    /// <c>Height</c> 是**依赖动画**：必须显式打开 <c>EnableDependentAnimation</c>，
    /// 否则 Storyboard 会被静默跳过 —— 不报错、也不动，最容易白写。
    /// </para>
    /// <para>
    /// 时长曲线取 Fluent 基线（出处见 Motion.xaml 顶部注释）：展开 250ms + decelerate，
    /// 收起 167ms + accelerate。系统关掉「动画效果」时直接落终态。
    /// </para>
    /// <param name="expanding">展开还是收起。注意绑定的 Visibility 会**立即**翻转
    /// （OneWay 绑定以 VM 为准），所以动画期间用本地值把面板顶回 Visible，收尾时显式写成
    /// 绑定想要的终态（不能 ClearValue：x:Bind 不会重推，属性会回落成默认 Visible 导致关不上）。
    /// 不然收起动画播在 Collapsed 元素上，根本看不见。</param>
    /// <summary>每张面板的展开动画代次：新一轮开始就把旧的作废，别让它播完再落旧终态（连点会闪 / 关不上）。</summary>
    private sealed class AreaExpandState
    {
        public int Generation;
    }

    private static readonly ConditionalWeakTable<FrameworkElement, AreaExpandState> AreaExpandStates = new();

    public static void PlayAreaExpand(FrameworkElement panel, bool expanding)
    {
        AreaExpandState state = AreaExpandStates.GetOrCreateValue(panel);
        int generation = ++state.Generation;

        // 收起时绑定已经把它 Collapsed 了；展开时绑定刚把它 Visible。统一用本地值顶住：
        // 收起要再变回 Visible 才能播「收拢」的过程。
        panel.Visibility = Visibility.Visible;

        // 收起状态（或刚 Visible 还没量过）的元素 ActualWidth/Height 不可靠，宽度问父级
        double width = (panel.Parent as FrameworkElement)?.ActualWidth ?? panel.ActualWidth;
        if (width <= 0)
        {
            // 量不到就不播了，但别留下「本地 Visible 顶掉收起的终态」
            panel.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        // 先按内容量一次，拿到动画的目标高度（此时面板是 Visible 的，量得出来）
        panel.Height = double.NaN;
        panel.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        double target = panel.DesiredSize.Height;

        // XAML 默认**不裁剪**子元素：收起动画中途内容会溢到下面那张卡片上。
        // 挂一个 inset clip（四个 inset 都是 0 = 按自身边界裁），边界跟着动画里的 Height 走，不用单独动画。
        Visual visual = ElementCompositionPreview.GetElementVisual(panel);
        visual.Clip ??= visual.Compositor.CreateInsetClip();

        if (target <= 0 || !AnimationsEnabled())
        {
            // 内容空的 / 系统关了动画效果：没什么可播的，直接落终态
            Finish();
            return;
        }

        double from = expanding ? 0 : Math.Max(panel.ActualHeight, target);

        Duration duration = MotionDuration(expanding ? "MotionDurationNormal" : "MotionDurationFast", expanding ? 250 : 167);
        EasingFunctionBase ease = MotionEase(expanding ? "MotionEaseEnter" : "MotionEaseExit", expanding ? EasingMode.EaseOut : EasingMode.EaseIn);

        var storyboard = new Storyboard();

        var height = new DoubleAnimation
        {
            From = from,
            To = expanding ? target : 0,
            Duration = duration,
            EasingFunction = ease,
            EnableDependentAnimation = true,
        };
        Storyboard.SetTarget(height, panel);
        Storyboard.SetTargetProperty(height, "Height");
        storyboard.Children.Add(height);

        var opacity = new DoubleAnimation
        {
            From = expanding ? 0 : 1,
            To = expanding ? 1 : 0,
            Duration = duration,
            EasingFunction = ease,
        };
        Storyboard.SetTarget(opacity, panel);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        storyboard.Children.Add(opacity);

        storyboard.Completed += (_, _) => Finish();
        storyboard.Begin();

        void Finish()
        {
            if (state.Generation != generation)
            {
                // 已经被新一轮展开 / 收起取代：旧动画的收尾别再落地（会把新状态盖掉）
                return;
            }

            panel.Height = double.NaN;
            panel.Opacity = 1;
            // 显式写终态，不用 ClearValue —— x:Bind OneWay 在 ClearValue 后**不会**重推，
            // 属性会回落到默认值 Visible，收起就失效（卡片关不上的 bug）。
            // 下次 IsExpanded 变化时绑定会 SetValue 覆盖这个本地值，互不冲突。
            panel.Visibility = expanding ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>
    /// 从列表容器里找到 DataTemplate 里那块配置区（<paramref name="areaName"/>），给它播展开 / 收起。
    /// 找不到容器或控件就静默返回（还没排版的快速点击属于正常情况）。
    /// </summary>
    public static void PlayItemAreaExpand(ItemsControl host, object item, string areaName, bool expanding)
    {
        if (host.ContainerFromItem(item) is not DependencyObject container)
        {
            return;
        }

        if (FindDescendantByName(container, areaName) is not FrameworkElement panel)
        {
            return;
        }

        PlayAreaExpand(panel, expanding);
    }

    /// <summary>在模板实例的可视化树里按名字找控件（DataTemplate 里的 x:Name 找得到）</summary>
    public static FrameworkElement? FindDescendantByName(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement element && element.Name == name)
            {
                return element;
            }

            if (FindDescendantByName(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>系统「动画效果」开关（无障碍 / 省电设置）</summary>
    private static bool AnimationsEnabled()
    {
        try
        {
            return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        }
        catch
        {
            return true;
        }
    }

    private static Duration MotionDuration(string key, double fallbackMs) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is Duration duration
            ? duration
            : new Duration(TimeSpan.FromMilliseconds(fallbackMs));

    private static EasingFunctionBase MotionEase(string key, EasingMode mode) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is EasingFunctionBase ease
            ? ease
            : new PowerEase { Power = 3, EasingMode = mode };

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
