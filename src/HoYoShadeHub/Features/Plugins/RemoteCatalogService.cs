using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Networking;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.Modules;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>远端目录的默认地址（开源仓库里的 catalog/ 目录）。换仓库只改这里。</summary>
internal static class RemoteCatalogDefaults
{
    /// <summary>
    /// 我们这份 fork 的仓库地址前缀。
    /// ⚠ 仓库名定了之后改这一行即可（也可以在 AppConfig.CatalogBaseUrl / Setting 表里覆盖）。
    /// </summary>
    public const string BaseUrl = "https://raw.githubusercontent.com/thx114/HoYoShadeHub-DLSS5/main/catalog/";
}

/// <summary>一次拉取的结果</summary>
internal sealed record RemoteCatalogResult(bool Fetched, bool PluginsUpdated, bool OptiScalerUpdated, bool ModulesUpdated, string Message);

/// <summary>
/// 远端目录（插件 + OptiScaler + 模块）的拉取与缓存。
///
/// <para>
/// 2026-09-30 起目录**完全由 GitHub 提供**：启动器代码里没有任何内置条目，
/// 新增插件 / 模块 / OptiScaler 来源只改仓库里的 catalog/*.json，不重新发版。
/// 拉到的文件缓存在 <c>&lt;用户数据目录&gt;\.hysx\catalog\</c>，页面从缓存读（离线可用）。
/// </para>
///
/// <para>
/// **每天最多自动拉一次**（用户要求）：超过 24 小时才自动拉；设置页的按钮用 force 绕过节流。
/// 首跑还没有缓存时，用随启动器发布的 <c>catalog\*.json</c> 副本（仓库根 catalog/ 目录，
/// 构建时拷进输出）做种子，离线也能开出完整的插件 / 模块 / OptiScaler 列表。
/// </para>
/// </summary>
internal static class RemoteCatalogService
{
    private static readonly ILogger _logger = AppConfig.GetLogger<GlobalPluginPage>();

    private static readonly TimeSpan _interval = TimeSpan.FromHours(24);

    /// <summary>&lt;用户数据目录&gt;\.hysx\catalog</summary>
    public static string CacheDirectory
        => string.IsNullOrWhiteSpace(AppConfig.UserDataFolder)
            ? string.Empty
            : Path.Combine(AppConfig.UserDataFolder, ".hysx", "catalog");

    public static string PluginsCachePath => CacheDirectory.Length == 0 ? string.Empty : Path.Combine(CacheDirectory, "plugins.json");

    public static string OptiScalerCachePath => CacheDirectory.Length == 0 ? string.Empty : Path.Combine(CacheDirectory, "optiscaler.json");

    /// <summary>模块目录（catalog/modules.json）的缓存</summary>
    public static string ModulesCachePath => CacheDirectory.Length == 0 ? string.Empty : Path.Combine(CacheDirectory, "modules.json");

    /// <summary>同步读插件目录缓存（addon 文件名匹配等同步场景用；没有缓存返回 null）</summary>
    public static ExtensionCatalogDocument? LoadCachedPlugins()
        => ExtensionCatalogService.LoadFile(PluginsCachePath);

    /// <summary>随启动器发布的 catalog 副本目录（&lt;exe 目录&gt;\catalog\）；没有返回空串</summary>
    private static string BundleCatalogDirectory
    {
        get
        {
            try
            {
                string dir = Path.Combine(AppContext.BaseDirectory, "catalog");
                return Directory.Exists(dir) ? dir : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>
    /// 缓存文件缺失时，用随包 catalog 副本做种子（只补缺失，不覆盖已有缓存）。
    /// 这样首跑离线也能开出完整列表；联网后 24 小时拉取会自然覆盖成最新。
    /// </summary>
    public static void SeedFromBundle()
    {
        try
        {
            string bundle = BundleCatalogDirectory;
            if (bundle.Length == 0 || CacheDirectory.Length == 0)
            {
                return;
            }

            Directory.CreateDirectory(CacheDirectory);

            foreach ((string fileName, string cachePath) in new[]
            {
                ("plugins.json", PluginsCachePath),
                ("optiscaler.json", OptiScalerCachePath),
                ("modules.json", ModulesCachePath),
            })
            {
                if (File.Exists(cachePath))
                {
                    continue;
                }

                string source = Path.Combine(bundle, fileName);
                if (File.Exists(source))
                {
                    File.Copy(source, cachePath);
                    _logger.LogInformation("Seeded catalog {File} from bundle copy", fileName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Seed catalog from bundle failed");
        }
    }

    /// <summary>离上次成功拉取超过 24 小时（或者从没拉过）</summary>
    public static bool IsRefreshDue => DateTimeOffset.UtcNow - AppConfig.LastCatalogFetchUtc > _interval;

    public static string BaseUrl
    {
        get
        {
            string url = AppConfig.CatalogBaseUrl;
            return string.IsNullOrWhiteSpace(url) ? RemoteCatalogDefaults.BaseUrl : url;
        }
    }

    /// <summary>到点才拉（页面加载时用）。</summary>
    public static async Task<RemoteCatalogResult> RefreshIfDueAsync(CancellationToken cancellationToken = default)
        => await RefreshAsync(force: false, cancellationToken);

    public static async Task<RemoteCatalogResult> RefreshAsync(bool force, CancellationToken cancellationToken = default)
    {
        // 先把随包种子落到缓存（首跑离线也能用），再把缓存里的模块目录应用上
        //（打开页面就能用 —— 不管这次拉不拉）
        SeedFromBundle();
        ModuleCatalogFile.Apply(ModulesCachePath);

        if (!force && !IsRefreshDue)
        {
            return new RemoteCatalogResult(false, false, false, false, "还没到 24 小时，先不拉。");
        }

        if (CacheDirectory.Length == 0)
        {
            return new RemoteCatalogResult(false, false, false, false, "还没读到用户数据目录。");
        }

        Directory.CreateDirectory(CacheDirectory);

        using HttpClient client = HysxHttp.CreateClient(timeout: TimeSpan.FromSeconds(30));

        bool plugins = await TryDownloadAsync(client, "plugins.json", PluginsCachePath, cancellationToken);
        bool optiScaler = await TryDownloadAsync(client, "optiscaler.json", OptiScalerCachePath, cancellationToken);
        bool modules = await TryDownloadAsync(client, "modules.json", ModulesCachePath, cancellationToken);

        // 一个都没拿到就不盖时间戳，下次还会再试
        if (!plugins && !optiScaler && !modules)
        {
            _logger.LogWarning("Remote catalog fetch failed (base = {Base})", BaseUrl);
            return new RemoteCatalogResult(false, false, false, false, "拉取失败（网络不通或仓库里还没有 catalog/ 文件）。");
        }

        ModuleCatalogFile.Apply(ModulesCachePath);

        AppConfig.LastCatalogFetchUtc = DateTimeOffset.UtcNow;
        _logger.LogInformation("Remote catalog fetched: plugins={Plugins}, optiscaler={Opti}, modules={Modules}", plugins, optiScaler, modules);

        return new RemoteCatalogResult(true, plugins, optiScaler, modules,
            $"目录已更新：插件 {(plugins ? "✓" : "—")}，OptiScaler {(optiScaler ? "✓" : "—")}，模块 {(modules ? "✓" : "—")}。");
    }

    /// <summary>下载一个文件，成功才覆盖（先写临时文件再搬过去，避免半截文件）。</summary>
    private static async Task<bool> TryDownloadAsync(HttpClient client, string fileName, string targetPath, CancellationToken cancellationToken)
    {
        try
        {
            string url = BaseUrl.TrimEnd('/') + "/" + fileName;
            string json = await client.GetStringAsync(HysxHttp.Apply(url), cancellationToken);

            if (string.IsNullOrWhiteSpace(json) || json.Length < 16)
            {
                return false;
            }

            string temp = targetPath + ".tmp";
            await File.WriteAllTextAsync(temp, json, cancellationToken);
            File.Move(temp, targetPath, overwrite: true);
            return true;
        }
        catch (HttpRequestException ex)
        {
            // 404 = 仓库里还没放这个文件；别的 = 网络问题。都按「没拉到」处理
            _logger.LogDebug(ex, "Remote catalog {File} not fetched", fileName);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Remote catalog {File} failed", fileName);
            return false;
        }
    }
}
