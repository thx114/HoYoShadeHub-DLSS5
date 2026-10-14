using HoYoShadeHub.Core;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Helpers;
using Microsoft.Extensions.Logging;
using NuGet.Versioning;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Update;

/// <summary>GitHub 渠道里的一个可装版本</summary>
public sealed class GithubVersionInfo
{
    // 注意：这些属性会被 WinUI 的 XamlTypeInfo.g.cs 当绑定目标 set 一遍，
    // 所以不能用 record 的 init（会 CS8852），要用可写属性。
    public string Tag { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string AssetName { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>「当前版本」/「比当前新」/「比当前旧」</summary>
    public string Note { get; set; } = string.Empty;

    public bool IsCurrent => Note.Contains("当前", StringComparison.Ordinal);

    public override string ToString() => $"{Tag}（{Note}）";
}

/// <summary>
/// **本 fork 自己的更新渠道**：直接从 GitHub Release 装便携包，也能装旧版本（= 退回）。
///
/// <para>
/// 官方渠道走 RPC 元数据（<see cref="UpdateService"/>）；这里走我们仓库的 Release。
/// 资产固定叫 <c>HoYoShadeHub_Portable_&lt;版本&gt;_x64.zip</c>，解析复用 Extensions 里现成的
/// <see cref="GithubReleaseResolver"/>（releases.atom + expanded_assets HTML，不吃 API 限额）。
/// </para>
///
/// <para>
/// 「安装」= 把 zip 解压到便携包根目录：会多出一个 <c>app-&lt;版本&gt;\</c> 目录并改写 <c>version.ini</c>，
/// **旧版本目录原样留着** —— 所以退回就是再装一个旧版本（或者把 version.ini 指回去）。
/// 装之前会把当前 version.ini 备份到 <c>&lt;用户数据目录&gt;\.hysx\update-backup\</c>。
/// </para>
/// </summary>
internal sealed class GithubUpdateService
{
    /// <summary>本 fork 的仓库</summary>
    public const string Repository = "thx114/HoYoShadeHub-DLSS5";

    private const string AssetPattern = "HoYoShadeHub_Portable_*_x64.zip";

    private readonly ILogger<GithubUpdateService> _logger = AppConfig.GetLogger<GithubUpdateService>();

    private readonly GithubReleaseResolver _resolver = new();

    private readonly DownloadService _downloads = new();

    private static ExtensionSource Source => new()
    {
        Type = ExtensionSourceType.GithubRelease,
        Repository = Repository,
        AssetPattern = AssetPattern,
        TagPattern = @"^v?\d",
    };

    /// <summary>便携包根目录（<c>&lt;root&gt;\app-&lt;ver&gt;\</c> 的上一级）；不是便携版就是 null</summary>
    public static string? PortableRoot
        => AppConfig.IsPortable ? new DirectoryInfo(AppContext.BaseDirectory).Parent?.FullName : null;

    /// <summary>zip 与 version.ini 备份放这儿：&lt;用户数据目录&gt;\.hysx\update-backup</summary>
    public static string BackupDirectory
        => string.IsNullOrWhiteSpace(AppConfig.UserDataFolder)
            ? Path.Combine(TemporaryFolder.Path, "HoYoShadeHub-update-backup")
            : Path.Combine(AppConfig.UserDataFolder, ".hysx", "update-backup");

    /// <summary>仓库里所有能装的版本（新 → 旧），带「当前 / 比当前新 / 比当前旧」标注</summary>
    public async Task<List<GithubVersionInfo>> ListVersionsAsync(CancellationToken cancellationToken = default)
    {
        List<ExtensionVersion> versions = await _resolver.ListVersionsAsync(Source, max: 30, cancellationToken);

        _ = NuGetVersion.TryParse(NormalizeVersion(AppConfig.AppVersion), out NuGetVersion? current);

        var result = new List<GithubVersionInfo>();

        foreach (ExtensionVersion version in versions)
        {
            string normalized = NormalizeVersion(version.Tag);
            _ = NuGetVersion.TryParse(normalized, out NuGetVersion? parsed);

            string note = "比当前旧";
            if (parsed is null || current is null)
            {
                note = "版本号认不出来";
            }
            else if (parsed == current)
            {
                note = "当前版本";
            }
            else if (parsed > current)
            {
                note = "比当前新";
            }

            result.Add(new GithubVersionInfo
            {
                Tag = version.Tag,
                Version = normalized,
                Note = note,
            });
        }

        return result;
    }

    /// <summary>下载指定 tag 的便携包，返回本地 zip 路径</summary>
    public async Task<string> DownloadAsync(
        string tag,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        GithubArtifact? artifact = await _resolver.ResolveAsync(Source, tag, cancellationToken);

        if (artifact is null)
        {
            throw new InvalidOperationException($"在 {Repository} 的 {tag} 里没找到 {AssetPattern} 这个资产。");
        }

        Directory.CreateDirectory(BackupDirectory);
        string zipPath = Path.Combine(BackupDirectory, Sanitize(artifact.AssetName));

        await _downloads.DownloadToFileAsync(artifact.DownloadUrl, zipPath, null, progress, cancellationToken);
        _logger.LogInformation("GitHub update downloaded: {Tag} -> {Path}", tag, zipPath);

        return zipPath;
    }

    /// <summary>
    /// 把 zip 解压到便携包根目录（overwrite）。装完 **要重启** 才生效 —— 新版本在 app-&lt;版本&gt;\ 里。
    /// </summary>
    /// <remarks>
    /// 两个历史坑都在这里兜底：
    /// ① v1.3.8.x 的发布 zip 把所有条目套进了 <c>HoYoShadeHub/</c> 一层（package.ps1 的
    /// <c>$entryPrefix</c> bug，已修）—— 直接解到根上会多出一个嵌套目录、根上的 version.ini
    /// 原封不动，重启还是旧版本（「更新完没变化」的根因）。检测到套壳就把那层剥掉。
    /// ② 同版本目录原地更新时，运行中的启动器占着旧 DLL —— 覆盖失败就把旧文件改名让位再写。
    /// </remarks>
    public async Task ApplyAsync(string zipPath, CancellationToken cancellationToken = default)
    {
        string root = PortableRoot
            ?? throw new InvalidOperationException("只有便携版能一键更新：找不到便携包根目录（app-<版本> 的上一级）。");

        Directory.CreateDirectory(BackupDirectory);

        // 保底：备份当前 version.ini —— 想退回时手动把它指回旧目录即可
        string versionIni = Path.Combine(root, "version.ini");
        if (File.Exists(versionIni))
        {
            File.Copy(
                versionIni,
                Path.Combine(BackupDirectory, $"version-{Sanitize(AppConfig.AppVersion)}.ini.bak"),
                overwrite: true);
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string? strip = null;
        List<string> leftovers = [];

        await Task.Run(() =>
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            strip = DetectWrapperPrefix(archive);

            var entries = new List<(ZipArchiveEntry Entry, string Target)>();
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            byte[]? versionContents = null;

            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string name = entry.FullName;
                if (strip is not null)
                {
                    if (!name.StartsWith(strip, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;   // 套壳目录自身的条目
                    }

                    name = name[strip.Length..];
                }

                if (string.IsNullOrEmpty(name) || name.EndsWith('/'))
                {
                    continue;
                }

                string target = PortableUpdatePolicy.GetArchiveTarget(root, name);
                if (!targets.Add(target))
                {
                    throw new InvalidDataException($"Update contains duplicate destination: {name}");
                }

                if (target.Equals(versionIni, StringComparison.OrdinalIgnoreCase))
                {
                    if (entry.Length > 65536)
                    {
                        throw new InvalidDataException("version.ini is too large.");
                    }

                    using var content = new MemoryStream();
                    using (Stream input = entry.Open())
                    {
                        input.CopyTo(content);
                    }

                    versionContents = content.ToArray();
                    continue;
                }

                entries.Add((entry, target));
            }

            if (versionContents is null)
            {
                throw new InvalidDataException("Update does not contain version.ini.");
            }

            string selectedExe = PortableUpdatePolicy.ReadVersionTarget(root, System.Text.Encoding.UTF8.GetString(versionContents).TrimStart('\uFEFF'));
            if (!entries.Any(item => item.Target.Equals(selectedExe, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("Update does not contain the executable selected by version.ini.");
            }

            // Validate the entire package before touching payloads; switch the version only after all writes succeed.
            foreach ((ZipArchiveEntry entry, string target) in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                PortableUpdatePolicy.GetArchiveTarget(root, Path.GetRelativePath(root, target));
                string? dir = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                ExtractOver(entry, target, stamp, leftovers);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(selectedExe))
            {
                throw new InvalidDataException("The selected update executable was not installed.");
            }

            PortableUpdatePolicy.GetArchiveTarget(root, "version.ini");
            PortableUpdatePolicy.WriteVersionAtomically(versionIni, versionContents);
        }, cancellationToken);

        _logger.LogInformation(
            "GitHub update applied: {Zip} -> {Root}（剥壳={Strip}，改名让位={Renamed}）",
            zipPath, root, strip is not null, leftovers.Count);
    }

    /// <summary>覆盖解压一个条目；旧文件被运行中的程序占用时，先改名让位再写（改已加载的 DLL/EXE 也允许）。</summary>
    private static void ExtractOver(ZipArchiveEntry entry, string target, string stamp, List<string>? leftovers)
    {
        try
        {
            entry.ExtractToFile(target, overwrite: true);
            return;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        string backup = target + ".old-" + stamp + "-" + Guid.NewGuid().ToString("N");
        File.Move(target, backup);
        try
        {
            entry.ExtractToFile(target, overwrite: true);
            leftovers?.Add(backup);
        }
        catch
        {
            File.Move(backup, target, overwrite: true);
            throw;
        }
    }

    /// <summary>
    /// 识别「多包一层」的 zip：根上没有 version.ini / 启动器 exe，所有条目都在同一个顶层目录里、
    /// 且那一层里有 version.ini。返回要剥掉的前缀（如 <c>HoYoShadeHub/</c>），正常布局返回 null。
    /// </summary>
    private static string? DetectWrapperPrefix(ZipArchive archive)
    {
        string? top = null;

        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string first = entry.FullName.Split('/')[0];

            if (first.Length == 0)
            {
                continue;
            }

            if (top is null)
            {
                top = first;
            }
            else if (!string.Equals(top, first, StringComparison.OrdinalIgnoreCase))
            {
                return null;   // 根上有多个条目 = 正常布局
            }
        }

        if (top is null
            || top.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || top.EndsWith(".ini", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string prefix = top + "/";
        return archive.Entries.Any(e => e.FullName.Equals(prefix + "version.ini", StringComparison.OrdinalIgnoreCase))
            ? prefix
            : null;
    }

    /// <summary>用便携包启动器重启（跟 UpdateWindow.Restart 一个套路）</summary>
    public static bool Restart()
    {
        try
        {
            string? launcher = AppConfig.HoYoShadeHubLauncherExecutePath;

            if (launcher is null || !File.Exists(launcher))
            {
                return false;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = launcher,
                WorkingDirectory = Path.GetDirectoryName(launcher),
                Arguments = LauncherCommandLine.BuildRestartArguments(),
                UseShellExecute = false,
            });

            Environment.Exit(0);
            return true;
        }
        catch (Exception ex)
        {
            AppConfig.GetLogger<GithubUpdateService>().LogWarning(ex, "Restart after GitHub update");
            return false;
        }
    }

    private static string NormalizeVersion(string? version)
        => (version ?? string.Empty).Trim().TrimStart('v', 'V');

    private static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]);
    }
}
