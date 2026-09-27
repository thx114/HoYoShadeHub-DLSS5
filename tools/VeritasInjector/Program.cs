// VeritasInjector — 星铁 Veritas（伤害统计）最小注入器（命令行版）
//
// 与 Hub 注入路径完全一致：等进程（500ms 轮询，排除已试 pid）→ 等稳（存活 >= 阈值
// 且主窗口出现，窗口最多比阈值晚 6s，取不到就按时间放行）→ CreateRemoteThread +
// LoadLibraryW → 8s 存活确认 → 没了就换新 pid 重试（最多 3 次，共用 20 分钟预算）。
//
// 用法：
//   VeritasInjector.exe                  双击运行：dll 取同目录 veritas.dll，预热 4s，结束等回车
//   VeritasInjector.exe 0                cmd 模式：预热 0s（复现「注太早带崩游戏」），结束立即退出
//   VeritasInjector.exe C:\x\veritas.dll 8   指定 dll 和预热秒
//
// 只要参数里出现任意一个（哪怕只是预热秒数），结束就立即退出，方便 cmd 里取 %ERRORLEVEL%。
// 编译：build.cmd（用系统自带 csc，.NET Framework 4.8 运行时，无需装任何 SDK）

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace VeritasInjector
{
    internal static class Program
    {
        const string CacheRoot = @"D:\APPS\HoYoShadeHub\Cache\modules\veritas\veritas";
        const string ProcessName = "StarRail";
        const int SurvivalSeconds = 8;      // Hub InjectExtraDllsAsync 的 8 秒存活确认
        const int MaxAttempts = 3;
        const int WindowGraceSeconds = 6;   // Hub InjectionWindowGraceSeconds

        static void Log(string msg)
        {
            Console.WriteLine("[{0:HH:mm:ss.fff}] {1}", DateTime.Now, msg);
        }

        static void Log(string format, params object[] args)
        {
            Log(string.Format(format, args));
        }

        static int Done(int code, bool interactive)
        {
            if (interactive)
            {
                Log("[*] 按回车退出…");
                Console.ReadLine();
            }
            return code;
        }

        static int Main(string[] args)
        {
            bool interactive = args.Length == 0;

            string dllPath = null;
            int warmup = 4;                 // Hub 全局默认 inject_warmup_seconds = 4
            foreach (string a in args)
            {
                int n;
                if (int.TryParse(a, out n))
                {
                    warmup = Math.Max(0, n);
                }
                else
                {
                    dllPath = a;
                }
            }

            Log("[*] VeritasInjector — 星铁 Veritas 最小注入器（复刻 Hub 注入路径）");

            // ---------- 1. 定位 veritas.dll：旁边优先，缓存最新版兜底 ----------
            if (dllPath == null)
            {
                string beside = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "veritas.dll");
                if (File.Exists(beside))
                {
                    dllPath = beside;
                }
                else
                {
                    dllPath = FindNewestFromCache();
                    if (dllPath != null)
                    {
                        Log("[*] 旁边没有 veritas.dll，改用缓存最新版");
                    }
                }
            }
            if (dllPath == null || !File.Exists(dllPath))
            {
                Log("[x] DLL 不存在（旁边和缓存都没找到）：{0}", dllPath == null ? "(未指定)" : dllPath);
                return Done(2, interactive);
            }

            FileInfo dllItem = new FileInfo(dllPath);
            Log("[*] DLL  : {0}  ({1:n0} KB, 改于 {2:yyyy-MM-dd HH:mm})",
                dllItem.FullName, dllItem.Length / 1024.0, dllItem.LastWriteTime);
            Log("[*] 目标 : {0}.exe   预热 {1}s（0=立即）   存活确认 {2}s", ProcessName, warmup, SurvivalSeconds);

            // ---------- 2. 主循环 ----------
            HashSet<int> exclude = new HashSet<int>();
            DateTime deadline = DateTime.UtcNow.AddMinutes(20);

            for (int attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                string tried = string.Empty;
                foreach (int pid in exclude) { tried += (tried.Length > 0 ? "," : "") + pid; }
                Log("--- 第 {0}/{1} 次尝试：等 {2} 进程出现（排除已试过 pid：{3}）---",
                    attempt, MaxAttempts, ProcessName, tried.Length > 0 ? tried : "无");

                Process target = WaitProcess(deadline, exclude);
                if (target == null)
                {
                    Log("[x] 没等到进程（20 分钟预算用完）");
                    return Done(1, interactive);
                }
                Log("[*] 目标 pid = {0}", target.Id);

                if (!WaitSteady(target, warmup))
                {
                    exclude.Add(target.Id);
                    target.Dispose();
                    Log("[!] 进程在等待期间退出（像是壳进程/更新重启），换新进程重试");
                    continue;
                }

                string err = Inject(target.Id, dllItem.FullName);
                if (err != null)
                {
                    Log("[x] 注入失败：{0}", err);
                    target.Dispose();
                    return Done(1, interactive);
                }
                Log("[√] LoadLibraryW ok（pid {0}）", target.Id);

                if (SurvivalSeconds > 0)
                {
                    Log("[*] 等 {0}s 确认进程存活…", SurvivalSeconds);
                    Thread.Sleep(SurvivalSeconds * 1000);
                    if (!IsAlive(target))
                    {
                        exclude.Add(target.Id);
                        target.Dispose();
                        Log("[!] 注入后进程没了（被带崩/壳进程被换），换新进程重试");
                        continue;
                    }
                    Log("[√] 进程存活 → 注入成功（veritas.log 应有本次条目）");
                }

                target.Dispose();
                return Done(0, interactive);
            }

            Log("[x] {0} 次尝试全部失败", MaxAttempts);
            return Done(1, interactive);
        }

        static string FindNewestFromCache()
        {
            if (!Directory.Exists(CacheRoot))
            {
                return null;
            }

            string best = null;
            Version bestVer = null;
            foreach (string dir in Directory.GetDirectories(CacheRoot))
            {
                string dll = Path.Combine(dir, "veritas.dll");
                if (!File.Exists(dll))
                {
                    continue;
                }

                Version v;
                if (!Version.TryParse(Path.GetFileName(dir), out v))
                {
                    v = new Version(0, 0);
                }
                if (bestVer == null || v > bestVer)
                {
                    bestVer = v;
                    best = dll;
                }
            }
            return best;
        }

        static bool IsAlive(Process proc)
        {
            try { return !proc.HasExited; }
            catch { return false; }
        }

        // 等进程出现（500ms 轮询，跳过 exclude 里的 pid），超时返回 null
        static Process WaitProcess(DateTime deadline, HashSet<int> exclude)
        {
            while (DateTime.UtcNow < deadline)
            {
                Process[] hits = Process.GetProcessesByName(ProcessName);
                Process picked = null;
                foreach (Process hit in hits)
                {
                    if (!exclude.Contains(hit.Id))
                    {
                        picked = hit;
                        continue;
                    }
                    hit.Dispose();
                }
                if (picked != null)
                {
                    return picked;
                }
                Thread.Sleep(500);
            }
            return null;
        }

        // 与 Hub WaitForInjectionSteadyAsync 同语义：存活 >= 阈值 且 主窗口出现；
        // 窗口最多比阈值晚 6s，一直取不到就按时间放行。阈值 0 = 只确认活着。
        static bool WaitSteady(Process proc, int threshold)
        {
            if (threshold <= 0)
            {
                return IsAlive(proc);
            }

            DateTime aliveSinceUtc = DateTime.UtcNow;
            try { aliveSinceUtc = proc.StartTime.ToUniversalTime(); }
            catch { }

            DateTime timeOnlyDeadlineUtc = aliveSinceUtc.AddSeconds(threshold + WindowGraceSeconds);
            DateTime nextLogUtc = DateTime.MinValue;

            while (true)
            {
                if (!IsAlive(proc))
                {
                    return false;
                }

                DateTime now = DateTime.UtcNow;
                double aliveSeconds = (now - aliveSinceUtc).TotalSeconds;
                if (aliveSeconds < 0) { aliveSeconds = 0; }

                bool hasWindow = false;
                try
                {
                    proc.Refresh();
                    hasWindow = proc.MainWindowHandle != IntPtr.Zero;
                }
                catch { }

                if (now >= nextLogUtc)
                {
                    Log("    等稳：alive {0:n1}s / window={1}", aliveSeconds, hasWindow);
                    nextLogUtc = now.AddSeconds(2);
                }

                if (aliveSeconds >= threshold && (hasWindow || now >= timeOnlyDeadlineUtc))
                {
                    Log("    稳了（{0:n1}s，window={1}）→ 注入", aliveSeconds, hasWindow);
                    return true;
                }

                Thread.Sleep(200);
            }
        }

        // ---------- 注入原语：与 Hub DllInjector 完全一致的 CreateRemoteThread + LoadLibraryW ----------
        const uint PROCESS_CREATE_THREAD = 0x0002;
        const uint PROCESS_QUERY_INFORMATION = 0x0400;
        const uint PROCESS_VM_OPERATION = 0x0008;
        const uint PROCESS_VM_WRITE = 0x0020;
        const uint PROCESS_VM_READ = 0x0010;
        const uint MEM_COMMIT = 0x1000;
        const uint MEM_RESERVE = 0x2000;
        const uint MEM_RELEASE = 0x8000;
        const uint PAGE_READWRITE = 0x04;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr VirtualAllocEx(IntPtr proc, IntPtr addr, UIntPtr size, uint type, uint prot);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool WriteProcessMemory(IntPtr proc, IntPtr addr, byte[] buf, UIntPtr size, out UIntPtr written);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool VirtualFreeEx(IntPtr proc, IntPtr addr, UIntPtr size, uint type);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr CreateRemoteThread(IntPtr proc, IntPtr attr, UIntPtr stack, IntPtr start, IntPtr param, uint flags, out uint tid);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern uint WaitForSingleObject(IntPtr handle, uint ms);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetExitCodeThread(IntPtr handle, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandleW(string name);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        static string LastError()
        {
            return new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;
        }

        // 成功返回 null，失败返回错误信息
        static string Inject(int pid, string dllPath)
        {
            IntPtr proc = IntPtr.Zero;
            IntPtr remote = IntPtr.Zero;
            IntPtr thread = IntPtr.Zero;
            try
            {
                proc = OpenProcess(PROCESS_CREATE_THREAD | PROCESS_QUERY_INFORMATION | PROCESS_VM_OPERATION |
                                   PROCESS_VM_WRITE | PROCESS_VM_READ, false, pid);
                if (proc == IntPtr.Zero)
                {
                    return "打不开游戏进程（" + LastError() + "）—— 游戏是管理员启动的话，本程序也要管理员运行";
                }

                byte[] pathBytes = Encoding.Unicode.GetBytes(dllPath + "\0");
                remote = VirtualAllocEx(proc, IntPtr.Zero, (UIntPtr)pathBytes.Length, MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
                if (remote == IntPtr.Zero)
                {
                    return "在目标进程里申请内存失败：" + LastError();
                }

                UIntPtr written;
                if (!WriteProcessMemory(proc, remote, pathBytes, (UIntPtr)pathBytes.Length, out written))
                {
                    return "写入目标进程失败：" + LastError();
                }

                IntPtr loadLibrary = GetProcAddress(GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
                if (loadLibrary == IntPtr.Zero)
                {
                    return "找不到 LoadLibraryW";
                }

                uint tid;
                thread = CreateRemoteThread(proc, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, out tid);
                if (thread == IntPtr.Zero)
                {
                    return "创建远端线程失败：" + LastError() + "（多半是被游戏/反作弊拦了）";
                }

                if (WaitForSingleObject(thread, 30000) != 0)
                {
                    return "等远端 LoadLibraryW 超时";
                }

                uint exitCode;
                if (!GetExitCodeThread(thread, out exitCode))
                {
                    return "拿不到远端线程返回值";
                }
                if (exitCode == 0)
                {
                    return "LoadLibraryW 返回 0：DLL 没加载起来（位数不对 / 缺依赖 / 被拦）";
                }

                return null;
            }
            finally
            {
                if (remote != IntPtr.Zero && proc != IntPtr.Zero) VirtualFreeEx(proc, remote, UIntPtr.Zero, MEM_RELEASE);
                if (thread != IntPtr.Zero) CloseHandle(thread);
                if (proc != IntPtr.Zero) CloseHandle(proc);
            }
        }
    }
}
