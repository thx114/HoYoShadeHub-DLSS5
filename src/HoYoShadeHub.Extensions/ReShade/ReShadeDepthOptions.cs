namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>一个 ReShade 深度相关的全局预处理器定义（写在活动预设的 PreprocessorDefinitions 里）</summary>
/// <param name="Name">宏名（如 RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN）</param>
/// <param name="Label">卡片上显示的名字</param>
/// <param name="ToolTip">悬停说明</param>
/// <param name="IsNumber">true = 数值型（倍数 / 远平面）；false = 0/1 开关</param>
/// <param name="DefaultValue">清空输入时回写的默认值（行为等同于 ReShade 默认）</param>
public sealed record ReShadeDepthOption(string Name, string Label, string ToolTip, bool IsNumber, string DefaultValue = "");

/// <summary>
/// ReShade 深度宏读写；全局配置使用 GENERAL，预设使用根节。
/// 只更新指定宏，其余定义保留。
/// </summary>
public static class ReShadeDepthOptions
{
    public static readonly ReShadeDepthOption UpsideDown = new(
        "RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN", "深度上下翻转",
        "深度缓冲上下颠倒时打开（常见于 OpenGL / 部分 DX11 游戏）。崩铁、绝区零诊断深度时先查这一项。", false);
    public static readonly ReShadeDepthOption Reversed = new(
        "RESHADE_DEPTH_INPUT_IS_REVERSED", "深度前后反向",
        "深度是反向 Z（1=近、0=远）时打开。", false, "1");
    public static readonly ReShadeDepthOption Logarithmic = new(
        "RESHADE_DEPTH_INPUT_IS_LOGARITHMIC", "对数深度",
        "游戏用对数深度缓冲（远平面精度分布）时打开。", false);
    public static readonly ReShadeDepthOption Mirrored = new(
        "RESHADE_DEPTH_INPUT_IS_MIRRORED", "深度左右镜像",
        "深度缓冲左右颠倒时打开。", false);
    public static readonly ReShadeDepthOption Multiplier = new(
        "RESHADE_DEPTH_MULTIPLIER", "深度倍数",
        "整体缩放深度值（默认 1.0）。一般不用动。", true, "1.0");
    public static readonly ReShadeDepthOption FarPlane = new(
        "RESHADE_DEPTH_LINEARIZATION_FAR_PLANE", "线性化远平面",
        "线性化深度用的远平面值（默认 1000.0）。游戏实际远平面不同会导致深度比例不对。", true, "1000.0");

    public static IReadOnlyList<ReShadeDepthOption> All => [UpsideDown, Reversed, Logarithmic, Mirrored, Multiplier, FarPlane];

    /// <summary>读宏当前值；没有写 = null（开关型视为关，数值型视为默认）。</summary>
    public static string? Get(IniDocument preset, ReShadeDepthOption option, string section = IniDocument.RootSection)
    {
        string? raw = preset.GetValue(section, "PreprocessorDefinitions");
        if (raw is null) return null;
        foreach (string token in SplitDefinitions(raw))
        {
            string trimmed = token.Trim();
            int eq = trimmed.IndexOf('=');
            if (eq <= 0)
            {
                if (string.Equals(trimmed, option.Name, StringComparison.OrdinalIgnoreCase)) return "1";
                continue;
            }
            if (string.Equals(trimmed[..eq].Trim(), option.Name, StringComparison.OrdinalIgnoreCase))
            {
                return trimmed[(eq + 1)..].Trim().Trim('"');
            }
        }
        return null;
    }

    /// <summary>写入宏值（upsert：已存在就替换、不存在就追加），其余 token 不动。返回有没有实际改动。</summary>
    public static bool Set(IniDocument preset, ReShadeDepthOption option, string value, string section = IniDocument.RootSection)
    {
        string? raw = preset.GetValue(section, "PreprocessorDefinitions");
        var tokens = new List<string>();
        bool replaced = false;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (string token in SplitDefinitions(raw))
            {
                string trimmed = token.Trim();
                int eq = trimmed.IndexOf('=');
                string name = eq <= 0 ? trimmed : trimmed[..eq].Trim();
                if (string.Equals(name, option.Name, StringComparison.OrdinalIgnoreCase))
                {
                    if (!replaced)
                    {
                        tokens.Add(option.Name + "=" + value);
                        replaced = true;
                    }
                    // 重复的同名 token 一律丢掉（写重复本来就是坏状态）
                }
                else
                {
                    tokens.Add(token);
                }
            }
        }

        if (!replaced) tokens.Add(option.Name + "=" + value);
        string joined = string.Join(',', tokens);
        if (string.Equals(raw, joined, StringComparison.Ordinal)) return false;
        preset.SetValue(section, "PreprocessorDefinitions", joined);
        return true;
    }

    private static IEnumerable<string> SplitDefinitions(string raw)
    {
        bool quoted = false;
        int start = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] == '"' && (i == 0 || raw[i - 1] != '\\')) quoted = !quoted;
            if (raw[i] != ',' || quoted) continue;
            yield return raw[start..i];
            start = i + 1;
        }
        yield return raw[start..];
    }
}
