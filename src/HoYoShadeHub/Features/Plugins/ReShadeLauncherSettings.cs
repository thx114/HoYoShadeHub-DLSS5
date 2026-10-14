using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.ReShade;
using System.IO;

namespace HoYoShadeHub.Features.Plugins;

internal static class ReShadeLauncherSettings
{
    public static void Apply(GameId gameId, string? iniPath)
    {
        if (iniPath is null || !File.Exists(iniPath)) return;
        IniDocument ini = IniDocument.Load(iniPath);
        bool changed = false;
        string? hotkey = AppConfig.GetReShadeOverride(gameId, "KeyOverlay");
        if (hotkey is not null && ini.GetValue("INPUT", "KeyOverlay") != hotkey)
        {
            ini.SetValue("INPUT", "KeyOverlay", hotkey);
            changed = true;
        }
        foreach (ReShadeDepthOption option in ReShadeDepthOptions.All)
        {
            string? value = AppConfig.GetReShadeOverride(gameId, option.Name);
            if (value is not null) changed |= ReShadeDepthOptions.Set(ini, option, value, "GENERAL");
        }
        if (changed) ini.Save(iniPath);
    }
}
