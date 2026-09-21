using System.Text.Json.Serialization;

namespace HoYoShadeHub.Extensions.Models;

/// <summary>
/// 一个已安装扩展的落盘记录。卸载时按这个清单逐个校验后删除，
/// 绝不直接删目录 —— reshade-shaders 里混着官方包和用户自己放的文件。
/// </summary>
public class InstalledExtension
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("version")]
    public string Version { get; set; } = string.Empty;

    [JsonPropertyName("installedAt")]
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.Now;

    /// <summary>来源快照，用于「检查更新」和二次安装</summary>
    [JsonPropertyName("source")]
    public ExtensionSource? Source { get; set; }

    /// <summary>解析到的具体版本 tag / 下载地址，便于直接复现</summary>
    [JsonPropertyName("resolvedTag")]
    public string? ResolvedTag { get; set; }

    [JsonPropertyName("files")]
    public List<InstalledExtensionFile> Files { get; set; } = [];

    [Serializable]
    public class InstalledExtensionFile
    {
        /// <summary>相对 HoYoShade 根目录的路径，例如 reshade-shaders/Addons/Foo.addon64</summary>
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("sha256")]
        public string Sha256 { get; set; } = string.Empty;

        /// <summary>true 表示该文件是我们新建的；false 表示覆盖了原有文件（卸载时不会删，只提示）</summary>
        [JsonPropertyName("created")]
        public bool Created { get; set; } = true;
    }
}

/// <summary>
/// 某个 HoYoShade 宿主目录下的扩展安装账本
/// </summary>
public class InstalledExtensionLedger
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("extensions")]
    public List<InstalledExtension> Extensions { get; set; } = [];

    [JsonIgnore]
    public static System.Text.Json.JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
