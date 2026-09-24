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
    /// </summary>
    public static readonly string[] StreamlineFileNames =
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
    /// 用构建目录里的 310.9 dlssg 替换游戏目录那份。
    /// 源取自构建内 2 个落位之一（<c>OptiScaler\streamline</c> 或 <c>OptiScaler</c>）。
    /// </summary>
    /// <returns>错误信息；成功返回 null</returns>
    public static string? ReplaceGameDlssg(string gameDlssgPath, string buildDirectory)
    {
        if (string.IsNullOrWhiteSpace(gameDlssgPath))
        {
            return "游戏 dlssg 路径为空。";
        }

        string? source = DlssgTargetSubdirs
            .Select(subdir => Path.Combine(buildDirectory, subdir, DlssgFileName))
            .FirstOrDefault(file => File.Exists(file) && IsUnlockDlssg(file));
        if (source is null)
        {
            return "OptiScaler 目录里没有可用的 310.9 dlssg。";
        }

        try
        {
            string backup = gameDlssgPath + ".bak";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(gameDlssgPath, backup);
            try
            {
                File.Copy(source, gameDlssgPath, overwrite: true);
            }
            catch
            {
                // 复制失败把备份还原，保证游戏目录不留缺
                try
                {
                    File.Move(backup, gameDlssgPath);
                }
                catch
                {
                    // 还原也失败：维持抛出，交给调用方报告
                }

                throw;
            }

            return null;
        }
        catch (Exception ex)
        {
            return $"替换游戏目录 dlssg 失败：{ex.Message}";
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

        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                inLibraries = string.Equals(trimmed, "[Libraries]", StringComparison.OrdinalIgnoreCase);
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
            return false;
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
