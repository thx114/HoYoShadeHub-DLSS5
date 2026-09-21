using HoYoShadeHub.Core.HoYoPlay;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HoYoShadeHub.Features.Xxmi;

/// <summary>
/// 找 XXMI（模型替换那套）。XXMI 的目录结构是：
/// <c>&lt;XXMI 根&gt;&lt;MI 实例&gt;{d3d11.dll, d3dx.ini, Mods}</c>，
/// 实例名按游戏叫 ZZMI（绝区零）/ GIMI（原神）/ SRMI（星铁）/ WWMI（鸣潮）/ HIMI（崩 3）。
///
/// 根目录的找法（按优先级）：手动指定 → %AppData%\XXMI Launcher → %LocalAppData%\XXMI Launcher
/// → **开始菜单快捷方式**（解析 .lnk 的目标）→ **注册表卸载项**（DisplayName 含 XXMI 的 InstallLocation）
/// → 各固定盘浅层目录里带 XXMI 特征文件的目录。
/// </summary>
internal sealed class XxmiLocator
{
    private static readonly ILogger Logger = AppConfig.GetLogger<XxmiLocator>();

    /// <summary>
    /// HoYoPlay 的 GameBiz → MI 实例名。注意 GameBiz 是**代号**不是游戏英文名：
    /// 原神 hk4e*、星铁 hkrpg*、绝区零 nap*（nap = 绝区零代号）、崩 3 bh3*。
    /// 之前注入那边写成 "ZZZ"/"Genshin" 去查，永远查不到。
    /// </summary>
    private static readonly (string Prefix, string Importer)[] ImporterByBiz =
    [
        ("hk4e", "GIMI"),
        ("hkrpg", "SRMI"),
        ("nap", "ZZMI"),
        ("bh3", "HIMI"),
    ];

    /// <summary>认识的 MI 实例名（顺序 = 界面显示顺序）</summary>
    public static readonly string[] ImporterNames = ["ZZMI", "GIMI", "SRMI", "WWMI", "HIMI", "EFMI"];

    /// <summary>MI 实例名 → 中文游戏名（界面上显示用）</summary>
    public static readonly Dictionary<string, string> ImporterGames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ZZMI"] = "绝区零",
        ["GIMI"] = "原神",
        ["SRMI"] = "崩坏：星穹铁道",
        ["WWMI"] = "鸣潮",
        ["HIMI"] = "崩坏 3",
        ["EFMI"] = "明日方舟：终末地",
    };

    public static string? ImporterFor(string? gameBiz)
    {
        if (string.IsNullOrWhiteSpace(gameBiz))
        {
            return null;
        }

        string biz = gameBiz.ToLowerInvariant();

        foreach ((string prefix, string importer) in ImporterByBiz)
        {
            if (biz.StartsWith(prefix, StringComparison.Ordinal))
            {
                return importer;
            }
        }

        return null;
    }

    /// <summary>XXMI 根目录（没有配置 / 启动器 / 任何 MI 实例就不算）</summary>
    public static string? FindRoot()
    {
        foreach (string candidate in RootCandidates())
        {
            if (IsXxmiRoot(candidate))
            {
                Logger.LogInformation("XXMI 根目录：{Root}", candidate);
                return candidate;
            }
        }

        return null;
    }

    public static bool IsXxmiRoot(string dir)
    {
        if (!Directory.Exists(dir))
        {
            return false;
        }

        if (File.Exists(Path.Combine(dir, "XXMI Launcher Config.json"))
            || File.Exists(Path.Combine(dir, "Resources", "Bin", "XXMI Launcher.exe")))
        {
            return true;
        }

        return ImporterNames.Any(name => IsInstance(Path.Combine(dir, name)));
    }

    /// <summary>这个目录是不是一个 MI 实例（有 3DMigoto 的 d3dx.ini 或 d3d11.dll）</summary>
    public static bool IsInstance(string dir)
    {
        return Directory.Exists(dir)
            && (File.Exists(Path.Combine(dir, "d3dx.ini")) || File.Exists(Path.Combine(dir, "d3d11.dll")));
    }

    public static string ModsDirectory(string instance) => Path.Combine(instance, "Mods");

    public static string LoaderPath(string instance) => Path.Combine(instance, "d3d11.dll");

    /// <summary>根目录下所有 MI 实例目录</summary>
    public static List<string> ListInstances(string root)
    {
        List<string> list = [];

        foreach (string name in ImporterNames)
        {
            string dir = Path.Combine(root, name);

            if (IsInstance(dir))
            {
                list.Add(dir);
            }
        }

        return list;
    }

    /// <summary>
    /// 这个实例在 XXMI 配置里的启动参数（例如绝区零是 <c>-use-d3d12</c>）。
    /// 取 <c>Importers.&lt;实例名&gt;.Importer.launch_options</c>，并且要 <c>use_launch_options</c> 为真。
    /// </summary>
    public static string LaunchOptions(string? gameBiz)
    {
        try
        {
            string? instance = FindInstance(gameBiz, out string? importer);

            if (instance is null || importer is null)
            {
                return string.Empty;
            }

            // 从实例目录往上找 XXMI 根（有配置文件那个）
            DirectoryInfo? dir = new(instance);

            while (dir is not null)
            {
                string config = Path.Combine(dir.FullName, "XXMI Launcher Config.json");

                if (File.Exists(config))
                {
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllText(config));

                    if (document.RootElement.TryGetProperty("Importers", out JsonElement importers)
                        && importers.TryGetProperty(importer, out JsonElement importerNode)
                        && importerNode.TryGetProperty("Importer", out JsonElement block))
                    {
                        bool use = !block.TryGetProperty("use_launch_options", out JsonElement useNode) || useNode.GetBoolean();

                        if (use && block.TryGetProperty("launch_options", out JsonElement options))
                        {
                            return options.GetString() ?? string.Empty;
                        }
                    }

                    return string.Empty;
                }

                dir = dir.Parent;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "读 XXMI 启动参数失败");
        }

        return string.Empty;
    }

    /// <summary>
    /// 这个实例在 XXMI 配置里配的「Extra Libraries」（额外注入的 dll，例如
    /// <c>D:\APPS\HoYoShadeHub\HoYoShade\ReShade64.dll</c>）。XXMI 启动时会把这些也注入游戏进程，
    /// 所以我们按 XXMI 方式启动时也要带上，不然 ReShade 这类就没了。
    /// </summary>
    public static List<string> ExtraLibraries(string? gameBiz)
    {
        List<string> list = [];

        try
        {
            string? instance = FindInstance(gameBiz, out string? importer);

            if (instance is null || importer is null)
            {
                return list;
            }

            DirectoryInfo? dir = new(instance);

            while (dir is not null)
            {
                string config = Path.Combine(dir.FullName, "XXMI Launcher Config.json");

                if (!File.Exists(config))
                {
                    dir = dir.Parent;
                    continue;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(config));

                if (document.RootElement.TryGetProperty("Importers", out JsonElement importers)
                    && importers.TryGetProperty(importer, out JsonElement importerNode)
                    && importerNode.TryGetProperty("Importer", out JsonElement block))
                {
                    bool enabled = !block.TryGetProperty("extra_libraries_enabled", out JsonElement enabledNode) || enabledNode.GetBoolean();

                    if (enabled && block.TryGetProperty("extra_libraries", out JsonElement libs))
                    {
                        string raw = libs.GetString() ?? string.Empty;

                        foreach (string piece in raw.Split([';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
                        {
                            string path = piece.Trim().Trim('"');

                            if (path.Length > 0 && File.Exists(path) && !list.Contains(path, StringComparer.OrdinalIgnoreCase))
                            {
                                list.Add(path);
                            }
                        }
                    }
                }

                break;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "读 XXMI Extra Libraries 失败");
        }

        return list;
    }

    /// <summary>XXMI 配置里 enabled_importers（读不出来就返回空表）</summary>
    public static List<string> EnabledImporters(string root)
    {
        try
        {
            string config = Path.Combine(root, "XXMI Launcher Config.json");

            if (!File.Exists(config))
            {
                return [];
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(config));

            if (document.RootElement.TryGetProperty("Launcher", out JsonElement launcher)
                && launcher.TryGetProperty("enabled_importers", out JsonElement importers)
                && importers.ValueKind == JsonValueKind.Array)
            {
                return [.. importers.EnumerateArray().Select(x => x.GetString() ?? string.Empty).Where(x => x.Length > 0)];
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "读 XXMI 配置失败：{Root}", root);
        }

        return [];
    }

    /// <summary>找这个游戏的 MI 实例；<paramref name="importerName"/> 返回实际用到的实例名</summary>
    public static string? FindInstance(string? gameBiz, out string? importerName)
    {
        importerName = ImporterFor(gameBiz);

        if (gameBiz is not null)
        {
            string? manual = AppConfig.GetXxmiInstance(gameBiz);

            if (!string.IsNullOrWhiteSpace(manual) && IsInstance(manual))
            {
                importerName = ImporterFor(gameBiz) ?? Path.GetFileName(manual.TrimEnd('\\'));
                return manual;
            }
        }

        string? root = FindRoot();

        if (root is null)
        {
            return null;
        }

        List<string> order = importerName is null ? [.. ImporterNames] : [importerName, .. ImporterNames];

        foreach (string name in order)
        {
            string dir = Path.Combine(root, name);

            if (IsInstance(dir))
            {
                importerName = name;
                return dir;
            }
        }

        return null;
    }

    private static IEnumerable<string> RootCandidates()
    {
        if (!string.IsNullOrWhiteSpace(AppConfig.XxmiRoot))
        {
            yield return AppConfig.XxmiRoot!;
        }

        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XXMI Launcher");
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "XXMI Launcher");

        foreach (string fromMenu in FromStartMenu())
        {
            yield return fromMenu;
        }

        foreach (string fromRegistry in FromRegistry())
        {
            yield return fromRegistry;
        }

        foreach (DriveInfo drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            foreach (string dir in ShallowDirectories(drive.RootDirectory.FullName, 3))
            {
                if (IsXxmiRoot(dir))
                {
                    yield return dir;
                }
            }
        }
    }

    /// <summary>从开始菜单的 XXMI 快捷方式反推根目录</summary>
    private static List<string> FromStartMenu()
    {
        List<string> roots = [];
        string[] menus =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        ];

        foreach (string menu in menus)
        {
            if (!Directory.Exists(menu))
            {
                continue;
            }

            IEnumerable<string> links;

            try
            {
                links = Directory.EnumerateFiles(menu, "*.lnk", SearchOption.AllDirectories);
            }
            catch
            {
                continue;
            }

            foreach (string link in links)
            {
                if (!Path.GetFileName(link).Contains("XXMI", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? target = ResolveShortcut(link);

                if (string.IsNullOrWhiteSpace(target))
                {
                    continue;
                }

                // 目标通常是 <根>\Resources\Bin\XXMI Launcher.exe → 往上两级就是根
                string? dir = Path.GetDirectoryName(target);

                if (dir is not null && Path.GetFileName(dir).Equals("Bin", StringComparison.OrdinalIgnoreCase))
                {
                    roots.Add(Path.GetFullPath(Path.Combine(dir, "..", "..")));
                }

                if (dir is not null)
                {
                    roots.Add(dir);
                }
            }
        }

        return roots;
    }

    private static string? ResolveShortcut(string linkPath)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");

            if (shellType is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(linkPath);
            string target = (string)link.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "解析快捷方式失败：{Link}", linkPath);
            return null;
        }
    }

    /// <summary>从注册表卸载项里找 XXMI 的安装目录</summary>
    private static List<string> FromRegistry()
    {
        List<string> roots = [];
        string[] keys =
        [
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
            @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        ];

        foreach (Microsoft.Win32.RegistryKey hive in new[] { Microsoft.Win32.Registry.CurrentUser, Microsoft.Win32.Registry.LocalMachine })
        {
            foreach (string sub in keys)
            {
                try
                {
                    using Microsoft.Win32.RegistryKey? key = hive.OpenSubKey(sub);

                    if (key is null)
                    {
                        continue;
                    }

                    foreach (string name in key.GetSubKeyNames())
                    {
                        using Microsoft.Win32.RegistryKey? item = key.OpenSubKey(name);

                        string display = item?.GetValue("DisplayName") as string ?? string.Empty;

                        if (!display.Contains("XXMI", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string? location = item?.GetValue("InstallLocation") as string;

                        if (!string.IsNullOrWhiteSpace(location) && Directory.Exists(location))
                        {
                            roots.Add(location);
                        }

                        string? uninstall = item?.GetValue("UninstallString") as string;

                        if (!string.IsNullOrWhiteSpace(uninstall))
                        {
                            string trimmed = uninstall.Trim('"');
                            string? dir = Path.GetDirectoryName(trimmed);

                            if (dir is not null)
                            {
                                roots.Add(dir.TrimEnd('\\'));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "读注册表失败：{Sub}", sub);
                }
            }
        }

        return roots;
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
}
