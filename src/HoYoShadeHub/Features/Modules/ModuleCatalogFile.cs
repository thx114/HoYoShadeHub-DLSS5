using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoYoShadeHub.Features.Modules;

/// <summary>远端 <c>catalog/modules.json</c> 的一条模块</summary>
public sealed class ModuleManifest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    /// <summary>owner/repo</summary>
    [JsonPropertyName("repository")]
    public string Repository { get; set; } = string.Empty;

    /// <summary>tag 正则过滤</summary>
    [JsonPropertyName("tagPattern")]
    public string TagPattern { get; set; } = @"^v?\d";

    [JsonPropertyName("homepage")]
    public string? Homepage { get; set; }

    /// <summary>注入那个 dll 的文件名（在模块目录里找它；找不到就按代理 dll 名找）</summary>
    [JsonPropertyName("dllHint")]
    public string? DllHint { get; set; }

    [JsonPropertyName("tags")]
    public string[]? Tags { get; set; }

    /// <summary>
    /// 有些模块不发 Release 资产、文件直接在仓库树里（dlssg_for_sm86 的 version.dll + dlssg_sm86.ini）：
    /// 给这个数组就走 raw 直下，忽略 tagPattern。
    /// </summary>
    [JsonPropertyName("directFiles")]
    public string[]? DirectFiles { get; set; }

    /// <summary>直下用哪个分支（默认 main）</summary>
    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    /// <summary><c>true</c> = 这条被删了（墓碑）</summary>
    [JsonPropertyName("removed")]
    public bool Removed { get; set; }

    /// <summary>
    /// 随包模块：dll 就在启动器自带资源里，不用下载。
    /// 以前只有 C# 能置 true（schema 里没这个键），于是"加一个随包模块"必须改代码 ——
    /// 远端能用这个字段之后，随包模块也只是 catalog/modules.json 里的一条。
    /// </summary>
    [JsonPropertyName("bundled")]
    public bool Bundled { get; set; }

    /// <summary>能接受的最低 dll 版本（如 <c>2.3.1</c>）。不写 = 不门控</summary>
    [JsonPropertyName("minVersion")]
    public string? MinVersion { get; set; }

    /// <summary>
    /// 历史安装位置（模板）。支持 <c>{userData}</c> / <c>{modulesCache}</c> / <c>{id}</c> 占位符，
    /// 相对路径按用户数据目录展开 —— 迁移前装在别处的模块靠它仍然能被认出来，不用改代码。
    /// </summary>
    [JsonPropertyName("legacyDirs")]
    public string[]? LegacyDirs { get; set; }

    public ModuleDefinition? ToDefinition()
    {
        if (string.IsNullOrWhiteSpace(Id) || string.IsNullOrWhiteSpace(Repository))
        {
            return null;
        }

        return new ModuleDefinition(
            Id,
            string.IsNullOrWhiteSpace(Name) ? Id : Name,
            Description ?? string.Empty,
            Repository,
            TagPattern,
            Homepage,
            string.IsNullOrWhiteSpace(DllHint) ? "version.dll" : DllHint!,
            Tags,
            DirectFiles,
            string.IsNullOrWhiteSpace(Branch) ? "main" : Branch!,
            Bundled,
            MinVersion,
            LegacyDirs);
    }
}

/// <summary>远端 <c>catalog/modules.json</c> 文档</summary>
public sealed class ModuleCatalogDocument
{
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    [JsonPropertyName("modules")]
    public ModuleManifest[] Modules { get; set; } = [];
}

/// <summary>
/// 读远端模块目录（跟插件/OptiScaler 一套做法：只当覆盖层，读不到就用内置的）。
/// 文件由 <c>RemoteCatalogService</c> 每天拉一次、缓存在 &lt;用户数据目录&gt;\.hysx\catalog\modules.json。
/// </summary>
public static class ModuleCatalogFile
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>读文件并应用成覆盖层；读不到 / 坏了就把覆盖层清空（= 只用内置）。</summary>
    public static void Apply(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            ModuleRegistry.SetRemoteOverlay(null);
            return;
        }

        try
        {
            ModuleCatalogDocument? document = JsonSerializer.Deserialize<ModuleCatalogDocument>(File.ReadAllText(path), _jsonOptions);
            if (document is null)
            {
                ModuleRegistry.SetRemoteOverlay(null);
                return;
            }

            List<ModuleDefinition> modules = [];
            List<string> removed = [];

            foreach (ModuleManifest manifest in document.Modules)
            {
                if (manifest.Removed)
                {
                    if (!string.IsNullOrWhiteSpace(manifest.Id))
                    {
                        removed.Add(manifest.Id);
                    }

                    continue;
                }

                if (manifest.ToDefinition() is { } definition)
                {
                    modules.Add(definition);
                }
            }

            ModuleRegistry.SetRemoteOverlay(modules, removed);
        }
        catch
        {
            // 远端目录只是锦上添花，坏了不该影响其它东西
            ModuleRegistry.SetRemoteOverlay(null);
        }
    }
}
