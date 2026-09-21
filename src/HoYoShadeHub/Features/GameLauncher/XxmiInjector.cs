using HoYoShadeHub.Core.HoYoPlay;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    /// **用 XXMI Launcher 自己的命令行静默启动**：<c>XXMI Launcher.exe "&lt;游戏 exe&gt;" -x ZZMI -n</c>
    /// （<c>-n/--nogui</c> = 不开界面）。
    /// 这是正解：ZZMI 里那份 <c>d3d11.dll</c> 是"受控版"（ini 头写着 intended to be loaded by XXMI Launcher），
    /// 我们自己 Inject 进去它能加载但完全不初始化（连 d3d11_log.txt 都不写）；而 XXMI Launcher 有 CLI，
    /// 可以让它**在后台**把整条启动+注入流程跑完，我们不用碰它的 dll。
    /// </summary>
    public static XxmiLaunchResult LaunchViaXxmiCli(GameId gameId, string gameExePath)
    {
        string? instance = Xxmi.XxmiLocator.FindInstance(gameId.GameBiz, out string? importer);

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
    public static XxmiLaunchResult Launch(GameId gameId, string gameExePath, string? extraArguments, IReadOnlyList<string>? extraDlls = null)
    {
        string? injector = FindInjector(gameId);
        string? loader = FindLoader(gameId);

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

                foreach (string extra in Xxmi.XxmiLocator.ExtraLibraries(gameId.GameBiz))
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
    public static string? FindLoader(GameId gameId)
    {
        string? instance = Xxmi.XxmiLocator.FindInstance(gameId.GameBiz, out _);

        if (string.IsNullOrWhiteSpace(instance))
        {
            return null;
        }

        string loader = Xxmi.XxmiLocator.LoaderPath(instance);
        return File.Exists(loader) ? loader : null;
    }

    /// <summary>XXMI 的注入器 3dmloader.dll（从 MI 实例往上找 Resources\Packages\XXMI）</summary>
    public static string? FindInjector(GameId gameId)
    {
        string? instance = Xxmi.XxmiLocator.FindInstance(gameId.GameBiz, out _);

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
    public static string? FindPackageLoader(GameId gameId)
    {
        string? injector = FindInjector(gameId);

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
    public static async Task<bool> ArmAndWaitAsync(GameId gameId, string processName, CancellationToken cancellationToken)
    {
        string? injector = FindInjector(gameId);
        string? instanceDll = FindLoader(gameId);
        string? packageDll = FindPackageLoader(gameId);

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
