using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Networking;
using System.Net.Http;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>解析出来的「要下哪个文件」</summary>
public sealed record GithubArtifact(string Tag, string AssetName, string DownloadUrl, long Size);

/// <summary>
/// 解析 GitHub 仓库的 Release，挑出要下载的资产。
///
/// <para>三条路径，**优先走不吃 API 限额的那两条**：</para>
/// <list type="number">
/// <item>
/// <b>固定资产名</b>（<see cref="ExtensionSource.AssetName"/>）：
/// 只要能拿到 tag 就能拼出下载地址 <c>https://github.com/{repo}/releases/download/{tag}/{name}</c>，
/// 于是走 <b>releases.atom</b>（约 6 KB）拿 tag，完全不碰 API。
/// </item>
/// <item>
/// <b>glob 选资产</b>（<see cref="ExtensionSource.AssetPattern"/>）：
/// atom 拿 tag → 再抓 <c>releases/expanded_assets/{tag}</c> 这个 HTML 页拿资产名。
/// 这条以前是必须读 Release JSON 的，但**匿名 API 只有 60 次/小时**（按出口 IP 算），
/// 打爆了就直接 403，用户那边只看到「获取文件失败」——所以改成抓 HTML。
/// </item>
/// <item>
/// <b>API 兜底</b>：上面都拿不到才读 Release JSON（那里有 size / prerelease 这些更准的信息）。
/// </item>
/// </list>
///
/// <para>
/// 另外：像 <c>clshortfuse/renodx</c> 这种仓库每个 release 挂 450+ 个资产，
/// 拉 <c>/releases</c> 列表会返回好几 MB，GitHub 直接 504 —— 这也是尽量别走 API 的原因。
/// </para>
/// </summary>
public sealed class GithubReleaseResolver
{
    private const string AtomUrlTemplate = "https://github.com/{0}/releases.atom";

    private readonly HttpClient _httpClient;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private static readonly Regex _atomTagRegex = new(
        @"releases/tag/(?<tag>[^""<]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>releases 列表页里每条 release 的 tag 链接</summary>
    private static readonly Regex _releaseTagHrefRegex = new(
        @"/releases/tag/(?<tag>[^""<?#]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// releases 列表页里每条 release 卡片上的发布时间：
    /// <c>&lt;relative-time class="no-wrap" prefix="" datetime="2026-09-21T05:02:09Z"&gt;</c>，
    /// 和 API 的 <c>published_at</c> 一模一样。只认 ISO 那种写法 —— 页面上还有 commit 时间用的是
    /// <c>datetime="2026-09-10 23:50:46 UTC"</c>，别混进来。
    /// </summary>
    private static readonly Regex _releaseCardDateRegex = new(
        @"datetime=""(?<dt>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:Z|[+-]\d{2}:\d{2}))""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>expanded_assets 那个 HTML 页里每个资产的下载链接</summary>
    private static readonly Regex _assetHrefRegex = new(
        @"/releases/download/(?<tag>[^/""]+)/(?<asset>[^""<]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    #region 缓存（用户要求：一天只自动拉一次；手动按钮强制刷新）

    /// <summary>
    /// 缓存目录。默认落在 <c>%LOCALAPPDATA%\HoYoShadeHub\github-cache</c>（纯缓存，丢了就重拉）。
    /// 不依赖主程序的用户数据目录  Extensions 层拿不到 AppConfig。
    /// </summary>
    public static string? CacheDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HoYoShadeHub", "github-cache");

    /// <summary>自动检查的缓存有效期。默认 24 小时：一天之内进页面/切标签都用缓存，不再打 GitHub。</summary>
    public static TimeSpan CacheTtl { get; set; } = TimeSpan.FromHours(24);

    private sealed record CacheEntry(DateTime FetchedUtc, string Json);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> _cache = new();

    /// <summary>读缓存；<paramref name="forceRefresh"/> = true（手动按钮）时直接不读。</summary>
    private static T? ReadCache<T>(string key, bool forceRefresh) where T : class
    {
        try
        {
            if (forceRefresh)
            {
                return null;
            }

            if (_cache.TryGetValue(key, out CacheEntry? hot) && DateTime.UtcNow - hot.FetchedUtc < CacheTtl)
            {
                return JsonSerializer.Deserialize<T>(hot.Json);
            }

            string? path = CachePathOf(key);
            if (path is null || !File.Exists(path))
            {
                return null;
            }

            // 文件格式：第一行 ISO 抓取时间，其余是 JSON
            string text = File.ReadAllText(path);
            int split = text.IndexOf(char.Parse("\n"));
            if (split <= 0)
            {
                return null;
            }

            string stamp = text[..split].Trim();
            string json = text[(split + 1)..];
            if (!DateTime.TryParse(stamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime when)
                || DateTime.UtcNow - when >= CacheTtl)
            {
                return null;
            }

            _cache[key] = new CacheEntry(when, json);
            return JsonSerializer.Deserialize<T>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache<T>(string key, T value)
    {
        try
        {
            string json = JsonSerializer.Serialize(value);
            _cache[key] = new CacheEntry(DateTime.UtcNow, json);

            string? path = CachePathOf(key);
            if (path is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture) + "\n" + json);
            }
        }
        catch
        {
        }
    }

    private static string? CachePathOf(string key)
    {
        string? dir = CacheDirectory;
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        string hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)))[..24];
        return Path.Combine(dir, hash + ".json");
    }

    #endregion

    public GithubReleaseResolver(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? HysxHttp.CreateClient(timeout: TimeSpan.FromSeconds(60));
    }

    #region 对外入口

    /// <summary>
    /// 解析一个 github-release 来源，得到「下哪个地址」。
    /// </summary>
    /// <summary>
    /// 解析一个 github-release 来源；<paramref name="tagOverride"/> 不为空时**装指定版本**
    /// （用户要求：可以下拉选插件版本）。
    /// </summary>
    public async Task<GithubArtifact?> ResolveAsync(
        ExtensionSource source,
        string? tagOverride = null,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        string? repo = HysxUtil.NormalizeRepository(source.Repository);
        if (repo is null)
        {
            throw new ArgumentException($"不是合法的 GitHub 仓库: {source.Repository}", nameof(source));
        }

        string key = $"art|{repo}|{tagOverride}|{source.AssetName}|{source.AssetPattern}";
        if (ReadCache<GithubArtifact>(key, forceRefresh) is { } cached)
        {
            return cached;
        }

        GithubArtifact? resolved = await ResolveCoreAsync(source, tagOverride, cancellationToken);
        if (resolved is not null)
        {
            WriteCache(key, resolved);
        }

        return resolved;
    }

    /// <summary>真正干活的版本（包装层负责缓存）</summary>
    private async Task<GithubArtifact?> ResolveCoreAsync(
        ExtensionSource source,
        string? tagOverride,
        CancellationToken cancellationToken)
    {
        string? repo = HysxUtil.NormalizeRepository(source.Repository);
        if (repo is null)
        {
            throw new ArgumentException($"不是合法的 GitHub 仓库: {source.Repository}", nameof(source));
        }

        // 路径 0：指定了版本 —— 资产名写死就直接拼，否则用 expanded_assets HTML 挑（都不吃 API 限额）
        if (!string.IsNullOrWhiteSpace(tagOverride))
        {
            string tag = tagOverride.Trim();

            if (!string.IsNullOrWhiteSpace(source.AssetName))
            {
                return new GithubArtifact(
                    tag,
                    source.AssetName!,
                    $"https://github.com/{repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(source.AssetName!)}",
                    0);
            }

            List<string> names = await GetAssetNamesFromHtmlAsync(repo, tag, cancellationToken);
            string? picked = SelectAssetName(names, source);
            return picked is null
                ? null
                : new GithubArtifact(
                    tag,
                    picked,
                    $"https://github.com/{repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(picked)}",
                    0);
        }

        // 路径 1：资产名写死 —— 只要能确定 tag
        if (!string.IsNullOrWhiteSpace(source.AssetName))
        {
            string? tag = await ResolveLatestTagAsync(source, repo, cancellationToken);
            if (tag is null)
            {
                return null;
            }

            string url = $"https://github.com/{repo}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(source.AssetName!)}";
            return new GithubArtifact(tag, source.AssetName!, url, 0);
        }

        // 路径 2：atom 拿 tag + expanded_assets 拿资产名（都不吃 API 限额）
        GithubArtifact? withoutApi = await ResolveWithoutApiAsync(source, repo, cancellationToken);
        if (withoutApi is not null)
        {
            return withoutApi;
        }

        // 路径 3：读 Release JSON 挑资产（信息更全，但吃限额）
        GithubRelease? release = await ResolveLatestReleaseAsync(source, cancellationToken);
        if (release is null)
        {
            return null;
        }

        GithubAsset? asset = SelectAsset(release, source);
        return asset is null
            ? null
            : new GithubArtifact(release.TagName, asset.Name, asset.BrowserDownloadUrl, asset.Size);
    }

    /// <summary>
    /// 只求 tag：先 atom（快、不吃限额），失败再退回 API 列表。
    /// </summary>
    public async Task<string?> ResolveLatestTagAsync(
        ExtensionSource source,
        string repository,
        CancellationToken cancellationToken = default)
    {
        string? tag = await ResolveLatestTagWithoutApiAsync(source, repository, cancellationToken);
        if (tag is not null)
        {
            return tag;
        }

        // 都拿不到才动 API（匿名 60 次/小时，能不用就不用）
        var release = await ResolveLatestReleaseAsync(source, cancellationToken);
        return release?.TagName;
    }

    /// <summary>
    /// 不吃 API 限额地找 tag：
    /// 先 atom（只有最近 10 条），再翻 <c>/releases?page=N</c> 的 HTML（老插件族在这儿）。
    /// </summary>
    private async Task<string?> ResolveLatestTagWithoutApiAsync(
        ExtensionSource source,
        string repository,
        CancellationToken cancellationToken)
    {
        string? fromAtom = await TryResolveTagFromAtomAsync(source, repository, cancellationToken);
        return fromAtom ?? await TryResolveTagFromReleasesHtmlAsync(source, repository, maxPages: 6, cancellationToken);
    }

    /// <summary>
    /// 翻 releases 列表页（HTML，不吃 API 限额）找匹配 tagPattern 的最新 tag。
    /// 遇到第一个匹配就返回（页面是新→旧）。
    /// </summary>
    private async Task<string?> TryResolveTagFromReleasesHtmlAsync(
        ExtensionSource source,
        string repository,
        int maxPages = 6,
        CancellationToken cancellationToken = default)
    {
        Regex? tagRegex = BuildTagRegex(source.TagPattern);

        for (int page = 1; page <= maxPages; page++)
        {
            List<string> tags = [];

            try
            {
                string url = page == 1
                    ? $"https://github.com/{repository}/releases"
                    : $"https://github.com/{repository}/releases?page={page}";

                using var request = new HttpRequestMessage(HttpMethod.Get, HysxHttp.Apply(url));
                request.Headers.Accept.ParseAdd("text/html");
                using var response = await _httpClient.SendAsync(request, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    break;
                }

                string html = await response.Content.ReadAsStringAsync(cancellationToken);
                foreach (Match match in _releaseTagHrefRegex.Matches(html))
                {
                    string tag = Uri.UnescapeDataString(match.Groups["tag"].Value);
                    if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                    {
                        tags.Add(tag);
                    }
                }
            }
            catch
            {
                break;
            }

            if (tags.Count == 0)
            {
                break;   // 没有更多页了
            }

            foreach (string tag in tags)
            {
                if (tagRegex is null || tagRegex.IsMatch(tag))
                {
                    return tag;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 列出这个来源能装的版本（新 → 旧）。
    /// 先 atom（最近 ~10 条，带时间），不够再翻 <c>/releases?page=N</c> 的 HTML —— 全程不碰 API 限额。
    /// </summary>
    public async Task<List<ExtensionVersion>> ListVersionsAsync(
        ExtensionSource source,
        int max = 30,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        string? repo = HysxUtil.NormalizeRepository(source.Repository);
        if (repo is null)
        {
            throw new ArgumentException($"不是合法的 GitHub 仓库: {source.Repository}", nameof(source));
        }

        // 用户要求：自动检查一天只拉一次；点「检查更新」这类手动动作传 forceRefresh: true
        string key = $"ver|{repo}|{max}|{source.TagPattern}";
        if (ReadCache<List<ExtensionVersion>>(key, forceRefresh) is { } cached)
        {
            return cached;
        }

        List<ExtensionVersion> list = await ListVersionsCoreAsync(source, max, cancellationToken);
        if (list.Count > 0)
        {
            WriteCache(key, list);
        }

        return list;
    }

    /// <summary>真正干活的版本（包装层负责缓存）</summary>
    private async Task<List<ExtensionVersion>> ListVersionsCoreAsync(
        ExtensionSource source,
        int max,
        CancellationToken cancellationToken)
    {
        string? repo = HysxUtil.NormalizeRepository(source.Repository);
        if (repo is null)
        {
            throw new ArgumentException($"不是合法的 GitHub 仓库: {source.Repository}", nameof(source));
        }

        Regex? tagRegex = BuildTagRegex(source.TagPattern);
        var versions = new List<ExtensionVersion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var positions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(string tag, DateTimeOffset? published)
        {
            if (string.IsNullOrWhiteSpace(tag) || (tagRegex is not null && !tagRegex.IsMatch(tag)))
            {
                return;
            }

            if (!seen.Add(tag))
            {
                // 同一个 tag 又见一次：这次带了发布时间就补上（atom 那批有、翻 HTML 那批本来没有）
                if (published is not null
                    && positions.TryGetValue(tag, out int position)
                    && versions[position].Published is null)
                {
                    versions[position] = versions[position] with { Published = published };
                }

                return;
            }

            positions[tag] = versions.Count;
            versions.Add(new ExtensionVersion(tag, published));
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, HysxHttp.Apply(string.Format(AtomUrlTemplate, repo)));
            request.Headers.Accept.ParseAdd("application/atom+xml");
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                string xml = await response.Content.ReadAsStringAsync(cancellationToken);
                foreach (ExtensionVersion version in ParseAtom(xml))
                {
                    Add(version.Tag, version.Published);
                }
            }
        }
        catch
        {
            // atom 拿不到就退 HTML
        }

        for (int page = 1; versions.Count < max && page <= 6; page++)
        {
            try
            {
                string url = page == 1
                    ? $"https://github.com/{repo}/releases"
                    : $"https://github.com/{repo}/releases?page={page}";

                using var request = new HttpRequestMessage(HttpMethod.Get, HysxHttp.Apply(url));
                request.Headers.Accept.ParseAdd("text/html");
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    break;
                }

                string html = await response.Content.ReadAsStringAsync(cancellationToken);
                int before = versions.Count;
                foreach ((string tag, DateTimeOffset? published) in ExtractReleaseCards(html))
                {
                    Add(tag, published);
                }

                if (versions.Count == before)
                {
                    break;
                }
            }
            catch
            {
                break;
            }
        }

        return SortByPublishedDescending(versions).Take(max).ToList();
    }

    /// <summary>
    /// 按真正的发布时间排成新 → 旧。
    ///
    /// <para>
    /// GitHub 的 <c>/releases</c> 列表是按「release 对象的创建时间」排的：同一批创建的几条会挨在一起，
    /// 于是「后发布的那条」反而排在下面（实测 rhi-repo 页面前四条发布时间是 09-19 / 09-18 / 09-20 / 09-21），
    /// 下拉框看起来就像乱序。这里按卡片里抓到的发布时间重排；<c>OrderByDescending</c> 是稳定排序，
    /// 没拿到时间的（翻页时页面改版、或者 atom 也没有）保持原顺序垫底。
    /// </para>
    /// </summary>
    public static List<ExtensionVersion> SortByPublishedDescending(IEnumerable<ExtensionVersion> versions)
        => [.. versions.OrderByDescending(v => v.Published ?? DateTimeOffset.MinValue)];

    /// <summary>
    /// 从 <c>/releases</c> 列表页按卡片顺序取出 (tag, 发布时间)。
    /// 卡片 = 一条 <c>/releases/tag/…</c> 链接到下一链接之间的那段 HTML，日期取这段里第一个 ISO 时间。
    /// </summary>
    public static List<(string Tag, DateTimeOffset? Published)> ExtractReleaseCards(string html)
    {
        var cards = new List<(string Tag, DateTimeOffset? Published)>();
        if (string.IsNullOrEmpty(html))
        {
            return cards;
        }

        MatchCollection links = _releaseTagHrefRegex.Matches(html);

        for (int i = 0; i < links.Count; i++)
        {
            Match link = links[i];
            string tag = Uri.UnescapeDataString(link.Groups["tag"].Value);

            int start = link.Index;
            int end = i + 1 < links.Count ? links[i + 1].Index : html.Length;
            int length = Math.Min(end - start, 20000);   // 发布日期就在卡片开头，不用扫整张卡

            DateTimeOffset? published = null;
            Match date = _releaseCardDateRegex.Match(html, start, length);
            if (date.Success
                && DateTimeOffset.TryParse(
                    date.Groups["dt"].Value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTimeOffset parsed))
            {
                published = parsed;
            }

            cards.Add((tag, published));
        }

        return cards;
    }

    /// <summary>atom 里每条 entry 的整段 XML</summary>
    private static readonly Regex _atomEntryRegex = new(
        @"<entry>(?<body>.*?)</entry>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    /// <summary>atom entry 的 id：<c>tag:github.com,2008:Repository/123456/&lt;tag&gt;</c></summary>
    private static readonly Regex _atomIdTagRegex = new(
        @"/Repository/\d+/(?<tag>[^<]+)</id>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>atom entry 里的时间（= Release 的 published_at）</summary>
    private static readonly Regex _atomUpdatedRegex = new(
        @"<updated>(?<updated>[^<]+)</updated>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 解析 releases.atom，按 feed 里的顺序返回、每条都带发布时间。
    ///
    /// <para>
    /// **必须逐条 entry 取，不能写一个跨 entry 的正则**：entry 里的 <c>&lt;updated&gt;</c> 在 tag 链接
    /// **前面**，懒惰匹配会一路跑到下一条 entry 的 <c>&lt;updated&gt;</c>，
    /// 于是每条 tag 都被配上「下一条」的时间（实测就是这个问题，下拉框看起来完全没有规律）。
    /// </para>
    /// </summary>
    public static List<ExtensionVersion> ParseAtom(string xml)
    {
        var versions = new List<ExtensionVersion>();
        if (string.IsNullOrEmpty(xml))
        {
            return versions;
        }

        foreach (Match entry in _atomEntryRegex.Matches(xml))
        {
            string body = entry.Groups["body"].Value;

            Match tagMatch = _atomIdTagRegex.Match(body);
            if (!tagMatch.Success)
            {
                tagMatch = _atomTagRegex.Match(body);
            }

            if (!tagMatch.Success)
            {
                continue;
            }

            string tag = Uri.UnescapeDataString(tagMatch.Groups["tag"].Value.Trim());

            DateTimeOffset? published = null;
            Match updated = _atomUpdatedRegex.Match(body);
            if (updated.Success
                && DateTimeOffset.TryParse(
                    updated.Groups["updated"].Value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out DateTimeOffset parsed))
            {
                published = parsed;
            }

            versions.Add(new ExtensionVersion(tag, published));
        }

        return versions;
    }

    /// <summary>只走 atom：拿到匹配 tagPattern 的最新 tag；拿不到返回 null</summary>
    private async Task<string?> TryResolveTagFromAtomAsync(
        ExtensionSource source,
        string repository,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, HysxHttp.Apply(string.Format(AtomUrlTemplate, repository)));
            request.Headers.Accept.ParseAdd("application/atom+xml");
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            Regex? tagRegex = BuildTagRegex(source.TagPattern);
            string xml = await response.Content.ReadAsStringAsync(cancellationToken);

            // atom 的顺序**不是**发布时间倒序（实测 rhi-repo 里最新发布的 DLSS-Enabler-4.10.0.7
            // 排在第 4 位），所以取「命中的里面发布时间最晚的」，而不是第一个。
            string? latest = null;
            DateTimeOffset newest = DateTimeOffset.MinValue;

            foreach (ExtensionVersion version in ParseAtom(xml))
            {
                if (tagRegex is not null && !tagRegex.IsMatch(version.Tag))
                {
                    continue;
                }

                DateTimeOffset published = version.Published ?? DateTimeOffset.MinValue;
                if (latest is null || published > newest)
                {
                    latest = version.Tag;
                    newest = published;
                }
            }

            if (latest is not null)
            {
                return latest;
            }
        }
        catch
        {
            // 网络问题就走别路
        }

        return null;
    }

    /// <summary>
    /// 不吃 API 限额的完整解析：atom 拿 tag → expanded_assets 页拿资产名 → 拼下载地址。
    /// </summary>
    private async Task<GithubArtifact?> ResolveWithoutApiAsync(
        ExtensionSource source,
        string repository,
        CancellationToken cancellationToken)
    {
        string? tag = await ResolveLatestTagWithoutApiAsync(source, repository, cancellationToken);
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        List<string> assets = await GetAssetNamesFromHtmlAsync(repository, tag, cancellationToken);
        if (assets.Count == 0)
        {
            return null;
        }

        string? name = SelectAssetName(assets, source);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string url = $"https://github.com/{repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";
        return new GithubArtifact(tag, name, url, 0);
    }

    /// <summary>
    /// 抓 <c>releases/expanded_assets/{tag}</c> 这个页面拿资产名。
    /// 它就是个普通 HTML（GitHub 网页用的），不吃 REST API 的 60 次/小时。
    /// </summary>
    public async Task<List<string>> GetAssetNamesFromHtmlAsync(
        string repository,
        string tag,
        CancellationToken cancellationToken = default,
        bool forceRefresh = false)
    {
        string? repo = HysxUtil.NormalizeRepository(repository);
        if (repo is null)
        {
            throw new ArgumentException($"不是合法的 GitHub 仓库: {repository}", nameof(repository));
        }

        // 某个 tag 的资产清单不会变，缓存一天  选版本 / 安装时别反复打 GitHub（用户要求）
        string key = $"assets|{repo}|{tag}";
        if (ReadCache<List<string>>(key, forceRefresh) is { } cached)
        {
            return cached;
        }

        List<string> names = await GetAssetNamesFromHtmlCoreAsync(repository, tag, cancellationToken);
        if (names.Count > 0)
        {
            WriteCache(key, names);
        }

        return names;
    }

    /// <summary>真正去抓 expanded_assets 的那个（包装层负责缓存）</summary>
    private async Task<List<string>> GetAssetNamesFromHtmlCoreAsync(
        string repository,
        string tag,
        CancellationToken cancellationToken)
    {
        var names = new List<string>();

        try
        {
            string url = $"https://github.com/{repository}/releases/expanded_assets/{Uri.EscapeDataString(tag)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, HysxHttp.Apply(url));
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return names;
            }

            string html = await response.Content.ReadAsStringAsync(cancellationToken);

            foreach (Match match in _assetHrefRegex.Matches(html))
            {
                string name = Uri.UnescapeDataString(match.Groups["asset"].Value);
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(name);
                }
            }
        }
        catch
        {
            // 拿不到就让调用方退回 API
        }

        return names;
    }

    /// <summary>在「只有资产名」的情况下挑一个（API 那条路有 size 可用，这里只能按名字）</summary>
    public static string? SelectAssetName(IEnumerable<string> assetNames, ExtensionSource source)
    {
        List<string> names = [.. assetNames.Where(n => !string.IsNullOrWhiteSpace(n))];
        if (names.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(source.AssetName))
        {
            return names.FirstOrDefault(n => string.Equals(n, source.AssetName, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(source.AssetPattern))
        {
            return names
                .Where(n => GlobMatcher.IsMatch(source.AssetPattern, n))
                .Order(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }

        string[] preferredSuffixes = [".zip", ".7z", ".addon64", ".addon32", ".addon", ".fx"];
        foreach (string suffix in preferredSuffixes)
        {
            string? hit = names.FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit;
            }
        }

        return names.Count == 1 ? names[0] : null;
    }

    #endregion

    #region Release JSON

    public async Task<List<GithubRelease>> GetReleasesAsync(
        string repository,
        int maxReleases = 200,
        CancellationToken cancellationToken = default)
    {
        string? repo = HysxUtil.NormalizeRepository(repository);
        if (repo is null)
        {
            throw new ArgumentException($"不是合法的 GitHub 仓库: {repository}", nameof(repository));
        }

        // 像 RankFTW/rhi-repo 这种仓库有近百个 release、十几个插件族，
        // 只取最新一页会把老的那些族整族漏掉，所以必须翻页。
        const int pageSize = 100;
        var result = new List<GithubRelease>();

        for (int page = 1; result.Count < maxReleases; page++)
        {
            string url = $"https://api.github.com/repos/{repo}/releases?per_page={pageSize}&page={page}";
            using var request = HysxHttp.CreateGithubApiRequest(url);
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                int code = (int)response.StatusCode;
                string hint = code is 403 or 429
                    ? $"GitHub API 限流了（匿名只有 60 次/小时，按出口 IP 算）：HTTP {code}。等一会儿再试。"
                    : $"GitHub API 返回 HTTP {code}：{url}";
                throw new HttpRequestException(hint, null, response.StatusCode);
            }

            var batch = await response.Content.ReadFromJsonAsync<List<GithubRelease>>(_jsonOptions, cancellationToken);
            if (batch is null || batch.Count == 0)
            {
                break;
            }

            result.AddRange(batch);
            if (batch.Count < pageSize)
            {
                break;
            }
        }

        return result;
    }

    public async Task<GithubRelease?> GetReleaseByTagAsync(
        string repository,
        string tag,
        CancellationToken cancellationToken = default)
    {
        string? repo = HysxUtil.NormalizeRepository(repository);
        if (repo is null)
        {
            throw new ArgumentException($"不是合法的 GitHub 仓库: {repository}", nameof(repository));
        }

        string url = $"https://api.github.com/repos/{repo}/releases/tags/{Uri.EscapeDataString(tag)}";
        using var request = HysxHttp.CreateGithubApiRequest(url);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<GithubRelease>(_jsonOptions, cancellationToken);
    }

    /// <summary>
    /// 过滤 draft → prerelease（除非允许）→ tagPattern → 发布时间倒序取第一个。
    /// </summary>
    public async Task<GithubRelease?> ResolveLatestReleaseAsync(
        ExtensionSource source,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(source.Repository))
        {
            return null;
        }

        var releases = await GetReleasesAsync(source.Repository, 200, cancellationToken);
        if (releases.Count == 0)
        {
            return null;
        }

        Regex? tagRegex = BuildTagRegex(source.TagPattern);

        return releases
            .Where(r => !r.Draft && !string.IsNullOrWhiteSpace(r.TagName))
            .Where(r => source.IncludePrerelease || !r.Prerelease)
            .Where(r => tagRegex is null || tagRegex.IsMatch(r.TagName))
            .OrderByDescending(r => r.PublishedAt ?? DateTimeOffset.MinValue)
            .FirstOrDefault();
    }

    #endregion

    private static Regex? BuildTagRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return null;
        }

        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            // pattern 写错就当没写，不要让整个安装失败
            return null;
        }
    }

    /// <summary>
    /// 在 release 里挑资产。pattern 为空时的兜底顺序：zip → addon64 → addon32 → 唯一资产。
    /// </summary>
    public static GithubAsset? SelectAsset(GithubRelease release, ExtensionSource source)
    {
        if (!string.IsNullOrWhiteSpace(source.AssetName))
        {
            return release.Assets.FirstOrDefault(
                a => string.Equals(a.Name, source.AssetName, StringComparison.OrdinalIgnoreCase));
        }

        if (release.Assets.Length == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(source.AssetPattern))
        {
            var matched = release.Assets
                .Where(a => GlobMatcher.IsMatch(source.AssetPattern, a.Name))
                .OrderByDescending(a => a.Size)
                .ToArray();

            return matched.Length > 0 ? matched[0] : null;
        }

        string[] preferredSuffixes = [".zip", ".7z", ".addon64", ".addon32", ".addon", ".fx"];

        foreach (string suffix in preferredSuffixes)
        {
            var hit = release.Assets.FirstOrDefault(a => a.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit;
            }
        }

        return release.Assets.Length == 1 ? release.Assets[0] : null;
    }
}
