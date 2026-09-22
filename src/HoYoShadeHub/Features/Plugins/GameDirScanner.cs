using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>游戏目录 / MI 实例里扫出来的一条可疑项</summary>
internal sealed record GameDirSuspiciousEntry(string Path, string Reason, bool IsReparsePoint, bool IsDll)
{
    public string Name => System.IO.Path.GetFileName(Path);

    public string Describe() => IsReparsePoint
        ? $"{Name}　 链接到 {Reason}"
        : $"{Name}　（{Reason}）";
}

/// <summary>
/// 第 13 / 15 条：扫「常见的第三方 dll」和「超链接（软/硬链接）」。
///
/// <para>
/// 为什么这两样一起查：游戏目录里躺着的注入型 dll（dxgi.dll / d3d11.dll / version.dll ）
/// 和 ReShade 抢的是同一批钩子，谁先加载谁赢；而<b>软链接</b>是「看着像游戏自己的 dll，
/// 其实指向别处」的常见伪装  两者都会让 DLSS5 插件挂不上或游戏起不来。
/// </para>
///
/// <para>
/// 只扫游戏根目录 + 一级子目录（不递归全盘），而且只把「注入型 dll 名」当嫌疑 
/// 游戏自带的 nvngx_dlss*.dll、UnityPlayer.dll 这类不算。
/// </para>
/// </summary>
internal class GameDirScanner
{
    private static readonly ILogger Logger = AppConfig.GetLogger<GameDirScanner>();

    /// <summary>注入型 dll 的常见文件名  出现在游戏目录就该问一句</summary>
    public static readonly string[] InjectableDllNames =
    [
        "d3d11.dll", "dxgi.dll", "d3d12.dll", "d3d9.dll", "opengl32.dll", "version.dll",
        "winmm.dll", "winhttp.dll", "wininet.dll", "dinput8.dll", "dinput.dll", "dsound.dll",
        "xinput1_3.dll", "xinput9_1_0.dll", "d3dcompiler_47.dll",
        "reshade64.dll", "reshade32.dll", "reshade.dll",
        "optiscaler.dll", "nvngx.dll", "fakenvapi.dll", "dlss-enabler-upscaler.dll",
        "dlssg_to_fsr3_amd_is_better.dll", "dbghelp.dll", "xinput1_4.dll",
    ];

    /// <summary>我们自己的东西（Renovation/RenoDX/HoYoShade 的运行时）不算第三方</summary>
    private static readonly string[] OwnHints = ["renodx", "hoyoshade", "hysx"];

    /// <summary>这些 dll 是游戏/驱动正常的运行时，不算嫌疑</summary>
    private static readonly string[] AlwaysAllowed =
    [
        "nvngx_dlss.dll", "nvngx_dlssg.dll", "nvngx_dlssd.dll", "nvngx_dlssnr.dll",
        "nvngx-wrapper.dll", "nvapi64.dll", "nvgfx.dll",   
        // XXMI / 3DMigoto 实例的固定组成部分（用户实机核对过，不是第三方）：
        //   3dmloader.dll    = 3DMigoto 加载器本体
        //   d3d11.dll        = 3DMigoto 的 proxy DLL（它就是靠这个注入的）
        //   d3dcompiler_47.dll = 着色器编译运行时，XXMI 自带
        // 注意 d3d11.dll 也在 InjectableDllNames 里（游戏目录出现它确实可疑），
        // 但为了 XXMI 实例不误报，这里统一放行  XXMI 检测本来就只在 MI 实例目录里跑。
        "3dmloader.dll", "d3dcompiler_47.dll", "d3d11.dll",
    ];

    /// <summary>扫一个目录（根 + 一级子目录），返回可疑项</summary>
    public static List<GameDirSuspiciousEntry> Scan(string? directory)
    {
        List<GameDirSuspiciousEntry> result = [];

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return result;
        }

        try
        {
            foreach (string file in EnumerateShallow(directory))
            {
                string name = Path.GetFileName(file).ToLowerInvariant();

                // 软链接 / 硬链接：不管叫什么名字都值得说一句
                bool isLink = IsReparsePoint(file);
                if (isLink)
                {
                    result.Add(new GameDirSuspiciousEntry(file, ResolveLinkTarget(file), true, name.EndsWith(".dll")));
                    continue;
                }

                if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (AlwaysAllowed.Contains(name) || OwnHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (InjectableDllNames.Contains(name))
                {
                    result.Add(new GameDirSuspiciousEntry(file, "常见的第三方注入型 dll", false, true));
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "扫游戏目录失败：{Dir}", directory);
        }

        return result;
    }

    private static IEnumerable<string> EnumerateShallow(string directory)
    {
        IEnumerable<string> root;

        try
        {
            root = Directory.EnumerateFiles(directory);
        }
        catch
        {
            yield break;
        }

        foreach (string file in root)
        {
            yield return file;
        }

        IEnumerable<string> subdirs;

        try
        {
            subdirs = Directory.EnumerateDirectories(directory);
        }
        catch
        {
            yield break;
        }

        foreach (string sub in subdirs)
        {
            IEnumerable<string> files;

            try
            {
                files = Directory.EnumerateFiles(sub, "*.dll");
            }
            catch
            {
                continue;
            }

            foreach (string file in files)
            {
                yield return file;
            }
        }
    }

    public static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveLinkTarget(string path)
    {
        try
        {
            FileSystemInfo? target = new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true);
            return target?.FullName ?? "（解析不出目标）";
        }
        catch (Exception ex)
        {
            return "（解析失败：" + ex.Message + "）";
        }
    }

    /// <summary>
    /// 自动清理：把扫出来的项「移到备份目录」而不是删掉  万一误判（比如用户自己装的
    /// 必须的 dll），还能从备份里拿回来。备份放在 &lt;用户数据目录&gt;\.hysx\gamedir-backup\&lt;时间戳&gt;\。
    /// </summary>
    public static async Task<string?> FixAsync(string? directory, IReadOnlyList<GameDirSuspiciousEntry> entries)
    {
        if (string.IsNullOrWhiteSpace(directory) || entries.Count == 0)
        {
            return "没有要清理的项。";
        }

        await Task.Yield();

        string? userData = AppConfig.UserDataFolder;
        if (string.IsNullOrWhiteSpace(userData))
        {
            return "读不到用户数据目录，不敢动游戏目录里的文件。";
        }

        string backup = Path.Combine(userData, ".hysx", "gamedir-backup",
            DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(backup);

        int moved = 0;
        var failed = new List<string>();

        foreach (GameDirSuspiciousEntry entry in entries)
        {
            try
            {
                if (!File.Exists(entry.Path))
                {
                    continue;
                }

                string target = Path.Combine(backup, entry.Name);

                // 同名冲突加个序号
                int n = 1;
                while (File.Exists(target))
                {
                    target = Path.Combine(backup, $"{Path.GetFileNameWithoutExtension(entry.Name)}-{n++}{Path.GetExtension(entry.Name)}");
                }

                if (entry.IsReparsePoint)
                {
                    // 链接文件直接删（它的目标在别处，不该跟着删）
                    File.Delete(entry.Path);
                }
                else
                {
                    File.Move(entry.Path, target);
                }

                moved++;
            }
            catch (Exception ex)
            {
                failed.Add($"{entry.Name}（{ex.Message}）");
            }
        }

        string message = $"已把 {moved} 个文件移出游戏目录（备份在 {backup}） 确认游戏还能正常启动后可以删掉这个备份目录。";

        if (failed.Count > 0)
        {
            message += "\n这几个动不了（游戏在跑 / 没有权限）：" + string.Join("、", failed);
        }

        if (moved == 0)
        {
            message = "一个都没移走（多半是游戏正在运行或没权限）。" + (failed.Count > 0 ? "\n" + string.Join("、", failed) : string.Empty);
        }

        Logger.LogInformation("GameDirScanner cleanup: {Message}", message);
        return message;
    }
}
