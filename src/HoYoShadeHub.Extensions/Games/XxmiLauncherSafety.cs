namespace HoYoShadeHub.Extensions.Games;

public sealed record XxmiOwnedLauncher(int ProcessId, DateTime StartTimeUtc, string ExecutablePath);

public static class XxmiLauncherSafety
{
    public static bool Matches(XxmiOwnedLauncher owned, int pid, DateTime startTimeUtc, string executablePath) =>
        owned.ProcessId == pid && owned.StartTimeUtc == startTimeUtc
        && string.Equals(Path.GetFullPath(owned.ExecutablePath), Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);

    // XXMI InjectorLib: timeout is seconds; 0 = success, 1 = elapsed without injection.
    // Each awaited native call finishes before cancellation can leave this method.
    public static async Task<int> PollAsync(Func<int, int> wait, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int result = await Task.Run(() => wait(1)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (result != 1) return result;
        }
        return 1;
    }
}
