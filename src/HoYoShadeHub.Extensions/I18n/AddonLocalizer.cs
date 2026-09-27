using HoYoShadeHub.Extensions.ReShade;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoYoShadeHub.Extensions.I18n;

public sealed class AddonI18nEntry
{
    [JsonPropertyName("en")] public string En { get; set; } = string.Empty;
    [JsonPropertyName("zh")] public string Zh { get; set; } = string.Empty;

    /// <summary>
    /// 只当「共享常量」判据、**不翻译**这一条。实测 <c>Model A/B/C</c> 的常量（<c>"Mode"</c>、<c>"el A"</c>）
    /// 跟没法翻译的 <c>Model</c> 标签（5 字节装不下「模型」）共用同一份立即数：翻它们，
    /// 标签那行就变成「?式l」。所以这几条留在表里、标 guard，用来挡住别的串把这份常量当尾巴写。
    /// </summary>
    [JsonPropertyName("guard")] public bool Guard { get; set; }
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
/// addon 的界面是 ImGui 画的、没有语言机制（<c>renodx-dlss</c> 连 <c>UiLanguage</c> 都没有；
/// <c>renodx-dlss5</c> 自带多语言，那种插件**不该**走这儿 —— 直接写
/// <c>[RenoDX.DLSS5] UiLanguage=zh-Hans</c> 就行）。所以做法是：拿一张翻译表（英文原文 → 中文），
/// 在 DLL 里**整条覆盖式**原地替换：
/// <list type="bullet">
/// <item>NUL 结尾的完整字面量（<c>.rdata</c> 里的标签表）—— 换成中文，右边补空格；</item>
/// <item>代码立即数拼出来的短标签（<c>mov rax, "Upscaled"</c>）—— 一条串的所有片段都凑齐了才写；
/// 少一段就整条不写（半截中文 + 英文尾巴 = 界面上的「缩ling」「结构强度 sity」怪字，
/// 见 <see cref="PatchCodeImmediatesAll"/>）。</item>
/// <item>运行时**覆盖写的尾巴**：有些标签的头是字面量（编译器用 SSE 16 字节拷贝搬过去），尾巴却是紧接着的
/// 一条短立即数（<c>mov word [rbx+0x10], "y\0"</c>）—— 它会把我们补的空格盖回英文，界面就是
/// 「总体强度 y」「结构强度 sity」。所以头汉化之后，盖到串尾的片段也要按同一套偏移写掉。</item>
/// </list>
/// 只有中文 UTF-8 字节数 ≤ 英文才换得动，放不下的跳过并汇报。
/// </para>
///
/// <para>
/// 动手前先把原 DLL 备份到 <c>&lt;备份目录&gt;\&lt;名字&gt;.&lt;路径哈希&gt;.bak</c>，界面上可以一键还原。
/// 长句放不下的想全量汉化，需要「加 PE 新节 + 改指针」（另一条路，见 docs/GAME-AND-INJECT.md §10）。
/// </para>
/// </summary>
public static class AddonLocalizer
{
    public const string BuiltinResourceName = "HoYoShadeHub.Extensions.Resources.i18n.builtin.json";

    /// <summary>
    /// 补丁算法版本。改动替换规则时必须 +1 —— 记账文件里记的版本比它小，就说明磁盘上那份是旧算法
    /// 打过的（可能已经被写坏），要先从备份还原、再按新算法重打（见 AddonLocalizationJob）。
    /// </summary>
    public const int AlgorithmVersion = 5;

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

            if (string.IsNullOrWhiteSpace(entry.En) || string.IsNullOrWhiteSpace(entry.Zh) || entry.Guard)
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

            // 立即数那边是「整条盖上」才写、而且已经写进 bytes 了，这里必须一起算 ——
            // 老版本漏了这一步：只走立即数的条目会算成「没替换」，于是 applied 停在 0、整个文件根本不落盘。
            if (inCode)
            {
                changed += codeHit[index];
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
    /// 把「代码立即数」里的标签换成中文 —— 一条串要么**整条都盖上**，要么一个字节都不动。
    ///
    /// <para>
    /// 这个插件里很多标签不是一整条字符串，而是编译器把片段写成立即数、运行时按位移拼到同一块缓冲上：
    /// <c>mov dword [rsi+0xA3], "Scal"</c> + <c>mov dword [rsi+0xA7], "ling"</c> 拼出 <c>Scaling</c>；
    /// <c>mov rax, "Scaling "</c> + <c>mov rax, "g Domain"</c> 拼出 <c>Scaling Domain</c>（两段还重叠）。
    /// 所以判据不是「某个片段等于英文」，而是**这条英文的每个字节都被某个片段盖住**：
    /// 片段按「在英文里的偏移」排序，偏移必须从 0 起首尾相接（允许重叠），最后正好盖到串尾。
    /// </para>
    ///
    /// <para>
    /// 盖不满就整条不写。老版本「能写多少写多少」，中文被切成半个 UTF-8、后面还留着英文尾巴，
    /// 界面上就是「缩ling」「结构强度 sity」「上采?镜」「使用曝光值 ue」这种怪字（用户截图报过）。
    /// 每个片段写的是**目标串同一个偏移上的字节**；目标串在那一段已经结束就补空格（尾随空格看不见）。
    /// </para>
    ///
    /// <para>
    /// 长串优先：更长的条目先挑片段，免得短条目（<c>Scaling</c>）把长条目（<c>Scaling Domain</c>）
    /// 的片段抢走；每个立即数只归一条串（<c>assigned</c>）。同一条串在界面里出现多次时，
    /// 每凑齐一整套片段就写一处，然后接着找下一处。
    /// </para>
    /// </summary>
    private static int[] PatchCodeImmediatesAll(
        byte[] bytes,
        IReadOnlyList<AddonI18nEntry> entries,
        List<(int Position, int Size, int Disp, bool HasDisp)> immediates,
        bool requireDisp = true)
    {
        int[] patched = new int[entries.Count];

        // 只挑「中文放得下」的条目，并按英文长度从长到短分配片段
        List<(int Index, byte[] Source, byte[] Target)> candidates = [];
        for (int index = 0; index < entries.Count; index++)
        {
            AddonI18nEntry entry = entries[index];
            if (string.IsNullOrWhiteSpace(entry.En) || string.IsNullOrWhiteSpace(entry.Zh) || entry.Guard)
            {
                continue;
            }

            byte[] source = Encoding.UTF8.GetBytes(entry.En);
            byte[] target = Encoding.UTF8.GetBytes(entry.Zh);
            if (target.Length <= source.Length)
            {
                candidates.Add((index, source, target));
            }
        }

        candidates.Sort((a, b) => b.Source.Length.CompareTo(a.Source.Length));

        HashSet<int> assigned = [];
        foreach ((int index, byte[] source, byte[] target) in candidates)
        {
            while (FindChain(bytes, source, immediates, assigned, requireDisp) is { } chain)
            {
                foreach ((int position, int size, int window, _, _) in chain)
                {
                    for (int k = 0; k < size; k++)
                    {
                        int offset = window + k;
                        if (offset >= source.Length)
                        {
                            // 立即数尾部那些**补零**（mov rdx, "Mode\0\0\0\0"）是串的终止符，不能动
                            break;
                        }

                        bytes[position + k] = offset < target.Length ? target[offset] : (byte)0x20;
                    }

                    assigned.Add(position);
                }

                // 记「写掉了几个片段」而不是「几处界面」：诊断和自测都按片段数看更直观
                patched[index] += chain.Count;
            }
        }

        // 第二轮：运行时**覆盖写**的尾巴。
        //
        // 实测（用户截图 + 真文件对照）：有些标签的头是 .rdata 字面量（编译器用 16 字节 SSE 拷贝搬过去），
        // 尾巴却是一条紧接着的短立即数——Overall Intensity 的 `mov word [rbx+0x10], "y\0"`、
        // Structure Intensity 的 `mov dword [rbx+0xF], "sity"`、Skin Structure Strength 的
        // `mov [rbx+0x1E], "Strength"`。它们运行时**又写一遍尾巴**，把字面量那份里我们补的空格盖回英文，
        // 界面就是「总体强度 y」「结构强度 sity」「皮肤结构强Strength」。
        //
        // 所以：只要这条串的头已经汉化（整条链命中，或者文件里有 NUL 结尾的完整字面量），
        // 就把所有「盖到串尾」的片段按**目标串同一套偏移**写掉（目标串已经结束就补空格，NUL 不动）。
        // 所有条目的英文（不管中文放不放得下）——「共享常量」判据要用：一个片段如果正好是**别的条目**
        // 英文的开头，就可能是那一条的头部写入，不能拿它当尾巴写（实测 "Mode" 是 "Model A/B/C" 的头）
        List<byte[]> allSources = [];
        foreach (AddonI18nEntry entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.En))
            {
                allSources.Add(Encoding.UTF8.GetBytes(entry.En));
            }
        }

        foreach ((int index, byte[] source, byte[] target) in candidates)
        {
            if (patched[index] == 0 && FindLiteral(bytes, source).Count == 0)
            {
                continue;
            }

            PatchTailPieces(bytes, source, target, immediates, allSources, assigned, requireDisp, ref patched[index]);
        }

        return patched;
    }

    /// <summary>
    /// 把「运行时覆盖写尾巴」的片段按目标串的偏移写掉（见 <see cref="PatchCodeImmediatesAll"/> 第二轮）。
    /// 只认盖到串尾的片段：<c>窗口 + 长度 &gt;= 英文长度</c>；比英文长出来的字节必须是补零。
    ///
    /// <para>
    /// 这类片段天生有歧义：同一个短常量在别的函数里还有副本。实测 <c>"sity"</c> 有两份 —— <c>disp 15</c>
    /// 那份是 <c>Structure Intensity</c> 的、<c>disp 16</c> 那份是 <c>Local Tone Intensity</c> 的
    /// （证据是 v3：「全写」时两条串都去写这两份，先写的 Local Tone 把两份都占成自己的字节，
    /// 界面上坏的正是 Structure 那行）。所以这里**一条串只写一处**：先挑**别的条目都盖不到**的那份
    /// （唯一归属，最可信），都被共用时挑 <c>|disp - 窗口|</c> 最小的那份 —— 这个差值就是该指令里缓冲
    /// 相对基址寄存器的偏移，越接近 0 越说明它写的就是串头那块缓冲。挑中的那份要是已经归了别人，
    /// 就干脆不写（退而求其次会写到别人的缓冲上，实测出「结构强度 ??」）。
    /// </para>
    ///
    /// <para>
    /// 另有一类片段**必须放过**：它同时是**别的条目**英文的**开头**。实测 <c>"Mode"</c> 在文件里有 6 份，
    /// 其中 3 份是 <c>Model A/B/C</c> 的头，写成中文界面立刻变「????A」「?式l」
    /// （见 <see cref="IsSharedConstant"/>）。
    /// </para>
    /// </summary>
    private static void PatchTailPieces(
        byte[] bytes,
        byte[] source,
        byte[] target,
        List<(int Position, int Size, int Disp, bool HasDisp)> immediates,
        IReadOnlyList<byte[]> allSources,
        HashSet<int> assigned,
        bool requireDisp,
        ref int written)
    {
        (int Position, int Size, int Window) best = default;
        int bestSkew = int.MaxValue;
        int bestLength = -1;
        bool bestUnique = false;
        bool found = false;

        foreach ((int position, int size, int disp, bool hasDisp) in immediates)
        {
            // 这里**不看 assigned**：挑中别人的那份就不再退而求其次（见上面说明）。
            if (size < 2 || (requireDisp && !hasDisp)
                || IsSharedConstant(bytes, position, size, source, allSources))
            {
                continue;
            }

            for (int window = 0; window < source.Length; window++)
            {
                // 只认「盖到串尾」的片段：它就是运行时那条覆盖写
                if (window + size < source.Length)
                {
                    continue;
                }

                int length = Math.Min(size, source.Length - window);
                if (length < 1 || !Matches(bytes, position, source, window, length))
                {
                    continue;
                }

                if (window + size > source.Length && !IsPadding(bytes, position + length, size - length))
                {
                    continue;
                }

                if (length < 2 && !IsPadding(bytes, position + length, size - length))
                {
                    continue;
                }

                bool unique = !IsSharedTail(bytes, position, size, source, allSources);
                int skew = hasDisp ? Math.Abs(disp - window) : int.MaxValue;
                bool better = !found
                    || (unique && !bestUnique)
                    || (unique == bestUnique
                        && (skew < bestSkew
                            || (skew == bestSkew && length > bestLength)
                            || (skew == bestSkew && length == bestLength && position < best.Position)));

                if (better)
                {
                    best = (position, size, window);
                    bestSkew = skew;
                    bestLength = length;
                    bestUnique = unique;
                    found = true;
                }
            }
        }

        if (!found || assigned.Contains(best.Position))
        {
            return;
        }

        for (int k = 0; k < best.Size; k++)
        {
            int offset = best.Window + k;
            if (offset >= source.Length)
            {
                // 串尾那个 NUL 是终止符，不能动
                break;
            }

            bytes[best.Position + k] = offset < target.Length ? target[offset] : (byte)0x20;
        }

        assigned.Add(best.Position);
        written++;
    }

    /// <summary>
    /// 这份片段是不是**别的条目**也能当尾巴用（同样盖到它串尾、超出部分补零）—— 是的话归属存疑，
    /// 挑的时候要排在「唯一归属」的后面（见 <see cref="PatchTailPieces"/>）。
    /// </summary>
    private static bool IsSharedTail(
        byte[] bytes,
        int position,
        int size,
        byte[] source,
        IReadOnlyList<byte[]> allSources)
    {
        foreach (byte[] other in allSources)
        {
            if (other.Length < 2 || SameSource(other, source))
            {
                continue;
            }

            for (int window = 0; window < other.Length; window++)
            {
                if (window + size < other.Length)
                {
                    continue;
                }

                int length = Math.Min(size, other.Length - window);
                if (length < 1 || !Matches(bytes, position, other, window, length))
                {
                    continue;
                }

                if (window + size > other.Length && !IsPadding(bytes, position + length, size - length))
                {
                    continue;
                }

                if (length < 2 && !IsPadding(bytes, position + length, size - length))
                {
                    continue;
                }

                return true;
            }
        }

        return false;
    }

    /// <summary>两条英文是不是同一份内容（<c>string</c> 那种引用相等在这里不可靠，老老实实比字节）</summary>
    private static bool SameSource(byte[] left, byte[] right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        for (int k = 0; k < left.Length; k++)
        {
            if (left[k] != right[k])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 这个片段是不是「**别的条目**英文的开头」：那样它更可能是那一条的头部写入，而不是这条的尾巴。
    /// 这个片段是不是「**别的条目**英文的开头」：那样它更可能是那一条的头部写入，而不是这条的尾巴。
    /// 实测 <c>"Mode"</c> 既是 <c>Options Mode</c> 的尾巴、又是 <c>Model A/B/C</c> 的头，写下去就把
    /// Model 那几行写成「????A」。片段取到 NUL 为止，长度 1 的不做这个判断（实测只见过 4 字节这种）。
    /// </summary>
    private static bool IsSharedConstant(
        byte[] bytes,
        int position,
        int size,
        byte[] source,
        IReadOnlyList<byte[]> allSources)
    {
        int length = 0;
        while (length < size && bytes[position + length] != 0x00)
        {
            length++;
        }

        if (length < 2)
        {
            return false;
        }

        foreach (byte[] other in allSources)
        {
            if (ReferenceEquals(other, source) || other.Length < length)
            {
                continue;
            }

            bool same = true;
            for (int k = 0; k < length; k++)
            {
                if (other[k] != bytes[position + k])
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 找一条**盖满整条英文**的片段链；任何一段缺失（盖不满）就返回 null。
    /// 「盖满」= 片段按英文里的偏移排序后，从 0 起首尾相接（允许重叠）、最后正好到串尾。
    ///
    /// <para>
    /// 光「拼得上」还不够，两道闸都是实测踩出来的：
    /// <list type="number">
    /// <item>**必须带位移**：<c>mov rax, imm64</c> 后面那条 store 的 disp 就是它运行时落在缓冲上的位置。
    /// 拿不到位移就不知道它落在哪，宁可不写（比如 <c>"Of"</c> + 某个碰巧以 <c>ff</c> 开头的 8 字节常量，
    /// 拼起来「正好」是 <c>Off</c>，写进去就是乱改别人的常量）。</item>
    /// <item>**位移必须连续**：所有片段的 <c>disp - 偏移</c> 要相等（同一个缓冲）。
    /// 实测 <c>Options Mode</c> 的头是 <c>disp-偏移=0</c>、尾却在 <c>-129</c>（另一个缓冲/另一个局部量），
    /// <c>Render</c> 是 <c>-265</c>、<c>Model A/B/C</c> 是 <c>+2097/2129/2161</c> —— 这些链写下去就是
    /// 「中文头 + 英文尾」的老毛病，直接整条不写。</item>
    /// <item>**片段超出串尾的那几个字节必须是补零**：<c>mov rdx, "Mode\\0\\0\\0\\0"</c> 是对的，
    /// 一个以英文开头的 8 字节常量则说明它其实是别的串。</item>
    /// </list>
    /// </para>
    /// </summary>
    private static List<(int Position, int Size, int Window, int Disp, bool HasDisp)>? FindChain(
        byte[] bytes,
        byte[] source,
        List<(int Position, int Size, int Disp, bool HasDisp)> immediates,
        HashSet<int> assigned,
        bool requireDisp = true)
    {
        // 这条英文在每个立即数里的落点：(立即数位置, 立即数长度, 在英文里的偏移, 位移, 有没有位移)
        List<(int Position, int Size, int Window, int Disp, bool HasDisp)> pieces = [];
        foreach ((int position, int size, int disp, bool hasDisp) in immediates)
        {
            if (size < 2 || (requireDisp && !hasDisp) || assigned.Contains(position))
            {
                continue;
            }

            for (int window = 0; window < source.Length; window++)
            {
                int length = Math.Min(size, source.Length - window);
                if (length < 1 || !Matches(bytes, position, source, window, length))
                {
                    continue;
                }

                // 片段比这条串长 → 多出来的那几个字节必须是补零，否则它其实是别的串/别的常量
                bool overflowIsPad = window + size > source.Length
                    && IsPadding(bytes, position + length, size - length);

                if (window + size > source.Length && !overflowIsPad)
                {
                    continue;
                }

                // 只对上 1 个字节的片段，必须连 NUL 一起写进来（`mov word [x+0x10], "y\0"`）——
                // 否则满文件的 `mov byte [..], 'a'` 都会被当成片段，链就拼出鬼来了
                if (length < 2 && !overflowIsPad)
                {
                    continue;
                }

                pieces.Add((position, size, window, disp, hasDisp));
            }
        }

        if (pieces.Count == 0)
        {
            return null;
        }

        // 链必须从 0 起，所以先看「谁盖住了第 0 个字节」：每个这样的片段代表一种**缓冲基线**
        // （disp - 窗口 = 这条指令里缓冲相对它基址寄存器的偏移）。
        //
        // 同一个短常量在别的函数里还有副本、基线各不相同 —— 实测 "Mode" 在文件里有 6 份
        // （基线 -1 / 128 / 120 / 2096 / 2128 / 2160）。老写法「抓到第一个片段就把它的基线定死，
        // 一遇到不一致就直接 return null」，于是 Options Mode 明明头（基线 128）尾（基线 128）都在，
        // 却因为先撞上基线 -1 那份 "Mode" 被判成「位移不连续 → 整条不写」。改成**逐个基线试**：
        // 基线 128 那一组能盖满 → 就是它。
        List<int> baselines = [];
        foreach ((_, _, int window, int disp, bool hasDisp) in pieces)
        {
            if (window == 0 && hasDisp && !baselines.Contains(disp))
            {
                baselines.Add(disp);
            }
        }

        foreach (int baseline in baselines)
        {
            if (SweepChain(pieces, source.Length, baseline) is { } chain)
            {
                return chain;
            }
        }

        // 自测里 requireDisp:false 的合成指令（mov rax, imm64 后面没有 store）拿不到位移，
        // 这时只能按「能盖满就好」拼一次；线上永远 requireDisp:true，走不到这里。
        return requireDisp ? null : SweepChain(pieces, source.Length, baseline: null);
    }

    /// <summary>
    /// 在候选片段里贪心地把整条串盖满：每步挑「起点 ≤ 已覆盖、终点最远」的那一段
    /// （区间覆盖问题的最优贪心），并且必须落在同一条缓冲上（<c>disp - 窗口</c> 相等；
    /// 没带位移的片段不参与这个判断）。盖不满返回 null。
    /// </summary>
    private static List<(int Position, int Size, int Window, int Disp, bool HasDisp)>? SweepChain(
        List<(int Position, int Size, int Window, int Disp, bool HasDisp)> pieces,
        int length,
        int? baseline)
    {
        List<(int Position, int Size, int Window, int Disp, bool HasDisp)> chain = [];
        HashSet<int> used = [];
        int covered = 0;

        while (covered < length)
        {
            bool found = false;
            int bestEnd = covered;
            (int Position, int Size, int Window, int Disp, bool HasDisp) best = default;

            foreach ((int position, int size, int window, int disp, bool hasDisp) in pieces)
            {
                if (window > covered || used.Contains(position))
                {
                    continue;
                }

                if (baseline is int want && hasDisp && disp - window != want)
                {
                    continue;
                }

                int end = window + Math.Min(size, length - window);
                if (end > bestEnd)
                {
                    bestEnd = end;
                    best = (position, size, window, disp, hasDisp);
                    found = true;
                }
            }

            if (!found)
            {
                return null;
            }

            chain.Add(best);
            used.Add(best.Position);
            covered = bestEnd;
        }

        // 盖不满就整条不写：宁可留英文，也不能留半个中文字
        return chain.Count > 0 ? chain : null;
    }

    /// <summary>
    /// 立即数里字符串之后的那些字节是不是**补零**（真正的串尾）。
    /// 只认 0x00，不认空格 —— 实测 <c>"Scaling "</c> 末尾那个空格是它后面还接着
    /// <c>"g Domain"</c>（两条立即数拼出 "Scaling Domain"），把它当「整条串结束」就会写错。
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

    /// <summary>
    /// 诊断用：把一条英文在文件里能凑齐的片段链列出来（含每个片段的位移），
    /// 用来核对「片段的运行时偏移 == 它在英文里的偏移」这个假设 —— 位移差为 0 才说明拼出来的中文是对的。
    /// </summary>
    public static List<string> DescribeChains(
        byte[] bytes,
        string english,
        List<(int Position, int Size, int Disp, bool HasDisp)> immediates)
    {
        byte[] source = Encoding.UTF8.GetBytes(english);
        HashSet<int> assigned = [];
        List<string> lines = [];

        while (FindChain(bytes, source, immediates, assigned) is { } chain)
        {
            int baseline = int.MinValue;
            List<string> parts = [];

            foreach ((int position, int size, int window, int disp, bool hasDisp) in chain)
            {
                assigned.Add(position);

                if (!hasDisp)
                {
                    parts.Add($"[偏移 {window} 长 {size} 无位移]");
                    continue;
                }

                if (baseline == int.MinValue)
                {
                    baseline = disp - window;
                }

                parts.Add($"[偏移 {window} 长 {size} 位移差 {disp - window - baseline}]");
            }

            lines.Add(string.Join(" + ", parts));
        }

        return lines;
    }

    /// <summary>
    /// 自测入口：只跑一条串，且**关掉位移闸**（测试里是手写的假立即数，没有 store 位移）。
    /// 真实调用永远开着位移闸，见 <see cref="FindChain"/>。
    /// </summary>
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

        return PatchCodeImmediatesAll(bytes, [entry], rich, requireDisp: false)[0];
    }

    /// <summary>自测入口：整张表跑一趟（用来测「共享常量 / 共享后缀 / guard」这些**跨条目**的规矩）</summary>
    public static int PatchCodeImmediatesForTest(
        byte[] bytes,
        IReadOnlyList<AddonI18nEntry> entries,
        List<(int Position, int Size, int Disp, bool HasDisp)> immediates)
    {
        int sum = 0;
        foreach (int hit in PatchCodeImmediatesAll(bytes, entries, immediates, requireDisp: true))
        {
            sum += hit;
        }

        return sum;
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

    /// <summary>立即数里的第 window 个字节起，是不是等于英文串的同一段</summary>
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
