using System.Security.Cryptography;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.ReShade;

public static class PackPendingMigration
{
    public static bool TryMigrate(string sourceRoot, string destinationRoot, out string? error)
    {
        string? stage = null;
        try
        {
            string source = Path.GetFullPath(sourceRoot).TrimEnd(Path.DirectorySeparatorChar);
            string destination = Path.GetFullPath(destinationRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (source.Equals(destination, StringComparison.OrdinalIgnoreCase)
                || source.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || destination.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Pending and destination directories overlap.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using var lease = new FileStream(destination + ".hysx-migration.lock", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            PackageTree.ValidateTree(source);
            if (Directory.Exists(destination)) PackageTree.ValidateTree(destination);
            bool existed = Directory.Exists(destination);
            Dictionary<string, string> original = existed ? Manifest(destination) : new(StringComparer.OrdinalIgnoreCase);
            string[] originalDirectories = existed ? Directories(destination) : [];
            Dictionary<string, string> before = Manifest(source);
            string[] directories = Directories(source);
            if (before.Keys.Any(IsLedgerName))
                throw new InvalidDataException("Pending content cannot replace the once ledger.");
            if (directories.Any(IsLedgerName))
                throw new InvalidDataException("Pending content cannot inject once ledger directories.");
            stage = PackageTree.CreateStage(destination);
            // Copy bytes rather than hardlink: staging edits must never affect the live destination.
            foreach (string directory in originalDirectories)
                Directory.CreateDirectory(PackageTree.ResolveRelative(stage, directory));
            foreach (var file in original)
                File.Copy(PackageTree.ResolveRelative(destination, file.Key), PackageTree.ResolveRelative(stage, file.Key));
            foreach (string directory in directories)
                Directory.CreateDirectory(PackageTree.ResolveRelative(stage, directory));
            foreach (var file in before)
            {
                string target = PackageTree.ResolveRelative(stage, file.Key);
                File.Copy(PackageTree.ResolveRelative(source, file.Key), target, overwrite: true);
                if (Hash(target) != file.Value) throw new IOException("Pending copy verification failed: " + file.Key);
            }
            Dictionary<string, string> after = Manifest(source);
            string[] afterDirectories = Directories(source);
            if (!directories.SequenceEqual(afterDirectories, StringComparer.OrdinalIgnoreCase)
                || before.Count != after.Count || before.Any(file => !after.TryGetValue(file.Key, out string? hash) || hash != file.Value))
                throw new IOException("Pending source changed during migration.");
            foreach (var file in before)
                if (Hash(PackageTree.ResolveRelative(stage, file.Key)) != file.Value)
                    throw new IOException("Pending destination changed during migration: " + file.Key);
            foreach (var file in original.Where(file => !before.ContainsKey(file.Key)))
                if (Hash(PackageTree.ResolveRelative(stage, file.Key)) != file.Value)
                    throw new IOException("Existing destination copy verification failed: " + file.Key);
            if (Directory.Exists(destination) != existed || (existed
                && (!Same(original, Manifest(destination))
                    || !originalDirectories.SequenceEqual(Directories(destination), StringComparer.OrdinalIgnoreCase))))
                throw new IOException("Destination changed during pending migration; commit blocked.");
            PackageTree.Commit(stage, destination);
            stage = null;
            Directory.Delete(source, recursive: true);
            error = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (stage is not null && Directory.Exists(stage))
                HysxUtil.TryDeleteDirectory(stage);
        }
    }

    private static bool Same(Dictionary<string, string> left, Dictionary<string, string> right) =>
        left.Count == right.Count && left.All(file => right.TryGetValue(file.Key, out string? hash) && hash == file.Value);

    private static bool IsLedgerName(string relative)
    {
        string name = Path.GetFileName(relative);
        return name.Equals(PackOnceLedger.FileName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(PackOnceLedger.FileName + ".", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] Directories(string root) => Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
        .Select(path => Path.GetRelativePath(root, path)).Order(StringComparer.OrdinalIgnoreCase).ToArray();

    private static Dictionary<string, string> Manifest(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(root, path), Hash, StringComparer.OrdinalIgnoreCase);

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
