using System.Diagnostics;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>Protocol installs share the page path policy and publish only validated content.</summary>
public static class ModInstallTransaction
{
    public static void CheckPath(string path)
    {
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
        {
            try
            {
                if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Mod path contains a link: " + p);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public static void CheckTree(string path)
    {
        CheckPath(path);
        foreach (string child in Directory.EnumerateFileSystemEntries(path))
        {
            CheckPath(child);
            if (Directory.Exists(child)) CheckTree(child);
        }
    }

    public static string PrepareZip(string zip, string stagingInstance)
    {
        CheckPath(stagingInstance);
        Directory.CreateDirectory(stagingInstance);
        File.WriteAllText(Path.Combine(stagingInstance, "d3dx.ini"), string.Empty);
        var context = ModDirectoryPolicy.ResolveXxmi("protocol-stage", stagingInstance);
        var session = new ModDirectorySession();
        var lease = session.Bind(session.Select(context.OwnerKey, context.Backend), context);
        string content = ModDirectoryPolicy.ImportZip(session, lease, zip);
        if (!Directory.EnumerateFileSystemEntries(content).Any()) throw new InvalidDataException("Mod archive is empty.");
        string[] dirs = Directory.GetDirectories(content);
        if (dirs.Length == 1 && Directory.GetFiles(content).Length == 0) content = dirs[0];
        CheckTree(content);
        return content;
    }

    public static async Task<FileStream> AcquireAsync(ModDirectoryContext context, int modId, CancellationToken cancellationToken)
    {
        ModDirectoryPolicy.Validate(context);
        CheckPath(context.ModsDirectory);
        Directory.CreateDirectory(context.ModsDirectory);
        string path = Path.Combine(context.ModsDirectory, "_install-lock-" + modId);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ModDirectoryPolicy.Validate(context);
            CheckPath(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (timer.Elapsed < TimeSpan.FromSeconds(30))
            { await Task.Delay(100, cancellationToken).ConfigureAwait(false); }
        }
    }

    public static string Commit(ModDirectoryContext context, int modId, string content, bool replace, Action? beforePublish = null)
    {
        ModDirectoryPolicy.Validate(context);
        CheckTree(content);
        string destination = Path.Combine(context.ModsDirectory, "gb_" + modId);
        CheckPath(destination);
        if (File.Exists(destination)) throw new IOException("Mod destination is a file.");
        if (Directory.Exists(destination) && !replace) throw new IOException("Mod already exists: " + destination);
        string? backup = null;
        if (Directory.Exists(destination))
        {
            CheckTree(destination);
            string root = Path.Combine(context.ModsDirectory, "_backup");
            CheckPath(root);
            Directory.CreateDirectory(root);
            backup = Path.Combine(root, "gb_" + modId + "_" + Guid.NewGuid().ToString("N"));
            Directory.Move(destination, backup);
        }
        try
        {
            beforePublish?.Invoke();
            ModDirectoryPolicy.Validate(context);
            CheckPath(destination);
            Directory.Move(content, destination);
            return destination;
        }
        catch (Exception publishError)
        {
            if (backup is not null)
            {
                try
                {
                    ModDirectoryPolicy.Validate(context);
                    CheckPath(destination);
                    CheckPath(backup);
                    Directory.Move(backup, destination);
                }
                catch (Exception rollbackError)
                { throw new AggregateException("Publish and rollback failed; old Mod remains at " + backup, publishError, rollbackError); }
            }
            throw;
        }
    }
}
