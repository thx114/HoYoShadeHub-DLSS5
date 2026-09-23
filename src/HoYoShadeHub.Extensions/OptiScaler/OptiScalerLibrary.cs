using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>一个已经下载到本地的 OptiScaler 构建。</summary>
public sealed class OptiScalerBuild
{
    // 注意：XamlTypeInfo.g.cs 会 new 这些类型，所以不能用 C# 的 required（CS9035）
    /// <summary>库内 id：<c>&lt;sourceId&gt;/&lt;版本&gt;</c></summary>
    public string Id { get; init; } = string.Empty;

    public string SourceId { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    /// <summary>这个构建的目录（绝对路径，里面就是解压出来的那一堆文件）</summary>
    public string Directory { get; init; } = string.Empty;

    /// <summary>下载时的资产名（展示用）</summary>
    public string? AssetName { get; init; }

    /// <summary>目录里找到的 OptiScaler 主 DLL；包里没有就是 null（注入时会被跳过）</summary>
    public string? DllPath { get; init; }

    public DateTimeOffset InstalledAt { get; init; }

    /// <summary>目录里所有文件加起来的大小（展示用）</summary>
    public long SizeBytes { get; init; }
}

/// <summary>每个构建目录里的 <c>build.json</c></summary>
internal sealed class OptiScalerBuildManifest
{
    [JsonPropertyName("sourceId")] public string? SourceId { get; set; }

    [JsonPropertyName("version")] public string? Version { get; set; }

    [JsonPropertyName("assetName")] public string? AssetName { get; set; }

    [JsonPropertyName("installedAt")] public DateTimeOffset? InstalledAt { get; set; }
}

/// <summary>库根目录的 <c>state.json</c>：**只记一个**「当前启用」的构建。</summary>
internal sealed class OptiScalerState
{
    [JsonPropertyName("selected")] public string? Selected { get; set; }
}

/// <summary>
/// 本地 OptiScaler 库：<c>&lt;用户数据目录&gt;\OptiScaler\</c>
///
/// <code>
/// OptiScaler\
///   ├─ state.json                      ← {"selected":"wilsjo2/v0.8.6"}
///   ├─ wilsjo2\v0.8.6\…                ← 解压出来的整包（OptiScaler.dll / OptiScaler.ini / 一堆 dll）
///   └─ neurotic\alpha-0.9.6\…
/// </code>
///
/// <para>
/// 「启用」是**单选**：同一时间只有一个构建是当前启用（用户要求：OptiScaler 只能单选）。
/// 换一个就是把 state.json 里的 selected 改掉，不碰文件。
/// </para>
/// </summary>
public sealed class OptiScalerLibrary
{
    public const string StateFileName = "state.json";
    public const string BuildManifestName = "build.json";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public OptiScalerLibrary(string rootPath)
    {
        RootPath = string.IsNullOrWhiteSpace(rootPath)
            ? string.Empty
            : Path.GetFullPath(rootPath);
    }

    /// <summary>库根目录（可能还不存在）</summary>
    public string RootPath { get; }

    public bool Exists => RootPath.Length > 0 && Directory.Exists(RootPath);

    /// <summary>扫一遍库里的所有构建（新装的排前面）。目录不存在就返回空表。</summary>
    public List<OptiScalerBuild> List()
    {
        var builds = new List<OptiScalerBuild>();
        if (!Exists)
        {
            return builds;
        }

        foreach (string sourceDir in Directory.EnumerateDirectories(RootPath))
        {
            // 只有「来源目录」下面一层才是构建
            foreach (string versionDir in Directory.EnumerateDirectories(sourceDir))
            {
                OptiScalerBuild? build = TryReadBuild(sourceDir, versionDir);
                if (build is not null)
                {
                    builds.Add(build);
                }
            }
        }

        return [.. builds.OrderByDescending(b => b.InstalledAt).ThenBy(b => b.Id, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>当前启用的那个（state.json 里记的）；没启用 / 目录已经没了 → null</summary>
    public OptiScalerBuild? GetSelected()
    {
        OptiScalerState state = ReadState();
        if (string.IsNullOrWhiteSpace(state.Selected))
        {
            return null;
        }

        return List().FirstOrDefault(b => string.Equals(b.Id, state.Selected, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>当前启用构建的主 DLL 路径（没启用 / 包里没有 dll → null）</summary>
    public string? SelectedDllPath => GetSelected()?.DllPath;

    /// <summary>把它设成唯一启用的那个；<paramref name="id"/> 为 null/空 = 取消启用。</summary>
    public void Select(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            WriteState(new OptiScalerState { Selected = null });
            return;
        }

        if (List().All(b => !string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"库里没有这个 OptiScaler 构建：{id}");
        }

        WriteState(new OptiScalerState { Selected = id });
    }

    /// <summary>删掉一个构建（顺带清掉指向它的选择）。</summary>
    public bool Delete(string id)
    {
        OptiScalerBuild? build = List().FirstOrDefault(b => string.Equals(b.Id, id, StringComparison.OrdinalIgnoreCase));
        if (build is null)
        {
            return false;
        }

        HysxUtil.TryDeleteDirectory(build.Directory);

        // 来源目录空了就一起收掉
        string? sourceDir = Path.GetDirectoryName(build.Directory);
        try
        {
            if (sourceDir is not null && Directory.Exists(sourceDir)
                && !Directory.EnumerateFileSystemEntries(sourceDir).Any())
            {
                Directory.Delete(sourceDir);
            }
        }
        catch
        {
            // 收不掉就算了，不影响
        }

        if (string.Equals(ReadState().Selected, id, StringComparison.OrdinalIgnoreCase))
        {
            WriteState(new OptiScalerState { Selected = null });
        }

        return true;
    }

    /// <summary>构建目录：<c>&lt;库&gt;\&lt;sourceId&gt;\&lt;版本&gt;\</c></summary>
    public string DirectoryFor(string sourceId, string version)
        => Path.Combine(RootPath, Sanitize(sourceId), Sanitize(version));

    public static string MakeId(string sourceId, string version) => sourceId + "/" + version;

    /// <summary>把不能当文件名/目录名的字符换掉（tag 里偶尔有 : 之类）。</summary>
    public static string Sanitize(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        string result = new string(chars).Trim().TrimEnd('.');
        return result.Length == 0 ? "unknown" : result;
    }

    /// <summary>在构建目录里找 OptiScaler 的主 DLL（可能在子目录里）。</summary>
    public static string? FindDll(string buildDirectory)
    {        if (!Directory.Exists(buildDirectory))
        {
            return null;
        }

        List<string> dlls;
        try
        {
            dlls = [.. Directory.EnumerateFiles(buildDirectory, "*.dll", SearchOption.AllDirectories)];
        }
        catch
        {
            return null;
        }

        // ① 正主
        string? exact = dlls.FirstOrDefault(f => string.Equals(Path.GetFileName(f), "OptiScaler.dll", StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
        {
            return exact;
        }

        // ② 带后缀的变体（OptiScaler.DLSSNR.dll 之类）
        string? prefixed = dlls
            .Where(f => Path.GetFileName(f).StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Length)
            .FirstOrDefault();
        if (prefixed is not null)
        {
            return prefixed;
        }

        // ③ 把主 dll 改名成「游戏本来就加载的代理名」：这些才是 OptiScaler 的正身。
        //    danielblnc/DLSS-NR-on-AMD 的安装程序默认装出来就是 version.dll（用户确认过）。
        foreach (string proxyName in ProxyDllNames)
        {
            string? hit = dlls.FirstOrDefault(f => string.Equals(Path.GetFileName(f), proxyName, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                return hit;
            }
        }

        // ④ 实在认不出来，只有唯一候选时才拿它
        return dlls.Count == 1 ? dlls[0] : null;
    }

    /// <summary>
    /// 把构建当前识别到的主 DLL 改名成固定的 <c>OptiScaler.dll</c>，让外部注入和依赖解析都用同一个名字。
    /// dlss-unlocked 包发布出来的正身叫 <c>dxgi.dll</c>，落库后需要归一化。
    /// 已经叫这个名字、或没有可识别主 DLL 时不做任何操作。
    /// </summary>
    public static bool NormalizePrimaryDll(string buildDirectory)
    {
        string? dll = FindDll(buildDirectory);
        if (dll is null)
        {
            return false;
        }

        string target = Path.Combine(Path.GetDirectoryName(dll)!, "OptiScaler.dll");
        if (string.Equals(dll, target, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 极少数情况下目录里已存在同名文件，先收掉再改名。
        if (File.Exists(target))
        {
            try
            {
                File.Delete(target);
            }
            catch
            {
                return false;
            }
        }

        File.Move(dll, target);
        return true;
    }

    /// <summary>
    /// OptiScaler 可以被改名成的「代理 DLL」名（游戏会自己加载的那些）。
    /// 顺序 = 优先当注入目标；<c>version.dll</c> 排前面是因为 DLSS NR on AMD 的安装程序默认就装这个。
    /// </summary>
    public static IReadOnlyList<string> ProxyDllNames { get; } =
    [
        "version.dll",
        "dxgi.dll",
        "winmm.dll",
        "dinput8.dll",
        "wininet.dll",
        "dbghelp.dll",
    ];

    private OptiScalerBuild? TryReadBuild(string sourceDir, string versionDir)
    {
        string manifestPath = Path.Combine(versionDir, BuildManifestName);
        OptiScalerBuildManifest? manifest = null;

        if (File.Exists(manifestPath))
        {
            try
            {
                manifest = JsonSerializer.Deserialize<OptiScalerBuildManifest>(File.ReadAllText(manifestPath), _jsonOptions);
            }
            catch (JsonException)
            {
                manifest = null;
            }
        }

        string sourceId = manifest?.SourceId ?? Path.GetFileName(sourceDir);
        string version = manifest?.Version ?? Path.GetFileName(versionDir);
        string? dll = FindDll(versionDir);

        // 既没有清单又找不到 dll 的目录不是有效构建（比如用户手放的杂物）
        if (manifest is null && dll is null)
        {
            return null;
        }

        return new OptiScalerBuild
        {
            Id = MakeId(sourceId, version),
            SourceId = sourceId,
            Version = version,
            Directory = versionDir,
            AssetName = manifest?.AssetName,
            DllPath = dll,
            InstalledAt = manifest?.InstalledAt ?? SafeDirectoryTime(versionDir),
            SizeBytes = SafeDirectorySize(versionDir),
        };
    }

    private OptiScalerState ReadState()
    {
        try
        {
            string path = Path.Combine(RootPath, StateFileName);
            if (!File.Exists(path))
            {
                return new OptiScalerState();
            }

            return JsonSerializer.Deserialize<OptiScalerState>(File.ReadAllText(path), _jsonOptions) ?? new OptiScalerState();
        }
        catch
        {
            return new OptiScalerState();
        }
    }

    private void WriteState(OptiScalerState state)
    {
        Directory.CreateDirectory(RootPath);
        File.WriteAllText(Path.Combine(RootPath, StateFileName), JsonSerializer.Serialize(state, _jsonOptions));
    }

    private static DateTimeOffset SafeDirectoryTime(string directory)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(directory);
        }
        catch
        {
            return DateTimeOffset.MinValue;
        }
    }

    private static long SafeDirectorySize(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        }
        catch
        {
            return 0;
        }
    }
}
