using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>持久化的一条游戏条目。派生属性（目录 / 进程名 / ini 路径）不存。</summary>
public sealed class GameEntryRecord
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("displayName")]
    public string? DisplayName { get; set; }

    [JsonPropertyName("exePath")]
    public string? ExePath { get; set; }

    /// <summary>Hub 已知游戏的 GameBiz（自定义条目为 null）</summary>
    [JsonPropertyName("biz")]
    public string? Biz { get; set; }

    [JsonPropertyName("isCustom")]
    public bool IsCustom { get; set; }
}

/// <summary>
/// <c>&lt;用户数据目录&gt;\.hysx\games.json</c>。
///
/// 只存「用户自己产生的状态」：
/// <list type="bullet">
/// <item>自定义游戏（exe 路径、显示名）；</item>
/// <item>Hub 已知游戏的 exe 覆盖（目录里有多个 exe、用户挑过一个）；</item>
/// <item>每个游戏的注入模式开关。</item>
/// </list>
/// 自动发现的游戏**每次重算**，不落盘 —— 游戏卸载了就不该再出现在列表里。
/// </summary>
public sealed class GameEntryStore
{
    public const int CurrentSchema = 1;

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Games / InjectMode 是只读属性（= 只增不减的集合），必须显式要求「填进已有实例」，
        // 否则反序列化会直接跳过它们 —— 表现就是「存了但读不回来」。
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };

    [JsonPropertyName("schema")]
    public int Schema { get; set; } = CurrentSchema;

    [JsonPropertyName("games")]
    public List<GameEntryRecord> Games { get; } = [];

    /// <summary>游戏 id → 注入模式开关</summary>
    [JsonPropertyName("injectMode")]
    public Dictionary<string, bool> InjectMode { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>默认落盘位置</summary>
    public static string GetDefaultPath(string userDataFolder) =>
        Path.Combine(userDataFolder, ".hysx", "games.json");

    /// <summary>读；文件不存在 / 坏了都当空的重来（不能让用户看见解析异常）</summary>
    public static GameEntryStore Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                GameEntryStore? store = JsonSerializer.Deserialize<GameEntryStore>(File.ReadAllText(path, Encoding.UTF8), _jsonOptions);
                if (store is not null)
                {
                    store.Games.RemoveAll(g => string.IsNullOrWhiteSpace(g.Id));
                    return store;
                }
            }
        }
        catch
        {
            // 坏了就重建
        }

        return new GameEntryStore();
    }

    public void Save(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string temp = full + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, _jsonOptions), new UTF8Encoding(false));
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            // 存不下来不该让整个页面崩掉
        }
    }

    #region 记录

    public GameEntryRecord? Find(string id) =>
        Games.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));

    public GameEntryRecord Upsert(GameEntryRecord record)
    {
        GameEntryRecord? existing = Find(record.Id);
        if (existing is null)
        {
            Games.Add(record);
            return record;
        }

        existing.DisplayName = record.DisplayName ?? existing.DisplayName;
        existing.ExePath = record.ExePath ?? existing.ExePath;
        existing.Biz = record.Biz ?? existing.Biz;
        existing.IsCustom |= record.IsCustom;
        return existing;
    }

    public bool Remove(string id)
    {
        InjectMode.Remove(id);
        return Games.RemoveAll(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
    }

    #endregion

    #region 注入模式

    public bool GetUseInjectMode(string id) =>
        InjectMode.TryGetValue(id, out bool value) && value;

    public void SetUseInjectMode(string id, bool value) => InjectMode[id] = value;

    #endregion

    #region 条目 ←→ 记录

    /// <summary>把存下来的状态套到一个（重新发现的）条目上：exe 覆盖 + 注入模式</summary>
    public void ApplyTo(GameEntry entry)
    {
        if (Find(entry.Id) is { } record && !string.IsNullOrWhiteSpace(record.ExePath))
        {
            entry.ExePath = record.ExePath;
        }

        entry.UseInjectMode = GetUseInjectMode(entry.Id);
    }

    public GameEntryRecord CaptureFrom(GameEntry entry) => new()
    {
        Id = entry.Id,
        DisplayName = entry.DisplayName,
        ExePath = entry.ExePath,
        Biz = entry.Biz?.Value,
        IsCustom = entry.IsCustom,
    };

    /// <summary>把条目的当前状态存回去（只存自定义 / 覆盖过 exe 的）</summary>
    public void Capture(GameEntry entry)
    {
        Upsert(CaptureFrom(entry));
        SetUseInjectMode(entry.Id, entry.UseInjectMode);
    }

    #endregion
}
