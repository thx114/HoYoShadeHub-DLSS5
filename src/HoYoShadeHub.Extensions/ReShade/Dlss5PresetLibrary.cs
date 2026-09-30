namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>DLSS5 预设切换器的一个预设文件（扫描结果 / 导入产物）</summary>
public sealed class Dlss5PresetFile
{
    /// <summary>完整路径</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>显示名（文件名）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>相对 Addons 目录的位置（DLSS5-Presets 子目录里的带相对路径，便于区分来源）</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>文件字节数（分享码 256 KiB 上限检查时提示用）</summary>
    public long Size { get; set; }
}

/// <summary>
/// DLSS5 预设切换器的预设库：扫描 / 读内容 / 存导入。
///
/// <para>
/// 扫描位置和游戏内插件一致（dlss5-preset-switcher.cpp RefreshPresets）：
/// Addons 目录顶层（非递归）+ <c>Addons\DLSS5-Presets\</c>（递归，可建子目录按游戏 / 作者整理）。
/// 导入的分享码默认写进 <c>DLSS5-Presets\</c>，文件名 <c>Shared-XXXXXXXX.ini</c>
/// （和游戏内插件的默认命名一致）。
/// </para>
/// </summary>
public static class Dlss5PresetLibrary
{
    public const string PresetFolderName = "DLSS5-Presets";

    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>扫描预设列表（Addons 顶层 + DLSS5-Presets 递归），按文件名排序</summary>
    public static List<Dlss5PresetFile> Scan(string? addonsDirectory)
    {
        var result = new List<Dlss5PresetFile>();
        if (string.IsNullOrWhiteSpace(addonsDirectory) || !Directory.Exists(addonsDirectory))
        {
            return result;
        }

        void AddFile(string path, string relativeRoot)
        {
            try
            {
                var info = new FileInfo(path);
                result.Add(new Dlss5PresetFile
                {
                    Path = info.FullName,
                    Name = info.Name,
                    RelativePath = Path.GetRelativePath(relativeRoot, info.FullName),
                    Size = info.Length,
                });
            }
            catch
            {
                // 单个文件读不到不影响列表
            }
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(addonsDirectory, "*.ini", SearchOption.TopDirectoryOnly))
            {
                AddFile(file, addonsDirectory);
            }
        }
        catch
        {
            // 目录读不了就只剩子目录那份
        }

        string presetFolder = Path.Combine(addonsDirectory, PresetFolderName);
        if (Directory.Exists(presetFolder))
        {
            try
            {
                foreach (string file in Directory.EnumerateFiles(presetFolder, "*.ini", SearchOption.AllDirectories))
                {
                    AddFile(file, addonsDirectory);
                }
            }
            catch
            {
                // ignore
            }
        }

        return [.. result.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>读预设原始字节（UTF-8 BOM 剥掉，和游戏内插件 ReadText 一致；UTF-16 直接拒）</summary>
    public static byte[]? ReadContent(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            {
                bytes = bytes[3..];
            }

            if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
            {
                return null;
            }

            return bytes;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>给这个预设生成分享码（超 256 KiB / 读不到返回 null）</summary>
    public static string? BuildShareCode(string path)
    {
        byte[]? content = ReadContent(path);
        return content is null ? null : Dlss5PresetShareCode.Encode(content);
    }

    /// <summary>
    /// 把分享码解码并保存成预设文件（写进 <c>Addons\DLSS5-Presets\</c>）。
    /// </summary>
    /// <param name="wantedName">用户指定的文件名（不含扩展名；空 = Shared-XXXXXXXX）</param>
    /// <returns>保存的完整路径；解码失败 / 写失败返回 null</returns>
    public static string? ImportShareCode(string? addonsDirectory, string code, string? wantedName)
    {
        if (string.IsNullOrWhiteSpace(addonsDirectory))
        {
            return null;
        }

        byte[]? content = Dlss5PresetShareCode.Decode(code);
        if (content is null)
        {
            return null;
        }

        string baseName = SanitizeFileName(wantedName);
        if (baseName.Length == 0)
        {
            baseName = "Shared-" + RandomName();
        }

        try
        {
            string directory = Path.Combine(addonsDirectory, PresetFolderName);
            Directory.CreateDirectory(directory);

            string target = UniquePath(directory, baseName + ".ini");
            File.WriteAllBytes(target, content);
            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>导入一个 .ini / .txt 预设文件（复制进 DLSS5-Presets，重名自动加后缀）</summary>
    public static string? ImportFile(string? addonsDirectory, string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(addonsDirectory) || !File.Exists(sourcePath))
        {
            return null;
        }

        string baseName = SanitizeFileName(Path.GetFileNameWithoutExtension(sourcePath));
        if (baseName.Length == 0)
        {
            baseName = "Shared-" + RandomName();
        }

        try
        {
            string directory = Path.Combine(addonsDirectory, PresetFolderName);
            Directory.CreateDirectory(directory);

            string target = UniquePath(directory, baseName + ".ini");
            File.Copy(sourcePath, target);
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        char[] chars = name.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        string result = new string(chars).Trim().TrimEnd('.');
        return result;
    }

    private static string UniquePath(string directory, string fileName)
    {
        string target = Path.Combine(directory, fileName);
        if (!File.Exists(target))
        {
            return target;
        }

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);
        for (int i = 2; ; i++)
        {
            target = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(target))
            {
                return target;
            }
        }
    }

    private static string RandomName()
    {
        var random = new Random();
        var chars = new char[8];
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[random.Next(Alphabet.Length)];
        }

        return new string(chars);
    }
}
