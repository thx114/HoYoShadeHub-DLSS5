using HoYoShadeHub.Core.Settings;
using System.Threading;

namespace HoYoShadeHub.Extensions.Tests;

internal static class SettingPersistenceTests
{
    public static (int Passed, int Failed) Run()
    {
        int passed = 0, failed = 0;
        void Check(bool condition, string name)
        {
            Console.WriteLine($"  [{(condition ? "PASS" : "FAIL")}] {name}");
            if (condition) passed++; else failed++;
        }

        var gate = new Lock();
        var cache = new Dictionary<string, string?> { ["quality"] = "old" };
        bool saved = SettingPersistence.TryWrite(gate, cache, "quality", "new",
            () => throw new IOException("Simulated write failure"), out var error);
        Check(!saved && error is IOException && cache["quality"] == "old",
            "Failed persistence retains the confirmed cache value");
        int attempts = 0;
        saved = SettingPersistence.TryWrite(gate, cache, "quality", "new", () => attempts++, out error);
        Check(saved && error is null && attempts == 1 && cache["quality"] == "new",
            "Retry of a failed value reaches persistence");
        SettingPersistence.TryWrite(gate, cache, "quality", "new", () => attempts++, out _);
        Check(attempts == 1, "Confirmed identical values avoid another write");

        string? persisted = null;
        int active = 0, maximum = 0;
        Parallel.For(0, 32, index =>
        {
            string value = index.ToString();
            SettingPersistence.TryWrite(gate, cache, "concurrent", value, () =>
            {
                int count = Interlocked.Increment(ref active);
                maximum = Math.Max(maximum, count);
                Thread.Yield();
                persisted = value;
                Interlocked.Decrement(ref active);
            }, out _);
        });
        Check(maximum == 1 && cache["concurrent"] == persisted,
            "Concurrent writes serialize persistence and cache publication");

        string directory = Path.Combine(Path.GetTempPath(), "hoyoshade-settings-test-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "config.ini");
        try
        {
            SettingPersistence.WriteConfiguration(path, "old");
            SettingPersistence.WriteConfiguration(path, "new");
            Check(File.ReadAllText(path) == "new" && Directory.GetFiles(directory).Length == 1,
                "Atomic configuration replaces existing content and removes staging files");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool rejected = false;
                try { SettingPersistence.WriteConfiguration(path, "blocked"); }
                catch (IOException) { rejected = true; }
                Check(rejected, "Locked configuration reports the failed replacement");
            }
            Check(File.ReadAllText(path) == "new" && Directory.GetFiles(directory).Length == 1,
                "Failed replacement preserves original bytes and removes staging files");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
        return (passed, failed);
    }
}
