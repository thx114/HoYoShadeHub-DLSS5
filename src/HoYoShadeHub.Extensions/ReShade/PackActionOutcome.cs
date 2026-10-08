namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>Only a successful once action may suppress future setup retries.</summary>
public static class PackActionOutcome
{
    public static bool ShouldMarkOnce(string? outcome) =>
        !string.IsNullOrWhiteSpace(outcome)
        && !outcome.TrimStart().StartsWith("✗", StringComparison.Ordinal);
}
