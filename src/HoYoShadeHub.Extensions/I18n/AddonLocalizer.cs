using HoYoShadeHub.Extensions.ReShade;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoYoShadeHub.Extensions.I18n;

public sealed class AddonI18nEntry
{
    [JsonPropertyName("en")] public string En { get; set; } = string.Empty;
    [JsonPropertyName("zh")] public string Zh { get; set; } = string.Empty;
}

/// <summary>一个插件族的翻译表（slug 用前缀匹配文件名，见 <see cref="AddonLocalizer.SelectTable"/>）</summary>
public sealed class AddonI18nTable
{
    [JsonPropertyName("slug")] public string Slug { get; set; } = string.Empty;
    [JsonPropertyName("entries")] public List<AddonI18nEntry> Entries { get; set; } = [];
}

/// <summary>catalog/i18n 文档：一张表可以带多族</summary>
public sealed class AddonI18nDocument
{
    [JsonPropertyName("tables")] public List<AddonI18nTable> Tables { get; set; } = [];
}

/// <summary>一次汉化/还原的结果</summary>
public sealed record AddonLocalizeResult(int Applied, int SkippedTooLong, int Missing, string Message, string? BackupPath)
{
    public bool Ok => Applied > 0 || Message.Length > 0;
}

/// <summary>
/// 插件（ReShade addon）汉化 = **白名单原地替换**：
///
/// <para>
/// addon 的界面是 ImGui 画的，文案是 <c>.rdata</c> 里的 UTF-8 窄字符串，没有语言机制。
/// 所以做法是：拿一张翻译表（英文原文 → 中文），在 DLL 里找 NUL 结尾的那条英文，
/// **只有中文 UTF-8 字节数 ≤ 英文**才原地覆盖（右边补空格），放不下的跳过并汇报。
/// </para>
///
/// <para>
/// 动手前先把原 DLL 备份到 <c>&lt;备份目录&gt;\&lt;文件名&gt;.bak</c>，界面上可以一键还原。
/// 长句放不下的想全量汉化，需要「加 PE 新节 + 改指针」（另一条路，见 docs/GAME-AND-INJECT.md §10）。
/// </para>
/// </summary>
public static class AddonLocalizer
{
    public const string BuiltinResourceName = "HoYoShadeHub.Extensions.Resources.i18n.builtin.json";

    public const string BackupFolderName = "i18n-backup";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>内置翻译表（资源里那份）</summary>
    public static AddonI18nDocument LoadBuiltin()
    {
        using Stream? stream = typeof(AddonLocalizer).Assembly.GetManifestResourceStream(BuiltinResourceName);
        if (stream is null)
        {
            return new AddonI18nDocument();
        }

        try
        {
            return JsonSerializer.Deserialize<AddonI18nDocument>(stream, _jsonOptions) ?? new AddonI18nDocument();
        }
        catch (JsonException)
        {
            return new AddonI18nDocument();
        }
    }

    /// <summary>读一个翻译表文件（用户/远端放这儿的 *.json）</summary>
    public static AddonI18nDocument? LoadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AddonI18nDocument>(File.ReadAllText(path), _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>把一个目录里的所有 *.json 翻译表和内置表合并（同一个 en 后面的覆盖前面的）</summary>
    public static List<AddonI18nTable> LoadTables(string? extraDirectory)
    {
        var tables = new List<AddonI18nTable>(LoadBuiltin().Tables);

        if (!string.IsNullOrWhiteSpace(extraDirectory) && Directory.Exists(extraDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(extraDirectory, "*.json"))
            {
                AddonI18nDocument? document = LoadFile(file);
                if (document is not null)
                {
                    tables.AddRange(document.Tables);
                }
            }
        }

        return tables;
    }

    /// <summary>文件名（或 slug）该用哪张表：slug 前缀匹配，取最长的那个</summary>
    public static AddonI18nTable? SelectTable(IEnumerable<AddonI18nTable> tables, string addonFileName)
    {
        string slug = AddonFileInfo.Parse(addonFileName)?.Slug ?? Path.GetFileNameWithoutExtension(addonFileName);
        AddonI18nTable? best = null;
        int bestLength = -1;

        foreach (AddonI18nTable table in tables)
        {
            if (string.IsNullOrWhiteSpace(table.Slug) || table.Entries.Count == 0)
            {
                continue;
            }

            if (slug.StartsWith(table.Slug, StringComparison.OrdinalIgnoreCase) && table.Slug.Length > bestLength)
            {
                best = table;
                bestLength = table.Slug.Length;
            }
        }

        return best;
    }

    /// <summary>备份文件路径（没有备份返回 null）。同名插件在不同目录各有各的备份。</summary>
    public static string? BackupPathOf(string dllPath, string backupDirectory)
    {
        string path = BackupFilePath(dllPath, backupDirectory);

        if (File.Exists(path))
        {
            return path;
        }

        // 兼容老版本留下的 <名字>.bak（同一个名字只有一份备份的那种）。
        // 只有「大小跟当前这份一样」才敢用 —— 同名不同版本的插件拿它还原会把用户的版本换掉（踩过一次）。
        string legacy = Path.Combine(backupDirectory, Path.GetFileName(dllPath) + ".bak");

        if (File.Exists(legacy) && new FileInfo(legacy).Length == new FileInfo(dllPath).Length)
        {
            return legacy;
        }

        return null;
    }

    /// <summary>这个副本该用的备份路径：<c>&lt;名字&gt;.&lt;路径哈希&gt;.bak</c></summary>
    public static string BackupFilePath(string dllPath, string backupDirectory)
    {
        string full = Path.GetFullPath(dllPath).ToLowerInvariant();
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(full));
        string tag = Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
        return Path.Combine(backupDirectory, $"{Path.GetFileName(dllPath)}.{tag}.bak");
    }

    /// <summary>打汉化：只替换放得下的条目（先自动备份原文件）</summary>
    public static AddonLocalizeResult Apply(
        string dllPath,
        AddonI18nTable table,
        string backupDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(dllPath))
        {
            return new AddonLocalizeResult(0, 0, 0, "找不到这个插件文件。", null);
        }

        byte[] bytes = File.ReadAllBytes(dllPath);
        List<(int Start, int End)> codeRanges = PeImage.ExecutableRanges(bytes);
        List<(int Position, int Size, int Disp, bool HasDisp)> immediates = codeRanges.Count == 0
            ? []
            : [.. EnumerateEntries(bytes, codeRanges)];
        // 代码立即数先「全局分配」（每个立即数只归一条串，整条串优先），再逐条处理字面量 ——
        // 顺序反了的话，两条串会抢同一段立即数，把中文写成半个字符。
        int[] codeHit = PatchCodeImmediatesAll(bytes, table.Entries, immediates);
        int applied = 0;
        int tooLong = 0;
        int missing = 0;

        for (int index = 0; index < table.Entries.Count; index++)
        {
            AddonI18nEntry entry = table.Entries[index];
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(entry.En) || string.IsNullOrWhiteSpace(entry.Zh))
            {
                continue;
            }

            byte[] source = Encoding.UTF8.GetBytes(entry.En);
            byte[] target = Encoding.UTF8.GetBytes(entry.Zh);

            List<int> hits = FindLiteral(bytes, source);
            bool inCode = codeHit[index] > 0;

            if (hits.Count == 0 && !inCode)
            {
                missing++;
                continue;
            }

            if (target.Length > source.Length)
            {
                // 中文比英文长，原地放不下 —— 跳过（全量汉化要另加 PE 节，见类注释）
                tooLong++;
                continue;
            }

            int changed = 0;

            foreach (int position in hits)
            {
                Array.Copy(target, 0, bytes, position, target.Length);
                for (int i = position + target.Length; i < position + source.Length; i++)
                {
                    bytes[i] = 0x20;   // 右边补空格，保持原长度（NUL 不动）
                }

                changed++;
            }

            if (changed == 0)
            {
                missing++;
                continue;
            }

            applied++;
        }

        string? backupPath = null;

        if (applied > 0)
        {
            Directory.CreateDirectory(backupDirectory);
            backupPath = BackupFilePath(dllPath, backupDirectory);

            // 只在第一次打汉化时备份（保住最原始那份）
            if (!File.Exists(backupPath))
            {
                File.Copy(dllPath, backupPath, overwrite: false);
            }

            string temp = dllPath + ".hysx-tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, dllPath, overwrite: true);
        }

        string message = applied == 0
            ? $"没替换任何条目（放不下 {tooLong} 条 / 文件里没有 {missing} 条）。"
            : $"已汉化 {applied} 条（放不下跳过 {tooLong} 条，DLL 里没有 {missing} 条）。";

        return new AddonLocalizeResult(applied, tooLong, missing, message, backupPath);
    }

    /// <summary>还原成备份那份</summary>
    public static AddonLocalizeResult Restore(string dllPath, string backupDirectory)
    {
        string? backupPath = BackupPathOf(dllPath, backupDirectory);
        if (backupPath is null)
        {
            return new AddonLocalizeResult(0, 0, 0, "没有这个插件的备份，没法还原。", null);
        }

        File.Copy(backupPath, dllPath, overwrite: true);
        return new AddonLocalizeResult(0, 0, 0, "已还原成备份里的原版。", backupPath);
    }

    /// <summary>
    /// 很多短标签（Render / Upscaled / Model A …）根本不是字符串 —— 编译器把它们变成
    /// <c>mov rax, imm64</c> 这类立即数写在代码里，运行时在栈上拼出来（实测 48 B8 "Upscaled"）。
    /// 只改 .rdata 的话这些标签永远是英文；长句还会变成「中文头 + 英文尾」
    /// （头从 .rdata 读，尾就是这个立即数）。这里把「立即数正好等于某条英文串的一段」的也一起换。
    /// </summary>
    private static int PatchCodeImmediatesLegacy(
        byte[] bytes,
        byte[] source,
        byte[] target,
        IReadOnlyList<(int Position, int Size)> immediates,
        HashSet<int>? alreadyPatched = null)
    {
        int patched = 0;
        List<(int Position, int Window, int Length)> applied = [];

        foreach ((int position, int size) in immediates)
        {
            // 单字节 store 留给下面那一步（单独认太容易误伤别的常量）；已经被别的条目改过的也不要动
            if (size < 2 || (alreadyPatched is not null && alreadyPatched.Contains(position)))
            {
                continue;
            }

            bool found = false;

            // 从长到短试：整段（length == size）最稳；尾巴匹配（接到串尾）至少 4 字节，
            // 否则 "Upsample Filter" 尾巴那 3 个字节 "ter" 会把别的串的 "ter Mask" 咬掉一半
            // （实测把「角色遮罩」写成了「角色＋9C＋空格＋Mask」）。
            for (int length = size; length >= 1 && !found; length--)
            {
                if (length != size && length < 4)
                {
                    break;
                }

                for (int window = 0; window + length <= source.Length && !found; window++)
                {
                    // 比立即数短 → 只能是「接到串尾」那一段（后面跟的是补零，不能动）
                    if (length < size && window + length != source.Length)
                    {
                        continue;
                    }

                    // 短的立即数（< 8 字节）必须「有根」：这条串的前一段就在前 96 字节内刚被改过。
                    // 否则 "nt" 这种两字节常量会被别的串抢走 —— 实测 "Present temporal staging" 的目标里
                    // 也有 "nt"，它先把 imm16 记账了（写进去的还是 "nt"，字节没变），后面的 "Pass Count" 就被跳过。
                    if (length < 8 && !Anchored(applied, position))
                    {
                        continue;
                    }

                    bool match = true;

                    for (int k = 0; k < length; k++)
                    {
                        if (bytes[position + k] != source[window + k])
                        {
                            match = false;
                            break;
                        }
                    }

                    if (!match)
                    {
                        continue;
                    }

                    bool changed = false;

                    for (int k = 0; k < length; k++)
                    {
                        byte value = window + k < target.Length ? target[window + k] : (byte)0x20;

                        if (bytes[position + k] != value)
                        {
                            changed = true;
                        }

                        bytes[position + k] = value;
                    }

                    applied.Add((position, window, length));

                    if (changed)
                    {
                        alreadyPatched?.Add(position);
                        patched++;
                    }

                    found = true;
                }
            }
        }

        // 尾巴常常是「mov byte ptr [rbp+X], 单个字符」。单字节没法单独认（任何常量都像），
        // 只认紧接着刚改过那一段、而且字节正好是这条串下一个字节的那种 store。
        // 注意：这里**故意不做**「单字节 store 链」（原来会去附近找 C6 45 xx 那种单字符 store）。
        // 那个启发式只看「字节值对不对得上」，可能认到别的串的 store 上，偏移错一位就会把中文切成
        // 半个 UTF-8 字符 —— 用户界面上就会显示出「?握采」这种怪字（实测）。宁可少数一两个单字节尾巴，
        // 也不要写坏别的字符串。

        return patched;
    }

    /// <summary>
    /// 一次性、全局地把「代码立即数」分给各条目 —— 每个立即数只归一条串。
    ///
    /// 为什么必须全局：两条串会抢同一段立即数。实测那条 8 字节立即数本来存的是 <c>"Scaling "</c>，
    /// 表里更长的 <c>"Scaling Domain"</c> 先把 <c>缩放空间</c> 的前 8 字节写了进去，于是 <c>Scaling</c>
    /// 这个标签的最后一个字符被切成半个 UTF-8，界面上显示成「?握采」这种怪字。
    ///
    /// 分配优先级：<b>整条串都在这一个立即数里</b> &gt; <b>完整的 8/4 字节块</b> &gt;
    /// <b>接在串尾的短块</b>（还要求同一条串的前一段就在前 96 字节内）。
    /// 返回每个条目改了几处。
    /// </summary>
    /// <summary>
    /// 一次性、全局地把「代码立即数」分给各条目 —— 每个立即数只归一条串。
    ///
    /// 为什么必须全局：两条串会抢同一段立即数。实测 <c>"Scaling " + "g Domain"</c> 两条立即数
    /// 拼出的是 "Scaling Domain"，若被短的 <c>Scaling</c> 抢走一块，重叠区就对不上、
    /// 中文被切成半个 UTF-8，界面上显示成「?握采」这种怪字。
    ///
    /// 轮次 = 优先级：0「长串的一块」&gt; 1「整条串就在这一个立即数里」&gt; 2「接在串尾的短块」。
    /// 最后再用**位移**把单字节尾巴（mov byte ptr [rbp+X], 'x'）精确接上。
    /// </summary>
    private static int[] PatchCodeImmediatesAll(
        byte[] bytes,
        IReadOnlyList<AddonI18nEntry> entries,
        List<(int Position, int Size, int Disp, bool HasDisp)> immediates)
    {
        int[] patched = new int[entries.Count];
        HashSet<int> assigned = [];
        List<(int Entry, int Position, int Window, int Length, int Disp, bool HasDisp)> applied = [];
        byte[]?[] sources = new byte[]?[entries.Count];
        byte[]?[] targets = new byte[]?[entries.Count];

        for (int index = 0; index < entries.Count; index++)
        {
            AddonI18nEntry entry = entries[index];

            if (string.IsNullOrWhiteSpace(entry.En) || string.IsNullOrWhiteSpace(entry.Zh))
            {
                continue;
            }

            byte[] source = Encoding.UTF8.GetBytes(entry.En);
            byte[] target = Encoding.UTF8.GetBytes(entry.Zh);

            if (target.Length > source.Length)
            {
                continue;
            }

            sources[index] = source;
            targets[index] = target;
        }

        for (int round = 0; round <= 2; round++)
        {
            for (int index = 0; index < entries.Count; index++)
            {
                if (sources[index] is null || targets[index] is null)
                {
                    continue;
                }

                byte[] source = sources[index]!;
                byte[] target = targets[index]!;

                foreach ((int position, int size, int disp, bool hasDisp) in immediates)
                {
                    if (size < 2 || assigned.Contains(position))
                    {
                        continue;
                    }

                    bool done = false;

                    for (int window = 0; window < source.Length && !done; window++)
                    {
                        int length = Math.Min(size, source.Length - window);

                        if (length < 2 || !Matches(bytes, position, source, window, length))
                        {
                            continue;
                        }

                        bool whole = window == 0 && source.Length <= size
                            && IsPadding(bytes, position + source.Length, size - source.Length);
                        bool tail = length < size && window + length == source.Length;
                        int wanted = length == size ? 0 : whole ? 1 : tail ? 2 : -1;

                        // 少于 4 字节的「半截」太弱，不要
                        if (wanted < 0 || (wanted == 2 && length < 4))
                        {
                            continue;
                        }

                        if (wanted != round)
                        {
                            continue;
                        }

                        // 短的立即数（< 8）要「有根」：同一条串的前一段就在附近刚被改过。
                        // 但**串首那一块**（window == 0）不需要锚 —— 很多短标签就是 4 字节一块拼的。
                        if (length < 8 && window != 0 && !AnchoredFor(applied, index, position))
                        {
                            continue;
                        }

                        for (int k = 0; k < length; k++)
                        {
                            bytes[position + k] = window + k < target.Length ? target[window + k] : (byte)0x20;
                        }

                        assigned.Add(position);
                        applied.Add((index, position, window, length, disp, hasDisp));
                        patched[index]++;
                        done = true;
                    }
                }
            }
        }

        // 尾巴常是「mov byte ptr [rbp+X], 单个字符」一个个写的。单字节没法单独认，
        // 但**位移**能精确对上：某块的位移 0x80、窗口 0、长度 8 → 下一字节就在位移 0x88。
        // 早期版本靠「附近找一个字节对得上的 store」，会认到别的串上、偏移错一位就把中文写坏，所以现在只认位移。
        Dictionary<int, List<(int Position, byte Value)>> byteStores = [];

        foreach ((int position, int size, int disp, bool hasDisp) in immediates)
        {
            if (size != 1 || !hasDisp || assigned.Contains(position))
            {
                continue;
            }

            if (!byteStores.TryGetValue(disp, out List<(int Position, byte Value)>? list))
            {
                list = [];
                byteStores[disp] = list;
            }

            list.Add((position, bytes[position]));
        }

        foreach ((int entryIndex, int position, int window, int length, int disp, bool hasDisp) in applied)
        {
            if (!hasDisp || sources[entryIndex] is null || targets[entryIndex] is null)
            {
                continue;
            }

            byte[] source = sources[entryIndex]!;
            byte[] target = targets[entryIndex]!;
            int next = window + length;
            int wantDisp = disp + length;

            while (next < source.Length && byteStores.TryGetValue(wantDisp, out List<(int Position, byte Value)>? list))
            {
                int store = -1;

                foreach ((int storePosition, byte value) in list)
                {
                    if (value == source[next] && !assigned.Contains(storePosition))
                    {
                        store = storePosition;
                        break;
                    }
                }

                if (store < 0)
                {
                    break;
                }

                bytes[store] = next < target.Length ? target[next] : (byte)0x20;
                assigned.Add(store);
                patched[entryIndex]++;
                next++;
                wantDisp++;
            }
        }

        return patched;
    }

    /// <summary>测试用入口：只跑一条串（不带位移信息，单字节尾巴那一步不会触发）</summary>
    public static int PatchCodeImmediates(
        byte[] bytes,
        byte[] source,
        byte[] target,
        IReadOnlyList<(int Position, int Size)> immediates,
        HashSet<int>? alreadyPatched = null)
    {
        AddonI18nEntry entry = new() { En = Encoding.UTF8.GetString(source), Zh = Encoding.UTF8.GetString(target) };
        List<(int Position, int Size, int Disp, bool HasDisp)> rich = [];

        foreach ((int position, int size) in immediates)
        {
            rich.Add((position, size, 0, false));
        }

        return PatchCodeImmediatesAll(bytes, [entry], rich)[0];
    }

    /// <summary>测试用入口：带位移的立即数（用来测单字节尾巴那一步）</summary>
    public static int PatchCodeImmediatesWithDisp(
        byte[] bytes,
        byte[] source,
        byte[] target,
        List<(int Position, int Size, int Disp, bool HasDisp)> immediates)
    {
        AddonI18nEntry entry = new() { En = Encoding.UTF8.GetString(source), Zh = Encoding.UTF8.GetString(target) };
        return PatchCodeImmediatesAll(bytes, [entry], immediates)[0];
    }

    /// <summary>测试用：按 PE 头枚举带位移的立即数</summary>
    public static List<(int Position, int Size, int Disp, bool HasDisp)> EnumerateEntriesWithDisp(byte[] bytes)
    {
        List<(int Position, int Size, int Disp, bool HasDisp)> list = [];

        foreach ((int position, int size, int disp, bool hasDisp) in EnumerateEntries(bytes, PeImage.ExecutableRanges(bytes)))
        {
            list.Add((position, size, disp, hasDisp));
        }

        return list;
    }

    /// <summary>锚定：同一条串的前一段就在前 96 字节内刚被改过</summary>
    private static bool Matches(byte[] bytes, int position, byte[] source, int window, int length)
    {
        for (int k = 0; k < length; k++)
        {
            if (bytes[position + k] != source[window + k])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 立即数里字符串之后的那些字节是不是**补零**（真正的串尾）。
    /// 只认 0x00，不认空格 —— 实测 <c>"Scaling "</c> 末尾那个空格是它后面还接着
    /// <c>"g Domain"</c>（两条立即数拼出 "Scaling Domain"），把它当"整条串结束"就会写错。
    /// </summary>
    private static bool IsPadding(byte[] bytes, int offset, int count)
    {
        for (int k = 0; k < count; k++)
        {
            if (offset + k >= bytes.Length || bytes[offset + k] != 0x00)
            {
                return false;
            }
        }

        return true;
    }

    private static bool AnchoredFor(
        List<(int Entry, int Position, int Window, int Length, int Disp, bool HasDisp)> applied,
        int entry,
        int position)
    {
        foreach ((int index, int appliedPosition, int window, int length, int disp, bool hasDisp) in applied)
        {
            // 距离放宽到 512 字节：编译器不一定把一条串的所有块挨着放（实测尾巴可能在几百字节外）
            if (index == entry && position > appliedPosition && position - appliedPosition <= 512)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>这份文件里有没有「就是这条英文串的一段」的代码立即数</summary>
    public static bool HasCodeImmediate(byte[] bytes, byte[] source, IReadOnlyList<(int Position, int Size)> immediates)
    {
        foreach ((int position, int size) in immediates)
        {
            if (size < 2)
            {
                continue;
            }

            for (int window = 0; window < source.Length; window++)
            {
                int length = Math.Min(size, source.Length - window);

                if (length < 2 || (length < size && window + length != source.Length))
                {
                    continue;
                }

                bool match = true;

                for (int k = 0; k < length; k++)
                {
                    if (bytes[position + k] != source[window + k])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>把 .text 里常见的「字符串立即数」位置都列出来（mov r64/r32, imm64/imm32；mov [栈], imm）</summary>
    /// <summary>诊断用：按 PE 头找出这份文件里所有「写常量」的立即数</summary>
    public static List<(int Position, int Size)> EnumerateImmediates(byte[] bytes)
    {
        List<(int Position, int Size)> list = [];

        foreach ((int position, int size) in EnumerateImmediates(bytes, PeImage.ExecutableRanges(bytes)))
        {
            list.Add((position, size));
        }

        return list;
    }

    public static IEnumerable<(int Position, int Size)> EnumerateImmediates(byte[] bytes, IReadOnlyList<(int Start, int End)> codeRanges)
    {
        foreach ((int position, int size, int disp, bool hasDisp) in EnumerateEntries(bytes, codeRanges))
        {
            yield return (position, size);
        }
    }

    /// <summary>同上，但多带「存到哪里」（位移）—— 用来安全地把单字节尾巴接上去</summary>
    private static IEnumerable<(int Position, int Size, int Disp, bool HasDisp)> EnumerateEntries(byte[] bytes, IReadOnlyList<(int Start, int End)> codeRanges)
    {
        int index = 0;

        while (index < bytes.Length)
        {
            if (!InCode(codeRanges, index)
                || !TryParseImmediate(bytes, index, out int position, out int size, out int length, out int disp, out bool hasDisp))
            {
                index++;
                continue;
            }

            // 寄存器形式的立即数（mov rax, imm64）本身没有位移 —— 位移在紧跟其后的那条 store 上
            // （48 89 86 <disp32> = mov [rsi+disp32], rax）。看一眼下一条，能补就补上，
            // 这样「单字节尾巴」那一步才能用位移精确对上。
            if (!hasDisp && TryReadStoreDisp(bytes, index + length, out int storeDisp))
            {
                disp = storeDisp;
                hasDisp = true;
            }

            yield return (position, size, disp, hasDisp);
            index += length;   // 跳过整条指令，否则 48 B8 的第二个字节 B8 会被当成 mov eax, imm32
        }
    }

    /// <summary>
    /// 认「把常量写进去」的指令，返回立即数在文件里的位置和长度。
    /// 手写模式表认不全 —— 这个编译器爱用 <c>41 C7 84 24 &lt;disp32&gt; &lt;imm32&gt;</c> 这种带 REX+SIB
    /// 的形式往 r12+偏移 里写短尾巴，所以这里直接解 ModRM / SIB：
    /// <c>B8+r imm32/imm64</c>、<c>C6 /0 imm8</c>、<c>C7 /0 imm16/imm32</c>（带 66 / REX.W / 任意 ModRM+SIB+位移）。
    /// </summary>
    /// <summary>紧跟一条 <c>mov r64, imm64</c> 之后的 store 指令（<c>48 89 86 disp32</c>）里的位移</summary>
    private static bool TryReadStoreDisp(byte[] bytes, int start, out int disp)
    {
        disp = 0;
        int i = start;

        if (i < bytes.Length && bytes[i] >= 0x40 && bytes[i] <= 0x4F)
        {
            i++;
        }

        if (i + 1 >= bytes.Length || bytes[i] != 0x89)
        {
            return false;
        }

        i++;
        byte modrm = bytes[i];
        int mod = modrm >> 6;
        int rm = modrm & 0x07;
        int cursor = i + 1;

        if (mod == 3)
        {
            return false;
        }

        if (rm == 4)
        {
            if (cursor >= bytes.Length)
            {
                return false;
            }

            byte sib = bytes[cursor++];
            int baseField = sib & 0x07;

            if (mod == 1)
            {
                if (cursor >= bytes.Length) { return false; }

                disp = (sbyte)bytes[cursor];
                return true;
            }

            if (mod == 2)
            {
                if (cursor + 4 > bytes.Length) { return false; }

                disp = BitConverter.ToInt32(bytes, cursor);
                return true;
            }

            return baseField != 5;
        }

        if (mod == 1)
        {
            if (cursor >= bytes.Length) { return false; }

            disp = (sbyte)bytes[cursor];
            return true;
        }

        if (mod == 2)
        {
            if (cursor + 4 > bytes.Length) { return false; }

            disp = BitConverter.ToInt32(bytes, cursor);
            return true;
        }

        return rm != 5;
    }

    private static bool TryParseImmediate(
        byte[] bytes,
        int start,
        out int position,
        out int size,
        out int length,
        out int disp,
        out bool hasDisp)
    {
        position = 0;
        size = 0;
        length = 0;
        disp = 0;
        hasDisp = false;

        int i = start;
        bool operandSize16 = false;

        if (i < bytes.Length && bytes[i] == 0x66)
        {
            operandSize16 = true;
            i++;
        }

        bool rexW = false;

        if (i < bytes.Length && bytes[i] >= 0x40 && bytes[i] <= 0x4F)
        {
            rexW = (bytes[i] & 0x08) != 0;
            i++;
        }

        if (i >= bytes.Length)
        {
            return false;
        }

        byte op = bytes[i];

        // mov r32 / r64, imm
        if (op >= 0xB8 && op <= 0xBF)
        {
            int movSize = rexW ? 8 : operandSize16 ? 2 : 4;
            int movAt = i + 1;

            if (movAt + movSize > bytes.Length)
            {
                return false;
            }

            position = movAt;
            size = movSize;
            length = movAt + movSize - start;
            return true;
        }

        if (op != 0xC6 && op != 0xC7)
        {
            return false;
        }

        int immSize = op == 0xC6 ? 1 : operandSize16 ? 2 : 4;
        int cursor = i + 1;

        if (cursor >= bytes.Length)
        {
            return false;
        }

        byte modrm = bytes[cursor++];
        int mod = modrm >> 6;
        int reg = (modrm >> 3) & 0x07;
        int rm = modrm & 0x07;

        if (reg != 0)
        {
            return false;   // 只认 /0（mov），别的组不是「往这里写常量」
        }

        int dispValue = 0;
        bool dispKnown = mod != 3;

        if (mod != 3 && rm == 4)
        {
            if (cursor >= bytes.Length)
            {
                return false;
            }

            byte sib = bytes[cursor++];
            int baseField = sib & 0x07;

            if (mod == 1)
            {
                if (cursor >= bytes.Length) { return false; }

                dispValue = (sbyte)bytes[cursor++];
            }
            else if (mod == 2)
            {
                if (cursor + 4 > bytes.Length) { return false; }

                dispValue = BitConverter.ToInt32(bytes, cursor);
                cursor += 4;
            }
            else if (baseField == 5)
            {
                dispKnown = false;   // [disp32] 绝对地址
                cursor += 4;
            }
        }
        else if (mod == 3)
        {
            dispKnown = false;       // 目标是寄存器
        }
        else if (mod == 1)
        {
            if (cursor >= bytes.Length) { return false; }

            dispValue = (sbyte)bytes[cursor++];
        }
        else if (mod == 2)
        {
            if (cursor + 4 > bytes.Length) { return false; }

            dispValue = BitConverter.ToInt32(bytes, cursor);
            cursor += 4;
        }
        else if (rm == 5)
        {
            dispKnown = false;       // RIP 相对
            cursor += 4;
        }

        if (cursor + immSize > bytes.Length)
        {
            return false;
        }

        position = cursor;
        size = immSize;
        length = cursor + immSize - start;
        disp = dispValue;
        hasDisp = dispKnown;
        return true;
    }
    /// <summary>尾巴匹配锚定：同一个立即数列表里，前面 96 字节内已经有这条串改过的段</summary>
    private static bool Anchored(List<(int Position, int Window, int Length)> applied, int position)
    {
        foreach ((int Position, int Window, int Length) done in applied)
        {
            if (position > done.Position && position - done.Position <= 96)
            {
                return true;
            }
        }

        return false;
    }

    private static bool InCode(IReadOnlyList<(int Start, int End)> ranges, int offset)
    {
        for (int i = 0; i < ranges.Count; i++)
        {
            if (offset >= ranges[i].Start && offset < ranges[i].End)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>这个字节算不算「文本的一部分」（用来判断 needle 是不是落在某个更长字符串的内部）</summary>
    private static bool IsTextByte(byte value)
        => value == 0x20 || (value >= 0x21 && value <= 0x7E) || value >= 0x80 || (value >= 0x09 && value <= 0x0D);

    /// <summary>找完整的 NUL 结尾字符串（前面是 NUL 或文件开头），避免命中更长字符串的后半截</summary>
    public static List<int> FindLiteral(byte[] haystack, byte[] needle)
    {
        var hits = new List<int>();

        if (needle.Length == 0 || needle.Length > haystack.Length)
        {
            return hits;
        }

        for (int i = 0; i <= haystack.Length - needle.Length - 1; i++)
        {
            if (haystack[i + needle.Length] != 0)
            {
                continue;
            }

            // 只认「完整的一条字符串」：前面必须是 NUL（或文件开头）。
            // 以前的写法只排除「前面是 0x21-0x7E 的可打印字符」，空格(0x20) 不算 —— 于是
            // 更长的字符串 "Open Neural Rendering Debug" 里的 "Neural Rendering Debug"
            // （前面正好是空格）也会被命中，替换完就变成 "Open 神经渲染调试" 这种中英混排。
            if (i > 0 && IsTextByte(haystack[i - 1]))
            {
                continue;
            }

            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                hits.Add(i);
                i += needle.Length - 1;
            }
        }

        return hits;
    }
}
