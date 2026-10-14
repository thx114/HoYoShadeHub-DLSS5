using System.Globalization;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>
/// ReShade <c>[INPUT]</c> 快捷键值的规范化。
///
/// <para>
/// ReShade 的快捷键是固定的四段 <c>键码,ctrl,alt,shift</c>（<c>36,0,0,0</c> = Home）。
/// 实测出现过 <c>36,,0,,0,,0</c>（段间多插了空字段）、<c>36,0,0,,0</c> 这类写坏的值：
/// ReShade 解析不出来就等于<strong>这个键根本没绑</strong> —— 现象是「按 Home 打不开菜单」，
/// 而且坏值会随覆盖包的存档（<c>GamePack\auto.json</c> / <c>ReShade.current-portable.ini</c>）
/// 在每次启动时被写回游戏 ini，用户手改完一启动又变回去（2026-10-10 工单）。
/// </para>
///
/// <para>
/// 这里只做一件窄事：<c>[INPUT]</c> 里 <c>Key…</c> 开头的键、且<strong>每一段都是整数或空</strong>时，
/// 去掉空段并把段数补足四段。认不出来的一律原样返回，绝不猜。
/// </para>
/// </summary>
public static class ReShadeHotkeys
{
    /// <summary>热键所在的节</summary>
    public const string InputSection = "INPUT";

    /// <summary>ReShade 快捷键的段数（键码 + ctrl/alt/shift）</summary>
    private const int FieldCount = 4;

    /// <summary>这个键是不是热键（<c>[INPUT]</c> 里 <c>Key…</c> 开头的那些）</summary>
    public static bool IsHotkeyKey(string? section, string? key) =>
        string.Equals(section?.Trim(), InputSection, StringComparison.OrdinalIgnoreCase)
        && key is not null
        && key.Trim().StartsWith("Key", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 写坏了返回 true，<paramref name="normalized"/> 是修好的四段值；本来正常 / 认不出来返回 false。
    /// </summary>
    public static bool TryNormalize(string? section, string? key, string? value, out string normalized)
    {
        normalized = value ?? string.Empty;
        if (!IsHotkeyKey(section, key) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var kept = new List<string>(FieldCount);
        foreach (string field in value.Split(','))
        {
            string trimmed = field.Trim();
            if (trimmed.Length == 0)
            {
                // 写坏时多出来的空段，直接丢
                continue;
            }

            if (!int.TryParse(trimmed, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _))
            {
                // 认不出来（不是纯数字段）：不猜，原样放行
                return false;
            }

            kept.Add(trimmed);
        }

        // 一段都没有、或者段数比四段还多：这不像快捷键，别动
        if (kept.Count is 0 or > FieldCount)
        {
            return false;
        }

        while (kept.Count < FieldCount)
        {
            kept.Add("0");
        }

        string candidate = string.Join(',', kept);
        if (string.Equals(candidate, value, StringComparison.Ordinal))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }

    /// <summary>ReShade 默认的叠加层快捷键（Home = 36）。</summary>
    public const string DefaultOverlayKey = "36,0,0,0";

    /// <summary>禁用某个键的写法。</summary>
    public const string DisabledKey = "0,0,0,0";

    /// <summary>按 ReShade 四段格式（键码,ctrl,alt,shift）拼一个值。</summary>
    public static string Format(int keyCode, bool ctrl, bool alt, bool shift)
        => string.Create(CultureInfo.InvariantCulture, $"{keyCode},{(ctrl ? 1 : 0)},{(alt ? 1 : 0)},{(shift ? 1 : 0)}");

    /// <summary>解析四段值；认不出来返回 false。</summary>
    public static bool TryParse(string? value, out int keyCode, out bool ctrl, out bool alt, out bool shift)
    {
        keyCode = 0; ctrl = alt = shift = false;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string[] parts = value.Split(',');
        if (parts.Length != FieldCount) return false;
        if (!int.TryParse(parts[0].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out keyCode)) return false;
        if (keyCode is < 0 or > 255) return false;
        if (!int.TryParse(parts[1].Trim(), out int c) || c is < 0 or > 1
            || !int.TryParse(parts[2].Trim(), out int a) || a is < 0 or > 1
            || !int.TryParse(parts[3].Trim(), out int s) || s is < 0 or > 1) return false;
        ctrl = c == 1; alt = a == 1; shift = s == 1;
        return true;
    }

    /// <summary>人类可读的按键名（"Ctrl + Home"）；0 / 解析失败 = 未绑定。</summary>
    public static string Describe(string? value)
    {
        if (!TryParse(value, out int keyCode, out bool ctrl, out bool alt, out bool shift) || keyCode == 0)
        {
            return "未绑定";
        }

        var parts = new List<string>(4);
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(DescribeKey(keyCode));
        return string.Join(" + ", parts);
    }

    /// <summary>虚拟键码 → 按键名（覆盖字母 / 数字 / F 键 / 小键盘 / 常用功能键）。</summary>
    public static string DescribeKey(int keyCode) => keyCode switch
    {
        >= 0x30 and <= 0x39 => ((char)keyCode).ToString(),
        >= 0x41 and <= 0x5A => ((char)keyCode).ToString(),
        >= 0x70 and <= 0x87 => "F" + (keyCode - 0x6F),
        >= 0x60 and <= 0x69 => "小键盘" + (keyCode - 0x60),
        0x20 => "空格", 0x1B => "Esc", 0x09 => "Tab", 0x08 => "退格", 0x0D => "回车",
        0x21 => "PageUp", 0x22 => "PageDown", 0x23 => "End", 0x24 => "Home",
        0x2D => "Insert", 0x2E => "Delete",
        0x26 => "↑", 0x28 => "↓", 0x25 => "←", 0x27 => "→",
        0x10 => "Shift", 0x11 => "Ctrl", 0x12 => "Alt",
        0xBA => ";", 0xBB => "=", 0xBC => ",", 0xBD => "-", 0xBE => ".", 0xBF => "/", 0xC0 => "`",
        0xDB => "[", 0xDC => "\\", 0xDD => "]", 0xDE => "'",
        _ => "键 " + keyCode,
    };
}
