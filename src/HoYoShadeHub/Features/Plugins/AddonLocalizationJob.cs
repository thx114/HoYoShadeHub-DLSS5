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
///
/// 记账文件带**补丁算法版本**（<see cref="AddonLocalizer.AlgorithmVersion"/>）。版本比当前小 = 磁盘上那份是
/// 老算法打的：老算法会把中文切成半个 UTF-8、后面还留英文尾巴（「缩ling」「结构强度 sity」），
/// 而且英文 needle 早被写掉、没法在原地重打。所以升级时先把记账里的文件**从备份还原回原版**，
/// 再按新算法重打一遍（<see cref="ReapplyAsync"/> 启动前会自动做这件事）。
/// </summary>
internal sealed class AddonLocalizationJob
{
    private static readonly ILogger Logger = AppConfig.GetLogger<AddonLocalizationJob>();

    /// <summary>记了哪些插件被汉化过（存绝对路径：同一份插件可能有好几个 HoYoShade 目录）</summary>
    public static string StatePath => string.IsNullOrWhiteSpace(AppConfig.UserDataFolder)
        ? Path.Combine(Path.GetTempPath(), "HoYoShadeHub-i18n", "localized.json")
        : Path.Combine(AppConfig.UserDataFolder, ".hysx", "i18n", "localized.json");

    private static readonly JsonSerializerOptions _stateJson = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>记账文件：<c>{ "algorithm": 2, "files": [...] }</c></summary>
    private sealed record I18nState(int Algorithm, List<string> Files);

    public static List<string> Load() => LoadState().Files;

    /// <summary>记账里的算法版本比当前小 → 磁盘上那份是老算法打的，要先还原再重打。</summary>
    public static bool NeedsUpgrade() => LoadState().Algorithm < AddonLocalizer.AlgorithmVersion;

    private static I18nState LoadState()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return new I18nState(AddonLocalizer.AlgorithmVersion, []);
            }

            string json = File.ReadAllText(StatePath);

            // 老版本（v1）存的是一个纯字符串数组，没有版本号 —— 那就当算法版本 1
            if (json.TrimStart().StartsWith('['))
            {
                List<string> legacy = JsonSerializer.Deserialize<List<string>>(json) ?? [];
                return new I18nState(1, legacy);
            }

            I18nState? state = JsonSerializer.Deserialize<I18nState>(json, _stateJson);

            return state is null || state.Files is null
                ? new I18nState(AddonLocalizer.AlgorithmVersion, [])
                : state with { Files = [.. state.Files.Where(p => !string.IsNullOrWhiteSpace(p))] };
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "读取汉化记账失败：{Path}", StatePath);
            return new I18nState(AddonLocalizer.AlgorithmVersion, []);
        }
    }

    private static void Save(I18nState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);

            if (state.Files.Count == 0)
            {
                if (File.Exists(StatePath))
                {
                    File.Delete(StatePath);
                }

                return;
            }

            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, _stateJson));
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "写汉化记账失败：{Path}", StatePath);
        }
    }

    public static void Remember(string path)
    {
        I18nState state = LoadState();

        if (state.Files.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        // 版本号原样保留：只是多记了一个刚打过的文件，不代表老文件也重打过了
        state.Files.Add(path);
        Save(state);
    }

    public static void Forget(string path)
    {
        I18nState state = LoadState();

        if (state.Files.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)) > 0)
        {
            Save(state);
        }
    }

    /// <summary>整批按新算法还原重打过之后才调用（不然会把没重打的老文件标成新版本）。</summary>
    private static void MarkCurrent()
    {
        I18nState state = LoadState();

        if (state.Files.Count == 0 || state.Algorithm == AddonLocalizer.AlgorithmVersion)
        {
            return;
        }

        Save(state with { Algorithm = AddonLocalizer.AlgorithmVersion });
    }

    /// <summary>
    /// 同一个插件的其它副本：便携版在 &lt;盘&gt;\...\HoYoShadeHub\HoYoShade\reshade-shaders\Addons，
    /// 默认装在 &lt;用户数据目录&gt;\HoYoShade\reshade-shaders\Addons —— 游戏读哪一份取决于它用的是哪个启动器。
    /// 所以汉化时把所有能便宜找到的副本都打上（浅层目录里叫 HoYoShade 的，最多往下 5 层）。
    /// </summary>
    /// <summary>
    /// 找副本要扫所有固定盘（深度 5），十几秒很正常 —— 全程可取消（界面上那个「停止」按钮靠它）。
    /// </summary>
    public static List<string> FindCopies(string fileName, CancellationToken cancellationToken = default)
    {
        List<string> copies = [];

        foreach (string root in CandidateRoots(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(root, "reshade-shaders", "Addons", fileName);

            if (File.Exists(path) && !copies.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                copies.Add(path);
            }
        }

        return copies;
    }

    private static IEnumerable<string> CandidateRoots(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(AppConfig.UserDataFolder))
        {
            yield return Path.Combine(AppConfig.UserDataFolder, "HoYoShade");
        }

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            foreach (string dir in ShallowDirectories(drive.RootDirectory.FullName, 5, cancellationToken))
            {
                if (string.Equals(Path.GetFileName(dir), "HoYoShade", StringComparison.OrdinalIgnoreCase))
                {
                    yield return dir;
                }
            }
        }
    }

    private static IEnumerable<string> ShallowDirectories(string root, int depth, CancellationToken cancellationToken = default)
    {
        if (depth <= 0)
        {
            yield break;
        }

        cancellationToken.ThrowIfCancellationRequested();

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
            cancellationToken.ThrowIfCancellationRequested();
            yield return child;

            foreach (string nested in ShallowDirectories(child, depth - 1, cancellationToken))
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

        foreach (string root in CandidateRoots(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            directories.Add(Path.Combine(root, "reshade-shaders", "Addons"));
        }

        foreach (string directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();

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

        if (targets.Count > 0)
        {
            // 这一批全是「先还原、再按当前算法打」，所以可以放心把记账标成当前版本
            MarkCurrent();
        }

        string message = targets.Count == 0
            ? "没找到能汉化的插件（要 addon64 文件 + 对应翻译表）。"
            : $"汉化 {targets.Count} 份插件、共 {entries} 条：{string.Join("；", done)}";

        Logger.LogInformation("Localize all (experimental): {Message}", message);
        return message;
    }

    /// <summary>
    /// 「还原插件汉化」：把记账里记过的、以及各个 HoYoShade 目录里**有备份**的插件都还原成原版。
    /// 备份是第一次汉化之前拷的、之后不再覆盖，所以还原出来的永远是最初那份原版（不是「上一次汉化」）。
    /// </summary>
    public static async Task<string> RestoreAllAsync(CancellationToken cancellationToken = default)
    {
        List<string> targets = [];

        foreach (string path in Load())
        {
            if (File.Exists(path) && !targets.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                targets.Add(path);
            }
        }

        // 记账可能丢过（换过用户数据目录、手工删过记账）：目录里有备份的也算上
        foreach (string root in CandidateRoots())
        {
            string directory = Path.Combine(root, "reshade-shaders", "Addons");

            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (AddonFileInfo info in AddonFileInfo.ScanDirectory(directory))
            {
                string path = Path.Combine(directory, info.FileName);

                if (AddonLocalizer.BackupPathOf(path, GlobalPluginPage.I18nBackupDirectory) is not null
                    && !targets.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    targets.Add(path);
                }
            }
        }

        int restored = 0;
        List<string> done = [];

        foreach (string path in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                AddonLocalizeResult result = await Task.Run(
                    () => AddonLocalizer.Restore(path, GlobalPluginPage.I18nBackupDirectory),
                    cancellationToken);

                if (result.BackupPath is not null)
                {
                    restored++;
                    done.Add(Path.GetFileName(path));
                    Forget(path);
                }
            }
            catch (Exception ex)
            {
                // 文件被游戏占用之类：不炸，只记一笔
                Logger.LogWarning(ex, "还原插件汉化失败：{Path}", path);
            }
        }

        string message = targets.Count == 0
            ? "没找到汉化过的插件（没有备份）。"
            : $"已还原 {restored} 份插件：{string.Join("；", done)}（重启游戏生效）";

        Logger.LogInformation("Restore all addon i18n: {Message}", message);
        return message;
    }

    /// <summary>
    /// 启动游戏前重打一遍。返回一句给日志/提示用的话（没记过事就返回 null）。
    /// </summary>
    public static async Task<string?> ReapplyAsync(CancellationToken cancellationToken = default)
    {
        I18nState state = LoadState();
        List<string> paths = state.Files;

        if (paths.Count == 0)
        {
            return null;
        }

        // 记账里的版本比当前小 = 磁盘上那份是老算法打的（「缩ling」「结构强度 sity」就是这么来的）。
        // 老补丁把英文 needle 写掉了、没法原地重打，只能先从备份还原回原版再重打。
        bool upgrade = state.Algorithm < AddonLocalizer.AlgorithmVersion;
        List<AddonI18nTable> tables = AddonLocalizer.LoadTables(GlobalPluginPage.I18nTableDirectory);
        int files = 0;
        int entries = 0;
        int skipped = 0;

        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!File.Exists(path))
            {
                Forget(path);
                continue;
            }

            AddonI18nTable? table = AddonLocalizer.SelectTable(tables, Path.GetFileName(path));

            if (table is null)
            {
                // 翻译表没了（dlss5 系列自带多语言后删表）：从记账里清掉，别每次启动都白扫
                Forget(path);
                skipped++;
                continue;
            }

            try
            {
                if (upgrade && AddonLocalizer.BackupPathOf(path, GlobalPluginPage.I18nBackupDirectory) is not null)
                {
                    AddonLocalizer.Restore(path, GlobalPluginPage.I18nBackupDirectory);
                }

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
                skipped++;
                Logger.LogWarning(ex, "启动前重打汉化失败：{Path}", path);
            }
        }

        if (upgrade)
        {
            if (skipped == 0)
            {
                MarkCurrent();
            }

            Logger.LogInformation(
                "汉化补丁算法 v{Old} → v{New}：还原重打 {Files} 个插件（{Entries} 条），跳过 {Skipped}",
                state.Algorithm,
                AddonLocalizer.AlgorithmVersion,
                files,
                entries,
                skipped);

            return $"已按新算法重打汉化补丁（{files} 个插件 / {entries} 条）";
        }

        return files == 0 ? null : $"启动前重新汉化 {files} 个插件（{entries} 条）";
    }
}
