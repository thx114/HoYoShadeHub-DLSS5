using System.Diagnostics;

namespace HoYoShadeHub.Extensions.Services;

public sealed record OwnedProcessShutdownResult(int Closed, int Forced, IReadOnlyList<string> Errors)
{
    public bool Success => Errors.Count == 0;
}

/// <summary>Close only the supplied process instances, never their child game processes.</summary>
public static class OwnedProcessShutdown
{
    public static async Task<OwnedProcessShutdownResult> CloseAsync(IReadOnlyList<Process> processes,
        TimeSpan grace, CancellationToken cancellationToken = default)
    {
        int closed = 0, forced = 0;
        var errors = new List<string>();
        var waiting = new List<Process>();
        foreach (Process process in processes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (process.HasExited) { closed++; continue; }
                if (process.CloseMainWindow()) waiting.Add(process);
                else
                {
                    process.Kill(entireProcessTree: false);
                    forced++;
                    waiting.Add(process);
                }
            }
            catch (Exception ex) { errors.Add($"pid {process.Id}: {ex.Message}"); }
        }
        DateTime deadline = DateTime.UtcNow + grace;
        while (DateTime.UtcNow < deadline && waiting.Any(p => IsRunning(p)))
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        foreach (Process process in waiting)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: false);
                    forced++;
                }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                closed++;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { errors.Add($"pid {process.Id}: 关闭后仍未退出"); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            { errors.Add($"pid {process.Id}: {ex.Message}"); }
        }
        return new(closed, forced, errors);
    }

    private static bool IsRunning(Process process)
    {
        try { return !process.HasExited; }
        catch { return false; }
    }
}
