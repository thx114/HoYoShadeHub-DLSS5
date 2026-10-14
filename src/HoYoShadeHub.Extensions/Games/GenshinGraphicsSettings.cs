using System.Text;
using System.Text.Json.Nodes;

namespace HoYoShadeHub.Extensions.Games;

public static class GenshinGraphicsSettings
{
    public static IReadOnlyList<string> RenderScaleLabels { get; } =
        Array.AsReadOnly(new[] { "0.6", "0.8", "0.9", "1.0", "1.1", "1.2", "1.3", "1.4", "1.5" });

    public static IReadOnlyList<string> AntiAliasingLabels { get; } =
        Array.AsReadOnly(new[] { "FSR", "SMAA", "关闭" });

    // Game indices are not ordered by scale: 0.9 was added after 1.5.
    private static readonly int[] RenderScaleIndices = [0, 1, 8, 2, 3, 4, 5, 6, 7];
    private static readonly int[] AntiAliasingIndices = [1, 2, 0];

    public static (int RenderScaleIndex, int AntiAliasingIndex) Read(byte[]? data)
    {
        if (data is null || data.Length == 0)
            return (-1, -1);

        JsonObject root = ParseRoot(data);
        JsonObject? graphics = ReadObject(root, "graphicsData");
        JsonObject? perf = ReadObject(root, "globalPerfData");
        return (Array.IndexOf(RenderScaleIndices, ReadIndex(graphics, perf, 2) ?? -1),
            Array.IndexOf(AntiAliasingIndices, ReadIndex(graphics, perf, 8) ?? -1));
    }

    public static byte[] Apply(byte[] data, int? renderScaleIndex, int? antiAliasingIndex)
    {
        if (renderScaleIndex is { } scale && (uint)scale >= RenderScaleIndices.Length)
            throw new ArgumentOutOfRangeException(nameof(renderScaleIndex));
        if (antiAliasingIndex is { } aa && (uint)aa >= AntiAliasingIndices.Length)
            throw new ArgumentOutOfRangeException(nameof(antiAliasingIndex));
        if (renderScaleIndex is null && antiAliasingIndex is null)
            return data;

        JsonObject root = ParseRoot(data);
        JsonObject? existingGraphics = ReadObject(root, "graphicsData");
        JsonObject? existingPerf = ReadObject(root, "globalPerfData");
        if (existingGraphics is null && existingPerf is null)
            throw new InvalidOperationException("原神图形配置尚未生成，请先启动游戏并保存一次图形设置。");

        JsonObject graphics = existingGraphics ?? new JsonObject();
        JsonObject perf = existingPerf ?? new JsonObject();
        JsonArray grades = GetArray(graphics, "customVolatileGrades");
        JsonArray items = GetArray(perf, "saveItems");
        string version = perf["portedVersion"]?.GetValue<string>()
            ?? graphics["volatileVersion"]?.GetValue<string>() ?? string.Empty;

        // Older installations may have only one representation. Migrate its entries
        // before changing the requested fields so unrelated quality settings survive.
        if (existingPerf is null)
        {
            foreach (JsonObject grade in grades.OfType<JsonObject>())
                UpdateEntry(items, "entryType", grade["key"]!.GetValue<int>(), "index",
                    grade["value"]!.GetValue<int>() - 1, version);
            perf["portedVersion"] = version;
            perf["truePortedFromGraphicData"] = true;
        }
        if (existingGraphics is null)
        {
            foreach (JsonObject item in items.OfType<JsonObject>())
            {
                int key = item["entryType"]!.GetValue<int>();
                if (key != 18)
                    UpdateEntry(grades, "key", key, "value", item["index"]!.GetValue<int>() + 1);
            }
            graphics["volatileVersion"] = version;
        }

        if (renderScaleIndex is { } render)
            SetIndex(2, RenderScaleIndices[render]);
        if (antiAliasingIndex is { } antialiasing)
            SetIndex(8, AntiAliasingIndices[antialiasing]);
        graphics["currentVolatielGrade"] = -1;
        foreach (JsonObject item in items.OfType<JsonObject>().Where(x => x["entryType"]?.GetValue<int>() == 18))
            item["index"] = -2; // Legacy custom grade (-1) uses grade - 1 in perf data.

        WriteObject(root, "graphicsData", graphics);
        WriteObject(root, "globalPerfData", perf);
        return Encoding.UTF8.GetBytes(root.ToJsonString() + "\0");

        void SetIndex(int key, int index)
        {
            UpdateEntry(grades, "key", key, "value", index + 1);
            UpdateEntry(items, "entryType", key, "index", index, version);
        }
    }

    private static JsonObject ParseRoot(byte[] data) =>
        JsonNode.Parse(Encoding.UTF8.GetString(data).TrimEnd('\0')) as JsonObject
        ?? throw new InvalidOperationException("原神图形配置格式无效。");

    private static JsonObject? ReadObject(JsonObject root, string name)
    {
        JsonNode? value = root[name];
        if (value is null)
            return null;
        if (value is JsonObject obj)
            return obj;
        if (value is JsonValue scalar && scalar.TryGetValue<string>(out string? json))
            return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject
                ?? throw new InvalidOperationException($"原神 {name} 格式无效。");
        throw new InvalidOperationException($"原神 {name} 格式无效。");
    }

    private static void WriteObject(JsonObject root, string name, JsonObject value)
    {
        if (root[name] is JsonObject)
            root[name] = value;
        else
            root[name] = value.ToJsonString();
    }

    private static JsonArray GetArray(JsonObject obj, string name)
    {
        if (obj[name] is JsonArray array)
            return array;
        if (obj[name] is not null)
            throw new InvalidOperationException($"原神 {name} 格式无效。");
        var created = new JsonArray();
        obj[name] = created;
        return created;
    }

    private static int? ReadIndex(JsonObject? graphics, JsonObject? perf, int key)
    {
        JsonObject? item = (perf?["saveItems"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(x => x["entryType"]?.GetValue<int>() == key);
        if (item?["index"] is { } index)
            return index.GetValue<int>();
        JsonObject? grade = (graphics?["customVolatileGrades"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(x => x["key"]?.GetValue<int>() == key);
        return grade?["value"]?.GetValue<int>() - 1;
    }

    private static void UpdateEntry(JsonArray entries, string keyName, int key, string valueName, int value,
        string? version = null)
    {
        JsonObject[] matching = entries.OfType<JsonObject>()
            .Where(x => x[keyName]?.GetValue<int>() == key).ToArray();
        if (matching.Length == 0)
        {
            var entry = new JsonObject { [keyName] = key };
            if (version is not null)
                entry["itemVersion"] = version;
            entries.Add(entry);
            matching = [entry];
        }
        foreach (JsonObject entry in matching)
            entry[valueName] = value;
    }
}
