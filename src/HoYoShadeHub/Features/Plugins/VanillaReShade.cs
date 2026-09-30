using HoYoShadeHub.Core.Networking;
using HoYoShadeHub.Extensions.ReShade;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
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

    /// <summary>官方 Addon 版安装器（Addon 版 = 带完整插件支持的原版 ReShade）</summary>
    public const string SetupUrl = "https://reshade.me/downloads/ReShade_Setup_6.8.0_Addon.exe";

    private static string CacheDirectory => Path.Combine(AppConfig.CacheFolder, "vanilla-reshade");

    /// <summary>解出来的原版 ReShade64.dll 全路径</summary>
    public static string CachedDllPath => Path.Combine(CacheDirectory, "ReShade64.dll");

    /// <summary>原版 dll 是不是已经下载过了（下过一次就不重复下）</summary>
    public static bool IsDownloaded => File.Exists(CachedDllPath);

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
    /// 没有 ReShade.ini 就补一份最小的。返回装好的游戏目录。
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
