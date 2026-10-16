using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.OptiScaler;

namespace HoYoShadeHub.Extensions.Tests;

internal static class OptiScalerLaunchRecoveryTests
{
    private const string Game = "hkrpg_bilibili";
    private const string OtherGame = "hk4e_cn";
    private const string Original = "[NR]\nQuality=2\nScale=0.67\nWidth=1920\nHeight=1080\n[Libraries]\nOptiDllPath=auto\n";

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "hys-launch-recovery-" + Guid.NewGuid().ToString("N"));
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
        }
        Fixture Case(string name) => new(Path.Combine(root, name));
        void Backups(string directory, IReadOnlyDictionary<string, byte[]> expected, string label)
        {
            Check(Directory.Exists(directory), label + " backup directory exists");
            foreach (var entry in expected)
            {
                string archived = Path.Combine(directory, entry.Key);
                Check(File.Exists(archived) && File.ReadAllBytes(archived).SequenceEqual(entry.Value),
                    label + " preserves original bytes: " + entry.Key);
            }
        }
        void Synchronized(Fixture fixture, string game, string label)
        {
            byte[] main = File.ReadAllBytes(fixture.Main);
            Check(main.SequenceEqual(File.ReadAllBytes(fixture.Profile(game)))
                && main.SequenceEqual(File.ReadAllBytes(fixture.Baseline)), label + " synchronizes runtime/profile/baseline");
            Check(OptiScalerProfiles.RuntimeOwner(fixture.Root) == game && !File.Exists(fixture.Transaction),
                label + " commits owner and removes transaction");
        }
        try
        {
            var normal = Case("normal");
            var result = OptiScalerProfiles.RecoverForLaunch(normal.Root);
            Check(result.Succeeded && !result.Recovered && result.BackupDirectory is null,
                "healthy state needs no recovery");
            Check(!Directory.Exists(normal.ArchiveRoot), "healthy state creates no backup");

            var legacy = Case("legacy");
            File.Delete(legacy.Baseline);
            string legacyRuntime = Original.Replace("Scale=0.67", "Scale=0.75");
            File.WriteAllText(legacy.Main, legacyRuntime);
            var legacyBefore = legacy.Snapshot();
            Check(OptiScalerProfiles.CollectRuntimeChangesChecked(legacy.Root).Status
                == OptiScalerProfiles.CollectionStatus.Blocked, "generic legacy collection stays strict");
            result = OptiScalerProfiles.RecoverForLaunch(legacy.Root);
            Check(result.Succeeded && result.Recovered && result.BackupDirectory is not null,
                "differing legacy state recovers for launch");
            Backups(result.BackupDirectory!, legacyBefore, "legacy");
            Check(File.ReadAllText(legacy.Main) == legacyRuntime, "legacy recovery keeps runtime settings");
            Synchronized(legacy, Game, "legacy");
            int legacyArchives = Directory.GetDirectories(legacy.ArchiveRoot).Length;
            result = OptiScalerProfiles.RecoverForLaunch(legacy.Root);
            Check(result.Succeeded && !result.Recovered
                && Directory.GetDirectories(legacy.ArchiveRoot).Length == legacyArchives,
                "repeated recovery creates no further backup");

            var independent = Case("independent");
            string runtimePath = Path.Combine(independent.Root, "OptiScaler");
            File.WriteAllText(independent.Main, Original.Replace("OptiDllPath=auto", "OptiDllPath=" + runtimePath));
            File.WriteAllText(independent.Profile(Game), Original.Replace("Quality=2", "Quality=4")
                .Replace("Scale=0.67", "Scale=0.75"));
            var independentBefore = independent.Snapshot();
            Check(OptiScalerProfiles.CollectRuntimeChangesChecked(independent.Root).Status
                == OptiScalerProfiles.CollectionStatus.Blocked, "generic divergent collection stays strict");
            result = OptiScalerProfiles.RecoverForLaunch(independent.Root);
            Check(result.Succeeded && result.Recovered, "independent key edits recover");
            Backups(result.BackupDirectory!, independentBefore, "independent");
            string merged = File.ReadAllText(independent.Main);
            Check(IniValue(merged, "Libraries", "OptiDllPath") == runtimePath
                && IniValue(merged, "NR", "Quality") == "4" && IniValue(merged, "NR", "Scale") == "0.75"
                && IniValue(merged, "NR", "Width") == "1920", "merge retains machine path and profile NR settings");
            Synchronized(independent, Game, "independent");

            var collision = Case("collision");
            File.WriteAllText(collision.Main, Original.Replace("Quality=2", "Quality=3"));
            File.WriteAllText(collision.Profile(Game), Original.Replace("Quality=2", "Quality=4"));
            var collisionBefore = collision.Snapshot();
            result = OptiScalerProfiles.RecoverForLaunch(collision.Root);
            Check(result.Succeeded && IniValue(File.ReadAllText(collision.Main), "NR", "Quality") == "3",
                "runtime wins same-key collision");
            Backups(result.BackupDirectory!, collisionBefore, "collision");
            Synchronized(collision, Game, "collision");

            var deletion = Case("deletion");
            File.WriteAllText(deletion.Main, Original.Replace("Scale=0.67", "Scale=0.75"));
            File.WriteAllText(deletion.Profile(Game), Original.Replace("Width=1920\n", string.Empty));
            result = OptiScalerProfiles.RecoverForLaunch(deletion.Root);
            merged = File.ReadAllText(deletion.Main);
            Check(result.Succeeded && IniValue(merged, "NR", "Width") is null
                && IniValue(merged, "NR", "Scale") == "0.75", "profile-only key deletion survives merge");
            Synchronized(deletion, Game, "deletion");

            var bom = Case("bom-comments");
            string annotated = "; user custom header\n" + Original.Replace("Quality=2", "; retain NR note\nQuality=2");
            var utf8Bom = new System.Text.UTF8Encoding(true);
            File.WriteAllText(bom.Baseline, annotated, utf8Bom);
            File.WriteAllText(bom.Main, annotated.Replace("OptiDllPath=auto", "OptiDllPath=D:\\custom\\OptiScaler"), utf8Bom);
            File.WriteAllText(bom.Profile(Game), annotated.Replace("Quality=2", "Quality=4"), utf8Bom);
            var bomBefore = bom.Snapshot();
            result = OptiScalerProfiles.RecoverForLaunch(bom.Root);
            byte[] bomBytes = File.ReadAllBytes(bom.Main);
            merged = File.ReadAllText(bom.Main);
            Check(result.Succeeded && bomBytes.AsSpan().StartsWith(System.Text.Encoding.UTF8.Preamble),
                "merged runtime keeps UTF-8 BOM");
            Check(merged.Contains("; user custom header") && merged.Contains("; retain NR note")
                && IniValue(merged, "NR", "Quality") == "4"
                && IniValue(merged, "Libraries", "OptiDllPath") == "D:\\custom\\OptiScaler",
                "merge retains runtime comments and both independent edits");
            Backups(result.BackupDirectory!, bomBefore, "bom-comments");
            Synchronized(bom, Game, "bom-comments");

            var transaction = Case("transaction");
            File.WriteAllText(transaction.Transaction, Game);
            var transactionBefore = transaction.Snapshot();
            Check(OptiScalerProfiles.CollectRuntimeChangesChecked(transaction.Root).Status
                == OptiScalerProfiles.CollectionStatus.Blocked, "generic stale transaction stays strict");
            result = OptiScalerProfiles.RecoverForLaunch(transaction.Root);
            Check(result.Succeeded && result.Recovered, "consistent stale transaction recovers");
            Backups(result.BackupDirectory!, transactionBefore, "transaction");
            Synchronized(transaction, Game, "transaction");

            var sameOwner = Case("same-owner-partial-transaction");
            string targetProfile = Original.Replace("Quality=2", "Quality=4");
            File.WriteAllText(sameOwner.Profile(Game), targetProfile);
            File.WriteAllText(sameOwner.Main, Original.Replace("Quality=2", "Quality=9"));
            File.WriteAllText(sameOwner.Transaction, Game);
            var sameOwnerBefore = sameOwner.Snapshot();
            result = OptiScalerProfiles.RecoverForLaunch(sameOwner.Root);
            Check(result.Succeeded && result.Recovered && File.ReadAllText(sameOwner.Main) == targetProfile,
                "same-owner partial transaction completes from known target profile");
            Backups(result.BackupDirectory!, sameOwnerBefore, "same-owner-partial-transaction");
            Synchronized(sameOwner, Game, "same-owner-partial-transaction");

            var crossOwner = Case("cross-owner");
            string foreign = "[NR]\nQuality=6\nScale=0.5\nGameMarker=genshin\n";
            File.WriteAllText(crossOwner.Profile(OtherGame), foreign);
            File.WriteAllText(crossOwner.Main, Original.Replace("Quality=2", "Quality=5"));
            File.WriteAllText(crossOwner.Transaction, OtherGame);
            var crossOwnerBefore = crossOwner.Snapshot(OtherGame);
            result = OptiScalerProfiles.RecoverForLaunch(crossOwner.Root);
            Check(result.Succeeded && result.Recovered, "cross-owner partial transaction recovers");
            Backups(result.BackupDirectory!, crossOwnerBefore, "cross-owner");
            Check(File.ReadAllText(crossOwner.Main) == foreign
                && File.ReadAllText(crossOwner.Profile(Game)) == Original,
                "known transaction target wins without copying runtime into another game");
            Synchronized(crossOwner, OtherGame, "cross-owner");

            var missingProfile = Case("missing-profile");
            File.Delete(missingProfile.Profile(Game));
            File.WriteAllText(missingProfile.Main, legacyRuntime);
            var missingProfileBefore = missingProfile.Snapshot();
            result = OptiScalerProfiles.RecoverForLaunch(missingProfile.Root);
            Check(result.Succeeded && File.ReadAllText(missingProfile.Profile(Game)) == legacyRuntime,
                "missing owner profile recreated from its runtime");
            Backups(result.BackupDirectory!, missingProfileBefore, "missing-profile");
            Synchronized(missingProfile, Game, "missing-profile");

            var missingMain = Case("missing-main");
            File.Delete(missingMain.Main);
            var missingMainBefore = missingMain.Snapshot();
            result = OptiScalerProfiles.RecoverForLaunch(missingMain.Root);
            Check(result.Succeeded && File.ReadAllText(missingMain.Main) == Original,
                "missing runtime recreated from known owner profile");
            Backups(result.BackupDirectory!, missingMainBefore, "missing-main");
            Synchronized(missingMain, Game, "missing-main");

            var missingBoth = Case("missing-both");
            File.Delete(missingBoth.Main);
            File.Delete(missingBoth.Profile(Game));
            var missingBothBefore = missingBoth.Snapshot();
            result = OptiScalerProfiles.RecoverForLaunch(missingBoth.Root);
            Check(!result.Succeeded && !string.IsNullOrWhiteSpace(result.FailureReason),
                "missing runtime and profile reports failure");
            Check(missingBoth.Unchanged(missingBothBefore) && !File.Exists(missingBoth.Main)
                && !File.Exists(missingBoth.Profile(Game)), "missing sources are not invented");

            var unknownOwner = Case("unknown-owner");
            File.Delete(unknownOwner.Owner);
            File.WriteAllText(unknownOwner.Main, legacyRuntime);
            var unknownBefore = unknownOwner.Snapshot();
            result = OptiScalerProfiles.RecoverForLaunch(unknownOwner.Root);
            Check(result.Succeeded && !result.Recovered && unknownOwner.Unchanged(unknownBefore)
                && !File.Exists(unknownOwner.Owner), "unowned state does not guess a game");

            var blockedArchive = Case("blocked-archive");
            File.Delete(blockedArchive.Baseline);
            File.WriteAllText(blockedArchive.Main, legacyRuntime);
            File.WriteAllText(blockedArchive.ArchiveRoot, "occupied");
            var blockedBefore = blockedArchive.Snapshot();
            result = OptiScalerProfiles.RecoverForLaunch(blockedArchive.Root);
            Check(!result.Succeeded && blockedArchive.Unchanged(blockedBefore)
                && !File.Exists(blockedArchive.Baseline), "backup failure leaves original state untouched");

            var locked = Case("locked");
            File.Delete(locked.Baseline);
            File.WriteAllText(locked.Main, legacyRuntime);
            var lockedBefore = locked.Snapshot();
            using (File.Open(locked.Main, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                result = OptiScalerProfiles.RecoverForLaunch(locked.Root);
                Check(!result.Succeeded, "unreadable runtime blocks recovery");
            }
            Check(locked.Unchanged(lockedBefore) && !File.Exists(locked.Baseline),
                "read failure leaves original state untouched");

            TestSelectedPreset(root, Check);

            var rollback = Case("commit-rollback");
            File.Delete(rollback.Baseline);
            File.WriteAllText(rollback.Main, legacyRuntime);
            var rollbackBefore = rollback.Snapshot();
            using (File.Open(rollback.Owner, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                result = OptiScalerProfiles.RecoverForLaunch(rollback.Root);
                Check(!result.Succeeded && result.BackupDirectory is not null,
                    "locked owner fails commit after creating backups");
                Check(rollback.Unchanged(rollbackBefore) && !File.Exists(rollback.Baseline)
                    && !File.Exists(rollback.Transaction), "failed commit rolls back original files and missing markers");
                Backups(result.BackupDirectory!, rollbackBefore, "commit-rollback");
            }
            Check(rollback.Unchanged(rollbackBefore) && !File.Exists(rollback.Baseline)
                && !File.Exists(rollback.Transaction), "rollback remains intact after owner lock is released");

            var unreadableOwner = Case("unreadable-owner");
            File.Delete(unreadableOwner.Baseline);
            File.WriteAllText(unreadableOwner.Main, legacyRuntime);
            var unreadableOwnerBefore = unreadableOwner.Snapshot();
            using (File.Open(unreadableOwner.Owner, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                result = OptiScalerProfiles.RecoverForLaunch(unreadableOwner.Root);
                Check(!result.Succeeded, "unreadable existing owner cannot be treated as absent");
            }
            Check(unreadableOwner.Unchanged(unreadableOwnerBefore) && !File.Exists(unreadableOwner.Baseline)
                && !File.Exists(unreadableOwner.Transaction), "unreadable owner preserves original state");

            Console.WriteLine($"Launch profile recovery: PASS {passed} / FAIL 0; GameTested=false.");
            return passed;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static void TestSelectedPreset(string root, Action<bool, string> check)
    {
        string oldRoot = AppConfig.OptiScalerRootPath;
        var oldValues = new Dictionary<string, string>(AppConfig.Values);
        try
        {
            AppConfig.OptiScalerRootPath = Path.Combine(root, "preset-store");
            AppConfig.Values.Clear();
            var fixture = new Fixture(Path.Combine(AppConfig.OptiScalerRootPath, "official", "candidate"));
            File.WriteAllText(fixture.Main, Original.Replace("Scale=0.67", "Scale=0.75"));
            File.WriteAllText(fixture.Profile(Game), Original.Replace("Quality=2", "Quality=4"));
            Directory.CreateDirectory(OptiScalerPresets.Root);
            string preset = Original;
            File.WriteAllText(OptiScalerPresets.PathOf("selected"), preset);
            string selectedMeta = Path.Combine(OptiScalerPresets.Root, "selected.meta.json");
            const string presetMeta = "{\"sources\": [\"official\"]}";
            File.WriteAllText(selectedMeta, presetMeta);
            AppConfig.Values[OptiScalerPresets.PresetKey(Game, "official/candidate")] = "selected";
            var result = OptiScalerPresets.SyncIntoProfileForLaunchChecked(fixture.Root, Game);
            check(result.Succeeded && result.PresetName == "selected"
                && IniValue(File.ReadAllText(fixture.Main), "NR", "Quality") == "4"
                && IniValue(File.ReadAllText(fixture.Main), "NR", "Scale") == "0.75",
                "launch recovery retains merged settings after selected preset replay");
            string recoveredPreset = File.ReadAllText(OptiScalerPresets.PathOf("selected"));
            check(IniValue(recoveredPreset, "NR", "Quality") == "4"
                && IniValue(recoveredPreset, "NR", "Scale") == "0.75",
                "normal follow saves recovered settings into selected preset");
            string archivedPreset = Path.Combine(result.RecoveryDirectory!, OptiScalerPresets.FolderName, "selected.ini");
            string archivedMeta = Path.Combine(result.RecoveryDirectory!, OptiScalerPresets.FolderName, "selected.meta.json");
            check(File.ReadAllText(archivedPreset) == preset && File.ReadAllText(archivedMeta) == presetMeta,
                "selected preset and metadata originals are archived before follow");

            var previous = new Fixture(Path.Combine(AppConfig.OptiScalerRootPath, "official", "previous-owner"));
            File.WriteAllText(previous.Main, Original.Replace("Scale=0.67", "Scale=0.75"));
            File.WriteAllText(previous.Profile(Game), Original.Replace("Quality=2", "Quality=4"));
            string requestedPreset = Original.Replace("Quality=2", "Quality=6").Replace("Scale=0.67", "Scale=0.5");
            File.WriteAllText(previous.Profile(OtherGame), requestedPreset);
            File.WriteAllText(OptiScalerPresets.PathOf("previous"), Original);
            File.WriteAllText(OptiScalerPresets.PathOf("requested"), requestedPreset);
            AppConfig.Values[OptiScalerPresets.PresetKey(Game, "official/previous-owner")] = "previous";
            AppConfig.Values[OptiScalerPresets.PresetKey(OtherGame, "official/previous-owner")] = "requested";
            result = OptiScalerPresets.SyncIntoProfileForLaunchChecked(previous.Root, OtherGame);
            check(result.Succeeded && result.PresetName == "requested"
                && OptiScalerProfiles.RuntimeOwner(previous.Root) == OtherGame
                && File.ReadAllText(previous.Main) == requestedPreset,
                "requested game applies its own preset after previous owner recovery");
            recoveredPreset = File.ReadAllText(OptiScalerPresets.PathOf("previous"));
            check(IniValue(recoveredPreset, "NR", "Quality") == "4"
                && IniValue(recoveredPreset, "NR", "Scale") == "0.75"
                && File.ReadAllText(OptiScalerPresets.PathOf("requested")) == requestedPreset,
                "recovery follows previous owner without mutating requested game preset");
            check(File.ReadAllText(Path.Combine(result.RecoveryDirectory!, OptiScalerPresets.FolderName, "previous.ini")) == Original,
                "previous owner preset original is archived");

            var explicitApply = new Fixture(Path.Combine(AppConfig.OptiScalerRootPath, "official", "explicit-apply"));
            File.WriteAllText(explicitApply.Main, Original.Replace("Scale=0.67", "Scale=0.75"));
            File.WriteAllText(explicitApply.Profile(Game), Original.Replace("Quality=2", "Quality=4"));
            File.Delete(explicitApply.Baseline);
            File.WriteAllText(OptiScalerPresets.PathOf("explicit-old"), Original);
            AppConfig.Values[OptiScalerPresets.PresetKey(Game, "official/explicit-apply")] = "explicit-old";
            string incomingPreset = Original.Replace("Quality=2", "Quality=6");
            File.WriteAllText(OptiScalerPresets.PathOf("incoming"), incomingPreset);
            check(OptiScalerPresets.CollectFromRuntimeChecked(explicitApply.Root).Succeeded == false,
                "ordinary legacy collection remains strict before explicit application");
            check(OptiScalerPresets.Apply(explicitApply.Root, Game, "incoming"),
                "explicit incoming preset applies after recovering legacy conflict");
            check(File.ReadAllText(explicitApply.Main) == incomingPreset
                && File.ReadAllText(explicitApply.Profile(Game)) == incomingPreset
                && File.ReadAllText(explicitApply.Baseline) == incomingPreset
                && OptiScalerProfiles.RuntimeOwner(explicitApply.Root) == Game
                && !File.Exists(explicitApply.Transaction),
                "explicit application commits incoming quality6 and synchronizes owner/profile/baseline");
            check(File.ReadAllText(OptiScalerPresets.PathOf("incoming")) == incomingPreset,
                "recovery cannot replace explicit incoming preset with stale runtime settings");

            var sameName = new Fixture(Path.Combine(AppConfig.OptiScalerRootPath, "official", "same-name-conflict"));
            File.WriteAllText(sameName.Main, Original.Replace("Scale=0.67", "Scale=0.75"));
            File.WriteAllText(sameName.Profile(Game), Original.Replace("Quality=2", "Quality=4"));
            var sameNameBefore = sameName.Snapshot();
            File.WriteAllText(OptiScalerPresets.PathOf("selected"), incomingPreset);
            AppConfig.Values[OptiScalerPresets.PresetKey(Game, "official/same-name-conflict")] = "selected";
            check(OptiScalerPresets.Apply(sameName.Root, Game, "selected"),
                "explicit same-named selected preset applies during conflict recovery");
            result = OptiScalerPresets.SyncIntoProfileForLaunchChecked(sameName.Root, Game);
            check(result.Succeeded && result.PresetName == "selected"
                && File.ReadAllText(sameName.Main) == incomingPreset
                && File.ReadAllText(sameName.Profile(Game)) == incomingPreset
                && File.ReadAllText(OptiScalerPresets.PathOf("selected")) == incomingPreset,
                "same-named explicit preset stays quality6 on subsequent launch");
            string sameNameBackup = Directory.GetDirectories(sameName.ArchiveRoot).Single();
            check(sameNameBefore.All(pair => File.ReadAllBytes(Path.Combine(sameNameBackup, pair.Key)).SequenceEqual(pair.Value)),
                "same-name conflict recovery archives original runtime and profile bytes");

            var runtimeOnly = new Fixture(Path.Combine(AppConfig.OptiScalerRootPath, "official", "same-name-runtime-only"));
            File.WriteAllText(runtimeOnly.Main, Original.Replace("Scale=0.67", "Scale=0.75"));
            File.WriteAllText(OptiScalerPresets.PathOf("selected"), incomingPreset);
            AppConfig.Values[OptiScalerPresets.PresetKey(Game, "official/same-name-runtime-only")] = "selected";
            check(OptiScalerPresets.Apply(runtimeOnly.Root, Game, "selected"),
                "explicit selected preset applies over healthy runtime-only changes");
            result = OptiScalerPresets.SyncIntoProfileForLaunchChecked(runtimeOnly.Root, Game);
            check(result.Succeeded && result.PresetName == "selected"
                && File.ReadAllText(runtimeOnly.Main) == incomingPreset
                && File.ReadAllText(runtimeOnly.Profile(Game)) == incomingPreset
                && File.ReadAllText(OptiScalerPresets.PathOf("selected")) == incomingPreset,
                "runtime collection cannot overwrite explicit selected preset before repeat launch");
        }
        finally
        {
            AppConfig.OptiScalerRootPath = oldRoot;
            AppConfig.Values.Clear();
            foreach (var item in oldValues) AppConfig.Values.Add(item.Key, item.Value);
        }
    }

    private static string? IniValue(string text, string section, string key)
    {
        string current = string.Empty;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) current = line[1..^1];
            else if (current.Equals(section, StringComparison.OrdinalIgnoreCase)
                && !line.StartsWith(';') && !line.StartsWith('#'))
            {
                int equals = line.IndexOf('=');
                if (equals > 0 && line[..equals].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                    return line[(equals + 1)..].Trim();
            }
        }
        return null;
    }

    private sealed class Fixture
    {
        public string Root { get; }
        public string Main => Path.Combine(Root, OptiScalerRuntime.ConfigFileName);
        public string Baseline => Path.Combine(Root, OptiScalerProfiles.RuntimeBaselineFileName);
        public string Owner => Path.Combine(Root, OptiScalerProfiles.RuntimeOwnerFileName);
        public string Transaction => Path.Combine(Root, OptiScalerProfiles.RuntimeTransactionFileName);
        public string ArchiveRoot => Path.Combine(Root, "profile-recovery");
        public string Profile(string game) => OptiScalerProfiles.ProfilePath(Root, game);

        public Fixture(string root)
        {
            Root = root;
            Directory.CreateDirectory(root);
            File.WriteAllText(Main, Original);
            if (!OptiScalerProfiles.Activate(root, Game)) throw new InvalidOperationException("Fixture activation failed.");
        }

        public Dictionary<string, byte[]> Snapshot(string? extraGame = null)
        {
            string[] paths = [Main, Baseline, Owner, Transaction, Profile(Game)];
            if (extraGame is not null) paths = [.. paths, Profile(extraGame)];
            return paths.Where(File.Exists).ToDictionary(path => Path.GetRelativePath(Root, path), File.ReadAllBytes);
        }

        public bool Unchanged(IReadOnlyDictionary<string, byte[]> before) =>
            before.All(item => File.Exists(Path.Combine(Root, item.Key))
                && File.ReadAllBytes(Path.Combine(Root, item.Key)).SequenceEqual(item.Value));
    }
}
