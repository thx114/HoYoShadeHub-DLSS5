namespace HoYoShadeHub.Extensions.Services;

/// <summary>「把 <c>nvngx_dlssnr.dll</c> 放到 OptiScaler 旁边」的结果</summary>
public enum NrdllPlaceStatus
{
    /// <summary>目标目录里已经有一份，而且和源头一样大 —— 什么都不用做</summary>
    AlreadyPresent,

    /// <summary>这次从别处复制过去了</summary>
    Copied,

    /// <summary>哪儿都找不到 nvngx_dlssnr.dll</summary>
    NotFound,

    /// <summary>出错（目录不存在 / 复制失败）</summary>
    Failed,
}

public sealed record NrdllPlaceResult(NrdllPlaceStatus Status, string? SourcePath, string? TargetPath)
{
    public bool Ok => Status is NrdllPlaceStatus.AlreadyPresent or NrdllPlaceStatus.Copied;

    /// <summary>给界面用的一句话</summary>
    public string Message => Status switch
    {
        NrdllPlaceStatus.AlreadyPresent => "构建目录里已经有 nvngx_dlssnr.dll。",
        NrdllPlaceStatus.Copied => $"已把 nvngx_dlssnr.dll 从 {SourcePath} 复制到构建目录。",
        NrdllPlaceStatus.NotFound => "没找到 nvngx_dlssnr.dll —— 先到「DLL 配置」里装一个（或自己放一份到插件目录）。",
        _ => "复制 nvngx_dlssnr.dll 失败。",
    };
}

/// <summary>
/// OptiScaler 的神经渲染运行时（<c>nvngx_dlssnr.dll</c>）。
///
/// <para>
/// 各分支的手册都写着「把 <c>nvngx_dlssnr.dll</c> 放在包旁边」：
/// wilsjo2 的 INSTALL-DLSSNR.md 第 2 步「解压完整包到游戏 exe 旁边」、第 3 步「把 nvngx_dlssnr.dll 放到那里」；
/// NeuRotic / DLSS NR on AMD 同理（DLSS NR on AMD 用自己的安装程序，装完也是放在同一个目录）。
/// </para>
///
/// <para>
/// 我们这边是**注入**而不是铺到游戏目录，所以「旁边」= OptiScaler 构建目录
/// （<c>&lt;用户数据目录&gt;\OptiScaler\&lt;来源&gt;\&lt;版本&gt;\</c>）。
/// 这个类负责从「DLL 配置」装好的插件目录（以及别的构建目录）里拿一份复制过去。
/// </para>
/// </summary>
public static class OptiScalerRuntime
{
    public const string NeuralRuntimeFileName = "nvngx_dlssnr.dll";

    /// <summary>OptiScaler 的 Streamline 后端文件夹名（ini 注释：OptiScaler/streamline）</summary>
    public const string StreamlineFolderName = "streamline";

    /// <summary>
    /// Streamline 核心文件清单（OptiScaler 自带的 <c>OptiScaler/streamline</c> 那一套）。
    /// FGOutput=DLSSG 时 ini 注释要求这套 + nvngx_dlssg.dll。
    /// </summary>
    public static readonly string[] StreamlineFileNames =
    [
        "sl.interposer.dll",
        "sl.common.dll",
        "sl.dlss.dll",
        "sl.dlss_g.dll",
        "sl.reflex.dll",
        "sl.pcl.dll",
        "sl.nis.dll",
    ];

    /// <summary>DLSSG 后端额外需要的 NGX 实现文件</summary>
    public const string DlssgFileName = "nvngx_dlssg.dll";

    /// <summary>
    /// 确保构建目录里有 <c>nvngx_dlssnr.dll</c>。
    /// 已经有**同样大小**的一份就不动（用户可能自己换过版本，别乱覆盖）。
    /// </summary>
    /// <param name="buildDirectory">OptiScaler 构建目录</param>
    /// <param name="addonsDirectory">插件目录（DLL 配置把运行时装在这儿）</param>
    /// <param name="extraSearchDirectories">其它候选目录（比如别的 OptiScaler 构建目录）</param>
    public static NrdllPlaceResult EnsureNrdll(
        string buildDirectory,
        string? addonsDirectory,
        IEnumerable<string>? extraSearchDirectories = null)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory) || !Directory.Exists(buildDirectory))
        {
            return new NrdllPlaceResult(NrdllPlaceStatus.Failed, null, null);
        }

        string target = Path.Combine(buildDirectory, NeuralRuntimeFileName);
        string? source = FindSource(target, addonsDirectory, extraSearchDirectories);

        if (source is null)
        {
            // 目标目录自己带着一份（大小无从比较）也算有
            return File.Exists(target)
                ? new NrdllPlaceResult(NrdllPlaceStatus.AlreadyPresent, target, target)
                : new NrdllPlaceResult(NrdllPlaceStatus.NotFound, null, target);
        }

        try
        {
            bool sameFile = string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
            bool both = File.Exists(target) && File.Exists(source);
            if (both && !sameFile && new FileInfo(target).Length == new FileInfo(source).Length)
            {
                return new NrdllPlaceResult(NrdllPlaceStatus.AlreadyPresent, source, target);
            }

            if (!both || !sameFile)
            {
                File.Copy(source, target, overwrite: true);
                return new NrdllPlaceResult(NrdllPlaceStatus.Copied, source, target);
            }

            return new NrdllPlaceResult(NrdllPlaceStatus.AlreadyPresent, source, target);
        }
        catch
        {
            return new NrdllPlaceResult(NrdllPlaceStatus.Failed, source, target);
        }
    }

    /// <summary>构建目录里有没有运行时</summary>
    public static bool HasNrdll(string buildDirectory)
        => !string.IsNullOrWhiteSpace(buildDirectory)
           && File.Exists(Path.Combine(buildDirectory, NeuralRuntimeFileName));

    // ───────────────────────── Streamline ─────────────────────────

    /// <summary>构建目录里 Streamline 后端文件夹的绝对路径（<c>OptiScaler/streamline</c>）</summary>
    public static string StreamlineDirectory(string buildDirectory)
        => Path.Combine(buildDirectory, "OptiScaler", StreamlineFolderName);

    /// <summary>构建目录里 Streamline 文件齐不齐（核心 7 个 + streamline\nvngx_dlssg.dll）</summary>
    public static bool HasStreamline(string buildDirectory)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory))
        {
            return false;
        }

        string dir = StreamlineDirectory(buildDirectory);
        if (!StreamlineFileNames.All(f => File.Exists(Path.Combine(dir, f))))
        {
            return false;
        }

        return File.Exists(Path.Combine(dir, DlssgFileName));
    }

    /// <summary>
    /// 把 Streamline 核心 7 文件和 nvngx_dlssg.dll 从源目录复制到构建目录的
    /// <c>OptiScaler/streamline</c>。OptiScaler 的 StreamlineProxy 用绝对路径
    /// 从该文件夹逐个加载（sl.common.dll / nvngx_dlssg.dll 也在里面），
    /// 所以 dlssg 放进 streamline 文件夹；构建根另放一份兼容包内默认布局。
    /// 目标已存在同样大小的文件就跳过，不覆盖用户自己换过的版本。
    /// </summary>
    /// <param name="buildDirectory">OptiScaler 构建目录</param>
    /// <param name="sourceDirectory">含整套 sl.* 的目录（比如插件目录、游戏目录）</param>
    /// <returns>复制成功的文件名列表；源里找不到的不在列表里</returns>
    public static List<string> EnsureStreamline(string buildDirectory, string sourceDirectory)
    {
        var copied = new List<string>();

        if (string.IsNullOrWhiteSpace(buildDirectory) || !Directory.Exists(buildDirectory)
            || string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return copied;
        }

        string targetDir = StreamlineDirectory(buildDirectory);
        Directory.CreateDirectory(targetDir);

        foreach (string file in StreamlineFileNames)
        {
            if (CopyIfNeeded(Path.Combine(sourceDirectory, file), Path.Combine(targetDir, file)))
            {
                copied.Add(file);
            }
        }

        if (CopyIfNeeded(Path.Combine(sourceDirectory, DlssgFileName), Path.Combine(targetDir, DlssgFileName)))
        {
            copied.Add(DlssgFileName);
        }

        // 构建根也放一份（跟 nvngx_dlssnr.dll 同一层，兼容默认 ini 布局）
        CopyIfNeeded(Path.Combine(sourceDirectory, DlssgFileName), Path.Combine(buildDirectory, DlssgFileName));

        return copied;
    }

    /// <summary>OptiScaler 配置文件名</summary>
    public const string ConfigFileName = "OptiScaler.ini";

    /// <summary>
    /// 把 ini 里 <c>[Libraries] OptiDllPath</c> 写成构建目录下 <c>OptiScaler</c> 文件夹的绝对路径。
    /// 默认值 auto 解析为相对「游戏 exe 目录」的 <c>.\OptiScaler</c>，外部注入（DLL 在数据目录）
    /// 时会指向游戏目录导致 StreamlineProxy 找不到 sl.interposer.dll。
    /// </summary>
    /// <returns>true 表示 ini 存在且已写入（或本来就是该绝对路径）</returns>
    public static bool EnsureConfigDllPath(string buildDirectory)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory) || !Directory.Exists(buildDirectory))
        {
            return false;
        }

        string iniPath = Path.Combine(buildDirectory, ConfigFileName);
        if (!File.Exists(iniPath))
        {
            return false;
        }

        string wanted = Path.Combine(buildDirectory, "OptiScaler");
        string[] lines = File.ReadAllLines(iniPath);
        bool inLibraries = false;
        bool changed = false;
        bool found = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inLibraries = string.Equals(trimmed, "[Libraries]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (!inLibraries)
            {
                continue;
            }

            int eq = lines[i].IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = lines[i][..eq].Trim();
            if (string.Equals(key, "OptiDllPath", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                string value = lines[i][(eq + 1)..].Trim();
                if (!string.Equals(value, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"OptiDllPath = {wanted}";
                    changed = true;
                }

                break;
            }
        }

        if (!found)
        {
            return false;
        }

        if (changed)
        {
            File.WriteAllLines(iniPath, lines);
        }

        return true;
    }

    /// <summary>目标缺失或大小不一致才复制；源不存在返回 false</summary>
    private static bool CopyIfNeeded(string source, string target)    {
        if (!File.Exists(source))
        {
            return false;
        }

        try
        {
            if (File.Exists(target) && new FileInfo(source).Length == new FileInfo(target).Length)
            {
                return false;
            }

            File.Copy(source, target, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>找一份可用的源（跳过目标自己那个文件）</summary>
    public static string? FindSource(
        string? targetPath,
        string? addonsDirectory,
        IEnumerable<string>? extraSearchDirectories = null)
    {
        var candidates = new List<string?>();

        if (extraSearchDirectories is not null)
        {
            candidates.AddRange(extraSearchDirectories);
        }

        candidates.Add(addonsDirectory);

        foreach (string? directory in candidates)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string path = Path.Combine(directory, NeuralRuntimeFileName);
            if (!File.Exists(path))
            {
                continue;
            }

            if (targetPath is not null
                && string.Equals(Path.GetFullPath(path), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return path;
        }

        return null;
    }
}
