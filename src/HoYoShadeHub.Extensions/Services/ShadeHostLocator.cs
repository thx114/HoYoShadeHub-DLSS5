using HoYoShadeHub.Extensions.Models;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// 定位 HoYoShade 宿主目录。
///
/// 事实依据（Hub 源码）：HoYoShade 是**全局一份**，装在用户数据目录下，所有游戏共用：
/// <code>
/// Features/GameLauncher/GameLauncherPage.xaml.cs:118
///     string hoYoShadePath = Path.Combine(AppConfig.UserDataFolder, "HoYoShade");
/// </code>
/// 不是 &lt;游戏目录&gt;\HoYoShade。
/// </summary>
public static class ShadeHostLocator
{
    public const string HoYoShadeFolderName = "HoYoShade";
    public const string OpenHoYoShadeFolderName = "OpenHoYoShade";

    public static string FolderNameOf(ShadeHostKind kind) =>
        kind == ShadeHostKind.HoYoShade ? HoYoShadeFolderName : OpenHoYoShadeFolderName;

    /// <summary>拼出默认安装根目录；userDataFolder 为空时返回 null</summary>
    public static string? GetDefaultRoot(string? userDataFolder, ShadeHostKind kind = ShadeHostKind.HoYoShade)
    {
        if (string.IsNullOrWhiteSpace(userDataFolder))
        {
            return null;
        }
        return Path.Combine(userDataFolder, FolderNameOf(kind));
    }

    /// <summary>从用户数据目录定位（Hub 的默认布局）</summary>
    public static ShadeHost? FromUserDataFolder(string? userDataFolder, ShadeHostKind kind = ShadeHostKind.HoYoShade)
    {
        string? root = GetDefaultRoot(userDataFolder, kind);
        return root is null ? null : FromShadeRoot(root, kind);
    }

    /// <summary>直接给一个 HoYoShade 目录（手动指定 / 用户自己挪过位置）</summary>
    public static ShadeHost? FromShadeRoot(string? shadeRoot, ShadeHostKind kind = ShadeHostKind.HoYoShade)
    {
        if (string.IsNullOrWhiteSpace(shadeRoot) || !Directory.Exists(shadeRoot))
        {
            return null;
        }

        var host = new ShadeHost(shadeRoot, kind);
        return host.Exists ? host : null;
    }

    /// <summary>
    /// 兜底候选：用户数据目录下的两个常规名 + 传进来的额外目录。
    /// 只做浅层探测，不扫盘。
    /// </summary>
    public static IEnumerable<ShadeHost> EnumerateCandidates(string? userDataFolder, IEnumerable<string>? extraRoots = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string?>();

        foreach (ShadeHostKind kind in new[] { ShadeHostKind.HoYoShade, ShadeHostKind.OpenHoYoShade })
        {
            candidates.Add(GetDefaultRoot(userDataFolder, kind));
        }

        if (extraRoots is not null)
        {
            foreach (string root in extraRoots)
            {
                candidates.Add(root);
                // 也试试把用户选到的目录当成 <用户数据目录>
                candidates.Add(Path.Combine(root, HoYoShadeFolderName));
                candidates.Add(Path.Combine(root, OpenHoYoShadeFolderName));
            }
        }

        foreach (string? candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate) || !Directory.Exists(candidate))
            {
                continue;
            }

            ShadeHostKind kind = Path.GetFileName(candidate).Equals(OpenHoYoShadeFolderName, StringComparison.OrdinalIgnoreCase)
                ? ShadeHostKind.OpenHoYoShade
                : ShadeHostKind.HoYoShade;

            ShadeHost? host = FromShadeRoot(candidate, kind);
            if (host is not null)
            {
                yield return host;
            }
        }
    }
}
