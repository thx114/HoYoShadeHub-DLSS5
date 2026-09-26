using HoYoShadeHub.Extensions.Models;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>归档里的一个版本（一个扩展 id 下的一份 tag）</summary>
public sealed class StoredAddonVersion
{
    public string ExtensionId { get; init; } = string.Empty;

    public string Tag { get; init; } = string.Empty;

    /// <summary>归档目录：&lt;CacheRoot&gt;\plugins\&lt;extId&gt;\&lt;tag&gt;\</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>归档时间（取目录的写入时间；读不到就是最小值）</summary>
    public DateTimeOffset StoredAt { get; init; }
}

/// <summary>一次归档的结果</summary>
public sealed class AddonArchiveResult
{
    public string ExtensionId { get; init; } = string.Empty;

    public string Tag { get; init; } = string.Empty;

    public string Directory { get; init; } = string.Empty;

    /// <summary>这次真正落位的文件（硬链接 + 复制）</summary>
    public int Archived { get; init; }

    public int Linked { get; init; }

    public int Copied { get; init; }

    public int Skipped { get; init; }

    public int Missing { get; init; }

    public bool Ok => Missing == 0;
}

/// <summary>
/// 插件（addon 扩展）的版本归档库：<c>&lt;CacheRoot&gt;\plugins\&lt;extId&gt;\&lt;tag&gt;\</c>。
///
/// <para>
/// 共享的 <c>reshade-shaders\Addons</c> 永远只放**一份当前生效**的版本（账本也是这个语义），
/// 这里另外留一份装过的版本：反复切版本不用重新下载，也才能给每个游戏拼出
/// 「这个游戏用 1.0、那个游戏用 1.1」的插件包。
/// </para>
///
/// <para>
/// 文件用**硬链接**（同盘），几乎不占额外空间；跨盘退回复制。归档里的相对布局和
/// HoYoShade 根目录一致，所以按 <c>reshade-shaders/Addons/</c> 挑出来就是那一版的插件文件。
/// </para>
/// </summary>
public sealed class AddonVersionStore
{
    /// <summary>缓存根下面这一层目录名</summary>
    public const string FolderName = "plugins";

    public AddonVersionStore(string? cacheRoot)
    {
        RootPath = string.IsNullOrWhiteSpace(cacheRoot)
            ? string.Empty
            : Path.Combine(Path.GetFullPath(cacheRoot), FolderName);
    }

    /// <summary>&lt;CacheRoot&gt;\plugins</summary>
    public string RootPath { get; }

    public bool Exists => RootPath.Length > 0 && Directory.Exists(RootPath);

    /// <summary>归档目录：&lt;Root&gt;\&lt;extId&gt;\&lt;tag&gt;\</summary>
    public string DirectoryFor(string extensionId, string tag)
        => RootPath.Length == 0
            ? string.Empty
            : Path.Combine(RootPath, OptiScalerLibrary.Sanitize(extensionId), OptiScalerLibrary.Sanitize(tag));

    /// <summary>归档里有没有这个版本</summary>
    public bool Has(string extensionId, string tag)
    {
        string dir = DirectoryFor(extensionId, tag);
        return dir.Length > 0 && Directory.Exists(dir);
    }

    /// <summary>这个扩展归档过的所有版本（新 → 旧）</summary>
    public List<StoredAddonVersion> ListVersions(string extensionId)
    {
        var result = new List<StoredAddonVersion>();
        if (RootPath.Length == 0)
        {
            return result;
        }

        string extDir = Path.Combine(RootPath, OptiScalerLibrary.Sanitize(extensionId));
        if (!Directory.Exists(extDir))
        {
            return result;
        }

        try
        {
            foreach (string tagDir in Directory.EnumerateDirectories(extDir))
            {
                DateTimeOffset time;
                try
                {
                    time = Directory.GetLastWriteTimeUtc(tagDir);
                }
                catch
                {
                    time = DateTimeOffset.MinValue;
                }

                result.Add(new StoredAddonVersion
                {
                    ExtensionId = extensionId,
                    Tag = Path.GetFileName(tagDir),
                    Directory = tagDir,
                    StoredAt = time,
                });
            }
        }
        catch
        {
            // 目录读不了就当没有
        }

        return [.. result.OrderByDescending(v => v.StoredAt)];
    }

    public List<string> InstalledTags(string extensionId)
        => [.. ListVersions(extensionId).Select(v => v.Tag)];

    /// <summary>只删这一版（其余版本与共享目录都不动）</summary>
    public bool DeleteVersion(string extensionId, string tag)
    {
        string dir = DirectoryFor(extensionId, tag);
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            return false;
        }

        HysxUtil.TryDeleteDirectory(dir);

        // 这个扩展的版本目录空了就把空目录一起收掉
        try
        {
            string? extDir = Path.GetDirectoryName(dir);
            if (extDir is not null && Directory.Exists(extDir)
                && !Directory.EnumerateFileSystemEntries(extDir).Any())
            {
                Directory.Delete(extDir);
            }
        }
        catch
        {
            // 收不掉无所谓
        }

        return true;
    }

    /// <summary>归档里这个版本的文件（完整路径，递归）</summary>
    public List<string> FilesOf(string extensionId, string tag)
    {
        string dir = DirectoryFor(extensionId, tag);
        if (dir.Length == 0 || !Directory.Exists(dir))
        {
            return [];
        }

        try
        {
            return [.. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>
    /// 归档里这个版本**会落到共享 Addons 目录**的那些文件（按共享目录的文件名挑出来）。
    /// 每游戏插件包用它替换共享目录里对应扩展的文件。
    /// </summary>
    public List<string> AddonFiles(string extensionId, string tag)
    {
        string dir = DirectoryFor(extensionId, tag);
        if (dir.Length == 0)
        {
            return [];
        }

        string addonsRoot = Path.Combine(dir, "reshade-shaders", "Addons");
        if (!Directory.Exists(addonsRoot))
        {
            return [];
        }

        try
        {
            return [.. Directory.EnumerateFiles(addonsRoot, "*", SearchOption.AllDirectories)];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>读这条已装记录对应的版本 tag（优先 resolvedTag，退回 version）；读不到返回 null</summary>
    public static string? TagOf(InstalledExtension record)
    {
        string? tag = string.IsNullOrWhiteSpace(record.ResolvedTag) ? record.Version : record.ResolvedTag;
        return string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
    }

    /// <summary>
    /// 把一条已装记录的文件归档一份。硬链接优先，失败/跨盘复制。
    /// 目标文件已经存在且大小一样就跳过（幂等，可以做很多次）。
    /// </summary>
    public AddonArchiveResult Archive(ShadeHost host, InstalledExtension record)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(record);

        string? tag = TagOf(record);
        if (tag is null || RootPath.Length == 0)
        {
            return new AddonArchiveResult { ExtensionId = record.Id, Tag = tag ?? string.Empty };
        }

        string dir = DirectoryFor(record.Id, tag);
        Directory.CreateDirectory(dir);

        int archived = 0;
        int linked = 0;
        int copied = 0;
        int skipped = 0;
        int missing = 0;

        foreach (InstalledExtension.InstalledExtensionFile file in record.Files)
        {
            string source;
            try
            {
                source = host.ResolveRelative(file.Path);
            }
            catch
            {
                missing++;
                continue;
            }

            if (!File.Exists(source))
            {
                missing++;
                continue;
            }

            string relative = file.Path.Replace('/', Path.DirectorySeparatorChar);
            string target = Path.Combine(dir, relative);

            if (HysxFileLink.SizeEquals(source, target))
            {
                skipped++;
                continue;
            }

            try
            {
                bool hard = HysxFileLink.LinkOrCopy(source, target);
                archived++;
                if (hard)
                {
                    linked++;
                }
                else
                {
                    copied++;
                }
            }
            catch
            {
                missing++;
            }
        }

        return new AddonArchiveResult
        {
            ExtensionId = record.Id,
            Tag = tag,
            Directory = dir,
            Archived = archived,
            Linked = linked,
            Copied = copied,
            Skipped = skipped,
            Missing = missing,
        };
    }
}
