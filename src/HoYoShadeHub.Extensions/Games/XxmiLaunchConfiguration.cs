namespace HoYoShadeHub.Extensions.Games;

/// <summary>The new game_launch enum must not be copied into the obsolete start-method enum.</summary>
public static class XxmiLaunchConfiguration
{
    public static string PreserveLegacyStartMethod(string current)
    {
        // Repair the invalid DIRECT value written by our official-baseline trial.
        // Keep existing legal legacy values rather than inventing new enum names.
        return string.IsNullOrWhiteSpace(current) || current.Equals("DIRECT", StringComparison.OrdinalIgnoreCase)
            ? "OPTION_REMOVED" : current;
    }
}
