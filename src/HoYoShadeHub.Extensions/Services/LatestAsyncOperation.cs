namespace HoYoShadeHub.Extensions.Services;

/// <summary>The current lease alone can publish results or clear its busy state.</summary>
public sealed class LatestAsyncOperation
{
    private Lease? _current;

    public Lease Begin(CancellationToken parent = default)
    {
        Cancel();
        var lease = new Lease(this, parent);
        _current = lease;
        return lease;
    }

    public void Cancel()
    {
        var previous = _current;
        _current = null;
        previous?.Cancel();
    }

    public sealed class Lease : IDisposable
    {
        private readonly LatestAsyncOperation _owner;
        private readonly CancellationTokenSource _cancellation;
        private bool _disposed;
        internal Lease(LatestAsyncOperation owner, CancellationToken parent)
        {
            _owner = owner;
            _cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
            Token = _cancellation.Token;
        }
        public CancellationToken Token { get; }
        public bool IsCurrent => !_disposed && !Token.IsCancellationRequested && ReferenceEquals(_owner._current, this);
        internal void Cancel() { if (!_disposed) _cancellation.Cancel(); }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (ReferenceEquals(_owner._current, this)) _owner._current = null;
            _cancellation.Dispose();
        }
    }
}
