using HoYoShadeHub.Core.HoYoPlay;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>
/// 帧率解锁数据（shellcode + 特征）跟随上游 github.com/xiaonian233/genshin-fps-unlock 更新。
/// <para>
/// 游戏版本变动后，旧特征/旧 shellcode 可能失效，所以数据按游戏版本同步：
/// 版本变了就拉取上游 <c>unlockfps/main.cpp</c>，解析 <c>_shellcode_genshin_Const</c> 数组与
/// 扫描特征，存到数据目录。
/// </para>
/// </summary>
public static class FpsUnlockDataService
{
    private const string UpstreamRawUrl =
        "https://raw.githubusercontent.com/xiaonian233/genshin-fps-unlock/master/unlockfps/main.cpp";

    private const string UpstreamApiUrl =
        "https://api.github.com/repos/xiaonian233/genshin-fps-unlock/contents/unlockfps/main.cpp";

    public enum UpdateResult
    {
        /// <summary>拉取并解析成功，且数据相对本地有变化</summary>
        Updated,

        /// <summary>拉取并解析成功，但数据与本地一致（上游未发布新版本）</summary>
        Unchanged,

        /// <summary>本地没有任何数据</summary>
        NoLocalData,

        /// <summary>网络或解析失败</summary>
        Failed,
    }

    private static string FolderPath =>
        Path.Combine(AppConfig.UserDataFolder ?? throw new InvalidOperationException("UserDataFolder is null"), "FpsUnlock");

    private static string ShellcodePath => Path.Combine(FolderPath, "shellcode.bin");
    private static string MetaPath => Path.Combine(FolderPath, "meta.json");

    /// <summary>本地是否已有可用数据</summary>
    public static bool HasLocalData()
        => File.Exists(ShellcodePath) && File.Exists(MetaPath);

    /// <summary>拉取上游并更新本地数据</summary>
    public static async Task<UpdateResult> UpdateAsync(CancellationToken ct = default)
    {
        string? source = await FetchSourceAsync(ct);
        if (source is null)
        {
            return UpdateResult.Failed;
        }

        ParsedData parsed;
        try
        {
            parsed = Parse(source);
        }
        catch
        {
            return UpdateResult.Failed;
        }

        string oldHash = HasLocalData() ? ComputeFileHash(ShellcodePath) : string.Empty;
        bool changed = oldHash != parsed.Sha256;

        Directory.CreateDirectory(FolderPath);

        string tempBin = ShellcodePath + ".tmp";
        await File.WriteAllBytesAsync(tempBin, parsed.Shellcode, ct);
        File.Move(tempBin, ShellcodePath, overwrite: true);

        Meta meta = new()
        {
            sha256 = parsed.Sha256,
            pattern = parsed.Pattern,
            updated_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
        };

        string tempMeta = MetaPath + ".tmp";
        await File.WriteAllTextAsync(tempMeta, JsonSerializer.Serialize(meta), ct);
        File.Move(tempMeta, MetaPath, overwrite: true);

        return changed ? UpdateResult.Updated : UpdateResult.Unchanged;
    }

    /// <summary>
    /// 设置页手动检查：强制拉取，并把结果应用到指定游戏（记录数据版本与检查时间）。
    /// 返回结果状态用于界面提示。
    /// </summary>
    public static async Task<UpdateResult> CheckManuallyAsync(GameId gameId, string gameVersion, CancellationToken ct = default)
    {
        UpdateResult result = await UpdateAsync(ct);
        if (result is UpdateResult.Updated)
        {
            AppConfig.SetFpsUnlockDataVersion(gameId, gameVersion);
            AppConfig.SetFpsUnlockLastCheckTicks(gameId, DateTime.UtcNow.Ticks);
        }

        return result;
    }

    /// <summary>读取本地 shellcode；不存在返回 null</summary>
    public static byte[]? LoadShellcode()
        => File.Exists(ShellcodePath) ? File.ReadAllBytes(ShellcodePath) : null;

    /// <summary>读取本地扫描特征（字节 + 是否通配）；不存在返回 null</summary>
    public static (byte[] bytes, bool[] mask)? LoadPattern()
    {
        if (!File.Exists(MetaPath))
        {
            return null;
        }

        Meta? meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath));
        if (meta?.pattern is not { } tokens || tokens.Count == 0)
        {
            return null;
        }

        byte[] bytes = new byte[tokens.Count];
        bool[] mask = new bool[tokens.Count];

        for (int i = 0; i < tokens.Count; i++)
        {
            mask[i] = tokens[i] is not null;
            bytes[i] = tokens[i] ?? 0;
        }

        return (bytes, mask);
    }

    /// <summary>本地数据的更新时间（UTC）</summary>
    public static DateTimeOffset? GetUpdatedAt()
    {
        if (!File.Exists(MetaPath))
        {
            return null;
        }

        Meta? meta = JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaPath));
        return meta?.updated_at is long unix
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : null;
    }

    private static async Task<string?> FetchSourceAsync(CancellationToken ct)
    {
        HttpClient http = AppConfig.GetService<HttpClient>();

        try
        {
            using HttpResponseMessage resp = await http.GetAsync(UpstreamRawUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadAsStringAsync(ct);
            }
        }
        catch
        {
            // raw 走代理失败时继续试 GitHub API
        }

        try
        {
            using HttpRequestMessage req = new(HttpMethod.Get, UpstreamApiUrl);
            req.Headers.Add("Accept", "application/vnd.github.raw");
            using HttpResponseMessage resp = await http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadAsStringAsync(ct);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string ComputeFileHash(string path)
        => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed record ParsedData(byte[] Shellcode, string Sha256, List<byte?> Pattern);

    // ---- 解析（移植自 extract_shellcode.ps1） ----

    private static ParsedData Parse(string source)
    {
        const string marker = "_shellcode_genshin_Const";
        int markerIndex = source.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            throw new InvalidDataException("shellcode marker missing");
        }

        int open = source.IndexOf('{', markerIndex);
        if (open < 0)
        {
            throw new InvalidDataException("shellcode opening brace missing");
        }

        int? close = FindArrayEnd(source, open);
        if (close is null)
        {
            throw new InvalidDataException("shellcode closing brace missing");
        }

        string block = source.Substring(open, close.Value - open);
        block = Regex.Replace(block, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        List<byte> bytes = [];
        foreach (string rawLine in block.Split('\n'))
        {
            string line = rawLine;
            int lineComment = line.IndexOf("//", StringComparison.Ordinal);
            if (lineComment >= 0)
            {
                line = line[..lineComment];
            }

            foreach (Match m in Regex.Matches(line, @"0x[0-9A-Fa-f]{2}|'(\\?.)'"))
            {
                string tok = m.Value;
                if (tok.StartsWith("0x", StringComparison.Ordinal))
                {
                    bytes.Add(Convert.ToByte(tok, 16));
                }
                else
                {
                    string c = tok.Trim('\'');
                    if (c.StartsWith('\\'))
                    {
                        c = UnescapeChar(c);
                    }

                    bytes.Add((byte)c[0]);
                }
            }
        }

        byte[] shellcode = [.. bytes];
        if (shellcode.Length is 0 or > 4096)
        {
            throw new InvalidDataException("invalid shellcode length");
        }

        string hash = Convert.ToHexString(SHA256.HashData(shellcode));
        List<byte?> pattern = ExtractScanPattern(source);

        return new ParsedData(shellcode, hash, pattern);
    }

    /// <summary>大括号配对（跳过字符串/字符字面量内的括号）</summary>
    private static int? FindArrayEnd(string source, int open)
    {
        int depth = 0;
        bool inString = false;
        bool inChar = false;
        bool escape = false;

        for (int i = open; i < source.Length; i++)
        {
            char c = source[i];

            if (escape)
            {
                escape = false;
                continue;
            }

            if (c == '\\' && (inString || inChar))
            {
                escape = true;
                continue;
            }

            if (inString)
            {
                if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inChar)
            {
                inChar = false;
                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '\'')
            {
                inChar = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    if (i + 1 < source.Length && source[i + 1] == ';')
                    {
                        return i;
                    }
                }
            }
        }

        return null;
    }

    private static string UnescapeChar(string c)
    {
        return c[1] switch
        {
            'n' => "\n",
            'r' => "\r",
            't' => "\t",
            '0' => "\0",
            '\\' => "\\",
            '\'' => "'",
            '"' => "\"",
            _ => c[1..],
        };
    }

    /// <summary>从 PatternScan_Region 调用处提取带 ?? 通配的特征</summary>
    private static List<byte?> ExtractScanPattern(string source)
    {
        Match m = Regex.Match(source,
            "PatternScan_Region\\(.*?\"((?:[0-9A-Fa-f]{2}|\\?\\?)(?: (?:[0-9A-Fa-f]{2}|\\?\\?))*)\"",
            RegexOptions.Singleline);

        if (!m.Success)
        {
            throw new InvalidDataException("scan pattern missing");
        }

        List<byte?> pattern = [];
        foreach (string part in m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == "??")
            {
                pattern.Add(null);
            }
            else
            {
                pattern.Add(Convert.ToByte(part, 16));
            }
        }

        return pattern.Count > 0
            ? pattern
            : throw new InvalidDataException("scan pattern empty");
    }

    private sealed class Meta
    {
        public string sha256 { get; set; } = string.Empty;
        public List<byte?> pattern { get; set; } = [];
        public long updated_at { get; set; }
    }
}
