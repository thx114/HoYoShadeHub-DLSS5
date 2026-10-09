using System;
using System.Collections.Generic;
using System.IO;
using HoYoShadeHub.Extensions.Modules;

namespace HoYoShadeHub.Features.Modules;

/// <summary>
/// 把模块目录里的一条变成运行期定义。<see cref="ModuleDefinition"/> 在启动器侧（它要用 AppConfig），
/// 所以这个扩展方法留在启动器，schema 本身在 Extensions 里（和插件/OptiScaler 的目录同一套做法）。
/// </summary>
public static class ModuleManifestExtensions
{
    public static ModuleDefinition? ToDefinition(this ModuleManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Repository))
        {
            return null;
        }

        return new ModuleDefinition(
            manifest.Id,
            string.IsNullOrWhiteSpace(manifest.Name) ? manifest.Id : manifest.Name,
            manifest.Description ?? string.Empty,
            manifest.Repository,
            manifest.TagPattern,
            manifest.Homepage,
            string.IsNullOrWhiteSpace(manifest.DllHint) ? "version.dll" : manifest.DllHint!,
            manifest.Tags,
            manifest.DirectFiles,
            string.IsNullOrWhiteSpace(manifest.Branch) ? "main" : manifest.Branch!,
            manifest.Bundled,
            manifest.MinVersion,
            manifest.LegacyDirs);
    }
}

/// <summary>
/// 读模块目录并应用成覆盖层。两层叠着读：
/// ① 远端 / 随包缓存的 <c>catalog\modules.json</c>；
/// ② 用户自己的 <c>&lt;用户数据目录&gt;\.hysx\catalog\modules.user.json</c>（后读的覆盖先读的，
///    所以用户能改远端条目、也能加远端还没有的条目，不用等我们推仓库、更不用等发版）。
/// 两层都读不到就清空覆盖层（= 只用内置兜底）。
/// </summary>
public static class ModuleCatalogFile
{
    /// <summary>用户级模块目录文件路径</summary>
    public static string UserPath => ModuleUserCatalog.PathFor(AppConfig.UserDataFolder);

    /// <summary>确保用户级文件存在（不存在就写一份带字段说明的空壳），返回路径</summary>
    public static string EnsureUserFile() => ModuleUserCatalog.EnsureTemplate(AppConfig.UserDataFolder) ?? string.Empty;

    /// <summary>往用户级目录加/改一条（同 id 覆盖），写完立刻重新应用。界面「从仓库添加」用。</summary>
    public static bool AddUserModule(ModuleManifest module)
    {
        if (!ModuleUserCatalog.AddOrUpdate(UserPath, module))
        {
            return false;
        }

        Apply(Features.Plugins.RemoteCatalogService.ModulesCachePath);
        return true;
    }

    /// <summary>读文件并应用成覆盖层；读不到 / 坏了就当没这一层。</summary>
    public static void Apply(string? path)
    {
        var (manifests, tombstones) = ModuleCatalogMerge.Merge([ReadDocument(path), ReadDocument(UserPath)]);

        List<ModuleDefinition> modules = [];
        foreach (ModuleManifest manifest in manifests)
        {
            if (manifest.ToDefinition() is { } definition)
            {
                modules.Add(definition);
            }
        }

        ModuleRegistry.SetRemoteOverlay(modules, tombstones);
    }

    private static ModuleCatalogDocument? ReadDocument(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            return ModuleCatalogDocument.Parse(File.ReadAllText(path!));
        }
        catch
        {
            // 目录只是锦上添花，读不了不该影响别的东西
            return null;
        }
    }
}
