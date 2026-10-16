using System.Text;
using System.Text.Json;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>
/// 覆盖包的 <c>ini_config.json</c>：仅明确安装时写进这个游戏 ReShade.ini 的节 / 键。
///
/// <para>
/// 格式：
/// <code>
/// {
///   "set":    { "SECTION": { "Key": "Value" } },
///   "remove": { "SECTION": ["Key1", "Key2"] }
/// }
/// </code>
/// <c>set</c> 和 <c>remove</c> 每次明确安装覆盖包时应用一次；
/// <c>remove</c> 的键从 ini 里摘掉
/// （死键清理）。两个都可选、可为空；文件不存在 / 解析失败 = 没有配置。
/// </para>
/// </summary>
public sealed class GamePackIniConfig
{
    private const string AppliedMarker = ".hysx_ini_config_applied";
    /// <summary>节 → 键 → 值（写入）</summary>
    public Dictionary<string, Dictionary<string, string>> Set { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>节 → 要摘除的键</summary>
    public Dictionary<string, List<string>> Remove { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => Set.Count == 0 && Remove.Count == 0;

    /// <summary>读包里的 ini_config.json；没有 / 读不了返回 null（= 没配置）</summary>
    public static GamePackIniConfig? Load(string? packRoot)
        => LoadCore(packRoot, throwOnError: false);

    private static GamePackIniConfig? LoadCore(string? packRoot, bool throwOnError)
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
                if (throwOnError) throw new InvalidDataException("ini_config.json must contain an object.");
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
        catch when (!throwOnError)
        {
            // 写坏了就当没有配置 —— 不能让一个 json 把启动挡了
            return null;
        }
    }

    /// <summary>把这个配置写进游戏 ini（幂等；有实际改动才落盘）。返回有没有写入。</summary>
    public bool Apply(string iniPath)
    {
        try { return ApplyCore(iniPath, throwOnError: false); }
        catch { return false; }
    }

    private bool ApplyCore(string iniPath, bool throwOnError)
    {
        if (IsEmpty || string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
        {
            if (throwOnError && !IsEmpty) throw new FileNotFoundException("The target ReShade INI was not created before package installation.", iniPath);
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
                    // 包里写成 36,,0,,0,,0 的热键值会让 ReShade 直接不绑这个键（Home 打不开菜单），
                    // 在安装时统一修复，保留用户之后的手动修改。
                    string wanted = ReShadeHotkeys.TryNormalize(section, key, value, out string repaired)
                        ? repaired
                        : value;

                    if (!string.Equals(ini.GetValue(section, key), wanted, StringComparison.Ordinal))
                    {
                        ini.SetValue(section, key, wanted);
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
        catch when (!throwOnError)
        {
            return false;
        }
    }

    /// <summary>Explicit installation only. Every call applies this package, including reinstallations.</summary>
    public bool ApplyForInstall(string iniPath)
    {
        try { return ApplyCore(iniPath, throwOnError: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Failed to apply package INI settings to '{iniPath}': {ex.Message}", ex);
        }
    }

    /// <summary>Explicit installation only. Every call applies this package, including reinstallations.</summary>
    public static bool ApplyForInstall(string packRoot, string iniPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packRoot);
        try
        {
            GamePackIniConfig? config = LoadCore(packRoot, throwOnError: true);
            return config?.ApplyCore(iniPath, throwOnError: true) ?? false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new InvalidDataException($"Failed to install ini_config.json from '{packRoot}' into '{iniPath}': {ex.Message}", ex);
        }
    }

    /// <summary>按覆盖包安装事务执行一次；后续启动拒绝重复写回游戏 ini。</summary>
    public bool ApplyOnce(string packRoot, string iniPath)
    {
        if (string.IsNullOrWhiteSpace(packRoot) || !Directory.Exists(packRoot)) return false;
        string configPath = Path.Combine(packRoot, GameAddonPackUserContent.IniConfigFileName);
        if (!File.Exists(configPath)) return false;

        try
        {
            string markerPath = Path.Combine(packRoot, AppliedMarker);
            if (File.Exists(markerPath))
                return false;

            bool changed = Apply(iniPath);
            string temporary = markerPath + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllText(temporary, "applied" + Environment.NewLine, new UTF8Encoding(false));
            if (File.Exists(markerPath)) File.Replace(temporary, markerPath, null);
            else File.Move(temporary, markerPath);
            return changed;
        }
        catch
        {
            return false;
        }
    }
}
