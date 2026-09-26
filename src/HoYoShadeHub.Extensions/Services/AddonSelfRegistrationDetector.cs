namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// 判断一个插件（addon）**自己会不会往 ReShade.ini 里登记自己的加载方式**。
///
/// <para>
/// 用户要求（第 7 条）：插件自己会登记（比如自己在 ini 里写 <c>LoadFromDllMain</c> /
/// <c>DisabledAddons</c>，或者带着 <c>WritePrivateProfileString</c> 这类写 ini 的导入）时，
/// 「从 DllMain 加载」这个勾选项就不该显示 —— 两边各写各的会打架。判不了就保持现状（显示）。
/// </para>
///
/// <para>
/// 判据是二进制的字符串痕迹（ASCII 和 UTF-16LE 两种），刻意做得保守：
/// 只有明确出现「自己写 ini / 自己登记加载方式」的痕迹才算；纯读 ini 不算。
/// </para>
/// </summary>
public static class AddonSelfRegistrationDetector
{
    /// <summary>
    /// 「它会自己登记加载方式 / 自己写 ini」的字符串痕迹。
    /// 刻意只留两条最明确的；DisabledAddons 单独出现可能只是读，不算。
    /// </summary>
    private static readonly string[] Needles =
    [
        "LoadFromDllMain",
        "WritePrivateProfileString",
    ];

    /// <summary>读文件判定；读不了 / 文件不存在一律当「判不了」= false（保持显示）</summary>
    public static bool Detect(string? filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return false;
            }

            return Detect(File.ReadAllBytes(filePath));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>纯字节判定（自测可以直接喂假二进制）</summary>
    public static bool Detect(byte[]? bytes)
    {
        if (bytes is null || bytes.Length == 0)
        {
            return false;
        }

        foreach (string needle in Needles)
        {
            if (ContainsAscii(bytes, needle) || ContainsUtf16(bytes, needle))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        byte[] pattern = System.Text.Encoding.ASCII.GetBytes(needle);
        return IndexOf(haystack, pattern) >= 0;
    }

    private static bool ContainsUtf16(byte[] haystack, string needle)
    {
        byte[] pattern = System.Text.Encoding.Unicode.GetBytes(needle);
        return IndexOf(haystack, pattern) >= 0;
    }

    private static int IndexOf(byte[] haystack, byte[] pattern)
    {
        if (pattern.Length == 0 || haystack.Length < pattern.Length)
        {
            return -1;
        }

        int last = haystack.Length - pattern.Length;
        for (int i = 0; i <= last; i++)
        {
            int j = 0;
            while (j < pattern.Length && haystack[i + j] == pattern[j])
            {
                j++;
            }

            if (j == pattern.Length)
            {
                return i;
            }
        }

        return -1;
    }
}
