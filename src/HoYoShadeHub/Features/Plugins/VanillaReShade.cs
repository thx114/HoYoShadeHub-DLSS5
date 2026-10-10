using HoYoShadeHub.Core.Networking;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Features.GameLauncher;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 原版 ReShade（官方「可加载插件」版）的下载与安装。
///
/// <para>
/// 官方只发联网安装器（<c>ReShade_Setup_*_Addon.exe</c>），但那个 exe 是「PE + 尾部追加 zip」的
/// 自解压结构（crosire/reshade 的 setup/MainWindow.xaml.cs：Look for archive at the end of this
/// executable）——把尾部的 zip 切出来就能直接拿到 <c>ReShade64.dll</c>，不用真跑安装器。
/// </para>
/// </summary>
internal static class VanillaReShade
{
    /// <summary>只给 GetLogger 当类型参数用（静态类不能作泛型实参）</summary>
    private sealed class Marker;

    private static readonly ILogger Logger = AppConfig.GetLogger<Marker>();

    /// <summary>官方安装器版本（下载 URL 与安装标记共用）</summary>
    public const string SetupVersion = "6.8.0";

    /// <summary>官方 Addon 版安装器（Addon 版 = 带完整插件支持的原版 ReShade）</summary>
    public const string SetupUrl = "https://reshade.me/downloads/ReShade_Setup_" + SetupVersion + "_Addon.exe";

    private static string CacheDirectory => Path.Combine(AppConfig.CacheFolder, "vanilla-reshade");

    /// <summary>解出来的原版 ReShade64.dll 全路径</summary>
    public static string CachedDllPath => Path.Combine(CacheDirectory, "ReShade64.dll");

    /// <summary>原版 dll 是不是已经下载过了（下过一次就不重复下）</summary>
    public static bool IsDownloaded => File.Exists(CachedDllPath);

    /// <summary>安装标记：记下这份 dxgi.dll 是本启动器装的、哪个版本、哈希多少 —— 移除时只删自己那份</summary>
    public const string MarkerFileName = ".hysx-vanilla-reshade.json";

    private const string MarkerVersionKey = "version";
    private const string MarkerHashKey = "sha256";
    private const string MarkerInstalledKey = "installed";

    /// <summary>
    /// 原版 ReShade 该装到哪个目录。鸣潮这种「注册 exe 是启动器壳」的游戏必须装进
    /// <b>本体进程目录</b>（<c>&lt;根&gt;\Wuthering Waves Game\Binaries\Win64</c>）：
    /// 壳进程旁边的 dxgi.dll 游戏本体根本不会加载。认不出真身目录就退回注册目录。
    /// </summary>
    public static string? ResolveTargetDirectory(GameEntry? entry, string? registeredDirectory)
        => entry is not null && ShadeBlacklistBypass.RealGameDirectory(entry) is { } real
            ? real
            : (string.IsNullOrWhiteSpace(registeredDirectory) ? null : registeredDirectory);

    /// <summary>这个目录里是不是本启动器装的原版 ReShade（认安装标记）</summary>
    public static bool IsInstalled(string? directory) => ReadMarker(directory) is not null;

    /// <summary>目录里有没有 dxgi.dll（不区分谁装的）</summary>
    public static bool HasProxyDll(string? directory)
        => !string.IsNullOrWhiteSpace(directory) && File.Exists(Path.Combine(directory, "dxgi.dll"));

    /// <summary>
    /// 卸掉本启动器装的原版 ReShade。只删「安装标记里的哈希与当前 dxgi.dll 一致」的那份 ——
    /// 别人装的、或用户后来换过的 dxgi.dll 一律不碰。ReShade.ini 保留（用户可能已经配过）。
    /// </summary>
    public static bool RemoveFromGame(string? directory, out string message)
    {
        message = string.Empty;

        if (string.IsNullOrWhiteSpace(directory))
        {
            message = "没有可用的游戏目录。";
            return false;
        }

        InstallMarker? marker = ReadMarker(directory);

        if (marker is null)
        {
            message = "这个目录没有本启动器的安装记录，未删除任何文件（避免删掉别人装的 dxgi.dll）。";
            return false;
        }

        string markerPath = Path.Combine(directory, MarkerFileName);
        string dllPath = Path.Combine(directory, "dxgi.dll");

        try
        {
            if (File.Exists(dllPath))
            {
                if (!string.Equals(TryHash(dllPath), marker.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(markerPath);
                    message = "dxgi.dll 已被替换或改动，没有删除（安装记录已清除）。";
                    return false;
                }

                File.Delete(dllPath);
            }

            File.Delete(markerPath);
            Logger.LogInformation("原版 ReShade 已移除：{Dir}", directory);
            message = "已移除原版 ReShade（dxgi.dll）。ReShade.ini 保留。";
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "移除原版 ReShade 失败：{Dir}", directory);
            message = "移除失败：" + ex.Message;
            return false;
        }
    }

    private sealed record InstallMarker(string Version, string Sha256);

    private static InstallMarker? ReadMarker(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(directory, MarkerFileName);

            if (!File.Exists(path))
            {
                return null;
            }

            string? version = null;
            string? sha = null;

            foreach (string line in File.ReadAllLines(path))
            {
                int split = line.IndexOf('=');

                if (split <= 0)
                {
                    continue;
                }

                string key = line[..split].Trim();
                string value = line[(split + 1)..].Trim();

                if (key.Equals(MarkerVersionKey, StringComparison.OrdinalIgnoreCase))
                {
                    version = value;
                }
                else if (key.Equals(MarkerHashKey, StringComparison.OrdinalIgnoreCase))
                {
                    sha = value;
                }
            }

            return string.IsNullOrWhiteSpace(sha) ? null : new InstallMarker(version ?? "?", sha);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteMarker(string directory, string installedDllPath)
    {
        try
        {
            string? hash = TryHash(installedDllPath);

            if (string.IsNullOrWhiteSpace(hash))
            {
                return;
            }

            string body =
                $"{MarkerVersionKey}={SetupVersion}{Environment.NewLine}" +
                $"{MarkerHashKey}={hash}{Environment.NewLine}" +
                $"{MarkerInstalledKey}={DateTime.UtcNow:O}{Environment.NewLine}";

            File.WriteAllText(Path.Combine(directory, MarkerFileName), body);
        }
        catch (Exception ex)
        {
            // 标记写不上不影响使用，只是以后不能自动移除
            Logger.LogWarning(ex, "写原版 ReShade 安装标记失败：{Dir}", directory);
        }
    }

    private static string? TryHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>下载并解出 ReShade64.dll；有缓存直接返回。失败抛异常，由调用方弹给用户看。</summary>
    public static async Task<string> EnsureDllAsync(IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (File.Exists(CachedDllPath))
        {
            return CachedDllPath;
        }

        Directory.CreateDirectory(CacheDirectory);
        string setupPath = Path.Combine(CacheDirectory, "ReShade_Setup_Addon.exe");

        progress?.Report("正在下载原版 ReShade 安装器…");

        using (var client = new HttpClient(DohService.CreateSocketsHttpHandler())
        {
            DefaultVersionPolicy = System.Net.Http.HttpVersionPolicy.RequestVersionOrHigher,
        })
        {
            client.DefaultRequestHeaders.UserAgent.ParseAdd("HoYoShadeHub");

            using var response = await client.GetAsync(SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(setupPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await input.CopyToAsync(output, cancellationToken);
        }

        progress?.Report("正在从安装器里解出 ReShade64.dll…");
        ExtractAddonDll(setupPath, CachedDllPath);

        try
        {
            File.Delete(setupPath);
        }
        catch
        {
            // 安装器本体留着不碍事
        }

        Logger.LogInformation("原版 ReShade 已下载：{Path}", CachedDllPath);
        return CachedDllPath;
    }

    /// <summary>
    /// 装到游戏目录：ReShade64.dll 以 <c>dxgi.dll</c> 的名字铺进去（DX11 / DX12 游戏都会被它拦到），
    /// 没有 ReShade.ini 就补一份最小的，并写安装标记（供 <see cref="RemoveFromGame"/> 只删自己那份）。
    /// <para>
    /// 目录必须传 <b>游戏本体进程所在目录</b> —— 鸣潮这类「注册 exe 是启动器壳」的游戏要用
    /// <see cref="ResolveTargetDirectory"/> 解析真身目录，否则 dxgi.dll 不会被游戏加载。
    /// </para>
    /// 返回装好的游戏目录。
    /// </summary>
    public static async Task<string> InstallToGameAsync(string gameDirectory, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        string dll = await EnsureDllAsync(progress, cancellationToken);

        Directory.CreateDirectory(gameDirectory);

        string target = Path.Combine(gameDirectory, "dxgi.dll");
        string staged = target + ".hysx-new";
        File.Copy(dll, staged, overwrite: true);
        File.Move(staged, target, overwrite: true);

        string ini = Path.Combine(gameDirectory, "ReShade.ini");

        if (!File.Exists(ini))
        {
            ReShadeProfile.CreateNew(ini).Save();
        }

        WriteMarker(gameDirectory, target);

        Logger.LogInformation("原版 ReShade 已装进游戏目录：{Dir}", gameDirectory);
        return gameDirectory;
    }

    /// <summary>从自解压安装器里把 ReShade64.dll 抠出来（见类注释的结构说明）</summary>
    private static void ExtractAddonDll(string setupPath, string destination)
    {
        byte[] bytes = File.ReadAllBytes(setupPath);

        // EOCD（PK\x05\x06）一定在文件最后 64KB+22 字节之内，从后往前扫
        int minPos = Math.Max(0, bytes.Length - (65535 + 22));
        int eocd = -1;

        for (int i = bytes.Length - 22; i >= minPos; i--)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x05 && bytes[i + 3] == 0x06)
            {
                eocd = i;
                break;
            }
        }

        if (eocd < 0)
        {
            throw new InvalidDataException("安装器里没有找到 zip 结尾 —— 不是预期的自解压结构，可能官网改了发布格式");
        }

        int cdSize = BitConverter.ToInt32(bytes, eocd + 12);
        int cdOffset = BitConverter.ToInt32(bytes, eocd + 16);

        // 中央目录紧贴在 EOCD 前面：zipStart + cdOffset + cdSize == eocdPos
        int zipStart = eocd - cdOffset - cdSize;

        if (zipStart < 0)
        {
            throw new InvalidDataException("安装器里 zip 的偏移算不出来");
        }

        using var zipStream = new MemoryStream(bytes, zipStart, bytes.Length - zipStart, writable: false);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);

        ZipArchiveEntry? entry = archive.GetEntry("ReShade64.dll")
                                 ?? throw new InvalidDataException("安装器里没有 ReShade64.dll，可能官网改了发布格式");

        string staged = destination + ".hysx-new";

        using (var input = entry.Open())
        using (var output = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            input.CopyTo(output);
        }

        File.Move(staged, destination, overwrite: true);
    }
}
