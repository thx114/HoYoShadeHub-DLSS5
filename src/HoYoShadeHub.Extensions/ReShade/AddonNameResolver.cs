using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>
/// 解析 addon 的「内部注册名」（就是写进 <c>DisabledAddons=名字@文件名</c> 的那一段）。
///
/// <para>
/// 事实依据（crosire/reshade，source/addon_manager.cpp）：
/// 名字是 addon 自己加载时调 <c>ReShadeRegisterAddon(name, ...)</c> 注册进去的，
/// <b>既不在 PE 导出表，也不一定在版本资源里</b>。所以不真正 LoadLibrary 是拿不到权威值的。
/// </para>
///
/// <para>
/// 但同一段代码里还写着关键的一句 —— 判断某个 addon 是否被禁用时：
/// </para>
/// <code>
/// const size_t at_pos = addon_name.find('@');
/// if (at_pos == npos) return false;
/// info.name = addon_name.substr(0, at_pos);
/// info.file = addon_name.substr(at_pos + 1);
/// return file_name == info.file;      // 只比文件名
/// </code>
/// <para>
/// 也就是说：<b>@ 前面的名字纯粹是给界面显示的，写错不影响禁用是否生效</b>；
/// 但 <b>@ 必须存在</b>，否则这条永远不会命中。
/// </para>
///
/// <para>
/// 所以这里的策略是「尽力猜准，猜不准也不影响功能」：
/// </para>
/// </summary>
public static class AddonNameResolver
{
    /// <summary>
    /// 实测拿到的 slug → 内部注册名（docs/RESHADE-INI.md §6.3 那张表）。
    /// PE 版本资源读不出名字的那些（renodx-dlss 两兄弟）就靠这张表。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> KnownInternalNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["renodx-dlss5-super-anus"] = "RenoDX DLSS_A",
            ["renodx-dlss"] = "RenoDX DLSS",
            ["dlss5-bridge"] = "DLSS 5 Bridge",
        };

    /// <summary>查已知映射；没有返回 null</summary>
    public static string? GetKnownInternalName(string? slug) =>
        slug is not null && KnownInternalNames.TryGetValue(slug, out string? name) ? name : null;

    /// <summary>
    /// 按优先级拿名字：学到缓存 &gt; 版本资源 &gt; 已知映射 &gt; slug 兜底。
    /// </summary>
    /// <param name="addonFilePath">addon 文件全路径；不存在或读不了就跳过需要读文件的两步</param>
    /// <param name="fileName">addon 文件名（匹配键）</param>
    /// <param name="learnedName">从任意一个游戏的 DisabledAddons 里学到的名字，最可信</param>
    /// <param name="knownName">我们 catalog 里维护的映射</param>
    /// <param name="candidateNames">候选名（一般直接给 catalog 里的发布名），用来在二进制里做包含匹配</param>
    /// <param name="fallback">兜底显示名（一般是文件名里的 slug）</param>
    public static string Resolve(
        string? addonFilePath,
        string fileName,
        string? learnedName = null,
        string? knownName = null,
        IEnumerable<string>? candidateNames = null,
        string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(learnedName))
        {
            return learnedName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(knownName))
        {
            return knownName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(addonFilePath) && File.Exists(addonFilePath))
        {
            string? fromResource = FromVersionResource(addonFilePath);
            if (!string.IsNullOrWhiteSpace(fromResource))
            {
                return fromResource.Trim();
            }

            // 版本资源是空的时候（实测 renodx-dlss 就是这样），
            // 退一步在二进制里找候选名 —— 名字作为字符串字面量是编在 DLL 里的。
            if (candidateNames is not null)
            {
                string? matched = MatchAgainstStrings(addonFilePath, candidateNames);
                if (!string.IsNullOrWhiteSpace(matched))
                {
                    return matched;
                }
            }
        }

        return AddonFileInfo.Parse(fileName)?.Slug ?? Path.GetFileNameWithoutExtension(fileName);
    }

    /// <summary>
    /// 从 PE 版本资源取 <c>FileDescription</c>。
    /// 实测：super-anus 和 dlss5-bridge 有值且和 ReShade 里的名字一致，
    /// 但 <c>renodx-dlss</c> 两个文件是空的 —— 所以这只是「有时候能用」，不能当唯一手段。
    /// </summary>
    public static string? FromVersionResource(string addonFilePath)
    {
        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(addonFilePath);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
            {
                return info.FileDescription;
            }
        }
        catch
        {
            // 非 PE / 被占用 / 权限不足都当没有
        }

        return null;
    }

    /// <summary>
    /// 最后一道兜底：在二进制里找候选名。
    ///
    /// 名字是作为字符串字面量编进 DLL 的，所以把「目录里可能出现的名字」当候选拿来做包含匹配，
    /// 命中就说明这个 addon 确实注册了这个名字。找不到返回 null。
    /// </summary>
    public static string? MatchAgainstStrings(string addonFilePath, IEnumerable<string> candidates)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(addonFilePath);

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate) || candidate.Length < 3)
                {
                    continue;
                }

                // 版本资源里的名字一般是 UTF-16；代码里的字符串字面量一般是 UTF-8/ASCII，两种都试
                if (ContainsBytes(bytes, Encoding.Unicode.GetBytes(candidate))
                    || ContainsBytes(bytes, Encoding.UTF8.GetBytes(candidate)))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length)
        {
            return false;
        }

        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool hit = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    hit = false;
                    break;
                }
            }

            if (hit)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// 名字缓存：<c>文件名 → 内部注册名</c>。
///
/// 存在的意义是「自愈」：用户在 ReShade 叠加层里手动开关一次，ReShade 就会把真名写进
/// 那个游戏的 DisabledAddons，我们下次读到就学到了，以后新游戏直接写对。
/// </summary>
public sealed class AddonNameCache
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    /// <summary>key = addon 文件名（含扩展名），value = 内部注册名</summary>
    [JsonPropertyName("names")]
    public Dictionary<string, string> Names { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions _options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static AddonNameCache Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<AddonNameCache>(File.ReadAllText(path), _options) ?? new AddonNameCache();
            }
        }
        catch
        {
            // 缓存坏了重建就好，别让用户看见
        }

        return new AddonNameCache();
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, _options), new UTF8Encoding(false));
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>从一份 ReShade.ini 里把名字学下来；返回学到了几条新的</summary>
    public int LearnFrom(ReShadeProfile profile)
    {
        int learned = 0;
        foreach (var entry in profile.GetDisabledAddons())
        {
            if (string.IsNullOrWhiteSpace(entry.DisplayName))
            {
                continue;
            }

            if (!Names.TryGetValue(entry.FileName, out string? existing)
                || !string.Equals(existing, entry.DisplayName, StringComparison.Ordinal))
            {
                Names[entry.FileName] = entry.DisplayName;
                learned++;
            }
        }

        return learned;
    }

    public string? Get(string addonFileName) =>
        Names.TryGetValue(addonFileName, out string? name) ? name : null;
}
