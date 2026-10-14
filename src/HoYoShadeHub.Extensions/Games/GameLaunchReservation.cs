namespace HoYoShadeHub.Extensions.Games;

/// <summary>Shared process-creation ownership across launcher entry points.</summary>
public sealed class GameLaunchReservation : IDisposable
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, GameLaunchReservation> Active = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _gameKey;
    private bool _disposed;

    private GameLaunchReservation(string gameKey) => _gameKey = gameKey;

    public static GameLaunchReservation Acquire(string gameKey, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameKey);
        cancellationToken.ThrowIfCancellationRequested();
        lock (Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Active.ContainsKey(gameKey))
                throw new InvalidOperationException($"Game launch already in progress: {gameKey}.");
            var reservation = new GameLaunchReservation(gameKey);
            Active.Add(gameKey, reservation);
            return reservation;
        }
    }

    public void Validate(string gameKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (Gate)
        {
            if (_disposed || !StringComparer.OrdinalIgnoreCase.Equals(_gameKey, gameKey)
                || !Active.TryGetValue(gameKey, out var owner) || !ReferenceEquals(owner, this))
                throw new InvalidOperationException("Game launch reservation is no longer current.");
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed) return;
            _disposed = true;
            if (Active.TryGetValue(_gameKey, out var owner) && ReferenceEquals(owner, this))
                Active.Remove(_gameKey);
        }
    }
}
