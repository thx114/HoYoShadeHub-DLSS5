using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HoYoShadeHub.Extensions.Diagnostics;

/// <summary>
/// 把 Bridge / OptiScaler / ReShade / Hub 那种几十万行、几 MB 的日志压成「给 AI 读的摘要」。
/// </summary>
/// <remarks>
/// <para>
/// 实测一局游戏：bridge 日志 3.6 MB、OptiScaler 2.9 MB、ReShade 366 KB、Hub 570 KB，
/// 合计约 7.5 MB ≈ 200 万 token —— 直接丢给 AI 既不现实也没意义，
/// 因为其中 99.9% 是逐帧重复行。
/// </para>
/// <para>做法（不猜语义，只做可验证的机械压缩）：</para>
/// <list type="bullet">
/// <item>按行分级：<c>E</c>/<c>W</c>/<c>O</c>（其它）。<c>O</c> 只计数，不留内容。</item>
/// <item><c>E</c>/<c>W</c> 行做归一化（时间戳、句柄地址、GUID、路径、长数字全部折叠），同形状合并计数。</item>
/// <item>每个文件再附末尾若干行原文 —— 崩的那一下永远在尾巴上。</item>
/// <item>有字节预算，超了显式写「省略了 N 条」，绝不静默丢信息。</item>
/// </list>
/// <para>同一个 3.6 MB 桥日志实测输出 1～4 KB。</para>
/// </remarks>
public static class LogDigest
{
    /// <summary>一个待摘要的日志文件。</summary>
    public sealed record Source(string DisplayName, string Path);

    /// <summary>摘要选项（全部是上限，只会更小）。</summary>
    public sealed record Options
    {
        /// <summary>单文件最多输出多少字节的摘要。</summary>
        public int MaxOutputBytesPerFile { get; init; } = 12 * 1024;

        /// <summary>整份摘要最多多少字节（多个文件共享）。</summary>
        public int MaxOutputBytesTotal { get; init; } = 48 * 1024;

        /// <summary>单文件最多列多少条 E/W 形状。</summary>
        public int MaxPatternsPerFile { get; init; } = 120;

        /// <summary>
        /// 单文件最多列多少条 W 形状。实测警告形状能到四位数（ReShade 一局 1073 条），
        /// 而它们绝大多数是 `not found` / `invalid` 这类线索，价值远低于错误行和末尾原文 —— 单独限额。
        /// </summary>
        public int MaxWarnPatterns { get; init; } = 40;

        /// <summary>W 形状最多占单文件预算的比例（剩下的留给 E 和末尾原文）。</summary>
        public double WarnBudgetShare { get; init; } = 0.30;

        /// <summary>末尾原文每行最多留多少字符。</summary>
        public int TailLineChars { get; init; } = 180;

        /// <summary>每个文件附多少行末尾原文。</summary>
        public int TailLines { get; init; } = 10;

        /// <summary>单个文件最多读多少字节。</summary>
        public long MaxInputBytes { get; init; } = 64L * 1024 * 1024;

        /// <summary>单个配置文件的紧凑视图最多多少字节。</summary>
        public int MaxConfigBytesPerFile { get; init; } = 16 * 1024;

        /// <summary>所有配置文件紧凑视图合计最多多少字节。</summary>
        public int MaxConfigBytesTotal { get; init; } = 48 * 1024;

        public static Options Default { get; } = new();
    }

    /// <summary>单个配置文件的紧凑视图结果。</summary>
    public sealed record ConfigResult(
        string DisplayName,
        string Text,
        long InputBytes,
        int InputLines,
        int KeptLines,
        int DroppedLines,
        bool Failed);

    /// <summary>整份配置紧凑视图的结果。</summary>
    public sealed record ConfigDocumentResult(string Text, long InputBytes, long OutputBytes, IReadOnlyList<ConfigResult> Files);

    /// <summary>单个文件的摘要结果。</summary>
    public sealed record FileResult(
        string DisplayName,
        string Text,
        long InputBytes,
        int InputLines,
        int Errors,
        int Warnings,
        int Others,
        int Patterns,
        int PatternsDropped,
        bool BudgetTruncated,
        bool Failed);

    /// <summary>整份摘要文档的结果。</summary>
    public sealed record DocumentResult(string Text, long InputBytes, long OutputBytes, int Errors, int Warnings, IReadOnlyList<FileResult> Files);

    private static readonly Regex TimePrefix = new(
        @"^\s*\[?(\d{4})[-/](\d{2})[-/](\d{2})[ T](\d{2}):(\d{2}):(\d{2})(?:[.,](\d{1,7}))?\]?\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ClockPrefix = new(
        @"^\s*\[?(\d{1,2}):(\d{2}):(\d{2})(?:[.,](\d{1,7}))?\]?\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 级别标签有两种写法：OptiScaler 的 [I]/[W]/[E]，和 ReShade/Bridge 的 [INFO]/[ERROR]/[WARN]。
    // 单字母只认带方括号的，否则「E 1234 行」这种正文会被误判成错误行。
    private static readonly Regex BracketLevelPrefix = new(
        @"^\s*[\[\(](?<lvl>info|information|warning|warn|error|err|debug|trace|verbose|fatal|critical|success|[iwe])[\]\)]\s*[:\-]?\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex BareLevelPrefix = new(
        @"^\s*(?<lvl>info|information|warning|warn|error|err|debug|trace|verbose|fatal|critical|success)[:\s]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex FramePrefix = new(
        @"^\s*[\[\(]\s*#?\d{1,9}\s*[\]\)]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex GuidText = new(
        @"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex HexText = new(
        @"0x[0-9A-Fa-f]{4,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 配置行内注释：只在「空白 + ; / #」处切，免得把 `C:\a;b` 这种值切坏。
    private static readonly Regex ConfigInlineComment = new(
        @"\s+[;#].*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ConfigSpacedEquals = new(
        @"\s*=\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex LongDigits = new(
        @"\d{3,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WindowsPath = new(
        @"[A-Za-z]:\\[^""'\s;,)\]]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Spaces = new(
        @"\s{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] StrongErrorTokens =
    [
        "error", "fail", "fatal", "exception", "crash", "assert", "denied", "refused",
        "失败", "错误", "异常", "崩溃", "拒绝",
    ];

    /// <summary>「没找到 / 不支持 / 无效」这类是线索不是事故，降一档当警告报，免得 E 计数失去意义。</summary>
    private static readonly string[] SoftTokens =
    [
        "not found", "missing", "invalid", "unsupported", "cannot", "unable", "timeout",
        "未找到", "找不到", "无法", "不支持", "缺失",
    ];

    private static readonly string[] WarnTokens =
    [
        "warn", "deprecated", "fallback", "警告", "回退", "忽略",
    ];

    // 词边界匹配：IsFeatureDenied / errorCount 这种标识符里的子串不能算错误。
    // 中文词不加 \b（「加载失败」里 失败 前面是汉字，没有词边界）。
    private static readonly Regex[] StrongErrorPatterns = BuildPatterns(StrongErrorTokens);
    private static readonly Regex[] SoftPatterns = BuildPatterns(SoftTokens);
    private static readonly Regex[] WarnPatterns = BuildPatterns(WarnTokens);

    private static Regex[] BuildPatterns(string[] tokens) =>
        [.. tokens.Select(static token => new Regex(
            (token.All(static c => c < 128) ? @"\b" : string.Empty) + Regex.Escape(token),
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase))];

    /// <summary>摘要一个文件（读不到也不抛，返回一段说明）。</summary>
    public static FileResult BuildFile(string path, string displayName, Options? options = null)
    {
        Options opt = options ?? Options.Default;
        long size = 0;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new FileResult(displayName, $"## {displayName} 缺失\n", 0, 0, 0, 0, 0, 0, 0, false, true);
            }

            size = info.Length;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return Digest(Enumerate(reader, opt.MaxInputBytes), displayName, size, opt);
        }
        catch (Exception ex)
        {
            return new FileResult(displayName, $"## {displayName} 读取失败：{ex.Message}\n", size, 0, 0, 0, 0, 0, 0, false, true);
        }
    }

    /// <summary>摘要一段文本（测试与内存数据用）。</summary>
    public static FileResult BuildText(string text, string displayName, Options? options = null)
    {
        Options opt = options ?? Options.Default;
        using var reader = new StringReader(text);
        return Digest(Enumerate(reader, opt.MaxInputBytes), displayName, Encoding.UTF8.GetByteCount(text), opt);
    }

    /// <summary>把多个文件摘要拼成一份文档（开头带总览行，AI 看这一段就能决定要不要细看）。</summary>
    public static DocumentResult BuildDocument(string banner, IReadOnlyList<Source> sources, Options? options = null)
    {
        Options opt = options ?? Options.Default;
        var results = new List<FileResult>(sources.Count);
        var sb = new StringBuilder(opt.MaxOutputBytesTotal + 512);
        sb.Append("== HSH-DIGEST v1 ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" ==\n");
        if (!string.IsNullOrWhiteSpace(banner))
        {
            sb.Append(banner.TrimEnd()).Append('\n');
        }

        long inputBytes = 0;
        int errors = 0, warnings = 0;
        foreach (Source source in sources)
        {
            FileResult r = BuildFile(source.Path, source.DisplayName, opt);
            results.Add(r);
            inputBytes += r.InputBytes;
            errors += r.Errors;
            warnings += r.Warnings;
        }

        long output = 0;
        var index = new StringBuilder();
        var bodies = new List<string>(results.Count);
        foreach (FileResult r in results)
        {
            string body = r.Text;
            int bodyBytes = Encoding.UTF8.GetByteCount(body);
            if (output + bodyBytes > opt.MaxOutputBytesTotal)
            {
                // 总预算用尽：只留一行标题，不然「摘要」自己又变成一份大日志
                body = body.Split('\n')[0] + " (总预算用尽，未展开)\n";
                bodyBytes = Encoding.UTF8.GetByteCount(body);
            }

            output += bodyBytes;
            bodies.Add(body);
            index.Append("idx ").Append(r.DisplayName).Append(' ')
                 .Append(FormatSize(r.InputBytes)).Append('/').Append(r.InputLines).Append("L E")
                 .Append(r.Errors).Append(" W").Append(r.Warnings)
                 .Append(r.Errors == 0 && r.Warnings == 0 ? " clean" : string.Empty)
                 .Append('\n');
        }

        double ratio = inputBytes > 0 ? 100.0 * (1.0 - (double)output / inputBytes) : 0;
        sb.Append("files=").Append(results.Count)
          .Append(" in=").Append(FormatSize(inputBytes))
          .Append(" out=").Append(FormatSize(output))
          .Append(" (-").Append(ratio.ToString("F1", CultureInfo.InvariantCulture)).Append("%) E=").Append(errors)
          .Append(" W=").Append(warnings).Append('\n');
        sb.Append(index);

        foreach (string body in bodies)
        {
            sb.Append(body);
        }

        string text = sb.ToString();
        return new DocumentResult(text, inputBytes, Encoding.UTF8.GetByteCount(text), errors, warnings, results);
    }

    /// <summary>
    /// 配置紧凑视图：去掉注释与空行、`key = value` 归一成 `key=value`、折叠连续重复行。
    /// 配置原文照旧全量留在同目录（人工核对用），这里只给 AI 一份「所有键、没有废话」的版本。
    /// 实测：OptiScaler.ini 60 KB / 1906 行 → 14 KB / 692 行；Dx11FsrBridge.ini 17.9 KB → 1.8 KB。
    /// </summary>
    public static ConfigDocumentResult BuildConfigs(string banner, IReadOnlyList<Source> sources, Options? options = null)
    {
        Options opt = options ?? Options.Default;
        var results = new List<ConfigResult>(sources.Count);
        var bodies = new List<string>(sources.Count);
        var index = new StringBuilder();
        long inputBytes = 0;
        long keptTotal = 0;

        foreach (Source source in sources)
        {
            long inBytes = 0;
            int inLines = 0, kept = 0, dropped = 0, sbBytes = 0;
            bool failed = false;
            string? previous = null;
            var sb = new StringBuilder(4096);
            try
            {
                var info = new FileInfo(source.Path);
                if (!info.Exists)
                {
                    failed = true;
                }
                else
                {
                    inBytes = info.Length;
                    using var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    string? raw;
                    while ((raw = reader.ReadLine()) is not null)
                    {
                        inLines++;
                        string line = CompactConfigLine(raw);
                        if (line.Length == 0)
                        {
                            continue;
                        }

                        if (string.Equals(line, previous, StringComparison.Ordinal))
                        {
                            dropped++;
                            continue;
                        }

                        previous = line;
                        int bytes = Encoding.UTF8.GetByteCount(line) + 1;
                        if (sbBytes + bytes > opt.MaxConfigBytesPerFile || keptTotal + sbBytes + bytes > opt.MaxConfigBytesTotal)
                        {
                            dropped++;
                            continue;
                        }

                        sb.Append(line).Append('\n');
                        sbBytes += bytes;
                        kept++;
                    }
                }
            }
            catch (Exception ex)
            {
                failed = true;
                _ = ex;
            }

            var body = new StringBuilder(sb.Length + 128);
            body.Append("## ").Append(source.DisplayName).Append(' ')
                .Append(FormatSize(inBytes)).Append('/').Append(inLines).Append("L → ")
                .Append(FormatSize(sbBytes)).Append('/').Append(kept).Append("L\n");
            if (failed)
            {
                body.Append("（读不到，原文见同目录）\n");
            }
            else if (dropped > 0)
            {
                body.Append("… 省略 ").Append(dropped).Append(" 行（注释/空行/连续重复/预算）…\n");
            }

            body.Append(sb);

            string text = body.ToString();
            inputBytes += inBytes;
            keptTotal += sbBytes;
            bodies.Add(text);
            index.Append("idx ").Append(source.DisplayName).Append(' ')
                 .Append(FormatSize(inBytes)).Append('/').Append(inLines).Append("L → ")
                 .Append(FormatSize(sbBytes)).Append('/').Append(kept).Append("L\n");
            results.Add(new ConfigResult(source.DisplayName, text, inBytes, inLines, kept, dropped, failed));
        }

        double ratio = inputBytes > 0 ? 100.0 * (1.0 - (double)keptTotal / inputBytes) : 0;
        var doc = new StringBuilder((int)Math.Min(keptTotal, int.MaxValue - 1024) + 1024);
        doc.Append("== HSH-CONFIG v1 ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)).Append(" ==\n");
        if (!string.IsNullOrWhiteSpace(banner))
        {
            doc.Append(banner.TrimEnd()).Append('\n');
        }

        doc.Append("cfgs=").Append(results.Count)
           .Append(" in=").Append(FormatSize(inputBytes))
           .Append(" out=").Append(FormatSize(keptTotal))
           .Append(" (-").Append(ratio.ToString("F1", CultureInfo.InvariantCulture)).Append("%)")
           .Append(" 注释/空行/连续重复已去，键值全留；原文见同目录 cfg-*\n");
        doc.Append(index);
        foreach (string body in bodies)
        {
            doc.Append(body);
        }

        string text2 = doc.ToString();
        return new ConfigDocumentResult(text2, inputBytes, Encoding.UTF8.GetByteCount(text2), results);
    }

    /// <summary>配置一行归一：去注释、去空行、`key = value` → `key=value`。返回空串表示这行不要了。</summary>
    internal static string CompactConfigLine(string line)
    {
        string text = line.Trim();
        if (text.Length == 0 || text[0] is ';' or '#' || text.StartsWith("//", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        text = ConfigInlineComment.Replace(text, string.Empty);
        text = ConfigSpacedEquals.Replace(text, "=");
        return Spaces.Replace(text, " ").Trim();
    }

    private static IEnumerable<string> Enumerate(TextReader reader, long maxBytes)    {
        long read = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            read += line.Length + 1;
            if (read > maxBytes)
            {
                yield return $"… 已读到 {FormatSize(maxBytes)}，其余截断 …";
                yield break;
            }

            yield return line;
        }
    }

    private static FileResult Digest(IEnumerable<string> lines, string displayName, long inputBytes, Options opt)
    {
        var patterns = new Dictionary<string, Pattern>(StringComparer.Ordinal);
        var tail = new Queue<string>(Math.Max(1, opt.TailLines));
        int total = 0, errors = 0, warnings = 0, others = 0, order = 0;
        string? firstTime = null, lastTime = null;
        bool tailOnly = false;

        foreach (string raw in lines)
        {
            total++;
            string trimmed = raw.TrimEnd();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (trimmed.StartsWith('…') && trimmed.EndsWith('…'))
            {
                tailOnly = true;
                break;
            }

            tail.Enqueue(trimmed.Length > opt.TailLineChars ? trimmed[..opt.TailLineChars] + "…" : trimmed);
            if (tail.Count > opt.TailLines)
            {
                tail.Dequeue();
            }

            string stripped = StripTime(trimmed, out string? time);
            int severity = Classify(stripped, out string? keyword);
            if (severity == 0)
            {
                others++;
                continue;
            }

            string normalized = Fold(stripped);
            if (time is not null)
            {
                firstTime ??= time;
                lastTime = time;
            }

            if (severity == 2)
            {
                errors++;
            }
            else
            {
                warnings++;
            }

            if (!patterns.TryGetValue(normalized, out Pattern? p))
            {
                if (patterns.Count >= opt.MaxPatternsPerFile * 8)
                {
                    // 形状爆炸（真·随机内容）：只计数，不再存样本
                    continue;
                }

                patterns[normalized] = p = new Pattern { Order = order++, Severity = severity, Sample = normalized, Keyword = keyword };
            }

            p.Count++;
            p.First ??= time;
            p.Last = time ?? p.Last;
            if (severity > p.Severity)
            {
                p.Severity = severity;
                p.Keyword = keyword;
            }
        }

        var ordered = patterns.Values
            .OrderByDescending(p => p.Severity)
            .ThenByDescending(p => p.Count)
            .ThenBy(p => p.Order)
            .ToList();

        var sb = new StringBuilder(2048);
        sb.Append("## ").Append(displayName).Append(' ')
          .Append(FormatSize(inputBytes)).Append('/').Append(total).Append("L ")
          .Append(firstTime ?? "-").Append('-').Append(lastTime ?? "-")
          .Append(" E=").Append(errors).Append(" W=").Append(warnings).Append(" O=").Append(others);
        if (errors == 0 && warnings == 0)
        {
            sb.Append(" clean");
        }

        if (tailOnly)
        {
            sb.Append(" (输入被截断)");
        }

        sb.Append('\n');

        int shown = 0, dropped = 0;
        int bytes = Encoding.UTF8.GetByteCount(sb.ToString());

        // 末尾原文是「崩的那一下」，比警告形状值钱得多 —— 先把它的字节数预留出来，
        // 否则会被几百条 `not found` 警告挤到一行都写不下（实测过：末尾只剩 51 字节）。
        int tailReserve = 0;
        if (tail.Count > 0)
        {
            tailReserve = Encoding.UTF8.GetByteCount("TAIL " + tail.Count.ToString(CultureInfo.InvariantCulture) + "\n");
            foreach (string line in tail)
            {
                tailReserve += Encoding.UTF8.GetByteCount("  " + line + "\n");
            }
        }

        int patternBudget = Math.Max(512, opt.MaxOutputBytesPerFile - tailReserve - bytes - 64);
        int warnBudget = Math.Max(256, (int)(patternBudget * opt.WarnBudgetShare));
        int errBudget = patternBudget;
        int shownWarn = 0, shownErr = 0;
        int errBytes = 0, warnBytes = 0;
        foreach (Pattern p in ordered)
        {
            string line = string.Create(CultureInfo.InvariantCulture,
                $"{p.Mark}({p.Keyword ?? "-"}) {p.Count}x {p.First ?? "-"} | {p.Sample}\n");
            int lineBytes = Encoding.UTF8.GetByteCount(line);
            if (p.Severity >= 2)
            {
                if (shownErr >= opt.MaxPatternsPerFile || errBytes + lineBytes > errBudget)
                {
                    dropped++;
                    continue;
                }

                errBytes += lineBytes;
                shownErr++;
            }
            else
            {
                if (shownWarn >= opt.MaxWarnPatterns || warnBytes + lineBytes > warnBudget)
                {
                    dropped++;
                    continue;
                }

                warnBytes += lineBytes;
                shownWarn++;
            }

            sb.Append(line);
            bytes += lineBytes;
            shown++;
        }

        if (dropped > 0)
        {
            sb.Append("… 省略 ").Append(dropped).Append(" 条形状（预算/条数上限，看原文件）…\n");
        }

        if (tail.Count > 0)
        {
            string tailHead = "TAIL " + tail.Count.ToString(CultureInfo.InvariantCulture) + "\n";
            sb.Append(tailHead);
            bytes += Encoding.UTF8.GetByteCount(tailHead);
            int tailSkipped = 0;
            foreach (string line in tail)
            {
                string entry = "  " + line + "\n";
                int entryBytes = Encoding.UTF8.GetByteCount(entry);
                if (bytes + entryBytes > opt.MaxOutputBytesPerFile)
                {
                    tailSkipped++;
                    continue;
                }

                sb.Append(entry);
                bytes += entryBytes;
            }

            if (tailSkipped > 0)
            {
                sb.Append("… 末尾另有 ").Append(tailSkipped).Append(" 行因预算省略 …\n");
            }
        }

        bool truncated = bytes > opt.MaxOutputBytesPerFile;
        return new FileResult(displayName, sb.ToString(), inputBytes, total, errors, warnings, others, shown, dropped, truncated, false);
    }

    /// <summary>0=其它，1=警告，2=错误。有明确级别标签就信标签，否则按关键词。</summary>
    internal static int Classify(string line) => Classify(line, out _);

    /// <summary>同上，另外把「凭什么这么判」的关键词带出来（长行截断后看正文是看不出原因的）。</summary>
    internal static int Classify(string line, out string? keyword)
    {
        keyword = null;

        Match level = BracketLevelPrefix.Match(line);
        if (!level.Success)
        {
            level = BareLevelPrefix.Match(line);
        }

        if (level.Success)
        {
            string lvl = level.Groups["lvl"].Value.ToLowerInvariant();
            if (lvl is "error" or "err" or "fatal" or "critical" or "e")
            {
                keyword = lvl;
                return 2;
            }

            if (lvl is "warn" or "warning" or "w")
            {
                keyword = lvl;
                return 1;
            }
        }

        if (TryMatch(line, StrongErrorPatterns, out keyword))
        {
            return 2;
        }

        if (TryMatch(line, WarnPatterns, out keyword) || TryMatch(line, SoftPatterns, out keyword))
        {
            return 1;
        }

        return 0;
    }

    private static bool TryMatch(string line, Regex[] patterns, out string? keyword)
    {
        foreach (Regex pattern in patterns)
        {
            foreach (Match match in pattern.Matches(line))
            {
                if (!IsZeroAssertion(line, match.Index + match.Length))
                {
                    keyword = match.Value.ToLowerInvariant();
                    return true;
                }
            }
        }

        keyword = null;
        return false;
    }

    /// <summary><c>exception=0</c> / <c>errors: 0</c> / <c>failed = 0</c> 说的是「没发生」，不能当错误报。</summary>
    private static bool IsZeroAssertion(string line, int at)
    {
        int i = at;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }

        if (i >= line.Length || (line[i] != '=' && line[i] != ':'))
        {
            return false;
        }

        i++;
        while (i < line.Length && line[i] == ' ')
        {
            i++;
        }

        return i < line.Length && line[i] == '0';
    }

    /// <summary>折叠时间戳、地址、GUID、路径、长数字，让同形状的行合并成一条。</summary>
    internal static string Normalize(string line, out string? time) => Fold(StripTime(line, out time));

    /// <summary>只剥行首时间戳（两种常见写法），把时间交给调用方。分级必须在剥完之后做，否则看不到 [warn] 标签。</summary>
    internal static string StripTime(string line, out string? time)
    {
        time = null;

        Match m = TimePrefix.Match(line);
        if (m.Success)
        {
            time = $"{m.Groups[4].Value}:{m.Groups[5].Value}:{m.Groups[6].Value}";
            return line[m.Length..];
        }

        m = ClockPrefix.Match(line);
        if (m.Success)
        {
            time = $"{m.Groups[1].Value.PadLeft(2, '0')}:{m.Groups[2].Value}:{m.Groups[3].Value}";
            return line[m.Length..];
        }

        return line;
    }

    /// <summary>把地址、GUID、路径、长数字、连续空格折叠掉；<c>[warn]</c> 这类标签故意留着。</summary>
    internal static string Fold(string text)
    {
        Match m = FramePrefix.Match(text);
        if (m.Success)
        {
            text = text[m.Length..];
        }

        text = GuidText.Replace(text, "{guid}");
        text = HexText.Replace(text, "0x#");
        text = WindowsPath.Replace(text, static x => "\\" + Path.GetFileName(x.Value));
        text = LongDigits.Replace(text, "#");
        text = Spaces.Replace(text, " ").Trim();
        text = CompactSample(text);

        return text.Length > 220 ? text[..220] + "…" : text;
    }

    /// <summary>
    /// 长行开头常叠着好几层纯元数据（<c>[I] SL Log: [10-14-30][streamline][info][tid:#][3s:#ms:#us]commonEntry.cpp:</c> …），
    /// 不剥掉的话真正的错误正文会被挤到截断线之外，摘要就白压了。只在长行上做，且至少剥掉两层才认。
    /// </summary>
    internal static string CompactSample(string text)
    {
        if (text.Length < 160)
        {
            return text;
        }

        int i = 0;
        int groups = 0;
        int labels = 0;
        for (int step = 0; step < 8; step++)
        {
            int start = i;
            while (i < text.Length && text[i] == ' ')
            {
                i++;
            }

            if (i < text.Length && text[i] == '[')
            {
                int close = text.IndexOf(']', i + 1);
                if (close > i && close - i <= 40)
                {
                    i = close + 1;
                    groups++;
                    continue;
                }
            }

            int colon = text.IndexOf(':', i);
            if (labels < 2 && colon > i && colon - i <= 20 && !text[i..colon].Contains('='))
            {
                i = colon + 1;
                labels++;
                continue;
            }

            i = start;
            break;
        }

        if (groups < 2)
        {
            return text;
        }

        string tail = text[i..].TrimStart();
        return tail.Length >= 40 ? tail : text;
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024 * 1024)).ToString("F2", CultureInfo.InvariantCulture) + "G",
        >= 1024 * 1024 => (bytes / (1024.0 * 1024)).ToString("F2", CultureInfo.InvariantCulture) + "M",
        >= 1024 => (bytes / 1024.0).ToString("F1", CultureInfo.InvariantCulture) + "k",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + "B",
    };

    private sealed class Pattern
    {
        public int Count { get; set; }

        public int Severity { get; set; }

        public int Order { get; init; }

        public string? First { get; set; }

        public string? Last { get; set; }

        public string Sample { get; init; } = string.Empty;

        /// <summary>判成 E/W 的依据（级别标签或命中的关键词），写进摘要里让人看得懂为什么。</summary>
        public string? Keyword { get; set; }

        public char Mark => Severity >= 2 ? 'E' : 'W';
    }
}
