using HoYoShadeHub.Core;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>Hub 报上来的一个「已知游戏」（安装目录来自 GameLauncherService）</summary>
public sealed record KnownGameCandidate(GameBiz Biz, string DisplayName, string? InstallPath, string? ExeName = null);

/// <summary><see cref="GameDiscoveryService.AddCustom"/> 的结果</summary>
public sealed record AddCustomResult(GameEntry? Entry, bool Added, string? Error);

/// <summary>
/// 游戏发现。**不做全盘扫描**（用户答复见 docs/GAMES-AND-INJECT.md §5）：
/// <list type="number">
/// <item>Hub 已知游戏 → 用 <see cref="KnownGameCandidate.InstallPath"/> 直接判断；</item>
/// <item>自定义游戏 → 用户自己选 exe。</item>
/// </list>
/// 结果是并集，按 <see cref="GameEntry.ReShadeIniPath"/> 去重。
/// </summary>
public sealed class GameDiscoveryService
{
    /// <summary>猜主程序时要跳过的名字（启动器 / 卸载器 / 崩溃上报之类）</summary>
    private static readonly string[] _exeNoise =
    [
        "uninst", "unins", "setup", "install", "launcher", "crashreport", "crashpad",
        "vcredist", "dxsetup", "updater", "patch", "repair", "config", "tool",
    ];

    public GameDiscoveryService(GameEntryStore store)
    {
        Store = store;
    }

    public GameEntryStore Store { get; }

    #region 自动发现

    /// <summary>
    /// 已知游戏 → 条目。目录不存在就跳过；目录里没有 ReShade.ini 也留着
    /// （注入模式要靠它补 ini，插件页会显示「该游戏没有 ReShade.ini」）。
    /// </summary>
    public List<GameEntry> Discover(IEnumerable<KnownGameCandidate> known)
    {
        var entries = new List<GameEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (KnownGameCandidate candidate in known)
        {
            if (string.IsNullOrWhiteSpace(candidate.InstallPath) || !Directory.Exists(candidate.InstallPath))
            {
                continue;
            }

            string? exePath = PickMainExe(candidate.InstallPath, candidate.ExeName ?? KnownProcessNames.ForBiz(candidate.Biz));
            bool hasIni = File.Exists(Path.Combine(candidate.InstallPath, "ReShade.ini"));
            if (exePath is null && !hasIni)
            {
                // 既没有主程序也没有 ini —— 这条对三个用途都没用
                continue;
            }

            var entry = new GameEntry(GameEntry.MakeBizId(candidate.Biz), candidate.DisplayName)
            {
                ExePath = exePath,
                Biz = candidate.Biz,
                IsCustom = false,
            };

            // 存过的 exe 覆盖 + 注入模式开关
            Store.ApplyTo(entry);

            if (!seen.Add(DedupeKey(entry)))
            {
                continue;
            }

            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>把自定义游戏（存过的那几条）读出来，接在自动发现的结果后面</summary>
    public void AppendCustom(List<GameEntry> entries)
    {
        var seen = new HashSet<string>(entries.Select(DedupeKey), StringComparer.OrdinalIgnoreCase);

        foreach (GameEntryRecord record in Store.Games.Where(g => g.IsCustom).ToList())
        {
            var entry = new GameEntry(record.Id, string.IsNullOrWhiteSpace(record.DisplayName) ? "自定义游戏" : record.DisplayName!)
            {
                ExePath = record.ExePath,
                IsCustom = true,
            };

            Store.ApplyTo(entry);

            if (seen.Add(DedupeKey(entry)))
            {
                entries.Add(entry);
            }
        }
    }

    /// <summary>自动发现 + 自定义，一次拿全</summary>
    public List<GameEntry> DiscoverAll(IEnumerable<KnownGameCandidate> known)
    {
        List<GameEntry> entries = Discover(known);
        AppendCustom(entries);
        return entries;
    }

    #endregion

    #region 手动添加

    /// <summary>
    /// 「添加自定义游戏」：选一个 exe → 得到安装目录 → 顺带就能定位 ReShade.ini。
    /// 这正是它比 Hub 现有「自定义注入」（要手输进程名）强的地方。
    /// </summary>
    public AddCustomResult AddCustom(string exePath, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return new AddCustomResult(null, false, "没有选择文件。");
        }

        string full;
        try
        {
            full = Path.GetFullPath(exePath);
        }
        catch (Exception ex)
        {
            return new AddCustomResult(null, false, "这个路径用不了：" + ex.Message);
        }

        if (!File.Exists(full))
        {
            return new AddCustomResult(null, false, $"找不到这个文件：{full}");
        }

        if (!string.Equals(Path.GetExtension(full), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return new AddCustomResult(null, false, "请选择游戏的 .exe 主程序。");
        }

        string id = GameEntry.MakeExeId(full);
        if (Store.Find(id) is { } existingRecord)
        {
            var existing = new GameEntry(id, existingRecord.DisplayName ?? Path.GetFileNameWithoutExtension(full))
            {
                ExePath = existingRecord.ExePath ?? full,
                IsCustom = true,
            };
            Store.ApplyTo(existing);
            return new AddCustomResult(existing, false, "这个游戏已经在列表里了。");
        }

        var entry = new GameEntry(id, string.IsNullOrWhiteSpace(displayName)
            ? KnownProcessNames.GuessDisplayName(full) ?? Path.GetFileNameWithoutExtension(full)
            : displayName!)
        {
            ExePath = full,
            IsCustom = true,
        };

        Store.Capture(entry);

        // 存不下来就等于没加上（重启就没），当场说清楚比让用户自己发现好
        if (!Store.Save(StorePath, out string? saveError))
        {
            return new AddCustomResult(null, false,
                $"写不进游戏列表（{StorePath}）：{saveError}");
        }

        return new AddCustomResult(entry, true, null);
    }

    public bool RemoveCustom(string id)
    {
        GameEntryRecord? record = Store.Find(id);
        if (record is null || !record.IsCustom)
        {
            return false;
        }

        bool removed = Store.Remove(id);
        Store.Save(StorePath);
        return removed;
    }

    /// <summary>exe 变了（比如用户挪了游戏）时更新一条自定义记录</summary>
    public void UpdateExePath(GameEntry entry, string exePath)
    {
        entry.ExePath = exePath;
        Store.Capture(entry);
        Store.Save(StorePath);
    }

    #endregion

    #region 小工具

    /// <summary>落盘位置；没设置就只放在内存里（测试用）</summary>
    public string StorePath { get; set; } = string.Empty;

    /// <summary>
    /// 目录里的主程序。
    /// 优先级：Hub 给的 exe 名 &gt; 内置进程名表 &gt; 目录里唯一的 exe &gt; 排除噪声后的唯一 exe。
    /// 多个候选时返回 null，交给界面让用户挑（**不瞎猜**）。
    /// </summary>
    public static string? PickMainExe(string? directory, string? preferredExeName)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(preferredExeName))
        {
            string preferred = Path.Combine(directory, Path.GetFileName(preferredExeName));
            if (File.Exists(preferred))
            {
                return preferred;
            }
        }

        List<string> candidates = EnumerateExeCandidates(directory);

        // 内置表里认识的名字
        string? known = candidates.FirstOrDefault(c => KnownProcessNames.IsKnownProcess(c));
        if (known is not null)
        {
            return known;
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    /// <summary>目录里所有看起来是「游戏主程序」的 exe（已剔除启动器 / 安装器之类）</summary>
    public static List<string> EnumerateExeCandidates(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            var all = Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var filtered = all
                .Where(f => !_exeNoise.Any(noise => Path.GetFileNameWithoutExtension(f).Contains(noise, StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // 全是噪声名的时候别把结果清空
            return filtered.Count > 0 ? filtered : all;
        }
        catch
        {
            return [];
        }
    }

    public static GameEntry? FindByBiz(IEnumerable<GameEntry> entries, GameBiz biz) =>
        entries.FirstOrDefault(e => e.Biz is { } b && string.Equals(b.Value, biz.Value, StringComparison.OrdinalIgnoreCase));

    public static GameEntry? FindByExePath(IEnumerable<GameEntry> entries, string? exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return null;
        }

        string key = GameEntry.NormalizePath(exePath);
        return entries.FirstOrDefault(e => !string.IsNullOrWhiteSpace(e.ExePath) && GameEntry.NormalizePath(e.ExePath!) == key);
    }

    /// <summary>去重用的键：优先 ReShade.ini（同一个 ini 就是同一个游戏），退而用 exe</summary>
    public static string DedupeKey(GameEntry entry) =>
        entry.ReShadeIniPath is { } ini
            ? "ini:" + GameEntry.NormalizePath(ini)
            : entry.ExePath is { } exe
                ? "exe:" + GameEntry.NormalizePath(exe)
                : "id:" + entry.Id;

    #endregion
}
