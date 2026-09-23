using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;

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
    /// 把 <paramref name="target"/> 背后的内容做高斯模糊。元素的 <c>Background</c> 负责染色。
    /// </summary>
    /// <param name="target">要变成模糊层的元素（通常是个 Border）</param>
    /// <param name="blurRadius">模糊半径，越大越糊（12~28 比较自然）</param>
    public static void Apply(FrameworkElement target, float blurRadius = 22f)
    {
        if (target is null)
        {
            return;
        }

        try
        {
            Visual host = ElementCompositionPreview.GetElementVisual(target);
            Compositor compositor = host.Compositor;

            var blur = new GaussianBlurEffect
            {
                Name = "Blur",
                BlurAmount = blurRadius,
                BorderMode = EffectBorderMode.Hard,
                Optimization = EffectOptimization.Balanced,
                Source = new CompositionEffectSourceParameter("backdrop"),
            };

            CompositionEffectFactory factory = compositor.CreateEffectFactory(blur);
            CompositionEffectBrush brush = factory.CreateBrush();
            brush.SetSourceParameter("backdrop", compositor.CreateBackdropBrush());

            SpriteVisual visual = compositor.CreateSpriteVisual();
            visual.Brush = brush;
            visual.Size = new Vector2(
                (float)Math.Max(target.ActualWidth, 1),
                (float)Math.Max(target.ActualHeight, 1));

            target.SizeChanged += (_, e) =>
            {
                if (e.NewSize.Width > 0 && e.NewSize.Height > 0)
                {
                    visual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
                }
            };

            ElementCompositionPreview.SetElementChildVisual(target, visual);
        }
        catch (Exception)
        {
            // 模糊失败不能连累卡片显示 —— XAML 里的半透明底色会兜底
        }
    }
}
