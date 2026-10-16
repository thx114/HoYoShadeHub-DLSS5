using System.Text;
using System.Text.Json;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Services;

public static partial class OptiScalerProfiles
{
    public const string PresetSyncPendingFileName = ".hub-preset-sync-pending";
    public readonly record struct LaunchRecoveryResult(
        bool Succeeded, bool Recovered = false, string? BackupDirectory = null, string? FailureReason = null,
        string? Owner = null);

    // Activation may recover a blocked state; ordinary editor reads and collection stay strict.
    public static LaunchRecoveryResult RecoverForLaunch(string buildDirectory,
        Func<string, string, bool>? archivePresetBeforeCommit = null)
    {
        lock (Gate)
        {
            string? backup = null;
            try
            {
                var collection = CollectRuntimeChangesChecked(buildDirectory);
                if (collection.Succeeded) return new(true);
                if (collection.Status != CollectionStatus.Blocked)
                    return new(false, FailureReason: "Configuration files could not be read or saved.");

                string mainPath = Path.Combine(buildDirectory, OptiScalerRuntime.ConfigFileName);
                string baselinePath = Path.Combine(buildDirectory, RuntimeBaselineFileName);
                string ownerPath = Path.Combine(buildDirectory, RuntimeOwnerFileName);
                string transactionPath = Path.Combine(buildDirectory, RuntimeTransactionFileName);
                string presetPendingPath = Path.Combine(buildDirectory, PresetSyncPendingFileName);
                string? owner = RuntimeOwner(buildDirectory);
                string? transactionOwner = File.Exists(transactionPath)
                    ? File.ReadAllText(transactionPath).Trim() : null;
                string? recoveryOwner = !string.IsNullOrWhiteSpace(transactionOwner) ? transactionOwner : owner;
                if (string.IsNullOrWhiteSpace(recoveryOwner) || recoveryOwner != Sanitize(recoveryOwner)
                    || recoveryOwner is "." or "..")
                    return new(false, FailureReason: "The runtime configuration owner is unavailable or invalid.");

                string profilePath = ProfilePath(buildDirectory, recoveryOwner);
                string[] paths = [mainPath, baselinePath, ownerPath, transactionPath, profilePath, presetPendingPath];
                if (!string.IsNullOrWhiteSpace(owner)) paths = [.. paths, ProfilePath(buildDirectory, owner)];
                var snapshot = paths.Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(p => p, p => File.Exists(p) ? File.ReadAllBytes(p) : null,
                        StringComparer.OrdinalIgnoreCase);
                byte[]? runtime = snapshot[mainPath];
                byte[]? saved = snapshot[profilePath];
                if (runtime is null && saved is null)
                    return new(false, FailureReason: "Both runtime and game configuration files are missing.");

                // During an interrupted cross-game activation the live file's owner is ambiguous.
                // Complete from the target's known profile, never copy that live file to either game.
                bool interrupted = snapshot[transactionPath] is not null;
                if (interrupted && saved is null && !string.Equals(owner, recoveryOwner, StringComparison.Ordinal))
                    return new(false, FailureReason: "The interrupted activation has no known target profile.");
                byte[] recovered = interrupted && saved is not null ? saved : runtime ?? saved!;
                if (!interrupted && runtime is not null && saved is not null
                    && snapshot[baselinePath] is { } baseline)
                    recovered = MergeProfileChanges(runtime, saved, baseline);

                backup = ArchiveLaunchSnapshot(buildDirectory, snapshot, recoveryOwner,
                    interrupted ? "interrupted-activation-use-target-profile" : "merge-with-runtime-precedence");
                bool syncPreset = archivePresetBeforeCommit?.Invoke(recoveryOwner, backup) == true;
                if (!SnapshotMatches(snapshot))
                    return new(false, BackupDirectory: backup,
                        FailureReason: "Configuration changed while recovery was being prepared.");

                // A crash after recovery must collect the recovered profile before replaying a preset.
                if (syncPreset) AtomicWrite(presetPendingPath, Encoding.UTF8.GetBytes(recoveryOwner));
                if (!CommitActivation(buildDirectory, recoveryOwner, recovered, updateProfile: true))
                {
                    // CommitActivation may have replaced an existing transaction marker before failing.
                    foreach (var pair in snapshot.Reverse())
                    {
                        try
                        {
                            if (pair.Value is { } bytes) AtomicWrite(pair.Key, bytes);
                            else if (File.Exists(pair.Key)) File.Delete(pair.Key);
                        }
                        catch { }
                    }
                    if (!SnapshotMatches(snapshot) && !File.Exists(transactionPath))
                        AtomicWrite(transactionPath, Encoding.UTF8.GetBytes(recoveryOwner));
                    return new(false, BackupDirectory: backup, FailureReason: "Configuration recovery could not be committed.");
                }
                return new(true, true, backup, Owner: recoveryOwner);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return new(false, BackupDirectory: backup, FailureReason: "Configuration recovery failed: " + ex.Message);
            }
        }
    }

    private static bool SnapshotMatches(Dictionary<string, byte[]?> snapshot) => snapshot.All(pair =>
        pair.Value is { } bytes
            ? File.Exists(pair.Key) && File.ReadAllBytes(pair.Key).AsSpan().SequenceEqual(bytes)
            : !File.Exists(pair.Key));

    private static string ArchiveLaunchSnapshot(string directory, Dictionary<string, byte[]?> snapshot,
        string owner, string policy)
    {
        string backup = Path.Combine(directory, "profile-recovery",
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(backup);
        foreach (var pair in snapshot)
        {
            if (pair.Value is not { } bytes) continue;
            string target = Path.Combine(backup, Path.GetRelativePath(directory, pair.Key));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using (var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (!File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes))
                throw new IOException("Configuration backup verification failed.");
        }
        File.WriteAllText(Path.Combine(backup, "recovery.json"), JsonSerializer.Serialize(new
        {
            owner, policy,
            files = snapshot.Select(pair => new { path = Path.GetRelativePath(directory, pair.Key), existed = pair.Value is not null })
        }, new JsonSerializerOptions { WriteIndented = true }));
        return backup;
    }

    private static byte[] MergeProfileChanges(byte[] runtime, byte[] saved, byte[] baseline)
    {
        var live = IniDocument.LoadFromText(DecodeIni(runtime));
        var profile = IniDocument.LoadFromText(DecodeIni(saved));
        var original = IniDocument.LoadFromText(DecodeIni(baseline));
        bool changed = false;
        foreach (string section in profile.GetSections().Concat(original.GetSections()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (string key in profile.GetSectionKeys(section).Concat(original.GetSectionKeys(section))
                         .Where(key => !key.StartsWith(';') && !key.StartsWith('#')).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                string? before = original.GetValue(section, key);
                string? current = live.GetValue(section, key);
                string? edited = profile.GetValue(section, key);
                if (string.Equals(edited?.Trim(), before?.Trim(), StringComparison.Ordinal)
                    || !string.Equals(current?.Trim(), before?.Trim(), StringComparison.Ordinal)) continue;
                if (edited is null) live.RemoveKey(section, key);
                else live.SetValue(section, key, edited);
                changed = true;
            }
        }
        if (!changed) return runtime;
        string text = string.Join("\r\n", live.Lines).TrimEnd('\r', '\n') + "\r\n";
        var encoding = new UTF8Encoding(runtime.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        return [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
    }

    private static string DecodeIni(byte[] bytes)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}
