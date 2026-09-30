using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace HoYoShadeHub.Features.Xxmi;

/// <summary>
/// 给卡片名字条做出**真高斯模糊**（磨砂玻璃）。
///
/// <para>
/// 原理：<see cref="CompositionBackdropBrush"/> 取「这个元素背后已经渲染好的内容」（也就是卡片上那张预览图），
/// 接进 Win2D 的 <see cref="GaussianBlurEffect"/> 模糊，再挂回元素上。
/// </para>
///
/// <para>
/// <b>染色不在这里做</b>：叠色交给 XAML 里那层 Border 自己的 <c>Background</c>
/// （半透明深色）。在 Composition 里拼 BlendEffect 会踩枚举名/兼容性的坑，
/// 而「模糊归模糊、染色归 XAML」职责更清楚，也好调。
/// </para>
///
/// <para>
/// 两个前提：
/// <list type="number">
/// <item>模糊层必须真的盖在图上、且在图片<b>之后</b>绘制，否则背后没东西可模糊；</item>
/// <item>Win2D（<c>Microsoft.Graphics.Win2D</c>）不能是 <c>ExcludeAssets="all"</c>，
/// 否则编译期找不到 <see cref="GaussianBlurEffect"/>。</item>
/// </list>
/// </para>
/// </summary>
internal static class BackdropBlur
{
    /// <summary>
    /// 模糊效果的 <see cref="CompositionEffectFactory"/> 按 <see cref="Compositor"/> + 半径缓存。
    /// GridView 滚动时卡片反复 Loaded，每次都重新编译一遍 GaussianBlur effect
    /// 既慢又会堆积大量 factory；同一 compositor + 半径只编译一次。
    /// </summary>
    private static readonly ConditionalWeakTable<Compositor, FactoryCache> Factories = new();

    private sealed class FactoryCache
    {
        public CompositionEffectFactory? Radius22 { get; set; }
    }

    private static CompositionEffectFactory GetFactory(Compositor compositor, float blurRadius)
    {
        FactoryCache cache = Factories.GetValue(compositor, _ => new FactoryCache());

        if (cache.Radius22 is { } cached)
        {
            return cached;
        }

        var blur = new GaussianBlurEffect
        {
            Name = "Blur",
            BlurAmount = blurRadius,
            BorderMode = EffectBorderMode.Hard,
            Optimization = EffectOptimization.Balanced,
            Source = new CompositionEffectSourceParameter("backdrop"),
        };

        return cache.Radius22 = compositor.CreateEffectFactory(blur);
    }

    /// <summary>
    /// 把 <paramref name="target"/> 背后的内容做高斯模糊。元素的 <c>Background</c> 负责染色。
    /// 返回是否成功挂上；<b>失败或之后 Unloaded 都要允许调用方再 Apply 一次</b>。
    /// </summary>
    /// <param name="target">要变成模糊层的元素（通常是个 Border）</param>
    /// <param name="blurRadius">模糊半径，越大越糊（12~28 比较自然）</param>
    public static bool Apply(FrameworkElement target, float blurRadius = 22f)
    {
        if (target is null)
        {
            return false;
        }

        try
        {
            Visual host = ElementCompositionPreview.GetElementVisual(target);
            Compositor compositor = host.Compositor;

            CompositionEffectBrush brush = GetFactory(compositor, blurRadius).CreateBrush();
            brush.SetSourceParameter("backdrop", compositor.CreateBackdropBrush());

            SpriteVisual visual = compositor.CreateSpriteVisual();
            visual.Brush = brush;
            visual.Size = new Vector2(
                (float)Math.Max(target.ActualWidth, 1),
                (float)Math.Max(target.ActualHeight, 1));

            void OnSizeChanged(object sender, SizeChangedEventArgs e)
            {
                if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
                {
                    visual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
                }
            }

            // 虚拟化容器回收时会 Unloaded：必须把挂上去的 SpriteVisual 摘下来。
            // 留着 backdrop brush 的 visual 跟着容器进回收池，是滚动中崩溃的常见来源
            // （backdrop 采样的是「背后的内容」，容器一摘，采样目标没了）。
            void OnUnloaded(object sender, RoutedEventArgs e)
            {
                target.SizeChanged -= OnSizeChanged;
                target.Unloaded -= OnUnloaded;

                try
                {
                    ElementCompositionPreview.SetElementChildVisual(target, null);
                    visual.Dispose();
                    brush.Dispose();
                }
                catch (Exception)
                {
                    // 摘不下来就算了 —— XAML 底色还在
                }
            }

            target.SizeChanged += OnSizeChanged;
            target.Unloaded += OnUnloaded;

            ElementCompositionPreview.SetElementChildVisual(target, visual);
            return true;
        }
        catch (Exception)
        {
            // 模糊失败不能连累卡片显示 —— XAML 里的半透明底色会兜底
            return false;
        }
    }
}
