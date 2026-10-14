using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using HoYoShadeHub.Helpers;

namespace HoYoShadeHub.PortableLauncher;

/// <summary>
/// 便携版的外层启动器。
///
/// <para>
/// 和上游 <c>src/HoYoShadeHub.Launcher</c>（C++ 那份）行为一致：
/// </para>
/// <list type="number">
/// <item>读同目录 <c>version.ini</c> 里的 <c>exe_path</c>，把参数原样传给它跑起来；</item>
/// <item>没有 version.ini 就扫 <c>app-*</c> 子目录，挑最新那个 <c>HoYoShadeHub.exe</c>；</item>
/// <item>保留其它 <c>app-*</c> 目录供回退；启动不承担旧版本清理。</item>
/// <item>什么都找不到 → 弹窗问要不要去下载。</item>
/// </list>
///
/// <para>
/// 为什么用 C# 重写：这份 <c>.vcxproj</c> 要 VS 的 C++ 工作负载 + MSBuild，本机没有；
/// 而它的逻辑就这么点，复刻一份不费事，还不用引依赖（MessageBox / ShellExecute 走 Win32）。
/// </para>
///
/// <para>
/// 附带一个作用：App 判定「便携版」的依据就是**父目录里存在 HoYoShadeHub.exe**
/// （见 AppConfig），所以这个文件同时也是那个标记。
/// </para>
/// </summary>
internal static class Program
{
    private const uint MB_OKCANCEL = 0x00000001;
    private const uint MB_ICONWARNING = 0x00000030;
    private const int IDOK = 1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(uint processId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr ShellExecuteW(IntPtr hwnd, string? operation, string file, string? parameters, string? directory, int showCommand);

    [STAThread]
    private static int Main(string[] args)
    {
        bool trace = args.Any(a => string.Equals(a, "--trace", StringComparison.OrdinalIgnoreCase));
        if (trace && !AllocConsole())
        {
            AttachConsole(unchecked((uint)-1));
        }

        // 命令行动词：启动壳不能直接 fire-and-forget —— 调用方等的是退出码和进度输出。
        // 挂到父控制台，把子进程的 stdout/stderr 原样泵过来，等它跑完回传退出码。
        bool cliMode = args.Length > 0 && LauncherCommandLine.IsCliVerb(args[0]);
        if (cliMode && !Console.IsOutputRedirected && !AttachConsole(unchecked((uint)-1)))
        {
            AllocConsole();
        }

        try
        {
            args = LauncherCommandLine.WaitForParentExit(args);
            cliMode = args.Length > 0 && LauncherCommandLine.IsCliVerb(args[0]);
            string baseFolder = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            EnsureConfigIni(baseFolder);

            string? targetExe = ResolveFromVersionIni(baseFolder) ?? ResolveNewestAppFolder(baseFolder);
            Log(trace, $"run_exe: {targetExe ?? "(none)"}");

            if (targetExe is null)
            {
                int answer = MessageBoxW(IntPtr.Zero,
                    "HoYoShade Hub files not found.\r\nWould you like to download it now?\r\nhttps://github.com/DuolaD/HoYoShade-Hub",
                    "HoYoShade Hub",
                    MB_ICONWARNING | MB_OKCANCEL);

                if (answer == IDOK)
                {
                    ShellExecuteW(IntPtr.Zero, null, "https://github.com/DuolaD/HoYoShade-Hub", null, null, 1);
                }

                return 1;
            }

            string arguments = LauncherCommandLine.BuildArguments(args);
            Log(trace, $"arg: {arguments}");

            string? workDirectory = Path.GetDirectoryName(targetExe);

            if (cliMode)
            {
                return RunCliChild(targetExe, arguments, workDirectory ?? baseFolder);
            }

            Process? process = Process.Start(new ProcessStartInfo(targetExe)
            {
                Arguments = arguments,
                WorkingDirectory = workDirectory ?? baseFolder,
                UseShellExecute = false,
            });

            Log(trace, $"Process started ({process?.Id.ToString() ?? "failed"})");

            return process is null ? 1 : 0;
        }
        catch (Exception ex)
        {
            Log(trace, ex.ToString());
            return 1;
        }
    }


    /// <summary>
    /// 命令行模式跑子进程：stdout/stderr 泵到当前控制台，等退出，回传退出码。
    /// </summary>
    private static int RunCliChild(string targetExe, string arguments, string workDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(targetExe)
            {
                Arguments = arguments,
                WorkingDirectory = workDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
            EnableRaisingEvents = true,
        };

        var stderrDone = new ManualResetEventSlim(false);
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                Console.Out.WriteLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                stderrDone.Set();
            }
            else
            {
                Console.Error.WriteLine(e.Data);
            }
        };

        if (!process.Start())
        {
            return 1;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        stderrDone.Wait(TimeSpan.FromSeconds(5));
        return process.ExitCode;
    }

    /// <summary>version.ini 里的 <c>exe_path</c>（build.ps1 写进去的那行）</summary>
    /// <summary>
    /// 保证根目录下有 <c>config.ini</c>（没有就按默认造一个）。
    ///
    /// <para>
    /// 为什么放在启动器里：更新包解压覆盖旧客户端时，如果 zip 里带着 <c>config.ini</c>，会把用户那份
    /// （里面有 UserDataFolder / 各种设置）**覆盖成默认的**，用户就以为「游戏没了」。
    /// 所以 zip 不再带这个文件，改由这里**只在缺失时**创建。
    /// </para>
    /// </summary>
    private static void EnsureConfigIni(string baseFolder)
    {
        try
        {
            string path = Path.Combine(baseFolder, "config.ini");
            if (File.Exists(path))
            {
                return;
            }

            File.WriteAllText(path, "Language=zh-CN\r\nUserDataFolder=\r\n", new UTF8Encoding(false));
        }
        catch
        {
            // 写不了就算了，App 自己也会兜底
        }
    }



    private static string? ResolveFromVersionIni(string baseFolder)
    {
        string iniPath = Path.Combine(baseFolder, "version.ini");
        if (!File.Exists(iniPath))
        {
            return null;
        }

        try
        {
            string candidate = PortableUpdatePolicy.ReadVersionTarget(baseFolder, File.ReadAllText(iniPath, Encoding.UTF8));
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            // ini 坏了就走下面的兜底
            return null;
        }

    }

    /// <summary>兜底：扫 app-* 子目录，挑 exe 修改时间最新的那个</summary>
    private static string? ResolveNewestAppFolder(string baseFolder)
    {
        try
        {
            string? best = null;
            DateTime bestTime = DateTime.MinValue;

            foreach (string folder in Directory.EnumerateDirectories(baseFolder, "app-*"))
            {
                string exe = PortableUpdatePolicy.GetArchiveTarget(baseFolder, Path.GetFileName(folder) + "/HoYoShadeHub.exe");
                if (!File.Exists(exe))
                {
                    continue;
                }

                DateTime time = File.GetLastWriteTimeUtc(exe);
                if (time > bestTime)
                {
                    best = exe;
                    bestTime = time;
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }

    private static void Log(bool trace, string message)
    {
        if (!trace)
        {
            return;
        }

        try
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
        }
        catch
        {
            // ignore
        }
    }
}

