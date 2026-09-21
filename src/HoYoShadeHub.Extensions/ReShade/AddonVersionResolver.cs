using System.Diagnostics;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>
/// addon 版本号的兜底：文件名括号里没有版本时，退回 **PE 版本资源**。
///
/// <para>实测（用户真实目录）：</para>
/// <code>
/// dlss5-bridge.addon64                名字里没版本，FileVersion = 1.4.13-pre7   ← 能用
/// renodx-neural-interposer-nvngx.dll (23.1.0 RC1).addon64   文件名 23.1.0 RC1 / PE 19.0.0.1
/// renodx-dlss(9.17.12).addon64        PE 里是 1789595968（时间戳）        ← 没用，所以文件名优先
/// </code>
/// </summary>
public static class AddonVersionResolver
{
    /// <summary>文件名里的版本优先；没有才读 PE</summary>
    public static string? Resolve(string? addonFilePath, string? fileNameVersion)
    {
        if (!string.IsNullOrWhiteSpace(fileNameVersion))
        {
            return fileNameVersion.Trim();
        }

        if (string.IsNullOrWhiteSpace(addonFilePath) || !File.Exists(addonFilePath))
        {
            return null;
        }

        try
        {
            FileVersionInfo info = FileVersionInfo.GetVersionInfo(addonFilePath);
            string? version = !string.IsNullOrWhiteSpace(info.FileVersion) ? info.FileVersion : info.ProductVersion;
            return IsMeaningful(version) ? version!.Trim() : null;
        }
        catch
        {
            // 非 PE / 被占用 / 权限不足都当读不到
            return null;
        }
    }

    /// <summary>过滤掉「1789595968」这种被写进版本字段的时间戳</summary>
    private static bool IsMeaningful(string? version) =>
        !string.IsNullOrWhiteSpace(version)
        && !(version.Length >= 9 && version.All(char.IsAsciiDigit));
}
