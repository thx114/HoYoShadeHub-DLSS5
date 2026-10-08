using System.Globalization;
using System.Text;

namespace HoYoShadeHub.Extensions.Diagnostics;

/// <summary>
/// 会话快照清单 <c>session.txt</c> 的紧凑写盘器（v2）。
/// </summary>
/// <remarks>
/// <para>
/// 旧格式（v1）每次快照都把「文件名 + 完整绝对路径 + 大小 + 修改时间」整表重抄一遍。
/// 实测一局 5 次快照的 session.txt：125 行里只有 74 行唯一，16.5 KB 里九成是重复；
/// 一局 40 分钟的游戏能膨胀到 70 KB 以上，而这些字节最终都是要被 AI 读掉的。
/// </para>
/// <para>v2 的规则（目标是「给 AI 读也不烧 token」）：</para>
/// <list type="bullet">
/// <item>正文全部走路径基：开头写一次 <c>@0=…</c>，之后只写 <c>@0\ReShade.log</c>。</item>
/// <item>文件目录只在开头列一次，每条一个 2 位 id；后续快照只写<b>变化</b>项。</item>
/// <item>不再写每行的修改时间 —— 快照自己的时间戳已经表达了先后。</item>
/// <item>没有任何变化的快照只占一行；没有异常的日志只有一行摘要。</item>
/// </list>
/// <para>同一份数据实测 16.5 KB → 1.7 KB（约 -90%），快照越多省得越多。</para>
/// </remarks>
public sealed class SessionLogManifest
{
    /// <summary>清单里的一个被抄文件。Kind：L=日志，C=配置。</summary>
    public sealed record Entry(string Id, string Name, string SourcePath, char Kind);

    /// <summary>模块版本行。</summary>
    public sealed record Module(string Label, string? Version, string Path);

    /// <summary>清单头信息。</summary>
    public sealed record Header(
        DateTimeOffset Start,
        int Pid,
        string Process,
        string? CommandLine,
        string? GameKey,
        string AppVersion,
        bool Portable,
        string? DataDir,
        string LogDir,
        string SessionDir);

    /// <summary>一次快照里对某个文件的观察结果。</summary>
    public sealed record Observation(string Id, bool Exists, long Bytes);

    private readonly string _filePath;
    private readonly Dictionary<string, string> _last = new(StringComparer.Ordinal);
    private readonly List<string> _bases = new();
    private long _bytes;
    private int _snapshots;

    public SessionLogManifest(string sessionDirectory, string fileName = "session.txt")
    {
        _filePath = Path.Combine(sessionDirectory, fileName);
    }

    /// <summary>清单文件路径。</summary>
    public string FilePath => _filePath;

    /// <summary>已经写进清单的字节数（UTF-8）。</summary>
    public long BytesWritten => _bytes;

    /// <summary>已经记了几次快照。</summary>
    public int Snapshots => _snapshots;

    /// <summary>本次选出来的路径基（按选择顺序）。</summary>
    public IReadOnlyList<string> PathBases => _bases;

    /// <summary>把绝对路径换成 <c>@N\相对</c>；没有命中基就原样返回。</summary>
    public string Shorten(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return "?";
        }

        string best = string.Empty;
        int bestIndex = -1;
        for (int i = 0; i < _bases.Count; i++)
        {
            string prefix = _bases[i];
            if (prefix.Length <= best.Length)
            {
                continue;
            }

            if (path.Length <= prefix.Length || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            char next = path[prefix.Length];
            if (next != '\\' && next != '/')
            {
                continue;
            }

            best = prefix;
            bestIndex = i;
        }

        return bestIndex < 0 ? path : "@" + bestIndex + path[best.Length..];
    }

    /// <summary>写清单头（会先算路径基，并清空历史状态）。</summary>
    public void WriteHeader(Header header, IReadOnlyList<Module> modules, IReadOnlyList<Entry> entries)
    {
        _last.Clear();
        _snapshots = 0;

        var candidates = new List<string>(entries.Count + modules.Count + 2);
        foreach (Entry e in entries)
        {
            candidates.Add(e.SourcePath);
        }

        foreach (Module m in modules)
        {
            candidates.Add(m.Path);
        }

        _bases.Clear();
        _bases.AddRange(ChooseBases(candidates, 5));

        var sb = new StringBuilder(1024);
        sb.Append("HSH-SESSION 2\n");
        sb.Append("legend: L=日志 C=配置 @N=路径基 s=快照 id=字节 +id=新出现 miss=缺失\n");
        sb.Append("t0=").Append(header.Start.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
          .Append(header.Start.ToString("zzz", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("pid=").Append(header.Pid).Append(" exe=").Append(header.Process)
          .Append(" game=").Append(Blank(header.GameKey))
          .Append(" hub=").Append(header.AppVersion);
        if (header.Portable)
        {
            sb.Append(" portable=1");
        }

        sb.Append('\n');
        if (!string.IsNullOrWhiteSpace(header.CommandLine))
        {
            sb.Append("args=").Append(header.CommandLine.Trim()).Append('\n');
        }

        sb.Append("dir=").Append(Blank(header.DataDir)).Append('\n');
        sb.Append("log=").Append(Blank(header.LogDir)).Append('\n');
        sb.Append("sess=").Append(Blank(header.SessionDir)).Append('\n');

        foreach (Module m in modules)
        {
            sb.Append("mod ").Append(m.Label).Append(' ').Append(string.IsNullOrWhiteSpace(m.Version) ? "-" : m.Version)
              .Append(' ').Append(Shorten(m.Path)).Append('\n');
        }

        for (int i = 0; i < _bases.Count; i++)
        {
            sb.Append('@').Append(i).Append('=').Append(_bases[i]).Append('\n');
        }

        foreach (Entry e in entries)
        {
            sb.Append(e.Kind).Append(' ').Append(e.Id).Append(' ').Append(e.Name).Append(' ')
              .Append(Shorten(e.SourcePath)).Append('\n');
        }

        Flush(sb.ToString());
    }

    /// <summary>
    /// 追加一次快照。<paramref name="observations"/> 是<b>全量</b>观察结果，
    /// 本方法只写与上一次相比有变化的项。
    /// </summary>
    public void AppendSnapshot(string reason, TimeSpan elapsed, IReadOnlyList<Observation> observations)
    {
        int index = _snapshots++;
        var head = new StringBuilder(160);
        head.Append("s ").Append(index).Append(" +")
            .Append(elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture))
            .Append(' ').Append(reason);

        var row = new StringBuilder(128);
        int changes = 0;
        foreach (Observation o in observations)
        {
            string now = o.Exists ? o.Bytes.ToString(CultureInfo.InvariantCulture) : "miss";
            bool has = _last.TryGetValue(o.Id, out string? prev);
            if (has && prev == now)
            {
                continue;
            }

            _last[o.Id] = now;
            changes++;
            if (row.Length > 0)
            {
                row.Append(' ');
            }

            row.Append(o.Id).Append('=');
            if (!o.Exists)
            {
                row.Append("miss");
            }
            else
            {
                if (!has || prev == "miss")
                {
                    row.Append('+');
                }

                row.Append(now);
            }
        }

        head.Append('\n');
        if (changes > 0)
        {
            head.Append(' ').Append(row).Append('\n');
        }

        Flush(head.ToString());
    }

    /// <summary>追加一段自由文本（例如摘要索引行）。</summary>
    public void AppendSection(string title, string body)
    {
        var sb = new StringBuilder(body.Length + 32);
        sb.Append("# ").Append(title).Append('\n');
        if (!string.IsNullOrEmpty(body))
        {
            sb.Append(body);
            if (!body.EndsWith('\n'))
            {
                sb.Append('\n');
            }
        }

        Flush(sb.ToString());
    }

    private void Flush(string text)
    {
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            using var stream = new FileStream(_filePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes, 0, bytes.Length);
            _bytes += bytes.Length;
        }
        catch
        {
            // 写清单失败绝不能影响启动/收尾，调用方自己会记 Hub 日志
        }
    }

    private static string Blank(string? value) => string.IsNullOrWhiteSpace(value) ? "?" : value;

    /// <summary>
    /// 贪心挑路径基：每挑一个，覆盖到的路径就少写 <c>len(基)+1-3</c> 个字符，
    /// 代价是开头多一行 <c>@N=基</c>。收益为负就停。
    /// </summary>
    internal static List<string> ChooseBases(IEnumerable<string> paths, int maxBases)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string? dir;
            try
            {
                dir = Path.GetDirectoryName(path);
            }
            catch
            {
                continue;
            }

            string current = dir ?? string.Empty;
            for (int up = 0; up < 3 && current.Length > 3; up++)
            {
                if (!groups.TryGetValue(current, out List<string>? list))
                {
                    groups[current] = list = new List<string>();
                }

                if (!list.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(path);
                }

                current = Path.GetDirectoryName(current) ?? string.Empty;
            }
        }

        var chosen = new List<string>();
        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int pick = 0; pick < maxBases; pick++)
        {
            string? best = null;
            long bestGain = 0;
            foreach ((string dir, List<string> list) in groups)
            {
                if (chosen.Contains(dir, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                long gain = -(dir.Length + 5);
                foreach (string path in list)
                {
                    if (!covered.Contains(path))
                    {
                        gain += dir.Length + 1 - 3;
                    }
                }

                if (gain > bestGain)
                {
                    bestGain = gain;
                    best = dir;
                }
            }

            if (best is null)
            {
                break;
            }

            chosen.Add(best);
            foreach (string path in groups[best])
            {
                covered.Add(path);
            }
        }

        return chosen;
    }
}
