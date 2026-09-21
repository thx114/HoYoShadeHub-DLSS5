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
