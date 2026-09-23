using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>
/// 原神帧率解锁，C# 原生移植自 xiaonian233/genshin-fps-unlock（shellcode credit winTEuser）。
/// <para>
/// 扫描游戏主模块 .text 段特征 <c>8B 0D ?? ?? ?? ?? EB ?? 33 C0</c> 定位游戏内帧率变量，
/// 写入 shellcode 并启动其同步线程；同步线程 OpenProcess 回本进程读取目标帧数值。
/// 游戏提权时 Hub 也必须提权。
/// </para>
/// </summary>
internal sealed class FpsUnlocker : IDisposable
{
    private const uint ProcessCreateThread = 0x0002;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessSynchronize = 0x00100000;
    private const uint MemCommit = 0x1000;
    private const uint MemReserve = 0x2000;
    private const uint MemRelease = 0x8000;
    private const uint PageExecuteReadwrite = 0x40;
    private const uint Th32CsSnapModule = 0x00000008;
    private const uint StillActive = 259;

    private const int SyncThreadOffset = 0x50;
    private const int FpsValueRemoteOffset = 0x194;
    private const int ShellcodeSize = 0x1A0;

    // 8B 0D ?? ?? ?? ?? EB ?? 33 C0
    private static readonly byte[] Pattern = { 0x8B, 0x0D, 0x00, 0x00, 0x00, 0x00, 0xEB, 0x00, 0x33, 0xC0 };
    private static readonly bool[] PatternMask = { true, true, false, false, false, false, true, false, true, true };

    private static readonly byte[] ShellcodeConst = new byte[]
    {
0x00,0x00,0x00,0x00,0x00,0xC0,0x9C,0x66,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0x48,0x83,0xEC,0x38,0x8B,0x05,0xA6,0xFF,0xFF,0xFF,0x85,0xC0,0x74,0x5C,0x41,0x89,0xC0,0x33,0xD2,0xB9,0xFF,0xFF,0x1F,0x00,0xFF,0x15,0xA2,0xFF,0xFF,0xFF,0x85,0xC0,0x74,0x48,0x89,0xC6,0x48,0x8B,0x3D,0x8D,0xFF,0xFF,0xFF,0x0F,0x1F,0x44,0x00,0x00,0x89,0xF1,0x48,0x89,0xFA,0x4C,0x8D,0x05,0x08,0x01,0x00,0x00,0x41,0xB9,0x04,0x00,0x00,0x00,0x31,0xC0,0x48,0x89,0x44,0x24,0x20,0xFF,0x15,0x79,0xFF,0xFF,0xFF,0x85,0xC0,0x74,0x12,0xB9,0xF4,0x01,0x00,0x00,0xFF,0x15,0x72,0xFF,0xFF,0xFF,0xE8,0x5D,0x00,0x00,0x00,0xEB,0xCB,0xE8,0x76,0x00,0x00,0x00,0x48,0x83,0xC4,0x38,0xC3,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0x89,0x0D,0xBA,0x00,0x00,0x00,0x31,0xC0,0x83,0xF9,0x1E,0x74,0x0E,0x83,0xF9,0x2D,0x74,0x15,0x2E,0xB9,0xE8,0x03,0x00,0x00,0xEB,0x06,0xCC,0xB9,0x3C,0x00,0x00,0x00,0x89,0x0D,0x0B,0x00,0x00,0x00,0xC3,0x8B,0x0D,0x97,0x00,0x00,0x00,0xEB,0xF1,0xCC,0xB8,0x78,0x00,0x00,0x00,0xC3,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0x8B,0x05,0x7A,0x00,0x00,0x00,0x83,0xF8,0x2D,0x75,0x0C,0x8B,0x05,0x73,0x00,0x00,0x00,0x89,0x05,0xDA,0xFF,0xFF,0xFF,0xC3,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0x48,0x83,0xEC,0x28,0x31,0xC9,0x48,0x8D,0x15,0x33,0x00,0x00,0x00,0x4C,0x8D,0x05,0x3C,0x00,0x00,0x00,0x41,0xB9,0x10,0x00,0x00,0x00,0xFF,0x15,0xD8,0xFE,0xFF,0xFF,0x89,0xF1,0xFF,0x15,0xD8,0xFE,0xFF,0xFF,0x48,0x83,0xC4,0x28,0xC3,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0xCC,0x53,0x79,0x6E,0x63,0x20,0x66,0x61,0x69,0x6C,0x65,0x64,0x21,0x00,0x00,0x00,0x00,0x45,0x72,0x72,0x6F,0x72,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00
    };

    private readonly IntPtr _fpsValueSlot = Marshal.AllocHGlobal(4);
    private readonly CancellationTokenSource _cts = new();

    private IntPtr _process;
    private IntPtr _remoteShellcode;
    private IntPtr _gameFpsVar;
    private Task? _loopTask;
    private bool _disposed;

    /// <summary>目标帧率（写入本进程槽位，游戏内同步线程回读）</summary>
    public void SetTarget(int fps)
    {
        Marshal.WriteInt32(_fpsValueSlot, fps);
    }

    /// <summary>
    /// 附加到已启动的游戏进程：等主模块加载、扫描特征、注入 shellcode、启动同步循环。
    /// 返回 false 表示本次解锁未生效（日志/提示由调用方处理）。
    /// </summary>
    public async Task<bool> AttachAsync(Process game, int targetFps, TimeSpan timeout)
    {
        SetTarget(targetFps);

        ModuleInfo module = await WaitForBaseModuleAsync(game, timeout, _cts.Token);
        if (module.Base == IntPtr.Zero)
        {
            return false;
        }

        _process = OpenProcess(
            ProcessCreateThread | ProcessQueryInformation | ProcessVmOperation | ProcessVmWrite | ProcessVmRead | ProcessSynchronize,
            false, game.Id);
        if (_process == IntPtr.Zero)
        {
            return false;
        }

        // 读 PE 头（前 4KB），解析 .text 节
        byte[] peBuffer = new byte[0x1000];
        if (!ReadProcessMemory(_process, module.Base, peBuffer, 0x1000, out _))
        {
            return false;
        }

        int eLfanew = BitConverter.ToInt32(peBuffer, 0x3C);
        if (eLfanew <= 0 || eLfanew + 264 > peBuffer.Length || BitConverter.ToUInt32(peBuffer, eLfanew) != 0x00004550)
        {
            return false;
        }

        ushort sectionCount = BitConverter.ToUInt16(peBuffer, eLfanew + 6);
        int sectionTable = eLfanew + 264;
        int textRva = 0;
        int textSize = 0;

        for (int i = 0; i < sectionCount; i++)
        {
            int entry = sectionTable + i * 40;
            if (entry + 40 > peBuffer.Length)
            {
                break;
            }

            if (peBuffer[entry] == (byte)'.' && peBuffer[entry + 1] == (byte)'t' &&
                peBuffer[entry + 2] == (byte)'e' && peBuffer[entry + 3] == (byte)'x' &&
                peBuffer[entry + 4] == (byte)'t' && peBuffer[entry + 5] == 0)
            {
                textRva = BitConverter.ToInt32(peBuffer, entry + 12);
                textSize = BitConverter.ToInt32(peBuffer, entry + 8);
                break;
            }
        }

        if (textRva == 0 || textSize == 0)
        {
            return false;
        }

        IntPtr textRemote = module.Base + textRva;
        byte[] textLocal = new byte[textSize];
        if (!ReadProcessMemory(_process, textRemote, textLocal, (nuint)textSize, out _))
        {
            return false;
        }

        int patternOffset = ScanPattern(textLocal);
        if (patternOffset < 0)
        {
            return false;
        }

        // 8B 0D 指令：mov ecx,[rip+disp]；disp 在 +2，指令长 6
        int rip = patternOffset + 6;
        int disp = BitConverter.ToInt32(textLocal, patternOffset + 2);
        _gameFpsVar = textRemote + (rip + disp);

        byte[] shellcode = BuildShellcode();

        _remoteShellcode = VirtualAllocEx(_process, IntPtr.Zero, 0x1000, MemCommit | MemReserve, PageExecuteReadwrite);
        if (_remoteShellcode == IntPtr.Zero)
        {
            return false;
        }

        if (!WriteProcessMemory(_process, _remoteShellcode, shellcode, (nuint)shellcode.Length, out _))
        {
            return false;
        }

        IntPtr thread = CreateRemoteThread(
            _process, IntPtr.Zero, 0, _remoteShellcode + SyncThreadOffset, IntPtr.Zero, 0, out _);
        if (thread == IntPtr.Zero)
        {
            return false;
        }
        CloseHandle(thread);

        _loopTask = Task.Run(LoopAsync, _cts.Token);
        return true;
    }
    /// <summary>失败原因（AttachAsync 返回 false 时）</summary>
    public string? LastError { get; private set; }

    private byte[] BuildShellcode()
    {
        byte[] sc = (byte[])ShellcodeConst.Clone();

        IntPtr k32 = GetModuleHandle("kernel32.dll");
        IntPtr u32 = GetModuleHandle("user32.dll");

        WriteUInt32(sc, 0x00, (uint)Environment.ProcessId);
        WriteUInt64(sc, 0x08, (ulong)_fpsValueSlot.ToInt64());
        WriteUInt64(sc, 0x10, (ulong)GetProcAddress(k32, "OpenProcess").ToInt64());
        WriteUInt64(sc, 0x18, (ulong)GetProcAddress(k32, "ReadProcessMemory").ToInt64());
        WriteUInt64(sc, 0x20, (ulong)GetProcAddress(k32, "Sleep").ToInt64());
        WriteUInt64(sc, 0x28, (ulong)GetProcAddress(u32, "MessageBoxA").ToInt64());
        WriteUInt64(sc, 0x30, (ulong)GetProcAddress(k32, "CloseHandle").ToInt64());

        WriteUInt32(sc, 0xE4, 1000);
        WriteUInt32(sc, 0xEC, 60);

        // 与原工具一致的写入顺序（0x112 与相邻 qword 有重叠）
        WriteUInt64(sc, 0x110, 0xB848);
        WriteUInt64(sc, 0x118, 0x741D8B0000);
        WriteUInt64(sc, 0x120, 0xCCCCCCCCCCC31889);
        WriteUInt64(sc, 0x112, (ulong)_gameFpsVar.ToInt64());

        WriteUInt64(sc, 0x15C, 0x5C76617E8834858);
        WriteUInt64(sc, 0x164, 0xE0FF21EBFFFFFF16);

        return sc;
    }

    private static void WriteUInt32(byte[] buffer, int offset, uint value)
        => BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static void WriteUInt64(byte[] buffer, int offset, ulong value)
        => BitConverter.GetBytes(value).CopyTo(buffer, offset);

    private static int ScanPattern(byte[] data)
    {
        for (int i = 0; i <= data.Length - Pattern.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < Pattern.Length; j++)
            {
                if (PatternMask[j] && data[i + j] != Pattern[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return i;
            }
        }

        return -1;
    }

    private async Task LoopAsync()
    {
        byte[] readBuffer = new byte[4];

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                if (!GetExitCodeProcess(_process, out uint exitCode) || exitCode != StillActive)
                {
                    break;
                }

                await Task.Delay(2000, _cts.Token);

                if (!ReadProcessMemory(_process, _gameFpsVar, readBuffer, 4, out _))
                {
                    continue;
                }

                int current = BitConverter.ToInt32(readBuffer);
                if (current == -1)
                {
                    continue;
                }

                int target = Marshal.ReadInt32(_fpsValueSlot);
                if (current != target)
                {
                    WriteProcessMemory(
                        _process,
                        _remoteShellcode + FpsValueRemoteOffset,
                        BitConverter.GetBytes(target),
                        4,
                        out _);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // 单轮读写失败，下一轮继续
            }
        }
    }

    private static async Task<ModuleInfo> WaitForBaseModuleAsync(Process game, TimeSpan timeout, CancellationToken ct)
    {
        string moduleName = game.ProcessName + ".exe";
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (game.HasExited)
            {
                return default;
            }

            ModuleInfo info = TryGetBaseModule(game.Id, moduleName);
            if (info.Base != IntPtr.Zero)
            {
                return info;
            }

            await Task.Delay(50, ct);
        }

        return default;
    }

    private static ModuleInfo TryGetBaseModule(int pid, string moduleName)
    {
        IntPtr snap = CreateToolhelp32Snapshot(Th32CsSnapModule, (uint)pid);
        if (snap == new IntPtr(-1))
        {
            return default;
        }

        try
        {
            ModuleEntry32 entry = new()
            {
                dwSize = (uint)Marshal.SizeOf<ModuleEntry32>()
            };

            if (Module32FirstW(snap, ref entry))
            {
                do
                {
                    if (entry.th32ProcessID == pid &&
                        string.Equals(entry.szModule, moduleName, StringComparison.OrdinalIgnoreCase))
                    {
                        return new ModuleInfo { Base = entry.modBaseAddr, Size = entry.modBaseSize };
                    }
                }
                while (Module32NextW(snap, ref entry));
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return default;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _cts.Cancel();
        }
        catch
        {
        }

        try
        {
            _loopTask?.Wait(1500);
        }
        catch
        {
        }

        if (_process != IntPtr.Zero)
        {
            bool alive = GetExitCodeProcess(_process, out uint code) && code == StillActive;
            if (alive && _remoteShellcode != IntPtr.Zero)
            {
                // 清零 shellcode 头部的 unlocker PID，游戏内同步线程读到 0 自行退出
                WriteProcessMemory(_process, _remoteShellcode, new byte[4], 4, out _);
                VirtualFreeEx(_process, _remoteShellcode, 0, MemRelease);
            }

            CloseHandle(_process);
        }

        Marshal.FreeHGlobal(_fpsValueSlot);
        _cts.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ModuleInfo
    {
        public IntPtr Base;
        public uint Size;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ModuleEntry32
    {
        public uint dwSize;
        public uint th32ModuleID;
        public uint th32ProcessID;
        public uint GlblcntUsage;
        public uint ProccntUsage;
        public IntPtr modBaseAddr;
        public uint modBaseSize;
        public IntPtr hModule;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szModule;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExePath;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, nuint size, out nuint written);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr baseAddress, byte[] buffer, nuint size, out nuint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, nuint stackSize, IntPtr startAddress, IntPtr parameter, uint creationFlags, out uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "Module32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32FirstW(IntPtr snapshot, ref ModuleEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "Module32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Module32NextW(IntPtr snapshot, ref ModuleEntry32 entry);

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr module, [MarshalAs(UnmanagedType.LPStr)] string procName);
}
