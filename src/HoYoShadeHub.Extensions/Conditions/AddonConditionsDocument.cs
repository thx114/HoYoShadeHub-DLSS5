using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoYoShadeHub.Extensions.Conditions;

/// <summary>
/// 一条 addon slug 前缀条件（catalog/conditions.json 的 addonConditions[] 元素）。
/// 按 slug 前缀匹配（OrdinalIgnoreCase），命中的条件取并集。
/// </summary>
public sealed class AddonConditionEntry
{
    [JsonPropertyName("slugPrefix")]
    public string SlugPrefix { get; set; } = string.Empty;

    /// <summary>是不是 DLSS5 那一类（决定 dll 需求 / DllMain 加载）</summary>
    [JsonPropertyName("dlss5")]
    public bool? Dlss5 { get; set; }

    /// <summary>要不要从 DllMain 加载</summary>
    [JsonPropertyName("loadFromDllMain")]
    public bool? LoadFromDllMain { get; set; }

    /// <summary>能不能改 hook 点（renodx-dlss* 族）</summary>
    [JsonPropertyName("hookPointCapable")]
    public bool? HookPointCapable { get; set; }

    /// <summary>是不是 RenoDX DLSS5 主插件（呈现模式要 DX11Source=native）</summary>
    [JsonPropertyName("renoDxDlss5")]
    public bool? RenoDxDlss5 { get; set; }

    /// <summary>是不是 DLSS5 Feed（预设 technique 同步对象）</summary>
    [JsonPropertyName("feed")]
    public bool? Feed { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    public bool Matches(string? slug) =>
        !string.IsNullOrWhiteSpace(slug)
        && !string.IsNullOrWhiteSpace(SlugPrefix)
        && slug.StartsWith(SlugPrefix, StringComparison.OrdinalIgnoreCase);
}

/// <summary>一条按 tag 匹配的 dll 需求（dllRequirements[] 元素）。files 内多文件是「与」关系。</summary>
public sealed class DllRequirementByTag
{
    [JsonPropertyName("tag")]
    public string Tag { get; set; } = string.Empty;

    [JsonPropertyName("required")]
    public string[][] Required { get; set; } = [];

    [JsonPropertyName("recommended")]
    public string[][] Recommended { get; set; } = [];

    [JsonPropertyName("notes")]
    public Dictionary<string, string>? Notes { get; set; }
}

/// <summary>dll 族定义（dllFamilies[] 元素，对应 DllComponentCatalog.Families）</summary>
public sealed class DllFamilyEntry
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string DisplayName { get; set; } = string.Empty;

    [JsonPropertyName("filePattern")]
    public string FilePattern { get; set; } = string.Empty;

    /// <summary>required | recommended</summary>
    [JsonPropertyName("level")]
    public string Level { get; set; } = "recommended";

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonPropertyName("preferredVersion")]
    public string? PreferredVersion { get; set; }
}

/// <summary>清单里没有、但确实存在的补充组件（dllExtras[] 元素）</summary>
public sealed class DllExtraEntry
{
    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("note")]
    public string? Note { get; set; }
}

/// <summary>屏蔽某个组件版本（blockedVersions[] 元素）</summary>
public sealed class BlockedVersionEntry
{
    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }
}

/// <summary>OptiScaler 依赖清单（optiscaler 段）</summary>
public sealed class OptiScalerConditions
{
    [JsonPropertyName("streamlineFolder")]
    public string? StreamlineFolder { get; set; }

    [JsonPropertyName("streamlineFiles")]
    public string[]? StreamlineFiles { get; set; }

    [JsonPropertyName("dlssgFile")]
    public string? DlssgFile { get; set; }

    [JsonPropertyName("upscalerRuntimeDlls")]
    public string[]? UpscalerRuntimeDlls { get; set; }
}

/// <summary>DLSS5 Feed 身份（feedAddon 段）</summary>
public sealed class FeedAddonConditions
{
    [JsonPropertyName("slugPrefix")]
    public string? SlugPrefix { get; set; }

    [JsonPropertyName("techniques")]
    public string[]? Techniques { get; set; }

    [JsonPropertyName("effectFiles")]
    public string[]? EffectFiles { get; set; }

    [JsonPropertyName("motionVectorProviderName")]
    public string? MotionVectorProviderName { get; set; }

    [JsonPropertyName("motionVectorProviderValue")]
    public string? MotionVectorProviderValue { get; set; }
}

/// <summary>
/// 远端条件表文档（catalog/conditions.json）。读不到的字段一律用内置默认（见
/// <see cref="AddonConditions.Default"/>），保证离线行为与硬编码时代一致。
/// </summary>
public sealed class AddonConditionsDocument
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("addonConditions")]
    public AddonConditionEntry[]? AddonConditions { get; set; }

    [JsonPropertyName("dllRequirements")]
    public DllRequirementByTag[]? DllRequirements { get; set; }

    [JsonPropertyName("dllFamilies")]
    public DllFamilyEntry[]? DllFamilies { get; set; }

    [JsonPropertyName("dllExtras")]
    public DllExtraEntry[]? DllExtras { get; set; }

    [JsonPropertyName("blockedVersions")]
    public BlockedVersionEntry[]? BlockedVersions { get; set; }

    [JsonPropertyName("optiscaler")]
    public OptiScalerConditions? OptiScaler { get; set; }

    [JsonPropertyName("internalNames")]
    public Dictionary<string, string>? InternalNames { get; set; }

    [JsonPropertyName("feedAddon")]
    public FeedAddonConditions? FeedAddon { get; set; }

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static AddonConditionsDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AddonConditionsDocument>(json, _options);
        }
        catch
        {
            // 文件坏了当没有：全部回落内置默认
            return null;
        }
    }

    /// <summary>
    /// 把 slug 命中的条件合并；每个标志取**第一条**命中且显式配置了该标志的条目
    /// （表按「具体前缀在前」排，例如 renodx-dlss5-super-anus 排在 renodx-dlss5 前，
    /// 就能盖住主插件判定）。没命中返回全 false。
    /// </summary>
    public AddonConditionMatch MatchAddon(string? slug)
    {
        var result = new AddonConditionMatch();
        bool dlss5Set = false, dllMainSet = false, hookSet = false, renoSet = false, feedSet = false;

        foreach (AddonConditionEntry? entry in AddonConditions ?? [])
        {
            if (entry is null || !entry.Matches(slug))
            {
                continue;
            }

            if (!dlss5Set && entry.Dlss5.HasValue) { result.Dlss5 = entry.Dlss5.Value; dlss5Set = true; }
            if (!dllMainSet && entry.LoadFromDllMain.HasValue) { result.LoadFromDllMain = entry.LoadFromDllMain.Value; dllMainSet = true; }
            if (!hookSet && entry.HookPointCapable.HasValue) { result.HookPointCapable = entry.HookPointCapable.Value; hookSet = true; }
            if (!renoSet && entry.RenoDxDlss5.HasValue) { result.RenoDxDlss5 = entry.RenoDxDlss5.Value; renoSet = true; }
            if (!feedSet && entry.Feed.HasValue) { result.Feed = entry.Feed.Value; feedSet = true; }
        }

        return result;
    }

    /// <summary>这个 tag 的 dll 需求（required/recommended 文件组）；没有返回空</summary>
    public (string[][] Required, string[][] Recommended, Dictionary<string, string>? Notes) DllRequirementOf(string? tag)
    {
        foreach (DllRequirementByTag? entry in DllRequirements ?? [])
        {
            if (entry is not null
                && !string.IsNullOrWhiteSpace(tag)
                && string.Equals(entry.Tag, tag, StringComparison.OrdinalIgnoreCase))
            {
                return (entry.Required ?? [], entry.Recommended ?? [], entry.Notes);
            }
        }

        return ([], [], null);
    }
}

/// <summary>slug 命中条件的并集结果</summary>
public sealed class AddonConditionMatch
{
    public bool Dlss5 { get; internal set; }

    public bool LoadFromDllMain { get; internal set; }

    public bool HookPointCapable { get; internal set; }

    public bool RenoDxDlss5 { get; internal set; }

    public bool Feed { get; internal set; }
}
