namespace HoYoShadeHub.Extensions.Conditions;

/// <summary>
/// 插件条件的运行期入口：远端 conditions.json 有值用远端，没有/读不到用内置默认
/// （= 2026-10-01 之前硬编码的行为）。Hub 侧 <c>RemoteCatalogService</c> 拉到文件后调
/// <see cref="LoadJson"/>；离线且没有种子文件时 <see cref="Current"/> 就是默认值。
///
/// <para>
/// 全部访问器都是「远端优先、缺字段回落默认」，所以旧 conditions.json（缺新字段）
/// 和新启动器（认识新字段）任意组合都能工作。
/// </para>
/// </summary>
public static class AddonConditions
{
    private static volatile AddonConditionsDocument? _remote;

    /// <summary>当前生效的条件文档（从没加载过 = null = 全默认）</summary>
    public static AddonConditionsDocument? Current => _remote;

    /// <summary>Hub 拉取后喂进来；传 null/坏 JSON = 清掉远端层，回落默认</summary>
    public static void LoadJson(string? json) => _remote = AddonConditionsDocument.Parse(json);

    /// <summary>测试用：直接塞解析好的文档</summary>
    public static void Load(AddonConditionsDocument? document) => _remote = document;

    // ==================== 内置默认（= 硬编码时代的行为，勿随意改） ====================

    /// <summary>默认的 slug 前缀条件（conditions.json 的 addonConditions 兜底）</summary>
    public static readonly AddonConditionEntry[] DefaultAddonConditions =
    [
        new() { SlugPrefix = "renodx-dlss5-super-anus", Dlss5 = true, LoadFromDllMain = true, HookPointCapable = true, RenoDxDlss5 = false },
        new() { SlugPrefix = "renodx-dlss5", Dlss5 = true, LoadFromDllMain = true, HookPointCapable = true, RenoDxDlss5 = true },
        new() { SlugPrefix = "renodx-dlss", Dlss5 = true, LoadFromDllMain = true, HookPointCapable = true },
        new() { SlugPrefix = "dlss5", Dlss5 = true, LoadFromDllMain = true },
        new() { SlugPrefix = "dlss5-feed", Feed = true },
    ];

    /// <summary>slug 条件匹配：远端 addonConditions 优先，空/缺失用默认表</summary>
    public static AddonConditionMatch MatchAddon(string? slug)
    {
        AddonConditionsDocument? remote = _remote;
        AddonConditionEntry[]? table = remote?.AddonConditions;
        if (table is { Length: > 0 })
        {
            AddonConditionMatch match = remote!.MatchAddon(slug);
            if (match is { Dlss5: false, LoadFromDllMain: false, HookPointCapable: false, RenoDxDlss5: false, Feed: false })
            {
                // 远端表没命中 → 再用默认表兜底（远端表可能没配全）
                AddonConditionMatch fallback = MatchInTable(DefaultAddonConditions, slug);
                return new AddonConditionMatch
                {
                    Dlss5 = fallback.Dlss5,
                    LoadFromDllMain = fallback.LoadFromDllMain,
                    HookPointCapable = fallback.HookPointCapable,
                    RenoDxDlss5 = fallback.RenoDxDlss5,
                    Feed = fallback.Feed,
                };
            }

            return match;
        }

        return MatchInTable(DefaultAddonConditions, slug);
    }

    private static AddonConditionMatch MatchInTable(AddonConditionEntry[] table, string? slug)
    {
        var doc = new AddonConditionsDocument { AddonConditions = table };
        return doc.MatchAddon(slug);
    }

    /// <summary>内部注册名映射：远端 internalNames 覆盖在默认表之上</summary>
    public static IReadOnlyDictionary<string, string> InternalNames()
    {
        Dictionary<string, string> merged = new(StringComparer.OrdinalIgnoreCase)
        {
            ["renodx-dlss5-super-anus"] = "RenoDX DLSS_A",
            ["renodx-dlss"] = "RenoDX DLSS",
            ["dlss5-bridge"] = "DLSS 5 Bridge",
        };

        if (_remote?.InternalNames is { Count: > 0 } remote)
        {
            foreach ((string key, string value) in remote)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    merged[key.Trim()] = value.Trim();
                }
            }
        }

        return merged;
    }

    // ---- DLSS5 dll 需求（DlssDllRequirements 的远端化）----

    /// <summary>DLSS5 默认的必需 dll 组（保持硬编码时代的并集语义：tags 有 dlss5 才生效）</summary>
    public static readonly (string[] Files, string? Note)[] DefaultDlss5Required =
    [
        (["nvngx_dlssnr.dll"], "DLSS5 的神经渲染运行时（nvngx_dlssnr.dll）"),
        (["sl.interposer.dll", "sl.dlss_nr.dll"], "Streamline 运行时（sl.interposer.dll / sl.dlss_nr.dll）"),
    ];

    /// <summary>这个 tag 的 dll 需求文件组（required 与 recommended 分开）；没有返回空</summary>
    public static (IReadOnlyList<(string[] Files, string? Note)> Required, IReadOnlyList<(string[] Files, string? Note)> Recommended)
        DllRequirementsOf(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)
            || !string.Equals(tag.Trim(), "dlss5", StringComparison.OrdinalIgnoreCase))
        {
            // 目前只有 dlss5 这一族有条件；其余 tag 无条件（与硬编码时代一致）
            // 远端表如果给别的 tag 配了条件，走到下面 remote 分支
            if (_remote?.DllRequirements is not { Length: > 0 })
            {
                return ([], []);
            }
        }

        AddonConditionsDocument? remote = _remote;
        if (remote is not null)
        {
            (string[][] required, string[][] recommended, Dictionary<string, string>? notes) = remote.DllRequirementOf(tag);
            if (required.Length > 0 || recommended.Length > 0)
            {
                return (
                    [.. required.Select(g => (g, NoteOf(g, notes)))],
                    [.. recommended.Select(g => (g, NoteOf(g, notes)))]);
            }

            if (remote.DllRequirements is { Length: > 0 })
            {
                // 远端表存在但这条 tag 没配 → 不是 dlss5 就空；是 dlss5 回落默认
                if (!string.Equals(tag?.Trim(), "dlss5", StringComparison.OrdinalIgnoreCase))
                {
                    return ([], []);
                }
            }
        }

        return string.Equals(tag?.Trim(), "dlss5", StringComparison.OrdinalIgnoreCase)
            ? ([.. DefaultDlss5Required], [])
            : ([], []);
    }

    private static string? NoteOf(string[] group, Dictionary<string, string>? notes)
    {
        if (notes is null)
        {
            return null;
        }

        return notes.TryGetValue(group[0], out string? note) ? note : null;
    }
}
