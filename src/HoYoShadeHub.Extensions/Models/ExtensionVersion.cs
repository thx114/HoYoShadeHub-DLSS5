namespace HoYoShadeHub.Extensions.Models;

/// <summary>一个可安装的版本（GitHub Release 的 tag）</summary>
/// <param name="Tag">Release tag，例如 v1.2.3 或 1.0.8.18</param>
/// <param name="Published">发布时间：atom 的 updated，或 releases 列表页卡片里的 relative-time；都没有就是 null</param>
public sealed record ExtensionVersion(string Tag, DateTimeOffset? Published = null);
