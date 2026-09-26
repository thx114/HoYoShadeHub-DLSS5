using HoYoShadeHub.Extensions.Models;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>一次路径对齐的结果</summary>
/// <param name="ChangedKeys">被改掉的键（形如 <c>[ADDON] AddonPath</c>）</param>
/// <param name="PreviousRoot">原来的 HoYoShade 根目录（没改就为 null）</param>
public sealed record ShadePathAlignResult(IReadOnlyList<string> ChangedKeys, string? PreviousRoot)
{
    public static readonly ShadePathAlignResult None = new([], null);

    public bool Changed => ChangedKeys.Count > 0;
}

/// <summary>
/// 把游戏目录那份 <c>ReShade.ini</c> 里的绝对路径指回**当前这个** HoYoShade。

/// <para>
/// 背景（用户反馈）：「应该用哪个启动器，ini 就对应到哪才对」。
/// HoYoShade 的 <c>INIBuild.exe</c> 写出来的模板里全是绝对路径（AddonPath / EffectSearchPaths /
/// PresetPath / 字体 / 截图目录），而 <c>inject.exe</c> 只在游戏目录**没有** ini 时才复制模板。
/// 所以换一个 HoYoShade（换启动器、换用户数据目录）之后，老 ini 会一直指着旧目录 ——
/// 插件页按它算出来的「缺 dll」是真的，游戏运行时也确实加载不到那些插件。
/// </para>

/// <para>
/// 只动「长得像 HoYoShade 根目录」的那几个键，而且只把根前缀换掉：
/// </para>
/// <list type="bullet">
/// <item>相对路径（<c>.\reshade-shaders\Addons</c>）不动 —— 它本来就跟着 ReShade DLL 走，永远是对的；</item>
/// <item>指向别处、不带 HoYoShade 特征目录的路径不动（比如用户自己指定的插件目录）；</item>
/// <item>其余键、注释、顺序、[RENODX-*] 那些调好的参数原样保留。</item>
/// </list>
/// </summary>
public static class ShadePathAligner
{
    /// <summary>ini 里写着「HoYoShade 根目录下绝对路径」的键</summary>
    public static readonly (string Section, string Key)[] PathKeys =
    [
        ("ADDON", "AddonPath"),
        ("GENERAL", "EffectSearchPaths"),
        ("GENERAL", "TextureSearchPaths"),
        ("GENERAL", "PresetPath"),
        ("STYLE", "Font"),
        ("STYLE", "EditorFont"),
        ("STYLE", "LatinFont"),
        ("SCREENSHOT", "SavePath"),
    ];

    /// <summary>HoYoShade 根目录下才会有的目录名，用来反推「这个路径属于哪个根」</summary>
    public static readonly string[] RootAnchors =
    [
        "reshade-shaders",
        "Presets",
        "InjectResource",
        "ScreenShot",
    ];

    /// <summary>把 <paramref name="gameIniPath"/> 里的绝对路径对到 <paramref name="host"/> 这个 HoYoShade</summary>
    public static ShadePathAlignResult Align(string? gameIniPath, ShadeHost host) =>
        Align(gameIniPath, host.RootPath);

    /// <inheritdoc cref="Align(string?, ShadeHost)"/>
    public static ShadePathAlignResult Align(string? gameIniPath, string? shadeRoot)
    {
        if (string.IsNullOrWhiteSpace(gameIniPath) || string.IsNullOrWhiteSpace(shadeRoot))
        {
            return ShadePathAlignResult.None;
        }

        if (!File.Exists(gameIniPath))
        {
            return ShadePathAlignResult.None;
        }

        string root = TrimSeparators(shadeRoot.Trim());
        IniDocument ini = IniDocument.Load(gameIniPath);
        List<string> changed = [];
        string? previousRoot = null;

        foreach ((string section, string key) in PathKeys)
        {
            string? value = ini.GetValue(section, key);

            // 每游戏插件包（<CacheRoot>\games\<gameKey>\Addons，带 pack.json 标记）是**故意**
            // 指到共享目录之外的：这个游戏选了非默认的插件版本。把它改回当前 HoYoShade
            // 就等于把「每游戏选版本」悄悄作废 —— 直接跳过 AddonPath 这一项。
            if (string.Equals(section, "ADDON", StringComparison.OrdinalIgnoreCase)
                && string.Equals(key, "AddonPath", StringComparison.OrdinalIgnoreCase)
                && GameAddonPack.IsPackDirectory(value))
            {
                continue;
            }

            // 缺 [ADDON] AddonPath 就补上 —— 兼容性检测第 12 项报「没写 AddonPath」之后
            // 点的就是这条路，以前这里直接 continue，于是「修复」点了等于没点（用户反馈）。
            // 少了这个键 ReShade 只会扫 HoYoShade 根目录，reshade-shaders\Addons 里的插件
            // 全都加载不到。写**绝对**路径：检测和插件页会拿这个值去 Directory.Exists，
            // 相对路径（.\reshade-shaders\Addons）在游戏目录那份 ini 里会被算成「目录不存在」。
            if (string.IsNullOrWhiteSpace(value))
            {
                if (string.Equals(section, "ADDON", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(key, "AddonPath", StringComparison.OrdinalIgnoreCase))
                {
                    ini.SetValue(section, key, Path.Combine(root, "reshade-shaders", "Addons"));
                    changed.Add($"[{section}] {key}");
                }

                continue;
            }

            string aligned = AlignValue(value, root, ref previousRoot);
            if (string.Equals(aligned, value, StringComparison.Ordinal))
            {
                continue;
            }

            ini.SetValue(section, key, aligned);
            changed.Add($"[{section}] {key}");
        }

        if (changed.Count == 0)
        {
            return ShadePathAlignResult.None;
        }

        ini.Save(gameIniPath);
        return new ShadePathAlignResult(changed, previousRoot);
    }

    /// <summary>单个值（可能带 <c>,</c> 分隔的多条搜索路径）对齐到 <paramref name="shadeRoot"/></summary>
    public static string AlignValue(string? value, string? shadeRoot)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(shadeRoot))
        {
            return value ?? string.Empty;
        }

        string? ignored = null;
        return AlignValue(value, TrimSeparators(shadeRoot.Trim()), ref ignored);
    }

    /// <summary>从一条绝对路径里反推 HoYoShade 根目录；认不出来返回 null</summary>
    public static string? RootOf(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string path = value.Trim();
        if (!Path.IsPathFullyQualified(path))
        {
            // 相对路径跟着 ReShade DLL 所在目录走 —— 换哪个启动器都对，不用管
            return null;
        }

        foreach (string anchor in RootAnchors)
        {
            int index = IndexOfSegment(path, anchor);
            if (index <= 0)
            {
                continue;
            }

            string prefix = TrimSeparators(path[..index]);
            if (prefix.Length > 0 && Path.IsPathFullyQualified(prefix))
            {
                return prefix;
            }
        }

        return null;
    }

    private static string AlignValue(string value, string root, ref string? previousRoot)
    {
        string[] parts = value.Split(',');
        bool any = false;

        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            if (part.Contains(';'))
            {
                // 用分号当分隔符的写法我们认不准，别乱动
                continue;
            }

            string? oldRoot = RootOf(part);
            if (oldRoot is null || PathsEqual(oldRoot, root))
            {
                continue;
            }

            previousRoot ??= oldRoot;
            parts[i] = root + part.Trim()[oldRoot.Length..];
            any = true;
        }

        // 同一条路径写了两遍（历史修复 / 其它工具反复写）ReShade 会扫两遍，顺手去重
        string[] distinct = parts.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (distinct.Length != parts.Length)
        {
            parts = distinct;
            any = true;
        }

        return any ? string.Join(',', parts) : value;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(TrimSeparators(a.Trim()), TrimSeparators(b.Trim()), StringComparison.OrdinalIgnoreCase);

    private static string TrimSeparators(string path) => path.TrimEnd('\\', '/');

    /// <summary>找整段出现的 <paramref name="anchor"/>（<c>\Presets\</c> 这种），返回它在字符串里的下标</summary>
    private static int IndexOfSegment(string path, string anchor)
    {
        int from = 0;
        while (from < path.Length)
        {
            int index = path.IndexOf(anchor, from, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return -1;
            }

            bool left = index > 0 && (path[index - 1] == '\\' || path[index - 1] == '/');
            int after = index + anchor.Length;
            bool right = after >= path.Length || path[after] == '\\' || path[after] == '/';
            if (left && right)
            {
                return index;
            }

            from = index + 1;
        }

        return -1;
    }
}
