using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>清理结果</summary>
public sealed record AddonReferenceCleanResult(int GamesScanned, List<string> ChangedGames, List<string> RemovedEntries)
{
    public bool Changed => ChangedGames.Count > 0;
}

/// <summary>
/// 清理各游戏 <c>ReShade.ini</c> 里指向「已经不存在的插件」的条目。

/// <para>
/// 用户要求（原话）：
/// <list type="number">
/// <item>「游戏关闭插件后，从 DllMain 加载（LoadFromDllMain）也应该暂时去掉」—— 那条在
/// <c>GamePluginService.SetAddonEnabled</c> 里做了；</item>
/// <item>「插件被全局禁用也是要变灰且暂时去掉」—— <see cref="RemoveFileNames"/> 干这个；</item>
/// <item>「插件被删除要永久去掉 LoadFromDllMain 和 DisabledAddons（检查是否有不存在的插件在里面）」——
/// 删除时调 <see cref="RemoveFileNames"/>，平时可以用 <see cref="RemoveStale"/> 扫一遍。</item>
/// </list>
/// </para>
/// </summary>
public static class AddonReferenceCleaner
{
    /// <summary>把这些文件名从**所有游戏**的 DisabledAddons / LoadFromDllMain 里去掉（比较时忽略 .addon64x 尾巴）</summary>
    public static AddonReferenceCleanResult RemoveFileNames(
        IEnumerable<GameEntry> games,
        IEnumerable<string> fileNames)
    {
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in fileNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                targets.Add(name);
                targets.Add(Normalize(name));
            }
        }

        return Clean(games, name => targets.Contains(Normalize(name)));
    }

    /// <summary>清掉「插件目录里已经没有这个文件」的条目（用户要求：检查是否有不存在的插件在里面）</summary>
    public static AddonReferenceCleanResult RemoveStale(IEnumerable<GameEntry> games, string? addonsDirectory)
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!string.IsNullOrWhiteSpace(addonsDirectory) && Directory.Exists(addonsDirectory))
            {
                foreach (AddonFileInfo file in AddonFileInfo.ScanDirectory(addonsDirectory))
                {
                    existing.Add(Normalize(file.FileName));
                }
            }
        }
        catch
        {
            // 目录读不了就别乱删条目 —— 返回「什么都没做」
            return new AddonReferenceCleanResult(0, [], []);
        }

        return Clean(games, name => !existing.Contains(Normalize(name)));
    }

    /// <param name="shouldRemove">这个文件名要不要清掉</param>
    private static AddonReferenceCleanResult Clean(IEnumerable<GameEntry> games, Func<string, bool> shouldRemove)
    {
        var changedGames = new List<string>();
        var removed = new List<string>();
        int scanned = 0;

        foreach (GameEntry game in games)
        {
            if (game.ReShadeIniPath is not { } iniPath || !File.Exists(iniPath))
            {
                continue;
            }

            scanned++;

            try
            {
                ReShadeProfile profile = ReShadeProfile.Load(iniPath);
                bool touched = false;

                List<DisabledAddonEntry> disabled = profile.GetDisabledAddons();
                int before = disabled.Count;
                disabled.RemoveAll(e => shouldRemove(e.FileName));
                if (disabled.Count != before)
                {
                    profile.SetDisabledAddons(disabled);
                    touched = true;
                    removed.AddRange(Enumerable.Range(0, before - disabled.Count).Select(_ => game.DisplayName + ":DisabledAddons"));
                }

                List<string>? slots = profile.GetLoadFromDllMain();
                if (slots is not null)
                {
                    int beforeSlots = slots.Count(s => !string.IsNullOrWhiteSpace(s));
                    List<string> kept = [.. slots.Where(s => string.IsNullOrWhiteSpace(s) || !shouldRemove(s))];
                    int afterSlots = kept.Count(s => !string.IsNullOrWhiteSpace(s));
                    if (afterSlots != beforeSlots)
                    {
                        profile.SaveLoadFromDllMainSlots(kept);
                        touched = true;
                        removed.AddRange(Enumerable.Range(0, beforeSlots - afterSlots).Select(_ => game.DisplayName + ":LoadFromDllMain"));
                    }
                }

                if (touched)
                {
                    profile.Save();
                    changedGames.Add(game.DisplayName);
                }
            }
            catch
            {
                // 单个游戏读坏了不影响别的
            }
        }

        return new AddonReferenceCleanResult(scanned, changedGames, removed);
    }

    /// <summary>比较用：<c>a.addon64x</c> 和 <c>a.addon64</c> 算同一个插件</summary>
    private static string Normalize(string fileName)
    {
        string name = fileName.Trim();
        return AddonFileInfo.Parse(name)?.IsRenamedDisabled == true && name.EndsWith('x')
            ? name[..^1]
            : name;
    }
}
