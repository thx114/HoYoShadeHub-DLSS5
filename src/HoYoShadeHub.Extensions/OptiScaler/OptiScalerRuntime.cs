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

    /// <summary>目录里有没有桥（DLL 在就算）。正身名 <c>Dx11FsrBridge.dll</c> 优先；
    /// 1.4.3.1 那批模块里桥被下载器改名成了 <c>OptiScaler.dll</c>，靠桥自己的 ini / 清单认出来。</summary>
    public static bool HasFsrBridge(string directory)
        => !string.IsNullOrWhiteSpace(directory)
           && (File.Exists(Path.Combine(directory, FsrBridgeDllName))
               || Games.FsrBridgePayload.LooksLikeBridgeDirectory(directory));

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
    /// 桥「支持的原神版本」标记文件：与桥 DLL 同目录的 <c>Dx11FsrBridge.game-versions.txt</c>。
    ///
    /// <para>
    /// 桥是按游戏版本的 RVA / 模式改出来的，跨版本不保证能用，所以**发布桥的时候要带上这张文件**，
    /// 启动器只负责读它（模块卡片上把「当前游戏版本 / 桥支持的版本」摆出来，启动时不匹配就提醒一句，
    /// 拦不拦由桥自己决定）。格式是一行一条，<c>#</c> 或 <c>;</c> 开头是注释、空行忽略：
    /// </para>
    /// <list type="bullet">
    /// <item><c>5.8.0</c> —— 精确到写了几段：<c>5.8</c> 覆盖 <c>5.8.x</c>，<c>5.8.1</c> 只认 <c>5.8.1</c>；</item>
    /// <item><c>&gt;=5.6.0</c> —— 下限（另有 <c>&gt;</c> / <c>&lt;=</c> / <c>&lt;</c>）；</item>
    /// <item><c>5.6.0~5.8.0</c> —— 区间，含两端。</item>
    /// </list>
    /// <para>
    /// 文件不在 / 读不出来 = 桥没声明，调用方按「未知」处理，<b>不要</b>当成不支持（老版本的包没有这张文件）。
    /// </para>
    /// </summary>
    public const string FsrBridgeGameVersionsName = "Dx11FsrBridge.game-versions.txt";

    /// <summary>桥声明支持的游戏版本条目（原始写法，未解析）。目录为空 / 没有文件 / 读失败都返回空表。</summary>
    public static IReadOnlyList<string> ReadFsrBridgeGameVersions(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            string path = Path.Combine(directory, FsrBridgeGameVersionsName);
            return File.Exists(path) ? ParseFsrBridgeGameVersions(File.ReadAllText(path)) : [];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>解析标记文件正文（测试直接喂字符串用）。允许 CRLF，行首 <c>#</c> / <c>;</c> 是注释。</summary>
    public static IReadOnlyList<string> ParseFsrBridgeGameVersions(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        List<string> entries = [];
        foreach (string rawLine in text.Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }
            entries.Add(line);
        }

        return entries;
    }

    /// <summary>
    /// 游戏版本在不在桥声明的支持范围内。
    /// 返回 <c>null</c> = 没声明（文件不在 / 没解析出条目 / 版本读不出来），调用方不该拿它当「不支持」。
    /// </summary>
    public static bool? IsFsrBridgeGameVersionSupported(string? directory, Version? gameVersion)
        => MatchFsrBridgeGameVersion(ReadFsrBridgeGameVersions(directory), gameVersion);

    /// <summary>同上，但喂已经读好的条目（界面 / 启动流程里避免重复读盘）。</summary>
    public static bool? MatchFsrBridgeGameVersion(IReadOnlyList<string>? entries, Version? gameVersion)
    {
        if (entries is not { Count: > 0 } || gameVersion is null)
        {
            return null;
        }

        foreach (string entry in entries)
        {
            if (MatchesFsrBridgeEntry(entry, gameVersion))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>一条声明的匹配规则：纯版本号按「写了几段」前缀匹配，其余按运算符 / 区间比较。</summary>
    private static bool MatchesFsrBridgeEntry(string entry, Version gameVersion)
    {
        string text = entry.Trim();
        if (text.Length == 0)
        {
            return false;
        }

        int tilde = text.IndexOf('~');
        if (tilde > 0)
        {
            return TryParseVersion(text[..tilde], out Version? lower)
                   && TryParseVersion(text[(tilde + 1)..], out Version? upper)
                   && gameVersion >= lower!
                   && gameVersion <= upper!;
        }

        if (TryStripOperator(text, ">=", out string? rest))
        {
            return TryParseVersion(rest, out Version? bound) && gameVersion >= bound!;
        }
        if (TryStripOperator(text, "<=", out rest))
        {
            return TryParseVersion(rest, out Version? bound) && gameVersion <= bound!;
        }
        if (TryStripOperator(text, ">", out rest))
        {
            return TryParseVersion(rest, out Version? bound) && gameVersion > bound!;
        }
        if (TryStripOperator(text, "<", out rest))
        {
            return TryParseVersion(rest, out Version? bound) && gameVersion < bound!;
        }

        return TryParseVersion(text, out Version? exact) && IsVersionPrefixOf(exact!, gameVersion);
    }

    private static bool TryStripOperator(string text, string op, out string? rest)
    {
        if (text.StartsWith(op, StringComparison.Ordinal))
        {
            rest = text[op.Length..].Trim();
            return rest.Length > 0;
        }

        rest = null;
        return false;
    }

    private static bool TryParseVersion(string? text, out Version? version)
        => Version.TryParse((text ?? string.Empty).Trim(), out version) && version is not null;

    /// <summary><c>written</c> 的每一段都和游戏版本对得上（<c>5.8</c> 覆盖 <c>5.8.0</c>）。</summary>
    private static bool IsVersionPrefixOf(Version written, Version gameVersion)
    {
        int[] writtenParts = [written.Major, written.Minor, written.Build, written.Revision];
        int[] gameParts = [gameVersion.Major, gameVersion.Minor, gameVersion.Build, gameVersion.Revision];

        int count = 0;
        foreach (int part in writtenParts)
        {
            if (part < 0)
            {
                break;
            }
            count++;
        }

        if (count == 0)
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (gameParts[i] < 0 || writtenParts[i] != gameParts[i])
            {
                return false;
            }
        }

        return true;
    }

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

    /// <summary>
    /// 原神 Bridge 的渲染精度不是注册表值，而是 Bridge DLL 目录旁的
    /// <c>Dx11FsrBridge.render-scale.cache</c>。FSR Bridge 开启时固定到 0.6，
    /// 让 DX11 FSR 输入保持低于 1，避免 Bridge/OptiScaler 走原生 1.0 路径。
    /// </summary>
    public static bool EnsureFsrBridgeRenderScale(string? directory, float scale = 0.6f)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        // Bridge 当前候选为 0.2, 0.3, 0.4, 0.5, 0.6, 0.7, 0.8, 0.9, 0.999。
        int index = scale switch
        {
            <= 0.25f => 0,
            <= 0.35f => 1,
            <= 0.45f => 2,
            <= 0.55f => 3,
            <= 0.65f => 4,
            <= 0.75f => 5,
            <= 0.85f => 6,
            <= 0.95f => 7,
            _ => 8,
        };

        try
        {
            string path = Path.Combine(directory, "Dx11FsrBridge.render-scale.cache");
            File.WriteAllText(path, $"version 1{Environment.NewLine}index {index}{Environment.NewLine}",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
        => RemoveSidecarFile(directory, FsrBridgeAutoloadName, out backupPath);

    /// <summary>
    /// 撤走 sidecar：原文件先备份成 <c>&lt;名字&gt;.hysx-backup</c>（已经有备份就不动它），
    /// 再删掉本体。这样桥下次启动不会还把那一层拉回来。
    /// </summary>
    private static bool RemoveSidecarFile(string? directory, string fileName, out string? backupPath)
    {
        backupPath = null;

        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        string path = Path.Combine(directory, fileName);
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

    // 桥的「有序注入链」 ------------------------------------------------------

    /// <summary>
    /// 桥的「有序注入链」清单：与桥 DLL 同目录的 <c>Dx11FsrBridge.chain.txt</c>。
    ///
    /// <para>
    /// 原神链路上有多个注入者时，谁先装钩子只能靠抢时间（历史上六轮注入时序实验全部证伪）。
    /// 这张清单把顺序变成**数据**：外部只在 CreateProcess 那一刻注一个桥 DLL，桥在游戏进程内
    /// 按行序 <c>LoadLibraryW</c> 后续各层 —— 进程内加载不受 mhyprot 对
    /// <c>VirtualAllocEx</c>/<c>CreateRemoteThread</c> 的拦截，也不受「反作弊生效前不到 1 秒」
    /// 那个外部注入窗口的约束。
    /// </para>
    ///
    /// <para>
    /// 行序 = 加载顺序，**不要重排**：3DMigoto 的 <c>d3d11.dll</c> 必须在 OptiScaler/ReShade
    /// 之前（用户实测反过来 3DMigoto 装载返回 600），而它自己的 DllMain 又要求 <c>dxgi.dll</c>
    /// 已在进程里（原神是 <c>mhypbase.dll</c> 把 dxgi 带进来的，CreateProcess 那一刻还没有）
    /// —— 所以第一步一定是 <c>wait dxgi.dll</c>。
    /// </para>
    ///
    /// <para>
    /// 桥那边没有这张文件时会退回老的 <see cref="FsrBridgeAutoloadName"/> 单行，
    /// 所以「只换桥 DLL 不换启动器」或回退启动器都不会把已验证链路弄坏。
    /// </para>
    /// </summary>
    public const string FsrBridgeChainName = "Dx11FsrBridge.chain.txt";

    /// <summary>链里的一步。<c>wait</c> 的参数是模块名（如 <c>dxgi.dll</c>），其余是 DLL 路径。</summary>
    public readonly record struct FsrBridgeChainStep(string Verb, string Argument)
    {
        /// <summary>等某个模块进目标进程模块表（桥侧上限 15 秒，超时继续下一步）</summary>
        public static FsrBridgeChainStep Wait(string moduleName) => new("wait", moduleName);

        /// <summary>
        /// 加载 3DMigoto：桥会先在游戏进程里建 <c>Local\3DMigotoLoader</c> 互斥体（XXMI 的
        /// <c>3dmloader.dll</c> 就是靠它在场判断「已有 loader」），再 LoadLibraryW。
        ///
        /// <para>
        /// **这一步现在进内置链，而且是第 2 步**（<c>wait dxgi.dll</c> 之后、OptiScaler 之前）：
        /// 3DMigoto 的代理 <c>d3d11.dll</c> 必须是先占位的那一层（反过来它装载会返回 600），
        /// 而它自己的 DllMain 又要求 <c>dxgi.dll</c> 已经在模块表里。
        /// </para>
        ///
        /// <para>
        /// **更正（2026-10-08 傍晚）**：早先「桥加载的 GIMI 会被 <c>[Loader] loader</c> 拒载、
        /// 并留下 <c>CreateDXGIFactory1</c> 钩子把游戏打崩（<c>0xC0000005</c>、<c>at=&lt;no-module&gt;</c>）」
        /// 的归因是错的。真正原因是桥克隆 <c>ID3D11DeviceContext</c> 虚表时按 128 项克隆
        /// （实际对象是 Context4，共 149 项），GIMI 作为第二层包装转发 Context4 方法时踩到
        /// 克隆区之后的 0，于是 <c>call [rax+0x430]</c> → <c>RIP=0</c>。桥修掉该缺陷后，
        /// 链里带 migoto 步的游戏内实测已能正常进游戏。排查全过程见
        /// <c>docs/XXMI-挂载顺序-调查-20261008.md</c> 第 14 节。
        /// </para>
        /// </summary>
        public static FsrBridgeChainStep Migoto(string dllPath) => new("migoto", dllPath);

        /// <summary>普通 LoadLibraryW</summary>
        public static FsrBridgeChainStep Load(string dllPath) => new("load", dllPath);

        /// <summary>这一步的参数是不是「可以按相对路径写」的 DLL 路径</summary>
        public bool IsPath => Verb is "migoto" or "load";
    }

    /// <summary>读链清单（没有 / 读不到就返回 null）</summary>
    public static string? ReadFsrBridgeChain(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        try
        {
            string path = Path.Combine(directory, FsrBridgeChainName);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 写链清单，返回真正写进去的行（失败 / 没有有效步骤返回 null）。
    /// 与 autoload 清单同一套路径策略：能写成「相对桥 DLL 目录」的就写相对，
    /// 整包搬到别的机器还能用；跨盘、或者要爬两层以上才退回绝对路径。
    /// 文件故意不写 BOM，免得读的人把第一个字符吃成 <c>﻿</c>。
    /// </summary>
    public static string[]? WriteFsrBridgeChain(string? directory, IReadOnlyList<FsrBridgeChainStep>? steps)
    {
        if (string.IsNullOrWhiteSpace(directory) || steps is null || steps.Count == 0
            || !Directory.Exists(directory))
        {
            return null;
        }

        var lines = new List<string>(steps.Count);
        foreach (FsrBridgeChainStep step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Argument))
            {
                continue;
            }

            string argument = step.Argument;
            if (step.IsPath)
            {
                try
                {
                    string relative = Path.GetRelativePath(directory, step.Argument);
                    if (!Path.IsPathRooted(relative)
                        && !relative.StartsWith(@"..\..\", StringComparison.Ordinal))
                    {
                        argument = relative;
                    }
                }
                catch
                {
                    argument = step.Argument;
                }
            }

            lines.Add(step.Verb + " " + argument);
        }

        if (lines.Count == 0)
        {
            return null;
        }

        var text = new System.Text.StringBuilder();
        text.Append("# 由 HoYoShade 启动器写入：行序 = 桥在游戏进程里的加载顺序，勿重排。")
            .Append(Environment.NewLine);
        foreach (string line in lines)
        {
            text.Append(line).Append(Environment.NewLine);
        }

        try
        {
            File.WriteAllText(
                Path.Combine(directory, FsrBridgeChainName),
                text.ToString(),
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return lines.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>撤走链清单（没勾「有序注入链」时用），语义同 <see cref="RemoveFsrBridgeAutoload"/></summary>
    public static bool RemoveFsrBridgeChain(string? directory, out string? backupPath)
        => RemoveSidecarFile(directory, FsrBridgeChainName, out backupPath);

    /// <summary>
    /// 覆写用的链清单文件名，放在数据目录根部（与 <c>config.ini</c> 同级）。
    /// 存在且能解析出至少一步时，启动器改用它去写 <c>Dx11FsrBridge.chain.txt</c>：
    /// 排查共存/顺序问题时改一次文本就能换一次链，不用重编启动器。
    /// </summary>
    public const string FsrBridgeChainOverrideName = "bridge-chain.override.txt";

    /// <summary>
    /// 把链清单文本解析成步骤：<c>#</c>/<c>;</c> 注释与空行忽略，没写动词的裸路径按 <c>load</c> 处理，
    /// 认不出的动词也当 <c>load</c>（宁可多注一个 DLL，也不要静默丢一步）。解析不出步骤返回 null。
    /// 规则与桥侧 <c>Dx11FsrBridge.cpp</c> 的解析保持一致。
    /// </summary>
    public static List<FsrBridgeChainStep>? ParseFsrBridgeChain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var steps = new List<FsrBridgeChainStep>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim().TrimStart('\uFEFF').Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            int space = line.IndexOfAny([' ', '\t']);
            if (space <= 0)
            {
                // 只有一个词：是动词就是「漏了参数」，跳过；否则按裸路径 load（旧格式兼容）
                string only = line.ToLowerInvariant();
                if (only is "wait" or "migoto" or "load")
                {
                    continue;
                }

                steps.Add(FsrBridgeChainStep.Load(line));
                continue;
            }

            string verb = line[..space].Trim().ToLowerInvariant();
            string argument = line[(space + 1)..].Trim().Trim('"');
            if (argument.Length == 0)
            {
                continue;
            }

            steps.Add(verb switch
            {
                "wait" => FsrBridgeChainStep.Wait(argument),
                "migoto" => FsrBridgeChainStep.Migoto(argument),
                _ => FsrBridgeChainStep.Load(argument),
            });
        }

        return steps.Count == 0 ? null : steps;
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
    /// Copies the upscaler replacement DLLs next to both OptiScaler entry points:
    /// the build root and <c>build\OptiScaler</c>. Some fg-only packages load
    /// relative to the component directory, while the injector/runtime still probes
    /// the build root. Keep both locations complete and preserve same-sized user swaps.
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

        string componentDirectory = Path.Combine(buildDirectory, "OptiScaler");
        Directory.CreateDirectory(componentDirectory);
        string[] targetDirectories = [buildDirectory, componentDirectory];

        foreach (string fileName in UpscalerReplacementFileNames)
        {
            string? source = FindFileSource(null, fileName, candidates);
            if (source is null)
            {
                continue;
            }

            foreach (string targetDirectory in targetDirectories)
            {
                string target = Path.Combine(targetDirectory, fileName);
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
                    copied.Add(Path.GetRelativePath(buildDirectory, target));
                }
                catch
                {
                    // A failed copy must not fail the caller.
                }
            }
        }

        return copied;
    }

    /// <summary>Are the upscaler replacement DLLs present next to OptiScaler?</summary>
    public static bool HasUpscalerReplacements(string buildDirectory)
        => !string.IsNullOrWhiteSpace(buildDirectory)
           && (File.Exists(Path.Combine(buildDirectory, "nvngx_dlss.dll"))
               || File.Exists(Path.Combine(buildDirectory, "OptiScaler", "nvngx_dlss.dll")));

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

    /// <summary>Prepare the selected game profile before early LoadLibrary can read OptiScaler.ini.</summary>
    public static bool PrepareGenshinEarlyConfiguration(string buildDirectory, string gameKey)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory) || string.IsNullOrWhiteSpace(gameKey)
            || !File.Exists(Path.Combine(buildDirectory, "OptiScaler.dll"))) return false;
        if (!OptiScalerProfiles.Activate(buildDirectory, gameKey)) return false;
        // Older DLLs do not implement the guide switch; their valid runtime path
        // must still be prepared before injection.
        EnsureGenshinNativeGuides(buildDirectory);
        return EnsureConfigDllPath(buildDirectory);
    }

    /// <summary>写入 Hub 路线标记，供 OptiScaler 在启动时区分 Rocket/GIMI 与普通 Hub 路径。</summary>
    public static bool SetRocketMode(string buildDirectory, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory)) return false;
        string iniPath = Path.Combine(buildDirectory, ConfigFileName);
        if (!File.Exists(iniPath)) return false;
        string text = File.ReadAllText(iniPath);
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string value = enabled ? "true" : "false";
        var section = new System.Text.RegularExpressions.Regex(
            @"(?ms)(^\[HoYoShade\][ \t]*\r?\n)(.*?)(?=^\[|\z)");
        string updated;
        if (section.IsMatch(text))
        {
            updated = section.Replace(text, match =>
            {
                string body = match.Groups[2].Value;
                var key = new System.Text.RegularExpressions.Regex(@"(?mi)^[ \t]*RocketMode[ \t]*=[^\r\n]*(?:\r?\n|$)");
                body = key.IsMatch(body)
                    ? key.Replace(body, $"RocketMode = {value}{newline}", 1)
                    : body.TrimEnd('\r', '\n') + newline + $"RocketMode = {value}" + newline;
                return match.Groups[1].Value + body;
            }, 1);
        }
        else
        {
            updated = text.TrimEnd('\r', '\n') + newline + newline + "[HoYoShade]" + newline + $"RocketMode = {value}" + newline;
        }
        if (!string.Equals(updated, text, StringComparison.Ordinal))
            File.WriteAllText(iniPath, updated, new System.Text.UTF8Encoding(false));
        return true;
    }

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
