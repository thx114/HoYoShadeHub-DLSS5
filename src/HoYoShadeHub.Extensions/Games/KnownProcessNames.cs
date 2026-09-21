using HoYoShadeHub.Core;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>一条内置的进程名知识</summary>
public sealed record KnownProcess(string ProcessName, string DisplayName, string? Biz = null, bool IsBeta = false);

/// <summary>
/// 内置进程名表。
///
/// 来源：HoYoShade 自带启动器（<c>简体中文启动器.bat</c>）里写死的那几行：
/// <code>
/// start "" /wait /b inject.exe YuanShen.exe
/// start "" /wait /b inject.exe GenshinImpact.exe
/// start "" /wait /b inject.exe StarRail.exe
/// start "" /wait /b inject.exe ZenlessZoneZero.exe
/// </code>
/// 这张表在我们这儿有两个用处：
/// <list type="number">
/// <item>Hub 不认识这个游戏（自定义条目 / 版本对不上）时，还能报出一个进程名；</item>
/// <item>用户选了个 exe 之后，反查这是哪个游戏，用来填显示名。</item>
/// </list>
/// </summary>
public static class KnownProcessNames
{
    public const string YuanShen = "YuanShen.exe";
    public const string GenshinImpact = "GenshinImpact.exe";
    public const string HonkaiImpact3rd = "BH3.exe";
    public const string StarRail = "StarRail.exe";
    public const string ZenlessZoneZero = "ZenlessZoneZero.exe";

    /// <summary>正式服的四个（bat 里直接注入的就是这四个）</summary>
    public static readonly IReadOnlyList<string> ReleaseProcesses =
    [
        YuanShen,
        GenshinImpact,
        StarRail,
        ZenlessZoneZero,
    ];

    public static readonly IReadOnlyList<KnownProcess> All =
    [
        new(YuanShen, "原神（国服）", GameBiz.hk4e_cn),
        new(GenshinImpact, "原神（国际）", GameBiz.hk4e_global),
        new(HonkaiImpact3rd, "崩坏3", GameBiz.bh3_cn),
        new(StarRail, "崩坏：星穹铁道", GameBiz.hkrpg_cn),
        new(ZenlessZoneZero, "绝区零", GameBiz.nap_cn),

        // 测试服（bat 里也列了）
        new("Genshin.exe", "原神（测试服）", GameBiz.hk4e_cn_beta, IsBeta: true),
        new("ZZZ.exe", "绝区零（公测前测试服）", GameBiz.nap_beta_prebeta, IsBeta: true),
        new("ZenlessZoneZeroBeta.exe", "绝区零（创作者体验服）", GameBiz.nap_beta_postbeta, IsBeta: true),
        new("NexusAnima.exe", "Nexus Anima", GameBiz.hna_cbt1, IsBeta: true),
        new("PetitPlanet.exe", "Petit Planet", GameBiz.pp_cbt1, IsBeta: true),
    ];

    private static readonly Dictionary<string, KnownProcess> _byProcessName = All
        .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>GameBiz → 进程名。认不出来的返回 null（Hub 自己还有一份更全的映射）</summary>
    public static string? ForBiz(GameBiz biz)
    {
        if (biz.Value.Length == 0)
        {
            return null;
        }

        foreach (KnownProcess known in All)
        {
            if (known.Biz is { } knownBiz && string.Equals(knownBiz, biz.Value, StringComparison.OrdinalIgnoreCase))
            {
                return known.ProcessName;
            }
        }

        // 认不出具体区服就退到游戏家族
        return biz.Game switch
        {
            GameBiz.hk4e => YuanShen,
            GameBiz.bh3 => HonkaiImpact3rd,
            GameBiz.hkrpg => StarRail,
            GameBiz.nap => ZenlessZoneZero,
            GameBiz.pp => "PetitPlanet.exe",
            GameBiz.hna => "NexusAnima.exe",
            _ => null,
        };
    }

    /// <summary>反查：进程名或 exe 全路径 → 内置条目</summary>
    public static KnownProcess? Find(string? exePathOrProcessName)
    {
        if (string.IsNullOrWhiteSpace(exePathOrProcessName))
        {
            return null;
        }

        string fileName = Path.GetFileName(exePathOrProcessName.Trim());
        return _byProcessName.TryGetValue(fileName, out KnownProcess? known) ? known : null;
    }

    public static bool IsKnownProcess(string? exePathOrProcessName) => Find(exePathOrProcessName) is not null;

    /// <summary>猜一个好听的显示名；猜不出返回 null</summary>
    public static string? GuessDisplayName(string? exePathOrProcessName) => Find(exePathOrProcessName)?.DisplayName;
}
