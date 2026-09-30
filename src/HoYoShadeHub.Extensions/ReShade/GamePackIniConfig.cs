using System.Text.Json;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>
/// 覆盖包的 <c>ini_config.json</c>：每次启动游戏时写进这个游戏 ReShade.ini 的节 / 键。
///
/// <para>
/// 格式：
/// <code>
/// {
///   "set":    { "SECTION": { "Key": "Value" } },
///   "remove": { "SECTION": ["Key1", "Key2"] }
/// }
/// </code>
/// <c>set</c> 的键每次启动都重新写入（ReShade 退出时会把自己的内存配置写回 ini，
/// 不重复 enforce 的话包里的配置只生效一次）；<c>remove</c> 的键从 ini 里摘掉
/// （死键清理）。两个都可选、可为空；文件不存在 / 解析失败 = 没有配置。
/// </para>
/// </summary>
public sealed class GamePackIniConfig
{
    /// <summary>节 → 键 → 值（写入）</summary>
    public Dictionary<string, Dictionary<string, string>> Set { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>节 → 要摘除的键</summary>
    public Dictionary<string, List<string>> Remove { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Set.Count == 0 && Remove.Count == 0;

    /// <summary>读包里的 ini_config.json；没有 / 读不了返回 null（= 没配置）</summary>
    public static GamePackIniConfig? Load(string? packRoot)
    {
        if (packRoot is null)
        {
            return null;
        }

        string path = Path.Combine(packRoot, GameAddonPackUserContent.IniConfigFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var config = new GamePackIniConfig();

            if (root.TryGetProperty("set", out JsonElement set) && set.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty section in set.EnumerateObject())
                {
                    if (section.Value.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonProperty key in section.Value.EnumerateObject())
                    {
                        pairs[key.Name] = key.Value.ValueKind == JsonValueKind.String
                            ? key.Value.GetString() ?? string.Empty
                            : key.Value.ToString();
                    }

                    if (pairs.Count > 0)
                    {
                        config.Set[section.Name] = pairs;
                    }
                }
            }

            if (root.TryGetProperty("remove", out JsonElement remove) && remove.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty section in remove.EnumerateObject())
                {
                    List<string>? keys = null;
                    if (section.Value.ValueKind == JsonValueKind.Array)
                    {
                        keys = section.Value.EnumerateArray()
                            .Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : null)
                            .Where(key => !string.IsNullOrWhiteSpace(key))
                            .Select(key => key!)
                            .ToList();
                    }
                    else if (section.Value.ValueKind == JsonValueKind.String)
                    {
                        string? single = section.Value.GetString();
                        keys = string.IsNullOrWhiteSpace(single) ? null : [single];
                    }

                    if (keys is { Count: > 0 })
                    {
                        config.Remove[section.Name] = keys;
                    }
                }
            }

            return config.IsEmpty ? null : config;
        }
        catch
        {
            // 写坏了就当没有配置 —— 不能让一个 json 把启动挡了
            return null;
        }
    }

    /// <summary>把这个配置写进游戏 ini（幂等；有实际改动才落盘）。返回有没有写入。</summary>
    public bool Apply(string iniPath)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
        {
            return false;
        }

        try
        {
            IniDocument ini = IniDocument.Load(iniPath);
            bool changed = false;

            foreach ((string section, Dictionary<string, string> keys) in Set)
            {
                foreach ((string key, string value) in keys)
                {
                    if (!string.Equals(ini.GetValue(section, key), value, StringComparison.Ordinal))
                    {
                        ini.SetValue(section, key, value);
                        changed = true;
                    }
                }
            }

            foreach ((string section, List<string> keys) in Remove)
            {
                foreach (string key in keys)
                {
                    if (ini.ContainsKey(section, key))
                    {
                        ini.RemoveKey(section, key);
                        changed = true;
                    }
                }
            }

            if (changed)
            {
                ini.Save(iniPath);
            }

            return changed;
        }
        catch
        {
            return false;
        }
    }
}
