using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Games;

public static class GameLaunchIniPreparation
{
    // Only supply a missing target. Existing INI values remain for the package's own actions to replace.
    public static void EnsurePackActionTarget(GameEntry? entry, ShadeHost? host, IEnumerable<PackAutoAction> actions)
    {
        bool needsReShade = actions.SelectMany(action => action.Steps).Any(step =>
            step.Action.Trim().ToLowerInvariant() is "set_addons" or "set_addon" or "apply_preset"
            || (step.Action.Equals("set_ini_keys", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrWhiteSpace(step.GetString("file"))));
        if (!needsReShade) return;
        string? target = entry?.ReShadeIniPath;
        if (target is not null && File.Exists(target)) return;
        if (string.IsNullOrWhiteSpace(target) || host is null || !File.Exists(host.ReShadeIniPath))
            throw new IOException("Cannot prepare the package's ReShade.ini target: game path or host template is unavailable.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(target))!);
        try { File.Copy(host.ReShadeIniPath, target, overwrite: false); }
        catch (IOException) when (File.Exists(target)) { }
    }
}
