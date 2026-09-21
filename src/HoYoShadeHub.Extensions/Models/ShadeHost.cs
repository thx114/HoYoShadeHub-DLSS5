namespace HoYoShadeHub.Extensions.Models;

/// <summary>
/// HoYoShade 宿主类型。当前只支持 HoYoShade（OpenHoYoShade 预留）。
/// </summary>
public enum ShadeHostKind
{
    HoYoShade = 0,
    OpenHoYoShade = 1,
}

/// <summary>
/// 一个 HoYoShade 安装实例的目录布局。
///
/// <code>
/// &lt;游戏目录&gt;\HoYoShade\
///   ├─ ReShade64.dll
///   ├─ inject.exe
///   ├─ ReShade.ini
///   ├─ reshade-shaders\
///   │    ├─ Shaders\   ← .fx
///   │    ├─ Textures\
///   │    └─ Addons\    ← *.addon64 / *.addon32
///   ├─ Presets\
///   └─ .hysx\          ← 扩展账本 + 备份（我们自己用，Hub 不碰）
/// </code>
/// </summary>
public sealed class ShadeHost
{
    public const string MetadataFolderName = ".hysx";
    public const string ShadersFolder = "reshade-shaders/Shaders";
    public const string TexturesFolder = "reshade-shaders/Textures";
    public const string AddonsFolder = "reshade-shaders/Addons";
    public const string PresetsFolder = "Presets";

    public ShadeHost(string rootPath, ShadeHostKind kind = ShadeHostKind.HoYoShade)
    {
        RootPath = Path.GetFullPath(rootPath);
        Kind = kind;
    }

    /// <summary>HoYoShade 根目录（不是游戏根目录）</summary>
    public string RootPath { get; }

    public ShadeHostKind Kind { get; }

    public string Name => Kind == ShadeHostKind.HoYoShade ? "HoYoShade" : "OpenHoYoShade";

    public string ReShadeIniPath => Path.Combine(RootPath, "ReShade.ini");

    /// <summary>插件真身所在目录（所有游戏共用这一份）</summary>
    public string AddonsPath => Path.Combine(RootPath, "reshade-shaders", "Addons");

    /// <summary>ReShade 主 DLL 是否存在 —— 判定这个目录是不是一个有效的宿主</summary>
    public bool Exists => File.Exists(Path.Combine(RootPath, "ReShade64.dll"))
                          || File.Exists(Path.Combine(RootPath, "ReShade32.dll"));

    public string MetadataPath => Path.Combine(RootPath, MetadataFolderName);

    public string LedgerPath => Path.Combine(MetadataPath, "installed.json");

    public string BackupPath => Path.Combine(MetadataPath, "backup");

    /// <summary>把账本里的相对路径还原成绝对路径，并阻止 .. 逃逸</summary>
    public string ResolveRelative(string relativePath)
    {
        string combined = Path.GetFullPath(Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        string root = Path.GetFullPath(RootPath);
        if (!combined.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"路径越界: {relativePath}");
        }
        return combined;
    }

    /// <summary>给定 HoYoShade 下的相对目录名，返回绝对目录</summary>
    public static string FolderToRelative(string folder)
    {
        return folder.Replace('\\', '/').Trim('/');
    }
}
