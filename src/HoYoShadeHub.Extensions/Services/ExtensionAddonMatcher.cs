using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// 把 addons 目录里的文件认领给各个扩展条目。
///
/// <para>
/// 为什么需要：账本（<c>.hysx\installed.json</c>）只记 Hub 自己装进去的东西。
/// 用户从 Discord / GitHub 手动放进去的插件，Hub 完全不知情 ——
/// 全局插件页会显示成「没装」，也就没有全局开关可用。
/// </para>
///
/// <para>
/// 认领规则：扩展条目上写的 <see cref="ExtensionManifest.AddonPatterns"/>（glob）。
/// 一个文件只会被**第一个**匹配上的条目认领，避免两个条目抢同一个文件。
/// </para>
/// </summary>
public static class ExtensionAddonMatcher
{
    /// <summary>
    /// 返回「扩展 id → 认领到的文件」。没写 <c>addonPatterns</c> 的条目不会出现在结果里。
    /// </summary>
    public static Dictionary<string, List<AddonFileInfo>> Match(
        IEnumerable<ExtensionManifest> manifests,
        IEnumerable<AddonFileInfo> files)
    {
        var result = new Dictionary<string, List<AddonFileInfo>>(StringComparer.OrdinalIgnoreCase);
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<AddonFileInfo> all = [.. files];

        foreach (ExtensionManifest manifest in manifests)
        {
            if (manifest.AddonPatterns is not { Length: > 0 })
            {
                continue;
            }

            var owned = new List<AddonFileInfo>();

            foreach (AddonFileInfo file in all)
            {
                if (claimed.Contains(file.FileName))
                {
                    continue;
                }

                if (manifest.AddonPatterns.Any(p => GlobMatcher.IsMatch(p, file.FileName)))
                {
                    claimed.Add(file.FileName);
                    owned.Add(file);
                }
            }

            if (owned.Count > 0)
            {
                result[manifest.Id] = owned;
            }
        }

        return result;
    }

    /// <summary>
    /// 只认一个文件：返回认领它的扩展 id（没有条目认领就是 null）。
    /// 给「这个 addon 属于哪个扩展」用（每游戏插件版本下拉靠它把 addon 文件对到扩展的版本归档）。
    /// </summary>
    public static string? MatchExtensionId(IEnumerable<ExtensionManifest> manifests, AddonFileInfo file)
    {
        if (file is null || string.IsNullOrWhiteSpace(file.FileName))
        {
            return null;
        }

        foreach (ExtensionManifest manifest in manifests)
        {
            if (manifest.AddonPatterns is not { Length: > 0 })
            {
                continue;
            }

            if (manifest.AddonPatterns.Any(p => GlobMatcher.IsMatch(p, file.FileName)))
            {
                return manifest.Id;
            }
        }

        return null;
    }

    /// <summary>只认一个文件名：返回认领它的扩展 id（文件名解析不出来 / 没条目认领就是 null）。</summary>
    public static string? MatchExtensionId(IEnumerable<ExtensionManifest> manifests, string addonFileName)
        => AddonFileInfo.Parse(addonFileName) is { } file ? MatchExtensionId(manifests, file) : null;
}
