using System.Text;
using System.Text.Json.Nodes;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class GenshinGraphicsSettingsTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string label)
        {
            if (!condition)
                throw new InvalidOperationException(label);
            passed++;
        }

        JsonObject Parse(byte[] data) => JsonNode.Parse(Encoding.UTF8.GetString(data).TrimEnd('\0'))!.AsObject();
        byte[] Encode(JsonObject root) => Encoding.UTF8.GetBytes(root.ToJsonString() + "\0");
        JsonObject Nested(JsonObject root, string name) => root[name] is JsonObject obj
            ? obj : JsonNode.Parse(root[name]!.GetValue<string>())!.AsObject();
        JsonObject Entry(JsonObject obj, string array, string key, int value) =>
            obj[array]!.AsArray().OfType<JsonObject>().Single(x => x[key]!.GetValue<int>() == value);
        void Reject(Action action, string label)
        {
            bool rejected = false;
            try { action(); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or ArgumentOutOfRangeException)
            { rejected = true; }
            Check(rejected, label);
        }

        foreach (bool stringFormat in new[] { true, false })
        {
            var graphics = JsonNode.Parse("""
                {"currentVolatielGrade":-1,"volatileVersion":"fixture-v1","unknownGraphics":true,
                 "customVolatileGrades":[{"key":2,"value":3},{"key":8,"value":2},{"key":12,"value":2,"extra":"keep"}]}
                """)!.AsObject();
            var perf = JsonNode.Parse("""
                {"portedVersion":"fixture-v1","unknownPerf":42,
                 "saveItems":[{"entryType":2,"index":2,"itemVersion":"fixture-v1"},
                              {"entryType":8,"index":1,"itemVersion":"fixture-v1"},
                              {"entryType":12,"index":1,"itemVersion":"original","extra":true}]}
                """)!.AsObject();
            var root = new JsonObject { ["maxLuminosity"] = 1234, ["unrelated"] = new JsonObject { ["keep"] = "yes" } };
            if (stringFormat)
            {
                root["graphicsData"] = graphics.ToJsonString();
                root["globalPerfData"] = perf.ToJsonString();
            }
            else
            {
                root["graphicsData"] = graphics;
                root["globalPerfData"] = perf;
            }
            byte[] original = Encode(root);
            Check(GenshinGraphicsSettings.Read(original) == (3, 0), "Read native 1.0 and FSR");
            Check(ReferenceEquals(original, GenshinGraphicsSettings.Apply(original, null, null)), "No change keeps original bytes");

            int[] expectedScaleIndices = [0, 1, 8, 2, 3, 4, 5, 6, 7];
            int[] expectedAaIndices = [1, 2, 0];
            for (int scale = 0; scale < expectedScaleIndices.Length; scale++)
            for (int aa = 0; aa < expectedAaIndices.Length; aa++)
            {
                byte[] updated = GenshinGraphicsSettings.Apply(original, scale, aa);
                JsonObject result = Parse(updated);
                JsonObject resultGraphics = Nested(result, "graphicsData");
                JsonObject resultPerf = Nested(result, "globalPerfData");
                Check(GenshinGraphicsSettings.Read(updated) == (scale, aa), "Round trip requested selections");
                Check(Entry(resultGraphics, "customVolatileGrades", "key", 2)["value"]!.GetValue<int>() == expectedScaleIndices[scale] + 1
                    && Entry(resultPerf, "saveItems", "entryType", 2)["index"]!.GetValue<int>() == expectedScaleIndices[scale], "Native scale mapping");
                Check(Entry(resultGraphics, "customVolatileGrades", "key", 8)["value"]!.GetValue<int>() == expectedAaIndices[aa] + 1
                    && Entry(resultPerf, "saveItems", "entryType", 8)["index"]!.GetValue<int>() == expectedAaIndices[aa], "AA mapping in both records");
                Check(JsonNode.DeepEquals(Entry(resultGraphics, "customVolatileGrades", "key", 12), Entry(graphics, "customVolatileGrades", "key", 12))
                    && JsonNode.DeepEquals(Entry(resultPerf, "saveItems", "entryType", 12), Entry(perf, "saveItems", "entryType", 12)), "Preserve unrelated quality and metadata");
                Check(result["maxLuminosity"]!.GetValue<int>() == 1234 && result["unrelated"]!["keep"]!.GetValue<string>() == "yes"
                    && resultGraphics["unknownGraphics"]!.GetValue<bool>() && resultPerf["unknownPerf"]!.GetValue<int>() == 42, "Preserve unknown fields and HDR");
                Check((result["graphicsData"] is JsonObject) == !stringFormat
                    && (result["globalPerfData"] is JsonObject) == !stringFormat && updated[^1] == 0, "Preserve representation and trailing NUL");
            }

            Check(GenshinGraphicsSettings.Read(GenshinGraphicsSettings.Apply(original, null, 1)) == (3, 1), "AA-only change keeps render scale");
            root.Remove("globalPerfData");
            JsonObject migrated = Parse(GenshinGraphicsSettings.Apply(Encode(root), null, 2));
            Check(Entry(Nested(migrated, "globalPerfData"), "saveItems", "entryType", 12)["index"]!.GetValue<int>() == 1,
                "Migrate legacy graphics without resetting other settings");
            Check(GenshinGraphicsSettings.Read(Encode(migrated)) == (3, 2), "Legacy migration selects Off");

            var perfOnly = new JsonObject { ["globalPerfData"] = perf.ToJsonString(), ["keep"] = "yes" };
            JsonObject migratedPerf = Parse(GenshinGraphicsSettings.Apply(Encode(perfOnly), 2, null));
            Check(Entry(Nested(migratedPerf, "graphicsData"), "customVolatileGrades", "key", 12)["value"]!.GetValue<int>() == 2,
                "Migrate perf-only representation");
            Check(GenshinGraphicsSettings.Read(Encode(migratedPerf)) == (2, 0), "Perf-only migration keeps AA");
            Reject(() => GenshinGraphicsSettings.Apply(original, 9, null), "Reject invalid native scale");
            Reject(() => GenshinGraphicsSettings.Apply(original, null, 3), "Reject invalid AA");
        }

        Check(GenshinGraphicsSettings.Read(null) == (-1, -1), "Missing config has no invented selection");
        Reject(() => GenshinGraphicsSettings.Apply(Encoding.UTF8.GetBytes("{}"), 0, 0), "Missing graphics cannot be overwritten");
        Reject(() => GenshinGraphicsSettings.Apply(Encoding.UTF8.GetBytes("{broken"), 0, 0), "Malformed JSON is rejected");
        Reject(() => GenshinGraphicsSettings.Apply(Encoding.UTF8.GetBytes("{\"graphicsData\":\"broken\"}"), 0, 0), "Malformed nested JSON is rejected");
        var unknown = Encoding.UTF8.GetBytes("{\"globalPerfData\":{\"saveItems\":[{\"entryType\":8,\"index\":77}]}}");
        Check(GenshinGraphicsSettings.Read(unknown) == (-1, -1), "Unknown game values remain unselected");
        var emptyPerf = Encoding.UTF8.GetBytes("{\"graphicsData\":{\"customVolatileGrades\":[{\"key\":8,\"value\":2}]},\"globalPerfData\":\"\"}");
        Check(GenshinGraphicsSettings.Read(emptyPerf) == (-1, 0), "Empty perf representation falls back to graphics");
        Check(GenshinGraphicsSettings.Read(GenshinGraphicsSettings.Apply(emptyPerf, null, 1)) == (-1, 1), "Empty perf can be migrated");

        string temp = Path.Combine(Path.GetTempPath(), "hysx-genshin-graphics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            for (int index = 0; index < 9; index++)
            {
                Check(OptiScalerRuntime.EnsureFsrBridgeRenderScale(temp, OptiScalerRuntime.FsrBridgeRenderScales[index]), "Write bridge cache");
                Check(File.ReadAllText(Path.Combine(temp, "Dx11FsrBridge.render-scale.cache"))
                    == $"version 1{Environment.NewLine}index {index}{Environment.NewLine}", "Bridge cache selects requested scale");
            }
        }
        finally { Directory.Delete(temp, true); }
        Check(OptiScalerRuntime.FsrBridgeRenderScales.Contains(0.999f), "Bridge render scale exposes 0.999");
        Console.WriteLine($"Genshin graphics settings: {passed} passed, 0 failed.");
        return 0;
    }

    public static int ValidateLocalSnapshot()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException();
        byte[] snapshot = Microsoft.Win32.Registry.GetValue("HKEY_CURRENT_USER\\Software\\miHoYo\\原神",
            "GENERAL_DATA_h2389025596", null) as byte[]
            ?? throw new InvalidOperationException("No local Genshin graphics snapshot.");
        byte[] original = (byte[])snapshot.Clone();
        JsonObject Unrelated(byte[] bytes)
        {
            JsonObject root = JsonNode.Parse(Encoding.UTF8.GetString(bytes).TrimEnd('\0'))!.AsObject();
            foreach (string name in new[] { "graphicsData", "globalPerfData" })
            {
                JsonObject obj = root[name] is JsonObject value ? value : JsonNode.Parse(root[name]!.GetValue<string>())!.AsObject();
                string arrayName = name == "graphicsData" ? "customVolatileGrades" : "saveItems";
                string keyName = name == "graphicsData" ? "key" : "entryType";
                JsonArray entries = obj[arrayName]!.AsArray();
                foreach (JsonNode? entry in entries.ToArray())
                    if (entry?[keyName]?.GetValue<int>() is 2 or 8 or 18)
                        entries.Remove(entry);
                obj.Remove("currentVolatielGrade");
                root[name] = obj.ToJsonString();
            }
            return root;
        }
        JsonObject expected = Unrelated(snapshot);
        int passed = 0;
        for (int scale = 0; scale < 9; scale++)
        for (int aa = 0; aa < 3; aa++)
        {
            byte[] updated = GenshinGraphicsSettings.Apply(snapshot, scale, aa);
            if (GenshinGraphicsSettings.Read(updated) != (scale, aa)
                || !JsonNode.DeepEquals(expected, Unrelated(updated)) || !snapshot.SequenceEqual(original))
                throw new InvalidOperationException("Local snapshot did not preserve unrelated settings.");
            passed++;
        }
        Console.WriteLine($"Local Genshin snapshot: {passed} in-memory combinations passed; registry was only read.");
        return 0;
    }
}
