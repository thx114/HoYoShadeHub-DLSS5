using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Helpers;

/// <summary>
/// Manages cloud proxy selection and fallback logic for downloading
/// </summary>
public class CloudProxyManager
{
    // Cloudflare proxy URLs
    private static readonly string[] CloudflareProxies = new[]
    {
        "https://hoyoshadehub-glasses.pages.dev/",
        "https://cdn.autumn.recipe.2dcd.cf.storage.hub.hoyosha.de",
        "https://cdn.delicate.meadow.be18.cf.storage.hub.hoyosha.de",
        "https://cdn.weathered.wave.q2c3.cf.storage.hub.hoyosha.de"
    };

    // Tencent Cloud proxy URLs
    private static readonly string[] TencentCloudProxies = new[]
    {
        "https://cdn.green.sea.12ae.tx.storage.hub.hoyosha.de",
        "https://cdn.jolly.snowflake.cd46.tx.storage.hub.hoyosha.de",
        "https://cdn.bold.wood.c623.tx.storage.hub.hoyosha.de"
    };

    // Alibaba Cloud proxy URLs
    private static readonly string[] AlibabaCloudProxies = new[]
    {
        "https://cdn.bold.wood.c623.ali.storage.hub.hoyosha.de",
        "https://cdn.jolly.snowflake.cd46.ali.storage.hub.hoyosha.de",
        "https://cdn.steep.pond.0c55.ali.storage.hub.hoyosha.de"
    };

    // 公共 GitHub 加速代理（用户提供）。用法是 <前缀>/<原始 github 链接>。
    // 这些是第三方服务，稳定性不如自家 CDN，所以各自单独一项、让用户按需选。
    private static readonly string[] GhProxyOrgProxies = ["https://gh-proxy.org"];
    private static readonly string[] GhFastTopProxies = ["https://ghfast.top"];
    private static readonly string[] GhProxyComProxies = ["https://gh-proxy.com"];
    private static readonly string[] GhAkamsCnProxies = ["https://github.akams.cn"];

    private static readonly Random _random = new Random();

    /// <summary>
    /// 最近失败过的下载服务器（序号  失败时间）。
    ///
    /// <para>
    /// 自动选择时跳过冷却中的服务器：用户装不上会反复点安装，而每次都要把候选服务器全试一遍，
    /// 线上日志里一次会话就出现了 76 个必然失败的请求（19 次  4 个服务器）。失败过的 5 分钟内不再试，
    /// 既少打人家服务，也让重试更快给出结果。
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<int, DateTime> _recentFailures = new();

    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);

    /// <summary>记下这个服务器刚刚失败了（自动选择的候选里会把它跳过一段时间）</summary>
    public static void MarkServerFailed(int serverIndex)
    {
        if (serverIndex >= 0)
        {
            _recentFailures[serverIndex] = DateTime.UtcNow;
        }
    }

    /// <summary>这个服务器还在失败冷却期里吗</summary>
    public static bool IsServerInCooldown(int serverIndex)
    {
        if (!_recentFailures.TryGetValue(serverIndex, out DateTime when))
        {
            return false;
        }

        if (DateTime.UtcNow - when < FailureCooldown)
        {
            return true;
        }

        _recentFailures.TryRemove(serverIndex, out _);
        return false;
    }

    /// <summary>把冷却中的服务器去掉；全都在冷却里就原样返回（总得试一次）</summary>
    private static int[] FilterCooldown(List<int> sequence)
    {
        int[] all = sequence.ToArray();
        int[] fresh = all.Where(i => !IsServerInCooldown(i)).ToArray();
        return fresh.Length > 0 ? fresh : all;
    }

    /// <summary>
    /// Get proxy URL prefix for the specified download server
    /// </summary>
    /// <param name="serverIndex">
    /// 0=GitHub 直连, 1=Cloudflare, 2=腾讯云, 3=阿里云,
    /// 4=gh-proxy.org, 5=ghfast.top, 6=gh-proxy.com, 7=github.akams.cn
    /// </param>
    /// <returns>代理前缀；GitHub 直连时为 null</returns>
    public static string? GetProxyUrl(int serverIndex)
    {
        return serverIndex switch
        {
            0 => null, // GitHub (Direct)
            1 => GetRandomProxy(CloudflareProxies),
            2 => GetRandomProxy(TencentCloudProxies),
            3 => GetRandomProxy(AlibabaCloudProxies),
            4 => GetRandomProxy(GhProxyOrgProxies),
            5 => GetRandomProxy(GhFastTopProxies),
            6 => GetRandomProxy(GhProxyComProxies),
            7 => GetRandomProxy(GhAkamsCnProxies),
            _ => null
        };
    }

    /// <summary>
    /// Apply proxy URL to the original URL
    /// </summary>
    /// <param name="originalUrl">Original URL to download</param>
    /// <param name="proxyUrl">Proxy URL prefix</param>
    /// <returns>Proxied URL or original URL if proxy is null</returns>
    public static string ApplyProxy(string originalUrl, string? proxyUrl)
    {
        if (string.IsNullOrWhiteSpace(proxyUrl))
        {
            return originalUrl;
        }

        return $"{proxyUrl}/{originalUrl}";
    }

    /// <summary>
    /// Get all proxy URLs for a specific cloud provider
    /// </summary>
    /// <param name="serverIndex">1=Cloudflare, 2=腾讯云, 3=阿里云, 4~7=公共 gh 代理</param>
    /// <returns>该服务器的候选代理前缀</returns>
    public static string[] GetAllProxiesForServer(int serverIndex)
    {
        return serverIndex switch
        {
            1 => CloudflareProxies,
            2 => TencentCloudProxies,
            3 => AlibabaCloudProxies,
            4 => GhProxyOrgProxies,
            5 => GhFastTopProxies,
            6 => GhProxyComProxies,
            7 => GhAkamsCnProxies,
            _ => Array.Empty<string>()
        };
    }

    /// <summary>
    /// Get the fallback sequence for Auto Select mode
    /// </summary>
    public static int[] GetAutoSelectFallbackSequence(bool isLauncherUpdate)
    {
        if (isLauncherUpdate)
        {
            // Launcher Update: Cloudflare -> Tencent -> Alibaba
            return new[] { 1, 2, 3 };
        }
        else
        {
            // HoYoShade / ReShade：公共 gh 代理 -> 腾讯云 -> 随机(Cloudflare, 阿里云) -> GitHub 直连兜底。
            // 三处都是冲着「别滥用」改的：
            //   1) GitHub 直连放最后  国内基本连不上，排第一等于每次必然多一个失败请求；
            //   2) 新加的 gh-proxy.org / ghfast.top 排最前（实测能转发 github，最可能成功）；
            //   3) 刚失败过的服务器这次直接跳过（见 FilterCooldown）。
            var sequence = new List<int> { 4, 5, 2 };
            if (_random.Next(2) == 0)
            {
                sequence.Add(1);
                sequence.Add(3);
            }
            else
            {
                sequence.Add(3);
                sequence.Add(1);
            }
            sequence.Add(0);
            return FilterCooldown(sequence);
        }
    }

    /// <summary>
    /// Try downloading with fallback to other proxies if the selected one fails
    /// </summary>
    /// <param name="originalUrl">Original URL to download</param>
    /// <param name="serverIndex">Server index from DownloadServers list</param>
    /// <param name="httpClient">HttpClient to use for requests</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>HttpResponseMessage from successful proxy, or throws if all fail</returns>
    public static async Task<HttpResponseMessage> DownloadWithFallbackAsync(
        string originalUrl,
        int serverIndex,
        HttpClient httpClient,
        CancellationToken cancellationToken = default)
    {
        // For GitHub direct, no fallback needed
        if (serverIndex == 0)
        {
            return await httpClient.GetAsync(originalUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        var proxies = GetAllProxiesForServer(serverIndex);
        if (proxies.Length == 0)
        {
            // Fallback to direct if no proxies
            return await httpClient.GetAsync(originalUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }

        // Shuffle proxies to try them in random order
        var shuffledProxies = proxies.OrderBy(_ => _random.Next()).ToArray();
        Exception? lastException = null;

        foreach (var proxy in shuffledProxies)
        {
            try
            {
                var proxiedUrl = ApplyProxy(originalUrl, proxy);
                var response = await httpClient.GetAsync(proxiedUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                
                // If successful, return immediately
                if (response.IsSuccessStatusCode)
                {
                    return response;
                }

                // If not successful, dispose and try next
                response.Dispose();
            }
            catch (Exception ex)
            {
                lastException = ex;
                // Continue to next proxy
            }
        }

        // All proxies failed, throw the last exception
        throw lastException ?? new HttpRequestException($"All proxy servers failed for URL: {originalUrl}");
    }

    /// <summary>
    /// Ping the download server to measure latency
    /// </summary>
    /// <param name="serverIndex">Server index</param>
    /// <param name="httpClient">HttpClient</param>
    /// <returns>Latency in milliseconds, or -1 if failed</returns>
    public static async Task<long> PingServerAsync(int serverIndex, HttpClient httpClient)
    {
        string pingUrl;
        if (serverIndex == 0) // GitHub Direct
        {
            pingUrl = "https://github.com/";
        }
        else
        {
            var proxies = GetAllProxiesForServer(serverIndex);
            if (proxies.Length == 0) return -1;
            // Use the first proxy to check latency, with /success.html/ to avoid 403 Forbidden
            string proxy = proxies[0].TrimEnd('/');
            pingUrl = $"{proxy}/success.html/";
        }

        try
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            // Send HEAD request to avoid downloading the whole page if possible, 
            // but GET is safer for just a quick success.html
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var request = new HttpRequestMessage(HttpMethod.Get, pingUrl);
            // Disable keep-alive to avoid connection reuse skewing the latency
            request.Headers.ConnectionClose = true;
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            stopwatch.Stop();
            
            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                return stopwatch.ElapsedMilliseconds;
            }
            return -1;
        }
        catch
        {
            return -1;
        }
    }

    /// <summary>
    /// Get a random proxy from the array
    /// </summary>
    private static string GetRandomProxy(string[] proxies)
    {
        if (proxies.Length == 0)
        {
            return null;
        }

        int index = _random.Next(proxies.Length);
        return proxies[index];
    }
}
