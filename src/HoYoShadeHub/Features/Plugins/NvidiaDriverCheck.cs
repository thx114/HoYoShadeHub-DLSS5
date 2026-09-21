using Microsoft.Win32;
using System;
using System.Linq;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>驱动检查的严重程度</summary>
internal enum DriverCheckLevel
{
    Ok = 0,
    Warning = 1,
    Error = 2,
}

internal sealed record DriverCheckResult(DriverCheckLevel Level, string? Version, string Message);

/// <summary>
/// 用 DLSS5 插件时检查 NVIDIA 驱动版本（用户要求的区间）：

/// <list type="bullet">
/// <item>&gt; 616.64 → 红：驱动版本高于 616.64 可能存在 dlss5 插件兼容性问题</item>
/// <item>&lt; 616.56 → 黄：驱动版本低于 616.56 可能存在些微 dlss5 插件兼容性问题</item>
/// <item>&lt; 610.47 → 红：驱动版本低于 610.47 可能 dlss5 插件报错</item>
/// </list>
///
/// <para>版本号来源：注册表卸载项里 NVIDIA Graphics Driver 的 DisplayVersion（用户看到的那串），
/// 拿不到就退回显卡驱动 <c>DriverVersion</c>（<c>32.0.15.6636</c> → <c>566.36</c>）。</para>
/// </summary>
internal static class NvidiaDriverCheck
{
    private const int MaxOk = 61664;      // 616.64
    private const int MinSoft = 61656;    // 616.56
    private const int MinHard = 61047;    // 610.47

    public static DriverCheckResult Check()
    {
        string? version = TryGetDriverVersion();
        if (string.IsNullOrWhiteSpace(version) || !TryParse(version, out int value))
        {
            return new DriverCheckResult(DriverCheckLevel.Ok, version, string.Empty);
        }

        if (value < MinHard)
        {
            return new DriverCheckResult(DriverCheckLevel.Error, version, $"驱动版本低于610.47可能dlss5插件报错（当前 {version}）");
        }

        if (value > MaxOk)
        {
            return new DriverCheckResult(DriverCheckLevel.Error, version, $"驱动版本高于616.64可能存在dlss5插件兼容性问题（当前 {version}）");
        }

        if (value < MinSoft)
        {
            return new DriverCheckResult(DriverCheckLevel.Warning, version, $"驱动版本低于616.56可能存在些微dlss5插件兼容性问题（当前 {version}）");
        }

        return new DriverCheckResult(DriverCheckLevel.Ok, version, $"驱动版本 {version}");
    }

    /// <summary>
    /// GeForce Game Ready 驱动版本（就是 NV app「已安装」里显示的那串，例如 <c>616.64</c>）。
    ///
    /// <para>
    /// 顺序（用户两次纠正过，别再动）：
    /// <list type="number">
    /// <item><b>卸载项</b>里那个「NVIDIA 图形驱动程序 / NVIDIA Graphics Driver」的 <c>DisplayVersion</c>
    /// —— NV app 显示的就是它，而且**名字是本地化的**，英文匹配会漏（中文机上是「图形驱动程序」）；</item>
    /// <item>拿不到才退回显示适配器类键的 <c>DriverVersion</c>（<c>32.0.16.1664</c> → <c>616.64</c>），
    /// 而且**必须确认那块是 NVIDIA**：本机 <c>0000</c> 是 AMD Radeon 610M，按「以 3 开头」的松判断会把
    /// AMD 的 <c>32.0.12011.1010</c> 算成 <c>110.10</c>（用户报过「检测到的还是不对」）。</item>
    /// </list>
    /// </para>
    /// </summary>
    public static string? TryGetDriverVersion()
    {
        string? fromUninstall = TryGetFromUninstallKeys();
        if (!string.IsNullOrWhiteSpace(fromUninstall))
        {
            return fromUninstall;
        }

        return TryGetFromDisplayAdapter();
    }

    /// <summary>「NNN.NN」形状（616.64）—— NV 的**驱动包**版本长这样，NV App / ShadowPlay 那些不是</summary>
    private static bool LooksLikeDriverVersion(string version) =>
        System.Text.RegularExpressions.Regex.IsMatch(version.Trim(), @"^\d{3}\.\d{1,2}$");

    /// <summary>卸载项名字是不是「显卡驱动包」（名字是本地化的，只match英文会漏）</summary>
    private static bool IsGraphicsDriverName(string displayName)
    {
        foreach (string pattern in GraphicsDriverNamePatterns)
        {
            if (displayName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 各语言下「NVIDIA 图形驱动程序」在卸载项里的写法。
    /// 本机中文 Windows 是「NVIDIA 图形驱动程序 616.64」——只匹配 "Graphics Driver" 会直接漏掉。
    /// </summary>
    private static readonly string[] GraphicsDriverNamePatterns =
    [
        "Graphics Driver",
        "图形驱动程序",
        "圖形驅動程式",
        "Grafiktreiber",
        "グラフィックス ドライバー",
        "그래픽 드라이버",
        "Pilote graphique",
        "Controlador de gráficos",
    ];

    private static string? TryGetFromUninstallKeys()
    {
        try
        {
            using RegistryKey? root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
            if (root is null)
            {
                return null;
            }

            string? best = null;
            int bestValue = 0;
            string? fallback = null;
            int fallbackValue = 0;

            foreach (string name in root.GetSubKeyNames())
            {
                using RegistryKey? key = root.OpenSubKey(name);
                string? display = key?.GetValue("DisplayName") as string;
                if (display is null || !display.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (key?.GetValue("DisplayVersion") is not string version)
                {
                    continue;
                }

                version = version.Trim();

                // NV 的驱动包装成一个「NNN.NN」形状（616.64）；NV App / ShadowPlay 那些是 11.0.9.251 之类
                if (!LooksLikeDriverVersion(version) || !TryParse(version, out int parsed))
                {
                    continue;
                }

                if (IsGraphicsDriverName(display))
                {
                    if (parsed > bestValue)
                    {
                        bestValue = parsed;
                        best = version;
                    }
                }
                else if (parsed > fallbackValue)
                {
                    // 名字认不出来（别的语言）时留个兜底：取最大的那个 NNN.NN
                    fallbackValue = parsed;
                    fallback = version;
                }
            }

            return best ?? fallback;
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? TryGetFromDisplayAdapter()
    {
        try
        {
            using RegistryKey? controllers = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
                .OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (controllers is null)
            {
                return null;
            }

            foreach (string name in controllers.GetSubKeyNames())
            {
                using RegistryKey? key = controllers.OpenSubKey(name);
                if (key?.GetValue("DriverVersion") is not string raw)
                {
                    continue;
                }

                // 必须确认这块适配器是 NVIDIA —— 本机第一块是 AMD Radeon 610M，
                // 用「以 3 开头」这种松判断会把它的 32.0.12011.1010 算成 110.10
                string provider = key.GetValue("ProviderName") as string ?? string.Empty;
                string description = key.GetValue("DriverDesc") as string ?? string.Empty;
                bool isNvidia = provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                                || description.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
                if (!isNvidia)
                {
                    continue;
                }

                string? converted = ConvertDriverVersion(raw);
                if (converted is not null)
                {
                    return converted;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    /// <summary><c>32.0.15.6636</c> → <c>566.36</c>（业界通用的换算）</summary>
    public static string? ConvertDriverVersion(string raw)
    {
        try
        {
            string[] parts = raw.Split('.');
            if (parts.Length < 4)
            {
                return null;
            }

            int last = (int.Parse(parts[2]) * 10000) + int.Parse(parts[3]);
            int four = last % 100000;
            return $"{four / 100}.{four % 100:00}";
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把 <c>566.36</c> / <c>566.36.01</c> 之类换算成比较用的整数（56636）</summary>
    public static bool TryParse(string version, out int value)
    {
        value = 0;

        try
        {
            string[] parts = version.Trim().Split('.');
            if (parts.Length < 2 || !int.TryParse(parts[0], out int major))
            {
                return false;
            }

            string minorRaw = new([.. parts[1].Where(char.IsDigit)]);
            if (minorRaw.Length == 0)
            {
                return false;
            }

            int minor = int.Parse(minorRaw.Length == 1 ? minorRaw + "0" : minorRaw[..2]);
            value = (major * 100) + minor;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
