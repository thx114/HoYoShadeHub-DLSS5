using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Networking;
using HoYoShadeHub.Extensions.Services;
using System;
using System.Collections.Generic;
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
    string Branch = "main")
{
    /// <summary>
    /// 有些模块压根不发 Release 资产，文件直接躺在仓库树里（比如 dlssg_for_sm86 的
    /// <c>version.dll</c> + <c>dlssg_sm86.ini</c>）。给了 <see cref="DirectFiles"/> 就走这条路：
    /// 从 <c>raw.githubusercontent.com/&lt;repository&gt;/&lt;branch&gt;/&lt;file&gt;</c> 下到模块目录。
    /// </summary>
    public bool IsDirect => DirectFiles is { Length: > 0 };

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

/// <summary>内置模块表 + 远端目录覆盖 + 每个游戏用哪些模块 + 下载部署</summary>
public static class ModuleRegistry
{
    /// <summary>
    /// 内置模块。DLSS-NR on AMD 以前挂在「全局插件 → OptiScaler 可下载」里，
    /// 它不是 OptiScaler（是另一套要单独注入的东西），所以搬到模块里。
    /// 远端 <c>catalog/modules.json</c> 可以按 id 覆盖 / 追加（不用发版）。
    /// </summary>
    public static IReadOnlyList<ModuleDefinition> Builtin { get; } =
    [
        new ModuleDefinition(
            "dlssnr-amd",
            "DLSS-NR on AMD",
            "A 卡用的 DLSS 神经渲染分支。它只发官方安装程序：点「下载」会先问一句，然后运行它 —— 装到模块目录即可。" +
            "装完那里会有 version.dll；某个游戏要用它就到左侧「模块」页勾上。",
            "danielblnc/DLSS-NR-on-AMD",
            @"^v?\d",
            "https://github.com/danielblnc/DLSS-NR-on-AMD/releases",
            "version.dll",
            ["dlssnr", "amd", "neural"]),

        new ModuleDefinition(
            "dlssg-sm86",
            "DLSSG for SM86（RTX 30/20 帧生成）",
            "在 RTX 30 系（SM86）和 20 系（SM75）上启用 NVIDIA DLSS 帧生成（DLSS-G），Windows x64 / D3D12。" +
            "运行文件是 version.dll + dlssg_sm86.ini（仓库根目录是 310.9 版，310.1/ 是老版运行库）。" +
            "生成帧数量、优化档位都在 ini 里（MaxGeneratedFrames 3=4X / 5=6X）。",
            "sdli1995/dlssg_for_sm86",
            @"^\d",
            "https://github.com/sdli1995/dlssg_for_sm86",
            "version.dll",
            ["dlssg", "framegen", "rtx20", "rtx30"],
            ["version.dll", "dlssg_sm86.ini"]),

        // 原神 DLSS5 链路里「让 OptiScaler 看见 FSR2」的那一环。
        // 它是被注入的原生 DLL，不是 ReShade 插件，也不是 OptiScaler 本身：
        // hook 游戏的 D3D11 与 GetProcAddress，把标准 ffxFsr2* 接口垫出来。
        // 上游是 AizawaHikaru233/genshin_fsr_brigde（GPL-3.0），但它只发「一键配置」整包
        //（含 OptiScaler / ReShade / 安装器），没有单文件 Release，所以这里从 CXP 的
        // 打包目录直下那两个文件（CXP 用的是同一份二进制，SHA-256 1AB7FBD9...）。
        new ModuleDefinition(
            "genshin-fsr-bridge",
            "Genshin FSR Bridge（原神 DX11 FSR2 桥）",
            "让 OptiScaler 在原神这种「FSR2 静态链进 exe、符号不导出」的 DX11 游戏里看见 FSR2：" +
            "hook 游戏的 D3D11 与 GetProcAddress，把标准 ffxFsr2* 接口垫出来。" +
            "注入进游戏进程后，游戏里抗锯齿必须选 FSR2、渲染精度低于 1 才生效。" +
            "这是 DLSS5 链路必需的一环，不是 ReShade 插件。",
            "CXP-2024/dlss5_for_genshinimpact",
            @"^v",
            "https://github.com/AizawaHikaru233/genshin_fsr_brigde",
            "Dx11FsrBridge.dll",
            ["dlss5", "fsr2", "genshin", "bridge"],
            ["release/configs/Dx11FsrBridge.dll", "release/configs/Dx11FsrBridge.ini"]),
    ];

    /// <summary>远端目录（catalog/modules.json）里读到的模块；同 id 覆盖内置</summary>
    public static IReadOnlyList<ModuleDefinition> RemoteOverlay { get; private set; } = [];

    /// <summary>远端目录里用 <c>removed: true</c> 删掉的模块 id</summary>
    public static IReadOnlyCollection<string> RemovedIds { get; private set; } = [];

    public static void SetRemoteOverlay(IEnumerable<ModuleDefinition>? modules, IEnumerable<string>? removed = null)
    {
        RemoteOverlay = modules is null ? [] : [.. modules];
        RemovedIds = removed is null ? [] : [.. removed];
    }

    /// <summary>内置 + 远端覆盖（同 id 用远端那条）</summary>
    public static List<ModuleDefinition> All()
    {
        var result = new List<ModuleDefinition>(Builtin)
            .Where(m => !RemovedIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (ModuleDefinition module in RemoteOverlay)
        {
            if (string.IsNullOrWhiteSpace(module.Id))
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
            entries.Add(new ModuleEntry
            {
                Key = path,
                Name = Path.GetFileName(path),
                Description = exists ? path : path + "（文件已经不在了）",
                IsBuiltin = false,
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

    /// <summary>这个游戏启动时真正要注入的模块：勾了 ∩ 全局开着 ∩ 文件在</summary>
    public static List<(string Name, string DllPath)> ResolveInjectionDlls(GameId gameId)
    {
        var result = new List<(string Name, string DllPath)>();

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

            result.Add((entry.Name, entry.DllPath));
        }

        return result;
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
    /// 模块要注入的 DLL：用户手动指定的 &gt; 模块目录里找（先按提示名，再按代理 dll 名）
    /// &gt; 以前从「OptiScaler 可下载」装的那份（更新后自动接上，不用重装）。
    /// </summary>
    public static string? ResolveDllPath(ModuleDefinition module)
    {
        string? chosen = AppConfig.GetModuleDllPath(module.Id);
        if (!string.IsNullOrWhiteSpace(chosen) && File.Exists(chosen))
        {
            return chosen;
        }

        string? inModuleDir = FindInjectDll(module.Directory, module.DllHint);
        if (inModuleDir is not null)
        {
            return inModuleDir;
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
                    if (dll is not null)
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
    /// 装完之后的「补齐文件」。现在只有原神 FSR 桥需要：它的 DLL 下下来就够用，
    /// 但 ini 缺失时补一份最小模板（桥的每个键都有代码默认值，ini 只是方便用户改）。
    /// 已经有一份就一律不动。
    /// </summary>
    private static void EnsurePostInstallFiles(ModuleDefinition module, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        if (string.Equals(module.DllHint, OptiScalerRuntime.FsrBridgeDllName, StringComparison.OrdinalIgnoreCase))
        {
            OptiScalerRuntime.EnsureFsrBridgeIni(directory);
        }
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

            EnsurePostInstallFiles(module, directory);

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

        EnsurePostInstallFiles(module, AppConfig.ModuleDirectory(module.Id));

        return $"{chosenTag} / {asset}";
    }
}
