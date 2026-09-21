using HoYoShadeHub.Extensions.Models;
using System.IO.Compression;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// 解析好的扩展载荷：一个临时目录（已解压）或一个单文件。
/// 用完必须 Dispose，否则临时目录会残留。
/// </summary>
public sealed class ResolvedExtensionPayload : IDisposable
{
    /// <summary>载荷根目录</summary>
    public required string PayloadRoot { get; init; }

    /// <summary>
    /// 非压缩包来源时，把下载到的文件当成这个虚拟相对路径参与规则匹配；
    /// 压缩包来源时为 null。
    /// </summary>
    public string? SingleFileName { get; init; }

    /// <summary>GitHub 资产的原文件名，用于展示</summary>
    public string? AssetName { get; init; }

    /// <summary>解析到的 Release tag（非 GitHub 来源为 null）</summary>
    public string? ResolvedTag { get; init; }

    /// <summary>下载地址</summary>
    public string? DownloadUrl { get; init; }

    private readonly List<string> _tempRoots = [];

    public void TrackTempRoot(string path) => _tempRoots.Add(path);

    public void Dispose()
    {
        foreach (string path in _tempRoots)
        {
            HysxUtil.TryDeleteDirectory(path);
        }
        _tempRoots.Clear();
    }
}

/// <summary>
/// 把 <see cref="ExtensionManifest"/> 的 source 描述变成本地可用的载荷。
/// </summary>
public sealed class ExtensionPackageFetcher
{
    private readonly GithubReleaseResolver _resolver;
    private readonly DownloadService _downloadService;

    public ExtensionPackageFetcher(GithubReleaseResolver? resolver = null, DownloadService? downloadService = null)
    {
        _resolver = resolver ?? new GithubReleaseResolver();
        _downloadService = downloadService ?? new DownloadService();
    }

    private static readonly string[] _archiveExtensions = [".zip"];

    public static bool IsArchive(string fileNameOrPath)
    {
        string ext = Path.GetExtension(fileNameOrPath);
        return _archiveExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    /// <param name="tagOverride">指定版本（GitHub tag）；null = 拿最新的</param>
    public async Task<ResolvedExtensionPayload> FetchAsync(
        ExtensionManifest manifest,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? tagOverride = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ExtensionSource source = manifest.Source;

        string workRoot = Path.Combine(Path.GetTempPath(), "HoYoShadeHub.Extensions", $"{Sanitize(manifest.Id)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workRoot);

        switch (source.Type)
        {
            case ExtensionSourceType.Local:
                return FetchLocal(manifest, workRoot);

            case ExtensionSourceType.Direct:
                return await FetchDirectAsync(manifest, workRoot, source, progress, cancellationToken);

            case ExtensionSourceType.GithubRelease:
                return await FetchGithubAsync(manifest, workRoot, source, progress, cancellationToken, tagOverride);

            default:
                HysxUtil.TryDeleteDirectory(workRoot);
                throw new NotSupportedException($"未知的 source.type: {source.Type}");
        }
    }

    private static ResolvedExtensionPayload FetchLocal(ExtensionManifest manifest, string workRoot)
    {
        string? path = manifest.Source.Url;
        if (string.IsNullOrWhiteSpace(path))
        {
            HysxUtil.TryDeleteDirectory(workRoot);
            throw new InvalidOperationException("local 来源必须提供 url（本地目录或压缩包路径）");
        }

        var payload = new ResolvedExtensionPayload
        {
            PayloadRoot = workRoot,
            AssetName = Path.GetFileName(path),
        };

        if (Directory.Exists(path))
        {
            CopyDirectory(path, workRoot);
            payload.TrackTempRoot(workRoot);
            return payload;
        }

        if (!File.Exists(path))
        {
            HysxUtil.TryDeleteDirectory(workRoot);
            throw new FileNotFoundException("找不到本地扩展包", path);
        }

        if (IsArchive(path))
        {
            ZipFile.ExtractToDirectory(path, workRoot, overwriteFiles: true);
            payload.TrackTempRoot(workRoot);
        }
        else
        {
            string dest = Path.Combine(workRoot, Path.GetFileName(path));
            File.Copy(path, dest, overwrite: true);
        }

        return payload;
    }

    private async Task<ResolvedExtensionPayload> FetchDirectAsync(
        ExtensionManifest manifest,
        string workRoot,
        ExtensionSource source,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        string url = source.Url!;
        string fileName = GetFileNameFromUrl(url);
        string downloadPath = Path.Combine(workRoot, fileName);

        await _downloadService.DownloadToFileAsync(url, downloadPath, manifest.Sha256, progress, cancellationToken);

        return ExtractOrKeep(manifest, workRoot, downloadPath, fileName, resolvedTag: null, downloadUrl: url);
    }

    private async Task<ResolvedExtensionPayload> FetchGithubAsync(
        ExtensionManifest manifest,
        string workRoot,
        ExtensionSource source,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        string? tagOverride = null)
    {
        if (string.IsNullOrWhiteSpace(source.Repository))
        {
            HysxUtil.TryDeleteDirectory(workRoot);
            throw new InvalidOperationException("github-release 来源必须提供 repository");
        }

        GithubArtifact? artifact = await _resolver.ResolveAsync(source, tagOverride, cancellationToken);
        if (artifact is null)
        {
            HysxUtil.TryDeleteDirectory(workRoot);
            throw new InvalidOperationException(tagOverride is null
                ? $"没有在 {source.Repository} 找到符合 tagPattern({source.TagPattern ?? "*"}) 的 Release/资产"
                : $"在 {source.Repository} 的 {tagOverride} 里没有找到符合的资产");
        }

        string downloadPath = Path.Combine(workRoot, Sanitize(artifact.AssetName));
        await _downloadService.DownloadToFileAsync(artifact.DownloadUrl, downloadPath, manifest.Sha256, progress, cancellationToken);

        return ExtractOrKeep(manifest, workRoot, downloadPath, artifact.AssetName, artifact.Tag, artifact.DownloadUrl);
    }

    private static ResolvedExtensionPayload ExtractOrKeep(
        ExtensionManifest manifest,
        string workRoot,
        string downloadPath,
        string assetName,
        string? resolvedTag,
        string? downloadUrl)
    {
        if (IsArchive(downloadPath))
        {
            string extractRoot = Path.Combine(workRoot, "payload");
            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(downloadPath, extractRoot, overwriteFiles: true);
            File.Delete(downloadPath);

            var payload = new ResolvedExtensionPayload
            {
                PayloadRoot = extractRoot,
                AssetName = assetName,
                ResolvedTag = resolvedTag,
                DownloadUrl = downloadUrl,
            };
            payload.TrackTempRoot(workRoot);
            return payload;
        }

        var single = new ResolvedExtensionPayload
        {
            PayloadRoot = workRoot,
            SingleFileName = assetName,
            AssetName = assetName,
            ResolvedTag = resolvedTag,
            DownloadUrl = downloadUrl,
        };
        single.TrackTempRoot(workRoot);
        return single;
    }

    private static string GetFileNameFromUrl(string url)
    {
        try
        {
            string path = new Uri(url).AbsolutePath;
            string name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? "payload.zip" : Sanitize(name);
        }
        catch (UriFormatException)
        {
            return "payload.zip";
        }
    }

    private static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
