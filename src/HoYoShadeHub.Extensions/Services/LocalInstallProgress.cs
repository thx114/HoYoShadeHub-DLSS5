namespace HoYoShadeHub.Extensions.Services;

public sealed record LocalInstallProgress(double? Percent, string Message);

/// <summary>Inline callback adapter; UI dispatch is owned by the caller, not the worker.</summary>
public sealed class LocalInstallProgressReporter(Action<LocalInstallProgress> report) : IProgress<LocalInstallProgress>
{
    public void Report(LocalInstallProgress value) => report(value);
}
