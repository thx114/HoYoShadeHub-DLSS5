using System.Text.Json.Serialization;

namespace HoYoShadeHub.Extensions.Models;

/// <summary>
/// 扩展来源类型
/// </summary>
public static class ExtensionSourceType
{
    /// <summary>GitHub Release 资产</summary>
    public const string GithubRelease = "github-release";

    /// <summary>直接下载地址（zip 或单个 dll）</summary>
    public const string Direct = "direct";

    /// <summary>本地文件 / 文件夹（作者自测用）</summary>
    public const string Local = "local";
}

/// <summary>
/// 扩展的下载来源描述
/// </summary>
public class ExtensionSource
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = ExtensionSourceType.GithubRelease;

    /// <summary>owner/repo</summary>
    [JsonPropertyName("repository")]
    public string? Repository { get; set; }

    /// <summary>资产名匹配（支持 * 与 ?）；为空时取第一个压缩包/插件</summary>
    [JsonPropertyName("assetPattern")]
    public string? AssetPattern { get; set; }

    /// <summary>
    /// 固定资产名（精确匹配，不支持通配）。
    /// 写死后就只需要解析出 tag，可以走 releases.atom，不必拉庞大的 Release JSON。
    /// </summary>
    [JsonPropertyName("assetName")]
    public string? AssetName { get; set; }

    /// <summary>Tag 正则过滤，例如 ^v?\d 用来跳过 nightly</summary>
    [JsonPropertyName("tagPattern")]
    public string? TagPattern { get; set; }

    /// <summary>直接下载地址</summary>
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    /// <summary>是否接受预发布版本</summary>
    [JsonPropertyName("includePrerelease")]
    public bool IncludePrerelease { get; set; }

    [JsonIgnore]
    public bool IsValid => Type switch
    {
        ExtensionSourceType.GithubRelease => !string.IsNullOrWhiteSpace(Repository) && Repository.Contains('/'),
        ExtensionSourceType.Direct => !string.IsNullOrWhiteSpace(Url),
        ExtensionSourceType.Local => true,
        _ => false,
    };
}

/// <summary>
/// 一个文件落位规则：把压缩包里的某些文件按 glob 铺到 HoYoShade 目录的某个位置
/// </summary>
public class ExtensionFileRule
{
    /// <summary>相对压缩包根目录的 glob，例如 **/*.addon64</summary>
    [JsonPropertyName("match")]
    public string Match { get; set; } = "**/*";

    /// <summary>HoYoShade 根目录下的目标相对路径，例如 reshade-shaders/Addons</summary>
    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    /// <summary>是否去掉源目录层级（默认 true，只取文件名铺平）</summary>
    [JsonPropertyName("flatten")]
    public bool Flatten { get; set; } = true;

    /// <summary>重命名（仅在匹配到单个文件时生效，可省略扩展名）</summary>
    [JsonPropertyName("rename")]
    public string? Rename { get; set; }

    /// <summary>该规则匹配不到文件时是否忽略（默认忽略；false 表示缺失即安装失败）</summary>
    [JsonPropertyName("optional")]
    public bool Optional { get; set; } = true;
}
