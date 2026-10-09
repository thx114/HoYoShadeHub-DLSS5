using HoYoShadeHub.Extensions.Conditions;
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

    /// <summary>条件表（catalog/conditions.json）的缓存：插件判定 / dll 需求 / OptiScaler 依赖清单</summary>
    public static string ConditionsCachePath => CacheDirectory.Length == 0 ? string.Empty : Path.Combine(CacheDirectory, "conditions.json");

    /// <summary>把缓存里的条件表喂给 Extensions 层的 <see cref="Conditions.AddonConditions"/>（同步、幂等）</summary>
    public static void ApplyConditions()
    {
        try
        {
            AddonConditions.LoadJson(
                !string.IsNullOrWhiteSpace(ConditionsCachePath) && File.Exists(ConditionsCachePath)
                    ? File.ReadAllText(ConditionsCachePath)
                    : null);
        }
        catch
        {
            // 条件表坏了/读不到 = 全部用内置默认，别影响页面
        }
    }

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
                ("conditions.json", ConditionsCachePath),
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
        // 先把随包种子落到缓存（首跑离线也能用），再把缓存里的模块目录 / 条件表应用上
        //（打开页面就能用 —— 不管这次拉不拉）
        SeedFromBundle();
        ModuleCatalogFile.Apply(ModulesCachePath);
        ApplyConditions();

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
        bool conditions = await TryDownloadAsync(client, "conditions.json", ConditionsCachePath, cancellationToken);

        // 一个都没拿到就不盖时间戳，下次还会再试
        if (!plugins && !optiScaler && !modules && !conditions)
        {
            _logger.LogWarning("Remote catalog fetch failed (base = {Base})", BaseUrl);
            return new RemoteCatalogResult(false, false, false, false, "拉取失败（网络不通或仓库里还没有 catalog/ 文件）。");
        }

        ModuleCatalogFile.Apply(ModulesCachePath);
        ApplyConditions();

        AppConfig.LastCatalogFetchUtc = DateTimeOffset.UtcNow;
        _logger.LogInformation("Remote catalog fetched: plugins={Plugins}, optiscaler={Opti}, modules={Modules}", plugins, optiScaler, modules);

        return new RemoteCatalogResult(true, plugins, optiScaler, modules,
            $"目录已更新：插件 {(plugins ? "✓" : "—")}，OptiScaler {(optiScaler ? "✓" : "—")}，模块 {(modules ? "✓" : "—")}。");
    }

    /// <summary>下载一个文件，成功才覆盖（先写临时文件再搬过去，避免半截文件）。</summary>
    private static async Task<bool> TryDownloadAsync(HttpClient client, string fileName, string targetPath, CancellationToken cancellationToken)
    {
        string url = BaseUrl.TrimEnd('/') + "/" + fileName;

        // 这段时间内已知「直连和公共代理都不通」：别对每个文件再把三档重试一遍
        // （4 个文件 × 3 档 × 30 秒超时太慢）。到点自然重试。
        if (DateTimeOffset.UtcNow < _networkDownUntil && _lastGoodProxy is null)
        {
            return false;
        }

        bool networkFailed = false;

        // 第一档 = 已经试通过的那档（没有就用「用户设置里那个代理 / 直连」），
        // 之后依次兜公共代理 —— raw.githubusercontent.com 在国内常不通，以前没这层兜底时
        // 远端目录永远拉不到，只能等随包种子（= 发一次版），看起来就像"加模块必须改代码"。
        foreach (string? proxy in Strategies())
        {
            try
            {
                string json = await client.GetStringAsync(HysxHttp.Apply(url, proxy), cancellationToken);

                if (string.IsNullOrWhiteSpace(json) || json.Length < 16)
                {
                    continue;
                }

                string temp = targetPath + ".tmp";
                await File.WriteAllTextAsync(temp, json, cancellationToken);
                File.Move(temp, targetPath, overwrite: true);

                if (proxy is not null && !string.Equals(proxy, _lastGoodProxy, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Remote catalog {File} fetched via public proxy {Proxy}", fileName, proxy);
                }

                _lastGoodProxy = proxy;
                return true;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                // 404 = 仓库里还没放这个文件（有些目录是可选的）。这不是网络问题，换代理也一样没有。
                _logger.LogDebug(ex, "Remote catalog {File} not found upstream", fileName);
                return false;
            }
            catch (HttpRequestException ex)
            {
                networkFailed = true;
                _logger.LogDebug(ex, "Remote catalog {File} not fetched (proxy={Proxy})", fileName, proxy ?? "direct");
            }
            catch (Exception ex)
            {
                networkFailed = true;
                _logger.LogWarning(ex, "Remote catalog {File} failed (proxy={Proxy})", fileName, proxy ?? "direct");
            }
        }

        if (networkFailed)
        {
            _networkDownUntil = DateTimeOffset.UtcNow.AddMinutes(2);
        }

        return false;
    }

    /// <summary>直连不通时兜的公共代理（和组件清单同一套：raw 在国内基本不通）</summary>
    private static readonly string[] _publicProxies = ["https://gh-proxy.org", "https://ghfast.top"];

    /// <summary>上一次成功用的那档（null = 直连 / 用户自己的代理），下次先试它</summary>
    private static string? _lastGoodProxy;

    /// <summary>到这个时刻之前不再重试（区分「仓库没文件」和「网络根本不通」）</summary>
    private static DateTimeOffset _networkDownUntil = DateTimeOffset.MinValue;

    /// <summary>本次要试的档位：上次成功的那档优先，然后剩下的</summary>
    private static System.Collections.Generic.IEnumerable<string?> Strategies()
    {
        yield return _lastGoodProxy;

        foreach (string proxy in _publicProxies)
        {
            if (!string.Equals(proxy, _lastGoodProxy, StringComparison.OrdinalIgnoreCase))
            {
                yield return proxy;
            }
        }
    }
}
