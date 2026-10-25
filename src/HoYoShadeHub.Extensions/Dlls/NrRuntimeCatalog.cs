namespace HoYoShadeHub.Extensions.Dlls;

/// <summary>用户提供的 NR 构建；PE 版本相同，变体身份必须由内容确认。</summary>
public static class NrRuntimeCatalog
{
    public const string SfVersion = "310.9.16-SF";
    public const string F5Version = "310.9.16-F5";
    public const string GenshinV2PresetA3000 = "原神V2 预设A_3000";
    public const string GenshinV23000 = "原神V2 3000";
    public static IReadOnlyList<DllComponent> Components =>
        [.. DllFingerprintCatalog.Packages.Where(p => p.Family.Equals("dlssnr", StringComparison.OrdinalIgnoreCase))];

    public static bool HasKnownSize(long size) => DllFingerprintCatalog.Fingerprints.Any(f =>
        f.Family.Equals("dlssnr", StringComparison.OrdinalIgnoreCase) && f.Size == size);

    public static string? Identify(long size, string sha256) =>
        DllFingerprintCatalog.Find("dlssnr", size, sha256)?.Version;

}

/// <summary>所有自动下载入口共用的默认选择；显式选择由调用方保留。</summary>
public static class DllSelectionPolicy
{
    public static DllComponent? SelectDefault(DllFamily family, IReadOnlyList<DllComponent> components, string? gpuClass)
    {
        if (family.Id.Equals("dlssnr", StringComparison.OrdinalIgnoreCase))
        {
            string version = gpuClass?.ToLowerInvariant() is "turing" or "ampere"
                ? NrRuntimeCatalog.F5Version : NrRuntimeCatalog.SfVersion;
            // 缺少适合的构建时也不要把 20/30 特化版默认给其他显卡。
            return components.FirstOrDefault(c => c.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
                ?? components.FirstOrDefault(c => c.Version.Equals(NrRuntimeCatalog.SfVersion, StringComparison.OrdinalIgnoreCase));
        }

        return components.FirstOrDefault(c => c.Version.Equals(family.PreferredVersion, StringComparison.OrdinalIgnoreCase))
            ?? components.FirstOrDefault();
    }
}
