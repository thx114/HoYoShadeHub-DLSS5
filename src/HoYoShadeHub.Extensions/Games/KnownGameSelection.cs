using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>Resolve a selected installed MiHoYo executable to its normal launcher scheme.</summary>
public static class KnownGameSelection
{
    public static GameBiz? Resolve(string exePath, IEnumerable<KnownGameCandidate> installed)
    {
        string full = Path.GetFullPath(exePath);
        foreach (var candidate in installed)
        {
            string? existing = GameDiscoveryService.PickMainExe(candidate.InstallPath, candidate.ExeName);
            if (existing is not null && string.Equals(Path.GetFullPath(existing), full, StringComparison.OrdinalIgnoreCase))
                return candidate.Biz;
        }
        KnownProcess? known = KnownProcessNames.Find(full);
        if (known?.Biz is not { } fallback) return null;
        string config = Path.Combine(Path.GetDirectoryName(full)!, "config.ini");
        if (File.Exists(config))
        {
            string? channel = null;
            string? region = null;
            foreach (string line in File.ReadLines(config))
            {
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line[..eq].Trim();
                string value = line[(eq + 1)..].Trim();
                if (key.Equals("game_biz", StringComparison.OrdinalIgnoreCase)
                    && new GameBiz(value).Game == new GameBiz(fallback).Game && GameId.FromGameBiz(value) is not null)
                    return new GameBiz(value);
                if (key.Equals("channel", StringComparison.OrdinalIgnoreCase)) channel = value;
                if (key.Equals("cps", StringComparison.OrdinalIgnoreCase)) region = value;
            }
            string family = new GameBiz(fallback).Game;
            if (channel == "14" && family is GameBiz.hk4e or GameBiz.hkrpg or GameBiz.nap)
                return new GameBiz(family + "_bilibili");
            if (region?.Equals("mihoyo", StringComparison.OrdinalIgnoreCase) == true && channel == "1")
                return new GameBiz(family + "_cn");
        }
        return new GameBiz(fallback);
    }
}
