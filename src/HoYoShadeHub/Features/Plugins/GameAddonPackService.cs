using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 「每游戏插件包」的胶水：把 AppConfig 里存的「这个游戏给某扩展选的版本」翻译成
/// <see cref="GameAddonPack"/> 的一份包目录，并把游戏 ReShade.ini 的 <c>[ADDON] AddonPath</c>
/// 指过去（取消选版本时再指回共享目录）。
///
/// <para>
/// 会在这些时机调用：
/// <list type="bullet">
/// <item>用户在这个游戏的「插件」页改了某个扩展的版本；</item>
/// <item>共享 Addons 目录变了（装 / 删扩展、全局开关改名）；</item>
/// <item>启动 / 注入这个游戏之前（<c>GameLauncherPage</c>），保证包是最新的。</item>
/// </list>
/// </para>
/// </summary>
internal static class GameAddonPackService
{
    /// <summary>
    /// 同步某个游戏的插件包。<paramref name="entry"/> 给 null 时不会改 ini，只更新包目录。
    /// </summary>
    /// <returns>包目录（这个游戏没有选任何版本时返回 null）</returns>
    public static string? Sync(GameId? gameId, GameEntry? entry, ShadeHost? host)
    {
        if (gameId is null || host is null)
        {
            return null;
        }

        string cacheRoot = AppConfig.CacheRoot;
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            return null;
        }

        string gameKey = gameId.GameBiz.Value;
        var store = new AddonVersionStore(cacheRoot);
        List<string> extensionIds = ReadLedgerIds(host);

        // 用户在 Addons 目录里**自己放**的插件（账本没有记录，靠 addonPatterns 认领的那种）
        // 也可能被选过版本。只按账本查会漏掉它们 → 选择查不到 → 这里判定成「没选任何版本」
        // → 把 AddonPath 撤回共享目录。用户看到的就是「切换插件版本没反应」，
        // 而且目录会在共享 / 专属包之间来回翻（用户报的 0xc0000005 那次日志里就是）。
        foreach (string id in AppConfig.GetPluginVersionSelectionIds(gameId))
        {
            if (!extensionIds.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                extensionIds.Add(id);
            }
        }

        Dictionary<string, string> selections = AppConfig.GetPluginVersionSelections(gameId, extensionIds);

        string addonsDirectory = GameAddonPack.AddonDirectory(cacheRoot, gameKey);
        string? iniPath = entry?.ReShadeIniPath is { } ini && File.Exists(ini) ? ini : null;

        if (selections.Count == 0)
        {
            // 没有选任何版本 = 回到今天的默认行为：AddonPath 指共享目录，包目录收掉。
            RevertIfPack(iniPath, host);
            if (Directory.Exists(addonsDirectory))
            {
                GameAddonPack.Remove(cacheRoot, gameKey);
            }

            return null;
        }

        List<AddonPackEntry> entries = GameAddonPack.Plan(
            host.AddonsPath,
            selections,
            (extensionId, tag) => store.AddonFiles(extensionId, tag));

        if (entries.Count == 0)
        {
            RevertIfPack(iniPath, host);
            return null;
        }

        GameAddonPack.Sync(addonsDirectory, entries, selections.Select(kv => (kv.Key, kv.Value)));

        if (iniPath is not null)
        {
            try
            {
                ReShadeProfile profile = ReShadeProfile.Load(iniPath);
                string full = Path.GetFullPath(addonsDirectory);
                if (!string.Equals(profile.AddonPath, full, StringComparison.OrdinalIgnoreCase))
                {
                    profile.SetAddonPath(full);
                    profile.Save();
                }
            }
            catch
            {
                // 写不动 ini 就只留包目录，下次启动/注入还会再试
            }
        }

        return addonsDirectory;
    }

    /// <summary>
    /// 共享 Addons 目录变了之后，把所有「有每游戏插件包」的游戏重拼一遍。
    /// 找不到宿主 / 没有任何游戏选过版本时什么都不做。
    /// </summary>
    public static int SyncAll()
    {
        ShadeHost? host = PluginHostLocator.Resolve(out _);
        if (host is null)
        {
            return 0;
        }

        IReadOnlyList<GameId> games = AppConfig.GetAddonPackGames();
        if (games.Count == 0)
        {
            return 0;
        }

        GameDiscoveryService service = GameCatalog.CreateService();
        int synced = 0;

        foreach (GameId gameId in games)
        {
            try
            {
                GameEntry? entry = GameCatalog.GetOrCreate(service, gameId);
                if (Sync(gameId, entry, host) is not null)
                {
                    synced++;
                }
            }
            catch
            {
                // 某个游戏出问题不该影响别的
            }
        }

        return synced;
    }

    /// <summary>把这个游戏之前指到插件包的 AddonPath 指回共享目录（只在确实指着包时才动）</summary>
    private static void RevertIfPack(string? iniPath, ShadeHost host)
    {
        if (iniPath is null)
        {
            return;
        }

        try
        {
            ReShadeProfile profile = ReShadeProfile.Load(iniPath);
            if (GameAddonPack.IsPackDirectory(profile.AddonPath))
            {
                profile.SetAddonPath(host.AddonsPath);
                profile.Save();
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>宿主账本里所有已装扩展 id（版本选择按这些 id 查）</summary>
    private static List<string> ReadLedgerIds(ShadeHost host)
    {
        try
        {
            if (!File.Exists(host.LedgerPath))
            {
                return [];
            }

            InstalledExtensionLedger? ledger = JsonSerializer.Deserialize<InstalledExtensionLedger>(
                File.ReadAllText(host.LedgerPath), InstalledExtensionLedger.JsonOptions);

            return ledger?.Extensions.Select(e => e.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }
}
