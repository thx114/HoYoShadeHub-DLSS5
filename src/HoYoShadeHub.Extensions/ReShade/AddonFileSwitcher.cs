namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>全局开关一个 addon 文件的结果</summary>
public sealed record AddonToggleResult(bool Ok, string? Path, string? Error)
{
    public static AddonToggleResult Already(string path) => new(true, path, null);
}

/// <summary>
/// **全局**启用/禁用插件：重命名 <c>.addon64</c> ↔ <c>.addon64x</c>（32 位同理）。
///
/// <para>
/// 和「按游戏禁用」（写那个游戏 <c>ReShade.ini</c> 的 <c>DisabledAddons</c>）是两回事：
/// 改名之后 ReShade 根本扫不到这个文件，**所有游戏一起失效** —— 界面上必须明确警告。
/// 社区里一直在这么做（用户真实目录里那个 <c>renodx-dlss(ShortFuse_9.11.6).addon64x</c> 就是这么来的）。
/// </para>
/// </summary>
public static class AddonFileSwitcher
{
    /// <summary>禁用后的文件名（<c>a.addon64</c> → <c>a.addon64x</c>）</summary>
    public static string DisabledNameOf(string fileName) =>
        AddonFileInfo.Parse(fileName) is { IsRenamedDisabled: false }
            ? fileName + "x"
            : fileName;

    /// <summary>启用后的文件名（<c>a.addon64x</c> → <c>a.addon64</c>）</summary>
    public static string EnabledNameOf(string fileName) =>
        AddonFileInfo.Parse(fileName) is { IsRenamedDisabled: true } info && info.FileName.EndsWith('x')
            ? fileName[..^1]
            : fileName;

    public static bool IsDisabledName(string fileName) =>
        AddonFileInfo.Parse(fileName)?.IsRenamedDisabled == true;

    /// <summary>改名；目标文件已经存在时不动手，返回原因</summary>
    public static AddonToggleResult SetEnabled(string addonFilePath, bool enabled)
    {
        try
        {
            string currentPath = addonFilePath;
            if (!File.Exists(currentPath))
            {
                // The global-plugin card keeps the manifest's original enabled
                // path. After disabling, that path no longer exists; recover the
                // sibling .addon64x/.addon32x file so the same card can re-enable it.
                string? candidateDirectory = Path.GetDirectoryName(Path.GetFullPath(addonFilePath));
                if (candidateDirectory is not null)
                {
                    string fileName = Path.GetFileName(addonFilePath);
                    string alternateName = IsDisabledName(fileName)
                        ? EnabledNameOf(fileName)
                        : DisabledNameOf(fileName);
                    string alternatePath = Path.Combine(candidateDirectory, alternateName);
                    if (File.Exists(alternatePath))
                    {
                        currentPath = alternatePath;
                    }
                }
            }

            if (!File.Exists(currentPath))
            {
                return new AddonToggleResult(false, null, "文件不在了：" + addonFilePath);
            }

            string directory = Path.GetDirectoryName(Path.GetFullPath(currentPath))!;
            string currentName = Path.GetFileName(currentPath);
            string targetName = enabled ? EnabledNameOf(currentName) : DisabledNameOf(currentName);

            if (string.Equals(currentName, targetName, StringComparison.Ordinal))
            {
                return AddonToggleResult.Already(currentPath);
            }

            string targetPath = Path.Combine(directory, targetName);
            if (File.Exists(targetPath))
            {
                return new AddonToggleResult(false, null, $"同名文件已经存在：{targetName}");
            }

            File.Move(currentPath, targetPath);
            return new AddonToggleResult(true, targetPath, null);
        }
        catch (Exception ex)
        {
            return new AddonToggleResult(false, null, "改名失败：" + ex.Message);
        }
    }
}
