using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Features.OptiScaler;

/// <summary>
/// OptiScaler「配置预设」的本地仓：<c>&lt;OptiScaler 根&gt;\presets\&lt;名字&gt;.ini</c>。
///
/// <para>
/// 一个预设就是一份完整的 <c>OptiScaler.ini</c>。套用它 = 把内容写进该构建的
/// <c>profiles\&lt;游戏&gt;.ini</c> 再 Activate 成主 ini（和启动游戏时走的是同一条路）。
/// </para>
///
/// <para>
/// 放在 OptiScaler 根目录下是为了让用户能直接看到 / 备份 / 手动互传  用户要求「可切换、
/// 可修改、可新增、可联网下载」。
/// </para>
/// </summary>
internal static class OptiScalerPresets
{
    public const string FolderName = "presets";

    /// <summary>新增对话框里「空配置」那一项</summary>
    public const string EmptyLabel = "空配置（全部默认）";

    /// <summary>
    /// 下拉里代表「用这个游戏自己的 profiles\&lt;游戏&gt;.ini」的那一项 
    /// 用户在游戏内改完 Save 出来的就是它，选它 = 别动、保持现状。
    /// </summary>
    public const string CurrentLabel = "当前配置（游戏内保存）";

    /// <summary>预设目录；用户数据目录缺失时返回空串</summary>
    public static string Root
    {
        get
        {
            string root = AppConfig.OptiScalerRootPath;
            return string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine(root, FolderName);
        }
    }

    public static bool IsAvailable => Root.Length > 0;

    public static string PathOf(string name) => Path.Combine(Root, Sanitize(name) + ".ini");

    /// <summary>本地预设名（不带 .ini），按名字排序</summary>
    public static List<string> List()
    {
        var names = new List<string>();

        try
        {
            if (Root.Length == 0 || !Directory.Exists(Root))
            {
                return names;
            }

            foreach (string file in Directory.EnumerateFiles(Root, "*.ini"))
            {
                names.Add(Path.GetFileNameWithoutExtension(file));
            }
        }
        catch
        {
            // 读不出来就当没有
        }

        names.Sort(StringComparer.CurrentCultureIgnoreCase);
        return names;
    }

    public static string? Read(string name)
    {
        try
        {
            string path = PathOf(name);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool Save(string name, string content) => Save(name, content, null);

    /// <summary>
    /// 保存预设。<paramref name="sources"/> 非空 = 这份预设只允许用在列出的
    /// OptiScaler 来源分支上（例如只给 mfg-ada 用），元数据写在同名 .meta.json 里。
    /// </summary>
    public static bool Save(string name, string content, IReadOnlyList<string>? sources)
    {
        try
        {
            if (Root.Length == 0)
            {
                return false;
            }

            Directory.CreateDirectory(Root);

            // 内容一模一样就不动文件。游戏退出时的回写会被兜底轮询连着触发好几次，
            // 每次都写只会让配置的时间戳白跳（内容没变本来也没什么可写的）。
            string path = PathOf(name);
            if (!File.Exists(path)
                || !string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
            {
                WriteTextAtomically(path, content);
            }

            string meta = MetaPathOf(name);
            if (sources is { Count: > 0 })
            {
                WriteTextAtomically(meta, JsonSerializer.Serialize(new PresetMeta { Sources = [.. sources] }));
            }
            else if (File.Exists(meta))
            {
                File.Delete(meta);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 从文件导入：把一份现成的 ini 原样拷进预设目录。
    /// 字节级拷贝、不改编码 / BOM —— 用户互传的 ini 什么编码都有，
    /// 读出来再写一遍反而容易把里面的中文注释 / 特殊字节弄坏。
    /// </summary>
    public static bool ImportFile(string sourcePath, string name)
    {
        try
        {
            if (Root.Length == 0 || !File.Exists(sourcePath))
            {
                return false;
            }

            Directory.CreateDirectory(Root);
            File.Copy(sourcePath, PathOf(name), overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>这份预设限定的 OptiScaler 来源分支（空 = 谁都能用）</summary>
    public static List<string> GetSources(string name)
    {
        try
        {
            string meta = MetaPathOf(name);
            if (!File.Exists(meta))
            {
                return [];
            }

            PresetMeta? parsed = JsonSerializer.Deserialize<PresetMeta>(File.ReadAllText(meta));
            return parsed?.Sources ?? [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>这份预设允许用在 <paramref name="sourceId"/> 这个分支上吗</summary>
    public static bool AllowsSource(string name, string? sourceId)
    {
        List<string> sources = GetSources(name);
        if (sources.Count == 0)
        {
            return true;
        }

        return sourceId is not null && sources.Contains(sourceId, StringComparer.OrdinalIgnoreCase);
    }

    private static string MetaPathOf(string name) => Path.Combine(Root, Sanitize(name) + ".meta.json");

    private sealed class PresetMeta
    {
        [JsonPropertyName("sources")]
        public List<string>? Sources { get; set; }
    }

    public static bool Delete(string name)
    {
        try
        {
            string path = PathOf(name);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把预设套到这个游戏 + 这个构建上：写 <c>profiles\&lt;游戏&gt;.ini</c> 再激活成主 ini。
    /// </summary>
    /// <returns>false = 读取 / 写入失败</returns>
    public static bool Apply(string buildDirectory, string gameKey, string presetName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(buildDirectory) || string.IsNullOrWhiteSpace(gameKey))
            {
                return false;
            }

            string? content = Read(presetName);
            if (content is null)
            {
                return false;
            }

            if (!CollectFromRuntimeChecked(buildDirectory).Succeeded) return false;

            string profile = OptiScalerProfiles.ProfilePath(buildDirectory, gameKey);
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            if (!OptiScalerProfiles.TryWriteProfile(buildDirectory, gameKey, content)) return false;

            // 立刻生效（否则要等下次启动游戏才 Activate）
            return OptiScalerProfiles.Activate(buildDirectory, gameKey);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把构建当前生效的主 ini 存成一个预设  「新增（复制现有配置）」和「修改」都用它。
    /// </summary>
    public static bool CaptureFromBuild(string buildDirectory, string presetName)
    {
        try
        {
            string mainIni = Path.Combine(buildDirectory, "OptiScaler.ini");
            if (!File.Exists(mainIni))
            {
                return false;
            }

            // 主 ini 里的 OptiDllPath 是 Hub 启动时按本机路径钉的，别让它跟着进配置
            string content = OptiScalerRuntime.RestoreMachinePinnedKeys(File.ReadAllText(mainIni), Read(presetName));

            // 保留原有的分支限制（否则「保存当前」会把 sources 洗掉）
            return Save(presetName, content, GetSources(presetName));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>重命名预设（.ini 和同名 .meta.json 一起改，分支限制不会丢）</summary>
    public static bool Rename(string oldName, string newName)
    {
        try
        {
            string target = Sanitize(newName);
            if (string.IsNullOrWhiteSpace(target)
                || string.Equals(target, Sanitize(oldName), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string source = PathOf(oldName);
            if (!File.Exists(source))
            {
                return false;
            }

            string destination = Path.Combine(Root, target + ".ini");
            string oldMeta = MetaPathOf(oldName);
            string newMeta = Path.Combine(Root, target + ".meta.json");
            if (File.Exists(destination) || File.Exists(newMeta))
            {
                return false;
            }

            File.Move(source, destination);

            try
            {
                if (File.Exists(oldMeta)) File.Move(oldMeta, newMeta);
            }
            catch
            {
                File.Move(destination, source);
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>这个游戏当前挂着哪份「配置」（游戏退出时把改动同步回它）</summary>
    public static string FollowKey(string gameKey) => "opti_config_follow_" + gameKey;

    /// <summary>
    /// 游戏退出、profiles\<游戏>.ini 被回写之后调用：如果这个游戏当前挂着一份命名配置，
    /// 就把改动**同步回那份配置**（用户要求：选着配置进游戏改完，配置本身要跟着动）。
    /// </summary>
    /// <returns>同步成功的配置名；没挂 / 失败返回 null</returns>
    public static string? Follow(string buildDirectory, string gameKey)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(buildDirectory) || string.IsNullOrWhiteSpace(gameKey))
            {
                return null;
            }

            // 挂的是哪份配置：先看「游戏 + 构建」的下拉选择键，再退回按游戏记的 follow 键
            string name = SelectedPreset(gameKey, buildDirectory);
            if (name.Length == 0)
            {
                return null;
            }

            string source = OptiScalerProfiles.ProfilePath(buildDirectory, gameKey);
            if (!File.Exists(source))
            {
                return null;
            }

            // 回写的是运行时那份（里面的 OptiDllPath 已被 Hub 钉成本机绝对路径）：
            // 把这些机器相关的键还原成配置自己那份，配置才还是用户写的样子。
            string content = OptiScalerRuntime.RestoreMachinePinnedKeys(File.ReadAllText(source), Read(name));

            // 保留这份配置原有的分支限制（GetSources 里读的就是它自己的 .meta.json）
            return Save(name, content, GetSources(name)) ? name : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 收回上一次运行留在主 ini 里的改动：按记账存回它自己的 profile，再同步回挂着的「配置」。
    ///
    /// <para>
    /// 火箭模式专用。Hub 在火箭模式下不启动游戏、也没有进程句柄，游戏退出时它不一定在场
    /// （或者整场都没发现进程）；而下次准备时的 profile 激活会拿旧内容盖掉主 ini ——
    /// 不先收一次，用户这次在游戏里改的就白改了（表现出来就是「改完再启动又回去了」）。
    /// </para>
    /// </summary>
    /// <returns>同步到的配置名；没东西可收 / 失败返回 null</returns>
    public static string? CollectFromRuntime(string buildDirectory)
    {
        var result = CollectFromRuntimeChecked(buildDirectory);
        if (!result.Succeeded) throw new IOException("Runtime profile collection is blocked or failed.");
        return result.PresetName;
    }

    public readonly record struct PreparationResult(bool Succeeded, string? PresetName, string? FailureReason = null);

    public static PreparationResult CollectFromRuntimeChecked(string buildDirectory)
    {
        string pending = Path.Combine(buildDirectory, ".hub-preset-sync-pending");
        try
        {
        if (File.Exists(pending))
        {
            string pendingOwner = File.ReadAllText(pending);
            if (SelectedPreset(pendingOwner, buildDirectory).Length > 0
                && Follow(buildDirectory, pendingOwner) is null)
                return new(false, null, "无法将上次游戏配置保存回所选预设，请检查预设文件的写入权限。");
            File.Delete(pending);
        }
        var collected = OptiScalerProfiles.CollectRuntimeChangesChecked(buildDirectory);
        if (!collected.Succeeded)
            return new(false, null, collected.Status == OptiScalerProfiles.CollectionStatus.Blocked
                ? "主配置与游戏配置存在冲突，或上次配置事务尚未完成。请保留两份配置并检查冲突。"
                : "无法读取或保存上次游戏配置，请检查配置文件的访问权限。");
        if (collected.Status != OptiScalerProfiles.CollectionStatus.Collected || collected.Owner is null)
            return new(true, null);
        string selected = SelectedPreset(collected.Owner, buildDirectory);
        if (selected.Length == 0) return new(true, null);
        WriteTextAtomically(pending, collected.Owner);
        string? followed = Follow(buildDirectory, collected.Owner);
        if (followed is not null) File.Delete(pending);
        return new(followed is not null, followed, followed is null
            ? "无法将游戏配置保存回所选预设，请检查预设文件的写入权限。" : null);
        }
        catch { return new(false, null, "无法完成配置回收，请检查配置目录的访问权限。"); }
    }

    /// <summary>
    /// 启动游戏前调用：把这个游戏挂着的「配置」重放成 <c>profiles\&lt;游戏&gt;.ini</c> 并立刻激活成主 ini。
    ///
    /// <para>
    /// 链路是单向三层：<c>presets\&lt;名字&gt;.ini</c>（配置，OptiScaler 主目录）→
    /// <c>profiles\&lt;游戏&gt;.ini</c>（这个游戏的最终配置）→ <c>OptiScaler.ini</c>（OptiScaler
    /// 真正读写的活文件）。每次启动都从配置重放一遍，所以在页面里改配置、或用编辑器直接改配置
    /// ini，下次启动游戏一定会生效；没挂命名配置时返回 null，由调用方的 Activate 保留 profile 自己那份。
    /// </para>
    /// </summary>
    /// <returns>套用的配置名；没挂命名配置 / 失败返回 null</returns>
    public static string? SyncIntoProfile(string buildDirectory, string gameKey)
    {
        var result = SyncIntoProfileChecked(buildDirectory, gameKey);
        if (!result.Succeeded) throw new IOException("OptiScaler profile preparation is blocked or failed.");
        return result.PresetName;
    }

    public static PreparationResult SyncIntoProfileChecked(string buildDirectory, string gameKey)
    {
        var collected = CollectFromRuntimeChecked(buildDirectory);
        if (!collected.Succeeded) return collected;
        string name = SelectedPreset(gameKey, buildDirectory);
        if (name.Length == 0) return new(true, null);
        bool applied = Apply(buildDirectory, gameKey, name);
        return new(applied, name, applied ? null
            : $"无法读取或套用预设「{name}」，请检查文件是否存在、被占用或无法写入。");
    }

    /// <summary>下拉选择键：按「游戏 + 构建」记，换构建不会串</summary>
    public static string PresetKey(string gameKey, string buildId) => $"opti_preset_{gameKey}_{buildId}";

    /// <summary>构建目录 → 构建 id（<c>来源/版本</c>，就是 OptiScaler 主目录下的相对路径）</summary>
    public static string? BuildIdOf(string? buildDirectory)
    {
        try
        {
            string root = AppConfig.OptiScalerRootPath;
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(buildDirectory))
            {
                return null;
            }

            string relative = Path.GetRelativePath(root, buildDirectory).Replace('\\', '/');
            return relative.StartsWith("..", StringComparison.Ordinal) ? null : relative;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 这个游戏（这个构建）当前挂着的命名配置。先看下拉选择键，再退回按游戏记的 follow 键
    /// —— 老安装里只有下拉键，会出现「页面显示挂着配置、退出却不回写」的鬼状态。
    /// </summary>
    /// <returns>配置名；空串 = 「当前配置（游戏内保存）」/ 没挂</returns>
    public static string SelectedPreset(string gameKey, string? buildDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(gameKey))
        {
            return string.Empty;
        }

        string name = string.Empty;

        if (BuildIdOf(buildDirectory) is { Length: > 0 } buildId)
        {
            name = AppConfig.GetValue(string.Empty, PresetKey(gameKey, buildId)) ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = AppConfig.GetValue(string.Empty, FollowKey(gameKey)) ?? string.Empty;
        }

        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)
            || string.Equals(name, CurrentLabel, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        // 更新或删除预设后，旧选择按当前游戏 profile 处理，避免阻断启动。
        return File.Exists(PathOf(name)) ? name : string.Empty;
    }

    /// <summary>预设名只留安全文件名字符</summary>
    public static string Sanitize(string name)
    {
        name = string.IsNullOrWhiteSpace(name) ? "preset" : name.Trim();

        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Replace('\\', '_').Replace('/', '_').Trim();
    }

    private static void WriteTextAtomically(string path, string content)
    {
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(staging, content);
            File.Move(staging, path, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }
}
