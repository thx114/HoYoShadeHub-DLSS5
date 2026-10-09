using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoYoShadeHub.Extensions.Modules;

/// <summary>
/// 模块目录里的一条模块。远端 <c>catalog/modules.json</c>、随包种子、用户级
/// <c>modules.user.json</c> 都是同一个形状 —— 所以"加/改模块"永远只是改 JSON。
/// </summary>
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

    /// <summary><c>true</c> = 这条被删了（墓碑）。能删远端条目，也能删内置条目。</summary>
    [JsonPropertyName("removed")]
    public bool Removed { get; set; }

    /// <summary>随包模块：dll 就在启动器自带资源里，不用下载</summary>
    [JsonPropertyName("bundled")]
    public bool Bundled { get; set; }

    /// <summary>能接受的最低 dll 版本（如 <c>2.3.1</c>）。不写 = 不门控</summary>
    [JsonPropertyName("minVersion")]
    public string? MinVersion { get; set; }

    /// <summary>
    /// 历史安装位置（模板）。支持 <c>{userData}</c> / <c>{modulesCache}</c> / <c>{id}</c> 占位符，
    /// 相对路径按用户数据目录展开。
    /// </summary>
    [JsonPropertyName("legacyDirs")]
    public string[]? LegacyDirs { get; set; }

    /// <summary>
    /// 「正身」DLL 文件名：模块目录里如果出现的是 <see cref="AliasDllNames"/> 里的历史别名，
    /// 就按这个名字归位（原神桥以前被旧下载器改名成 <c>OptiScaler.dll</c>，就是靠这条认回来的）。
    /// 不写 = 不认别名、不改名。
    /// </summary>
    [JsonPropertyName("canonicalDllName")]
    public string? CanonicalDllName { get; set; }

    /// <summary>能被认成同一个模块的历史 DLL 名（只在模块目录里比，不做全局按名匹配）</summary>
    [JsonPropertyName("aliasDllNames")]
    public string[]? AliasDllNames { get; set; }
}

/// <summary>模块目录文档（远端 / 随包种子 / 用户级共用这个形状）</summary>
public sealed class ModuleCatalogDocument
{
    private static readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        WriteIndented = true,
        // 中文名字写成中文，别变成 \uXXXX（用户要读、要改这份文件）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = 1;

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>文件开头的说明（正常读会带回，写回时保持原样）</summary>
    [JsonPropertyName("_readme")]
    public string[]? Readme { get; set; }

    [JsonPropertyName("modules")]
    public ModuleManifest[] Modules { get; set; } = [];

    /// <summary>解析一段 JSON；空/坏了返回 null（调用方当"没有这份目录"处理）</summary>
    public static ModuleCatalogDocument? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ModuleCatalogDocument>(json, _readOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>序列化（用户级目录文件用它写盘）</summary>
    public string ToJson() => JsonSerializer.Serialize(this, _writeOptions);
}

/// <summary>
/// 把若干层模块目录叠起来。后一层说了算：
/// 同 id 的活条目覆盖前面的；后一层的墓碑能下架前面（含内置兜底条）的同 id 条目；
/// 后一层的活条目能撤销前一层的墓碑。纯函数，界面/自测直接调。
/// </summary>
public static class ModuleCatalogMerge
{
    public static (List<ModuleManifest> Modules, List<string> Removed) Merge(IEnumerable<ModuleCatalogDocument?> layers)
    {
        List<ModuleManifest> modules = [];
        List<string> removed = [];

        foreach (ModuleCatalogDocument? layer in layers)
        {
            if (layer is null)
            {
                continue;
            }

            foreach (ModuleManifest manifest in layer.Modules)
            {
                if (string.IsNullOrWhiteSpace(manifest.Id))
                {
                    continue;
                }

                if (manifest.Removed)
                {
                    removed.RemoveAll(r => string.Equals(r, manifest.Id, StringComparison.OrdinalIgnoreCase));
                    removed.Add(manifest.Id);

                    // 前面几层里同 id 的活条目也一起撤掉，结果自洽（消费方不用再自己过滤一遍）
                    modules.RemoveAll(m => string.Equals(m.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));
                    continue;
                }

                // 这一层给了活条目，就把同 id 的墓碑撤掉
                removed.RemoveAll(r => string.Equals(r, manifest.Id, StringComparison.OrdinalIgnoreCase));
                modules.Add(manifest);
            }
        }

        return (modules, removed);
    }
}

/// <summary>
/// 用户级模块目录：<c>&lt;用户数据目录&gt;\.hysx\catalog\modules.user.json</c>。
///
/// <para>
/// 和远端 <c>catalog/modules.json</c> 完全同一个形状，读的时候后读的覆盖先读的 ——
/// 所以用户能覆盖远端条目、也能加远端还没有的条目，不用等我们推仓库、更不用等发版。
/// 全部是路径进 / 结果出的纯函数，界面和自测都直接用它。
/// </para>
/// </summary>
public static class ModuleUserCatalog
{
    public const string FileName = "modules.user.json";

    /// <summary>文件路径；读不到用户数据目录时返回空串</summary>
    public static string PathFor(string? userDataFolder)
        => string.IsNullOrWhiteSpace(userDataFolder)
            ? string.Empty
            : Path.Combine(userDataFolder, ".hysx", "catalog", FileName);

    /// <summary>文件内容（不存在/坏了返回 null）</summary>
    public static ModuleCatalogDocument? Read(string? path)
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
            return null;
        }
    }

    /// <summary>文件里的模块 id（按出现顺序）</summary>
    public static IReadOnlyList<string> Ids(string? path)
    {
        ModuleCatalogDocument? document = Read(path);
        return document is null
            ? []
            : [.. document.Modules.Where(m => !string.IsNullOrWhiteSpace(m.Id)).Select(m => m.Id)];
    }

    /// <summary>
    /// 不存在就写一份带字段说明的空壳，返回路径；已经存在就原样返回，不覆盖用户写的东西。
    /// </summary>
    public static string? EnsureTemplate(string? userDataFolder)
    {
        string path = PathFor(userDataFolder);
        if (path.Length == 0 || File.Exists(path))
        {
            return path.Length == 0 ? null : path;
        }

        var document = new ModuleCatalogDocument
        {
            UpdatedAt = DateTimeOffset.UtcNow,
            Readme =
            [
                "用户级模块目录：在这里加/改模块，不用等启动器发版，也不用改 catalog/modules.json。",
                "和远端 catalog/modules.json 同一个形状；同一个 id 时，这份文件里的覆盖远端/内置的那条。",
                "id / name / repository(owner/repo) 必填；可选：description / tagPattern / homepage / dllHint / tags / directFiles+branch / bundled / minVersion / legacyDirs。",
                "{ \"id\": \"某个模块\", \"removed\": true } 表示把它下架（连内置的也能下架）。",
                "改完在「全局插件 → 模块」里刷新一下就能看到（启动器也会自动重新读）。",
            ],
            Modules = [],
        };

        return Write(path, document) ? path : null;
    }

    /// <summary>加/改一条（同 id 覆盖）。文件坏了就拒绝写，绝不覆盖用户内容。</summary>
    public static bool AddOrUpdate(string? path, ModuleManifest? module)
    {
        if (string.IsNullOrWhiteSpace(path) || module is null || string.IsNullOrWhiteSpace(module.Id))
        {
            return false;
        }

        bool exists = File.Exists(path!);
        ModuleCatalogDocument? document = exists ? Read(path) : new ModuleCatalogDocument();

        if (document is null)
        {
            // 文件在但读不出来（手写坏了）：不要拿一份新的把它盖掉
            return false;
        }

        document.Modules =
        [
            .. document.Modules.Where(m => !string.Equals(m.Id, module.Id, StringComparison.OrdinalIgnoreCase)),
            module,
        ];
        document.UpdatedAt = DateTimeOffset.UtcNow;
        document.Readme ??= null;

        return Write(path!, document);
    }

    /// <summary>原子写（先写 .tmp 再搬），失败返回 false</summary>
    private static bool Write(string path, ModuleCatalogDocument document)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temp = path + ".tmp";
            File.WriteAllText(temp, document.ToJson());
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
