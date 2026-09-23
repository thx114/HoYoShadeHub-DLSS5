using System.Diagnostics;
using System.IO.Compression;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Dlls;

/// <summary>插件目录里的一个 dll</summary>
public sealed record InstalledDll(string FileName, long Size, string? Version)
{
    public string SizeText => Size >= 1024 * 1024
        ? $"{Size / 1024d / 1024d:F1} MB"
        : $"{Size / 1024d:F0} KB";
}

/// <summary>装一个 dll 包的结果</summary>
public sealed record DllInstallResult(
    bool Ok,
    string? Error,
    IReadOnlyList<string> Installed,
    IReadOnlyList<string> Overwritten,
    string? Version);

/// <summary>
/// dll 组件的下载 / 安装 / 盘点。
///
/// 装的位置就是插件的家：<c>&lt;HoYoShade&gt;\reshade-shaders\Addons</c> ——
/// 因为 ReShade 只从那儿加载 addon，而 DLSS5 那几个运行时（nvngx_dlssnr.dll / sl.*.dll）
/// 必须和 addon 躺在一起才被找到。
/// </summary>
public static class DllInstaller
{
    /// <summary>扫插件目录里现有的 dll（带 PE 版本号）</summary>
    public static List<InstalledDll> Scan(string? addonsDirectory)
    {
        var result = new List<InstalledDll>();

        if (string.IsNullOrWhiteSpace(addonsDirectory) || !Directory.Exists(addonsDirectory))
        {
            return result;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(addonsDirectory, "*.dll", SearchOption.TopDirectoryOnly))
            {
                long size = 0;
                string? version = null;

                try
                {
                    size = new FileInfo(file).Length;
                    FileVersionInfo info = FileVersionInfo.GetVersionInfo(file);
                    version = !string.IsNullOrWhiteSpace(info.FileVersion) ? info.FileVersion : info.ProductVersion;
                }
                catch
                {
                    // 读不了版本也无所谓，名字和大小还是能显示
                }

                result.Add(new InstalledDll(Path.GetFileName(file), size, version));
            }
        }
        catch
        {
            // ignore
        }

        return [.. result.OrderBy(d => d.FileName, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>某类 dll 现在装的是哪个版本（按 <see cref="DllFamily.FilePattern"/> 找）</summary>
    public static string? GetInstalledVersion(IEnumerable<InstalledDll> installed, DllFamily family)
    {
        InstalledDll? hit = installed.FirstOrDefault(d => Matches(d.FileName, family));

        if (hit is null)
        {
            return null;
        }

        // sl.*.dll 一大堆，取版本最高的那个当代表
        if (family.Id is "streamline")
        {
            hit = installed
                .Where(d => Matches(d.FileName, family) && !string.IsNullOrWhiteSpace(d.Version))
                .OrderByDescending(d => d.Version, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault() ?? hit;
        }

        return hit.Version;
    }

    public static bool Matches(string fileName, DllFamily family) => family.Id switch
    {
        "streamline" => fileName.StartsWith("sl.", StringComparison.OrdinalIgnoreCase)
                        && fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase),
        _ => string.Equals(fileName, family.FilePattern, StringComparison.OrdinalIgnoreCase),
    };

    /// <summary>下载一个组件（zip）并把里面的 dll 铺到插件目录</summary>
    public static async Task<DllInstallResult> InstallAsync(
        string addonsDirectory,
        DllComponent component,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        DownloadPauseToken? pauseToken = null)
    {
        string? tempZip = null;

        try
        {
            if (string.IsNullOrWhiteSpace(addonsDirectory))
            {
                return new DllInstallResult(false, "还没定位到 HoYoShade 的 addons 目录。", [], [], component.Version);
            }

            Directory.CreateDirectory(addonsDirectory);

            tempZip = Path.Combine(Path.GetTempPath(), $"hysx-dll-{Guid.NewGuid():N}.zip");

            var downloader = new DownloadService();
            await downloader.DownloadToFileAsync(component.Url, tempZip, expectedSha256: null, progress, cancellationToken, pauseToken);

            var installed = new List<string>();
            var overwritten = new List<string>();

            using (ZipArchive archive = ZipFile.OpenRead(tempZip))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    string name = Path.GetFileName(entry.FullName);
                    if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string target = Path.Combine(addonsDirectory, name);
                    if (File.Exists(target))
                    {
                        overwritten.Add(name);
                    }

                    entry.ExtractToFile(target, overwrite: true);
                    installed.Add(name);
                }
            }

            if (installed.Count == 0)
            {
                return new DllInstallResult(false, "这个包里没有 .dll。", [], [], component.Version);
            }

            return new DllInstallResult(true, null, installed, overwritten, component.Version);
        }
        catch (Exception ex)
        {
            return new DllInstallResult(false, ex.Message, [], [], component.Version);
        }
        finally
        {
            if (tempZip is not null)
            {
                try
                {
                    File.Delete(tempZip);
                }
                catch
                {
                    // ignore
                }
            }
        }
    }
}
