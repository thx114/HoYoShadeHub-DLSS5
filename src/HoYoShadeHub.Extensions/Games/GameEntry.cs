using HoYoShadeHub.Core;
using System.Security.Cryptography;
using System.Text;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>
/// 一个「游戏」条目。
///
/// 它要同时喂三个东西，所以是共享的一份数据：
/// <list type="bullet">
/// <item>插件开关 —— 要 <c>&lt;游戏目录&gt;\ReShade.ini</c> 的路径；</item>
/// <item>启动 —— 要 exe 全路径；</item>
/// <item>注入 —— 要进程名（<c>inject.exe</c> 的参数）。</item>
/// </list>
///
/// <see cref="GameDirectory"/> / <see cref="ProcessName"/> / <see cref="ReShadeIniPath"/> /
/// <see cref="HasReShadeIni"/> 全是**派生属性**，不单独持久化 —— 只存 <see cref="ExePath"/>。
/// </summary>
public sealed class GameEntry
{
    public const string BizIdPrefix = "biz:";
    public const string ExeIdPrefix = "exe:";

    private string? _shortId;

    public GameEntry(string id, string displayName)
    {
        Id = id;
        DisplayName = displayName;
    }

    /// <summary>稳定短标识（Id 的 SHA256 前 12 位）—— 拿来当文件名 / 合成 biz 用</summary>
    public string ShortId => _shortId ??= Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Id)))[..12]
        .ToLowerInvariant();

    /// <summary>
    /// 自定义游戏在 Hub 这边的**合成 GameBiz**。
    ///
    /// 顶部游戏列表、<c>AppConfig</c> 那一堆 per-biz 设置（安装路径、启动选项、DX12…）都是按字符串存的，
    /// 所以给自定义游戏一个稳定的假 biz，它们就能像已知游戏一样待在同一个列表里。
    /// <see cref="GameBiz.IsKnown"/> 对它是 false —— Hub 不会拿它去 HoYoPlay 查任何东西。
    /// </summary>
    public GameBiz CustomBiz => new(CustomBizValue);

    public string CustomBizValue => "custom_" + ShortId;

    /// <summary>稳定标识，用于持久化勾选状态（<c>biz:hk4e_cn</c> / <c>exe:&lt;全路径&gt;</c>）</summary>
    public string Id { get; }

    public string DisplayName { get; set; }

    /// <summary>用户选的（或 Hub 探测到的）exe 全路径</summary>
    public string? ExePath { get; set; }

    /// <summary>Hub 已知的游戏才有；自定义条目为 null</summary>
    public GameBiz? Biz { get; set; }

    /// <summary>用户手动加的</summary>
    public bool IsCustom { get; set; }

    /// <summary>这个游戏走不走注入模式（全局开关说法见 docs/GAMES-AND-INJECT.md §5：只影响单个游戏）</summary>
    public bool UseInjectMode { get; set; }

    public string? GameDirectory
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ExePath))
            {
                return null;
            }

            try
            {
                return Path.GetDirectoryName(Path.GetFullPath(ExePath));
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>进程名 —— 直接喂给 <c>inject.exe</c> 的那个参数</summary>
    public string? ProcessName =>
        string.IsNullOrWhiteSpace(ExePath) ? null : Path.GetFileName(ExePath);

    /// <summary>进程名去掉 .exe —— 比对进程列表时用</summary>
    public string? ProcessNameWithoutExtension =>
        string.IsNullOrWhiteSpace(ProcessName) ? null : Path.GetFileNameWithoutExtension(ProcessName);

    public string? ReShadeIniPath =>
        GameDirectory is null ? null : Path.Combine(GameDirectory, "ReShade.ini");

    public bool HasReShadeIni => ReShadeIniPath is not null && File.Exists(ReShadeIniPath);

    public bool HasExe => !string.IsNullOrWhiteSpace(ExePath) && File.Exists(ExePath);

    /// <summary>Hub 已知游戏的 id：跟着 GameBiz 走，重装游戏也不会变</summary>
    public static string MakeBizId(GameBiz biz) => BizIdPrefix + biz.Value;

    /// <summary>自定义条目的 id：跟 exe 全路径走</summary>
    public static string MakeExeId(string exePath) => ExeIdPrefix + NormalizePath(exePath);

    /// <summary>路径归一化（去尾斜杠、全小写）—— 只用于比较和做 id</summary>
    public static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/').ToLowerInvariant();
        }
        catch
        {
            return (path ?? string.Empty).Trim().TrimEnd('\\', '/').ToLowerInvariant();
        }
    }

    public override string ToString() =>
        $"{DisplayName} ({ProcessName ?? "无 exe"}){(HasReShadeIni ? " ✅" : " ⚠")}";
}
