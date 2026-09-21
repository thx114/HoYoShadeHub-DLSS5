using System.Text.Json.Serialization;

namespace HoYoShadeHub.Extensions.Models;

/// <summary>
/// 单个扩展的描述清单（catalog 条目 / .hysx 包里的 manifest.json 用同一套结构）
/// </summary>
public class ExtensionManifest
{
    /// <summary>全局唯一 id，建议 owner.repo 或反域名</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; set; } = "0.0.0";

    [JsonPropertyName("author")]
    public string? Author { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }

    [JsonPropertyName("tags")]
    public string[]? Tags { get; set; }

    /// <summary>
    /// 适用的游戏区服（GameBiz 字符串）。为空表示不限游戏。
    /// </summary>
    [JsonPropertyName("gameBiz")]
    public string[]? GameBiz { get; set; }

    /// <summary>
    /// 允许安装到的宿主，取值 HoYoShade / OpenHoYoShade。为空表示只允许 HoYoShade。
    /// </summary>
    [JsonPropertyName("hosts")]
    public string[]? Hosts { get; set; }

    [JsonPropertyName("source")]
    public ExtensionSource Source { get; set; } = new();

    [JsonPropertyName("rules")]
    public ExtensionFileRule[] Rules { get; set; } = [];

    /// <summary>
    /// 与之互斥的扩展 id：同一件事的不同实现，同时装进一个进程会打架。
    /// 安装时检查账本，命中直接拒绝。
    /// </summary>
    [JsonPropertyName("conflictsWith")]
    public string[]? ConflictsWith { get; set; }

    /// <summary>
    /// 这个插件装到 addons 目录里之后**文件名长什么样**（glob 数组，可以给几条）。
    ///
    /// 用处：插件如果不是 Hub 装的（自己从 Discord / GitHub 拿的），账本里没有记录，
    /// 全局插件页就认不出来 —— 显示「安装」、也没有全局开关。
    /// 有了这个 pattern，卡片可以自己去 addons 目录里认领文件。
    /// </summary>
    [JsonPropertyName("addonPatterns")]
    public string[]? AddonPatterns { get; set; }

    /// <summary>可选：整包 sha256，下载后校验</summary>
    [JsonPropertyName("sha256")]
    public string? Sha256 { get; set; }

    [JsonIgnore]
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(Id)
        && Source is not null
        && Source.IsValid
        && Rules.Length > 0;
}

/// <summary>
/// 扩展目录（catalog）文档
/// </summary>
public class ExtensionCatalogDocument
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("extensions")]
    public ExtensionManifest[] Extensions { get; set; } = [];
}
