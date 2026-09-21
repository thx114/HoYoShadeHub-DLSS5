namespace HoYoShadeHub.Extensions.Dlls;

/// <summary>缺这个 dll 时该怎么标</summary>
public enum DllRequirementLevel
{
    /// <summary>必须有 —— 缺了插件根本加载不了（界面标红）</summary>
    Required = 0,

    /// <summary>最好有 —— 缺了「可能无法使用」（界面标黄）</summary>
    Recommended = 1,
}

/// <summary>一个插件对 dll 的要求：<see cref="Files"/> 里列的文件必须**全部**在插件目录里</summary>
public sealed record DllRequirement(string[] Files, DllRequirementLevel Level, string Note)
{
    public string Display => string.Join(" + ", Files);
}

/// <summary>检查结果</summary>
public sealed record AddonDllStatus(
    IReadOnlyList<DllRequirement> MissingRequired,
    IReadOnlyList<DllRequirement> MissingRecommended)
{
    public static readonly AddonDllStatus Ok = new([], []);

    public bool HasMissingRequired => MissingRequired.Count > 0;

    public bool HasMissingRecommended => MissingRecommended.Count > 0;

    public bool IsOk => !HasMissingRequired && !HasMissingRecommended;

    /// <summary>0 = 没问题，1 = 缺推荐（黄），2 = 缺必须（红）</summary>
    public int Severity => HasMissingRequired ? 2 : HasMissingRecommended ? 1 : 0;

    public string Summary => Severity switch
    {
        2 => "缺 " + string.Join("、", MissingRequired.Select(r => r.Display)),
        1 => "缺 " + string.Join("、", MissingRecommended.Select(r => r.Display)) + "，可能无法使用",
        _ => string.Empty,
    };
}

/// <summary>
/// 哪些插件需要哪些 dll。
///
/// <para>
/// 判定依据是扩展目录里的 <c>tags</c>：带 <c>dlss5</c> 标签的插件就是要 DLSS5 那套运行时。
/// </para>
///
/// <para>
/// 实测（用户真机目录）：DLSS5 插件要 <c>nvngx_dlssnr.dll</c>（神经渲染运行时，165 MB）
/// 加上一整套 <c>sl.*.dll</c>（Streamline）。少了前者插件加载不了，少了后者大概率不出画面。
/// </para>
/// </summary>
public static class DlssDllRequirements
{
    public const string Dlss5Tag = "dlss5";

    /// <summary>DLSS5 的神经渲染运行时 —— 没有它插件起不来</summary>
    public static readonly DllRequirement DlssNr = new(
        ["nvngx_dlssnr.dll"],
        DllRequirementLevel.Required,
        "DLSS5 的神经渲染运行时（nvngx_dlssnr.dll）");

    /// <summary>Streamline —— 缺了可能不出画面（但不至于加载不了）</summary>
    public static readonly DllRequirement Streamline = new(
        ["sl.interposer.dll", "sl.dlss_nr.dll"],
        DllRequirementLevel.Recommended,
        "Streamline 运行时（sl.interposer.dll / sl.dlss_nr.dll）");

    public static bool IsDlss5(IEnumerable<string>? tags) =>
        tags?.Any(t => string.Equals(t?.Trim(), Dlss5Tag, StringComparison.OrdinalIgnoreCase)) == true;

    /// <summary>这个插件要哪些 dll（不是 DLSS5 类就返回空）</summary>
    public static IReadOnlyList<DllRequirement> For(IEnumerable<string>? tags) =>
        IsDlss5(tags) ? [DlssNr, Streamline] : [];
}

/// <summary>拿插件目录里的文件名去核对要求</summary>
public static class AddonDllChecker
{
    public static AddonDllStatus Check(string? addonsDirectory, IEnumerable<string>? tags)
    {
        if (string.IsNullOrWhiteSpace(addonsDirectory) || !Directory.Exists(addonsDirectory))
        {
            return new AddonDllStatus([], []);
        }

        try
        {
            return CheckFiles(Directory.EnumerateFiles(addonsDirectory).Select(Path.GetFileName)!, tags);
        }
        catch
        {
            return AddonDllStatus.Ok;
        }
    }

    public static AddonDllStatus CheckFiles(IEnumerable<string?> fileNames, IEnumerable<string>? tags)
    {
        IReadOnlyList<DllRequirement> requirements = DlssDllRequirements.For(tags);
        if (requirements.Count == 0)
        {
            return AddonDllStatus.Ok;
        }

        var files = new HashSet<string>(
            fileNames.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => f!),
            StringComparer.OrdinalIgnoreCase);

        var missingRequired = new List<DllRequirement>();
        var missingRecommended = new List<DllRequirement>();

        foreach (DllRequirement requirement in requirements)
        {
            if (requirement.Files.All(files.Contains))
            {
                continue;
            }

            (requirement.Level == DllRequirementLevel.Required ? missingRequired : missingRecommended).Add(requirement);
        }

        return new AddonDllStatus(missingRequired, missingRecommended);
    }
}
