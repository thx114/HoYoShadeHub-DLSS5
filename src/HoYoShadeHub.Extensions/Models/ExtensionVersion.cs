namespace HoYoShadeHub.Extensions.Models;

/// <summary>一个可安装的版本（GitHub Release 的 tag）</summary>
/// <param name="Tag">Release tag，例如 v1.2.3 或 1.0.8.18</param>
/// <param name="Published">发布时间（atom 里有，翻 HTML 拿到的那批没有）</param>
public sealed record ExtensionVersion(string Tag, DateTimeOffset? Published = null);
