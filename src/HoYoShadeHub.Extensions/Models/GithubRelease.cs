using System.Text.Json.Serialization;

namespace HoYoShadeHub.Extensions.Models;

/// <summary>
/// GitHub Release（只保留我们用得到的字段，反序列化时忽略多余内容）
/// </summary>
public class GithubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("body")]
    public string? Body { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; set; }

    [JsonPropertyName("html_url")]
    public string? HtmlUrl { get; set; }

    [JsonPropertyName("assets")]
    public GithubAsset[] Assets { get; set; } = [];
}

public class GithubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("browser_download_url")]
    public string BrowserDownloadUrl { get; set; } = string.Empty;

    [JsonPropertyName("content_type")]
    public string? ContentType { get; set; }

    /// <summary>GitHub 的 digest 字段，形如 sha256:xxxx（部分 release 才有）</summary>
    [JsonPropertyName("digest")]
    public string? Digest { get; set; }
}
