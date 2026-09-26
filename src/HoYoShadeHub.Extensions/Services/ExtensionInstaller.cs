using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>一条「哪个源文件 → 落到 HoYoShade 下哪个位置」的映射</summary>
public sealed record ExtensionPlanItem(string SourceFile, string RelativePath, string RuleMatch);

/// <summary>安装结果</summary>
public sealed class ExtensionInstallResult
{
    public required string ExtensionId { get; init; }
    public string? Version { get; init; }
    public string? ResolvedTag { get; init; }
    public List<string> InstalledFiles { get; init; } = [];
    public List<string> OverwrittenFiles { get; init; } = [];

    /// <summary>
    /// 装新版本时顺手清掉的**同族旧文件**（老版本换了个文件名留在目录里，ReShade 会把两份都加载）。
    /// 用户反馈：「很容易下到 2 个一样的插件」。
    /// </summary>
    public List<string> RemovedStaleFiles { get; init; } = [];

    public List<string> Warnings { get; init; } = [];
    public string? BackupDirectory { get; set; }
    public bool ReShadeIniUpdated { get; set; }
}

/// <summary>卸载结果</summary>
public sealed class ExtensionUninstallResult
{
    public required string ExtensionId { get; init; }
    public List<string> DeletedFiles { get; init; } = [];

    /// <summary>文件被改动过（哈希对不上）而被保留，需要用户自己决定</summary>
    public List<string> SkippedModifiedFiles { get; init; } = [];

    /// <summary>账本里有但磁盘上已经不在了</summary>
    public List<string> MissingFiles { get; init; } = [];

    /// <summary>本来就是覆盖别人文件的条目，卸载时只摘账本不删文件</summary>
    public List<string> KeptOverwrittenFiles { get; init; } = [];

    /// <summary>同族的其它文件（别的版本 / 被改名禁用的 .addon64x）也一起删掉了</summary>
    public List<string> DeletedStaleFiles { get; init; } = [];

    /// <summary>没拦住的非致命问题（比如文件被游戏占用删不掉），卸载本身照常完成、账本照常摘</summary>
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// 扩展的安装 / 卸载。
///
/// 设计要点：
/// <list type="bullet">
/// <item>安装时给每个文件算 sha256 记进账本，卸载逐个校验后再删 —— 绝不整目录删；</item>
/// <item>覆盖到「不是本次安装产生的」已有文件时先备份到 <c>.hysx\backup\</c>；</item>
/// <item>装 addon 时顺手补齐 ReShade.ini 的 <c>[ADDON] AddonPath=</c>，否则 ReShade 扫不到。</item>
/// </list>
/// </summary>
public sealed class ExtensionInstaller
{
    private readonly InstalledExtensionStore _store;

    public ExtensionInstaller(InstalledExtensionStore store)
    {
        _store = store;
    }

    /// <summary>
    /// 把载荷里的文件按 rules 映射成落位计划。
    /// 规则按顺序匹配，第一条命中的生效。
    /// </summary>
    public static List<ExtensionPlanItem> BuildPlan(ResolvedExtensionPayload payload, ExtensionManifest manifest)
    {
        var plan = new List<ExtensionPlanItem>();

        foreach (string relative in EnumeratePayloadFiles(payload))
        {
            foreach (ExtensionFileRule rule in manifest.Rules)
            {
                if (!GlobMatcher.IsMatch(rule.Match, relative))
                {
                    continue;
                }

                string destination = BuildDestination(relative, rule);
                if (!string.IsNullOrWhiteSpace(destination))
                {
                    plan.Add(new ExtensionPlanItem(relative, destination, rule.Match));
                }
                break;
            }
        }

        return plan;
    }

    /// <summary>载荷里的文件列表；单文件来源会把资产名当成虚拟相对路径</summary>
    public static IEnumerable<string> EnumeratePayloadFiles(ResolvedExtensionPayload payload)
    {
        if (!string.IsNullOrWhiteSpace(payload.SingleFileName))
        {
            yield return payload.SingleFileName;
            yield break;
        }

        foreach (string file in Directory.EnumerateFiles(payload.PayloadRoot, "*", SearchOption.AllDirectories))
        {
            yield return Path.GetRelativePath(payload.PayloadRoot, file).Replace('\\', '/');
        }
    }

    public static string PayloadFileToAbsolutePath(ResolvedExtensionPayload payload, string relativePath)
    {
        if (!string.IsNullOrWhiteSpace(payload.SingleFileName))
        {
            return Path.Combine(payload.PayloadRoot, payload.SingleFileName);
        }
        return Path.Combine(payload.PayloadRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string BuildDestination(string relativePath, ExtensionFileRule rule)
    {
        string fileName = Path.GetFileName(relativePath);

        if (!string.IsNullOrWhiteSpace(rule.Rename))
        {
            fileName = rule.Rename.Contains('.') ? rule.Rename : rule.Rename + Path.GetExtension(fileName);
        }

        string tail = rule.Flatten
            ? fileName
            : StripGlobPrefix(relativePath, rule.Match);

        if (!rule.Flatten && !string.IsNullOrWhiteSpace(rule.Rename))
        {
            string? directory = Path.GetDirectoryName(tail.Replace('/', Path.DirectorySeparatorChar));
            tail = string.IsNullOrEmpty(directory)
                ? fileName
                : Path.Combine(directory, fileName).Replace('\\', '/');
        }

        string folder = ShadeHost.FolderToRelative(rule.To ?? string.Empty);
        return string.IsNullOrEmpty(folder)
            ? ShadeHost.FolderToRelative(tail)
            : $"{folder}/{ShadeHost.FolderToRelative(tail)}";
    }

    /// <summary>去掉 glob 里第一个通配符之前的字面前缀，剩下的是「逻辑根目录」下的相对路径</summary>
    public static string StripGlobPrefix(string relativePath, string glob)
    {
        string[] segments = glob.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var prefix = new List<string>();

        foreach (string segment in segments)
        {
            if (segment.Contains('*') || segment.Contains('?'))
            {
                break;
            }
            prefix.Add(segment);
        }

        if (prefix.Count == 0)
        {
            return relativePath;
        }

        string prefixPath = string.Join('/', prefix) + "/";
        return relativePath.StartsWith(prefixPath, StringComparison.OrdinalIgnoreCase)
            ? relativePath[prefixPath.Length..]
            : relativePath;
    }

    /// <summary>
    /// 执行安装。
    /// </summary>
    /// <exception cref="FileNotFoundException">必填规则没有匹配到任何文件</exception>
    /// <exception cref="InvalidOperationException">目标不是有效的 HoYoShade 目录 / 路径越界</exception>
    public async Task<ExtensionInstallResult> InstallAsync(
        ShadeHost host,
        ExtensionManifest manifest,
        ResolvedExtensionPayload payload,
        CancellationToken cancellationToken = default,
        AddonVersionStore? versionStore = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(payload);

        if (!host.Exists)
        {
            throw new InvalidOperationException($"{host.RootPath} 不是有效的 HoYoShade 目录（找不到 ReShade64.dll）");
        }

        var plan = BuildPlan(payload, manifest);
        ValidateRequiredRules(manifest, plan);

        if (plan.Count == 0)
        {
            throw new FileNotFoundException($"扩展 {manifest.Id} 的所有规则都没有匹配到文件，载荷可能不是预期的包");
        }

        var ledger = await _store.LoadAsync(cancellationToken);

        // 互斥：同一件事的不同实现同时装进一个 ReShade 进程会打架
        if (manifest.ConflictsWith is { Length: > 0 } conflictsWith)
        {
            string[] hits = [.. ledger.Extensions
                .Select(e => e.Id)
                .Where(id => conflictsWith.Contains(id, StringComparer.OrdinalIgnoreCase))];

            if (hits.Length > 0)
            {
                throw new InvalidOperationException(
                    $"「{manifest.Name}」与已安装的 {string.Join("、", hits)} 互斥，请先卸载其中之一。");
            }
        }

        // 同一个文件被别的扩展占了 -> 直接拒绝，避免两个扩展互相覆盖
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (InstalledExtension other in ledger.Extensions.Where(
                     e => !string.Equals(e.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var file in other.Files)
            {
                claimed[file.Path] = other.Id;
            }
        }

        var conflicts = plan
            .Select(p => p.RelativePath)
            .Where(claimed.ContainsKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (conflicts.Length > 0)
        {
            string detail = string.Join(", ", conflicts.Take(5).Select(p => $"{p} (被 {claimed[p]} 占用)"));
            throw new InvalidOperationException(
                $"目标文件已被其它扩展占用：{detail}{(conflicts.Length > 5 ? " …" : "")}");
        }

        // 同族旧文件：账本里上次装的那些（这次计划里没有的）+ 插件目录里同一个 slug 的其它文件。
        // 留着的话 ReShade 会把两份 addon 都加载进来（用户反馈「很容易下到 2 个一样的插件」）。
        var staleFiles = new List<string>();
        InstalledExtension? previous = ledger.Extensions.FirstOrDefault(
            e => string.Equals(e.Id, manifest.Id, StringComparison.OrdinalIgnoreCase));

        if (previous is not null)
        {
            var planned = new HashSet<string>(plan.Select(p => p.RelativePath), StringComparer.OrdinalIgnoreCase);
            staleFiles.AddRange(previous.Files
                .Select(x => x.Path)
                .Where(p => !planned.Contains(p)));
        }

        staleFiles.AddRange(FindSiblingAddons(
            host,
            plan.Select(p => p.RelativePath),
            plan.Select(p => p.RelativePath)));
        staleFiles = [.. staleFiles.Distinct(StringComparer.OrdinalIgnoreCase)];

        string? backupDirectory = null;
        var record = new InstalledExtension
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Version = manifest.Version,
            InstalledAt = DateTimeOffset.Now,
            Source = manifest.Source,
            ResolvedTag = payload.ResolvedTag,
        };

        var result = new ExtensionInstallResult
        {
            ExtensionId = manifest.Id,
            Version = manifest.Version,
            ResolvedTag = payload.ResolvedTag,
        };

        try
        {
            foreach (ExtensionPlanItem item in plan)
            {
                cancellationToken.ThrowIfCancellationRequested();

                string source = PayloadFileToAbsolutePath(payload, item.SourceFile);
                string destination = host.ResolveRelative(item.RelativePath);

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

                bool existedBefore = File.Exists(destination);
                if (existedBefore)
                {
                    // 已经是我们自己上次装的同名文件，直接覆盖，不用备份
                    bool ownedByUs = ledger.Extensions
                        .Where(e => string.Equals(e.Id, manifest.Id, StringComparison.OrdinalIgnoreCase))
                        .SelectMany(e => e.Files)
                        .Any(f => string.Equals(f.Path, item.RelativePath, StringComparison.OrdinalIgnoreCase));

                    if (!ownedByUs)
                    {
                        backupDirectory ??= Path.Combine(
                            host.BackupPath, Sanitize(manifest.Id), DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                        string backupTarget = Path.Combine(
                            backupDirectory, item.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(backupTarget)!);
                        File.Copy(destination, backupTarget, overwrite: true);
                    }

                    result.OverwrittenFiles.Add(item.RelativePath);
                }

                // 用「临时文件 + 原子替换」写入。直接 File.Copy 覆盖会**保留目标文件的 inode**，
                // 于是指向它的硬链接（版本归档 / 每游戏插件包）会跟着被改掉 —— 老版本就白留了。
                string staged = destination + ".hysx-new";
                try
                {
                    File.Copy(source, staged, overwrite: true);
                    File.Move(staged, destination, overwrite: true);
                }
                finally
                {
                    // copy/move 半路失败（磁盘满 / 目标被游戏锁着）时别把 .hysx-new 留在 Addons 目录里
                    if (File.Exists(staged))
                    {
                        try
                        {
                            File.Delete(staged);
                        }
                        catch
                        {
                            // 删不掉就算了，下次安装会覆盖
                        }
                    }
                }

                var info = new FileInfo(destination);
                record.Files.Add(new InstalledExtension.InstalledExtensionFile
                {
                    Path = item.RelativePath,
                    Size = info.Length,
                    Sha256 = await HysxUtil.ComputeSha256Async(destination, cancellationToken),
                    Created = !existedBefore,
                });

                result.InstalledFiles.Add(item.RelativePath);
            }

            // addon 必须让 ReShade 知道去哪扫
            if (record.Files.Any(f => LooksLikeAddon(f.Path)))
            {
                if (ReShadeIniService.EnsureAddonPath(host))
                {
                    result.ReShadeIniUpdated = true;
                }
                else if (!ReShadeIniService.HasUsableAddonPath(host))
                {
                    result.Warnings.Add(
                        "ReShade.ini 里没有可用的 [ADDON] AddonPath，这个 addon 可能不会被加载。");
                }
            }

            await _store.UpsertAsync(record, cancellationToken);

            // 装好的这一版再归档一份（<CacheRoot>\plugins\<extId>\<tag>\）——
            // 共享 Addons 目录永远只有一份当前版本，归档留给「反复切版本不用重新下」和
            // 「每个游戏用不同版本」的插件包。归档失败不该让安装失败。
            if (versionStore is not null)
            {
                try
                {
                    versionStore.Archive(host, record);
                }
                catch
                {
                    // ignore
                }
            }

            var previousHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (previous is not null)
            {
                foreach (var old in previous.Files)
                {
                    if (!string.IsNullOrWhiteSpace(old.Sha256))
                    {
                        previousHashes[old.Path] = old.Sha256;
                    }
                }
            }

            foreach (string stale in staleFiles)
            {
                try
                {
                    string path = host.ResolveRelative(stale);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    // 账本里记过的旧文件：先核哈希，用户改过的不能删
                    if (previousHashes.TryGetValue(stale, out string? recorded))
                    {
                        string actual = await HysxUtil.ComputeSha256Async(path, cancellationToken);
                        if (!HysxUtil.IsHashEqual(actual, recorded))
                        {
                            result.Warnings.Add($"同族旧文件 {stale} 被改过，保留没删。");
                            continue;
                        }
                    }

                    File.Delete(path);
                    result.RemovedStaleFiles.Add(stale);
                }
                catch
                {
                    // 删不掉就算了，不要因为清理失败让安装报错
                }
            }
        }
        catch
        {
            // 装到一半失败：把这次已经落地的文件回滚掉，账本不动
            RollbackPartialInstall(host, record);
            throw;
        }

        result.BackupDirectory = backupDirectory;
        return result;
    }

    /// <summary>卸载：逐个文件校验 sha256 后再删</summary>
    /// <param name="removeSiblings">
    /// 连**同族**的其它文件一起删（别的版本、被改名禁用的 <c>.addon64x</c>）。
    /// 用户反馈「删插件却没法删除全部」—— 界面上的删除按钮就传 true。
    /// </param>
    public async Task<ExtensionUninstallResult> UninstallAsync(
        ShadeHost host,
        string extensionId,
        bool removeSiblings = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(host);

        var ledger = await _store.LoadAsync(cancellationToken);
        InstalledExtension? record = ledger.Extensions
            .FirstOrDefault(e => string.Equals(e.Id, extensionId, StringComparison.OrdinalIgnoreCase));

        if (record is null)
        {
            throw new InvalidOperationException($"没有找到已安装的扩展 {extensionId}");
        }

        var result = new ExtensionUninstallResult { ExtensionId = record.Id };

        foreach (var file in record.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string path;
            try
            {
                path = host.ResolveRelative(file.Path);
            }
            catch (InvalidOperationException)
            {
                result.SkippedModifiedFiles.Add(file.Path);
                continue;
            }

            if (!File.Exists(path))
            {
                result.MissingFiles.Add(file.Path);
                continue;
            }

            if (!file.Created)
            {
                // 这个文件本来就存在，是别人（或官方包）的，只摘账本不动文件
                result.KeptOverwrittenFiles.Add(file.Path);
                continue;
            }

            try
            {
                string actual = await HysxUtil.ComputeSha256Async(path, cancellationToken);
                if (!HysxUtil.IsHashEqual(actual, file.Sha256))
                {
                    result.SkippedModifiedFiles.Add(file.Path);
                    continue;
                }

                File.Delete(path);
                result.DeletedFiles.Add(file.Path);
            }
            catch
            {
                // 文件被占用（游戏开着，addon 已被加载）删不掉：跳过并提醒，而不是让整个卸载炸掉 ——
                // 异常往上抛的话，后面的账本摘除永远走不到，重试也永远卡在同一个文件上
                result.Warnings.Add("文件被占用删不掉（关掉游戏再卸一次可以彻底清掉）：" + file.Path);
            }
        }

        foreach (string deleted in result.DeletedFiles)
        {
            try
            {
                string directory = Path.GetDirectoryName(host.ResolveRelative(deleted))!;
                HysxUtil.TryDeleteEmptyDirectory(directory, host.RootPath);
            }
            catch
            {
                // ignore
            }
        }

        if (removeSiblings)
        {
            // 同族文件（别的版本 / .addon64x）也一起删：不然「删了插件」目录里还剩一份
            // keep = 这个扩展**账本里的全部文件**（不是只算删掉的那些）——
            // 被用户改过而保留的文件也在账本里，绝不能再当「同族旧文件」删一遍
            foreach (string sibling in FindSiblingAddons(host, record.Files.Select(x => x.Path), record.Files.Select(x => x.Path)))
            {
                try
                {
                    string path = host.ResolveRelative(sibling);
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                        result.DeletedStaleFiles.Add(sibling);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        await _store.RemoveAsync(record.Id, cancellationToken);
        return result;
    }

    /// <summary>
    /// 找出「同一个插件、别的文件」：按 addon 文件名解析出的 slug 比，同一个 slug 的其它
    /// <c>.addon64 / .addon64x</c> 都算。只扫这些 addon 所在的目录，不扫全盘。
    /// </summary>
    private static List<string> FindSiblingAddons(
        ShadeHost host,
        IEnumerable<string> addonRelativePaths,
        IEnumerable<string> keepRelativePaths)
    {
        var result = new List<string>();
        var slugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string relative in addonRelativePaths)
        {
            AddonFileInfo? info = AddonFileInfo.Parse(Path.GetFileName(relative));
            if (info?.IsAddon == true && !string.IsNullOrWhiteSpace(info.Slug))
            {
                slugs.Add(info.Slug);
                directories.Add((Path.GetDirectoryName(relative) ?? string.Empty).Replace('\\', '/'));
            }
        }

        if (slugs.Count == 0)
        {
            return result;
        }

        var keep = new HashSet<string>(
            keepRelativePaths.Select(NormalizeRelative),
            StringComparer.OrdinalIgnoreCase);

        foreach (string directory in directories)
        {
            string absolute;
            try
            {
                absolute = host.ResolveRelative(directory);
            }
            catch
            {
                continue;
            }

            if (!Directory.Exists(absolute))
            {
                continue;
            }

            foreach (AddonFileInfo file in AddonFileInfo.ScanDirectory(absolute))
            {
                if (file.Slug is null || !slugs.Contains(file.Slug))
                {
                    continue;
                }

                string relative = NormalizeRelative(
                    string.IsNullOrEmpty(directory) ? file.FileName : $"{directory}/{file.FileName}");

                if (!keep.Contains(relative))
                {
                    result.Add(relative);
                }
            }
        }

        return result;
    }

    private static string NormalizeRelative(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static void ValidateRequiredRules(ExtensionManifest manifest, List<ExtensionPlanItem> plan)
    {
        foreach (ExtensionFileRule rule in manifest.Rules.Where(r => !r.Optional))
        {
            if (!plan.Any(p => string.Equals(p.RuleMatch, rule.Match, StringComparison.OrdinalIgnoreCase)))
            {
                throw new FileNotFoundException(
                    $"扩展 {manifest.Id} 的必需规则 \"{rule.Match}\" 没有匹配到任何文件，安装中止。");
            }
        }
    }

    private static void RollbackPartialInstall(ShadeHost host, InstalledExtension record)
    {
        foreach (var file in record.Files)
        {
            try
            {
                string path = host.ResolveRelative(file.Path);
                if (file.Created && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static bool LooksLikeAddon(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".addon64", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".addon32", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".addon", StringComparison.OrdinalIgnoreCase);
    }

    private static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]);
    }
}
