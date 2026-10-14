namespace HoYoShadeHub.Extensions.Services;

/// <summary>Contained package paths and staged directory replacement.</summary>
public static class PackageTree
{
    public static string ResolveRelative(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Contains(':') || relative.Split('/', '\\').Any(p => p is ".." or "."))
            throw new InvalidDataException("Invalid package-relative path: " + relative);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Package path escapes its root: " + relative);
        RejectReparse(fullRoot);
        string current = fullRoot;
        foreach (string part in Path.GetRelativePath(fullRoot, full).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            RejectReparse(current);
        }
        return full;
    }

    public static void ValidateGame(string game)
    {
        if (string.IsNullOrWhiteSpace(game) || game.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-'))
            throw new InvalidDataException("Invalid package game identifier: " + game);
    }

    private static void RejectReparse(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Package path contains a reparse point: " + path);
    }

    public static void ValidateTree(string source)
    {
        RejectReparse(source);
        foreach (string entry in Directory.EnumerateFileSystemEntries(source))
        {
            RejectReparse(entry);
            if (Directory.Exists(entry)) ValidateTree(entry);
        }
    }

    public static int Copy(string source, string destination, CancellationToken cancellationToken = default)
    {
        ValidateTree(source);
        if (Directory.Exists(destination)) ValidateTree(destination);
        return CopyValidated(source, destination, cancellationToken);
    }

    private static int CopyValidated(string source, string destination, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        int copied = 0;
        foreach (string file in Directory.EnumerateFiles(source))
        {
            token.ThrowIfCancellationRequested();
            HysxFileLink.LinkOrCopy(file, ResolveRelative(destination, Path.GetFileName(file)));
            copied++;
        }
        foreach (string directory in Directory.EnumerateDirectories(source))
            copied += CopyValidated(directory, ResolveRelative(destination, Path.GetFileName(directory)), token);
        return copied;
    }

    public static string CreateStage(string target)
    {
        string full = Path.GetFullPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string stage = full + ".hysx-stage-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(stage);
        return stage;
    }

    public static void Commit(string stage, string target)
    {
        ValidateTree(stage);
        if (Directory.Exists(target)) ValidateTree(target);
        string backup = target + ".hysx-backup-" + Guid.NewGuid().ToString("N");
        bool moved = Directory.Exists(target);
        if (moved) Directory.Move(target, backup);
        try { Directory.Move(stage, target); }
        catch
        {
            if (moved) Directory.Move(backup, target);
            throw;
        }
        if (moved) HysxUtil.TryDeleteDirectory(backup);
    }
}
