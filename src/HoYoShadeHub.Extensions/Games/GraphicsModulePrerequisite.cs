namespace HoYoShadeHub.Extensions.Games;

/// <summary>Distinguish a model importer DLL from Windows' identically named graphics runtime.</summary>
public static class GraphicsModulePrerequisite
{
    public static bool Matches(string requested, string moduleName, string modulePath)
    {
        if (string.IsNullOrWhiteSpace(requested)) return false;
        if (!Path.IsPathRooted(requested))
            return string.Equals(Path.GetFileName(requested), moduleName, StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(modulePath)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(requested), Path.GetFullPath(modulePath),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
