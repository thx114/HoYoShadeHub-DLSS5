namespace HoYoShadeHub.Extensions.Games;

public static class GenshinLaunchRouting
{
    /// <summary>XXMI manual launch uses the ordinary Hub injection/start sequence.</summary>
    public static bool UseEarlyGraphicsLaunch(bool injectMode, bool starward, bool xxmiEnabled) =>
        !injectMode && !starward && !xxmiEnabled;
}
