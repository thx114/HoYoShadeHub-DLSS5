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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    /// <summary>这次 XXMI 启动的结果（给界面显示用）</summary>
    public sealed record XxmiLaunchResult(bool Started, bool Injected, string Message)
    {
        public int? ProcessId { get; init; }
    }

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

    /// <summary>Before writing configuration or arming a new injector, close stale XXMI only.</summary>
    public static async Task<string?> CloseExistingLaunchersAsync(CancellationToken cancellationToken)
    {
        var processes = Process.GetProcessesByName("XXMI Launcher");
        try
        {
            var result = await HoYoShadeHub.Extensions.Services.OwnedProcessShutdown.CloseAsync(
                processes, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            Logger.LogInformation("XXMI 启动前清理：找到 {Found} 个实例，已退出 {Closed}，结束残留 {Forced}，错误 {Errors}",
                processes.Length, result.Closed, result.Forced, result.Errors.Count);
            return result.Success ? null : string.Join("; ", result.Errors);
        }
        finally { foreach (var process in processes) process.Dispose(); }
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
    public static string PrepareManualMode(GameId gameId, string? gameName, string? gameExeName = null, bool manual = true)
    {
        string? configPath = Xxmi.XxmiLocator.FindConfigPath(gameId.GameBiz, gameName, out string? importer);
        if (!string.IsNullOrWhiteSpace(AppConfig.XxmiLauncherPath))
        {
            importer = Xxmi.XxmiLocator.ImporterForGame(gameId.GameBiz, gameName);
            configPath = FindConfigForLauncher(AppConfig.XxmiLauncherPath);
        }

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
            string desiredLaunch = manual ? ManualGameLaunch : "DIRECT";
            string oldLaunch = block["game_launch"]?.GetValue<string>() ?? string.Empty;
            string oldLegacy = block["process_start_method"]?.GetValue<string>() ?? string.Empty;
            string desiredLegacy = HoYoShadeHub.Extensions.Games.XxmiLaunchConfiguration.PreserveLegacyStartMethod(oldLegacy);
            bool changed = false;

            if (!string.Equals(oldLaunch, desiredLaunch, StringComparison.OrdinalIgnoreCase))
            {
                block["game_launch"] = desiredLaunch;
                changed = true;
            }

            if (!string.Equals(oldLegacy, desiredLegacy, StringComparison.OrdinalIgnoreCase))
            {
                block["process_start_method"] = desiredLegacy;
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
                "XXMI {Importer} 启动模式已更新：game_launch {OldLaunch}→{NewLaunch}、process_start_method {OldLegacy}→{NewLegacy}、"
                + "进程名 {OldExe}(enabled={OldEnabled})→{NewExe}(enabled=true)（备份 {Backup}）",
                importer, oldLaunch, desiredLaunch, oldLegacy, desiredLegacy,
                oldExe, exeEnabled, gameExeName, backup);

            return string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "XXMI 写手动模式失败");
            return "写 XXMI 手动模式失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 把本游戏导入器的 XXMI DLL 注入模式写成 <c>SKIP</c>（= 不注入 XXMI 自己那份 d3d11.dll）。
    ///
    /// <para>
    /// 有序注入链模式下必须这样：GIMI 的 d3d11.dll 改由桥在游戏进程内按清单加载，XXMI 再注一份
    /// 会变成「Found a second copy of 3DMigoto」，而且两个注入者又回到抢顺序。XXMI 自己就把 SKIP
    /// 当合法状态（<c>core/config_manager.py</c> 里模式不可用时写的就是 SKIP），此时它照样启动、
    /// 照样管 mod，只是不注入、也不建 loader 互斥体 —— 互斥体由桥建。
    /// </para>
    ///
    /// <para>
    /// 该键**不在** XXMI 的签名保护名单里（受保护的只有 <c>unsafe_mode</c> / <c>run_pre_launch</c> /
    /// <c>custom_launch</c> / <c>run_post_load</c> / <c>extra_libraries</c>），
    /// 所以启动器改写它不会被判成配置被篡改。
    /// </para>
    ///
    /// 空串 = 成功（含「本来就是 SKIP」），否则是失败原因。
    /// </summary>
    public static string PrepareInjectModeSkip(GameId gameId, string? gameName)
    {
        // XXMI 的 Loader 注入模式：DIRECT / HOOK / SKIP
        const string injectModeKey = "xxmi_dll_inject_mode";
        const string skipMode = "SKIP";

        string? configPath = Xxmi.XxmiLocator.FindConfigPath(gameId.GameBiz, gameName, out string? importer);
        if (!string.IsNullOrWhiteSpace(AppConfig.XxmiLauncherPath))
        {
            importer = Xxmi.XxmiLocator.ImporterForGame(gameId.GameBiz, gameName);
            configPath = FindConfigForLauncher(AppConfig.XxmiLauncherPath);
        }

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

            string oldMode = block[injectModeKey]?.GetValue<string>() ?? string.Empty;
            if (string.Equals(oldMode, skipMode, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            block[injectModeKey] = skipMode;

            string backup = configPath + ".bak-before-inject-skip";
            if (!File.Exists(backup))
            {
                File.Copy(configPath, backup);
            }

            File.WriteAllText(configPath,
                root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            Logger.LogInformation(
                "XXMI {Importer} 注入模式 {Old}→{New}：GIMI 改由桥在游戏进程内按清单加载（备份 {Backup}）",
                importer, string.IsNullOrWhiteSpace(oldMode) ? "(未设置)" : oldMode, skipMode, backup);

            return string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "XXMI 写注入模式失败");
            return "写 XXMI 注入模式失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 把注入模式还原成「有序注入链之前」的样子（原值从 <c>.bak-before-inject-skip</c> 备份里读）。
    ///
    /// <para>
    /// 用在没走链的分支上：那条分支靠 XXMI 自己注入 GIMI，如果还留着上次链写下的 SKIP，
    /// 就会静默地完全没有模型替换（不报错、不提示，只是 mod 不生效）。
    /// 备份里没记过这个键时宁可不猜（返回空串、什么都不写）。
    /// </para>
    ///
    /// 空串 = 成功或本来就没动过，否则是失败原因。
    /// </summary>
    public static string RestoreInjectMode(GameId gameId, string? gameName)
    {
        const string injectModeKey = "xxmi_dll_inject_mode";

        string? configPath = Xxmi.XxmiLocator.FindConfigPath(gameId.GameBiz, gameName, out string? importer);
        if (!string.IsNullOrWhiteSpace(AppConfig.XxmiLauncherPath))
        {
            importer = Xxmi.XxmiLocator.ImporterForGame(gameId.GameBiz, gameName);
            configPath = FindConfigForLauncher(AppConfig.XxmiLauncherPath);
        }

        if (configPath is null || importer is null)
        {
            return string.Empty;
        }

        string backup = configPath + ".bak-before-inject-skip";
        if (!File.Exists(backup))
        {
            return string.Empty;
        }

        try
        {
            string? oldMode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(backup))
                ?["Importers"]?[importer]?["Importer"]?[injectModeKey]?.GetValue<string>();

            if (string.IsNullOrWhiteSpace(oldMode))
            {
                return string.Empty;
            }

            System.Text.Json.Nodes.JsonNode? root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(configPath));
            if (root?["Importers"]?[importer]?["Importer"] is not System.Text.Json.Nodes.JsonObject block)
            {
                return "XXMI 配置里没有导入器 " + importer;
            }

            string current = block[injectModeKey]?.GetValue<string>() ?? string.Empty;
            if (string.Equals(current, oldMode, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            block[injectModeKey] = oldMode;

            File.WriteAllText(configPath,
                root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            Logger.LogInformation("XXMI {Importer} 注入模式 {Cur}→{Old} 还原：本次不走有序注入链，GIMI 交回 XXMI 自己注入",
                importer, string.IsNullOrWhiteSpace(current) ? "(未设置)" : current, oldMode);

            return string.Empty;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "XXMI 还原注入模式失败");
            return "还原 XXMI 注入模式失败：" + ex.Message;
        }
    }

    /// <summary>成功唤起且经过1秒交接；不代表已经检测到游戏或完成模型注入。</summary>
    public sealed record XxmiArmResult(bool Armed, string Message);


    internal static string? FindConfigForLauncher(string launcher)
    {
        for (DirectoryInfo? dir = new(Path.GetDirectoryName(Path.GetFullPath(launcher))!); dir is not null; dir = dir.Parent)
        {
            string config = Path.Combine(dir.FullName, "XXMI Launcher Config.json");
            if (File.Exists(config)) return config;
        }
        return null;
    }

    private static string? ResolveLauncher(GameId gameId, string? gameName, out string? importer)
    {
        importer = Xxmi.XxmiLocator.ImporterForGame(gameId.GameBiz, gameName);
        if (!string.IsNullOrWhiteSpace(AppConfig.XxmiLauncherPath))
            return File.Exists(AppConfig.XxmiLauncherPath) ? AppConfig.XxmiLauncherPath : null;
        string? instance = Xxmi.XxmiLocator.FindInstance(gameId.GameBiz, gameName, out importer);
        if (instance is null) return null;
        for (DirectoryInfo? dir = new(instance); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "Resources", "Bin", "XXMI Launcher.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>手动模式唤起 XXMI 注入器，等一秒后交回 Hub 注入/启动流程；不杀 XXMI 进程。</summary>
    public static async Task<XxmiArmResult> ArmForManualLaunchAsync(
        GameId gameId, string? gameName, string? gameExePath, CancellationToken cancellationToken)
    {
        try
        {
            string? launcher = ResolveLauncher(gameId, gameName, out string? importer);
            if (launcher is null || importer is null) return new(false, "找不到 XXMI Launcher 或对应导入器，请检查 XXMI 设置");

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

            using (started)
            {
                // Manual launch starts XXMI's injector, not the game. Give it one
                // second, then continue Hub's own injection + game launch flow.
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                if (started.HasExited && started.ExitCode != 0)
                    return new(false, $"XXMI 唤起失败（退出码 {started.ExitCode}）");
                Logger.LogInformation("XXMI 手动注入器已唤起并等待 1 秒；继续启动器注入和游戏启动，不等待游戏进程出现");
                return new(true, "XXMI 手动注入器已唤起；继续启动游戏");
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "唤起 XXMI 失败");
            return new(false, "唤起 XXMI 失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 按 XXMI 的方式启动游戏：**以挂起方式起进程 → 用 3dmloader.dll 的 <c>Inject(pid, dll, timeout)</c>
    /// 把 3DMigoto 的 d3d11.dll 注进目标进程 → 恢复线程**。
    /// 为什么这样能成：<c>Inject</c> 是在**目标进程里** CreateRemoteThread(LoadLibraryW)，
    /// DllMain 在游戏进程里跑，所以不会有 <c>HookLibrary</c> 那个 1114（DLL_INIT_FAILED）问题；
    /// 而且进程是挂起的，注入发生在 D3D 初始化之前。
    /// </summary>
    public static XxmiLaunchResult Launch(GameId gameId, string gameExePath, string? gameName, string? extraArguments, IReadOnlyList<string>? extraDlls = null, bool pairedGraphicsStack = false)
    {
        string? injector = FindInjector(gameId, gameName);
        string? loader = FindLoader(gameId, gameName);

        if (injector is null || loader is null)
        {
            return new XxmiLaunchResult(false, false, "找不到 XXMI 的注入器或 d3d11.dll（检查「模型替换」页里的实例目录）");
        }

        if (pairedGraphicsStack && !HoYoShadeHub.Extensions.Games.StarRailXxmiLaunchRouting.HasPairedLoaderExports(loader))
        {
            return new XxmiLaunchResult(false, false, "SRMI d3d11.dll 缺少配套 DX12 输出导出；尚未创建游戏进程，请检查配套 SRMI 版本。");
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

        // 生命周期失败：null = 恢复/清理都正常。存「为什么失败」而不是一个 bool ——
        // 界面报「已启动」而进程其实没跑起来，是这条路线最容易骗人的地方。
        string? lifecycleFailure = null;
        string lifecycleNote = string.Empty;

        // 3DMigoto 的 loader（3dmloader/XXMI Launcher）会创建 "Local\3DMigotoLoader" 这个互斥体，
        // 被注入的 DLL 靠它判断"loader 在不在" —— 我们注入时也建一个，等价于告诉它 loader 在场。
        // （XXMI 的 d3d11.dll 是受控版：能加载、但缺这个上下文就完全不动。）
        IntPtr loaderMutex = CreateMutexA(IntPtr.Zero, false, "Local\\3DMigotoLoader");
        Logger.LogInformation("XXMI loader mutex: {Handle} (err={Error})", loaderMutex, Marshal.GetLastWin32Error());
        if (loaderMutex == IntPtr.Zero)
        {
            int mutexError = Marshal.GetLastWin32Error();
            uint terminateWait = TerminateAndConfirm(processInformation.hProcess, out bool terminated);
            string note = HoYoShadeHub.Extensions.Games.InjectionLifecyclePolicy.DescribeTerminate(terminated, terminateWait);
            CloseHandle(processInformation.hThread);
            CloseHandle(processInformation.hProcess);
            return new XxmiLaunchResult(false, false, $"无法建立 XXMI loader 上下文（错误码 {mutexError}）；{note}。");
        }


        try
        {
            IntPtr library = NativeLibrary.Load(injector);

            try
            {
                InjectDelegate inject = Marshal.GetDelegateForFunctionPointer<InjectDelegate>(
                    NativeLibrary.GetExport(library, "Inject"));

                if (pairedGraphicsStack)
                {
                    // Paired 3DMigoto needs DXGI available before its DllMain.
                    string dxgi = Path.Combine(Environment.SystemDirectory, "dxgi.dll");
                    int dxgiCode = inject((uint)processInformation.dwProcessId, dxgi, 30);
                    if (dxgiCode != 0) throw new InvalidOperationException($"DXGI 预加载失败（{dxgiCode}）");
                }
                injectCode = inject((uint)processInformation.dwProcessId, loader, 30);
                injected = injectCode == 0;
                Logger.LogInformation("XXMI Inject(pid={Pid}, dll={Dll}) -> {Code}", processInformation.dwProcessId, loader, injectCode);

                // 顺序很重要：**先 3DMigoto，再注 ReShade 这类额外库**（XXMI 也是这个顺序）。
                // 反过来先注 ReShade 的话，ReShade 先占了 d3d11，3DMigoto 的代理 dll 在目标进程里
                // LoadLibraryW 会失败（Inject 返回 600，用户实测过）。
                List<string> extras = [];

                IEnumerable<string> configuredExtras = pairedGraphicsStack ? Array.Empty<string>() : Xxmi.XxmiLocator.ExtraLibraries(gameId.GameBiz, gameName);
                foreach (string extra in configuredExtras)
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
                    if (pairedGraphicsStack && !injected) break;
                    int extraCode = inject((uint)processInformation.dwProcessId, extra, 30);
                    Logger.LogInformation("XXMI Inject extra(pid={Pid}, dll={Dll}) -> {Code}", processInformation.dwProcessId, extra, extraCode);
                    if (pairedGraphicsStack && extraCode != 0)
                    {
                        injected = false;
                        injectCode = extraCode;
                        break;
                    }
                }
            }
            finally
            {
                NativeLibrary.Free(library);
            }
        }
        catch (Exception ex)
        {
            if (pairedGraphicsStack) injected = false;
            Logger.LogWarning(ex, "XXMI 注入失败");
        }
        finally
        {
            // A failed dedicated stack must not resume a partially configured child.
            // This handle is only the new suspended process created by this call.
            if (pairedGraphicsStack && !injected)
            {
                // 等它真的退出：不等的话，下一次启动的"游戏已经在运行"判断可能把这个
                // 正在退出的进程当成还在跑。结果要进日志/提示 —— 终止失败时不许假称已清理。
                uint terminateWait = TerminateAndConfirm(processInformation.hProcess, out bool terminated);
                lifecycleNote = HoYoShadeHub.Extensions.Games.InjectionLifecyclePolicy
                    .DescribeTerminate(terminated, terminateWait);
                Logger.LogWarning("配套图形栈注入失败：挂起进程（pid={Pid}）{Note}（err={Error}）",
                    processInformation.dwProcessId, lifecycleNote, Marshal.GetLastWin32Error());
            }
            else
            {
                // ResumeThread 返回的是**之前的挂起计数**，不是成败标记：
                //   0 = 本来就没挂起；1 = 正常从挂起恢复；>1 = 还剩别的挂起计数（进程仍然没跑）；
                //   0xFFFFFFFF = 调用失败。
                // 只判 0xFFFFFFFF 会把 ">1" 当成成功：界面报"已启动"，游戏却永远挂在初始状态。
                uint previousSuspendCount = ResumeThread(processInformation.hThread);
                // Only release the one suspension owned by our CreateProcess call.
                // Additional counts belong to other components; do not consume them.

                HoYoShadeHub.Extensions.Games.ResumeThreadOutcome resumeOutcome =
                    HoYoShadeHub.Extensions.Games.InjectionLifecyclePolicy.ClassifyResume(previousSuspendCount);
                if (resumeOutcome != HoYoShadeHub.Extensions.Games.ResumeThreadOutcome.Running)
                {
                    lifecycleFailure = resumeOutcome == HoYoShadeHub.Extensions.Games.ResumeThreadOutcome.StillSuspended
                        ? $"ResumeThread 报告进程仍在挂起（剩余计数 {previousSuspendCount - 1}）"
                        : "ResumeThread 调用失败";
                    uint terminateWait = TerminateAndConfirm(processInformation.hProcess, out bool terminated);
                    lifecycleNote = HoYoShadeHub.Extensions.Games.InjectionLifecyclePolicy
                        .DescribeTerminate(terminated, terminateWait);
                    Logger.LogWarning("恢复游戏进程失败（pid={Pid}，{Reason}）：{Note}（err={Error}）",
                        processInformation.dwProcessId, lifecycleFailure, lifecycleNote, Marshal.GetLastWin32Error());
                }
            }

            if (loaderMutex != IntPtr.Zero)
            {
                CloseHandle(loaderMutex);
            }
            CloseHandle(processInformation.hThread);
            CloseHandle(processInformation.hProcess);
        }

        if (lifecycleFailure is not null)
        {
            return new XxmiLaunchResult(false, false,
                $"图形栈已就绪但恢复游戏进程失败（{lifecycleFailure}）：{lifecycleNote}（pid {processInformation.dwProcessId}）；请重试");
        }

        if (injected)
        {
            return new XxmiLaunchResult(true, true, $"已按 XXMI 方式启动：注入 {Path.GetFileName(loader)} 成功（进程 {processInformation.dwProcessId}）")
                { ProcessId = processInformation.dwProcessId };
        }

        if (pairedGraphicsStack)
            return new XxmiLaunchResult(false, false,
                $"配套图形栈注入失败（Inject 返回 {injectCode}）；新建的挂起进程未恢复：{lifecycleNote}");

        // Inject 的返回码（源码里的定义）：100 进程打不开 / 110 dll 路径不对 / 120·130 找不到 kernel32·LoadLibraryW
        // / 200 远程内存分配失败 / 300 写 dll 路径失败 / 400 建远程线程失败 / 500 超时 / 600 dll 加载失败 / 700 未知
        return new XxmiLaunchResult(true, false, $"游戏已启动，但 XXMI 注入失败（Inject 返回 {injectCode}）");
    }

    /// <summary>
    /// 终止进程并**确认**它退出（返回 WaitForSingleObject 的结果；终止调用本身失败时返回 WaitFailed）。
    /// 不能只调 TerminateProcess 就当已清理：日志/提示里写"已终止"必须是真退了。
    /// </summary>
    private static uint TerminateAndConfirm(IntPtr processHandle, out bool terminated)
    {
        terminated = TerminateProcess(processHandle, 1);
        return terminated
            ? WaitForSingleObject(processHandle, 2000)
            : HoYoShadeHub.Extensions.Games.InjectionLifecyclePolicy.WaitFailed;
    }

    /// <summary>这个游戏的 XXMI 加载器（3DMigoto 的 d3d11.dll）；找不到返回 null</summary>
    /// <summary>Restore the official launcher path from the user's known coexistence run.</summary>
    public static XxmiLaunchResult LaunchOfficialBaseline(GameId gameId, string? displayName, string exePath)
    {
        string? launcher = ResolveLauncher(gameId, displayName, out string? importer);
        if (launcher is null || importer is null) return new(false, false, "找不到 XXMI Launcher 或对应导入器，请检查 XXMI 设置");
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = launcher, WorkingDirectory = Path.GetDirectoryName(launcher),
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            };
            info.ArgumentList.Add(exePath); info.ArgumentList.Add("-x");
            info.ArgumentList.Add(importer); info.ArgumentList.Add("-n");
            using Process? started = Process.Start(info);
            if (started is null) return new(false, false, "XXMI 官方启动器未能启动");
            Logger.LogInformation("XXMI official baseline launch: importer {Importer}, pid {Pid}, target {Exe}; XXMI owns game creation", importer, started.Id, exePath);
            return new(true, false, "已唤起 XXMI 官方启动链；模型注入尚未验证");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "XXMI official baseline launch failed");
            return new(false, false, "XXMI 官方启动失败：" + ex.Message);
        }
    }

    /// <summary>Local test: use XXMI's direct injection API on the existing game PID, last.</summary>
    public static XxmiLaunchResult InjectLast(GameId gameId, string? gameName, int pid, string loader)
    {
        string? injector = FindInjector(gameId, gameName);
        if (injector is null || !File.Exists(loader))
            return new(false, false, "找不到 XXMI 注入器或模型代理 DLL");
        if (!DllInjector.IsProcessAlive(pid))
            return new(false, false, $"游戏进程 {pid} 已退出，未执行 XXMI 最后注入");
        IntPtr library = IntPtr.Zero;
        IntPtr loaderMutex = CreateMutexA(IntPtr.Zero, false, LoaderMutexName);
        try
        {
            library = NativeLibrary.Load(injector);
            var inject = Marshal.GetDelegateForFunctionPointer<InjectDelegate>(NativeLibrary.GetExport(library, "Inject"));
            int code = inject(checked((uint)pid), loader, 10);
            Logger.LogInformation("XXMI last direct Inject: pid {Pid}, dll {Dll}, result {Code}", pid, loader, code);
            return new(true, code == 0, code == 0
                ? $"XXMI 最后注入 API 返回成功（pid {pid}），模型效果仍需确认"
                : $"XXMI 最后注入返回错误码 {code}（pid {pid}）");
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "XXMI last direct injection");
            return new(true, false, "XXMI 最后注入失败：" + ex.Message);
        }
        finally
        {
            if (library != IntPtr.Zero) NativeLibrary.Free(library);
            if (loaderMutex != IntPtr.Zero) CloseHandle(loaderMutex);
        }
    }

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
