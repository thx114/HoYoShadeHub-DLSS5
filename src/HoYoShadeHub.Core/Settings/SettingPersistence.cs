using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;

namespace HoYoShadeHub.Core.Settings;

public static class SettingPersistence
{
    public static bool TryWrite(Lock gate, Dictionary<string, string?> cache, string key,
        string? value, Action persist, out Exception? error)
    {
        lock (gate)
        {
            error = null;
            if (cache.TryGetValue(key, out string? current) && current == value)
                return true;
            try
            {
                persist();
                cache[key] = value;
                return true;
            }
            catch (Exception ex)
            {
                error = ex;
                return false;
            }
        }
    }

    public static void WriteConfiguration(string path, string content)
    {
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(fullPath))
                File.Replace(temporaryPath, fullPath, null);
            else
                File.Move(temporaryPath, fullPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
