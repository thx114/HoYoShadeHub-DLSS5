using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using HoYoShadeHub.Core;
using Vanara.PInvoke;

namespace HoYoShadeHub.RPC;

internal static class AppConfig
{

    public static string? AppVersion { get; private set; }


    public static bool IsAdmin { get; private set; }


    public static string MutexAndPipeName { get; private set; }


    public const string StartupMagic = "zb8L3ShgFjeyDxeA";


    public static string? HoYoShadeHubLauncherExecutePath { get; private set; }


    public static bool IsPortable { get; private set; }


    public static bool IsAppInRemovableStorage { get; private set; }


    /// <summary>
    /// 「真便携」：缓存 / 日志落在便携目录内部，不写 C: 盘。与
    /// <c>HoYoShadeHub.AppConfig.IsPortableLocal</c> 同一套规则（RPC 程序集拿不到 Extensions，
    /// 所以这里是同一份判定的副本）。无界面子进程（rpc / playtime）必须跟着主程序走同一个目录，
    /// 否则又会开出两份缓存。
    /// </summary>
    public static bool IsPortableLocal { get; private set; }


    /// <summary>「真便携」标记文件名：放在便携根目录（<c>HoYoShadeHub.exe</c> 旁边）</summary>
    private const string PortableMarkerFileName = ".portable";


    /// <summary>有没有 .portable 标记 / 环境变量 HYSHADE_PORTABLE_LOCAL=1（同 Extensions 的实现）</summary>
    private static bool IsPortableLocalEnabled(string? portableRoot)
    {
        try
        {
            string? flag = Environment.GetEnvironmentVariable("HYSHADE_PORTABLE_LOCAL")?.Trim();
            if (!string.IsNullOrEmpty(flag) &&
                (flag.Equals("1", StringComparison.OrdinalIgnoreCase) ||
                 flag.Equals("true", StringComparison.OrdinalIgnoreCase) ||
                 flag.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
                 flag.Equals("on", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }
        catch { }

        if (string.IsNullOrWhiteSpace(portableRoot))
        {
            return false;
        }

        try
        {
            return File.Exists(Path.Combine(portableRoot, PortableMarkerFileName));
        }
        catch
        {
            return false;
        }
    }


    public static string CacheFolder { get; private set; }


    /// <summary>
    /// 日志目录：便携版 = 启动器所在文件夹下的 log\（与主程序同一规则、同一目录）；
    /// 写不进去时回退缓存目录。与 HoYoShadeHub.AppConfig.LogFolder 保持一致。
    /// </summary>
    public static string LogFolder { get; private set; } = string.Empty;

    private static string ResolveLogFolder()
    {
        string fallback = Path.Combine(CacheFolder, "log");

        if (IsPortable && !string.IsNullOrWhiteSpace(HoYoShadeHubLauncherExecutePath))
        {
            string? launcherFolder = Path.GetDirectoryName(HoYoShadeHubLauncherExecutePath);
            if (!string.IsNullOrWhiteSpace(launcherFolder))
            {
                string candidate = Path.Combine(launcherFolder, "log");
                try
                {
                    Directory.CreateDirectory(candidate);
                    string probe = Path.Combine(candidate, ".write-probe");
                    File.WriteAllText(probe, "ok");
                    File.Delete(probe);
                    return candidate;
                }
                catch
                {
                    // 只读盘 / 没权限：回退缓存目录
                }
            }
        }

        return fallback;
    }


    static AppConfig()
    {
        AppVersion = typeof(AppConfig).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        MutexAndPipeName = $"HoYoShadeHub.RPC/{Process.GetCurrentProcess().SessionId}/{AppVersion}";
        IsAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

        IsAppInRemovableStorage = IsDeviceRemovableOrOnUSB(AppContext.BaseDirectory);
        string? parentFolder = new DirectoryInfo(AppContext.BaseDirectory).Parent?.FullName;
        string launcherExe = Path.Join(parentFolder, "HoYoShadeHub.exe");
        if (Directory.Exists(parentFolder) && File.Exists(launcherExe))
        {
            IsPortable = true;
            HoYoShadeHubLauncherExecutePath = launcherExe;
        }

        if (IsPortable)
        {
            IsPortableLocal = IsAppInRemovableStorage || IsPortableLocalEnabled(parentFolder);
        }

        if (IsAppInRemovableStorage && IsPortable)
        {
            CacheFolder = Path.Combine(parentFolder!, ".cache");
        }
        else if (IsAppInRemovableStorage)
        {
            CacheFolder = Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, ".HoYoShadeHubCache");
        }
        else if (IsPortable && IsPortableLocal)
        {
            // 真便携：跟主程序同一个 <便携根>\.cache
            CacheFolder = Path.Combine(parentFolder!, ".cache");
        }
        else if (IsPortable)
        {
            CacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoYoShadeHub");
        }
        else
        {
            CacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoYoShadeHub");
        }
        // 真便携：临时文件（下载中的 zip / 解压中间文件）也放进便携目录，不写 C: 盘
        if (IsPortableLocal && !string.IsNullOrWhiteSpace(CacheFolder))
        {
            TemporaryFolder.Override = Path.Combine(CacheFolder, "temp");
        }

        Directory.CreateDirectory(CacheFolder);
        LogFolder = ResolveLogFolder();
        Directory.CreateDirectory(LogFolder);
    }




    public static bool IsDeviceRemovableOrOnUSB(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                DriveInfo drive = new DriveInfo(path);
                if (drive.DriveType is DriveType.Removable)
                {
                    return true;
                }
                string fileName = $@"\\.\{drive.Name.Trim('\\')}";
                using Kernel32.SafeHFILE hDevice = Kernel32.CreateFile(fileName, 0, FileShare.ReadWrite | FileShare.Delete, null, FileMode.Open, 0, HFILE.NULL);
                if (hDevice.IsInvalid)
                {
                    return false;
                }
                Kernel32.STORAGE_PROPERTY_QUERY query = new()
                {
                    PropertyId = Kernel32.STORAGE_PROPERTY_ID.StorageDeviceProperty,
                    QueryType = Kernel32.STORAGE_QUERY_TYPE.PropertyStandardQuery,
                };
                bool result = Kernel32.DeviceIoControl(hDevice, Kernel32.IOControlCode.IOCTL_STORAGE_QUERY_PROPERTY, query, out Kernel32.STORAGE_DEVICE_DESCRIPTOR_MGD desc);
                if (result)
                {
                    if (desc.BusType == Kernel32.STORAGE_BUS_TYPE.BusTypeUsb)
                    {
                        return true;
                    }
                }
            }
        }
        catch { }
        return false;
    }



}
