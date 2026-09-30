using System.Text.Json;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>
/// 覆盖包（每游戏插件包）里用户可放的扩展内容：启动器生成 / 管理的只有 <c>Addons\</c> 子目录，
/// 其余文件都由用户（或扩展包作者）维护，存在即生效。
///
/// <list type="bullet">
/// <item><c>ini_config.json</c> —— 每次启动写进游戏 ReShade.ini 的节/键（见 <see cref="GamePackIniConfig"/>）；</item>
/// <item><c>auto.json</c> —— 自定义动作（见 <see cref="PackAutoAction"/>）；</item>
/// <item><c>presets\</c> —— 随包导入的 DLSS5 预设（复制进 <c>Addons\DLSS5-Presets\</c>）；</item>
/// <item><c>game_files\</c> —— 覆盖到游戏目录的文件（相对结构保持）；</item>
/// <item><c>launcher_files\</c> —— 覆盖到启动器目录的文件。</item>
/// </list>
/// </summary>
public static class GameAddonPackUserContent
{
    public const string IniConfigFileName = "ini_config.json";

    public const string AutoActionFileName = "auto.json";

    public const string PresetsFolderName = "presets";

    public const string GameFilesFolderName = "game_files";

    public const string LauncherFilesFolderName = "launcher_files";

    /// <summary>这个包目录里有没有用户内容（有就让包目录常驻，不被「没选版本」收掉）</summary>
    public static bool HasAny(string? packDirectory)
    {
        if (string.IsNullOrWhiteSpace(packDirectory) || !Directory.Exists(packDirectory))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(packDirectory, IniConfigFileName))
                   || File.Exists(Path.Combine(packDirectory, AutoActionFileName))
                   || Directory.Exists(Path.Combine(packDirectory, PresetsFolderName))
                   || Directory.Exists(Path.Combine(packDirectory, GameFilesFolderName))
                   || Directory.Exists(Path.Combine(packDirectory, LauncherFilesFolderName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>包根目录（= Addons 目录的上一级；addonPath 不是包时返回 null）</summary>
    public static string? PackRootOfAddonDirectory(string? addonPath)
    {
        if (string.IsNullOrWhiteSpace(addonPath) || !GameAddonPack.IsPackDirectory(addonPath))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(addonPath).TrimEnd('\\', '/'));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把包 <c>presets\</c> 下的预设复制进 <c>&lt;AddonPath&gt;\DLSS5-Presets\</c>
    /// （游戏内插件和启动器预设页都扫这里）。按内容幂等，预设页里已有的不重写。
    /// </summary>
    /// <returns>复制 / 更新了几个文件</returns>
    public static int SyncPresets(string? packRoot, string? addonDirectory)
    {
        string source = packRoot is null ? string.Empty : Path.Combine(packRoot, PresetsFolderName);
        if (source.Length == 0 || addonDirectory is null || !Directory.Exists(source))
        {
            return 0;
        }

        int copied = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(source, "*.ini", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                string target = Path.Combine(addonDirectory, Dlss5PresetLibrary.PresetFolderName, relative);

                try
                {
                    if (File.Exists(target))
                    {
                        byte[] existing = File.ReadAllBytes(target);
                        byte[] incoming = File.ReadAllBytes(file);
                        if (existing.AsSpan().SequenceEqual(incoming))
                        {
                            continue;
                        }
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                    copied++;
                }
                catch
                {
                    // 单个预设复制失败不影响其它
                }
            }
        }
        catch
        {
            // 源目录读不了就算了
        }

        return copied;
    }

    /// <summary>
    /// 把 <c>game_files\</c> / <c>launcher_files\</c> 覆盖到目标根目录（相对结构保持）。
    /// </summary>
    /// <returns>覆盖了几个文件</returns>
    public static int OverlayFiles(string? packRoot, string folderName, string? targetRoot)
    {
        string source = packRoot is null ? string.Empty : Path.Combine(packRoot, folderName);
        if (source.Length == 0 || targetRoot is null || !Directory.Exists(source))
        {
            return 0;
        }

        int copied = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(source, file);
                string target = Path.Combine(targetRoot, relative);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                    copied++;
                }
                catch
                {
                    // 单个文件失败继续
                }
            }
        }
        catch
        {
            // ignore
        }

        return copied;
    }
}
