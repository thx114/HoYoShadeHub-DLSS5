using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Dlls;

/// <summary>运行时 dll 归档里的一个版本（一个 familyId 下的一份版本）</summary>
public sealed class StoredDllVersion
{
    public string FamilyId { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    /// <summary>归档目录：&lt;CacheRoot&gt;\dlls\&lt;familyId&gt;\&lt;version&gt;\</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>归档时间（取目录的写入时间；读不到就是最小值）</summary>
    public DateTimeOffset StoredAt { get; init; }

    /// <summary>归档里的文件数</summary>
    public int FileCount { get; init; }
}

/// <summary>
/// 运行时 dll（nvngx_dlssnr.dll / sl.*.dll / nvngx_dlss*.dll）的版本归档库：
/// <c>&lt;CacheRoot&gt;\dlls\&lt;familyId&gt;\&lt;version&gt;\</c>。
///
/// <para>
/// 共享的 <c>reshade-shaders\Addons</c> 永远只放**一份当前生效**的版本（ReShade 只从那儿加载），
/// 这里另外留一份装过的版本 —— 换版本不用重新下载，也才能看出「盘上这一份是哪个版本」。
/// </para>
///
/// <para>
/// 文件用**硬链接**（同盘），几乎不占额外空间；跨盘退回复制。
/// 归档失败绝不能影响安装本身：所有方法都不抛，只返回计数 / 空表。
/// </para>
/// </summary>
public sealed class DllVersionStore
{
    /// <summary>缓存根下面这一层目录名</summary>
    public const string FolderName = "dlls";

    public DllVersionStore(string? cacheRoot)
    {
        RootPath = string.IsNullOrWhiteSpace(cacheRoot)
            ? string.Empty
            : Path.Combine(Path.GetFullPath(cacheRoot), FolderName);
    }

    /// <summary>&lt;CacheRoot&gt;\dlls</summary>
    public string RootPath { get; }

    public bool Exists => RootPath.Length > 0 && Directory.Exists(RootPath);

    /// <summary>归档目录：&lt;Root&gt;\&lt;familyId&gt;\&lt;version&gt;\</summary>
    public string DirectoryFor(string familyId, string version)
        => RootPath.Length == 0
            ? string.Empty
            : Path.Combine(RootPath, OptiScalerLibrary.Sanitize(familyId), OptiScalerLibrary.Sanitize(version));

    /// <summary>归档里有没有这个版本</summary>
    public bool Has(string familyId, string version)
    {
        string dir = DirectoryFor(familyId, version);
        return dir.Length > 0 && Directory.Exists(dir);
    }

    /// <summary>这一类 dll 归档过的所有版本（新 → 旧）</summary>
    public List<StoredDllVersion> ListVersions(string familyId)
    {
        var result = new List<StoredDllVersion>();
        if (RootPath.Length == 0)
        {
            return result;
        }

        string familyDir = Path.Combine(RootPath, OptiScalerLibrary.Sanitize(familyId));
        if (!Directory.Exists(familyDir))
        {
            return result;
        }

        try
        {
            foreach (string versionDir in Directory.EnumerateDirectories(familyDir))
            {
                DateTimeOffset time;
                try
                {
                    time = Directory.GetLastWriteTimeUtc(versionDir);
                }
                catch
                {
                    time = DateTimeOffset.MinValue;
                }

                int fileCount = 0;
                try
                {
                    fileCount = Directory.EnumerateFiles(versionDir, "*", SearchOption.TopDirectoryOnly).Count();
                }
                catch
                {
                    // 数不出来就算了
                }

                result.Add(new StoredDllVersion
                {
                    FamilyId = familyId,
                    Version = Path.GetFileName(versionDir),
                    Directory = versionDir,
                    StoredAt = time,
                    FileCount = fileCount,
                });
            }
        }
        catch
        {
            // 目录读不了就当没有
        }

        return [.. result.OrderByDescending(v => v.StoredAt)];
    }

    public List<string> InstalledVersions(string familyId)
        => [.. ListVersions(familyId).Select(v => v.Version)];

    /// <summary>只删这一版（其余版本与共享目录都不动）</summary>
    public bool DeleteVersion(string familyId, string version)
    {
        string dir = DirectoryFor(familyId, version);
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            return false;
        }

        HysxUtil.TryDeleteDirectory(dir);

        // 这个 family 的版本目录空了就把空目录一起收掉
        try
        {
            string? familyDir = Path.GetDirectoryName(dir);
            if (familyDir is not null && Directory.Exists(familyDir)
                && !Directory.EnumerateFileSystemEntries(familyDir).Any())
            {
                Directory.Delete(familyDir);
            }
        }
        catch
        {
            // 收不掉无所谓
        }

        return true;
    }

    /// <summary>
    /// 把刚装好的这些文件归档一份（硬链接优先，失败 / 跨盘复制）。
    /// 目标文件已经存在且大小一样就跳过（幂等）；任何一步失败都只记进结果，不抛。
    /// </summary>
    /// <returns>这次真正落位的文件数</returns>
    public int Archive(string familyId, string version, IEnumerable<string>? sourceFiles)
    {
        if (RootPath.Length == 0 || string.IsNullOrWhiteSpace(familyId) || string.IsNullOrWhiteSpace(version))
        {
            return 0;
        }

        string dir = DirectoryFor(familyId, version);
        int archived = 0;

        try
        {
            Directory.CreateDirectory(dir);

            foreach (string source in sourceFiles ?? [])
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                    {
                        continue;
                    }

                    string target = Path.Combine(dir, Path.GetFileName(source));
                    if (HysxFileLink.SizeEquals(source, target))
                    {
                        continue;
                    }

                    HysxFileLink.LinkOrCopy(source, target);
                    archived++;
                }
                catch
                {
                    // 单个文件归档失败不影响别的，更不影响安装
                }
            }
        }
        catch
        {
            return archived;
        }

        return archived;
    }
}
