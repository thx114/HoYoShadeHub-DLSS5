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
            File.WriteAllText(PathOf(name), content);

            string meta = MetaPathOf(name);
            if (sources is { Count: > 0 })
            {
                File.WriteAllText(meta, JsonSerializer.Serialize(new PresetMeta { Sources = [.. sources] }));
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

            string profile = OptiScalerProfiles.ProfilePath(buildDirectory, gameKey);
            Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
            File.WriteAllText(profile, content);

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

            // 保留原有的分支限制（否则「保存当前」会把 sources 洗掉）
            return Save(presetName, File.ReadAllText(mainIni), GetSources(presetName));
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
            if (File.Exists(destination))
            {
                return false;
            }

            File.Move(source, destination);

            string oldMeta = MetaPathOf(oldName);
            if (File.Exists(oldMeta))
            {
                File.Move(oldMeta, Path.Combine(Root, target + ".meta.json"));
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

            string name = AppConfig.GetValue(string.Empty, FollowKey(gameKey));
            if (string.IsNullOrWhiteSpace(name)
                || string.Equals(name, CurrentLabel, StringComparison.Ordinal))
            {
                return null;
            }

            string source = OptiScalerProfiles.ProfilePath(buildDirectory, gameKey);
            if (!File.Exists(source))
            {
                return null;
            }

            // 保留这份配置原有的分支限制（GetSources 里读的就是它自己的 .meta.json）
            return Save(name, File.ReadAllText(source), GetSources(name)) ? name : null;
        }
        catch
        {
            return null;
        }
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
}
