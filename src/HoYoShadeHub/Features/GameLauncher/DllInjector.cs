using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>
/// 把任意 DLL 注入到目标进程（用户要求的「额外注入 DLL」—— OptiScaler / DLSS Enabler 那套）。

/// <para>
/// 做法就是最普通的 <c>CreateRemoteThread + LoadLibraryW</c>。前提：
/// 目标必须是 64 位（Hub 是 x64），<b>目标提权时 Hub 也得提权</b>，否则 OpenProcess 直接被拒。
/// </para>

/// <para>
/// 注意时机：这只能保证「DLL 进了进程」，不能保证它赶得上 swapchain / DLSS 初始化的那几个 hook ——
/// 这也是为什么 HoYoShade 自己的 inject.exe 要等进程一起来就注入。所以我们要在游戏进程刚出现时就注。
/// </para>
/// </summary>
internal static partial class DllInjector
{
    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmRead = 0x0010;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    /// <summary>把 <paramref name="dllPath"/> 注入进程 <paramref name="processId"/></summary>
    public static bool Inject(int processId, string dllPath, out string error)
    {
        error = string.Empty;

        if (!File.Exists(dllPath))
        {
            error = "DLL 不存在：" + dllPath;
            return false;
        }

        IntPtr process = IntPtr.Zero;
        IntPtr remote = IntPtr.Zero;
        IntPtr thread = IntPtr.Zero;

        try
        {
            process = OpenProcess(
                ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmWrite | ProcessVmRead,
                false,
                processId);

            if (process == IntPtr.Zero)
            {
                error = $"打不开游戏进程（{new Win32Exception(Marshal.GetLastWin32Error()).Message}）" +
                        " —— 游戏是管理员启动的话，Hub 也得用管理员启动。";
                return false;
            }

            byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
            remote = VirtualAllocEx(process, IntPtr.Zero, (nuint)pathBytes.Length, MemCommit | MemReserve, PageReadWrite);
            if (remote == IntPtr.Zero)
            {
                error = "在目标进程里申请内存失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            if (!WriteProcessMemory(process, remote, pathBytes, (nuint)pathBytes.Length, out _))
            {
                error = "写入目标进程失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return false;
            }

            IntPtr loadLibrary = GetProcAddress(GetModuleHandle("kernel32.dll"), "LoadLibraryW");
            if (loadLibrary == IntPtr.Zero)
            {
                error = "找不到 LoadLibraryW。";
                return false;
            }

            thread = CreateRemoteThread(process, IntPtr.Zero, 0, loadLibrary, remote, 0, out _);
            if (thread == IntPtr.Zero)
            {
                error = "创建远端线程失败：" + new Win32Exception(Marshal.GetLastWin32Error()).Message +
                        "（多半是被游戏/反作弊拦了）。";
                return false;
            }

            if (WaitForSingleObject(thread, 30_000) != 0)
            {
                error = "等远端 LoadLibraryW 超时。";
                return false;
            }

            if (!GetExitCodeThread(thread, out uint exitCode))
            {
                error = "拿不到远端线程的返回值。";
                return false;
            }

            if (exitCode == 0)
            {
                error = "LoadLibraryW 返回 0：DLL 没加载起来（位数不对 / 缺依赖 / 被拦）。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (remote != IntPtr.Zero && process != IntPtr.Zero)
            {
                VirtualFreeEx(process, remote, 0, MemRelease);
            }

            if (thread != IntPtr.Zero)
            {
                CloseHandle(thread);
            }

            if (process != IntPtr.Zero)
            {
                CloseHandle(process);
            }
        }
    }

    /// <summary>等某个进程名起来（游戏是用户自己用启动器拉起来的，所以我们得等）</summary>
    public static async Task<Process?> WaitForProcessAsync(string processName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        string name = Path.GetFileNameWithoutExtension(processName);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                Process[] hits = Process.GetProcessesByName(name);
                if (hits.Length > 0)
                {
                    return hits[0];
                }
            }
            catch
            {
                // 进程刚好退了，继续等
            }

            await Task.Delay(500, cancellationToken);
        }

        return null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, ReadOnlySpan<byte> buffer, nuint size, out nuint written);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, nuint stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeThread(IntPtr handle, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetModuleHandle(string moduleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true)]
    private static partial IntPtr GetProcAddress(IntPtr module, [MarshalAs(UnmanagedType.LPStr)] string procName);
}
