namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// 便携版「只认自己目录树内的用户数据目录」这条规则 —— 单独拎出来是为了能自测。
///
/// <para>
/// 老实现是 <c>path.StartsWith(root + '\\')</c>，但 <paramref name="root"/> 自己
/// 不满足这个前缀（<c>"D:\APPS\HoYoShadeHub"</c> 不以 <c>"D:\APPS\HoYoShadeHub\"</c> 开头），
/// 于是便携根目录**自己**被排除掉了 —— 而便携版真正的用户数据目录恰好就是它。
/// 结果是 <c>TryFindExistingProfileFolder()</c> 在便携版下永远返回 null，
/// 主界面（<c>MainWindow.LoadContentView</c>）会兜底用默认目录，但
/// <c>playtime</c> / <c>rpc</c> 这些无界面子进程不会 —— 它们的 DB 连接串一直是空的
/// （SQLite 会静默开一个临时库），于是报
/// <c>no such table: PlayTimeItem</c>，游戏时长一条都记不下来。
/// </para>
/// </summary>
public static class PortableDataFolderScope
{
    /// <summary>目录是否在便携根目录**之内**（根目录自己也算）。任一侧为空 / 非法就返回 false。</summary>
    public static bool IsInside(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string fullRoot;
        string fullPath;
        try
        {
            fullRoot = TrimSeparator(Path.GetFullPath(root));
            fullPath = TrimSeparator(Path.GetFullPath(path));
        }
        catch
        {
            return false;
        }

        if (fullPath.Equals(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>「真便携」标记文件名：放在便携根目录（<c>HoYoShadeHub.exe</c> 旁边）</summary>
    public const string PortableMarkerFileName = ".portable";

    /// <summary>环境变量开关，效果和标记文件一样（脚本 / CI 里用）</summary>
    public const string PortableLocalEnvironmentVariable = "HYSHADE_PORTABLE_LOCAL";

    /// <summary>便携根目录里有没有 <c>.portable</c> 标记文件。</summary>
    public static bool HasPortableMarker(string? portableRoot)
    {
        if (string.IsNullOrWhiteSpace(portableRoot))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(portableRoot, PortableMarkerFileName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 「真便携」开着没有：便携根目录里有 <c>.portable</c> 标记文件，
    /// 或者环境变量 <c>HYSHADE_PORTABLE_LOCAL=1/true/yes/on</c>。
    ///
    /// <para>
    /// 开着的时候，缓存（webview / 缩略图 / 更新包 / github-cache）、<c>config.ini</c>、
    /// 数据库自动备份全部落在便携目录内部，一个字节都不写 C: 盘。
    /// 默认**不开**：老便携用户升级上来缓存目录还是 <c>%LOCALAPPDATA%\HoYoShadeHub</c>，
    /// 不会因为目录突然搬家看起来像「数据丢了」。
    /// </para>
    /// </summary>
    public static bool IsPortableLocalEnabled(string? portableRoot)
    {
        try
        {
            string? flag = Environment.GetEnvironmentVariable(PortableLocalEnvironmentVariable)?.Trim();
            if (!string.IsNullOrEmpty(flag) &&
                (flag.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                 flag.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                 flag.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                 flag.Equals("on", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch
        {
            // 环境变量读不到就当没设
        }

        return HasPortableMarker(portableRoot);
    }

    /// <summary>去掉结尾的分隔符（<c>D:\</c> → <c>D:</c>），但保留 <c>D:</c> 这种盘符本身。</summary>
    private static string TrimSeparator(string path)
    {
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? path : trimmed;
    }
}
