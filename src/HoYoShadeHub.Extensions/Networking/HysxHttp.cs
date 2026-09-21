using HoYoShadeHub.Core.Networking;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace HoYoShadeHub.Extensions.Networking;

/// <summary>
/// 下载用的 HttpClient 工厂。
/// 复用 HoYoShadeHub.Core 的 <see cref="DohService"/>，DoH / ECH / 连接回退行为与 Hub 本体保持一致。
/// </summary>
public static class HysxHttp
{
    public const string UserAgent = "HoYoShadeHub-Extensions";

    /// <summary>GitHub 上的资源统一走这个前缀，方便用户换镜像</summary>
    public static string? ProxyUrl { get; set; }

    public static HttpClient CreateClient(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        handler ??= DohService.CreateSocketsHttpHandler();

        var client = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = timeout ?? TimeSpan.FromMinutes(30),
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return client;
    }

    /// <summary>
    /// 应用代理前缀（与 Hub 的 DownloadServerItem 行为一致）：
    /// 传入 <c>https://gh-proxy.com</c> 会得到 <c>https://gh-proxy.com/https://api.github.com/...</c>
    /// </summary>
    public static string Apply(string url, string? proxyUrl = null)
    {
        string? prefix = string.IsNullOrWhiteSpace(proxyUrl) ? ProxyUrl : proxyUrl;
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return url;
        }

        prefix = prefix.Trim().TrimEnd('/');
        if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return $"{prefix}/{url}";
    }

    /// <summary>构造访问 GitHub API 的请求（带 Accept / UA）</summary>
    public static HttpRequestMessage CreateGithubApiRequest(string url, string? token = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Apply(url));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }

    /// <summary>把带重定向的下载包装成流；调用方负责释放</summary>
    public static async Task<HttpResponseMessage> GetWithRedirectAsync(
        HttpClient client,
        string url,
        CancellationToken cancellationToken = default)
    {
        var response = await client.GetAsync(Apply(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    public static bool IsSuccess(this HttpStatusCode code)
    {
        int value = (int)code;
        return value is >= 200 and < 300;
    }
}
