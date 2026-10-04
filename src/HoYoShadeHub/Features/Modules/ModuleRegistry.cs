using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Networking;
using HoYoShadeHub.Extensions.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Modules;

/// <summary>
/// 一个「模块」：启动游戏时要注入进程的东西。
///
/// <para>
/// 跟 ReShade 插件（<c>.addon64</c>）和 OptiScaler 都不是一回事 —— 它是独立的注入类工具，
/// 比如 <c>danielblnc/DLSS-NR-on-AMD</c> 装出来的 <c>version.dll</c>。
/// 模块可以同时用多个；「哪个游戏用哪些模块」在左侧「模块」页里勾，
/// 「下载 / 部署 / 全局开关」在「全局插件 → 模块」里（跟插件、OptiScaler 一套风格）。
/// </para>
/// </summary>
public sealed record ModuleDefinition(
    string Id,
    string Name,
    string Description,
    string Repository,
    string TagPattern,
    string? Homepage,
    string DllHint,
    string[]? Tags = null,
    string[]? DirectFiles = null,
    string Branch = "main",
    bool Bundled = false)
{
    /// <summary>
    /// 有些模块压根不发 Release 资产，文件直接躺在仓库树里（比如 dlssg_for_sm86 的
    /// <c>version.dll</c> + <c>dlssg_sm86.ini</c>）。给了 <see cref="DirectFiles"/> 就走这条路：
    /// 从 <c>raw.githubusercontent.com/&lt;repository&gt;/&lt;branch&gt;/&lt;file&gt;</c> 下到模块目录。
    /// </summary>
    public bool IsDirect => DirectFiles is { Length: > 0 };

    public bool IsBundled => Bundled;

    /// <summary>模块目录：&lt;用户数据目录&gt;\Modules\&lt;id&gt;\</summary>
    public string Directory => AppConfig.ModuleDirectory(Id);

    /// <summary>全局开关（在「全局插件 → 模块」里开/关；关掉的话哪个游戏都用不了）</summary>
    public bool Enabled
    {
        get => AppConfig.GetModuleEnabled(Id);
        set => AppConfig.SetModuleEnabled(Id, value);
    }
}

/// <summary>模块列表里的一行（内置模块 + 手动加的 DLL 统一成这个形状）</summary>
public sealed class ModuleEntry
{
    /// <summary>内置模块 = 模块 id；手动加的 = DLL 全路径</summary>
    public required string Key { get; init; }

    public required string Name { get; init; }

    public string Description { get; init; } = string.Empty;

    public string[]? Tags { get; init; }

    public string? Homepage { get; init; }

    public bool IsBuiltin { get; init; }

    public ModuleDefinition? Definition { get; init; }

    /// <summary>要注入的 DLL（内置模块解析出来的，或手动加的那个路径）</summary>
    public string? DllPath { get; init; }

    /// <summary>全局开关（「全局插件 → 模块」里那个）</summary>
    public bool GloballyEnabled { get; init; }
}

/// <summary>远端目录驱动的模块表 + 每个游戏用哪些模块 + 下载部署</summary>
public static class ModuleRegistry
{
    private const string GenshinFsrBridgeId = "genshin-fsr-bridge";

    // Metadata fallback only: the DLL is never shipped inside HoYoShadeHub.
    // The actual module is downloaded from our GitHub release catalog.
    private static readonly ModuleDefinition GenshinFsrBridge = new(
        GenshinFsrBridgeId,
        "Genshin FSR Bridge",
        "原神 DX11 FSR2 → OptiScaler Bridge。模块从我们的 GitHub release 下载；单独启用不会加载 OptiScaler，启用 OptiScaler 时由 Bridge 进程内加载当前选择的构建。",
        "thx114/genshin_fsr_brigde",
        "^v2\\.3\\.\\d+-fg-(?:delay-)?\\d+$",
        "https://github.com/thx114/genshin_fsr_brigde/releases",
        "Dx11FsrBridge.dll",
        ["genshin", "fsr", "bridge", "frame-generation"],
        null,
        "main",
        false);

    // The remote catalog may replace this metadata. It must not replace the
    // downloaded module with an app-bundled DLL.

    /// <summary>远端目录（catalog/modules.json）里读到的模块；同 id 覆盖</summary>
    public static IReadOnlyList<ModuleDefinition> RemoteOverlay { get; private set; } = [];

    /// <summary>远端目录里用 <c>removed: true</c> 删掉的模块 id</summary>
    public static IReadOnlyCollection<string> RemovedIds { get; private set; } = [];

    public static void SetRemoteOverlay(IEnumerable<ModuleDefinition>? modules, IEnumerable<string>? removed = null)
    {
        RemoteOverlay = modules is null ? [] : [.. modules];
        RemovedIds = removed is null ? [] : [.. removed];
    }

    /// <summary>远端目录（去墓碑、按 id 去重）</summary>
    public static List<ModuleDefinition> All()
    {
        var result = new List<ModuleDefinition> { GenshinFsrBridge };

        foreach (ModuleDefinition module in RemoteOverlay)
        {
            if (string.IsNullOrWhiteSpace(module.Id)
                || RemovedIds.Contains(module.Id, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            int index = result.FindIndex(m => string.Equals(m.Id, module.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                result[index] = module;
            }
            else
            {
                result.Add(module);
            }
        }

        return result;
    }

    public static ModuleDefinition? Find(string id)
        => All().FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsGenshin(GameId gameId)
        => gameId.GameBiz.ToString().StartsWith("hk4e_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ensures the bundled Genshin FSR Bridge is ready and selected for this game.
    /// The Bridge is injected as a module; OptiScaler is loaded by the Bridge from
    /// the launch-time autoload file, so this method never enables OptiScaler itself.
    /// </summary>
    public static bool EnsureGenshinFsrBridgeForLaunch(GameId gameId)
    {
        if (!IsGenshin(gameId))
        {
            return false;
        }

        ModuleDefinition? bridge = Find(GenshinFsrBridgeId);
        if (bridge is null || EnsureBundledGenshinFsrBridge() is null)
        {
            return false;
        }

        AppConfig.SetBundledModuleRemoved(bridge.Id, false);
        bridge.Enabled = true;
        RemoveManualGenshinFsrBridgeSelections(gameId);
        SetUsed(gameId, bridge.Id, true);
        // OptiScaler depends on the Bridge, so make the module launch option explicit.
        AppConfig.SetUseModulesLaunchOption(gameId, true);
        return true;
    }

    private static void RemoveManualGenshinFsrBridgeSelections(GameId gameId)
    {
        IReadOnlyList<string>? raw = AppConfig.GetUsedModuleKeysOrNull(gameId);
        if (raw is null)
        {
            return;
        }

        List<string> keys = [.. raw];
        int removed = keys.RemoveAll(key =>
            !string.Equals(key, GenshinFsrBridgeId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Path.GetFileName(key), OptiScalerRuntime.FsrBridgeDllName, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            AppConfig.SetUsedModuleKeys(gameId, keys);
        }
    }

    /// <summary>模块 + 手动加的 DLL，统一成列表（「全局插件 → 模块」的上下两截都用它）</summary>
    public static List<ModuleEntry> List()
    {
        var entries = new List<ModuleEntry>();

        foreach (ModuleDefinition module in All())
        {
            entries.Add(new ModuleEntry
            {
                Key = module.Id,
                Name = module.Name,
                Description = module.Description,
                Tags = module.Tags,
                Homepage = module.Homepage,
                IsBuiltin = true,
                Definition = module,
                DllPath = ResolveDllPath(module),
                GloballyEnabled = module.Enabled,
            });
        }

        foreach (string path in AppConfig.GetManualModuleDlls())
        {
            bool exists = File.Exists(path);
            string fileName = Path.GetFileName(path);

            // 需求3：手动加的 DLL 如果文件名能对上目录里某个模块要装的 DLL，
            // 就把它当成那个模块 —— 复用可下载卡片的介绍 / 标签 / 主页，
            // 界面上显示的也是模块本名，而不是一串裸文件名。
            // 版本号未知没关系，先留空，PE 里读到就显示。
            ModuleDefinition? matched = All().FirstOrDefault(
                m => !string.IsNullOrWhiteSpace(m.DllHint)
                     && string.Equals(Path.GetFileName(m.DllHint), fileName, StringComparison.OrdinalIgnoreCase));

            entries.Add(new ModuleEntry
            {
                Key = path,
                Name = matched?.Name ?? fileName,
                Description = exists
                    ? matched?.Description ?? path
                    : path + "（文件已经不在了）",
                Tags = matched?.Tags,
                Homepage = matched?.Homepage,
                IsBuiltin = false,
                Definition = null,
                DllPath = exists ? path : null,
                GloballyEnabled = AppConfig.IsManualModuleDllEnabled(path),
            });
        }

        return entries;
    }

    // ===================== 每个游戏用哪些模块 =====================

    /// <summary>默认勾选 = 全局开着的那些（老配置迁移：以前是全局启用，就等于每个游戏都启用）</summary>
    public static IReadOnlyList<string> DefaultUsedKeys()
        => [.. List().Where(e => e.GloballyEnabled && !string.IsNullOrWhiteSpace(e.DllPath)).Select(e => e.Key)];

    public static IReadOnlyList<string> GetUsedKeys(GameId gameId)
        => AppConfig.GetUsedModuleKeysOrNull(gameId) ?? DefaultUsedKeys();

    public static bool IsUsed(GameId gameId, string key)
        => GetUsedKeys(gameId).Contains(key, StringComparer.OrdinalIgnoreCase);

    public static void SetUsed(GameId gameId, string key, bool used)
    {
        List<string> keys = [.. GetUsedKeys(gameId)];
        keys.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));

        if (used)
        {
            keys.Add(key);
        }

        AppConfig.SetUsedModuleKeys(gameId, keys);
    }

    /// <summary>
    /// 这个游戏启动时真正要注入的模块：勾了 ∩ 全局开着 ∩ 文件在。
    /// 每项带 <c>Key</c>（模块 id / 手动 DLL 的全路径），「注入时机」按它存。
    /// <para>
    /// 顺序按用户在「模块」页排的来（<see cref="AppConfig.GetModuleOrder"/>）；
    /// 没排过的排在后面，保持默认顺序 —— 这样新装的模块不会插队到用户排好的前面。
    /// </para>
    /// </summary>
    public static List<(string Key, string Name, string DllPath)> ResolveInjectionDlls(GameId gameId)
    {
        var result = new List<(string Key, string Name, string DllPath)>();

        foreach (ModuleEntry entry in List())
        {
            if (!entry.GloballyEnabled || string.IsNullOrWhiteSpace(entry.DllPath))
            {
                continue;
            }

            if (!IsUsed(gameId, entry.Key))
            {
                continue;
            }

            // 原神的 Bridge 是启动器随包的唯一实例。旧配置里可能还留着手动
            // 添加的 Dx11FsrBridge.dll；不能把两个同名 Bridge 同时注入目标进程。
            if (IsGenshin(gameId)
                && !entry.IsBuiltin
                && string.Equals(Path.GetFileName(entry.DllPath), OptiScalerRuntime.FsrBridgeDllName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // 这个游戏给这个模块选了版本 → 注入那一份（没选就注入默认/最新装的那份）
            string dllPath = entry.DllPath;
            if (entry.Definition is { } definition
                && AppConfig.GetModuleVersion(gameId, definition.Id) is { Length: > 0 } tag
                && ResolveDllPath(definition, tag) is { } versionDll)
            {
                dllPath = versionDll;
            }

            result.Add((entry.Key, entry.Name, dllPath));
        }

        IReadOnlyList<string> order = AppConfig.GetModuleOrder();

        return [.. result
            .OrderBy(r =>
            {
                int index = -1;
                for (int i = 0; i < order.Count; i++)
                {
                    if (string.Equals(order[i], r.Key, StringComparison.OrdinalIgnoreCase))
                    {
                        index = i;
                        break;
                    }
                }

                // 没排过的统一给一个大值，稳定排序保证它们保持 List() 的默认次序
                return index < 0 ? int.MaxValue : index;
            })
            .Select(r => (r.Key, r.Name, r.DllPath))];
    }

    /// <summary>当前注入顺序下的全部模块 key（含没装的）——「模块」页排序用</summary>
    public static List<string> OrderedKeys()
    {
        List<string> all = [.. List().Select(e => e.Key)];
        IReadOnlyList<string> order = AppConfig.GetModuleOrder();

        return [.. all.OrderBy(k =>
        {
            int index = -1;
            for (int i = 0; i < order.Count; i++)
            {
                if (string.Equals(order[i], k, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }

            return index < 0 ? int.MaxValue : index;
        })];
    }

    /// <summary>把 key 上移 / 下移一位，落盘。返回 false = 已经在头 / 尾</summary>
    public static bool MoveModule(string key, int delta)
    {
        List<string> ordered = OrderedKeys();
        int index = ordered.FindIndex(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            return false;
        }

        int target = index + delta;
        if (target < 0 || target >= ordered.Count)
        {
            return false;
        }

        (ordered[index], ordered[target]) = (ordered[target], ordered[index]);
        AppConfig.SetModuleOrder(ordered);
        return true;
    }

    // ===================== DLL 解析 / 迁移 / 部署 =====================

    /// <summary>
    /// 删除一个已安装模块：删掉模块目录、每游戏使用记录、全局开关与 DLL 路径记账。
    /// 内置 / 远端目录里的定义本身保留（删的是装出来的文件，用户随时能再下）。
    /// </summary>
    public static bool DeleteInstalled(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return false;
        }

        string directory = AppConfig.ModuleDirectory(id);
        bool isBundled = Find(id)?.IsBundled == true;
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            return false;
        }

        AppConfig.SetModuleEnabled(id, false);
        AppConfig.SetModuleDllPath(id, null);
        AppConfig.SetBundledModuleRemoved(id, isBundled);

        foreach (GameBiz biz in GameBiz.AllGameBizs)
        {
            if (GameId.FromGameBiz(biz) is { } gameId)
            {
                SetUsed(gameId, id, false);
            }
        }

        return true;
    }

    /// <summary>
    private static string? EnsureBundledModuleInstalled(ModuleDefinition module)
    {
        if (!module.IsBundled || AppConfig.GetBundledModuleRemoved(module.Id))
            return null;

        string sourceDir = Path.Combine(AppContext.BaseDirectory, "Assets", "Modules", module.Id);
        string sourceDll = Path.Combine(sourceDir, module.DllHint);
        if (!File.Exists(sourceDll))
            return null;

        string targetDir = AppConfig.ModuleDirectory(module.Id);
        Directory.CreateDirectory(targetDir);
        foreach (string source in Directory.EnumerateFiles(sourceDir, "*", SearchOption.TopDirectoryOnly))
        {
            string target = Path.Combine(targetDir, Path.GetFileName(source));
            try
            {
                bool needsCopy = !File.Exists(target) || !BundledFilesEqual(source, target);
                if (needsCopy)
                    File.Copy(source, target, overwrite: true);
            }
            catch (IOException)
            {
                // Running game may lock the DLL; replace it on next launcher start.
            }
        }

        string targetDll = Path.Combine(targetDir, module.DllHint);
        return File.Exists(targetDll) ? targetDll : null;
    }

    private static bool BundledFilesEqual(string source, string target)
    {
        try
        {
            return File.ReadAllBytes(source).AsSpan().SequenceEqual(File.ReadAllBytes(target));
        }
        catch
        {
            return false;
        }
    }

    public static string? EnsureBundledGenshinFsrBridge()
        => ResolveDllPath(Find(GenshinFsrBridgeId) ?? GenshinFsrBridge);

    /// 模块要注入的 DLL：用户手动指定的 &gt; 模块目录里找（先按提示名，再按代理 dll 名）
    /// &gt; 以前从「OptiScaler 可下载」装的那份（更新后自动接上，不用重装）。
    /// </summary>
    public static string? ResolveDllPath(ModuleDefinition module) => ResolveDllPath(module, null);

    /// <summary>
    /// 解析要注入的 DLL。<paramref name="versionTag"/> 不为空时（= 每个游戏选的那个版本）
    /// **只**在那个版本目录里找；多个版本共存时默认也是「最新装的那份」优先 ——
    /// 直接递归扫整个模块目录会按文件系统顺序随便挑，可能挑到旧版本。
    /// </summary>
    public static string? ResolveDllPath(ModuleDefinition module, string? versionTag)
    {
        if (module.IsBundled)
            return EnsureBundledModuleInstalled(module);
        // 用户手动指定的 dll 永远优先
        string? chosen = AppConfig.GetModuleDllPath(module.Id);
        if (!string.IsNullOrWhiteSpace(chosen) && File.Exists(chosen)
            && IsAcceptedModuleDll(module, chosen))
        {
            return chosen;
        }

        // 1) 指定版本：只认那个版本目录
        if (!string.IsNullOrWhiteSpace(versionTag)
            && VersionDirectory(module, versionTag!) is { } picked
            && FindInjectDll(picked, module.DllHint) is { } pickedDll)
        {
            return IsAcceptedModuleDll(module, pickedDll) ? pickedDll : null;
        }

        // 2) 多版本共存：List() 按安装时间倒序 → 取最新装的那份
        if (!module.IsDirect)
        {
            foreach (OptiScalerBuild build in new OptiScalerLibrary(module.Directory).List())
            {
                string? dll = FindInjectDll(build.Directory, module.DllHint);
                if (dll is not null && IsAcceptedModuleDll(module, dll))
                {
                    return dll;
                }
            }
        }

        // 3) 兜底：整个模块目录递归找（安装程序型模块的文件直接铺在根目录里）
        string? inModuleDir = FindInjectDll(module.Directory, module.DllHint);
        if (inModuleDir is not null && IsAcceptedModuleDll(module, inModuleDir))
        {
            return inModuleDir;
        }

        // Portable overlays may be installed to the pre-migration Modules folder
        // while the active registry uses Cache/modules. Resolve either installed
        // location before claiming the dependency is missing; never manufacture DLLs.
        if (string.Equals(module.Id, GenshinFsrBridgeId, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(AppConfig.UserDataFolder))
        {
            string legacy = Path.Combine(AppConfig.UserDataFolder, "Modules", module.Id);
            string cached = Path.Combine(AppConfig.ModulesCachePath, module.Id);
            foreach (string installed in new[] { legacy, cached })
            {
                string? dll = FindInjectDll(installed, module.DllHint);
                if (dll is not null && IsAcceptedModuleDll(module, dll)) return dll;
            }
        }

        try
        {
            string root = AppConfig.OptiScalerRootPath;
            if (root.Length > 0)
            {
                var library = new OptiScalerLibrary(root);
                foreach (Extensions.Services.OptiScalerBuild build in library.List()
                             .Where(b => string.Equals(b.SourceId, module.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    string? dll = FindInjectDll(build.Directory, module.DllHint);
                    if (dll is not null && IsAcceptedModuleDll(module, dll))
                    {
                        return dll;
                    }
                }
            }
        }
        catch
        {
            // 库里读不出来不算错，顶多这个模块先注不了
        }

        return null;
    }

    private static bool IsAcceptedModuleDll(ModuleDefinition module, string path)
    {
        if (!string.Equals(module.Id, GenshinFsrBridgeId, StringComparison.OrdinalIgnoreCase))
            return true;

        // Accept maintained 2.3.x binaries, including the overlay's 2.3.2.
        // Reject old 2.2 builds without tying support to one exact patch version.
        try
        {
            var version = FileVersionInfo.GetVersionInfo(path);
            return HoYoShadeHub.Extensions.Games.BridgeCompatibility.IsSupported(
                new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把老配置里的「额外注入 DLL」（每个游戏一个路径）搬进模块的手动列表，只搬一次。
    /// </summary>
    public static bool MigrateLegacyExtraInjectDll(GameBiz biz)
    {
        string? old = AppConfig.GetExtraInjectDll(biz);
        if (string.IsNullOrWhiteSpace(old) || AppConfig.GetModuleMigrated(biz))
        {
            return false;
        }

        AppConfig.SetModuleMigrated(biz, true);

        List<string> list = [.. AppConfig.GetManualModuleDlls()];
        if (!list.Contains(old, StringComparer.OrdinalIgnoreCase))
        {
            list.Add(old);
            AppConfig.SetManualModuleDlls(list);
        }

        AppConfig.SetManualModuleDllEnabled(old, true);
        return true;
    }

    /// <summary>在目录里找要注入的那个 dll（可能在子目录里）</summary>
    public static string? FindInjectDll(string directory, string? hint)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        List<string> dlls;
        try
        {
            dlls = [.. Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)];
        }
        catch
        {
            return null;
        }

        if (dlls.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(hint))
        {
            string? exact = dlls.FirstOrDefault(f => string.Equals(Path.GetFileName(f), hint, StringComparison.OrdinalIgnoreCase));
            if (exact is not null)
            {
                return exact;
            }
        }

        foreach (string proxy in OptiScalerLibrary.ProxyDllNames)
        {
            string? hit = dlls.FirstOrDefault(f => string.Equals(Path.GetFileName(f), proxy, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit;
            }
        }

        return dlls.Count == 1 ? dlls[0] : null;
    }

    /// <summary>
    /// 这个模块当前装着的是哪个 tag（下载器写的 build.json）；读不到返回 null。
    /// 已装模块卡片的版本下拉用它标「(当前)」并默认选中。
    /// </summary>
    public static string? InstalledTag(ModuleDefinition module)
    {
        if (module.IsBundled)
            return EnsureBundledModuleInstalled(module) is not null ? "bundled" : null;
        try
        {
            return module.IsDirect
                ? null
                : new OptiScalerLibrary(module.Directory).List().FirstOrDefault()?.Version;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>这个模块装着的所有版本 tag（新 → 旧）。不是 Release 型模块 / 没装返回空表。</summary>
    public static List<string> InstalledTags(ModuleDefinition module)
    {
        if (module.IsBundled)
            return EnsureBundledModuleInstalled(module) is not null ? ["bundled"] : [];
        try
        {
            return module.IsDirect
                ? []
                : [.. new OptiScalerLibrary(module.Directory).List().Select(b => b.Version)];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>某个已装版本的目录（<c>&lt;模块&gt;\&lt;来源&gt;\&lt;tag&gt;</c>）；没有返回 null</summary>
    public static string? VersionDirectory(ModuleDefinition module, string tag)
    {
        try
        {
            return new OptiScalerLibrary(module.Directory).List()
                .FirstOrDefault(b => string.Equals(b.Version, tag, StringComparison.OrdinalIgnoreCase))?.Directory;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 删掉一个**版本**（只删那个版本目录，同来源的其它版本与模块记录都保留）。
    /// 用户要求：多个版本共存之后要能单独删掉不想要的那些。
    /// </summary>
    public static bool DeleteVersion(ModuleDefinition module, string tag)
    {
        if (module is null || module.IsDirect || string.IsNullOrWhiteSpace(tag))
        {
            return false;
        }

        string? directory = VersionDirectory(module, tag);
        if (directory is null)
        {
            return false;
        }

        HysxUtil.TryDeleteDirectory(directory);

        // 来源目录空了就一起收掉
        try
        {
            string? sourceDir = Path.GetDirectoryName(directory);
            if (sourceDir is not null && Directory.Exists(sourceDir)
                && !Directory.EnumerateFileSystemEntries(sourceDir).Any())
            {
                Directory.Delete(sourceDir);
            }
        }
        catch
        {
            // 收不掉无所谓
        }

        // 哪个游戏选了这个 tag 就退回「最新装的那份」，别让选择悬空
        foreach (GameBiz biz in GameBiz.AllGameBizs)
        {
            if (GameId.FromGameBiz(biz) is not { } gameId)
            {
                continue;
            }

            if (string.Equals(AppConfig.GetModuleVersion(gameId, module.Id), tag, StringComparison.OrdinalIgnoreCase))
            {
                AppConfig.SetModuleVersion(gameId, module.Id, null);
            }
        }

        return true;
    }

    /// <summary>
    /// 下载 / 更新一个模块（走 OptiScaler 那套下载器：它已经会处理 zip 和官方安装程序）。
    /// 界面用它。
    /// </summary>
    public static async Task<string> InstallAsync(
        ModuleDefinition module,
        IProgress<DownloadProgress>? progress = null,
        Func<string, Task<bool>>? confirmBeforeRun = null,
        CancellationToken cancellationToken = default,
        string? tag = null)
    {
        if (module.IsBundled)
        {
            AppConfig.SetBundledModuleRemoved(module.Id, false);
            if (EnsureBundledModuleInstalled(module) is null)
                throw new InvalidOperationException("随包 FSR Bridge 资源缺失。");
            return "bundled";
        }

        // 仓库树直链型模块（没有 Release 资产的那种，比如 dlssg_for_sm86）
        if (module.IsDirect)
        {
            string directory = AppConfig.ModuleDirectory(module.Id);
            Directory.CreateDirectory(directory);

            var downloads = new DownloadService();
            string branch = string.IsNullOrWhiteSpace(module.Branch) ? "main" : module.Branch;

            foreach (string file in module.DirectFiles!)
            {
                string name = Path.GetFileName(file);
                string url = HysxHttp.Apply($"https://raw.githubusercontent.com/{module.Repository}/{branch}/{file}");
                await downloads.DownloadToFileAsync(url, Path.Combine(directory, name), null, progress, cancellationToken);
            }

            return $"仓库树直下 {module.DirectFiles.Length} 个文件（{branch}）";
        }

        var source = new OptiScalerSource
        {
            Id = module.Id,
            Name = module.Name,
            Repository = module.Repository,
            TagPattern = module.TagPattern,
        };

        var downloader = new OptiScalerDownloader();
        var library = new OptiScalerLibrary(AppConfig.ModuleDirectory(module.Id));

        List<ExtensionVersion> versions = await downloader.ListVersionsAsync(source, cancellationToken);
        if (versions.Count == 0)
        {
            throw new InvalidOperationException("没读到版本 —— 仓库可能改名/删除，或者网络不通。");
        }

        string chosenTag = !string.IsNullOrWhiteSpace(tag)
            ? versions.FirstOrDefault(v => string.Equals(v.Tag, tag, StringComparison.OrdinalIgnoreCase))?.Tag
                ?? throw new InvalidOperationException($"版本 {tag} 不在这个模块的可装列表里。")
            : versions[0].Tag;
        List<string> assets = await downloader.ListAssetsAsync(source, chosenTag, cancellationToken);
        string? asset = assets.FirstOrDefault(a => a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        ?? OptiScalerDownloader.PickAsset(assets);

        if (string.IsNullOrWhiteSpace(asset))
        {
            throw new InvalidOperationException($"{chosenTag} 里没有可下载的包。");
        }

        await downloader.InstallAsync(source, chosenTag, asset, library, progress, cancellationToken, confirmBeforeRun);

        // 旧版本**保留**（用户要求：多个版本共存，反复切换不用重新下载）。
        // 具体注入哪一份由每个游戏选的版本决定（见 ResolveInjectionDlls）。
        return $"{chosenTag} / {asset}";
    }
}
