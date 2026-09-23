using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Xxmi;

/// <summary>GameBanana 上一个模组的展示用元信息</summary>
/// <param name="Name">模组名（卡片标题用这个，别用 gb_719975 这种目录名）</param>
/// <param name="Version">模组版本号（可能是空串）</param>
/// <param name="Author">作者</param>
/// <param name="ProfileUrl">模组主页</param>
/// <param name="ThumbnailUrl">缩略图地址</param>
internal sealed record GameBananaModInfo(
    string? Name,
    string? Version,
    string? Author,
    string? ProfileUrl,
    string? ThumbnailUrl);

/// <summary>
/// mod 卡片上那张预览图。
///
/// <para>
/// 图片来源是 GameBanana 的 apiv11：卡片目录名如果是 <c>gb_{modId}</c>（浏览器扩展装进来的
/// mod 就是这个命名，见 <see cref="GameBananaModInstaller"/>），就能反查出它的
/// <c>_aPreviewMedia._aImages[0]._sFile530</c>（530x412 的缩略图，正好当卡片图）。
/// </para>
///
/// <para>
/// 两条硬性约束（都是踩过的坑）：
/// <list type="number">
/// <item>必须<b>走系统代理</b> —— 工厂默认客户端的 DOH ConnectCallback 是裸 socket 直连、
/// 绕过代理会挂死，见 <see cref="GameBananaModInstaller"/> 的同款处理；</item>
/// <item>必须<b>落盘缓存</b> —— 卡片是虚拟化的，滚动时会反复建模板，
/// 每次都发网络请求既慢又会被限流，所以同一个 modId 只拉一次，之后读本地文件。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class GameBananaPreviewService
{
    private readonly ILogger<GameBananaPreviewService> _logger;
    private readonly HttpClient _httpClient;

    /// <summary>同一个 modId 并发的请求合并成一个，别重复拉</summary>
    private readonly ConcurrentDictionary<int, Task<string?>> _inFlight = new();

    /// <summary>拉失败过的 modId（本次运行内不再重试，避免卡片滚动时反复打网络）</summary>
    private readonly ConcurrentDictionary<int, byte> _failed = new();

    /// <summary>modId → 模组元信息（名字/版本），跑一次会话内缓存</summary>
    private readonly ConcurrentDictionary<int, GameBananaModInfo?> _infoCache = new();

    public GameBananaPreviewService(ILogger<GameBananaPreviewService> logger)
    {
        _logger = logger;
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        });
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    /// <summary>预览图缓存目录：&lt;用户数据&gt;\Cache\gb-preview</summary>
    public static string CacheDirectory =>
        Path.Combine(AppConfig.UserDataFolder, "Cache", "gb-preview");

    /// <summary>
    /// 目录名 → GameBanana mod id。只认 <c>gb_123456</c> 这种（扩展装进来的命名）；
    /// <c>gb_123456 DISABLED</c> 也算，因为禁用只改后缀。认不出来返回 null。
    /// </summary>
    public static int? ModIdFromFolderName(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return null;
        }

        string name = folderName.Trim();

        // 禁用后缀先剥掉
        if (name.EndsWith("DISABLED", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^"DISABLED".Length].TrimEnd();
        }

        if (!name.StartsWith("gb_", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string digits = name[3..].Trim();

        // 必须整串都是数字，避免把 gb_xxx 这种误认
        return digits.Length > 0 && digits.All(char.IsAsciiDigit) && int.TryParse(digits, out int id)
            ? id
            : null;
    }

    /// <summary>
    /// 拿这个 mod 的预览图本地路径；没有就返回 null（调用方显示占位图）。
    /// 命中缓存直接返回，否则联网拉一次并写进缓存。
    /// </summary>
    public Task<string?> GetPreviewAsync(int modId, CancellationToken cancellationToken = default)
    {
        string cached = CachePathFor(modId);

        if (File.Exists(cached) && new FileInfo(cached).Length > 0)
        {
            return Task.FromResult<string?>(cached);
        }

        if (_failed.ContainsKey(modId))
        {
            return Task.FromResult<string?>(null);
        }

        // 同一个 modId 只跑一个下载任务，其它调用者共享它
        return _inFlight.GetOrAdd(modId, id => FetchAndCacheAsync(id, cached, cancellationToken));
    }

    private async Task<string?> FetchAndCacheAsync(int modId, string cachedPath, CancellationToken cancellationToken)
    {
        try
        {
            GameBananaModInfo? info = await GetModInfoAsync(modId, cancellationToken).ConfigureAwait(false);

            if (info?.ThumbnailUrl is not { Length: > 0 } imageUrl)
            {
                _failed[modId] = 0;
                return null;
            }

            byte[] bytes = await _httpClient.GetByteArrayAsync(imageUrl, cancellationToken).ConfigureAwait(false);

            if (bytes.Length == 0)
            {
                _failed[modId] = 0;
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);

            // 先写临时文件再 Move，避免半截文件被当成有效缓存
            string temp = cachedPath + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken).ConfigureAwait(false);
            File.Move(temp, cachedPath, overwrite: true);

            _logger.LogInformation("GameBanana preview cached for mod {ModId}", modId);
            return cachedPath;
        }
        catch (Exception ex)
        {
            // 拉不到只是少了张图，不该影响卡片显示
            _logger.LogWarning(ex, "GameBanana preview fetch failed for mod {ModId}", modId);
            _failed[modId] = 0;
            return null;
        }
        finally
        {
            _inFlight.TryRemove(modId, out _);
        }
    }

    /// <summary>
    /// 拿模组元信息（名字 / 版本 / 缩略图地址）。一次请求把这些都取回来，
    /// 别为了名字和图片各打一次 API。
    /// </summary>
    public async Task<GameBananaModInfo?> GetModInfoAsync(int modId, CancellationToken cancellationToken = default)
    {
        if (_infoCache.TryGetValue(modId, out GameBananaModInfo? cached))
        {
            return cached;
        }

        try
        {
            string api = $"https://gamebanana.com/apiv11/Mod/{modId}" +
                         "?_csvProperties=_sName,_sVersion,_sProfileUrl,_tsDateUpdated,_aPreviewMedia,_aSubmitter";

            string json = await _httpClient.GetStringAsync(api, cancellationToken).ConfigureAwait(false);

            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;

            string? name = root.TryGetProperty("_sName", out JsonElement nameEl) ? nameEl.GetString() : null;
            string? version = root.TryGetProperty("_sVersion", out JsonElement verEl) ? verEl.GetString() : null;
            string? profile = root.TryGetProperty("_sProfileUrl", out JsonElement profEl) ? profEl.GetString() : null;
            string? author = null;

            if (root.TryGetProperty("_aSubmitter", out JsonElement submitter)
                && submitter.TryGetProperty("_sName", out JsonElement authorEl))
            {
                author = authorEl.GetString();
            }

            var info = new GameBananaModInfo(name, version, author, profile, ExtractThumbnailUrl(root));

            _infoCache[modId] = info;
            return info;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GameBanana mod info fetch failed for {ModId}", modId);
            _infoCache[modId] = null;
            return null;
        }
    }

    /// <summary>从 apiv11 的 JSON 里挑一张缩略图（优先 530，退到 220、100、原图）</summary>
    private static string? ExtractThumbnailUrl(JsonElement root)
    {
        if (!root.TryGetProperty("_aPreviewMedia", out JsonElement media)
            || !media.TryGetProperty("_aImages", out JsonElement images)
            || images.ValueKind != JsonValueKind.Array
            || images.GetArrayLength() == 0)
        {
            return null;
        }

        // HUD 图标那类不是截图，跳过；优先拿 screenshot
        foreach (JsonElement image in images.EnumerateArray())
        {
            if (!image.TryGetProperty("_sBaseUrl", out JsonElement baseUrlEl)
                || baseUrlEl.GetString() is not { Length: > 0 } baseUrl)
            {
                continue;
            }

            foreach (string key in new[] { "_sFile530", "_sFile220", "_sFile100", "_sFile" })
            {
                if (image.TryGetProperty(key, out JsonElement fileEl)
                    && fileEl.GetString() is { Length: > 0 } file)
                {
                    return baseUrl.TrimEnd('/') + "/" + file;
                }
            }
        }

        return null;
    }

    private static string CachePathFor(int modId) => Path.Combine(CacheDirectory, modId + ".jpg");
}
