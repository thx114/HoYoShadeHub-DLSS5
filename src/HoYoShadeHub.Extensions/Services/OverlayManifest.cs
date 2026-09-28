using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>覆盖包里一条运行时 dll 的声明</summary>
public sealed class OverlayManifestDll
{
    /// <summary>dll 家族：dlssnr / streamline / dlss / dlssd / dlssg（见 <see cref="Dlls.DllComponentCatalog.Families"/>）</summary>
    [JsonPropertyName("family")] public string? Family { get; set; }

    /// <summary>文件名（就在 Addons 目录里）或相对 HoYoShade 根的路径</summary>
    [JsonPropertyName("file")] public string? File { get; set; }

    /// <summary>清单里写的版本 —— 以它为准，PE 读不出来的也能归档</summary>
    [JsonPropertyName("version")] public string? Version { get; set; }

    [JsonPropertyName("note")] public string? Note { get; set; }
}

/// <summary>覆盖包里一个插件（addon）的声明</summary>
public sealed class OverlayManifestAddon
{
    [JsonPropertyName("file")] public string? File { get; set; }

    /// <summary>界面上显示的名字</summary>
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("version")] public string? Version { get; set; }
}

/// <summary>
/// 「包里的哪个目录盖到哪儿」：<c>to</c> 取 shade / optiscaler / modules / skip。
/// 不写的话按默认来（HoYoShade → shade，OptiScaler → optiscaler）。
/// </summary>
public sealed class OverlayManifestTarget
{
    [JsonPropertyName("from")] public string? From { get; set; }

    [JsonPropertyName("to")] public string? To { get; set; }
}

/// <summary>文件清单里的一条（装之前给用户看清单、也便于校验包完不完整）</summary>
public sealed class OverlayManifestFile
{
    [JsonPropertyName("path")] public string? Path { get; set; }

    [JsonPropertyName("size")] public long? Size { get; set; }
}

/// <summary>覆盖包里的 OptiScaler 构建声明</summary>
public sealed class OverlayManifestOptiScaler
{
    /// <summary>库内来源 id（<c>&lt;来源&gt;/&lt;版本&gt;</c> 的前半段）</summary>
    [JsonPropertyName("sourceId")] public string? SourceId { get; set; }

    [JsonPropertyName("version")] public string? Version { get; set; }

    /// <summary>装完要不要把它设成「当前启用」（默认 true）</summary>
    [JsonPropertyName("select")] public bool? Select { get; set; }
}

/// <summary>
/// 覆盖包顶层的 <c>filelist.json</c>：**一键覆盖包的唯一权威清单**。
///
/// <para>
/// 有了它，启动器就不用靠目录结构猜：装到哪儿、带的是哪些 dll / OptiScaler 构建 / 插件、
/// 分别是什么版本，全写在里面。没有它也能装（退回按目录结构识别），但会被当成不认识的包。
/// </para>
///
/// <code>
/// {
///   "hysxOverlay": 1,
///   "name": "星穹铁道 6 倍覆盖包",
///   "version": "1.1",
///   "game": "hkrpg",
///   "note": "DLSS5 + OptiScaler MFG Ada",
///   "targets": [ { "from": "HoYoShade", "to": "shade" },
///                { "from": "OptiScaler", "to": "optiscaler" } ],
///   "dlls": [ { "family": "dlssnr", "file": "nvngx_dlssnr.dll", "version": "310.9.1.0" } ],
///   "optiscaler": { "sourceId": "mfg-ada", "version": "mfg-ada-0.1.5" },
///   "addons": [ { "file": "renodx-dlss5.addon64", "name": "DLSS 5 神经渲染" } ],
///   "files": [ { "path": "HoYoShade/ReShade64.dll", "size": 5592064 } ]
/// }
/// </code>
/// </summary>
public sealed class OverlayManifest
{
    /// <summary>清单文件名（覆盖包顶层）</summary>
    public const string FileName = "filelist.json";

    /// <summary>目标代号：HoYoShade 框架根</summary>
    public const string TargetShade = "shade";

    /// <summary>目标代号：OptiScaler 本地库根</summary>
    public const string TargetOptiScaler = "optiscaler";

    /// <summary>目标代号：模块根</summary>
    public const string TargetModules = "modules";

    /// <summary>目标代号：不装，忽略这个目录</summary>
    public const string TargetSkip = "skip";

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>清单格式版本；写 <c>hysxOverlay</c> 或 <c>schema</c> 都认</summary>
    [JsonPropertyName("hysxOverlay")] public int Schema { get; set; }

    [JsonPropertyName("schema")] public int SchemaAlt { get; set; }

    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("version")] public string? Version { get; set; }

    /// <summary>给哪个游戏配的（hkrpg / hk4e / nap / bh3 …），只用于提示</summary>
    [JsonPropertyName("game")] public string? Game { get; set; }

    [JsonPropertyName("note")] public string? Note { get; set; }

    [JsonPropertyName("targets")] public List<OverlayManifestTarget> Targets { get; set; } = [];

    [JsonPropertyName("dlls")] public List<OverlayManifestDll> Dlls { get; set; } = [];

    [JsonPropertyName("addons")] public List<OverlayManifestAddon> Addons { get; set; } = [];

    [JsonPropertyName("optiscaler")] public OverlayManifestOptiScaler? OptiScaler { get; set; }

    [JsonPropertyName("files")] public List<OverlayManifestFile> Files { get; set; } = [];

    /// <summary>版本号取 <c>hysxOverlay</c>，没写就看 <c>schema</c></summary>
    public int EffectiveSchema => Schema > 0 ? Schema : SchemaAlt;

    public bool IsValid => EffectiveSchema > 0;

    /// <summary>界面上的名字：没写 name 就用版本号 / 「覆盖包」</summary>
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? "覆盖包" : Name.Trim();

    /// <summary>读覆盖包目录里的清单；没有 / 读不动 / 不是覆盖包清单 → null</summary>
    public static OverlayManifest? Load(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(directory, FileName);
            return File.Exists(path) ? Parse(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>解析清单：不是我们的清单（没写版本号）就当没有</summary>
    public static OverlayManifest? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            OverlayManifest? manifest = JsonSerializer.Deserialize<OverlayManifest>(json, _options);
            return manifest is { IsValid: true } ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
