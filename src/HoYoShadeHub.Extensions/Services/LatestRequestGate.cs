using System.Threading;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>Invalidates pending results when a newer request or owner replaces them.</summary>
public sealed class LatestRequestGate
{
    private long _generation;

    public long Begin() => Interlocked.Increment(ref _generation);
    public void Invalidate() => Interlocked.Increment(ref _generation);
    public bool IsCurrent(long generation) => Volatile.Read(ref _generation) == generation;
}
