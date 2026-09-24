using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 插件 / 模块 / OptiScaler 构建的「显示名 + 版本号」覆盖表。
///
/// <para>
/// 用户可以给一个已安装项起自己看得懂的名字、改一个自己认得的版本号。
/// <b>只改显示，不动磁盘</b> —— 目录名 / 文件名是程序用来定位和注入的，改了会直接坏掉。
/// </para>
///
/// <para>
/// 存在 <c>&lt;用户数据目录&gt;\.hysx\catalog-names.json</c>，
/// 键是「类别 + 稳定 id」（插件用文件名、模块用 id、OptiScaler 用构建 id），
/// 所以换版本 / 更新之后覆盖名不会丢。
/// </para>
/// </summary>
internal sealed class CatalogNameStore
{
    private readonly string _path;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>一条覆盖记录</summary>
    public sealed record Entry(string? Name, string? Version);

    private CatalogNameStore(string path)
    {
        _path = path;
        LoadFromDisk();
    }

    /// <summary>默认落在 &lt;用户数据目录&gt;\.hysx\catalog-names.json</summary>
    public static CatalogNameStore Load()
    {
        string root = AppConfig.UserDataFolder;

        if (string.IsNullOrWhiteSpace(root))
        {
            // 没有用户数据目录时退到临时目录，至少不要让页面开不起来
            return new CatalogNameStore(Path.Combine(Path.GetTempPath(), "hysx-catalog-names.json"));
        }

        return new CatalogNameStore(Path.Combine(root, ".hysx", "catalog-names.json"));
    }

    /// <summary>拼一个稳定的键：类别 + id（id 由调用方给，插件用文件名、OptiScaler 用构建 id…）</summary>
    public static string Key(string category, string id) => category + ":" + id;

    /// <summary>拿覆盖名（没设过返回 null）</summary>
    public string? GetName(string key) =>
        _entries.TryGetValue(key, out Entry? e) && e.Name is { Length: > 0 } ? e.Name : null;

    /// <summary>拿覆盖版本号（没设过返回 null）</summary>
    public string? GetVersion(string key) =>
        _entries.TryGetValue(key, out Entry? e) && e.Version is { Length: > 0 } ? e.Version : null;

    /// <summary>同时设置名字和版本号；两个都为空就删掉这条记录</summary>
    public void Set(string key, string? name, string? version)
    {
        name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        version = string.IsNullOrWhiteSpace(version) ? null : version.Trim();

        if (name is null && version is null)
        {
            _entries.Remove(key);
        }
        else
        {
            _entries[key] = new Entry(name, version);
        }

        Save();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 存不下只是下次打开丢了自定义名，不该弹错
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            Dictionary<string, Entry>? map =
                JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path));

            if (map is null)
            {
                return;
            }

            foreach ((string key, Entry value) in map)
            {
                if (value is not null && (!string.IsNullOrWhiteSpace(value.Name) || !string.IsNullOrWhiteSpace(value.Version)))
                {
                    _entries[key] = value;
                }
            }
        }
        catch
        {
            // 读坏了就当没设过，不能因为它让页面打不开
        }
    }
}
