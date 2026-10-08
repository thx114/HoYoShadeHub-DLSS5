using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>
/// v2.3.1 Bridge, OptiScaler, and ReShade enter Genshin right after CreateProcess through the parent
/// process handle, before anti-cheat (mhyprot/HoYoKProtect) blocks external
/// VirtualAllocEx/OpenProcess.
///
/// <para>
/// **这条路径有初始化竞态，不要当成保证**：这里是 <c>CreateProcess(..., 0, ...)</c>（**非挂起**），
/// 游戏从创建那一刻就在跑 —— 它可能在我们注完之前就把 dxgi 带进来、把 D3D11 设备/交换链建好，
/// 先到的那层就没赶上 hook。链式的 <c>wait dxgi.dll</c> / <c>wait swapchain</c> 只是让桥**在进程内**
/// 按数据顺序补位，缓解竞态，并不消除它（这些等待要等桥的 DllMain 跑起来才开始）。真要消掉得改成
/// <c>CREATE_SUSPENDED</c> → 注入 → <c>ResumeThread</c>（即 XxmiInjector 那条挂起路线），
/// 那会改动注入时机，需另行评估（含反作弊侧影响），不能顺手改。
/// </para>
///
/// <para>
/// <paramref name="chainOwnsStack"/> 为 true 时只注桥这一个 DLL：其余各层由桥在游戏进程内
/// 按 <c>Dx11FsrBridge.chain.txt</c> 的行序 <c>LoadLibraryW</c>（见
/// <c>OptiScalerRuntime.WriteFsrBridgeChain</c>）。这样外部只做一次注入，顺序是数据而不是
/// 抢时间，也不怕游戏在 0.5 秒就把 D3D11 设备建好。链里含 3DMigoto（GIMI 由桥在游戏进程内加载，所以 XXMI 注入模式要设成 SKIP）。
/// </para>
/// </summary>
internal static class GenshinEarlyLaunch
{
    public sealed record Result(Process? Process, string? Error);

    public static Task<Result> StartAsync(string exePath, string arguments, string workingDirectory,
        string bridgeDll, string? ffx12Dll, string optiDll, string? shadeDll = null, Action<string>? report = null,
        bool chainOwnsStack = false)
    {
        return Task.Run(() => Start(exePath, arguments, workingDirectory, bridgeDll, ffx12Dll, optiDll, shadeDll, report, chainOwnsStack));
    }

    private static Result Start(string exePath, string arguments, string workingDirectory,
        string bridgeDll, string? ffx12Dll, string optiDll, string? shadeDll, Action<string>? report, bool chainOwnsStack)
    {
        if (!File.Exists(exePath))
            return new(null, "找不到原神主程序：" + exePath);

        var si = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() };
        var pi = new ProcessInformation();
        var commandLine = new StringBuilder('"' + exePath + '"' + (string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments));

        if (!CreateProcess(exePath, commandLine, IntPtr.Zero, IntPtr.Zero, false, 0,
                IntPtr.Zero, workingDirectory, ref si, out pi))
        {
            return new(null, $"CreateProcess 失败：{new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        try
        {
            report?.Invoke($"CreateProcess 后由父句柄立即注入图形栈（pid {pi.dwProcessId}）");
            List<string> dlls = [bridgeDll];

            if (chainOwnsStack)
            {
                // 有序注入链：外部只注桥一个，OptiScaler/ReShade 等由桥在游戏进程内按清单顺序
                // LoadLibraryW。少一次外部注入、顺序固定，也不跟反作弊那不到 1 秒的窗口赛跑。
                // （链里含 3DMigoto：GIMI 由桥在游戏进程内按清单加载，XXMI 那边设成 SKIP 避免两份）
                report?.Invoke("有序注入链模式：外部只注桥，OptiScaler/ReShade 由桥在游戏进程内按清单顺序加载");
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(ffx12Dll) && File.Exists(ffx12Dll))
                {
                    dlls.Add(ffx12Dll);
                    report?.Invoke("Bridge 后预加载 FFX12 SDK，再注入 OptiScaler");
                }
                dlls.Add(optiDll);
                if (!string.IsNullOrWhiteSpace(shadeDll) && File.Exists(shadeDll))
                {
                    dlls.Add(shadeDll);
                    report?.Invoke($"OptiScaler 后预加载 {Path.GetFileName(shadeDll)}");
                }
            }

            if (!DllInjector.InjectIntoHandle(pi.hProcess, dlls, out string injectError))
            {
                // 注入失败：游戏是**非挂起**起来的，它已经在跑，必须收掉并确认退出 ——
                // 否则会留下一个没挂任何东西的游戏进程占着文件和账号，用户还以为没启动。
                uint terminateWait = TerminateAndConfirm(pi.hProcess, out bool terminated);
                string termination = HoYoShadeHub.Extensions.Games.InjectionLifecyclePolicy
                    .DescribeTerminate(terminated, terminateWait);
                report?.Invoke($"图形栈注入失败，已尝试收掉游戏进程：{termination}");
                return new(null, "图形栈批量早期注入失败：" + injectError + "；" + termination);
            }

            Process process;
            try
            {
                process = Process.GetProcessById(checked((int)pi.dwProcessId));
            }
            catch (ArgumentException)
            {
                return new(null, $"图形栈注入调用结束后游戏已退出（pid {pi.dwProcessId}），启动未完成；请查看本次会话日志。");
            }
            process.EnableRaisingEvents = true;

            // 注入调用成功 ≠ 各层都加载/初始化成功：LoadLibraryW 返回非 0 只说明 DLL 进了进程。
            // 链模式下后续各层（GIMI/OptiScaler/ReShade）是桥在游戏进程内按清单加载的，
            // 成没成只能看游戏进程里的桥日志 —— 这里不报"全量注入完成"。
            if (chainOwnsStack)
            {
                report?.Invoke($"已把桥交给游戏进程（pid {pi.dwProcessId}）：链模式下其余各层由桥按 Dx11FsrBridge.chain.txt 加载，" +
                    "加载结果以游戏进程内的桥日志为准");
            }
            else
            {
                report?.Invoke($"已向游戏进程（pid {pi.dwProcessId}）提交 {dlls.Count} 个 DLL 的注入调用" +
                    "（LoadLibraryW 返回非 0 只代表调用成功，不保证各层初始化成功）");
            }

            return new(process, null);
        }
        finally
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }

    /// <summary>
    /// 终止进程并**确认**它退出（返回 WaitForSingleObject 的结果；终止调用本身失败时返回 WaitFailed）。
    /// 这里是非挂起创建、进程在跑，失败清理不能只发一个 TerminateProcess 就当已收掉。
    /// </summary>
    private static uint TerminateAndConfirm(IntPtr processHandle, out bool terminated)
    {
        terminated = TerminateProcess(processHandle, 1);
        return terminated
            ? WaitForSingleObject(processHandle, 2000)
            : HoYoShadeHub.Extensions.Games.InjectionLifecyclePolicy.WaitFailed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    /// <summary>等进程退出（失败清理时用来**确认**它真的退了，而不是只发一个终止请求）</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? reserved;
        public string? desktop;
        public string? title;
        public int x;
        public int y;
        public int xSize;
        public int ySize;
        public int xCountChars;
        public int yCountChars;
        public int fillAttribute;
        public int flags;
        public short showWindow;
        public short reserved2;
        public IntPtr reserved2Ptr;
        public IntPtr stdin;
        public IntPtr stdout;
        public IntPtr stderr;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}
