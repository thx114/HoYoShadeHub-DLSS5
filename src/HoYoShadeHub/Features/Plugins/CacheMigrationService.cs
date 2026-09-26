using HoYoShadeHub.Extensions;
using HoYoShadeHub.Extensions.Models;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>「版本更新引导」一次迁移的结果（给对话框显示人话用）</summary>
internal sealed class CacheMigrationReport
{
    public List<string> Steps { get; } = [];

    /// <summary>目标已存在、只好两边都留的项</summary>
    public List<string> Conflicts { get; } = [];

    public int ExtensionsArchived { get; set; }

    public int GamesPacked { get; set; }

    public bool Ok { get; set; }

    public string BackupDirectory { get; set; } = string.Empty;

    public string Summary
    {
        get
        {
            if (!Ok)
            {
                return "迁移没有完成。老存储与老配置都还在，App 照常能用；可以稍后再试一次。";
            }

            string text = $"已完成：归档 {ExtensionsArchived} 个插件、为 {GamesPacked} 个游戏拼了插件包。"
                          + (Conflicts.Count > 0 ? $"　目标冲突 {Conflicts.Count} 处（两边都保留）。" : string.Empty);

            return text + (string.IsNullOrWhiteSpace(BackupDirectory)
                ? string.Empty
                : $"　备份在 {BackupDirectory}");
        }
    }
}

/// <summary>
/// 「版本更新引导」：把老存储搬进启动器的 <c>cache</c>，给现有插件归档当前版本，
/// 再给选过版本的游戏拼插件包。
///
/// <para>
/// 失败必须让 App 还能用：每一步单独 try/catch，搬迁失败就不置迁移标记 ——
/// <see cref="AppConfig.OptiScalerRootPath"/> / <see cref="AppConfig.ModulesRootPath"/>
/// 于是继续用老位置。
/// </para>
/// </summary>
internal static class CacheMigrationService
{
    private const string BackupFolderName = "migrate-backup";

    /// <summary>现在该不该弹一次性引导</summary>
    public static bool NeedsMigration()
    {
        try
        {
            if (AppConfig.CacheMigrated || string.IsNullOrWhiteSpace(AppConfig.UserDataFolder))
            {
                return false;
            }

            // 用户点过「不再提示」就别再弹；手动入口（force）不受这个限制。
            if (AppConfig.GetValue(false, "cache_migration_dismissed"))
            {
                return false;
            }

            string cacheRoot = AppConfig.CacheRoot;
            if (string.IsNullOrWhiteSpace(cacheRoot))
            {
                return false;
            }

            var store = new AddonVersionStore(cacheRoot);
            bool pluginStoreHasVersions = false;
            try
            {
                pluginStoreHasVersions = store.Exists && Directory.EnumerateDirectories(store.RootPath).Any();
            }
            catch
            {
                pluginStoreHasVersions = false;
            }

            bool hasInstalledExtensions = false;
            ShadeHost? host = PluginHostLocator.Resolve(out _);
            if (host is not null && File.Exists(host.LedgerPath))
            {
                try
                {
                    hasInstalledExtensions = new InstalledExtensionStore(host).LoadAsync()
                        .GetAwaiter().GetResult().Extensions.Count > 0;
                }
                catch
                {
                    hasInstalledExtensions = false;
                }
            }

            string userData = AppConfig.UserDataFolder!;
            return CacheMigrationPlanner.NeedsMigration(
                cacheMigrated: false,
                hasInstalledExtensions: hasInstalledExtensions,
                pluginStoreHasVersions: pluginStoreHasVersions,
                legacyOptiScalerExists: Directory.Exists(Path.Combine(userData, "OptiScaler")),
                cacheOptiScalerExists: Directory.Exists(AppConfig.OptiScalerCachePath),
                legacyModulesExists: Directory.Exists(Path.Combine(userData, "Modules")),
                cacheModulesExists: Directory.Exists(AppConfig.ModulesCachePath));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>用户在引导里点「不再提示」时记一笔</summary>
    public static void DismissPrompt() => AppConfig.SetValue(true, "cache_migration_dismissed");

    private static bool _promptShown;

    /// <summary>
    /// 启动时弹一次性「版本更新引导」，点「一键升级」就地跑迁移并弹结果。
    ///
    /// <para>
    /// **必须在窗口已经显示之后调用**（<c>MainView.Loaded</c>）——
    /// 以前是在全局插件页的刷新流程里 <c>await ShowAsync()</c>，页面导航还没走完就弹模态框，
    /// await 不回来，整个页面就卡死了（用户反馈「全局插件页面卡死」）。
    /// </para>
    /// </summary>
    public static async Task PromptIfNeededAsync(XamlRoot xamlRoot, ILogger? logger = null)
    {
        if (_promptShown || xamlRoot is null)
        {
            return;
        }

        _promptShown = true;

        try
        {
            // NeedsMigration() 里同步读账本（sync-over-async），丢后台线程，别卡启动
            if (!await Task.Run(NeedsMigration))
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = xamlRoot,
                Title = "版本更新引导",
                Content = "检测到老版本的存储布局：插件还没有版本归档，OptiScaler / 模块还在老位置。\n\n"
                          + "一键升级会：\n"
                          + "· 在启动器目录下建一个 cache 缓存目录；\n"
                          + "· 把 OptiScaler / 模块 搬进 cache；\n"
                          + "· 给已装插件各归档一份当前版本；\n"
                          + "· 给现在所有游戏各拼一份插件目录并指好 AddonPath。\n\n"
                          + "升级前会先备份。选「稍后」不做任何改动，App 照常能用。",
                PrimaryButtonText = "一键升级",
                SecondaryButtonText = "稍后",
                CloseButtonText = "不再提示",
                DefaultButton = ContentDialogButton.Primary,
            };

            ContentDialogResult result = await dialog.ShowAsync();

            if (result == ContentDialogResult.None)
            {
                DismissPrompt();
                return;
            }

            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            CacheMigrationReport report = await RunAsync(new Progress<string>(_ => { }));

            logger?.LogInformation("Cache migration: ok={Ok} archived={Archived} games={Games} conflicts={Conflicts}",
                report.Ok, report.ExtensionsArchived, report.GamesPacked, report.Conflicts.Count);

            await ShowResultAsync(xamlRoot, report);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Version migration prompt");
        }
    }

    private static async Task ShowResultAsync(XamlRoot xamlRoot, CacheMigrationReport report)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = report.Summary, TextWrapping = TextWrapping.Wrap });

        foreach (string step in report.Steps)
        {
            panel.Children.Add(new TextBlock { Text = "· " + step, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 });
        }

        foreach (string conflict in report.Conflicts)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "⚠ " + conflict,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCautionBrush"],
            });
        }

        await new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = report.Ok ? "升级完成" : "升级未完成",
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            CloseButtonText = "知道了",
        }.ShowAsync();
    }

    /// <summary>
    /// 跑一次迁移。永远不抛（失败也只是 <see cref="CacheMigrationReport.Ok"/> 为 false）。
    /// </summary>
    public static async Task<CacheMigrationReport> RunAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var report = new CacheMigrationReport();

        try
        {
            string? userData = AppConfig.UserDataFolder;
            string cacheRoot = AppConfig.CacheRoot;

            if (string.IsNullOrWhiteSpace(userData) || string.IsNullOrWhiteSpace(cacheRoot))
            {
                report.Steps.Add("还没有用户数据目录，先完成首次引导。");
                return report;
            }

            string backupDirectory = Path.Combine(userData, ".hysx", BackupFolderName, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            report.BackupDirectory = backupDirectory;
            try
            {
                Directory.CreateDirectory(backupDirectory);
            }
            catch
            {
                // 备份目录建不出来不阻断迁移
            }

            ShadeHost? host = PluginHostLocator.Resolve(out _);

            // 账本先备一份 —— 迁移出问题时至少记账还在
            if (host is not null)
            {
                BackupFile(host.LedgerPath, Path.Combine(backupDirectory, "installed.json"), report);
            }

            progress?.Report("正在创建缓存目录…");
            Directory.CreateDirectory(cacheRoot);
            Directory.CreateDirectory(new AddonVersionStore(cacheRoot).RootPath);
            Directory.CreateDirectory(GameAddonPack.GamesRoot(cacheRoot));
            report.Steps.Add("已创建缓存目录骨架：" + cacheRoot);

            // 1) 老存储搬进 cache
            int failures = 0;
            foreach (LegacyStoreMove move in CacheMigrationPlanner.PlanMoves(
                         Path.Combine(userData, "OptiScaler"),
                         Path.Combine(userData, "Modules"),
                         AppConfig.OptiScalerCachePath,
                         AppConfig.ModulesCachePath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"正在搬迁 {move.Kind}…");

                if (!MoveStore(move, report))
                {
                    failures++;
                }
            }

            if (failures > 0)
            {
                report.Steps.Add($"有 {failures} 个老存储没搬成功 —— 原目录已保留，这次不置迁移标记（下次再试）。");
                return report;
            }

            // 2) 已装插件：把当前版本归档一份
            if (host is not null)
            {
                progress?.Report("正在归档已装插件…");
                var store = new AddonVersionStore(cacheRoot);

                try
                {
                    InstalledExtensionLedger ledger = await new InstalledExtensionStore(host).LoadAsync(cancellationToken);

                    foreach (InstalledExtension record in ledger.Extensions)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        AddonArchiveResult archive = store.Archive(host, record);

                        if (archive.Archived > 0 || archive.Skipped > 0)
                        {
                            report.ExtensionsArchived++;
                        }
                    }

                    report.Steps.Add($"已归档 {report.ExtensionsArchived} 个插件的当前版本。");
                }
                catch (Exception ex)
                {
                    report.Steps.Add("归档已装插件时出错（不影响其它步骤）：" + ex.Message);
                }
            }

            // 3) 选过版本的游戏：拼插件包并指 AddonPath
            progress?.Report("正在拼每游戏插件包…");
            try
            {
                report.GamesPacked = GameAddonPackService.SyncAll();
                report.Steps.Add($"已为 {report.GamesPacked} 个游戏拼好专属插件包。");
            }
            catch (Exception ex)
            {
                report.Steps.Add("拼插件包时出错：" + ex.Message);
            }

            AppConfig.CacheMigrated = true;
            report.Ok = true;
            report.Steps.Add("迁移完成。");
        }
        catch (Exception ex)
        {
            report.Steps.Add("迁移失败：" + ex.Message);
        }

        return report;
    }

    /// <summary>搬迁一个老存储；返回 false = 失败（原目录保留）</summary>
    private static bool MoveStore(LegacyStoreMove move, CacheMigrationReport report)
    {
        try
        {
            if (!Directory.Exists(move.Source))
            {
                return true;
            }

            if (Directory.Exists(move.Target))
            {
                // 目标已经有一份：不覆盖，把源里多出来的文件补进去，源目录原地留着
                CopyTree(move.Source, move.Target, overwrite: false);
                report.Conflicts.Add($"{move.Kind}：{move.Target} 已经存在 —— 两边文件都保留（没有覆盖），老目录 {move.Source} 也不删。");
                return true;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(move.Target))!);

            bool moved = false;

            if (HysxFileLink.SameVolume(move.Source, move.Target))
            {
                try
                {
                    Directory.Move(move.Source, move.Target);
                    moved = true;
                    report.Steps.Add($"{move.Kind} 已搬迁：{move.Source} → {move.Target}");
                }
                catch (Exception ex)
                {
                    // 有进程打开着里面的文件（注入型 dll / 日志）时，整目录改名会被 Windows 拒绝
                    // （Access denied）—— 退回复制，别让整个迁移失败。
                    report.Steps.Add($"{move.Kind} 直接改名被拒（{ex.GetType().Name}），改成复制：{move.Source} → {move.Target}");
                }
            }

            if (!moved)
            {
                CopyTree(move.Source, move.Target, overwrite: true);
                HysxUtil.TryDeleteDirectory(move.Source);

                if (!Directory.Exists(move.Source))
                {
                    report.Steps.Add($"{move.Kind} 已搬迁（复制后删除）：{move.Source} → {move.Target}");
                }
                else
                {
                    // 老目录删不掉（文件被占用）**不算失败**：cache 里已经有一份，App 用 cache 那份；
                    // 老目录留着，用户方便时自己删。否则每次启动都会再失败一次、永远置不上迁移标记。
                    report.Conflicts.Add(
                        $"{move.Kind}：{move.Target} 已就绪（App 用这份），但老目录 {move.Source} 删不掉（有文件被占用）—— 两边都保留。");
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            // 走到 catch 的只有「全新搬迁」失败（目标已存在的合并路径直接返回 true）——
            // 半路抛掉的 CopyTree 会留下一个**残缺**的 cache 目标，而读侧（ModulesRootPath）
            // 只要目录存在就用 cache 那份：不清掉的话，下次成功迁移前模块列表是缺的。
            HysxUtil.TryDeleteDirectory(move.Target);
            report.Steps.Add($"{move.Kind} 搬迁失败（原目录保留，残缺的 cache 目标已清理）：{ex.Message}");
            return false;
        }
    }

    private static void CopyTree(string source, string target, bool overwrite)
    {
        Directory.CreateDirectory(target);

        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, directory);
            Directory.CreateDirectory(Path.Combine(target, relative));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);
            string destination = Path.Combine(target, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (overwrite || !File.Exists(destination))
            {
                File.Copy(file, destination, overwrite);
            }
        }
    }

    private static void BackupFile(string source, string target, CacheMigrationReport report)
    {
        try
        {
            if (!File.Exists(source))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
        }
        catch (Exception ex)
        {
            report.Steps.Add("备份 " + Path.GetFileName(source) + " 失败：" + ex.Message);
        }
    }
}
