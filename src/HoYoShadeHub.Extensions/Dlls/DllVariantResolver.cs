using System.Security.Cryptography;

namespace HoYoShadeHub.Extensions.Dlls;

/// <summary>盘上这份运行时 dll 是哪个变体（认不出来时 <see cref="Variant"/> 是 null）</summary>
public sealed record DllVariantProbe(string? Variant, string? PeVersion, long Size, string Reason)
{
    /// <summary>能显示给用户的那一版：认得出来用变体名，认不出来只有 PE 版本号</summary>
    public string Display => !string.IsNullOrWhiteSpace(Variant)
        ? Variant!
        : DllVersion.Normalize(PeVersion);

    public bool Known => !string.IsNullOrWhiteSpace(Variant);
}

/// <summary>
/// 「盘上这份 <c>nvngx_dlssnr.dll</c> 是哪个变体」的判断。
///
/// <para>
/// 为什么要专门做这个：PE 版本号里**没有变体信息**，甚至对不上清单里的版本号 ——
/// Lecram 那份的 PE 是 <c>310.8.3.0</c>，清单里写的是 <c>310.8.Lecram</c>。
/// 只比数字段的 <see cref="DllVersion.SameNumbers"/> 在它身上必然失败，
/// 于是界面回退到「清单里没备注的那一条」（310.8.0）——
/// 用户看到的就成了「下的明明是 Lecram，识别成 50 系」。
/// </para>
///
/// <para>
/// 判据优先级：记账 + **字节数一致** → 归档里同名同大小的那一份 → 记账且数字段对得上 → 认不出来（老实说，别瞎猜）。
/// </para>
/// </summary>
public static class DllVariantResolver
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,
        (long Size, long Modified, string Hash)> HashCache = new(StringComparer.OrdinalIgnoreCase);
    /// <param name="store">运行时 dll 归档库（可以为 null，那就只能用记账判断）</param>
    /// <param name="family">哪一类 dll</param>
    /// <param name="installed">这一类在盘上扫到的那一份（带大小和 PE 版本号）</param>
    /// <param name="recordedVariant">装的时候记下的变体（见 AppConfig.GetInstalledDllVariant）</param>
    /// <param name="recordedSize">装的时候那一份的字节数（没有就是 0）</param>
    public static DllVariantProbe Identify(
        DllVersionStore? store,
        DllFamily family,
        InstalledDll? installed,
        string? recordedVariant,
        long recordedSize)
    {
        if (installed is null)
        {
            return new DllVariantProbe(null, null, 0, "还没装");
        }

        bool nrFile = family.Id.Equals("dlssnr", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(installed.Path);
        if (!string.IsNullOrWhiteSpace(installed.Path)
            && DllFingerprintCatalog.Fingerprints.Any(f => f.Family.Equals(family.Id, StringComparison.OrdinalIgnoreCase)
                && f.Size == installed.Size))
        {
            var fingerprint = DllFingerprintCatalog.Find(family.Id, installed.Size, TrySha256(installed.Path!));
            if (fingerprint is not null)
            {
                if (!fingerprint.FileName.Equals(installed.FileName, StringComparison.OrdinalIgnoreCase)
                    || !DllFingerprintCatalog.ValidatePe(installed.Path!, fingerprint, out _))
                    return new DllVariantProbe(null, installed.Version, installed.Size, "云端指纹的 PE 校验未通过");
                return new DllVariantProbe(fingerprint.Version, installed.Version, installed.Size, "云端 SHA256 + PE 校验一致");
            }
        }

        // 1) 装的时候记过账，而且盘上这份字节数和记账时一样 —— 就是它
        if (!nrFile && IsRecordedMatch(recordedVariant, installed.Version, allowVariantSuffix: true)
            && recordedSize > 0 && installed.Size == recordedSize)
        {
            return new DllVariantProbe(recordedVariant, installed.Version, installed.Size, "记账 + 字节数一致");
        }

        // 2) 归档里按「同名同大小」找：归档是装的时候留的副本，能反过来认人
        if (store is not null)
        {
            List<string> bySize = [.. store.ListVersions(family.Id)
                .Where(v => store.FileSize(family.Id, v.Version, installed.FileName) == installed.Size)
                .Where(v => nrFile || IsRecordedMatch(v.Version, installed.Version, allowVariantSuffix: true))
                .Select(v => v.Version)];

            // 归档目录名可能来自旧清单，大小相同也不能单独证明是同一份。
            // 有真实路径时再比内容哈希；这样旧的错误记账不会把新 NR 伪装成 50xx。
            if (bySize.Count > 0 && !string.IsNullOrWhiteSpace(installed.Path))
            {
                string installedHash = TrySha256(installed.Path!);
                bySize = installedHash.Length == 0 ? [] : [.. bySize.Where(v =>
                        string.Equals(installedHash,
                            TrySha256(store.FilePath(family.Id, v, installed.FileName)),
                            StringComparison.OrdinalIgnoreCase))];
            }

            if (bySize.Count == 1)
            {
                return new DllVariantProbe(bySize[0], installed.Version, installed.Size, "归档里同名同大小的只有这一份");
            }

            if (bySize.Count > 1)
            {
                // 多份候选：先看记账那个在不在里面，再看谁的数字段对得上
                string? pick = bySize.FirstOrDefault(v => DllVersion.IsSame(v, recordedVariant));

                if (pick is null && nrFile)
                    return new DllVariantProbe(null, installed.Version, installed.Size, "多个归档标签指向相同内容，无法确定变体名");

                string reason = pick is null
                    ? $"归档里有 {bySize.Count} 份同大小，说不准是哪一份，先按 {bySize[0]} 显示"
                    : "归档里有几份同大小，按记账 / 数字段挑了一份";

                return new DllVariantProbe(pick ?? bySize[0], installed.Version, installed.Size, reason);
            }
        }

        // 3) 记过账但字节数变了（用户自己换过文件）—— 只有数字段对得上才敢继续信
        if (!nrFile && IsRecordedMatch(recordedVariant, installed.Version))
        {
            return new DllVariantProbe(recordedVariant, installed.Version, installed.Size, "记账（字节数变了，数字段对得上）");
        }

        return new DllVariantProbe(null, installed.Version, installed.Size, "PE 版本号里没有变体信息，认不出来");
    }

    private static bool IsRecordedMatch(string? recorded, string? peVersion, bool allowVariantSuffix = false)
    {
        if (string.IsNullOrWhiteSpace(recorded))
        {
            return false;
        }

        // 变体名（Lecram / SF / RTX40）本来就不写进 PE 版本；内容证据或同大小记账足以确认。
        // 带硬件说明的数字标签（例如 "310.8.3 (50xx)"）必须先和 PE 数字段一致，
        // 否则 310.9.16 这类新 NR 会被旧记录错误认成 50xx。
        if (recorded.Contains('(') || recorded.Contains(')'))
        {
            return DllVersion.SameNumbers(recorded, peVersion);
        }

        bool hasVariantSuffix = recorded.Any(char.IsLetter);
        return hasVariantSuffix && allowVariantSuffix
            ? DllVersion.SameNumericPrefix(recorded, peVersion)
            : !hasVariantSuffix && DllVersion.SameNumbers(recorded, peVersion);
    }

    private static string TrySha256(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return string.Empty;
        }

        try
        {
            var info = new FileInfo(path);
            string key = info.FullName;
            if (HashCache.TryGetValue(key, out var cached)
                && cached.Size == info.Length && cached.Modified == info.LastWriteTimeUtc.Ticks)
                return cached.Hash;
            using FileStream stream = File.OpenRead(path);
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            if (HashCache.Count > 128) HashCache.Clear();
            HashCache[key] = (info.Length, info.LastWriteTimeUtc.Ticks, hash);
            return hash;
        }
        catch
        {
            return string.Empty;
        }
    }
}
