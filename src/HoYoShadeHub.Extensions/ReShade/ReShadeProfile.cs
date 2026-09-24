using System.Text;
using System.Text.RegularExpressions;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>
/// 行式 INI 文档：只认「节 + 键=值」，其余内容（注释、空行、顺序、缩进）原样保留。
///
/// 为什么不整体反序列化：ReShade.ini 里还有 [OVERLAY]、[STYLE]、[SCREENSHOT] 等一大堆我们
/// 不关心的键，而且 ReShade 自己会往里写窗口布局。往返一次不能把用户的东西弄丢。
/// </summary>
public sealed class IniDocument
{
    private readonly List<string> _lines;
    private readonly bool _hadBom;

    private IniDocument(List<string> lines, bool hadBom)
    {
        _lines = lines;
        _hadBom = hadBom;
    }

    public IReadOnlyList<string> Lines => _lines;

    public static IniDocument Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool hadBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text = hadBom
            ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
            : Encoding.UTF8.GetString(bytes);

        return new IniDocument([.. SplitLines(text)], hadBom);
    }

    public static IniDocument CreateEmpty() => new([], hadBom: false);

    private static IEnumerable<string> SplitLines(string text)
    {
        // 统一到 \n 处理，回写时再用当前平台换行；ReShade 自己写的是 \r\n
        foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            yield return line;
        }

        // Split 会在结尾留一个空串，去掉它，回写时补回末尾换行
        yield break;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var encoding = new UTF8Encoding(_hadBom);
        string text = string.Join("\r\n", _lines.TrimTrailingEmpty()) + "\r\n";
        File.WriteAllText(path, text, encoding);
    }

    /// <summary>取节里某个键的原始值（未 trim 右侧）；不存在返回 null</summary>
    public string? GetValue(string section, string key)
    {
        int index = FindKeyLine(section, key);
        if (index < 0)
        {
            return null;
        }

        int separator = _lines[index].IndexOf('=');
        return separator < 0 ? string.Empty : _lines[index][(separator + 1)..];
    }

    public bool ContainsKey(string section, string key) => FindKeyLine(section, key) >= 0;

    /// <summary>写键；键不存在就插到该节末尾（节不存在就新建）</summary>
    public void SetValue(string section, string key, string value)
    {
        int index = FindKeyLine(section, key);
        if (index >= 0)
        {
            int separator = _lines[index].IndexOf('=');
            _lines[index] = _lines[index][..(separator + 1)] + value;
            return;
        }

        (int sectionStart, int sectionEnd) = FindSectionBounds(section);
        if (sectionStart < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Trim().Length > 0)
            {
                _lines.Add(string.Empty);
            }
            _lines.Add($"[{section}]");
            _lines.Add($"{key}={value}");
            return;
        }

        _lines.Insert(sectionEnd, $"{key}={value}");
    }

    public void RemoveKey(string section, string key)
    {
        int index = FindKeyLine(section, key);
        if (index >= 0)
        {
            _lines.RemoveAt(index);
        }
    }

    public IEnumerable<string> GetSectionKeys(string section)
    {
        string? current = null;
        foreach (string line in _lines)
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                current = trimmed[1..^1].Trim();
                continue;
            }

            if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int separator = trimmed.IndexOf('=');
            if (separator > 0)
            {
                yield return trimmed[..separator].Trim();
            }
        }
    }

    /// <summary>找节范围：返回 [起始行(节头), 结束行(下一节头或文件尾))</summary>
    private (int Start, int End) FindSectionBounds(string section)
    {
        string? current = null;
        int start = -1;

        for (int i = 0; i < _lines.Count; i++)
        {
            string trimmed = _lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (start >= 0)
                {
                    return (start, i);
                }

                current = trimmed[1..^1].Trim();
                if (string.Equals(current, section, StringComparison.OrdinalIgnoreCase))
                {
                    start = i;
                }
            }
        }

        return start >= 0 ? (start, _lines.Count) : (-1, -1);
    }

    private int FindKeyLine(string section, string key)
    {
        string? current = null;
        for (int i = 0; i < _lines.Count; i++)
        {
            string trimmed = _lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                current = trimmed[1..^1].Trim();
                continue;
            }

            if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int separator = trimmed.IndexOf('=');
            if (separator > 0 && string.Equals(trimmed[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }
}

internal static class ListExtensions
{
    /// <summary>去掉结尾的连续空行（回写时会统一补一个换行）</summary>
    public static List<string> TrimTrailingEmpty(this List<string> lines)
    {
        int end = lines.Count;
        while (end > 0 && lines[end - 1].Length == 0)
        {
            end--;
        }
        return lines.GetRange(0, end);
    }
}

/// <summary>`DisabledAddons` 里的一条：<c>Name@file.addon64</c></summary>
public sealed record DisabledAddonEntry(string? DisplayName, string FileName)
{
    public override string ToString() => DisplayName is null ? FileName : $"{DisplayName}@{FileName}";

    /// <summary>解析一条；只有文件名没有 @ 也认</summary>
    public static DisabledAddonEntry Parse(string raw)
    {
        string text = raw.Trim();
        int at = text.LastIndexOf('@');
        return at > 0
            ? new DisabledAddonEntry(text[..at], text[(at + 1)..])
            : new DisabledAddonEntry(null, text);
    }
}

/// <summary>
/// 一个游戏的 ReShade.ini。只碰 [ADDON] 段里我们关心的三个键，其余原样保留。
///
/// 事实来源：用户提供的四份真实样本（见 docs/RESHADE-INI.md）。
/// </summary>
public sealed class ReShadeProfile
{
    public const string AddonSection = "ADDON";

    /// <summary>
    /// <c>DirectNeuralRenderingHookPoint</c> 真正所在的段 —— 不是 <c>[ADDON]</c>！
    /// 四份真实样本（ZZZ / 星铁 / 蓝色星原 / Genshin）里这个键都写在 <c>[RENODX-DLSS]</c> 下，
    /// 是 RenoDX 这个 addon 自己存的配置段。写到 <c>[ADDON]</c> 里 addon 根本不看（用户反馈：
    /// 「HookPoint 设成 off，进游戏不是 off」）。
    /// </summary>
    public const string RenoDlssSection = "RENODX-DLSS";

    /// <summary>
    /// ShortFuse 的安装配置段。<c>HookStreamline=1</c> 让 DLSS 插件知道无需理会 Streamline
    /// 的 Present 钩子（外部注入时 Streamline 已先挂好，避免双重 Present hook / 顺序错乱）。
    /// </summary>
    public const string InstallSection = "INSTALL";

    public const string HookStreamlineKey = "HookStreamline";

    public const string DisabledAddonsKey = "DisabledAddons";
    public const string LoadFromDllMainKey = "LoadFromDllMain";

    /// <summary>老一些的 RenoDX DLSS 用的键名</summary>
    public const string HookPointKey = "DirectNeuralRenderingHookPoint";

    /// <summary>
    /// ShortFuse 那版 DLSS 插件（renodx-dlss(ShortFuse_*)）真正读的键名 —— 用户实测：
    /// 「SF 的 DLSS 插件 DirectNeuralRenderingHookStage 才是 hook 点」。
    /// 两个键我们**都写**（addon 只认自己那个，多写一个不影响），读的时候优先 Stage。
    /// </summary>
    public const string HookStageKey = "DirectNeuralRenderingHookStage";

    /// <summary>神经渲染 pass 数（用户要求：超过 3 就算高）</summary>
    public const string PassCountKey = "DirectNeuralRenderingPassCount";

    /// <summary>允许改 hook 点的插件 slug（用户明确要求的前置条件）</summary>
    public static readonly string[] HookPointCapableSlugs = ["renodx-dlss5-super-anus", "renodx-dlss"];

    private readonly IniDocument _ini;

    private ReShadeProfile(string filePath, IniDocument ini)
    {
        FilePath = filePath;
        _ini = ini;
    }

    public string FilePath { get; }

    public static ReShadeProfile Load(string path) => new(path, IniDocument.Load(path));

    public static ReShadeProfile CreateNew(string path) => new(path, IniDocument.CreateEmpty());

    public void Save()
    {
        _ini.Save(FilePath);
    }

    /// <summary>[ADDON] AddonPath —— 插件真身所在目录</summary>
    public string? AddonPath => _ini.GetValue(AddonSection, "AddonPath")?.Trim().TrimEnd('\\', '/');

    /// <summary>解析 AddonPath 成实际目录（样本里是绝对路径）</summary>
    public string? ResolveAddonDirectory()
    {
        string? raw = AddonPath;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return Path.IsPathFullyQualified(raw)
                ? Path.GetFullPath(raw)
                : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(FilePath)!, raw));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>[GENERAL] EffectSearchPaths  可能用逗号分隔写了好几条</summary>
    public List<string>? EffectSearchPaths => SplitPathList(_ini.GetValue("GENERAL", "EffectSearchPaths"));

    /// <summary>[GENERAL] TextureSearchPaths</summary>
    public List<string>? TextureSearchPaths => SplitPathList(_ini.GetValue("GENERAL", "TextureSearchPaths"));

    /// <summary>界面 / 诊断里显示用的原文（没写就是 null）</summary>
    public string? EffectSearchPathsText => _ini.GetValue("GENERAL", "EffectSearchPaths");

    public string? TextureSearchPathsText => _ini.GetValue("GENERAL", "TextureSearchPaths");

    private static List<string>? SplitPathList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>
    /// <c>[RENODX-DLSS] DirectNeuralRenderingPassCount</c>  神经渲染的 pass 数。
    /// 没写返回 null（= 用插件默认值）。
    /// </summary>
    public int? GetDirectNeuralRenderingPassCount()
    {
        string? raw = _ini.GetValue(RenoDlssSection, PassCountKey) ?? _ini.GetValue(AddonSection, PassCountKey);
        return int.TryParse(raw?.Trim(), out int value) ? value : null;
    }

    /// <summary>写 DirectNeuralRenderingPassCount（写进 addon 认的 [RENODX-DLSS] 段）</summary>
    public void SetDirectNeuralRenderingPassCount(int value)
    {
        _ini.SetValue(RenoDlssSection, PassCountKey, value.ToString());
        _ini.RemoveKey(AddonSection, PassCountKey);
    }
    #region DisabledAddons

    /// <summary>
    /// 解析 <c>DisabledAddons</c>。空值 → 空列表。
    /// 条目形如 <c>RenoDX DLSS_A@renodx-dlss5-super-anus(1.0.8.18).addon64</c>。
    /// </summary>
    public List<DisabledAddonEntry> GetDisabledAddons()
    {
        string? raw = _ini.GetValue(AddonSection, DisabledAddonsKey);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(DisabledAddonEntry.Parse)
            .Where(e => !string.IsNullOrWhiteSpace(e.FileName))];
    }

    public bool IsDisabled(string addonFileName) =>
        GetDisabledAddons().Any(e => string.Equals(e.FileName, addonFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>按文件名禁用；displayName 未知时可以传 null（保底只写文件名）</summary>
    public void DisableAddon(string addonFileName, string? displayName = null)
    {
        var entries = GetDisabledAddons();
        entries.RemoveAll(e => string.Equals(e.FileName, addonFileName, StringComparison.OrdinalIgnoreCase));
        entries.Add(new DisabledAddonEntry(displayName, addonFileName));
        SetDisabledAddons(entries);
    }

    public void EnableAddon(string addonFileName)
    {
        var entries = GetDisabledAddons();
        entries.RemoveAll(e => string.Equals(e.FileName, addonFileName, StringComparison.OrdinalIgnoreCase));
        SetDisabledAddons(entries);
    }

    public void SetDisabledAddons(IEnumerable<DisabledAddonEntry> entries)
    {
        // 样本里空的时候就是 `DisabledAddons=`，不要写成空字符串带空格
        _ini.SetValue(AddonSection, DisabledAddonsKey, string.Join(',', entries.Select(e => e.ToString())));
    }

    #endregion

    #region LoadFromDllMain

    /// <summary>
    /// 解析 <c>LoadFromDllMain</c>。
    /// <b>必须保留空槽位</b>：真实样本是 <c>a.addon64,,b.addon64</c>，
    /// 过滤掉空元素会让 ReShade 的解析位置错位。
    /// 整个键不存在时返回 null（区别于存在但为空）。
    /// </summary>
    public List<string>? GetLoadFromDllMain()
    {
        string? raw = _ini.GetValue(AddonSection, LoadFromDllMainKey);
        return raw is null ? null : [.. raw.Split(',')];
    }

    public bool IsLoadFromDllMain(string addonFileName) =>
        GetLoadFromDllMain()?.Any(v => string.Equals(v, addonFileName, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>加进 LoadFromDllMain（没有空位就在末尾追加一个槽）</summary>
    public void AddLoadFromDllMain(string addonFileName)
    {
        var slots = GetLoadFromDllMain() ?? [];
        if (slots.Any(s => string.Equals(s, addonFileName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        slots.Add(addonFileName);
        _ini.SetValue(AddonSection, LoadFromDllMainKey, string.Join(',', slots));
    }

    public void RemoveLoadFromDllMain(string addonFileName)
    {
        var slots = GetLoadFromDllMain();
        if (slots is null)
        {
            return;
        }

        for (int i = 0; i < slots.Count; i++)
        {
            if (string.Equals(slots[i], addonFileName, StringComparison.OrdinalIgnoreCase))
            {
                slots[i] = string.Empty;
            }
        }

        _ini.SetValue(AddonSection, LoadFromDllMainKey, string.Join(',', slots));
    }

    /// <summary>整串覆盖（清失效条目时用；空元素原样写回，不重排）</summary>
    public void SaveLoadFromDllMainSlots(IEnumerable<string> slots) =>
        _ini.SetValue(AddonSection, LoadFromDllMainKey, string.Join(',', slots));

    #endregion

    #region DirectNeuralRenderingHookPoint

    /// <summary>0 表示关闭；键不存在返回 null</summary>
    public int? GetHookPoint()
    {
        // [RENODX-DLSS] 才是 addon 认的位置；ShortFuse 那版读的是 HookStage，优先它
        string? raw = _ini.GetValue(RenoDlssSection, HookStageKey);

        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = _ini.GetValue(RenoDlssSection, HookPointKey);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            string? legacy = _ini.GetValue(AddonSection, HookPointKey);
            if (legacy is null)
            {
                return null;
            }

            raw = legacy;
        }

        return int.TryParse(raw.Trim(), out int value) ? value : null;
    }

    /// <summary>
    /// 写 hook 点。0 写成 0（不是删键）—— 用户要求「0 就是 off，直接显示成 off」，
    /// 但盘上仍要有这个键，否则 ReShade 会用它自己的默认值。
    /// </summary>
    public void SetHookPoint(int value)
    {
        // 两个键都写：Point 是老的 RenoDX DLSS，Stage 是 ShortFuse 那版（用户实测的键名）
        _ini.SetValue(RenoDlssSection, HookPointKey, value.ToString());
        _ini.SetValue(RenoDlssSection, HookStageKey, value.ToString());

        // 老版本把它误写在 [ADDON] 里过，留着会让「读哪一份」再次打架，顺手删掉
        _ini.RemoveKey(AddonSection, HookPointKey);
        _ini.RemoveKey(AddonSection, HookStageKey);
    }

    public void RemoveHookPoint()
    {
        _ini.RemoveKey(RenoDlssSection, HookPointKey);
        _ini.RemoveKey(RenoDlssSection, HookStageKey);
        _ini.RemoveKey(AddonSection, HookPointKey);
        _ini.RemoveKey(AddonSection, HookStageKey);
    }

    #endregion

    #region HookStreamline

    /// <summary>[INSTALL] HookStreamline 是否打开；键不存在 / 值不是 1 都算关</summary>
    public bool IsHookStreamlineEnabled() =>
        string.Equals(_ini.GetValue(InstallSection, HookStreamlineKey)?.Trim(), "1", StringComparison.Ordinal);

    /// <summary>写 HookStreamline（开=1，关=0；键始终保留，addon 靠它识别）</summary>
    public void SetHookStreamline(bool enabled)
    {
        _ini.SetValue(InstallSection, HookStreamlineKey, enabled ? "1" : "0");
    }

    #endregion
}

/// <summary>
/// 插件目录里的一个文件名解析结果。
///
/// 命名约定（从真实目录反推）：<c>&lt;slug&gt;(&lt;版本&gt;).addon64</c>
/// <code>
/// renodx-dlss(9.17.12).addon64                   slug=renodx-dlss              version=9.17.12
/// renodx-dlss(ShortFuse_9.11.6).addon64x         slug=renodx-dlss              version=9.11.6  branch=ShortFuse
/// renodx-dlss5-super-anus(1.0.8.18).addon64      slug=renodx-dlss5-super-anus  version=1.0.8.18
/// dlss5-bridge.addon64                           slug=dlss5-bridge             version=null
/// </code>
/// </summary>
public sealed partial class AddonFileInfo
{
    public required string FileName { get; init; }
    public required string Slug { get; init; }

    /// <summary>括号里的版本号（去掉分支前缀）；没有括号就是 null</summary>
    public string? Version { get; init; }

    /// <summary>版本号里的分支前缀，例如 <c>ShortFuse_9.11.6</c> 里的 ShortFuse</summary>
    public string? Branch { get; init; }

    /// <summary>是不是 addon（而不是配套 dll / zip / 备注文件）</summary>
    public bool IsAddon { get; init; }

    /// <summary>
    /// 允许改 hook 的插件：所有 <c>renodx-dlss*</c>（<c>renodx-dlss</c> / <c>renodx-dlss5</c> /
    /// <c>renodx-dlss5-super-anus</c> / <c>renodx-dlss-SF</c> …）。
    ///
    /// <para>
    /// 早先写成「必须是 <c>renodx-dlss</c> 且分支是 ShortFuse」，但**文件名里常常根本没有分支信息**
    /// （用户装的是 <c>renodx-dlss.addon64</c>，用的就是 SF 那版）→ 下拉框被灰掉，用户报过。
    /// </para>
    /// </summary>
    public bool IsHookPointCapable =>
        Slug is not null && Slug.StartsWith("renodx-dlss", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 按**文件名**判断是不是 DLSS5 那一类插件。
    ///
    /// <para>
    /// 扩展目录里的 tags 是首选判据，但那个表可能没配到 / 没更新 —— 光靠 tags 会把用户
    /// 手里明明是 DLSS5 的插件判成「不是」，于是「从 DllMain 加载」被灰掉、写盘也被拒。
    /// 文件名里认得出 <c>dlss5</c> 也算，两条判据取并集。
    /// </para>
    /// </summary>
    public bool IsDlss5ByName =>
        Slug is not null && Slug.Contains("dlss5", StringComparison.OrdinalIgnoreCase);

    /// <summary>文件被重命名为 .addon64x 之类 —— 这是「全局禁用」</summary>
    public bool IsRenamedDisabled { get; init; }

    private static readonly Regex _pattern = new(
        @"^(?<slug>[^(]+?)(?:\((?<ver>[^)]+)\))?(?<ext>\.addon(?:64|32)x?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 没括号的写法：<c>名字.版本.addon64</c>。
    /// 实测 thx114/hoyodlss5 发布的资产就是这种：
    /// <c>renodx-neural-interposer-nvngx.dll.23.1.0RC1.addon64</c>。
    /// </summary>
    private static readonly Regex _dottedPattern = new(
        @"^(?<slug>.+?)\.(?<ver>\d[0-9A-Za-z.\-_]*)(?<ext>\.addon(?:64|32)x?)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static AddonFileInfo? Parse(string fileName)
    {
        Match match = _pattern.Match(fileName);
        if (!match.Success)
        {
            return null;
        }

        string slug = match.Groups["slug"].Value.Trim();
        string extension = match.Groups["ext"].Value.ToLowerInvariant();
        string? rawVersion = match.Groups["ver"].Success ? match.Groups["ver"].Value.Trim() : null;

        // 没有括号版本时再试「名字.版本.addon64」
        if (rawVersion is null && _dottedPattern.Match(fileName) is { Success: true } dotted)
        {
            slug = dotted.Groups["slug"].Value.Trim();
            rawVersion = dotted.Groups["ver"].Value.Trim();
        }

        string? branch = null;
        string? version = rawVersion;
        if (rawVersion is not null)
        {
            int underscore = rawVersion.IndexOf('_');
            if (underscore > 0)
            {
                branch = rawVersion[..underscore];
                version = rawVersion[(underscore + 1)..];
            }
        }

        return new AddonFileInfo
        {
            FileName = fileName,
            Slug = slug,
            Version = version,
            Branch = branch,
            IsAddon = true,
            IsRenamedDisabled = extension.EndsWith('x'),
        };
    }

    /// <summary>扫一个目录，只挑出 addon（含被重命名禁用的），忽略 dll / zip / 备注文件</summary>
    public static List<AddonFileInfo> ScanDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var result = new List<AddonFileInfo>();
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            AddonFileInfo? info = Parse(Path.GetFileName(file));
            if (info is not null)
            {
                result.Add(info);
            }
        }

        return [.. result.OrderBy(a => a.Slug, StringComparer.OrdinalIgnoreCase).ThenBy(a => a.FileName, StringComparer.OrdinalIgnoreCase)];
    }
}
