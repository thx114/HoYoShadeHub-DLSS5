using HoYoShadeHub.Extensions.I18n;
using HoYoShadeHub.Extensions.ReShade;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 「汉化」的记账 + 启动游戏前重打。
///
/// 为什么需要它：HoYoShade 在**启动游戏**的时候会把自己的插件（reshade-shaders\Addons 下的 .addon64）
/// 重新部署一遍，我们改过的副本会被覆盖回原版 —— 用户看到的现象就是「上次汉化过，这次进去又是英文 /
/// 半中半英」。所以点过汉化的插件要记下来，启动前再打一次。
///
/// 重打是幂等的：已经打过的那份里英文 needle 早就没了，<see cref="AddonLocalizer.Apply"/> 会返回 0 条，
/// 不重新备份、也不会把中文当英文再换一遍。
/// </summary>
internal sealed class AddonLocalizationJob
{
    private static readonly ILogger Logger = AppConfig.GetLogger<AddonLocalizationJob>();

    /// <summary>记了哪些插件被汉化过（存绝对路径：同一份插件可能有好几个 HoYoShade 目录）</summary>
    public static string StatePath => string.IsNullOrWhiteSpace(AppConfig.UserDataFolder)
        ? Path.Combine(Path.GetTempPath(), "HoYoShadeHub-i18n", "localized.json")
        : Path.Combine(AppConfig.UserDataFolder, ".hysx", "i18n", "localized.json");

    public static List<string> Load()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return [];
            }

            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(StatePath)) ?? [];
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "读取汉化记账失败：{Path}", StatePath);
            return [];
        }
    }

    private static void Save(List<string> paths)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);

            if (paths.Count == 0)
            {
                if (File.Exists(StatePath))
                {
                    File.Delete(StatePath);
                }

                return;
            }

            File.WriteAllText(StatePath, JsonSerializer.Serialize(paths, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "写汉化记账失败：{Path}", StatePath);
        }
    }

    public static void Remember(string path)
    {
        List<string> paths = Load();

        if (paths.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        paths.Add(path);
        Save(paths);
    }

    public static void Forget(string path)
    {
        List<string> paths = Load();
        int removed = paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

        if (removed > 0)
        {
            Save(paths);
        }
    }

    /// <summary>
    /// 同一个插件的其它副本：便携版在 &lt;盘&gt;\...\HoYoShadeHub\HoYoShade\reshade-shaders\Addons，
    /// 默认装在 &lt;用户数据目录&gt;\HoYoShade\reshade-shaders\Addons —— 游戏读哪一份取决于它用的是哪个启动器。
    /// 所以汉化时把所有能便宜找到的副本都打上（浅层目录里叫 HoYoShade 的，最多往下 5 层）。
    /// </summary>
    public static List<string> FindCopies(string fileName)
    {
        List<string> copies = [];

        foreach (string root in CandidateRoots())
        {
            string path = Path.Combine(root, "reshade-shaders", "Addons", fileName);

            if (File.Exists(path) && !copies.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                copies.Add(path);
            }
        }

        return copies;
    }

    private static IEnumerable<string> CandidateRoots()
    {
        if (!string.IsNullOrWhiteSpace(AppConfig.UserDataFolder))
        {
            yield return Path.Combine(AppConfig.UserDataFolder, "HoYoShade");
        }

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            foreach (string dir in ShallowDirectories(drive.RootDirectory.FullName, 5))
            {
                if (string.Equals(Path.GetFileName(dir), "HoYoShade", StringComparison.OrdinalIgnoreCase))
                {
                    yield return dir;
                }
            }
        }
    }

    private static IEnumerable<string> ShallowDirectories(string root, int depth)
    {
        if (depth <= 0)
        {
            yield break;
        }

        IEnumerable<string> children;

        try
        {
            children = Directory.EnumerateDirectories(root);
        }
        catch
        {
            yield break;
        }

        foreach (string child in children)
        {
            yield return child;

            foreach (string nested in ShallowDirectories(child, depth - 1))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// 「全部汉化」（实验性，入口在「设置 → 实验性功能」）：把能找到的所有 addon 目录里、
    /// 能对上翻译表的插件都打一遍。插件页那两个按钮已按用户要求隐藏。
    /// </summary>
    public static async Task<string> LocalizeAllAsync(CancellationToken cancellationToken = default)
    {
        List<AddonI18nTable> tables = AddonLocalizer.LoadTables(GlobalPluginPage.I18nTableDirectory);
        List<string> targets = [];

        List<string> directories = [];

        foreach (string root in CandidateRoots())
        {
            directories.Add(Path.Combine(root, "reshade-shaders", "Addons"));
        }

        foreach (string directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (AddonFileInfo info in AddonFileInfo.ScanDirectory(directory))
            {
                if (AddonLocalizer.SelectTable(tables, info.FileName) is null)
                {
                    continue;
                }

                string path = Path.Combine(directory, info.FileName);

                if (!targets.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    targets.Add(path);
                }
            }
        }

        int entries = 0;
        List<string> done = [];

        foreach (string path in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            AddonI18nTable? table = AddonLocalizer.SelectTable(tables, Path.GetFileName(path));

            if (table is null)
            {
                continue;
            }

            try
            {
                if (AddonLocalizer.BackupPathOf(path, GlobalPluginPage.I18nBackupDirectory) is not null)
                {
                    AddonLocalizer.Restore(path, GlobalPluginPage.I18nBackupDirectory);
                }

                int applied = await Task.Run(
                    () => AddonLocalizer.Apply(path, table, GlobalPluginPage.I18nBackupDirectory).Applied,
                    cancellationToken);

                Remember(path);
                entries += applied;
                done.Add($"{Path.GetFileName(path)} → {applied} 条");
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "汉化插件失败：{Path}", path);
            }
        }

        string message = targets.Count == 0
            ? "没找到能汉化的插件（要 addon64 文件 + 对应翻译表）。"
            : $"汉化 {targets.Count} 份插件、共 {entries} 条：{string.Join("；", done)}";

        Logger.LogInformation("Localize all (experimental): {Message}", message);
        return message;
    }

    /// <summary>
    /// 启动游戏前重打一遍。返回一句给日志/提示用的话（没记过事就返回 null）。
    /// </summary>
    public static async Task<string?> ReapplyAsync(CancellationToken cancellationToken = default)
    {
        List<string> paths = Load();

        if (paths.Count == 0)
        {
            return null;
        }

        List<AddonI18nTable> tables = AddonLocalizer.LoadTables(GlobalPluginPage.I18nTableDirectory);
        int files = 0;
        int entries = 0;

        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                continue;
            }

            AddonI18nTable? table = AddonLocalizer.SelectTable(tables, Path.GetFileName(path));

            if (table is null)
            {
                continue;
            }

            try
            {
                AddonLocalizeResult result = await Task.Run(
                    () => AddonLocalizer.Apply(path, table, GlobalPluginPage.I18nBackupDirectory),
                    cancellationToken);

                if (result.Applied > 0)
                {
                    files++;
                    entries += result.Applied;
                    Logger.LogInformation("重新汉化 {Path}：{Message}", path, result.Message);
                }
            }
            catch (Exception ex)
            {
                // 文件被占用之类：不拦启动，只记一笔
                Logger.LogWarning(ex, "启动前重打汉化失败：{Path}", path);
            }
        }

        return files == 0 ? null : $"启动前重新汉化 {files} 个插件（{entries} 条）";
    }
}
