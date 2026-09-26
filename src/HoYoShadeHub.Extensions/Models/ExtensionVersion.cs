namespace HoYoShadeHub.Extensions.Models;

/// <summary>一个可安装的版本（GitHub Release 的 tag）</summary>
/// <param name="Tag">Release tag，例如 v1.2.3 或 1.0.8.18</param>
/// <param name="Published">发布时间：releases 列表页卡片里的 relative-time（= GitHub API 的 published_at）；
/// atom 的 updated 只是「最后编辑时间」，只在列表页拿不到时兜底；都没有就是 null</param>
public sealed record ExtensionVersion(string Tag, DateTimeOffset? Published = null);
