using HoYoShadeHub.Extensions.Models;
using System.Text;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// 维护 ReShade.ini 里我们关心的键。
///
/// 事实依据（crosire/reshade，source/addon_manager.cpp）：
/// <code>
/// std::filesystem::path addon_search_path = g_reshade_base_path;
/// if (config.get("ADDON", "AddonPath", addon_search_path))
///     addon_search_path = g_reshade_base_path / addon_search_path;
/// // 随后对 addon_search_path 做【非递归】目录扫描，匹配 *.addon / *.addon64
/// </code>
///
/// 也就是说：
/// <list type="number">
/// <item>加载 addon 的 ini 键是 <c>[ADDON] AddonPath=</c>（单个路径，不是 SearchPaths 列表）；</item>
/// <item>路径相对 ReShade DLL 所在目录（即 HoYoShade 根目录）；</item>
/// <item>不递归 —— addon 必须直接躺在该目录里，不能是子目录。</item>
/// </list>
/// HoYoShade Hub 会把 addon 放进 <c>reshade-shaders\Addons</c>，但从不写这个键，
/// 而 ini 缺失时 ReShade 只扫 HoYoShade 根目录，所以那些 addon 不会生效。
/// </summary>
public static class ReShadeIniService
{
    /// <summary>相对 HoYoShade 根目录的 addon 目录（与 Hub 的落位保持一致）</summary>
    public const string AddonPathValue = @".\reshade-shaders\Addons";

    private const string SectionName = "ADDON";
    private const string KeysSectionName = "GENERAL";
    private const string AddonPathKey = "AddonPath";

    /// <summary>
    /// 确保 ReShade.ini 里存在 <c>[ADDON] AddonPath=</c>。
    /// 只增不改：已有的其它键与注释原样保留。
    /// </summary>
    /// <returns>是否实际写入了文件</returns>
    public static bool EnsureAddonPath(ShadeHost host, string addonPathValue = AddonPathValue)
    {
        string path = host.ReShadeIniPath;

        List<string> lines = File.Exists(path)
            ? [.. File.ReadAllLines(path)]
            : [.. DefaultIniLines()];

        if (TryGetValue(lines, SectionName, AddonPathKey, out string? existing))
        {
            if (string.Equals(existing?.Trim(), addonPathValue, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            SetValue(lines, SectionName, AddonPathKey, addonPathValue);
        }
        else
        {
            SetValue(lines, SectionName, AddonPathKey, addonPathValue);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return true;
    }

    /// <summary>判断当前 ini 是否已经能让 ReShade 扫到 addon 目录</summary>
    public static bool HasUsableAddonPath(ShadeHost host)
    {
        string path = host.ReShadeIniPath;
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            List<string> lines = [.. File.ReadAllLines(path)];
            return TryGetValue(lines, SectionName, AddonPathKey, out string? value)
                   && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Hub 在 ReShade.ini 缺失时写的那套内容，我们保持一致并补上 addon 路径</summary>
    private static IEnumerable<string> DefaultIniLines()
    {
        yield return "[GENERAL]";
        yield return @"EffectSearchPaths=.\reshade-shaders\Shaders\**";
        yield return @"TextureSearchPaths=.\reshade-shaders\Textures\**";
        yield return "";
        yield return "[INPUT]";
        yield return "KeyOverlay=36,0,0,0";
        yield return "GamepadNavigation=0";
        yield return "";
        yield return "[" + SectionName + "]";
        yield return AddonPathKey + "=" + AddonPathValue;
        yield return "";
    }

    private static bool TryGetValue(List<string> lines, string section, string key, out string? value)
    {
        value = null;
        string? current = null;

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                current = line[1..^1].Trim();
                continue;
            }

            if (!string.Equals(current, section, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            if (string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                value = line[(separator + 1)..];
                return true;
            }
        }

        return false;
    }

    private static void SetValue(List<string> lines, string section, string key, string value)
    {
        string? current = null;
        int sectionStart = -1;
        int sectionEnd = lines.Count;

        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                if (string.Equals(current, section, StringComparison.OrdinalIgnoreCase))
                {
                    sectionEnd = i;
                    break;
                }

                current = line[1..^1].Trim();
                if (string.Equals(current, section, StringComparison.OrdinalIgnoreCase))
                {
                    sectionStart = i;
                }
                continue;
            }

            if (sectionStart >= 0 && string.Equals(current, section, StringComparison.OrdinalIgnoreCase))
            {
                int separator = line.IndexOf('=');
                if (separator > 0 && string.Equals(line[..separator].Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"{key}={value}";
                    return;
                }
            }
        }

        if (sectionStart >= 0)
        {
            lines.Insert(sectionEnd, $"{key}={value}");
            return;
        }

        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
        {
            lines.Add(string.Empty);
        }
        lines.Add($"[{section}]");
        lines.Add($"{key}={value}");
    }
}
