using HoYoShadeHub.Extensions.Networking;
using HoYoShadeHub.Helpers;
using System;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// Bridges HoYoShade Hub's download-server setting into the extensions download stack.
///
/// Why this exists: <see cref="HysxHttp.ProxyUrl"/> was read by every plugin download but
/// <b>never assigned anywhere</b> -- so plugin downloads silently bypassed the proxy and always
/// hit GitHub directly, even when the user had picked Cloudflare/Tencent/Alibaba in settings.
/// This class is the missing assignment.
///
/// Hub's convention for the setting:
/// <c>-1 = Auto, 0 = GitHub direct, 1 = Cloudflare, 2 = Tencent, 3 = Alibaba</c>.
/// </summary>
public static class PluginDownloadProxy
{
    /// <summary>Server index used when the user picked "Auto".</summary>
    private const int AutoFallbackServer = 1;

    /// <summary>Resolved prefix currently applied to plugin downloads; null means GitHub direct.</summary>
    public static string? CurrentProxyUrl { get; private set; }

    public static int CurrentServerIndex { get; private set; }

    /// <summary>True when plugin downloads are going through a mirror.</summary>
    public static bool IsProxied => !string.IsNullOrWhiteSpace(CurrentProxyUrl);

    /// <summary>Human readable name of the current route, for the UI.</summary>
    public static string DescribeRoute() => CurrentServerIndex switch
    {
        -1 => "??",
        0 => "GitHub ??",
        1 => IsProxied ? "Cloudflare ??" : "Cloudflare ?????????????" ,
        2 => IsProxied ? "?????" : "????????????????",
        3 => IsProxied ? "?????" : "????????????????",
        _ => "GitHub ??",
    };

    /// <summary>
    /// Reads the setting and pushes it into <see cref="HysxHttp.ProxyUrl"/>.
    /// Call this before any plugin catalog / download work.
    /// </summary>
    public static void Apply() => Apply(HoYoShadeHub.AppConfig.HoYoShadeFrameworkDownloadServer);

    public static void Apply(int serverIndex)
    {
        CurrentServerIndex = serverIndex;

        int effective = serverIndex;
        if (serverIndex == -1)
        {
            effective = AutoFallbackServer;
        }

        string? proxyUrl;
        try
        {
            proxyUrl = CloudProxyManager.GetProxyUrl(effective);
        }
        catch (Exception)
        {
            proxyUrl = null;
        }

        CurrentProxyUrl = string.IsNullOrWhiteSpace(proxyUrl) ? null : proxyUrl.TrimEnd('/');
        HysxHttp.ProxyUrl = CurrentProxyUrl;
    }
}