using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

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
/// <item>起来之后把其它 <c>app-*</c> 目录删掉（旧版本残留）——**正在跑的那份跳过**（见 <see cref="CleanupOldAppFolders"/>）；</item>
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

        try
        {
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

            string arguments = BuildArguments(args);
            Log(trace, $"arg: {arguments}");

            string? workDirectory = Path.GetDirectoryName(targetExe);

            Process? process = Process.Start(new ProcessStartInfo(targetExe)
            {
                Arguments = arguments,
                WorkingDirectory = workDirectory ?? baseFolder,
                UseShellExecute = false,
            });

            Log(trace, $"Process started ({process?.Id.ToString() ?? "failed"})");

            if (process is not null)
            {
                CleanupOldAppFolders(baseFolder, Path.GetFileName(workDirectory), trace);
            }

            return process is null ? 1 : 0;
        }
        catch (Exception ex)
        {
            Log(trace, ex.ToString());
            return 1;
        }
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
            foreach (string raw in File.ReadAllLines(iniPath, Encoding.UTF8))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#') || line.StartsWith('['))
                {
                    continue;
                }

                int separator = line.IndexOf('=');
                if (separator <= 0)
                {
                    continue;
                }

                if (!line[..separator].Trim().Equals("exe_path", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string relative = line[(separator + 1)..].Trim().Trim('"');
                if (relative.Length == 0)
                {
                    continue;
                }

                string candidate = Path.GetFullPath(Path.Combine(baseFolder, relative));
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // ini 坏了就走下面的兜底
        }

        return null;
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
                string exe = Path.Combine(folder, "HoYoShadeHub.exe");
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

    /// <summary>把参数原样传下去（每个都加引号，免得路径带空格被拆开）</summary>
    private static string BuildArguments(string[] args) =>
        args.Length == 0 ? string.Empty : string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

    /// <summary>启动之后把其它 app-* 目录删掉（上游也是这么清旧版本的）</summary>
    private static void CleanupOldAppFolders(string baseFolder, string? currentFolderName, bool trace)
    {
        if (string.IsNullOrWhiteSpace(currentFolderName))
        {
            return;
        }

        try
        {
            foreach (string folder in Directory.EnumerateDirectories(baseFolder, "app-*"))
            {
                string name = Path.GetFileName(folder);
                if (name.Equals(currentFolderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // ⚠ 别删「正在跑的那一份」。
                //
                // 用户（或更新器）在旧实例还开着的时候又启动了一次：版本目录改成新的 app-X，
                // 而旧实例仍在 app-Y 里跑。这时把 app-Y 删掉，旧实例后续**懒加载**的程序集
                // 就全部找不到（System.Threading.Thread / Microsoft.Extensions.* 之类），
                // 表现是「打开目录」报 FileNotFoundException、OptiScaler 页怎么点都没反应；
                // 而且删除只会删掉**当时没被加载**的那部分文件 —— 留下一个残包
                //（之前那个只剩 150 个文件的 app-1.3.7-z1i 就是这么来的）。
                if (IsInUse(folder))
                {
                    Log(trace, $"Skip in-use version: {name}");
                    continue;
                }

                Log(trace, $"Removing old version: {name}");

                try
                {
                    Directory.Delete(folder, recursive: true);
                }
                catch
                {
                    // 正在被占用就算了，下次启动再说
                }
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// 这个 app-* 目录里是不是有实例正在跑。
    ///
    /// <para>
    /// 办法就是试着**独占读写**它的主 exe：进程映像的文件是以只读方式映射的，
    /// 还开着的时候独占打开必然 SharingViolation。比查进程列表靠谱 ——
    /// 旧实例可能是提权跑的，普通权限读不到它的可执行路径。
    /// </para>
    /// </summary>
    private static bool IsInUse(string folder)
    {
        try
        {
            string exe = Path.Combine(folder, "HoYoShadeHub.exe");
            if (!File.Exists(exe))
            {
                return false;
            }

            using FileStream _ = new(exe, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch
        {
            return false;
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
