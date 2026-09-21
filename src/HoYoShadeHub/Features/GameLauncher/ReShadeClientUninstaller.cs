using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>删除结果</summary>
public sealed record ReShadeClientUninstallResult(bool Ok, IReadOnlyList<string> DeletedFiles, string? Error);

/// <summary>
/// 删掉**某一个客户端目录**里的 HoYoShade / ReShade 改动（=「卸载/还原 ReShade 改动」）。
///
/// <para>
/// 原来这段逻辑写在游戏设置对话框里，现在抽出来共用：对话框、以及游戏图标右键菜单都用它。
/// 删的都是"装进去的那几样"：ReShade 主 dll / ini / log、inject.exe、addon、reshade-shaders 目录。
/// </para>
/// </summary>
internal static class ReShadeClientUninstaller
{
    /// <summary>根目录下要删的文件</summary>
    public static readonly string[] RootFiles =
    [
        "ReShade.ini",
        "ReShade.log",
        "opengl32.dll",
        "opengl64.dll",
        "ReShade32.dll",
        "ReShade64.dll",
        "inject.exe",
        "ShaderToggler.ini",
    ];

    public static Task<ReShadeClientUninstallResult> UninstallAsync(string? installPath, ILogger? logger = null) =>
        Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
            {
                return new ReShadeClientUninstallResult(false, [], "这个游戏还没定位到安装目录。");
            }

            var deleted = new List<string>();

            try
            {
                foreach (string file in RootFiles)
                {
                    string path = Path.Combine(installPath, file);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        deleted.Add(file);
                    }
                }

                foreach (string addon in Directory.GetFiles(installPath, "*.addon32")
                             .Concat(Directory.GetFiles(installPath, "*.addon64")))
                {
                    File.Delete(addon);
                    deleted.Add(Path.GetFileName(addon));
                }

                string shaders = Path.Combine(installPath, "reshade-shaders");
                if (Directory.Exists(shaders))
                {
                    Directory.Delete(shaders, true);
                    deleted.Add(@"reshade-shaders\");
                }
            }
            catch (Exception ex)
            {
                logger?.LogError(ex, "Failed to uninstall ReShade files from {Path}", installPath);
                return new ReShadeClientUninstallResult(false, deleted, ex.Message);
            }

            logger?.LogInformation("Removed {Count} ReShade items from {Path}", deleted.Count, installPath);
            return new ReShadeClientUninstallResult(true, deleted, null);
        });
}
