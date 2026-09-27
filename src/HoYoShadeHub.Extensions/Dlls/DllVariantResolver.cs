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

        // 1) 装的时候记过账，而且盘上这份字节数和记账时一样 —— 就是它
        if (!string.IsNullOrWhiteSpace(recordedVariant) && recordedSize > 0 && installed.Size == recordedSize)
        {
            return new DllVariantProbe(recordedVariant, installed.Version, installed.Size, "记账 + 字节数一致");
        }

        // 2) 归档里按「同名同大小」找：归档是装的时候留的副本，能反过来认人
        if (store is not null)
        {
            List<string> bySize = [.. store.ListVersions(family.Id)
                .Where(v => store.FileSize(family.Id, v.Version, installed.FileName) == installed.Size)
                .Select(v => v.Version)];

            if (bySize.Count == 1)
            {
                return new DllVariantProbe(bySize[0], installed.Version, installed.Size, "归档里同名同大小的只有这一份");
            }

            if (bySize.Count > 1)
            {
                // 多份候选：先看记账那个在不在里面，再看谁的数字段对得上
                string? pick = bySize.FirstOrDefault(v => DllVersion.IsSame(v, recordedVariant))
                               ?? bySize.FirstOrDefault(v => DllVersion.SameNumbers(v, installed.Version));

                string reason = pick is null
                    ? $"归档里有 {bySize.Count} 份同大小，说不准是哪一份，先按 {bySize[0]} 显示"
                    : "归档里有几份同大小，按记账 / 数字段挑了一份";

                return new DllVariantProbe(pick ?? bySize[0], installed.Version, installed.Size, reason);
            }
        }

        // 3) 记过账但字节数变了（用户自己换过文件）—— 只有数字段对得上才敢继续信
        if (!string.IsNullOrWhiteSpace(recordedVariant) && DllVersion.SameNumbers(recordedVariant, installed.Version))
        {
            return new DllVariantProbe(recordedVariant, installed.Version, installed.Size, "记账（字节数变了，数字段对得上）");
        }

        return new DllVariantProbe(null, installed.Version, installed.Size, "PE 版本号里没有变体信息，认不出来");
    }
}
