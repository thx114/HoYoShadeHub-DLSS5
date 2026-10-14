using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HoYoShadeHub.Features.Plugins;

internal static class RtssInstallation
{
    public static string? FindRoot(out string note)
    {
        var candidates = new List<string>();
        var runningIds = new List<int>();
        foreach (var process in Process.GetProcessesByName("RTSS"))
        {
            try
            {
                runningIds.Add(process.Id);
                string? image = ProcessImage(process);
                if (!string.IsNullOrWhiteSpace(image) && Path.GetDirectoryName(image) is { } dir)
                    candidates.Add(dir);
            }
            catch { }
            finally { process.Dispose(); }
        }

        string? configured = AppConfig.GetValue<string>(null, "rtss_install_root");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        foreach (RegistryHive hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (RegistryView view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (string keyName in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(keyName);
                    string? name = key?.GetValue("DisplayName") as string;
                    if (name is null || !name.Contains("RivaTuner Statistics Server", StringComparison.OrdinalIgnoreCase)) continue;
                    if (key!.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location)) candidates.Add(location);
                    foreach (string field in new[] { "DisplayIcon", "UninstallString" })
                    {
                        string? raw = key.GetValue(field) as string;
                        if (string.IsNullOrWhiteSpace(raw)) continue;
                        string path = raw.StartsWith('"') ? raw.Split('"')[1] : raw.Split(',')[0];
                        if (Path.GetDirectoryName(path) is { } dir) candidates.Add(dir);
                    }
                }
            }
            catch { /* Restricted installation records: user may specify a folder. */ }
        }
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RivaTuner Statistics Server"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RivaTuner Statistics Server"));

        string? root = candidates.Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(p => File.Exists(Path.Combine(p, "RTSS.exe")) && Directory.Exists(Path.Combine(p, "Profiles")));
        note = runningIds.Count > 0 ? $"已检测到 RTSS 进程（PID：{string.Join("、", runningIds)}）。" : "RTSS 当前未运行。";
        return root;
    }

    private static string? ProcessImage(Process process)
    {
        // MainModule may require more access than merely querying the executable path.
        try { if (process.MainModule?.FileName is { Length: > 0 } image) return image; }
        catch { }
        IntPtr handle = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, process.Id);
        if (handle == IntPtr.Zero) return null;
        try
        {
            var buffer = new StringBuilder(32768);
            int size = buffer.Capacity;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString() : null;
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder image, ref int size);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    public static void EnsureStopped(string gameExe)
    {
        foreach (string name in new[] { "RTSS", "RTSSHooksLoader", "RTSSHooksLoader64", Path.GetFileNameWithoutExtension(gameExe) })
        {
            Process[] processes = Process.GetProcessesByName(name);
            bool running = processes.Length > 0;
            foreach (Process process in processes) process.Dispose();
            if (running)
                throw new InvalidOperationException($"请先正常退出 {name}，启动器不会强制结束进程。");
        }
    }

    public static string BackupRoot => Path.Combine(AppConfig.UserDataFolder ?? AppContext.BaseDirectory, "_codex-backup");
}
