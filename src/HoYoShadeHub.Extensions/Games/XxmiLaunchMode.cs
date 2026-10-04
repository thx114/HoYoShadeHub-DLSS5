namespace HoYoShadeHub.Extensions.Games;

public enum XxmiLaunchMode { Official, Manual }
public static class XxmiLaunchModes
{
    public static XxmiLaunchMode Parse(string? value) =>
        value?.Equals("manual", StringComparison.OrdinalIgnoreCase) == true
            ? XxmiLaunchMode.Manual : XxmiLaunchMode.Official;
}
