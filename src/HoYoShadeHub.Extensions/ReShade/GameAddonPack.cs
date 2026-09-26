using HoYoShadeHub.Extensions.Services;
using System.Text.Json;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>每游戏插件包里的一行：共享 Addons 目录里的一个文件名 → 用哪个源文件</summary>
public sealed class AddonPackEntry
{
    public string FileName { get; init; } = string.Empty;

    /// <summary>要链接/复制过来的源文件（共享目录里的，或版本归档里的）</summary>
    public string SourcePath { get; init; } = string.Empty;

    /// <summary>非空 = 这一行来自**这个扩展的某个指定版本**（归档里那份）</summary>
    public string? ExtensionId { get; init; }
}

/// <summary>一次插件包同步的结果</summary>
public sealed class AddonPackSyncResult
{
    public string AddonDirectory { get; init; } = string.Empty;

    public int Linked { get; init; }

    public int Copied { get; init; }

    /// <summary>内容一样、没动的</summary>
    public int Kept { get; init; }

    /// <summary>清掉的旧行（不再需要了）</summary>
    public int Removed { get; init; }

    public int Total => Linked + Copied + Kept;
}

/// <summary>
/// 每游戏插件包：<c>&lt;CacheRoot&gt;\games\&lt;gameKey&gt;\Addons\</c>。
///
/// <para>
/// 共享的 <c>reshade-shaders\Addons</c> 只放一份当前版本；这个游戏选了别的版本时，
/// 把共享目录里**全部**插件链接进这个包目录，其中「选过版本的那个扩展」的文件换成
/// 版本归档里的那一份（文件名保持一致，所以游戏 ini 里已有的
/// <c>DisabledAddons</c> / <c>LoadFromDllMain</c> 条目照旧匹配）。
/// </para>
///
/// <para>
/// 包目录里有一份 <c>pack.json</c> 作为标记 —— <see cref="ShadePathAligner"/> 靠它认出
/// 「AddonPath 指的是每游戏包」，从而**不会**把它再改回 HoYoShade 根目录。
/// </para>
/// </summary>
public static class GameAddonPack
{
    /// <summary>包目录里的标记文件（也是「这是插件包」的判据）</summary>
    public const string MarkerFileName = "pack.json";

    public const string GamesFolderName = "games";

    public const string AddonsFolderName = "Addons";

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// 游戏 key（就是 <c>GameBiz.Value</c>）清洗成合法目录名。
    /// 空 / 非法字符都退到 <c>unknown</c>，保证拼出来的路径永远能用。
    /// </summary>
    public static string SanitizeGameKey(string? gameKey)
    {
        if (string.IsNullOrWhiteSpace(gameKey))
        {
            return "unknown";
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        string result = new([.. gameKey.Select(c => invalid.Contains(c) ? '_' : c)]);
        result = result.Trim().TrimEnd('.');
        return result.Length == 0 ? "unknown" : result;
    }

    public static string GamesRoot(string? cacheRoot)
        => string.IsNullOrWhiteSpace(cacheRoot) ? string.Empty : Path.Combine(cacheRoot, GamesFolderName);

    /// <summary>&lt;CacheRoot&gt;\games\&lt;gameKey&gt;</summary>
    public static string PackDirectory(string? cacheRoot, string gameKey)
    {
        string root = GamesRoot(cacheRoot);
        return root.Length == 0 ? string.Empty : Path.Combine(root, SanitizeGameKey(gameKey));
    }

    /// <summary>&lt;CacheRoot&gt;\games\&lt;gameKey&gt;\Addons</summary>
    public static string AddonDirectory(string? cacheRoot, string gameKey)
    {
        string pack = PackDirectory(cacheRoot, gameKey);
        return pack.Length == 0 ? string.Empty : Path.Combine(pack, AddonsFolderName);
    }

    /// <summary>这个 AddonPath 是不是指向一个每游戏插件包（靠标记文件认）</summary>
    public static bool IsPackDirectory(string? addonPath)
    {
        if (string.IsNullOrWhiteSpace(addonPath))
        {
            return false;
        }

        try
        {
            string path = addonPath.Trim();
            return Path.IsPathFullyQualified(path) && File.Exists(Path.Combine(path, MarkerFileName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读包目录里 pack.json 记的「扩展 id → 这个游戏选的 tag」。
    /// 读不到 / 没有标记就返回空表（= 这个包没有选过版本）。
    /// </summary>
    public static Dictionary<string, string> ReadSelections(string? addonDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(addonDirectory))
        {
            return result;
        }

        string marker = Path.Combine(addonDirectory, MarkerFileName);
        if (!File.Exists(marker))
        {
            return result;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(marker));
            if (document.RootElement.TryGetProperty("selections", out JsonElement selections)
                && selections.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement entry in selections.EnumerateArray())
                {
                    string? id = entry.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() : null;
                    string? tag = entry.TryGetProperty("tag", out JsonElement tagElement) ? tagElement.GetString() : null;

                    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(tag))
                    {
                        result[id!] = tag!;
                    }
                }
            }
        }
        catch
        {
            // 标记读不坏就当没有
        }

        return result;
    }

    /// <summary>
    /// 规划插件包内容：共享目录里的所有文件 + 对「选过版本的扩展」用归档里的同名文件替换。
    /// </summary>
    /// <param name="sharedAddonsDirectory">共享 <c>reshade-shaders\Addons</c> 目录</param>
    /// <param name="selectedVersions">扩展 id → 这个游戏选的 tag</param>
    /// <param name="storeFiles">给定 (扩展 id, tag) 返回归档里落在 Addons 下的文件全路径</param>
    public static List<AddonPackEntry> Plan(
        string? sharedAddonsDirectory,
        IReadOnlyDictionary<string, string>? selectedVersions,
        Func<string, string, IReadOnlyList<string>>? storeFiles)
    {
        var map = new Dictionary<string, AddonPackEntry>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(sharedAddonsDirectory) && Directory.Exists(sharedAddonsDirectory))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(sharedAddonsDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(file);
                    if (name.Length > 0)
                    {
                        map[name] = new AddonPackEntry { FileName = name, SourcePath = file };
                    }
                }
            }
            catch
            {
                // 共享目录读不了：那就只有归档里的那些
            }
        }

        if (selectedVersions is not null && storeFiles is not null)
        {
            foreach ((string extensionId, string tag) in selectedVersions)
            {
                if (string.IsNullOrWhiteSpace(extensionId) || string.IsNullOrWhiteSpace(tag))
                {
                    continue;
                }

                foreach (string file in storeFiles(extensionId, tag) ?? [])
                {
                    string name = Path.GetFileName(file);
                    if (string.IsNullOrEmpty(name))
                    {
                        continue;
                    }

                    map[name] = new AddonPackEntry
                    {
                        FileName = name,
                        SourcePath = file,
                        ExtensionId = extensionId,
                    };
                }
            }
        }

        return [.. map.Values.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// 按计划把包目录同步出来：需要的内容硬链接/复制过来，多余的删掉，最后写标记。
    /// </summary>
    public static AddonPackSyncResult Sync(
        string addonDirectory,
        IReadOnlyList<AddonPackEntry> entries,
        IEnumerable<(string ExtensionId, string Tag)>? selections = null)
    {
        if (string.IsNullOrWhiteSpace(addonDirectory))
        {
            return new AddonPackSyncResult();
        }

        Directory.CreateDirectory(addonDirectory);

        int linked = 0;
        int copied = 0;
        int kept = 0;
        int removed = 0;

        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (AddonPackEntry entry in entries)
        {
            string target = Path.Combine(addonDirectory, entry.FileName);
            wanted.Add(entry.FileName);

            if (HysxFileLink.IsUpToDate(entry.SourcePath, target))
            {
                kept++;
                continue;
            }

            try
            {
                if (HysxFileLink.LinkOrCopy(entry.SourcePath, target))
                {
                    linked++;
                }
                else
                {
                    copied++;
                }
            }
            catch
            {
                // 某一行的链接失败不该让整个包同步失败
            }
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(addonDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, MarkerFileName, StringComparison.OrdinalIgnoreCase) || wanted.Contains(name))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    removed++;
                }
                catch
                {
                    // 删不掉就算了
                }
            }
        }
        catch
        {
            // 清理失败不影响结果
        }

        WriteMarker(addonDirectory, selections);
        return new AddonPackSyncResult
        {
            AddonDirectory = addonDirectory,
            Linked = linked,
            Copied = copied,
            Kept = kept,
            Removed = removed,
        };
    }

    /// <summary>只删包目录（换了「默认版本」之后不再需要时用）</summary>
    public static void Remove(string? cacheRoot, string gameKey)
    {
        string pack = PackDirectory(cacheRoot, gameKey);
        if (pack.Length > 0 && Directory.Exists(pack))
        {
            HysxUtil.TryDeleteDirectory(pack);
        }
    }

    private static void WriteMarker(string addonDirectory, IEnumerable<(string ExtensionId, string Tag)>? selections)
    {
        try
        {
            var payload = new
            {
                schema = 1,
                syncedAt = DateTimeOffset.Now,
                selections = (selections ?? []).Select(s => new { id = s.ExtensionId, tag = s.Tag }).ToArray(),
            };

            File.WriteAllText(Path.Combine(addonDirectory, MarkerFileName), JsonSerializer.Serialize(payload, _json));
        }
        catch
        {
            // 标记写不进去只是下次认不出来，不影响本次使用
        }
    }
}

/// <summary>
/// 一次「每游戏插件包体检」的结果：包目录和共享 Addons / 版本归档对不对得上。
/// </summary>
public sealed class AddonPackAudit
{
    /// <summary>这个目录是不是每游戏插件包（靠 pack.json 标记认）</summary>
    public bool IsPack { get; init; }

    public string AddonDirectory { get; init; } = string.Empty;

    public string SharedAddonsDirectory { get; init; } = string.Empty;

    /// <summary>包目录里的文件数（不含 pack.json）</summary>
    public int PackFileCount { get; init; }

    /// <summary>包标记里记的「扩展 id → tag」</summary>
    public IReadOnlyDictionary<string, string> Selections { get; init; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>选中的版本在归档里找不到（归档被删了）</summary>
    public IReadOnlyList<string> MissingArchiveVersions { get; init; } = [];

    /// <summary>共享目录 / 归档里该有的文件在包里没有</summary>
    public IReadOnlyList<string> MissingFiles { get; init; } = [];

    /// <summary>包里的文件和它的源（共享目录 / 归档）对不上（硬链接没了、内容旧了）</summary>
    public IReadOnlyList<string> StaleFiles { get; init; } = [];

    /// <summary>包里多出来的、不该在包里的文件</summary>
    public IReadOnlyList<string> ExtraFiles { get; init; } = [];

    /// <summary>包存在且没有任何不对</summary>
    public bool Ok => IsPack
                      && MissingArchiveVersions.Count == 0
                      && MissingFiles.Count == 0
                      && StaleFiles.Count == 0
                      && ExtraFiles.Count == 0;
}

/// <summary>
/// 「每游戏插件包」体检：把包目录和共享 Addons 目录 + 版本归档对一遍。
///
/// <para>
/// 只读、纯计算，DLSS5 兼容性检测里那条「每游戏插件包是否同步」就是它。
/// </para>
/// </summary>
public static class GameAddonPackAudit
{
    public static AddonPackAudit Inspect(string? cacheRoot, string gameKey, string? sharedAddonsDirectory)
    {
        string addonDirectory = GameAddonPack.AddonDirectory(cacheRoot, gameKey);

        if (!GameAddonPack.IsPackDirectory(addonDirectory))
        {
            return new AddonPackAudit
            {
                AddonDirectory = addonDirectory,
                SharedAddonsDirectory = sharedAddonsDirectory ?? string.Empty,
            };
        }

        Dictionary<string, string> selections = GameAddonPack.ReadSelections(addonDirectory);
        var store = new AddonVersionStore(cacheRoot);

        var missingArchive = new List<string>();
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(sharedAddonsDirectory) && Directory.Exists(sharedAddonsDirectory))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(sharedAddonsDirectory, "*", SearchOption.TopDirectoryOnly))
                {
                    string name = Path.GetFileName(file);
                    if (name.Length > 0)
                    {
                        expected[name] = file;
                    }
                }
            }
            catch
            {
                // 共享目录读不了就当没有
            }
        }

        // 选过版本的扩展：用归档里的那份替换同名文件
        foreach ((string extensionId, string tag) in selections)
        {
            List<string> files = store.AddonFiles(extensionId, tag);
            if (files.Count == 0)
            {
                missingArchive.Add($"{extensionId}@{tag}");
                continue;
            }

            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (name.Length > 0)
                {
                    expected[name] = file;
                }
            }
        }

        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stale = new List<string>();
        var extra = new List<string>();
        int packFileCount = 0;

        try
        {
            foreach (string file in Directory.EnumerateFiles(addonDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, GameAddonPack.MarkerFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                packFileCount++;
                actual.Add(name);

                if (!expected.TryGetValue(name, out string? source))
                {
                    extra.Add(name);
                    continue;
                }

                if (!HysxFileLink.IsUpToDate(source, file))
                {
                    stale.Add(name);
                }
            }
        }
        catch
        {
            // 包目录读不了就当空的
        }

        List<string> missing = [.. expected.Keys.Where(name => !actual.Contains(name))];

        return new AddonPackAudit
        {
            IsPack = true,
            AddonDirectory = addonDirectory,
            SharedAddonsDirectory = sharedAddonsDirectory ?? string.Empty,
            PackFileCount = packFileCount,
            Selections = selections,
            MissingArchiveVersions = missingArchive,
            MissingFiles = missing,
            StaleFiles = stale,
            ExtraFiles = extra,
        };
    }
}
