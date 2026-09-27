namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>
/// DLSS5 Feed（<c>dlss5-feed.addon64</c>）的配置文件 <c>dlss5-feed.cfg</c>。
///
/// <para>
/// 只管两个「开局多久才接管」的键（用户要求做成可调，插件卡片里直接改）：
/// <list type="bullet">
/// <item><c>create_delay</c>：第一个 NGX 特性晚多少帧创建（出厂 60）——
/// 日志里那句 <c>holding the feature (re)build for N frames</c> 就是它；</item>
/// <item><c>warmup_rebuild</c>：第几帧做一次预热重建（出厂 180）。</item>
/// </list>
/// 其余键、注释、空行、顺序一个字节都不动 —— 复用 <see cref="IniDocument"/>，只认「键=值」行。
/// </para>
/// </summary>
public sealed class Dlss5FeedConfig
{
    /// <summary>配置文件跟着 addon 放同一个目录</summary>
    public const string FileName = "dlss5-feed.cfg";

    public const string CreateDelayKey = "create_delay";

    public const string WarmupRebuildKey = "warmup_rebuild";

    /// <summary>出厂默认（和随包那份 cfg 一致）</summary>
    public const int DefaultCreateDelay = 60;

    public const int DefaultWarmupRebuild = 180;

    /// <summary>上限只做防呆（界面输入框限同一个值）</summary>
    public const int MaxFrames = 3600;

    private readonly IniDocument? _ini;

    private Dlss5FeedConfig(string path, IniDocument? ini, bool fileExists, int createDelay, int warmupRebuild)
    {
        Path = path;
        _ini = ini;
        FileExists = fileExists;
        CreateDelay = createDelay;
        WarmupRebuild = warmupRebuild;
    }

    /// <summary>cfg 的完整路径</summary>
    public string Path { get; }

    /// <summary>盘上有没有这份 cfg（没有 = 用出厂默认值，改一次才写出来）</summary>
    public bool FileExists { get; private set; }

    public int CreateDelay { get; private set; }

    public int WarmupRebuild { get; private set; }

    /// <summary>
    /// 读 addon 目录里的 cfg。目录认不出来返回 null；cfg 不存在就给出厂默认值
    /// （<see cref="FileExists"/> = false），改一次再写才落盘。
    /// </summary>
    public static Dlss5FeedConfig? Load(string? addonDirectory)
    {
        if (string.IsNullOrWhiteSpace(addonDirectory))
        {
            return null;
        }

        string path = System.IO.Path.Combine(addonDirectory, FileName);
        if (!File.Exists(path))
        {
            return new Dlss5FeedConfig(path, null, false, DefaultCreateDelay, DefaultWarmupRebuild);
        }

        try
        {
            IniDocument ini = IniDocument.Load(path);
            return new Dlss5FeedConfig(
                path,
                ini,
                true,
                ReadInt(ini, CreateDelayKey, DefaultCreateDelay),
                ReadInt(ini, WarmupRebuildKey, DefaultWarmupRebuild));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按目录拼 cfg 路径（不读盘）</summary>
    public static string? ResolvePath(string? addonDirectory) =>
        string.IsNullOrWhiteSpace(addonDirectory) ? null : System.IO.Path.Combine(addonDirectory, FileName);

    /// <summary>
    /// 改这两个键并落盘（其余行原样保留）。值夹到 0..<see cref="MaxFrames"/>。
    /// </summary>
    /// <returns>true = 写成功</returns>
    public bool Save(int createDelay, int warmupRebuild)
    {
        int create = Math.Clamp(createDelay, 0, MaxFrames);
        int warmup = Math.Clamp(warmupRebuild, 0, MaxFrames);

        try
        {
            IniDocument ini = _ini ?? IniDocument.CreateEmpty();
            ini.SetValue(IniDocument.RootSection, CreateDelayKey, create.ToString());
            ini.SetValue(IniDocument.RootSection, WarmupRebuildKey, warmup.ToString());
            ini.Save(Path);
        }
        catch
        {
            return false;
        }

        CreateDelay = create;
        WarmupRebuild = warmup;
        FileExists = true;
        return true;
    }

    private static int ReadInt(IniDocument ini, string key, int fallback)
    {
        string? raw = ini.GetValue(IniDocument.RootSection, key);
        return int.TryParse(raw?.Trim(), out int value) ? Math.Clamp(value, 0, MaxFrames) : fallback;
    }
}
