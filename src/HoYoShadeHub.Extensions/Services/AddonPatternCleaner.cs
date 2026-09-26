namespace HoYoShadeHub.Extensions.Services;

/// <summary>按扩展的 <c>addonPatterns</c> 清同名文件的结果</summary>
public sealed record AddonPatternCleanResult(
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Failed)
{
    public int DeletedCount => Deleted.Count;

    public int FailedCount => Failed.Count;
}

/// <summary>
/// 按扩展声明的 <see cref="GlobMatcher"/> 模式（<c>addonPatterns</c>）在共享 Addons 目录里
/// 把**同族**文件全删掉。
///
/// <para>
/// 为什么需要：用户反馈「删掉的插件（Super Anus）删了又出现」。只删账本 / 当前认领到的那几个
/// 名字会漏 —— 同一个插件的其它变体（<c>.addon64x</c> 被改名禁用的、带版本号
/// <c>xxx(1.2.3).addon64</c>、不带版本号的）还在目录里，下次刷新又被 addonPatterns 认领回来。
/// 所以删除时要**按模式**扫一遍，用 <see cref="GlobMatcher"/> 判断（不要自己写前缀比较）。
/// </para>
///
/// <para>
/// 只扫共享 Addons 目录这一层（和认领逻辑一致）；每游戏插件包里的副本由
/// <c>GameAddonPack.Sync</c> 重拼时清掉。
/// </para>
/// </summary>
public static class AddonPatternCleaner
{
    /// <summary>
    /// 删掉 <paramref name="addonsDirectory"/> 里所有匹配 <paramref name="patterns"/> 的文件。
    /// </summary>
    /// <param name="alreadyDeleted">已经删过的文件名（调用方刚删的那批），不重复计入。</param>
    public static AddonPatternCleanResult DeleteMatching(
        string? addonsDirectory,
        IEnumerable<string>? patterns,
        IEnumerable<string>? alreadyDeleted = null)
    {
        var deleted = new List<string>();
        var failed = new List<string>();

        try
        {
            if (string.IsNullOrWhiteSpace(addonsDirectory) || !Directory.Exists(addonsDirectory))
            {
                return new AddonPatternCleanResult(deleted, failed);
            }

            List<string> normalized = [.. (patterns ?? [])
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)];

            if (normalized.Count == 0)
            {
                return new AddonPatternCleanResult(deleted, failed);
            }

            var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in alreadyDeleted ?? [])
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    skip.Add(Path.GetFileName(name));
                }
            }

            foreach (string file in Directory.EnumerateFiles(addonsDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(file);
                if (name.Length == 0 || skip.Contains(name))
                {
                    continue;
                }

                if (!normalized.Any(p => GlobMatcher.IsMatch(p, name)))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    deleted.Add(name);
                }
                catch
                {
                    failed.Add(name);
                }
            }
        }
        catch
        {
            // 目录读不了就什么都不做
        }

        return new AddonPatternCleanResult(deleted, failed);
    }
}
