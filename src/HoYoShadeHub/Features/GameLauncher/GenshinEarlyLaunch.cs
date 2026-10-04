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
/// v2.3.1 Bridge and OptiScaler must enter Genshin immediately after
/// CreateProcess, before the normal launcher path has initialized its D3D stack.
/// This mirrors unlockfps_nc's CreateProcess -> inject DllList sequence.
/// The reference config does not use CREATE_SUSPENDED, so keep the process
/// running while the two DLLs are loaded.
/// </summary>
internal static class GenshinEarlyLaunch
{
    public sealed record Result(Process? Process, string? Error);

    public static Task<Result> StartAsync(string exePath, string arguments, string workingDirectory,
        string bridgeDll, string? ffx12Dll, string optiDll, Action<string>? report = null)
    {
        return Task.Run(() => Start(exePath, arguments, workingDirectory, bridgeDll, ffx12Dll, optiDll, report));
    }

    private static Result Start(string exePath, string arguments, string workingDirectory,
        string bridgeDll, string? ffx12Dll, string optiDll, Action<string>? report)
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
            report?.Invoke($"CreateProcess 后立即注入 Bridge + OptiScaler（pid {pi.dwProcessId}）");
            List<string> dlls = [bridgeDll];
            if (!string.IsNullOrWhiteSpace(ffx12Dll) && File.Exists(ffx12Dll))
            {
                dlls.Add(ffx12Dll);
                report?.Invoke("Bridge 后预加载 FFX12 SDK，再注入 OptiScaler");
            }
            dlls.Add(optiDll);
            if (!DllInjector.InjectIntoHandle(pi.hProcess, dlls, out string injectError))
            {
                TerminateProcess(pi.hProcess, 1);
                return new(null, "Bridge/OptiScaler 批量早期注入失败：" + injectError);
            }

            report?.Invoke($"Bridge + OptiScaler 已按 test DllList 顺序注入（pid {pi.dwProcessId}）");
            Process process = Process.GetProcessById(checked((int)pi.dwProcessId));
            process.EnableRaisingEvents = true;
            report?.Invoke($"Bridge + OptiScaler 已在 CreateProcess 后立即注入（pid {pi.dwProcessId}）");
            return new(process, null);
        }
        finally
        {
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfo startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

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



