using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace HoYoShadeHub.Features.Xxmi;

/// <summary>Mods 目录里的一条：一级子目录（常见）或一级 .ini</summary>
internal sealed record XxmiModEntry(string Name, string Path, bool IsDirectory, bool Enabled);

/// <summary>
/// XXMI / 3DMigoto 的 mod 管理。约定：<c>&lt;MI 实例&gt;\Mods\</c> 下**一级子目录就是一个 mod**，
/// 启用/禁用**靠改名字**（加/去掉 <c>DISABLED</c> 后缀），这也是 3DMigoto 社区通用做法。
/// </summary>
internal static class XxmiModManager
{
    /// <summary>列出 mods（只列一级，和 3DMigoto 的行为一致）</summary>
    public static List<XxmiModEntry> List(string modsDirectory)
    {
        List<XxmiModEntry> list = [];

        if (!Directory.Exists(modsDirectory))
        {
            return list;
        }

        try
        {
            foreach (string dir in Directory.EnumerateDirectories(modsDirectory))
            {
                string name = Path.GetFileName(dir);

                // 下划线开头的都是启动器自己用的内部目录（_cache = 解压暂存、_backup = 更新前的旧版本），
                // 不是用户装的 mod，不该出现在卡片列表里。
                if (name.StartsWith('.') || name.StartsWith('_'))
                {
                    continue;
                }

                list.Add(new XxmiModEntry(name, dir, true, IsEnabled(name)));
            }

            foreach (string file in Directory.EnumerateFiles(modsDirectory, "*.ini"))
            {
                string name = Path.GetFileName(file);
                list.Add(new XxmiModEntry(name, file, false, IsEnabled(name)));
            }
        }
        catch
        {
            // 目录读不了就当空的
        }

        return [.. list.OrderByDescending(m => m.Enabled).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>名字是不是「启用」状态（末尾带 DISABLED 就算禁用）</summary>
    public static bool IsEnabled(string name)
        => !name.EndsWith("DISABLED", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith("DISABLED ", StringComparison.OrdinalIgnoreCase);

    /// <summary>把名字末尾的 DISABLED 后缀剥掉（纯展示用，不碰磁盘）</summary>
    public static string StripDisabled(string name)
    {
        string clean = (name ?? string.Empty).Trim();

        while (clean.EndsWith("DISABLED", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[..^"DISABLED".Length].TrimEnd();
        }

        return clean;
    }

    /// <summary>启用/禁用：改名字（加/去掉 DISABLED 后缀）。返回新路径</summary>
    public static string SetEnabled(string path, bool enabled)
    {
        string parent = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileName(path);
        bool isDir = Directory.Exists(path);
        string clean = name.TrimEnd(' ');

        while (clean.EndsWith("DISABLED", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[..^"DISABLED".Length].TrimEnd(' ');
        }

        string wanted = enabled ? clean : clean + " DISABLED";
        string destination = Path.Combine(parent, wanted);

        if (string.Equals(path, destination, StringComparison.OrdinalIgnoreCase))
        {
            return path;
        }

        if (isDir)
        {
            Directory.Move(path, destination);
        }
        else
        {
            File.Move(path, destination, overwrite: true);
        }

        return destination;
    }

    /// <summary>删掉一个 mod（直接删，调用方负责先确认）</summary>
    public static void Delete(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    /// <summary>把一整个文件夹作为 mod 复制进 Mods 目录；返回新路径</summary>
    public static string ImportFolder(string modsDirectory, string sourceFolder)
    {
        string name = Path.GetFileName(sourceFolder.TrimEnd('\\'));
        string destination = Path.Combine(modsDirectory, name);
        destination = MakeUnique(destination);

        CopyDirectory(sourceFolder, destination);
        return destination;
    }

    /// <summary>把 zip 解压成一个 mod（解压到 Mods\&lt;zip 名&gt;）；返回新路径</summary>
    public static string ImportZip(string modsDirectory, string zipPath)
    {
        string name = Path.GetFileNameWithoutExtension(zipPath);
        string destination = MakeUnique(Path.Combine(modsDirectory, name));
        Directory.CreateDirectory(destination);
        ZipFile.ExtractToDirectory(zipPath, destination, overwriteFiles: true);
        return destination;
    }

    private static string MakeUnique(string path)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return path;
        }

        string parent = Path.GetDirectoryName(path) ?? string.Empty;
        string name = Path.GetFileName(path);

        for (int i = 2; i < 1000; i++)
        {
            string candidate = Path.Combine(parent, $"{name} ({i})");

            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        return path;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }
}
