using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace HoYoShadeHub.Features.Xxmi;

/// <summary>
/// mod 的「显示名」覆盖表。
///
/// <para>
/// 用户可以直接给一个 mod 起自己看得懂的名字（比如把 <c>gb_719975</c> 改成「迪娜的帽子」）。
/// <b>只改显示，不动磁盘</b> —— 目录名还是原来的，因为 3DMigoto / XXMI 靠目录名认 mod，
/// 而且 GameBanana 的更新逻辑也靠 <c>gb_{modId}</c> 这个名字精确定位旧版本。
/// </para>
///
/// <para>
/// 键用「稳定身份」（GameBanana 用 <c>gb:{modId}</c>，其余用去掉 DISABLED 的目录名），
/// 这样启用/禁用改名（加 DISABLED 后缀）之后，自定义名不会丢。
/// </para>
/// </summary>
internal sealed class XxmiRenameStore
{
    private readonly string _path;
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    private XxmiRenameStore(string path)
    {
        _path = path;
        LoadFromDisk();
    }

    /// <summary>默认落在用户数据目录下的 xxmi-renames.json</summary>
    public static XxmiRenameStore Load() =>
        new(Path.Combine(AppConfig.UserDataFolder, "xxmi-renames.json"));

    /// <summary>拿这个 mod 的自定义名；没设过返回 null</summary>
    public string? GetName(string identity) =>
        _names.TryGetValue(identity, out string? name) && name.Length > 0 ? name : null;

    /// <summary>设置/清除自定义名（传 null 或空串 = 清除）</summary>
    public void SetName(string identity, string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            _names.Remove(identity);
        }
        else
        {
            _names[identity] = name.Trim();
        }

        Save();
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            Dictionary<string, string>? map =
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_path));

            if (map is null)
            {
                return;
            }

            foreach ((string key, string value) in map)
            {
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value))
                {
                    _names[key] = value;
                }
            }
        }
        catch
        {
            // 读坏了就当没设过，不能因为它让页面打不开
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_names, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 存不下只是下次打开丢了自定义名，不该弹错
        }
    }
}
