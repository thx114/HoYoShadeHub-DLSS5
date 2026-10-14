using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.OptiScaler;

namespace HoYoShadeHub
{
    // Only the configuration store is simulated; the linked preset code and
    // profile transactions operate on real files in an isolated directory.
    internal static class AppConfig
    {
        public static string OptiScalerRootPath { get; set; } = string.Empty;
        public static Dictionary<string, string> Values { get; } = new();
        public static string GetValue(string fallback, string key) => Values.GetValueOrDefault(key, fallback);
    }
}

namespace HoYoShadeHub.Extensions.Tests
{
    internal static class OptiScalerPresetSelectionTests
    {
        public static int Run()
        {
            string root = Path.Combine(Path.GetTempPath(), "hys-preset-selection-" + Guid.NewGuid().ToString("N"));
            string build = Path.Combine(root, "official", "candidate");
            string oldRoot = AppConfig.OptiScalerRootPath;
            var oldValues = new Dictionary<string, string>(AppConfig.Values);
            const string game = "nap_cn";
            const string original = "[NR]\nQuality=2\nScale=0.67\nWidth=1920\nHeight=1080\n";
            const string missing = "40-removed-zzz-x6";
            int passed = 0;
            void Check(bool condition, string message)
            {
                if (!condition) throw new InvalidOperationException(message);
                passed++;
            }
            try
            {
                AppConfig.OptiScalerRootPath = root;
                AppConfig.Values.Clear();
                Directory.CreateDirectory(build);
                Directory.CreateDirectory(OptiScalerProfiles.ProfileDirectory(build));
                Directory.CreateDirectory(OptiScalerPresets.Root);
                string main = Path.Combine(build, OptiScalerRuntime.ConfigFileName);
                string profile = OptiScalerProfiles.ProfilePath(build, game);
                File.WriteAllText(main, original);
                File.WriteAllText(profile, original);
                File.WriteAllText(OptiScalerPresets.PathOf("valid"), original.Replace("Quality=2", "Quality=3"));
                string key = OptiScalerPresets.PresetKey(game, "official/candidate");
                string follow = OptiScalerPresets.FollowKey(game);

                Check(OptiScalerPresets.SelectedPreset(game, build) == "", "no selection uses current profile");
                AppConfig.Values[follow] = missing;
                Check(OptiScalerPresets.SelectedPreset(game, build) == "", "missing follow preset ignored");
                var result = OptiScalerPresets.SyncIntoProfileChecked(build, game);
                Check(result.Succeeded && result.PresetName is null, "stale follow cannot block ZZZ launch preparation");
                Check(File.ReadAllText(profile) == original && File.ReadAllText(main) == original,
                    "stale selection preserves profile and runtime NR settings");
                Check(AppConfig.Values[follow] == missing, "stale database selection preserved");
                AppConfig.Values[follow] = "valid";
                Check(OptiScalerPresets.SelectedPreset(game, build) == "valid", "existing follow preset retained");
                AppConfig.Values[key] = missing;
                Check(OptiScalerPresets.SelectedPreset(game, build) == "", "missing build selection does not apply another preset");
                AppConfig.Values[key] = OptiScalerPresets.CurrentLabel;
                Check(OptiScalerPresets.SelectedPreset(game, build) == "", "explicit current selection overrides follow");
                AppConfig.Values[key] = " valid ";
                Check(OptiScalerPresets.SelectedPreset(game, build) == "valid", "existing build selection trimmed and retained");
                result = OptiScalerPresets.SyncIntoProfileChecked(build, game);
                Check(result.Succeeded && result.PresetName == "valid"
                    && File.ReadAllText(profile).Contains("Quality=3"), "valid preset still applied");

                AppConfig.Values[key] = missing;
                AppConfig.Values[follow] = missing;
                Check(OptiScalerPresets.SyncIntoProfileChecked(build, game).Succeeded, "deleted selection after activation accepted");
                string pending = Path.Combine(build, ".hub-preset-sync-pending");
                File.WriteAllText(pending, game);
                Check(OptiScalerPresets.CollectFromRuntimeChecked(build).Succeeded && !File.Exists(pending)
                    && !File.Exists(OptiScalerPresets.PathOf(missing)), "missing pending preset does not get recreated");

                File.WriteAllText(main, original.Replace("Scale=0.67", "Scale=0.75"));
                File.WriteAllText(profile, original.Replace("Quality=2", "Quality=4"));
                byte[] mainBefore = File.ReadAllBytes(main), profileBefore = File.ReadAllBytes(profile);
                result = OptiScalerPresets.SyncIntoProfileChecked(build, game);
                Check(!result.Succeeded && result.FailureReason?.Contains("冲突") == true,
                    "stale preset cannot bypass a real concurrent profile conflict");
                Check(File.ReadAllBytes(main).SequenceEqual(mainBefore)
                    && File.ReadAllBytes(profile).SequenceEqual(profileBefore), "real conflict preserves both edits");

                File.WriteAllText(main, original.Replace("Quality=2", "Quality=3"));
                File.WriteAllText(profile, File.ReadAllText(main));
                AppConfig.Values[key] = "valid";
                using (File.Open(OptiScalerPresets.PathOf("valid"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    result = OptiScalerPresets.SyncIntoProfileChecked(build, game);
                    Check(!result.Succeeded && result.FailureReason?.Contains("valid") == true,
                        "existing unreadable preset still reports failure with preset name");
                }
                Check(OptiScalerPresets.SelectedPreset(" ", build) == "", "empty game key rejected");
                Console.WriteLine($"Preset selection: PASS {passed} / FAIL 0; GameTested=false.");
                return passed;
            }
            finally
            {
                AppConfig.OptiScalerRootPath = oldRoot;
                AppConfig.Values.Clear();
                foreach (var item in oldValues) AppConfig.Values.Add(item.Key, item.Value);
                if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
            }
        }
    }
}
