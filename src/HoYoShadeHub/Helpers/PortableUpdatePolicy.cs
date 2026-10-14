using System;
using System.IO;
using System.Linq;

namespace HoYoShadeHub.Helpers;

internal static class PortableUpdatePolicy
{
    internal static string GetArchiveTarget(string root, string entryName)
    {
        string relative = entryName.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative) || relative.StartsWith('/') || relative.Contains(':')
            || relative.Split('/').Any(part => part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.')))
        {
            throw new InvalidDataException($"Invalid update entry: {entryName}");
        }

        string rootPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(rootPath, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Update entry escapes its root: {entryName}");
        }

        RejectReparsePoints(rootPath, target);
        return target;
    }

    internal static string ReadVersionTarget(string root, string iniContents)
    {
        string? relative = null;
        foreach (string raw in iniContents.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' or '[')
            {
                continue;
            }

            int separator = line.IndexOf('=');
            if (separator > 0 && line[..separator].Trim().Equals("exe_path", StringComparison.OrdinalIgnoreCase))
            {
                if (relative is not null)
                {
                    throw new InvalidDataException("version.ini contains multiple exe_path entries.");
                }

                relative = line[(separator + 1)..].Trim().Trim('"');
            }
        }

        if (relative is null)
        {
            throw new InvalidDataException("version.ini is missing exe_path.");
        }

        string[] parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length != 2 || !parts[0].StartsWith("app-", StringComparison.OrdinalIgnoreCase)
            || parts[0].Length <= 4 || !parts[1].Equals("HoYoShadeHub.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("version.ini must point to app-*/HoYoShadeHub.exe.");
        }

        return GetArchiveTarget(root, relative);
    }

    internal static void WriteVersionAtomically(string versionIni, byte[] contents)
    {
        string temporary = versionIni + ".pending-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(versionIni))
            {
                File.Replace(temporary, versionIni, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, versionIni);
            }
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static void RejectReparsePoints(string root, string target)
    {
        string? path = target;
        while (path is not null)
        {
            if ((File.Exists(path) || Directory.Exists(path))
                && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException($"Update target traverses a reparse point: {path}");
            }

            if (path.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            path = Path.GetDirectoryName(path);
        }
    }
}
