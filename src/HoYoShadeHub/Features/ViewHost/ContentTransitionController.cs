using HoYoShadeHub.Extensions.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using System;
using System.Numerics;
using Windows.Foundation;

namespace HoYoShadeHub.Features.ViewHost;

internal sealed class ContentTransitionController : IDisposable
{
    private readonly PresentationState<ContentControl> _state;
    private readonly Action<bool> _setWizardBackground;
    private CompositionScopedBatch? _batch;
    private TypedEventHandler<object, CompositionBatchCompletedEventArgs>? _completed;
    private bool _wizardBackground;
    private bool _disposed;

    public ContentTransitionController(ContentControl current, ContentControl spare, Action<bool> setWizardBackground)
    {
        _state = new(current, spare);
        _setWizardBackground = setWizardBackground;
        ElementCompositionPreview.SetIsTranslationEnabled(current, true);
        ElementCompositionPreview.SetIsTranslationEnabled(spare, true);
    }

    internal static bool AnimationsEnabled()
    {
        try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; }
        catch { return true; }
    }

    public void Navigate(UIElement content, ViewTransitionType type, bool wizardBackground)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CommitActiveTransition();
        _wizardBackground = wizardBackground;
        if (type == ViewTransitionType.None || !AnimationsEnabled() || _state.Current.Content is null)
        {
            _state.Current.Content = content;
            Normalize(_state.Current, visible: true);
            Normalize(_state.Spare, visible: false);
            _setWizardBackground(wizardBackground);
            return;
        }

        if (wizardBackground) _setWizardBackground(true);
        var transition = _state.Begin();
        try { StartTransition(content, type, transition); }
        catch (Exception ex)
        {
            CommitActiveTransition();
            AppConfig.GetLogger<ContentTransitionController>().LogWarning(ex, "Content animation failed; committed requested view");
        }
    }

    private void StartTransition(UIElement content, ViewTransitionType type,
        PresentationState<ContentControl>.Transition transition)
    {
        transition.Incoming.Content = content;
        Normalize(transition.Incoming, visible: true);
        transition.Outgoing.IsHitTestVisible = false;
        var outgoing = ElementCompositionPreview.GetElementVisual(transition.Outgoing);
        var incoming = ElementCompositionPreview.GetElementVisual(transition.Incoming);
        var compositor = incoming.Compositor;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _batch = batch;
        try { Animate(batch, transition, outgoing, incoming, type); }
        finally { batch.End(); }
    }

    private void Animate(CompositionScopedBatch batch, PresentationState<ContentControl>.Transition transition,
        Visual outgoing, Visual incoming, ViewTransitionType type)
    {
        var compositor = incoming.Compositor;
        _completed = (_, _) =>
        {
            if (transition.Incoming.DispatcherQueue.HasThreadAccess) Finish(batch, transition);
            else transition.Incoming.DispatcherQueue.TryEnqueue(() => Finish(batch, transition));
        };
        batch.Completed += _completed;
        using var easing = compositor.CreateCubicBezierEasingFunction(new(0.1f, 0.9f), new(0.2f, 1f));
        incoming.Opacity = 0f;

        if (type == ViewTransitionType.SlideFromRight)
        {
            incoming.Properties.InsertVector3("Translation", new(60f, 0f, 0f));
            using var enter = compositor.CreateVector3KeyFrameAnimation();
            enter.Duration = TimeSpan.FromMilliseconds(350);
            enter.InsertKeyFrame(1f, Vector3.Zero, easing);
            incoming.StartAnimation("Translation", enter);
            using var exit = compositor.CreateVector3KeyFrameAnimation();
            exit.Duration = TimeSpan.FromMilliseconds(300);
            exit.InsertKeyFrame(1f, new(-50f, 0f, 0f), easing);
            outgoing.StartAnimation("Translation", exit);
        }
        else if (type == ViewTransitionType.DrillIn)
        {
            incoming.CenterPoint = new((float)Math.Max(transition.Incoming.ActualWidth, transition.Outgoing.ActualWidth) / 2f,
                (float)Math.Max(transition.Incoming.ActualHeight, transition.Outgoing.ActualHeight) / 2f, 0f);
            incoming.Scale = new(1.03f, 1.03f, 1f);
            using var enter = compositor.CreateVector3KeyFrameAnimation();
            enter.Duration = TimeSpan.FromMilliseconds(450);
            enter.InsertKeyFrame(1f, Vector3.One, easing);
            incoming.StartAnimation(nameof(Visual.Scale), enter);
        }
        using var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Duration = TimeSpan.FromMilliseconds(type == ViewTransitionType.DrillIn ? 400 : 300);
        fadeIn.InsertKeyFrame(1f, 1f, easing);
        incoming.StartAnimation(nameof(Visual.Opacity), fadeIn);
        using var fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Duration = TimeSpan.FromMilliseconds(250);
        fadeOut.InsertKeyFrame(1f, 0f, easing);
        outgoing.StartAnimation(nameof(Visual.Opacity), fadeOut);
    }

    private void Finish(CompositionScopedBatch batch, PresentationState<ContentControl>.Transition transition)
    {
        if (!ReferenceEquals(_batch, batch) || !_state.Complete(transition)) return;
        ReleaseBatch();
        Normalize(transition.Outgoing, visible: false);
        Normalize(transition.Incoming, visible: true);
        _setWizardBackground(_wizardBackground);
    }

    private void CommitActiveTransition()
    {
        var transition = _state.CommitPending();
        ReleaseBatch();
        if (transition is null) return;
        Normalize(transition.Outgoing, visible: false);
        Normalize(transition.Incoming, visible: true);
        _setWizardBackground(_wizardBackground);
    }

    private void ReleaseBatch()
    {
        var batch = _batch;
        _batch = null;
        if (batch is null) return;
        if (_completed is not null) batch.Completed -= _completed;
        _completed = null;
        batch.Dispose();
    }

    private static void Normalize(ContentControl presenter, bool visible)
    {
        var visual = ElementCompositionPreview.GetElementVisual(presenter);
        visual.StopAnimation("Translation");
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.StopAnimation(nameof(Visual.Scale));
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        visual.Opacity = 1f;
        visual.Scale = Vector3.One;
        visual.CenterPoint = Vector3.Zero;
        presenter.Opacity = 1;
        presenter.IsHitTestVisible = visible;
        presenter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (!visible) presenter.Content = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        CommitActiveTransition();
        _disposed = true;
    }
}
