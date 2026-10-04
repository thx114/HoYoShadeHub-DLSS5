namespace HoYoShadeHub.Extensions.Services;

/// <summary>「把 <c>nvngx_dlssnr.dll</c> 放到 OptiScaler 旁边」的结果</summary>
public enum NrdllPlaceStatus
{
    /// <summary>目标目录里已经有一份，而且和源头一样大 —— 什么都不用做</summary>
    AlreadyPresent,

    /// <summary>这次从别处复制过去了</summary>
    Copied,

    /// <summary>哪儿都找不到 nvngx_dlssnr.dll</summary>
    NotFound,

    /// <summary>出错（目录不存在 / 复制失败）</summary>
    Failed,
}

public sealed record NrdllPlaceResult(NrdllPlaceStatus Status, string? SourcePath, string? TargetPath)
{
    public bool Ok => Status is NrdllPlaceStatus.AlreadyPresent or NrdllPlaceStatus.Copied;

    /// <summary>给界面用的一句话</summary>
    public string Message => Status switch
    {
        NrdllPlaceStatus.AlreadyPresent => "构建目录里已经有 nvngx_dlssnr.dll。",
        NrdllPlaceStatus.Copied => $"已把 nvngx_dlssnr.dll 从 {SourcePath} 复制到构建目录。",
        NrdllPlaceStatus.NotFound => "没找到 nvngx_dlssnr.dll —— 先到「DLL 配置」里装一个（或自己放一份到插件目录）。",
        _ => "复制 nvngx_dlssnr.dll 失败。",
    };
}

/// <summary>确保 310.9 DLSSG 就位的结果</summary>
public sealed record DlssgEnsureResult(bool Ok, string Message, string? StagingPath);


/// <summary>
/// OptiScaler 的神经渲染运行时（<c>nvngx_dlssnr.dll</c>）。
///
/// <para>
/// 各分支的手册都写着「把 <c>nvngx_dlssnr.dll</c> 放在包旁边」：
/// wilsjo2 的 INSTALL-DLSSNR.md 第 2 步「解压完整包到游戏 exe 旁边」、第 3 步「把 nvngx_dlssnr.dll 放到那里」；
/// NeuRotic / DLSS NR on AMD 同理（DLSS NR on AMD 用自己的安装程序，装完也是放在同一个目录）。
/// </para>
///
/// <para>
/// 我们这边是**注入**而不是铺到游戏目录，所以「旁边」= OptiScaler 构建目录
/// （<c>&lt;用户数据目录&gt;\OptiScaler\&lt;来源&gt;\&lt;版本&gt;\</c>）。
/// 这个类负责从「DLL 配置」装好的插件目录（以及别的构建目录）里拿一份复制过去。
/// </para>
/// </summary>
public static class OptiScalerRuntime
{
    public const string NeuralRuntimeFileName = "nvngx_dlssnr.dll";

    /// <summary>OptiScaler 的 Streamline 后端文件夹名（ini 注释：OptiScaler/streamline）</summary>
    public const string StreamlineFolderName = "streamline";

    /// <summary>
    /// Streamline 核心文件清单（OptiScaler 自带的 <c>OptiScaler/streamline</c> 那一套）。
    /// FGOutput=DLSSG 时 ini 注释要求这套 + nvngx_dlssg.dll。
    /// 远端 catalog/conditions.json 的 optiscaler.streamlineFiles 可以覆盖这份默认清单。
    /// </summary>
    public static string[] StreamlineFileNames =>
        Conditions.AddonConditions.Current?.OptiScaler?.StreamlineFiles is { Length: > 0 } remote
            ? remote
            :
            [
                "sl.interposer.dll",
                "sl.common.dll",
                "sl.dlss.dll",
                "sl.dlss_g.dll",
                "sl.reflex.dll",
                "sl.pcl.dll",
                "sl.nis.dll",
            ];

    /// <summary>DLSSG 后端额外需要的 NGX 实现文件</summary>
    public const string DlssgFileName = "nvngx_dlssg.dll";

    /// <summary>MFG 解锁唯一支持的 DLSSG 大版本</summary>
    public const int DlssgUnlockMajor = 310;
    public const int DlssgUnlockMinor = 9;

    /// <summary>310.9 DLSSG 的托管下载地址（OptiScaler-MFG-Ada runtime release）</summary>
    public const string DlssgDownloadUrl =
        "https://github.com/thx114/OptiScaler-MFG-Ada/releases/download/runtime-310.9.1/nvngx_dlssg_310.9.1.dll";

    /// <summary>下载文件的预期 SHA256（小写），用于校验落盘内容</summary>
    public const string DlssgDownloadSha256 = "ff6e90eb78b827927dff5b4ecc6b1c870c2e9bca29ed9f48c7d348cc9e170b82";

    /// <summary>DLSSG 在构建目录内需要落位的 2 个相对目录</summary>
    public static IReadOnlyList<string> DlssgTargetSubdirs { get; } =
    [
        Path.Combine("OptiScaler", StreamlineFolderName),
        "OptiScaler",
    ];

    /// <summary>
    /// 确保构建目录 2 个位置都有 310.9 的 <c>nvngx_dlssg.dll</c>。
    /// 先复用已有的本地高版本（<see cref="EnsureDlssgForUnlock"/> 的候选）；两处仍缺 310.9 时从托管地址下载一份，
    /// 再分发到 2 个位置。
    /// </summary>
    /// <returns>结果说明；两处都就位返回 true</returns>
    public static async Task<DlssgEnsureResult> EnsureDlssgForUnlockAsync(
        string buildDirectory,
        IEnumerable<string?>? candidateDirectories = null,
        DownloadService? downloader = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory) || !Directory.Exists(buildDirectory))
        {
            return new DlssgEnsureResult(false, "构建目录不存在。", null);
        }

        // 1) 本地候选里最高的一份钉到 2 个位置（已有逻辑，纯文件复制）。
        EnsureDlssgForUnlock(buildDirectory, candidateDirectories);

        if (DlssgTargetsReady(buildDirectory))
        {
            return new DlssgEnsureResult(true, "构建目录 2 个位置已有 310.9 DLSSG。", null);
        }

        // 2) 仍有位置缺：下载一份到构建根的临时文件，校验后分发。
        try
        {
            string staging = Path.Combine(buildDirectory, DlssgFileName + ".download");
            DownloadService service = downloader ?? new DownloadService();
            await service.DownloadToFileAsync(
                DlssgDownloadUrl,
                staging,
                DlssgDownloadSha256,
                null,
                cancellationToken,
                null,
                allowResume: false);

            foreach (string subdir in DlssgTargetSubdirs)
            {
                string targetDir = Path.Combine(buildDirectory, subdir);
                Directory.CreateDirectory(targetDir);
                File.Copy(staging, Path.Combine(targetDir, DlssgFileName), overwrite: true);
            }

            try
            {
                File.Delete(staging);
            }
            catch
            {
                // 临时文件收不掉不影响功能
            }

            bool ready = DlssgTargetsReady(buildDirectory);
            return ready
                ? new DlssgEnsureResult(true, "已下载 310.9 DLSSG 并放到构建目录 2 个位置。", staging)
                : new DlssgEnsureResult(false, "下载完成，但 2 个位置未全部就位。", staging);
        }
        catch (Exception ex)
        {
            return new DlssgEnsureResult(false, $"下载 310.9 DLSSG 失败：{ex.Message}", null);
        }
    }

    /// <summary>2 个位置都存在且是 310.9 才算就绪</summary>
    public static bool DlssgTargetsReady(string buildDirectory)
    {
        foreach (string subdir in DlssgTargetSubdirs)
        {
            string file = Path.Combine(buildDirectory, subdir, DlssgFileName);
            if (!File.Exists(file) || !IsUnlockDlssg(file))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>文件版本是否为 310.9（major==310 且 minor==9）</summary>
    public static bool IsUnlockDlssg(string path)
    {
        Version? version = TryReadFileVersion(path);
        return version is { Major: DlssgUnlockMajor } && version.Minor >= DlssgUnlockMinor;
    }

    /// <summary>
    /// 在游戏 exe 目录里找 dlssg，搜索顺序与 OptiScaler 的 <c>Util::FindFilePath</c> 一致：
    /// 先看目录根部，再按广度优先往下找。只用于启动前的版本检查 —— OptiScaler 运行时
    /// 也会命中同一个文件。
    /// </summary>
    public static string? FindGameDlssg(string gameExeDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameExeDirectory) || !Directory.Exists(gameExeDirectory))
        {
            return null;
        }

        string direct = Path.Combine(gameExeDirectory, DlssgFileName);
        if (File.Exists(direct))
        {
            return direct;
        }

        // BFS：游戏目录通常不深；no-重解析点，避免绕进链接目录
        var queue = new Queue<string>();
        queue.Enqueue(gameExeDirectory);
        int visited = 0;
        while (queue.Count > 0 && visited < 4096)
        {
            string current = queue.Dequeue();
            visited++;

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (string dir in subdirs)
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    if (File.Exists(Path.Combine(dir, DlssgFileName)))
                    {
                        return Path.Combine(dir, DlssgFileName);
                    }

                    queue.Enqueue(dir);
                }
                catch
                {
                    // 无权限等：跳过
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 游戏目录里**所有** nvngx_dlssg.dll 副本（根部 + 广度优先的各级子目录，最多 <paramref name="max"/> 份）。
    /// OptiScaler 运行时也是这么找的，命中哪一份由目录结构决定 —— 要换就得把能找到的都换掉。
    /// </summary>
    public static List<string> FindGameDlssgCopies(string gameExeDirectory, int max = 8)
    {
        var found = new List<string>();

        if (string.IsNullOrWhiteSpace(gameExeDirectory) || !Directory.Exists(gameExeDirectory))
        {
            return found;
        }

        string direct = Path.Combine(gameExeDirectory, DlssgFileName);
        if (File.Exists(direct))
        {
            found.Add(direct);
        }

        var queue = new Queue<string>();
        queue.Enqueue(gameExeDirectory);
        int visited = 0;

        while (queue.Count > 0 && visited < 4096 && found.Count < max)
        {
            string current = queue.Dequeue();
            visited++;

            string[] subdirs;
            try
            {
                subdirs = Directory.GetDirectories(current);
            }
            catch
            {
                continue;
            }

            foreach (string dir in subdirs)
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    string candidate = Path.Combine(dir, DlssgFileName);
                    if (File.Exists(candidate) && !found.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                    {
                        found.Add(candidate);
                        if (found.Count >= max)
                        {
                            break;
                        }
                    }

                    queue.Enqueue(dir);
                }
                catch
                {
                    // 无权限等：跳过
                }
            }
        }

        return found;
    }

    /// <summary>构建目录里那份 310.9 的 dlssg（拿来替换游戏目录自带的旧版）</summary>
    public static string? FindUnlockDlssg(string buildDirectory)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory))
        {
            return null;
        }

        foreach (string subdir in DlssgTargetSubdirs)
        {
            string file = Path.Combine(buildDirectory, subdir, DlssgFileName);
            if (File.Exists(file) && IsUnlockDlssg(file))
            {
                return file;
            }
        }

        return null;
    }

    /// <summary>替换游戏目录自带 dlssg 的结果</summary>
    public sealed record GameDlssgSwapResult(
        bool Ok,
        int Replaced,
        int Already,
        IReadOnlyList<string> Failures,
        string Message);

    /// <summary>
    /// 把游戏目录里自带的 nvngx_dlssg.dll（典型 310.6.0）换成 310.9.1，原文件先备份到
    /// <paramref name="backupRoot"/>（只留第一次那份原版）。
    ///
    /// <para>
    /// 为什么必须换游戏目录这份：OptiScaler 运行时从**游戏 exe 目录**开始按广度优先找 dlssg，
    /// 游戏自带的那份永远先被命中 —— 只把 310.9 放进 OptiScaler 构建目录没用，
    /// 叠加层照样报「unlock unavailable for this runtime」，多帧生成不生效（用户实测：手动换掉才生效）。
    /// </para>
    /// </summary>
    public static GameDlssgSwapResult ReplaceGameDlssg(string gameExeDirectory, string sourceDll, string backupRoot)
    {
        if (string.IsNullOrWhiteSpace(gameExeDirectory) || !Directory.Exists(gameExeDirectory))
        {
            return new GameDlssgSwapResult(false, 0, 0, [], "游戏目录不存在。");
        }

        if (string.IsNullOrWhiteSpace(sourceDll) || !File.Exists(sourceDll))
        {
            return new GameDlssgSwapResult(false, 0, 0, [], "找不到 310.9.1 的 nvngx_dlssg.dll —— 先在「全局插件 → OptiScaler」把构建补齐。");
        }

        List<string> targets = FindGameDlssgCopies(gameExeDirectory);
        if (targets.Count == 0)
        {
            return new GameDlssgSwapResult(true, 0, 0, [], "游戏目录里没有 nvngx_dlssg.dll，不用替换。");
        }

        string? sourceHash = null;
        int replaced = 0;
        int already = 0;
        var failures = new List<string>();

        foreach (string target in targets)
        {
            if (IsSameContent(target, sourceDll, ref sourceHash))
            {
                already++;
                continue;
            }

            BackupGameDll(target, gameExeDirectory, backupRoot);

            if (TryReplaceFile(sourceDll, target, out string error) && IsSameContent(target, sourceDll, ref sourceHash))
            {
                replaced++;
            }
            else
            {
                failures.Add($"{target}：{error}");
            }
        }

        string message = failures.Count == 0
            ? $"游戏目录的 nvngx_dlssg.dll 已替换成 310.9.1（换掉 {replaced} 份，本来就是新版 {already} 份）。"
            : $"替换 {replaced} 份、跳过 {already} 份，{failures.Count} 份没成功：{string.Join("；", failures)}";

        return new GameDlssgSwapResult(failures.Count == 0, replaced, already, failures, message);
    }

    /// <summary>
    /// 把 <paramref name="source"/> 覆盖到 <paramref name="target"/>，遇到只读 / 占用 / 权限逐个绕：
    /// 1) 直接覆盖；2) 清只读再试；3) 改名让位再写（写失败把原文件挪回去，绝不让游戏缺文件）。
    /// </summary>
    public static bool TryReplaceFile(string source, string target, out string error)
    {
        error = string.Empty;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                File.Copy(source, target, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;

                if (attempt == 0)
                {
                    try
                    {
                        FileAttributes attributes = File.GetAttributes(target);
                        if (attributes.HasFlag(FileAttributes.ReadOnly))
                        {
                            File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
                        }
                    }
                    catch
                    {
                        // 属性拿不到就算了，进下一轮
                    }
                }
                else if (attempt == 1)
                {
                    string aside = target + ".hysx-old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");

                    try
                    {
                        File.Move(target, aside);
                    }
                    catch (Exception moveEx)
                    {
                        error = moveEx.Message;
                        continue;
                    }

                    try
                    {
                        File.Copy(source, target, overwrite: true);
                        return true;
                    }
                    catch (Exception copyEx)
                    {
                        error = copyEx.Message;

                        try
                        {
                            File.Move(aside, target);
                        }
                        catch
                        {
                            // 挪回去也失败：至少错误信息里有原名，让用户知道
                        }
                    }
                }
            }
        }

        return false;
    }

    /// <summary>两个文件内容是否一致（先比大小，再比 SHA256；源文件哈希只算一次）</summary>
    private static bool IsSameContent(string left, string right, ref string? rightHash)
    {
        try
        {
            var a = new FileInfo(left);
            var b = new FileInfo(right);

            if (!a.Exists || !b.Exists || a.Length != b.Length)
            {
                return false;
            }

            rightHash ??= Sha256Of(right);
            return string.Equals(Sha256Of(left), rightHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string Sha256Of(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
    }

    /// <summary>替换前把原文件备份一份（相对路径压平成文件名，只留第一次那份）</summary>
    private static void BackupGameDll(string target, string gameExeDirectory, string backupRoot)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(backupRoot))
            {
                return;
            }

            string relative = Path.GetRelativePath(gameExeDirectory, target).Replace('\\', '_').Replace('/', '_');
            string backup = Path.Combine(backupRoot, relative + ".bak");

            if (File.Exists(backup))
            {
                return;
            }

            Directory.CreateDirectory(backupRoot);
            File.Copy(target, backup, overwrite: false);
        }
        catch
        {
            // 备份失败不拦替换
        }
    }

    /// <summary>读 PE 文件版本，读不出来返回 null</summary>
    public static Version? TryReadFileVersion(string path)
    {
        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion is { } text
                ? ParseVersion(text)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static Version ParseVersion(string text)
    {
        // FileVersion 可能是 "310, 9, 1, 0"（逗号）或标准点分格式，统一成点分。
        string normalized = text.Replace(',', '.').Replace(" ", string.Empty);
        return Version.TryParse(normalized, out Version? version) ? version : new Version(0, 0);
    }

    /// <summary>
    /// 确保构建目录里有 <c>nvngx_dlssnr.dll</c>。
    /// 已经有**同样大小**的一份就不动（用户可能自己换过版本，别乱覆盖）。
    /// </summary>
    /// <param name="buildDirectory">OptiScaler 构建目录</param>
    /// <param name="addonsDirectory">插件目录（DLL 配置把运行时装在这儿）</param>
    /// <param name="extraSearchDirectories">其它候选目录（比如别的 OptiScaler 构建目录）</param>
    public static NrdllPlaceResult EnsureNrdll(
        string buildDirectory,
        string? addonsDirectory,
        IEnumerable<string>? extraSearchDirectories = null)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory) || !Directory.Exists(buildDirectory))
        {
            return new NrdllPlaceResult(NrdllPlaceStatus.Failed, null, null);
        }

        string target = Path.Combine(buildDirectory, NeuralRuntimeFileName);
        string? source = FindSource(target, addonsDirectory, extraSearchDirectories);

        // nvngx_dlssnr 2.14.1.0 那份本身缺文件，装上去 DLSS5 起不来
        // （用户实测 0xBAD0000B FAIL_UnableToInitializeFeature）：
        // 坏源不复制；目标目录里如果是它，也当成「没有」，好让好源覆盖掉。
        if (source is not null && IsBlockedNrdll(source))
        {
            source = null;
        }

        bool targetBlocked = File.Exists(target) && IsBlockedNrdll(target);

        if (source is null)
        {
            // 目标目录自己带着一份（大小无从比较）也算有
            return File.Exists(target) && !targetBlocked
                ? new NrdllPlaceResult(NrdllPlaceStatus.AlreadyPresent, target, target)
                : new NrdllPlaceResult(NrdllPlaceStatus.NotFound, null, target);
        }

        try
        {
            bool sameFile = string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
            bool both = File.Exists(target) && File.Exists(source);
            if (both && !sameFile && !targetBlocked && new FileInfo(target).Length == new FileInfo(source).Length)
            {
                return new NrdllPlaceResult(NrdllPlaceStatus.AlreadyPresent, source, target);
            }

            if (!both || !sameFile)
            {
                File.Copy(source, target, overwrite: true);
                return new NrdllPlaceResult(NrdllPlaceStatus.Copied, source, target);
            }

            return new NrdllPlaceResult(NrdllPlaceStatus.AlreadyPresent, source, target);
        }
        catch
        {
            return new NrdllPlaceResult(NrdllPlaceStatus.Failed, source, target);
        }
    }

    /// <summary>
    /// 2.14.1.0 那份 <c>nvngx_dlssnr.dll</c> 是坏的（包内缺文件，装上会 FAIL_UnableToInitializeFeature）。
    /// 版本号取文件版本的前缀匹配，拿不到版本就当不是坏的。
    /// </summary>
    private static bool IsBlockedNrdll(string path)
    {
        try
        {
            string? text = TryReadFileVersion(path)?.ToString();
            return text is not null && text.StartsWith("2.14.1", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>构建目录里有没有运行时</summary>
    public static bool HasNrdll(string buildDirectory)
        => !string.IsNullOrWhiteSpace(buildDirectory)
           && File.Exists(Path.Combine(buildDirectory, NeuralRuntimeFileName));

    // 原神 DX11 FSR2 桥（Genshin FSR Bridge） ---------------------------------

    /// <summary>
    /// 桥的 DLL 文件名。这是原神 DLSS5 链路里「让 OptiScaler 看见 FSR2」的那一环：
    /// 它 hook 游戏的 <c>D3D11CreateDevice</c> 与 <c>GetProcAddress</c>，把标准
    /// <c>ffxFsr2*</c> 接口垫出来（上游 <c>AizawaHikaru233/genshin_fsr_brigde</c>，GPL-3.0）。
    /// 它是被注入的原生 DLL，不是 ReShade 插件，也不是 OptiScaler 本身。
    /// </summary>
    public const string FsrBridgeDllName = "Dx11FsrBridge.dll";

    /// <summary>桥的配置文件名（和 DLL 同目录，桥自己按 DLL 所在目录找）</summary>
    public const string FsrBridgeIniName = "Dx11FsrBridge.ini";

    /// <summary>
    /// 桥 ini 的最小模板。桥的每个键都有代码默认值，这里只写 DLSS5 链路要明确的四项；
    /// 故意不复制上游那份 9 KB、含游戏 RVA 表的完整默认配置：那些 RVA 同样是代码默认值，
    /// 抄过来只会让一份旧值盖住桥以后更新的默认值。
    /// 内容保持纯 ASCII，桥是窄字符读 ini 的，中文注释的编码不值得赌。
    /// </summary>
    public const string FsrBridgeIniTemplate = """
; Generated by HoYoShadeHub DLSS5 fork. Restart the game after changing anything here.
; Every key has a built-in code default; only what the DLSS5 chain needs is pinned below.

[Dx11FsrBridge]
; Bridge master switch.
Enabled=1
; Write Dx11FsrBridge.log next to the DLL -- the first thing to read when debugging.
EnableLogging=1
; The point of the whole module: expose the standard ffxFsr2* exports to OptiScaler.
EnableFsr2GetProcAddressShim=1
; 2 = release path, replaces the game's TAAU directly.
Fsr2TranslationMode=2
""";

    /// <summary>目录里有没有桥（DLL 在就算）</summary>
    public static bool HasFsrBridge(string directory)
        => !string.IsNullOrWhiteSpace(directory)
           && File.Exists(Path.Combine(directory, FsrBridgeDllName));

    /// <summary>
    /// 补齐桥的文件。DLL 只能靠下载，这里不生成；ini 缺失就写一份 <see cref="FsrBridgeIniTemplate"/>。
    /// 已经有一份就一律不动，用户可能照 CXP 那份调过 RVA 或 <c>Fsr2JitterMode</c>。
    /// </summary>
    /// <returns>true 表示这次写了 ini</returns>
    public static bool EnsureFsrBridgeIni(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        string iniPath = Path.Combine(directory, FsrBridgeIniName);
        if (File.Exists(iniPath))
        {
            return false;
        }

        try
        {
            File.WriteAllText(iniPath, FsrBridgeIniTemplate);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 桥的「随桥加载」清单：与桥 DLL 同目录的 <c>Dx11FsrBridge.autoload.txt</c>。
    /// 原神链路上 mhyprot 会拒绝外部注入 OptiScaler，改由桥在进程内把它 LoadLibraryW 起来，
    /// 加载目标写在这个文件里。
    ///
    /// <para>
    /// 它是**跟着本次启动的开关走**的文件：启动器每次启动游戏时，勾了 OptiScaler 就写回去、
    /// 没勾就把上次留下的撤走（见 <see cref="RemoveFsrBridgeAutoload"/>）。里面的路径只对
    /// 当前这台机器有效，整包拷给别人时要让它重新生成。
    /// </para>
    /// </summary>
    public const string FsrBridgeAutoloadName = "Dx11FsrBridge.autoload.txt";

    /// <summary>
    /// v2.3.1 Bridge 的包布局把 OptiScaler.ini 放在 Bridge 的父目录下的
    /// <c>OptiScaler\</c> 旁边，而不是和 Bridge DLL 放在同一目录。
    /// 启动器的模块目录是扁平的，因此每次准备 Genshin + OptiScaler 启动时
    /// 从当前构建的主 ini 生成这个 sidecar；DLL 仍然只从 OptiScaler 构建目录加载。
    /// </summary>
    public static bool EnsureFsrBridgeOptiSidecar(string bridgeDirectory, string buildDirectory)
    {
        if (string.IsNullOrWhiteSpace(bridgeDirectory)
            || string.IsNullOrWhiteSpace(buildDirectory)
            || !Directory.Exists(bridgeDirectory)
            || !Directory.Exists(buildDirectory))
        {
            return false;
        }

        string source = Path.Combine(buildDirectory, ConfigFileName);
        if (!File.Exists(source))
        {
            return false;
        }

        DirectoryInfo? modulesDirectory = Directory.GetParent(bridgeDirectory);
        if (modulesDirectory is null)
        {
            return false;
        }

        string sidecarDirectory = Path.Combine(modulesDirectory.FullName, "OptiScaler");
        string sidecar = Path.Combine(sidecarDirectory, ConfigFileName);
        try
        {
            Directory.CreateDirectory(sidecarDirectory);
            File.Copy(source, sidecar, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>撤走 autoload 清单时留的备份后缀（只留第一份，避免盖掉用户自己放的东西）</summary>
    public const string FsrBridgeAutoloadBackupSuffix = ".hysx-backup";

    /// <summary>读桥的 autoload 清单（没有 / 读不到就返回 null）</summary>
    public static string? ReadFsrBridgeAutoload(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(directory, FsrBridgeAutoloadName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 写桥的 autoload 清单，返回真正写进去的那一行（失败返回 null）。
    ///
    /// <para>
    /// 路径优先写「相对桥 DLL 目录」的形式：一键包那种 <c>payloadBridge</c> 与
    /// <c>payloadOptiScaler</c> 并排的布局，整包拷到别人机器上还能用；只有跨盘、或者要往上爬
    /// 两层以上才退回绝对路径（那种布局本来也没法整包搬）。文件故意不写 BOM，免得读的人把
    /// 第一个字符吃成 <c>﻿</c>。
    /// </para>
    /// </summary>
    public static string? WriteFsrBridgeAutoload(string? directory, string? dllPath)
    {
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(dllPath)
            || !Directory.Exists(directory))
        {
            return null;
        }

        string target = dllPath;
        try
        {
            string relative = Path.GetRelativePath(directory, dllPath);
            if (!Path.IsPathRooted(relative)
                && !relative.StartsWith(@"..\..\", StringComparison.Ordinal))
            {
                target = relative;
            }
        }
        catch
        {
            target = dllPath;
        }

        try
        {
            File.WriteAllText(
                Path.Combine(directory, FsrBridgeAutoloadName),
                target + Environment.NewLine,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return target;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 撤走桥的 autoload 清单（关掉 OptiScaler 时用）：原文件先备份成
    /// <c>&lt;名字&gt;.hysx-backup</c>（已经有备份就不动它），再删掉本体。
    /// 这样桥下次启动不会还把 OptiScaler 拉回来，重新勾上又会由
    /// <see cref="WriteFsrBridgeAutoload"/> 写回去。
    /// </summary>
    /// <returns>true 表示这次真的移走了一份</returns>
    public static bool RemoveFsrBridgeAutoload(string? directory, out string? backupPath)
    {
        backupPath = null;

        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string path = Path.Combine(directory, FsrBridgeAutoloadName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            string backup = path + FsrBridgeAutoloadBackupSuffix;
            if (!File.Exists(backup))
            {
                File.Copy(path, backup);
            }

            backupPath = backup;
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ───────────────────────── Streamline ─────────────────────────

    #region Upscaler replacements (needed for FSR-only games)

    /// <summary>DLSS super-resolution implementation. OptiScaler looks for it next to itself.</summary>
    public const string DlssSrFileName = "nvngx_dlss.dll";

    /// <summary>
    /// Files OptiScaler resolves relative to its own directory (Util::DllPath().remove_filename()).
    ///
    /// nvngx_dlss.dll is the one that matters for FSR-only titles: OptiScaler's README says it
    /// "also works for FSR2-only games ... albeit requires manually providing nvngx_dlss.dll".
    /// Genshin is exactly that case, and Config::CheckUpscalerFiles() records it as
    /// state.nvngxReplacement -- one of the flags that decides whether the overlay shows
    /// "Can't find nvngx.dll and libxess.dll and FSR inputs".
    /// </summary>
    public static readonly string[] UpscalerReplacementFileNames =
    [
        "nvngx_dlss.dll",
        "nvngx_dlssd.dll",
    ];

    /// <summary>
    /// Copies the upscaler replacement DLLs next to OptiScaler. Same policy as EnsureNrdll:
    /// an existing target of identical size is left alone so user-swapped versions survive.
    /// </summary>
    /// <returns>file names copied this time</returns>
    public static List<string> EnsureUpscalerReplacements(
        string buildDirectory,
        string? addonsDirectory,
        IEnumerable<string>? extraSearchDirectories = null)
    {
        var copied = new List<string>();

        if (string.IsNullOrWhiteSpace(buildDirectory) || !Directory.Exists(buildDirectory))
        {
            return copied;
        }

        var candidates = new List<string?>();
        if (extraSearchDirectories is not null)
        {
            candidates.AddRange(extraSearchDirectories);
        }
        candidates.Add(addonsDirectory);

        foreach (string fileName in UpscalerReplacementFileNames)
        {
            string target = Path.Combine(buildDirectory, fileName);
            string? source = FindFileSource(target, fileName, candidates);
            if (source is null)
            {
                continue;
            }

            try
            {
                bool sameFile = string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase);
                if (sameFile)
                {
                    continue;
                }

                if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length)
                {
                    continue;
                }

                File.Copy(source, target, overwrite: true);
                copied.Add(fileName);
            }
            catch
            {
                // A failed copy must not fail the caller.
            }
        }

        return copied;
    }

    /// <summary>Are the upscaler replacement DLLs present next to OptiScaler?</summary>
    public static bool HasUpscalerReplacements(string buildDirectory)
        => !string.IsNullOrWhiteSpace(buildDirectory)
           && File.Exists(Path.Combine(buildDirectory, DlssSrFileName));

    /// <summary>Generic "find this file in the candidate directories" helper.</summary>
    public static string? FindFileSource(
        string? targetPath,
        string fileName,
        IEnumerable<string?>? sourceDirectories)
    {
        foreach (string? directory in sourceDirectories ?? [])
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            if (targetPath is not null
                && string.Equals(Path.GetFullPath(path), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return path;
        }

        return null;
    }

    #endregion

    /// <summary>构建目录里 Streamline 后端文件夹的绝对路径（<c>OptiScaler/streamline</c>）</summary>
    public static string StreamlineDirectory(string buildDirectory)
        => Path.Combine(buildDirectory, "OptiScaler", StreamlineFolderName);

    /// <summary>构建目录里 Streamline 文件齐不齐（核心 7 个 + streamline\nvngx_dlssg.dll）</summary>
    public static bool HasStreamline(string buildDirectory)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory))
        {
            return false;
        }

        string dir = StreamlineDirectory(buildDirectory);
        if (!StreamlineFileNames.All(f => File.Exists(Path.Combine(dir, f))))
        {
            return false;
        }

        return File.Exists(Path.Combine(dir, DlssgFileName));
    }

    /// <summary>
    /// 把 Streamline 核心 7 文件和 nvngx_dlssg.dll 从源目录复制到构建目录的
    /// <c>OptiScaler/streamline</c>。OptiScaler 的 StreamlineProxy 用绝对路径
    /// 从该文件夹逐个加载（sl.common.dll / nvngx_dlssg.dll 也在里面），
    /// 所以 dlssg 放进 streamline 文件夹；构建根另放一份兼容包内默认布局。
    /// 目标已存在同样大小的文件就跳过，不覆盖用户自己换过的版本。
    /// </summary>
    /// <param name="buildDirectory">OptiScaler 构建目录</param>
    /// <param name="sourceDirectory">含整套 sl.* 的目录（比如插件目录、游戏目录）</param>
    /// <returns>复制成功的文件名列表；源里找不到的不在列表里</returns>
    public static List<string> EnsureStreamline(string buildDirectory, string sourceDirectory)
    {
        var copied = new List<string>();

        if (string.IsNullOrWhiteSpace(buildDirectory) || !Directory.Exists(buildDirectory)
            || string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            return copied;
        }

        string targetDir = StreamlineDirectory(buildDirectory);
        Directory.CreateDirectory(targetDir);

        foreach (string file in StreamlineFileNames)
        {
            if (CopyIfNeeded(Path.Combine(sourceDirectory, file), Path.Combine(targetDir, file)))
            {
                copied.Add(file);
            }
        }

        if (CopyIfNeeded(Path.Combine(sourceDirectory, DlssgFileName), Path.Combine(targetDir, DlssgFileName)))
        {
            copied.Add(DlssgFileName);
        }

        // 构建根也放一份（跟 nvngx_dlssnr.dll 同一层，兼容默认 ini 布局）
        CopyIfNeeded(Path.Combine(sourceDirectory, DlssgFileName), Path.Combine(buildDirectory, DlssgFileName));

        return copied;
    }

    /// <summary>
    /// 把版本最高的一份 <c>nvngx_dlssg.dll</c> 统一放到构建内所有 FG 加载位置：
    /// <c>OptiScaler</c>（OptiDllPath 根）与 <c>OptiScaler\streamline</c>。
    ///
    /// <para>
    /// OptiScaler 先在 OptiDllPath 根直接找 dlssg；帧生成初始化时还会把 streamline 目录与
    /// OTA 模块各加载一份，叠加层显示最后一次尝试的解锁状态。dlss-unlocked 的 Ada MFG 解锁
    /// 靠字节签名，只认识 legacy 与 310.9 两套签名；任一处残留 310.6/310.8 都会让叠加层
    /// 显示「unlock unavailable for this runtime」。因此两处必须统一成候选里的最高版本。
    /// </para>
    /// </summary>
    /// <param name="buildDirectory">OptiScaler 构建目录</param>
    /// <param name="candidateDirectories">额外候选目录（插件目录、构建根等）</param>
    /// <returns>实际采用的 dlssg 源路径；哪儿都找不到时 null</returns>
    public static string? EnsureDlssgForUnlock(string buildDirectory, IEnumerable<string?>? candidateDirectories = null)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory) || !Directory.Exists(buildDirectory))
        {
            return null;
        }

        var candidates = new List<string>();
        if (candidateDirectories is not null)
        {
            foreach (string? dir in candidateDirectories)
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
                {
                    continue;
                }

                string file = Path.Combine(dir, DlssgFileName);
                if (File.Exists(file))
                {
                    candidates.Add(file);
                }
            }
        }

        string? best = null;
        Version bestVersion = new(0, 0);
        foreach (string file in candidates)
        {
            Version version = ReadFileVersion(file);
            if (best is null || version > bestVersion)
            {
                best = file;
                bestVersion = version;
            }
        }

        if (best is null)
        {
            return null;
        }

        // OptiDllPath 根 + streamline：两处都钉。锁定失败（游戏还开着）跳过该位置
        foreach (string targetDir in new[]
                 {
                     Path.Combine(buildDirectory, "OptiScaler"),
                     Path.Combine(buildDirectory, "OptiScaler", StreamlineFolderName),
                 })
        {
            string target = Path.Combine(targetDir, DlssgFileName);
            try
            {
                if (File.Exists(target) && ReadFileVersion(target) >= bestVersion)
                {
                    continue;
                }

                Directory.CreateDirectory(targetDir);
                File.Copy(best, target, overwrite: true);
            }
            catch
            {
                // 该位置被占用等：不影响另一处
            }
        }

        return best;
    }

    /// <summary>读 PE 文件版本（FileVersion），读不出来当 0.0</summary>
    private static Version ReadFileVersion(string path) => TryReadFileVersion(path) ?? new Version(0, 0);

    /// <summary>OptiScaler 配置文件名</summary>
    public const string ConfigFileName = "OptiScaler.ini";

    /// <summary>原神启用本 fork 0.1.9+ 时，profile 激活后保持外部 NR guide 坐标修正。</summary>
    public static bool EnsureGenshinNativeGuides(string buildDirectory)
    {
        string dll = Path.Combine(buildDirectory, "OptiScaler.dll");
        string ini = Path.Combine(buildDirectory, ConfigFileName);
        if (!File.Exists(dll) || !File.Exists(ini)) return false;
        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(dll);
        if (version.FileMajorPart != 0 || version.FileMinorPart != 1 || version.FileBuildPart < 9)
            return false;
        string text = File.ReadAllText(ini);
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var section = new System.Text.RegularExpressions.Regex(
            @"(?ms)(^\[DLSS\][ \t]*\r?\n)(.*?)(?=^\[|\z)");
        if (!section.IsMatch(text)) return false;
        string updated = section.Replace(text, match =>
        {
            string body = System.Text.RegularExpressions.Regex.Replace(match.Groups[2].Value,
                @"(?mi)^[ \t]*NativeScreenSpaceGuides[ \t]*=[^\r\n]*(?:\r?\n|$)", string.Empty);
            return match.Groups[1].Value + "NativeScreenSpaceGuides=true" + newline + body;
        }, 1);
        if (!string.Equals(updated, text, StringComparison.Ordinal))
            File.WriteAllText(ini, updated, new System.Text.UTF8Encoding(false));
        return true;
    }

    /// <summary>
    /// 把 ini 里 <c>[Libraries] OptiDllPath</c> 写成构建目录下 <c>OptiScaler</c> 文件夹的绝对路径。
    /// 默认值 auto 解析为相对「游戏 exe 目录」的 <c>.\OptiScaler</c>，外部注入（DLL 在数据目录）
    /// 时会指向游戏目录导致 StreamlineProxy 找不到 sl.interposer.dll。
    /// </summary>
    /// <returns>true 表示 ini 存在且已写入（或本来就是该绝对路径）</returns>
    public static bool EnsureConfigDllPath(string buildDirectory)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory) || !Directory.Exists(buildDirectory))
        {
            return false;
        }

        string iniPath = Path.Combine(buildDirectory, ConfigFileName);
        if (!File.Exists(iniPath))
        {
            return false;
        }

        string wanted = Path.Combine(buildDirectory, "OptiScaler");
        string[] lines = File.ReadAllLines(iniPath);
        bool inLibraries = false;
        bool changed = false;
        bool found = false;
        int librariesHeader = -1;

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inLibraries = string.Equals(trimmed, "[Libraries]", StringComparison.OrdinalIgnoreCase);
                if (inLibraries) librariesHeader = i;
                continue;
            }

            if (!inLibraries)
            {
                continue;
            }

            int eq = lines[i].IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            string key = lines[i][..eq].Trim();
            if (string.Equals(key, "OptiDllPath", StringComparison.OrdinalIgnoreCase))
            {
                found = true;
                string value = lines[i][(eq + 1)..].Trim();
                if (!string.Equals(value, wanted, StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"OptiDllPath = {wanted}";
                    changed = true;
                }

                break;
            }
        }

        if (!found)
        {
            var updated = lines.ToList();
            if (librariesHeader >= 0)
            {
                updated.Insert(librariesHeader + 1, $"OptiDllPath = {wanted}");
            }
            else
            {
                updated.Add("");
                updated.Add("[Libraries]");
                updated.Add($"OptiDllPath = {wanted}");
            }
            lines = updated.ToArray();
            changed = true;
        }

        if (changed)
        {
            File.WriteAllLines(iniPath, lines);
        }

        return true;
    }

    /// <summary>目标缺失或大小不一致才复制；源不存在返回 false</summary>
    private static bool CopyIfNeeded(string source, string target)    {
        if (!File.Exists(source))
        {
            return false;
        }

        try
        {
            if (File.Exists(target) && new FileInfo(source).Length == new FileInfo(target).Length)
            {
                return false;
            }

            File.Copy(source, target, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>找一份可用的源（跳过目标自己那个文件）</summary>
    public static string? FindSource(
        string? targetPath,
        string? addonsDirectory,
        IEnumerable<string>? extraSearchDirectories = null)
    {
        var candidates = new List<string?>();

        if (extraSearchDirectories is not null)
        {
            candidates.AddRange(extraSearchDirectories);
        }

        candidates.Add(addonsDirectory);

        foreach (string? directory in candidates)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            string path = Path.Combine(directory, NeuralRuntimeFileName);
            if (!File.Exists(path))
            {
                continue;
            }

            if (targetPath is not null
                && string.Equals(Path.GetFullPath(path), Path.GetFullPath(targetPath), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return path;
        }

        return null;
    }
}
