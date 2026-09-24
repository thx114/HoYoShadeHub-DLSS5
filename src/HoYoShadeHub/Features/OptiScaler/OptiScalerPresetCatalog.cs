using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using HoYoShadeHub.Features.Plugins;
using HoYoShadeHub.Helpers;

namespace HoYoShadeHub.Features.OptiScaler;

/// <summary>
/// OptiScaler 预设的「联网下载」目录。
///
/// <para>
/// 远端 = 本仓库 <c>catalog/optiscaler-presets.json</c>（索引）
/// + <c>catalog/optiscaler-presets/&lt;file&gt;.ini</c>（内容）。
/// 复用 <see cref="RemoteCatalogService.BaseUrl"/>，换仓库只改那一处。
/// </para>
///
/// <para>
/// 取文件时按 GitHub 直连  gh-proxy.org  ghfast.top 依次试；失败的服务器会记进
/// <see cref="CloudProxyManager"/> 的冷却表，避免反复打同一家。
/// </para>
/// </summary>
internal static class OptiScalerPresetCatalog
{
    /// <summary>远端索引里的一条</summary>
    public sealed class RemotePreset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("author")]
        public string? Author { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        /// <summary>限定只能用在哪些 OptiScaler 来源分支（空 / 缺省 = 不限制）</summary>
        [JsonPropertyName("sources")]
        public List<string>? Sources { get; set; }

        /// <summary>这个远端预设允许用在 sourceId 分支上吗</summary>
        public bool AllowsSource(string? sourceId)
            => Sources is not { Count: > 0 }
               || (sourceId is not null && Sources.Contains(sourceId, StringComparer.OrdinalIgnoreCase));

        /// <summary>限定给哪些游戏用（GameBiz 值，如 hkrpg / nap）。空 = 不限制  只有本仓库上传的条目才带</summary>
        [JsonPropertyName("games")]
        public List<string>? Games { get; set; }

        /// <summary>适配的显卡代次（ada / blackwell / ampere / turing）。空 = 不限制</summary>
        [JsonPropertyName("gpu")]
        public string? Gpu { get; set; }

        /// <summary>这个配置能不能用在这个游戏上（没写 games 的一律允许）</summary>
        public bool AllowsGame(string? gameBiz)
        {
            if (Games is not { Count: > 0 })
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(gameBiz))
            {
                return false;
            }

            // 传进来的是 hkrpg_bilibili / nap_cn 这种完整 biz，配置里写的是基名（hkrpg / nap），两种都认
            string baseName = gameBiz.Split('_')[0];
            return Games.Any(g => string.Equals(g, gameBiz, StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(g, baseName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>下拉里显示：适配本机显卡的那条加个尾巴，一眼能挑出来</summary>
        public override string ToString()
            => Gpu is { Length: > 0 } && string.Equals(Gpu, GpuClass, StringComparison.OrdinalIgnoreCase)
                ? Name + "（适配你的显卡）"
                : Name;
    }

    private sealed class IndexDocument
    {
        [JsonPropertyName("presets")]
        public List<RemotePreset> Presets { get; set; } = new();
    }

    private static string? _gpuClassCache;
    private static bool _gpuClassProbed;

    /// <summary>本机显卡代次（缓存一次）：ada=RTX 40 / blackwell=RTX 50 / ampere=RTX 30 / turing=RTX 20</summary>
    public static string? GpuClass
    {
        get
        {
            if (!_gpuClassProbed)
            {
                _gpuClassCache = DetectGpuClass();
                _gpuClassProbed = true;
            }

            return _gpuClassCache;
        }
    }

    /// <summary>
    /// 读显示驱动注册表里的 DriverDesc（形如 "NVIDIA GeForce RTX 4070 Ti"）判代次 
    /// 不引 WMI 依赖，也不需要 nvidia-smi。认不出返回 null（那就谁都不加分、不排序）。
    /// </summary>
    private static string? DetectGpuClass()
    {
        try
        {
            using var classKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (classKey is null)
            {
                return null;
            }

            foreach (string sub in classKey.GetSubKeyNames())
            {
                using var key = classKey.OpenSubKey(sub);
                if (key?.GetValue("DriverDesc") is not string desc || desc.Length == 0)
                {
                    continue;
                }

                if (desc.Contains("RTX 50", StringComparison.OrdinalIgnoreCase))
                {
                    return "blackwell";
                }

                if (desc.Contains("RTX 40", StringComparison.OrdinalIgnoreCase))
                {
                    return "ada";
                }

                if (desc.Contains("RTX 30", StringComparison.OrdinalIgnoreCase))
                {
                    return "ampere";
                }

                if (desc.Contains("RTX 20", StringComparison.OrdinalIgnoreCase))
                {
                    return "turing";
                }
            }
        }
        catch
        {
        }

        return null;
    }

    /// <summary>拉索引；失败返回空表（不抛，界面上给「拉不到」就行）</summary>
    public static async Task<List<RemotePreset>> FetchAsync(CancellationToken cancellationToken)
    {
        string url = RemoteCatalogService.BaseUrl + "optiscaler-presets.json";
        string? json = await FetchTextAsync(url, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<RemotePreset>();
        }

        try
        {
            IndexDocument? document = JsonSerializer.Deserialize<IndexDocument>(json);
            return document?.Presets is { } presets
                ? presets.FindAll(p => !string.IsNullOrWhiteSpace(p.Name) && !string.IsNullOrWhiteSpace(p.File))
                : new List<RemotePreset>();
        }
        catch
        {
            return new List<RemotePreset>();
        }
    }

    /// <summary>下载某条预设的 ini 内容；失败返回 null</summary>
    public static Task<string?> DownloadAsync(RemotePreset preset, CancellationToken cancellationToken)
    {
        string url = RemoteCatalogService.BaseUrl + "preset-files/" + preset.File;
        return FetchTextAsync(url, cancellationToken);
    }

    /// <summary>直连 + 两个公共 gh 代理依次试</summary>
    private static async Task<string?> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        int[] candidates = [0, 4, 5];

        foreach (int index in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                string? proxy = CloudProxyManager.GetProxyUrl(index);
                string target = string.IsNullOrWhiteSpace(proxy)
                    ? url
                    : CloudProxyManager.ApplyProxy(url, proxy);

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("HoYoShadeHub");
                return await client.GetStringAsync(target, cancellationToken);
            }
            catch
            {
                CloudProxyManager.MarkServerFailed(index);
            }
        }

        return null;
    }
}
