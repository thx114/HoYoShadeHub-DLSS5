namespace HoYoShadeHub.Extensions.Services;

/// <summary>Pairs load/unload and invalidates work from an earlier attachment.</summary>
public sealed class UiLifetime
{
    private CancellationTokenSource? _cancellation;
    public bool IsActive => _cancellation is not null;
    public long Generation { get; private set; }
    public CancellationToken Token { get; private set; } = new(canceled: true);

    public bool Enter()
    {
        if (IsActive) return false;
        _cancellation = new();
        Token = _cancellation.Token;
        Generation++;
        return true;
    }

    public bool IsCurrent(long generation) => IsActive && generation == Generation;

    public bool Exit()
    {
        var cancellation = _cancellation;
        if (cancellation is null) return false;
        _cancellation = null;
        Generation++;
        try { cancellation.Cancel(); }
        finally { cancellation.Dispose(); }
        return true;
    }
}
