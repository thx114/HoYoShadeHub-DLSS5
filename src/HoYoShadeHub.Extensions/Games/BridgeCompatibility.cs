namespace HoYoShadeHub.Extensions.Games;

public static class BridgeCompatibility
{
    public static bool IsSupported(Version? version) =>
        version is { Major: 2, Minor: 3, Build: >= 1 };
}
