using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Dlls;
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
    public static string? Sync(GameId? gameId, GameEntry? entry, ShadeHost? host, bool preserveSettings = false)
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

        string packDirectory = GameAddonPack.PackDirectory(cacheRoot, gameKey);
        string addonsDirectory = GameAddonPack.AddonDirectory(cacheRoot, gameKey);

        // 导入覆盖包时游戏可能还没注册：安装器把 GamePack 暂存在 .pending-gamepacks\<hint>。
        // 游戏一出现（Sync 在启动/插件页时都会跑）就把它铺进包目录，随后的动作确认框才能弹出。
        try
        {
            string pendingRoot = Path.Combine(cacheRoot, "games", ".pending-gamepacks");
            if (Directory.Exists(pendingRoot))
            {
                foreach (string hintDir in Directory.EnumerateDirectories(pendingRoot))
                {
                    string hint = Path.GetFileName(hintDir);
                    if (!gameKey.StartsWith(hint, StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!PackPendingMigration.TryMigrate(hintDir, packDirectory, out string? error))
                        throw new IOException("Pending 覆盖包迁移未完成；源内容保留：" + error);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 不允许部分迁移的 auto.json 在后续步骤中获得授权。
            throw new IOException("Pending 覆盖包不可用；同步已阻断。", ex);
        }
        string? iniPath = entry?.ReShadeIniPath is { } ini && File.Exists(ini) ? ini : null;

        // 覆盖包里有用户内容（ini_config.json / auto.json / presets\ / *_files\）时，
        // 就算没选任何版本包目录也常驻 —— 这些功能都挂在「AddonPath 指向包」上，收掉就全失效了。
        string? nrVersion = AppConfig.GetGameNrVersion(gameId, "reshade");
        bool keepPack = GameAddonPackUserContent.HasAny(packDirectory) || nrVersion is not null;

        if (selections.Count == 0 && !keepPack)
        {
            // 后台发现的遗留包仍可能被游戏 INI 引用；不在只更新文件的入口清包。
            if (preserveSettings) return null;
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

        if (entries.Count == 0 && !keepPack)
        {
            RevertIfPack(iniPath, host);
            return null;
        }

        if (nrVersion is not null)
        {
            string nrSource = NrRuntimeLibrary.VersionFile(cacheRoot, nrVersion);
            string[] nrTargets = entries.Where(e => HysxFileLink.IsNeuralRuntime(e.FileName))
                .Select(e => e.FileName).Append(NrRuntimeLibrary.FileName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            entries.RemoveAll(e => HysxFileLink.IsNeuralRuntime(e.FileName));
            foreach (string target in nrTargets)
                entries.Add(new AddonPackEntry { FileName = target, SourcePath = nrSource });
            selections["__nr_runtime"] = nrVersion;
        }

        // 包指纹：源（共享 Addons / 版本归档）没变就不重铺 —— 覆盖包只生效一次，
        // 用户在包目录里改过的插件设置不会被下次启动刷回。装包/升级/降级/换版本才重铺一次。
        var selectionPairs = selections.Select(kv => (kv.Key, kv.Value));
        string identity = GameAddonPack.ComputeIdentity(gameKey, entries, selectionPairs);
        GameAddonPack.Sync(addonsDirectory, entries, selectionPairs, identity, preserveSettings);

        if (nrVersion is not null)
        {
            string source = NrRuntimeLibrary.VersionFile(cacheRoot, nrVersion);
            HysxFileLink.LinkNeuralRuntime(source, Path.Combine(addonsDirectory, NrRuntimeLibrary.FileName));
            // 已存在的插件子目录也绑定相同版本。
            foreach (string nrFile in Directory.EnumerateFiles(addonsDirectory, NrRuntimeLibrary.FileName, SearchOption.AllDirectories))
                HysxFileLink.LinkNeuralRuntime(source, nrFile);
        }

        // 包 presets\ 随同步进 DLSS5-Presets（启动路径 GameIniBootstrap 也会再做一次兜底）
        if (!preserveSettings)
            GameAddonPackUserContent.SyncPresets(packDirectory, addonsDirectory);

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

        GameDiscoveryService service = GameCatalog.CreateService();
        List<GameId> games = [.. AppConfig.GetAddonPackGames()];
        // 覆盖包可以没有版本选择，因此不一定登记在 addon_pack_games 中。
        // 只补入已发现、且当前实际使用本启动器受管包的游戏。
        foreach (GameEntry entry in GameCatalog.DiscoverAll(service))
        {
            try
            {
                GameId? gameId = entry.IsCustom
                    ? new GameId { Id = entry.CustomBizValue, GameBiz = entry.CustomBiz }
                    : entry.Biz is { } biz ? GameId.FromGameBiz(biz) : null;
                if (gameId is null || games.Any(g => g.GameBiz == gameId.GameBiz))
                    continue;

                string expected = GameAddonPack.AddonDirectory(AppConfig.CacheRoot, gameId.GameBiz.Value);
                if (entry.HasReShadeIni
                    && GameAddonPack.IsPackDirectory(expected)
                    && string.Equals(ReShadeProfile.Load(entry.ReShadeIniPath!).ResolveAddonDirectory(),
                        expected, StringComparison.OrdinalIgnoreCase))
                    games.Add(gameId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 一份游戏 INI 读不了，不阻断其他游戏的插件更新。
            }
        }

        int synced = 0;

        foreach (GameId gameId in games)
        {
            try
            {
                // 后台同步只更新包，不改游戏 INI；切换版本/启动入口才负责路径切换。
                if (Sync(gameId, null, host, preserveSettings: true) is not null)
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

    /// <summary>
    /// 扩展被卸载后调用：清掉所有游戏给它的版本选择、把每游戏插件包重拼一遍，
    /// 并把这个扩展的版本归档整个删掉。
    ///
    /// <para>
    /// 不归零会复活：选择还留着 → 重拼包时从版本归档里再链接回包里
    /// （归档 intentionally 不受全局卸载影响）→ 游戏的 AddonPath 还指着包 →
    /// 插件照常被加载（用户报过：卸载 dlss5-feeder 后滤镜还在且自动启用）。
    /// 归档只为「切版本」存在，扩展卸载后没有保留价值，一并删掉。
    /// </para>
    /// </summary>
    /// <returns>(清掉选择的游戏数, 删掉的归档版本数)</returns>
    public static (int GamesCleared, int VersionsDeleted) RemoveExtension(string extensionId)
    {
        if (string.IsNullOrWhiteSpace(extensionId))
        {
            return (0, 0);
        }

        ShadeHost? host = PluginHostLocator.Resolve(out _);
        if (host is null)
        {
            return (0, 0);
        }

        int gamesCleared = 0;
        GameDiscoveryService service = GameCatalog.CreateService();

        // 用户明确卸载：旧覆盖包尚无 managedFiles，也必须按该扩展的模式清理。
        // 不依赖版本选择登记；不改游戏 INI 或其他插件文件。
        string[]? patterns = RemoteCatalogService.LoadCachedPlugins()?.Extensions
            .FirstOrDefault(e => string.Equals(e.Id, extensionId, StringComparison.OrdinalIgnoreCase))?.AddonPatterns;
        GameAddonPack.RemoveMatchingAddons(AppConfig.CacheRoot, patterns);

        foreach (GameId gameId in AppConfig.GetAddonPackGames())
        {
            try
            {
                if (string.IsNullOrWhiteSpace(AppConfig.GetPluginVersion(gameId, extensionId)))
                {
                    continue;
                }

                AppConfig.SetPluginVersion(gameId, extensionId, null);
                gamesCleared++;

                // 重拼：这个扩展的文件不再进 plan → Sync 会把包里的链接删掉；
                // 一个选择都不剩时整个包收掉、AddonPath 指回共享目录
                GameEntry? entry = GameCatalog.GetOrCreate(service, gameId);
                Sync(gameId, entry, host);
            }
            catch
            {
                // 单个游戏失败不影响其它游戏
            }
        }

        int versionsDeleted = 0;
        var store = new AddonVersionStore(AppConfig.CacheRoot);
        foreach (StoredAddonVersion version in store.ListVersions(extensionId))
        {
            if (store.DeleteVersion(extensionId, version.Tag))
            {
                versionsDeleted++;
            }
        }

        return (gamesCleared, versionsDeleted);
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
