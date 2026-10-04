using HoYoShadeHub.Core.HoYoPlay;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>
/// XXMI 注入 —— **照 XXMI 自己的启动方式**：
/// <list type="number">
/// <item><c>3dmloader.dll</c>（XXMI 包里的注入器）的 <c>HookLibrary(dll, &amp;hook, &amp;mutex)</c> 先挂钩子；</item>
/// <item>游戏进程启动（用户自己起 / 启动器起都行）；</item>
/// <item><c>WaitForInjection(dll, 进程名, 超时)</c> 等注入完成；</item>
/// <item><c>UnhookLibrary(&amp;hook, &amp;mutex)</c> 脱钩。</item>
/// </list>
/// 这样 DLL 是**进程刚起来、D3D 还没初始化时**注入的（XXMI 默认 Hook，不是起来之后再 LoadLibrary）。
///
/// 踩过的坑：
/// <list type="bullet">
/// <item><b>200 = 加载不了要注入的 dll</b>：注入器要先把它读进来找入口，而同目录的依赖
/// （d3dcompiler_47.dll 之类）不在默认搜索路径里 → 调 <c>SetDllDirectory</c> 把实例目录加进去；</item>
/// <item><b>100 = 上一个 3DMigotoLoader 实例还在</b>：失败时也必须 <c>UnhookLibrary</c>，否则 hook/mutex
/// 留在那儿，下一次直接 100；</item>
/// <item>实例里那份 d3d11.dll 加载失败时，拿 XXMI 包里自带的那份再试一次。</item>
/// </list>
///
/// 关于「不安全模式」：那是 XXMI 的 <c>Migoto.unsafe_mode</c>，**默认关**（关的时候 XXMI 会在注入前校验
/// 已部署文件的签名，防第三方 3dmigoto 库）。我们不是 XXMI 的包管理器，不做那一步校验，也不改任何 XXMI 配置。
/// </summary>
internal sealed class XxmiInjector
{
    private static readonly ILogger Logger = AppConfig.GetLogger<XxmiInjector>();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int HookLibraryDelegate(string dllPath, out IntPtr hook, out IntPtr mutex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int WaitForInjectionDelegate(string dllPath, string targetProcess, int timeout);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnhookLibraryDelegate(ref IntPtr hook, ref IntPtr mutex);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetDllDirectoryW(string? pathName);

    // ---- 「按 XXMI 的方式启动」用到的：挂起启动 + 目标进程内注入 + 恢复 ----

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private delegate int InjectDelegate(uint pid, string dllPath, int timeout);

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNewConsole = 0x00000010;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcessW(
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr CreateMutexA(IntPtr attributes, bool initialOwner, string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>这次 XXMI 启动的结果（给界面显示用）</summary>
    public sealed record XxmiLaunchResult(bool Started, bool Injected, string Message);

    /// <summary>
    /// 3dmloader 的 loader 互斥体：XXMI 挂钩子（<c>HookLibrary</c>）时建，被注入的 DLL 靠它判断
    /// loader 在不在。它存在的时间 = 「钩子已挂好、正等游戏进程出现」—— 正好是我们要等的「注入器已就绪」。
    /// </summary>
    private const string LoaderMutexName = "Local\\3DMigotoLoader";

    private const uint MutexSynchronize = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr OpenMutexW(uint desiredAccess, bool inheritHandle, string name);

    /// <summary>XXMI 的 loader 互斥体现在在不在（在 = 有实例正挂着钩子等游戏）</summary>
    public static bool LoaderArmed()
    {
        IntPtr handle = OpenMutexW(MutexSynchronize, false, LoaderMutexName);

        if (handle == IntPtr.Zero)
        {
            return false;
        }

        CloseHandle(handle);
        return true;
    }

    /// <summary>XXMI 2.3.9 起「启动方式」是 <c>game_launch</c>（GameLaunch 枚举），手动模式 = MANUAL</summary>
    private const string ManualGameLaunch = "MANUAL";

    /// <summary>XXMI 2.3.9 之前的旧键值；现在只剩占位 OPTION_REMOVED，写了不生效，留着照顾老版本</summary>
    private const string LegacyManualStartMethod = "Manual";

    /// <summary>
    /// 把 XXMI 里本游戏导入器的启动方式写成**手动模式**：手动模式下 XXMI 不会自己拉起游戏，
    /// 只把注入器挂上、等游戏进程出现，模型替换交给它。空串 = 成功，否则是失败原因。
    /// <paramref name="gameExeName"/> 传本次要启动的真实 exe 名（如 YuanShen.exe），用来钉住进程名。
    /// </summary>
    public static string PrepareManualMode(GameId gameId, string? gameName, string? gameExeName = null)
    {
        string? configPath = Xxmi.XxmiLocator.FindConfigPath(gameId.GameBiz, gameName, out string? importer);

        if (configPath is null || importer is null)
        {
            return "找不到 XXMI 配置文件（到「模型替换」页确认 MI 目录）";
        }

        try
        {
            System.Text.Json.Nodes.JsonNode? root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath));

            if (root is null
                || root["Importers"]?[importer]?["Importer"] is not System.Text.Json.Nodes.JsonObject block)
            {
                return "XXMI 配置里没有导入器 " + importer;
            }

            // XXMI 2.3.9 把「启动方式」从 process_start_method 搬到了 game_launch
            // （GameLaunch 枚举：DIRECT/STEAM/EPIC_GAMES/CUSTOM/MANUAL），旧键只剩占位 OPTION_REMOVED。
            // **只写旧键等于没写**：XXMI 还按 DIRECT 自己把游戏拉起来，跟 Hub 的启动/注入流程撞车
            // （原神那次启动后报「没有找到崩铁」就是它）。两个键都写，新键管 2.3.9+，旧键留给老版本。
            string oldLaunch = block["game_launch"]?.GetValue<string>() ?? string.Empty;
            string oldLegacy = block["process_start_method"]?.GetValue<string>() ?? string.Empty;
            bool changed = false;

            if (!string.Equals(oldLaunch, ManualGameLaunch, StringComparison.OrdinalIgnoreCase))
            {
                block["game_launch"] = ManualGameLaunch;
                changed = true;
            }

            if (!string.Equals(oldLegacy, LegacyManualStartMethod, StringComparison.OrdinalIgnoreCase))
            {
                block["process_start_method"] = LegacyManualStartMethod;
                changed = true;
            }

            // 手动模式下 XXMI 拿不到游戏 exe 路径（get_game_paths() 对手动模式直接返回 None），
            // 进程名只能退回**导入器的默认值** —— GIMI 默认是 GenshinImpact.exe，而国服原神是
            // YuanShen.exe（我们自己的日志里注入目标也一直是 YuanShen.exe）。名字不对，XXMI 的
            // WaitForInjection / 等窗口检测就盯着一个永远不会出现的进程，60 秒后报
            // 「无法检测到游戏进程 GenshinImpact.exe 的窗口」，模型替换直接失效。
            // 所以手动模式必须把真实进程名钉死：game_process_exe_enabled=true + game_process_exe=<真实 exe 名>。
            string oldExe = block["game_process_exe"]?.GetValue<string>() ?? string.Empty;
            bool exeEnabled = block["game_process_exe_enabled"]?.GetValue<bool>() ?? false;

            if (!string.IsNullOrWhiteSpace(gameExeName)
                && (!exeEnabled || !string.Equals(oldExe, gameExeName, StringComparison.OrdinalIgnoreCase)))
            {
                block["game_process_exe_enabled"] = true;
                block["game_process_exe"] = gameExeName;
                changed = true;
            }

            if (!changed)
            {
                return string.Empty;
            }

            string backup = configPath + ".bak-before-manual-mode";

            if (!File.Exists(backup))
            {
                File.Copy(configPath, backup);
            }

            File.WriteAllText(configPath,
                root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            Logger.LogInformation(
                "XXMI {Importer} 已写成手动模式：game_launch {OldLaunch}→{NewLaunch}、process_start_method {OldLegacy}→{NewLegacy}、"
                + "进程名 {OldExe}(enabled={OldEnabled})→{NewExe}(enabled=true)（备份 {Backup}）",
                importer, oldLaunch, ManualGameLaunch, oldLegacy, LegacyManualStartMethod,
                oldExe, exeEnabled, gameExeName, backup);

            return string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "XXMI 写手动模式失败");
            return "写 XXMI 手动模式失败：" + ex.Message;
        }
    }

    /// <summary>这次唤起 XXMI 的结果：<c>Armed</c> 为假只是这次没有模型替换，别拦着游戏启动</summary>
    public sealed record XxmiArmResult(bool Armed, string Message);

    private static readonly TimeSpan XxmiArmTimeout = TimeSpan.FromSeconds(25);

    /// <summary>
    /// **唤起 XXMI 走它自己的「启动游戏」流程**（<c>XXMI Launcher.exe "&lt;游戏 exe&gt;" -x ZZMI -n</c>），
    /// 并**等它把注入器挂好再返回**。
    ///
    /// 手动模式下 XXMI 不会真去起游戏，只把 3dmloader 的钩子挂上、然后等游戏进程出现。这一步必须等：
    /// XXMI 启动流程的第一件事是 <c>_ensure_game_close()</c>「确保游戏已关闭」，它会按进程名把**已经在跑的
    /// 游戏结束掉**。实机日志（2026-10-04 00:30，星铁）：Hub 起 XXMI 后 0.25 秒就起游戏 → 0.6 秒后
    /// XXMI 把它杀掉 → 启动器报 "Failed to start game process"（12 秒等不到进程），XXMI 自己报
    /// 「无法检测到游戏进程 StarRail.exe 的窗口」。所以顺序只能是：
    /// 写手动模式 → 唤起 XXMI 并等钩子挂好 → 注我们自己的东西 → 起游戏。
    ///
    /// 「挂好了」用两个信号判定，任一个成立即可（都是钩子挂好之后的）：
    /// <list type="bullet">
    /// <item>loader 互斥体出现（<c>HookLibrary</c> 建的，跟 XXMI 日志级别无关）；</item>
    /// <item>启动日志里出现「Waiting for user to start the game process」（手动模式挂好钩子后打的那行）。</item>
    /// </list>
    /// </summary>
    public static async Task<XxmiArmResult> ArmForManualLaunchAsync(
        GameId gameId, string? gameName, string? gameExePath, CancellationToken cancellationToken)
    {
        try
        {
            string? instance = Xxmi.XxmiLocator.FindInstance(gameId.GameBiz, gameName, out string? importer);

            if (instance is null || importer is null)
            {
                return new(false, "找不到 XXMI 实例（到「模型替换」页确认 MI 目录）");
            }

            string? launcher = null;

            for (DirectoryInfo? dir = new(instance); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, "Resources", "Bin", "XXMI Launcher.exe");

                if (File.Exists(candidate))
                {
                    launcher = candidate;
                    break;
                }
            }

            if (launcher is null)
            {
                return new(false, "找不到 XXMI Launcher.exe");
            }

            string? configPath = Xxmi.XxmiLocator.FindConfigPath(gameId.GameBiz, gameName, out _);
            string logPath = Path.Combine(
                Path.GetDirectoryName(configPath) ?? Path.GetDirectoryName(launcher) ?? string.Empty,
                "XXMI Launcher Log.txt");

            // 上一次没等到游戏、弹着错误框停在那儿的 XXMI 还占着 loader 互斥体，新实例的 HookLibrary
            // 会直接返回 100（"another instance is running"）—— 先把它收掉，本次才能干净地挂钩子。
            if (LoaderArmed())
            {
                KillArmedLaunchers();

                if (!await WaitForDisarmAsync(5000, cancellationToken).ConfigureAwait(false))
                {
                    return new(false, "XXMI 里有残留的注入器实例（多半是上次没等到游戏留下的），新实例挂不上钩子；"
                        + "请先关掉 XXMI 再启动");
                }
            }

            long logOffset = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;

            // 带 exe 路径：明确告诉 XXMI「这次启动的是哪个游戏」（配了 -x 时它只用来认游戏，手动模式下不会真拉起）
            string arguments = string.IsNullOrWhiteSpace(gameExePath)
                ? $"-x {importer} -n"
                : $"\"{gameExePath}\" -x {importer} -n";

            Process? started;

            try
            {
                started = Process.Start(new ProcessStartInfo
                {
                    FileName = launcher,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(launcher),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "调起 XXMI Launcher 失败");
                return new(false, "调起 XXMI Launcher 失败：" + ex.Message);
            }

            if (started is null)
            {
                return new(false, "调起 XXMI Launcher 失败");
            }

            Logger.LogInformation("XXMI Launcher 已调起（pid {Pid}，手动模式接管）：\"{Exe}\" {Args}",
                started.Id, launcher, arguments);

            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.Elapsed < XxmiArmTimeout)
            {
                await Task.Delay(150, cancellationToken).ConfigureAwait(false);

                if (LoaderArmed() || LogSaysArmed(logPath, ref logOffset))
                {
                    string processName = Path.GetFileName(gameExePath ?? string.Empty);

                    Logger.LogInformation("XXMI 注入器已就绪（{Seconds:F1} 秒）：钩子已挂上，等游戏进程 {Process}",
                        stopwatch.Elapsed.TotalSeconds,
                        processName.Length > 0 ? processName : "(XXMI 配置里的游戏 exe)");

                    return new(true, "XXMI 注入器已就绪");
                }

                if (started.HasExited)
                {
                    break;
                }
            }

            // 没挂上：把这次起的实例收回来（它还没挂钩子，收掉不会有副作用），然后照常启动游戏 ——
            // 手动模式没挂上只是这次没有模型替换，不该拦着游戏。
            string reason = started.HasExited
                ? "XXMI Launcher 提前退出"
                : $"XXMI 注入器 {XxmiArmTimeout.TotalSeconds:F0} 秒内没挂上钩子";

            try
            {
                if (!started.HasExited)
                {
                    started.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning("收回 XXMI Launcher 失败：{Message}", ex.Message);
            }

            Logger.LogWarning("{Reason}：本次没有模型替换，游戏照常启动", reason);
            return new(false, reason + "：本次没有模型替换");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "唤起 XXMI 失败");
            return new(false, "唤起 XXMI 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 收掉**还挂着钩子**的 XXMI Launcher 残留实例。它们多半是上一次没等到游戏、弹着错误框停在那儿的
    /// （实机见过好几个），占着 loader 互斥体让新实例 HookLibrary 返回 100。
    /// </summary>
    private static int KillArmedLaunchers()
    {
        int killed = 0;

        foreach (Process process in Process.GetProcessesByName("XXMI Launcher"))
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(3000);
                    killed++;
                    Logger.LogInformation("已收掉残留的 XXMI Launcher（pid {Pid}）：它还挂着 3dmigoto 钩子", process.Id);
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("结束残留 XXMI Launcher（pid {Pid}）失败：{Message}", process.Id, ex.Message);
                }
            }
        }

        return killed;
    }

    /// <summary>等 loader 互斥体消失（收掉残留实例后它会随进程一起释放）</summary>
    private static async Task<bool> WaitForDisarmAsync(int timeoutMs, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        while (stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            if (!LoaderArmed())
            {
                return true;
            }

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }

        return !LoaderArmed();
    }

    /// <summary>
    /// 从 <paramref name="offset"/> 往后读 XXMI 启动日志，看有没有「钩子已挂好、等游戏进程」那行
    /// （<c>Waiting for user to start the game process …</c>）。XXMI 还在往里写，所以用
    /// <see cref="FileShare.ReadWrite"/> 打开；日志被截断 / 轮转过就从头再来。
    /// </summary>
    private static bool LogSaysArmed(string logPath, ref long offset)
    {
        try
        {
            if (!File.Exists(logPath))
            {
                return false;
            }

            using FileStream stream = new(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length < offset)
            {
                offset = 0;     // 被截断 / 轮转过
            }

            if (stream.Length == offset)
            {
                return false;
            }

            stream.Seek(offset, SeekOrigin.Begin);

            using StreamReader reader = new(stream, Encoding.UTF8);
            string text = reader.ReadToEnd();
            offset = stream.Length;

            // 手动模式挂好钩子后 XXMI 打的就是这行（源码里写死的英文，跟界面语言无关）
            return text.Contains("Waiting for user to start the game process", StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "读 XXMI 启动日志失败：{Path}", logPath);
            return false;
        }
    }

    public static XxmiLaunchResult LaunchViaXxmiCli(GameId gameId, string gameExePath, string? gameName)
    {
        string? instance = Xxmi.XxmiLocator.FindInstance(gameId.GameBiz, gameName, out string? importer);

        if (instance is null || importer is null)
        {
            return new XxmiLaunchResult(false, false, "找不到 XXMI 实例（到「模型替换」页确认 MI 目录）");
        }

        string? launcher = null;
        DirectoryInfo? dir = new(instance);

        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Resources", "Bin", "XXMI Launcher.exe");

            if (File.Exists(candidate))
            {
                launcher = candidate;
                break;
            }

            dir = dir.Parent;
        }

        if (launcher is null)
        {
            return new XxmiLaunchResult(false, false, "找不到 XXMI Launcher.exe");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = launcher,
                Arguments = $"\"{gameExePath}\" -x {importer} -n",
                WorkingDirectory = Path.GetDirectoryName(launcher),
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,   // 连控制台窗口一起藏掉
            });

            Logger.LogInformation("XXMI CLI 启动：\"{Launcher}\" \"{Exe}\" -x {Importer} -n", launcher, gameExePath, importer);
            return new XxmiLaunchResult(true, true, $"已交给 XXMI 后台启动（{importer} -nogui），由它完成模型替换注入");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "XXMI CLI 启动失败");
            return new XxmiLaunchResult(false, false, "调起 XXMI 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 按 XXMI 的方式启动游戏：**以挂起方式起进程 → 用 3dmloader.dll 的 <c>Inject(pid, dll, timeout)</c>
    /// 把 3DMigoto 的 d3d11.dll 注进目标进程 → 恢复线程**。
    /// 为什么这样能成：<c>Inject</c> 是在**目标进程里** CreateRemoteThread(LoadLibraryW)，
    /// DllMain 在游戏进程里跑，所以不会有 <c>HookLibrary</c> 那个 1114（DLL_INIT_FAILED）问题；
    /// 而且进程是挂起的，注入发生在 D3D 初始化之前。
    /// </summary>
    public static XxmiLaunchResult Launch(GameId gameId, string gameExePath, string? gameName, string? extraArguments, IReadOnlyList<string>? extraDlls = null)
    {
        string? injector = FindInjector(gameId, gameName);
        string? loader = FindLoader(gameId, gameName);

        if (injector is null || loader is null)
        {
            return new XxmiLaunchResult(false, false, "找不到 XXMI 的注入器或 d3d11.dll（检查「模型替换」页里的实例目录）");
        }

        string arguments = string.IsNullOrWhiteSpace(extraArguments) ? string.Empty : " " + extraArguments.Trim();
        string commandLine = $"\"{gameExePath}\"{arguments}";
        StartupInfo startupInfo = new() { cb = Marshal.SizeOf<StartupInfo>() };

        // 只挂起，**不要** CREATE_NEW_CONSOLE：不然会冒一个黑窗出来（用户反馈过）
        if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                CreateSuspended, IntPtr.Zero, Path.GetDirectoryName(gameExePath),
                ref startupInfo, out ProcessInformation processInformation))
        {
            int error = Marshal.GetLastWin32Error();
            Logger.LogWarning("XXMI：挂起启动游戏失败，错误码 {Error}", error);
            return new XxmiLaunchResult(false, false, $"启动游戏失败（错误码 {error}）");
        }

        bool injected = false;
        int injectCode = -1;

        // 3DMigoto 的 loader（3dmloader/XXMI Launcher）会创建 "Local\3DMigotoLoader" 这个互斥体，
        // 被注入的 DLL 靠它判断"loader 在不在" —— 我们注入时也建一个，等价于告诉它 loader 在场。
        // （XXMI 的 d3d11.dll 是受控版：能加载、但缺这个上下文就完全不动。）
        IntPtr loaderMutex = CreateMutexA(IntPtr.Zero, false, "Local\\3DMigotoLoader");
        Logger.LogInformation("XXMI loader mutex: {Handle} (err={Error})", loaderMutex, Marshal.GetLastWin32Error());

        try
        {
            IntPtr library = NativeLibrary.Load(injector);

            try
            {
                InjectDelegate inject = Marshal.GetDelegateForFunctionPointer<InjectDelegate>(
                    NativeLibrary.GetExport(library, "Inject"));

                injectCode = inject((uint)processInformation.dwProcessId, loader, 30);
                injected = injectCode == 0;
                Logger.LogInformation("XXMI Inject(pid={Pid}, dll={Dll}) -> {Code}", processInformation.dwProcessId, loader, injectCode);

                // 顺序很重要：**先 3DMigoto，再注 ReShade 这类额外库**（XXMI 也是这个顺序）。
                // 反过来先注 ReShade 的话，ReShade 先占了 d3d11，3DMigoto 的代理 dll 在目标进程里
                // LoadLibraryW 会失败（Inject 返回 600，用户实测过）。
                List<string> extras = [];

                foreach (string extra in Xxmi.XxmiLocator.ExtraLibraries(gameId.GameBiz, gameName))
                {
                    extras.Add(extra);
                }

                if (extraDlls is not null)
                {
                    foreach (string extra in extraDlls)
                    {
                        if (File.Exists(extra) && !extras.Contains(extra, StringComparer.OrdinalIgnoreCase))
                        {
                            extras.Add(extra);
                        }
                    }
                }

                foreach (string extra in extras)
                {
                    int extraCode = inject((uint)processInformation.dwProcessId, extra, 30);
                    Logger.LogInformation("XXMI Inject extra(pid={Pid}, dll={Dll}) -> {Code}", processInformation.dwProcessId, extra, extraCode);
                }
            }
            finally
            {
                NativeLibrary.Free(library);
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "XXMI 注入失败");
        }
        finally
        {
            // 不管注入成没成都要恢复，不然游戏就卡在挂起状态
            ResumeThread(processInformation.hThread);

            if (loaderMutex != IntPtr.Zero)
            {
                CloseHandle(loaderMutex);
            }
            CloseHandle(processInformation.hThread);
            CloseHandle(processInformation.hProcess);
        }

        if (injected)
        {
            return new XxmiLaunchResult(true, true, $"已按 XXMI 方式启动：注入 {Path.GetFileName(loader)} 成功（进程 {processInformation.dwProcessId}）");
        }

        // Inject 的返回码（源码里的定义）：100 进程打不开 / 110 dll 路径不对 / 120·130 找不到 kernel32·LoadLibraryW
        // / 200 远程内存分配失败 / 300 写 dll 路径失败 / 400 建远程线程失败 / 500 超时 / 600 dll 加载失败 / 700 未知
        return new XxmiLaunchResult(true, false, $"游戏已启动，但 XXMI 注入失败（Inject 返回 {injectCode}）");
    }

    /// <summary>这个游戏的 XXMI 加载器（3DMigoto 的 d3d11.dll）；找不到返回 null</summary>
    public static string? FindLoader(GameId gameId, string? gameName)
    {
        string? instance = Xxmi.XxmiLocator.FindInstance(gameId.GameBiz, gameName, out _);

        if (string.IsNullOrWhiteSpace(instance))
        {
            return null;
        }

        string loader = Xxmi.XxmiLocator.LoaderPath(instance);
        return File.Exists(loader) ? loader : null;
    }

    /// <summary>XXMI 的注入器 3dmloader.dll（从 MI 实例往上找 Resources\Packages\XXMI）</summary>
    public static string? FindInjector(GameId gameId, string? gameName)
    {
        string? instance = Xxmi.XxmiLocator.FindInstance(gameId.GameBiz, gameName, out _);

        if (string.IsNullOrWhiteSpace(instance))
        {
            return null;
        }

        DirectoryInfo? dir = new(instance);

        while (dir is not null)
        {
            string candidate = Path.Combine(dir.FullName, "Resources", "Packages", "XXMI", "3dmloader.dll");

            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    /// <summary>XXMI 包里自带的那份 d3d11.dll（实例里那份加载失败时的备选）</summary>
    public static string? FindPackageLoader(GameId gameId, string? gameName)
    {
        string? injector = FindInjector(gameId, gameName);

        if (injector is null)
        {
            return null;
        }

        string dll = Path.Combine(Path.GetDirectoryName(injector)!, "d3d11.dll");
        return File.Exists(dll) ? dll : null;
    }

    /// <summary>
    /// 挂钩子 → 等注入 → 脱钩。调用时机：**游戏进程起来之前**（用户自己起游戏也行，钩子会一直等）。
    /// </summary>
    public static async Task<bool> ArmAndWaitAsync(GameId gameId, string? gameName, string processName, CancellationToken cancellationToken)
    {
        string? injector = FindInjector(gameId, gameName);
        string? instanceDll = FindLoader(gameId, gameName);
        string? packageDll = FindPackageLoader(gameId, gameName);

        if (injector is null || (instanceDll is null && packageDll is null))
        {
            Logger.LogWarning("XXMI：找不到 d3d11.dll（实例 {Instance} / 包 {Package}）或 3dmloader.dll（{Injector}）",
                instanceDll, packageDll, injector);
            return false;
        }

        IntPtr library = IntPtr.Zero;

        try
        {
            library = NativeLibrary.Load(injector);

            HookLibraryDelegate hookLibrary = Marshal.GetDelegateForFunctionPointer<HookLibraryDelegate>(
                NativeLibrary.GetExport(library, "HookLibrary"));
            WaitForInjectionDelegate waitForInjection = Marshal.GetDelegateForFunctionPointer<WaitForInjectionDelegate>(
                NativeLibrary.GetExport(library, "WaitForInjection"));
            UnhookLibraryDelegate unhookLibrary = Marshal.GetDelegateForFunctionPointer<UnhookLibraryDelegate>(
                NativeLibrary.GetExport(library, "UnhookLibrary"));

            // 实例里那份先试；200（加载不了）就拿包里自带的那份再试
            foreach (string dll in new[] { instanceDll, packageDll })
            {
                if (string.IsNullOrWhiteSpace(dll))
                {
                    continue;
                }

                // 注入器要先把 dll 读进来找入口；同目录的依赖不在默认搜索路径里 → 先把这个目录加进搜索路径
                SetDllDirectoryW(Path.GetDirectoryName(dll));

                int hookResult = hookLibrary(dll, out IntPtr hook, out IntPtr mutex);

                if (hookResult != 0)
                {
                    Logger.LogWarning("XXMI HookLibrary 失败，错误码 {Code}（dll={Dll}）", hookResult, dll);

                    // **失败也必须脱钩**：不然 3DMigotoLoader 的实例/mutex 留着，下一次直接 100
                    try
                    {
                        unhookLibrary(ref hook, ref mutex);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogDebug(ex, "XXMI 失败后脱钩也失败");
                    }

                    SetDllDirectoryW(null);

                    if (hookResult == 200)
                    {
                        continue;   // 换另一个 dll 再试
                    }

                    return false;
                }

                Logger.LogInformation("XXMI 钩子已挂上：{Dll} → {Process}", dll, processName);

                try
                {
                    // 游戏可能还没起来：WaitForInjection 内部会等进程出现再注入（超时给长一点）
                    int waitResult = await Task.Run(() => waitForInjection(dll, processName, 600), cancellationToken);
                    Logger.LogInformation("XXMI WaitForInjection 返回 {Result}", waitResult);
                    return waitResult == 0;
                }
                finally
                {
                    unhookLibrary(ref hook, ref mutex);
                    SetDllDirectoryW(null);
                    Logger.LogInformation("XXMI 钩子已脱掉");
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "XXMI 注入失败");
            return false;
        }
        finally
        {
            if (library != IntPtr.Zero)
            {
                NativeLibrary.Free(library);
            }
        }
    }
}
