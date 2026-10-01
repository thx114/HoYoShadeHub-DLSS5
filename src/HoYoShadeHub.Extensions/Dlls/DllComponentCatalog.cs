using System.Text.Json;
using System.Text.Json.Serialization;
using HoYoShadeHub.Extensions.Conditions;
using HoYoShadeHub.Extensions.Networking;

namespace HoYoShadeHub.Extensions.Dlls;

/// <summary>一个可下载的 dll 包（zip）</summary>
public sealed record DllComponent(string Family, string Version, string Url, string? Note = null)
{
    /// <summary>版本归一化（去掉结尾的 .0）—— PE 里读出来是 <c>310,8,0,0</c>，清单里写的是 <c>310.8.0</c></summary>
    public string NormalizedVersion => DllVersion.Normalize(Version);
}

/// <summary>一类 dll（dlssnr / streamline / dlss / dlssd / dlssg）</summary>
public sealed record DllFamily(
    string Id,
    string DisplayName,
    string FilePattern,
    DllRequirementLevel Level,
    string Note,
    string? PreferredVersion = null);

/// <summary>拉清单的结果</summary>
public sealed record DllCatalog(
    IReadOnlyDictionary<string, List<DllComponent>> Components,
    string? Error)
{
    public IReadOnlyList<DllComponent> Of(string family) =>
        Components.TryGetValue(family, out List<DllComponent>? list) ? list : [];

    public bool IsEmpty => Components.Count == 0 || Components.Values.All(v => v.Count == 0);
}

/// <summary>
/// dll 组件清单。
///
/// <para>
/// 来源是用户指定的 <c>RankFTW/RHI</c> 的 <c>dlss_manifest.json</c>
/// （见 docs/RESHADE-INI.md §4）：里面按 <c>dlss / dlssd / dlssg / dlssnr / streamline</c>
/// 分组，每条一个 <c>{ version, url }</c>，url 指向 rhi-repo 上的一个 zip。
/// </para>
///
/// <para>
/// 清单里 <c>dlssnr</c> 只有 2 条，但 rhi-repo 上还有 <c>-RTX40</c> / <c>.SF</c> 的变体
/// （实测 tag <c>dlssnr-310.8.0-RTX40</c> 等），这些硬编码补进来。
/// </para>
/// </summary>
public static class DllComponentCatalog
{
    public const string ManifestUrl = "https://raw.githubusercontent.com/RankFTW/RHI/main/dlss_manifest.json";

    private const string RhiRepoDownload = "https://github.com/RankFTW/rhi-repo/releases/download";

    /// <summary>
    /// 界面上的分组顺序 / 说明。可被 catalog/conditions.json 的 dllFamilies 整表替换
    /// （按 id 对齐到清单数据；读不到/为空用这份内置默认）。
    /// </summary>
    public static IReadOnlyList<DllFamily> Families =>
        Conditions.AddonConditions.Current?.DllFamilies is { Length: > 0 } remote
            ? [.. remote.Select(f => new DllFamily(
                f.Id,
                f.DisplayName,
                f.FilePattern,
                string.Equals(f.Level, "required", StringComparison.OrdinalIgnoreCase)
                    ? DllRequirementLevel.Required
                    : DllRequirementLevel.Recommended,
                f.Note ?? string.Empty,
                f.PreferredVersion))]
            :
            [
                new("dlssnr", "DLSS5 神经渲染运行时", "nvngx_dlssnr.dll", DllRequirementLevel.Required,
                    "DLSS5 插件必须有它。50 系一般用 310.8.0；30/40 系如果不出画面，换带 RTX40 或 SF 的那几个试试。"),
                new("streamline", "Streamline 运行时", "sl.*.dll", DllRequirementLevel.Required,
                    "sl.interposer.dll / sl.dlss_nr.dll 这一整套。缺了 DLSS5 大概率不出画面。\n注意：2.14.1.0 起的包里已经没有 sl.interposer.dll，默认装 2.14.0.0。",
                    PreferredVersion: "2.14.0.0"),
                new("dlss", "DLSS 超分", "nvngx_dlss.dll", DllRequirementLevel.Recommended,
                    "普通 DLSS 超分用的运行时。"),
                new("dlssd", "光线重建", "nvngx_dlssd.dll", DllRequirementLevel.Recommended,
                    "Ray Reconstruction 用的运行时。"),
                new("dlssg", "帧生成", "nvngx_dlssg.dll", DllRequirementLevel.Recommended,
                    "Frame Generation 用的运行时。"),
            ];

    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static DllFamily? FamilyOf(string id) =>
        Families.FirstOrDefault(f => string.Equals(f.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 拉一次清单。拉不到就返回 null（不抛），交给上层决定要不要换代理重试。
    /// </summary>
    /// <param name="proxyUrl">为空 = 用当前设置里的代理</param>
    private static async Task<Dictionary<string, List<DllManifestEntry>>?> FetchAsync(
        CancellationToken cancellationToken,
        string? proxyUrl = null)
    {
        try
        {
            using var client = HysxHttp.CreateClient(timeout: TimeSpan.FromSeconds(20));
            await using Stream stream = await client.GetStreamAsync(HysxHttp.Apply(ManifestUrl, proxyUrl), cancellationToken);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, List<DllManifestEntry>>>(stream, _options, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>拉清单；失败也返回（Error 里有原因），界面照样能用硬编码的那几条 dlssnr</summary>
    public static async Task<DllCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        var components = new Dictionary<string, List<DllComponent>>(StringComparer.OrdinalIgnoreCase);
        string? error = null;

        try
        {
            Dictionary<string, List<DllManifestEntry>>? manifest = await FetchAsync(cancellationToken);

            if (manifest is null)
            {
                // 直连 raw.githubusercontent.com 在国内基本不通，会抛 SSL / 超时。
                // 清单和远端目录都在 raw 上，所以这里必须走能转发 raw 的公共代理兜一次，
                // 否则用户看到的就是「拉组件清单失败（还能用内置的那几条）」。
                manifest = await FetchAsync(cancellationToken, "https://gh-proxy.org")
                           ?? await FetchAsync(cancellationToken, "https://ghfast.top");
            }

            if (manifest is null)
            {
                throw new InvalidOperationException("清单拉不到：直连和两个公共代理都失败了。");
            }

            foreach ((string family, List<DllManifestEntry> entries) in manifest ?? [])
            {
                var list = new List<DllComponent>();
                foreach (DllManifestEntry entry in entries)
                {
                    if (!string.IsNullOrWhiteSpace(entry?.Version) && !string.IsNullOrWhiteSpace(entry?.Url))
                    {
                        list.Add(new DllComponent(family, entry!.Version!, entry.Url!));
                    }
                }

                if (list.Count > 0)
                {
                    // 不能按字符串排：那样 310.9.1 会排在 310.10.0 前面、2.9 会排在 2.14 前面
                    components[family] = [.. list.OrderByDescending(c => c.Version, DllVersionComparer.Instance)];
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }

        // 屏蔽清单：conditions.json 的 blockedVersions（默认屏蔽 dlssnr 2.14.1.0 ——
        // 这个包本身缺 2 个文件，装上去 DLSS5 直接起不来（用户实测报 0xBAD0000B
        // FAIL_UnableToInitializeFeature）。版本列表里少一条比装上去崩一次强。
        IReadOnlyList<BlockedVersionEntry> blocked = AddonConditions.Current?.BlockedVersions is { Length: > 0 } remoteBlocked
            ? remoteBlocked
            : [new BlockedVersionEntry { Family = "dlssnr", Version = "2.14.1.0", Reason = "该包缺文件，装上会 FAIL_UnableToInitializeFeature" }];

        foreach (BlockedVersionEntry block in blocked)
        {
            if (components.TryGetValue(block.Family, out List<DllComponent>? blockedList))
            {
                int removed = blockedList.RemoveAll(c => string.Equals(c.Version, block.Version, StringComparison.OrdinalIgnoreCase));
                if (removed > 0)
                {
                    error = string.IsNullOrWhiteSpace(error)
                        ? $"已屏蔽 {block.Family} {block.Version}（{block.Reason}）"
                        : error + $"；已屏蔽 {block.Family} {block.Version}";
                }
            }
        }

        // 补充变体：conditions.json 的 dllExtras（默认补 rhi-repo 上的 dlssnr 变体，
        // 清单里没有但确实存在：30/40 系、ShortFuse 分支、Lecram 修改版）
        IReadOnlyList<DllExtraEntry> extras = AddonConditions.Current?.DllExtras is { Length: > 0 } remoteExtras
            ? remoteExtras
            :
            [
                new DllExtraEntry { Family = "dlssnr", Version = "310.8.0-RTX40", Url = $"{RhiRepoDownload}/dlssnr-310.8.0-RTX40/nvngx_dlssnr_310.8.0-RTX40.zip", Note = "30/40 系" },
                new DllExtraEntry { Family = "dlssnr", Version = "310.8.SF-v2", Url = $"{RhiRepoDownload}/dlssnr-310.8.SF-v2/nvngx_dlssnr_310.8.SF-v2.zip", Note = "ShortFuse 分支" },
                new DllExtraEntry { Family = "dlssnr", Version = "310.8.SF", Url = $"{RhiRepoDownload}/dlssnr-310.8.SF/nvngx_dlssnr_310.8.SF.zip", Note = "ShortFuse 分支" },
                new DllExtraEntry { Family = "dlssnr", Version = "310.8.Lecram", Url = $"{RhiRepoDownload}/dlssnr-310.8.Lecram/nvngx_dlssnr_310.8.Lecram.zip", Note = "Lecram 修改版（310.8.3，40 系实测 5~10%+，50 系更高）" },
            ];

        foreach (DllExtraEntry extra in extras)
        {
            if (!string.IsNullOrWhiteSpace(extra.Family) && !string.IsNullOrWhiteSpace(extra.Version) && !string.IsNullOrWhiteSpace(extra.Url))
            {
                AddIfMissing(components, extra.Family, extra.Version, extra.Url, extra.Note ?? string.Empty);
            }
        }

        return new DllCatalog(components, error);
    }

    private static void AddIfMissing(
        Dictionary<string, List<DllComponent>> components,
        string family,
        string version,
        string url,
        string note)
    {
        if (!components.TryGetValue(family, out List<DllComponent>? list))
        {
            list = [];
            components[family] = list;
        }

        if (list.Any(c => string.Equals(c.Version, version, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        list.Add(new DllComponent(family, version, url, note));
    }

    private sealed class DllManifestEntry
    {
        [JsonPropertyName("version")]
        public string? Version { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }
    }
}

/// <summary>按版本号大小比（不是按字符串）</summary>
public sealed class DllVersionComparer : IComparer<string?>
{
    public static readonly DllVersionComparer Instance = new();

    public int Compare(string? x, string? y) => DllVersion.Compare(x, y);
}

/// <summary>版本号比较用的小工具</summary>
public static class DllVersion
{
    /// <summary>
    /// 版本号大小比较（升序）。
    ///
    /// 规则：先按**数字段**比（所以 <c>310.9.1 &lt; 310.10.0</c>、<c>2.9 &lt; 2.14</c>），
    /// 数字段一样时再比剩下的尾巴（所以 <c>310.8.0 &lt; 310.8.0-RTX40</c>、
    /// <c>310.8 &lt; 310.8.SF</c>）。
    /// </summary>
    public static int Compare(string? x, string? y)
    {
        (List<int> numbersX, string restX) = Split(x);
        (List<int> numbersY, string restY) = Split(y);


        int length = Math.Max(numbersX.Count, numbersY.Count);
        for (int i = 0; i < length; i++)
        {
            int a = i < numbersX.Count ? numbersX[i] : 0;
            int b = i < numbersY.Count ? numbersY[i] : 0;
            if (a != b)
            {
                return a.CompareTo(b);
            }
        }

        return string.Compare(restX, restY, StringComparison.OrdinalIgnoreCase);
    }

    private static (List<int> Numbers, string Suffix) Split(string? version)
    {
        var numbers = new List<int>();

        if (string.IsNullOrWhiteSpace(version))
        {
            return (numbers, string.Empty);
        }

        string text = version.Trim();
        if (text.Length > 1 && (text[0] is 'v' or 'V') && char.IsAsciiDigit(text[1]))
        {
            text = text[1..];   // release tag 常带 v 前缀
        }

        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }

            if (i == start)
            {
                break;   // 不是数字开头了，剩下全算尾巴
            }

            numbers.Add(int.TryParse(text[start..i], out int value) ? value : 0);

            if (i < text.Length && text[i] == '.')
            {
                i++;
                continue;
            }

            break;
        }

        return (numbers, text[i..]);
    }

    /// <summary><c>310,8,0,0</c> → <c>310.8</c>；<c>2.13.0.0</c> → <c>2.13</c></summary>
    public static string Normalize(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return string.Empty;
        }

        string[] parts = version.Replace(',', '.').Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int end = parts.Length;
        while (end > 1 && parts[end - 1] == "0")
        {
            end--;
        }

        return string.Join('.', parts.Take(end));
    }

    public static bool IsSame(string? a, string? b) =>
        !string.IsNullOrEmpty(Normalize(a)) && Normalize(a) == Normalize(b);

    /// <summary>
    /// 只比数字段（<c>310.8.SF-v2</c> 和 <c>310.8.0.0</c> 算同一版）。
    /// 用来判断「盘上这个 dll 还是不是我记账时那个版本」——PE 版本号里没有 SF / RTX40 这种变体信息。
    /// </summary>
    public static bool SameNumbers(string? a, string? b)
    {
        (List<int> numbersA, _) = Split(a);
        (List<int> numbersB, _) = Split(b);
        if (numbersA.Count == 0 || numbersB.Count == 0)
        {
            return false;
        }

        int length = Math.Max(numbersA.Count, numbersB.Count);
        for (int i = 0; i < length; i++)
        {
            int x = i < numbersA.Count ? numbersA[i] : 0;
            int y = i < numbersB.Count ? numbersB[i] : 0;
            if (x != y)
            {
                return false;
            }
        }

        return true;
    }
}
