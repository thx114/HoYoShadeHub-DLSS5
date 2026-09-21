using HoYoShadeHub.Extensions.Models;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// OptiScaler 的下载器：列版本 → 列压缩包 → 下载 → 解压 → 落到本地库。
///
/// <para>
/// 这些 release 一次挂好几个 zip（主包、<c>-rtx40-mfg</c> 变体、补丁包、语言包），
/// 名字上看不出哪个是完整的，所以**不猜**：列出来让用户自己选一个。
/// </para>
/// </summary>
public sealed class OptiScalerDownloader
{
    private readonly GithubReleaseResolver _resolver;
    private readonly DownloadService _downloads;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public OptiScalerDownloader(GithubReleaseResolver? resolver = null, DownloadService? downloads = null)
    {
        _resolver = resolver ?? new GithubReleaseResolver();
        _downloads = downloads ?? new DownloadService();
    }

    /// <summary>这个来源能下载的版本（新 → 旧）</summary>
    public async Task<List<ExtensionVersion>> ListVersionsAsync(OptiScalerSource source, CancellationToken cancellationToken = default)
        => await _resolver.ListVersionsAsync(source.ToExtensionSource(), max: 20, cancellationToken);

    /// <summary>这个版本里能装的压缩包（.zip，去掉 sha256 清单那些）</summary>
    public async Task<List<string>> ListAssetsAsync(OptiScalerSource source, string tag, CancellationToken cancellationToken = default)
    {
        List<string> names = await _resolver.GetAssetNamesFromHtmlAsync(source.Repository, tag, cancellationToken);
        return [.. names.Where(IsUsableAsset).Order(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>能装的包：zip 压缩包，或者来源自己的安装程序（.exe）</summary>
    public static bool IsUsableAsset(string name)
    {
        bool archive = name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        bool installer = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

        if (!archive && !installer)
        {
            return false;
        }

        return !name.Contains("sha256", StringComparison.OrdinalIgnoreCase)
               && !name.Contains("checksum", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>是这个来源自己的安装程序（不是压缩包）—— 这种要下下来直接运行</summary>
    public static bool IsInstaller(string name)
        => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>名字里带 patch 的是补丁包（要覆盖在主包上，单独装是残缺的）</summary>
    public static bool LooksLikePatch(string name)
        => name.Contains("patch", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 只有一个包时直接用它；有多个时：先 zip（能整包解压进库里、参与注入），再非补丁，再名字短的。
    /// （有 zip 就不要顺手去跑人家的安装程序。）
    /// </summary>
    public static string? PickAsset(IEnumerable<string> names)
        => names
            .Where(IsUsableAsset)
            .OrderBy(n => IsInstaller(n) ? 1 : 0)
            .ThenBy(n => LooksLikePatch(n) ? 1 : 0)
            .ThenBy(n => n.Length)
            .FirstOrDefault();

    /// <summary>
    /// 下载并解压一个构建到本地库（同名版本会被覆盖重装）。
    /// </summary>
    /// <param name="assetName">要下载的资产名；为空时按 <see cref="PickAsset"/> 挑一个。</param>
    /// <param name="confirmBeforeRun">
    /// 安装程序下载完之后、**运行之前**问一句（返回 false = 先不跑，文件留在库里）。
    /// 界面用它弹「请装到默认目录」那个提醒。
    /// </param>
    public async Task<OptiScalerBuild> InstallAsync(
        OptiScalerSource source,
        string tag,
        string? assetName,
        OptiScalerLibrary library,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<string, Task<bool>>? confirmBeforeRun = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(library);

        if (string.IsNullOrWhiteSpace(tag))
        {
            throw new ArgumentException("要指定版本（GitHub tag）", nameof(tag));
        }

        string? asset = assetName;

        if (string.IsNullOrWhiteSpace(asset))
        {
            List<string> assets = await ListAssetsAsync(source, tag, cancellationToken);
            asset = PickAsset(assets);
        }

        if (string.IsNullOrWhiteSpace(asset))
        {
            throw new InvalidOperationException($"在 {source.Repository} 的 {tag} 里没有找到可下载的包。");
        }

        if (!IsUsableAsset(asset))
        {
            // 7z 之类：Extensions 这边没有解压器，明确告诉用户而不是下完了才失败
            throw new NotSupportedException($"只支持 .zip 压缩包或 .exe 安装程序，这个 release 里挑出来的是 {asset}。");
        }

        ExtensionSource ext = source.ToExtensionSource();
        ext.AssetName = asset;

        GithubArtifact? artifact = await _resolver.ResolveAsync(ext, tag, cancellationToken);
        if (artifact is null)
        {
            throw new InvalidOperationException($"在 {source.Repository} 的 {tag} 里没有找到 {asset}。");
        }

        // 安装程序（比如 DLSS NR on AMD 的 setup.exe）：下到构建目录里直接运行它 ——
        // 它有自己的界面（让用户选装到哪个游戏目录），所以不进「解压 → 落库」这条流水线。
        if (IsInstaller(asset))
        {
            string setupTarget = library.DirectoryFor(source.Id, tag);
            if (Directory.Exists(setupTarget))
            {
                HysxUtil.TryDeleteDirectory(setupTarget);
            }

            Directory.CreateDirectory(setupTarget);

            string setupPath = Path.Combine(setupTarget, OptiScalerLibrary.Sanitize(artifact.AssetName));
            await _downloads.DownloadToFileAsync(artifact.DownloadUrl, setupPath, null, progress, cancellationToken);

            WriteBuildManifest(setupTarget, source, tag, artifact.AssetName);

            // 运行之前先让界面问一句（「即将启动安装程序，请装到默认目录」）
            bool run = confirmBeforeRun is null || await confirmBeforeRun(setupPath);
            if (run)
            {
                await TryRunSetupAsync(setupPath, setupTarget, cancellationToken);
            }

            return FindInLibrary(library, source.Id, tag);
        }

        string workRoot = Path.Combine(Path.GetTempPath(), "HoYoShadeHub.OptiScaler", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workRoot);

        try
        {
            string zipPath = Path.Combine(workRoot, OptiScalerLibrary.Sanitize(artifact.AssetName));
            await _downloads.DownloadToFileAsync(artifact.DownloadUrl, zipPath, null, progress, cancellationToken);

            string extractRoot = Path.Combine(workRoot, "payload");
            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(zipPath, extractRoot, overwriteFiles: true);

            string target = library.DirectoryFor(source.Id, tag);
            if (Directory.Exists(target))
            {
                HysxUtil.TryDeleteDirectory(target);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            MoveDirectory(extractRoot, target);

            WriteBuildManifest(target, source, tag, artifact.AssetName);

            return FindInLibrary(library, source.Id, tag);
        }
        finally
        {
            HysxUtil.TryDeleteDirectory(workRoot);
        }
    }

    private static void WriteBuildManifest(string directory, OptiScalerSource source, string tag, string? assetName)
    {
        File.WriteAllText(
            Path.Combine(directory, OptiScalerLibrary.BuildManifestName),
            JsonSerializer.Serialize(new OptiScalerBuildManifest
            {
                SourceId = source.Id,
                Version = tag,
                AssetName = assetName,
                InstalledAt = DateTimeOffset.Now,
            }, _jsonOptions));
    }

    private static OptiScalerBuild FindInLibrary(OptiScalerLibrary library, string sourceId, string tag)
    {
        string id = OptiScalerLibrary.MakeId(sourceId, tag);
        return library.List().FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? throw new InvalidOperationException("下载完成了，但库里读不到这个构建（目录可能不可写）。");
    }

    /// <summary>运行来源自己的安装程序（等它退出）。跑不起来不致命 —— 文件已经在库里了，用户自己双击也行。</summary>
    private static async Task TryRunSetupAsync(string setupPath, string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            using Process? setup = Process.Start(new ProcessStartInfo(setupPath)
            {
                UseShellExecute = true,
                WorkingDirectory = workingDirectory,
            });

            if (setup is not null)
            {
                await setup.WaitForExitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 静默：页面上会提示「安装程序已下载，没装成的话点打开目录手动运行」
        }
    }

    /// <summary>临时目录和库目录可能不在一个盘上，Directory.Move 会炸，所以退回复制。</summary>
    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
            return;
        }
        catch (IOException)
        {
            // 跨卷 / 目标已存在 → 复制
        }

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

        HysxUtil.TryDeleteDirectory(source);
    }
}
