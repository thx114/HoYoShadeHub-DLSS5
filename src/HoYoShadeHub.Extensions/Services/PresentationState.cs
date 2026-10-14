namespace HoYoShadeHub.Extensions.Services;

/// <summary>Only the active transition may swap presentation slots.</summary>
public sealed class PresentationState<T> where T : class
{
    public sealed record Transition(T Outgoing, T Incoming);
    private Transition? _pending;
    public T Current { get; private set; }
    public T Spare { get; private set; }

    public PresentationState(T current, T spare)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(spare);
        if (ReferenceEquals(current, spare)) throw new ArgumentException("Presentation slots must be distinct.");
        Current = current;
        Spare = spare;
    }
    public Transition Begin()
    {
        if (_pending is not null) throw new InvalidOperationException("Commit the pending transition before starting another.");
        return _pending = new(Current, Spare);
    }
    public bool Complete(Transition transition)
    {
        if (!ReferenceEquals(_pending, transition)) return false;
        (Current, Spare) = (transition.Incoming, transition.Outgoing);
        _pending = null;
        return true;
    }
    public Transition? CommitPending()
    {
        var transition = _pending;
        if (transition is not null) Complete(transition);
        return transition;
    }
}
