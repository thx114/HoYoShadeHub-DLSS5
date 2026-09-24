using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Core.Networking;
using HoYoShadeHub.Core.SelfQuery;
using HoYoShadeHub.Features.Background;
using HoYoShadeHub.Features.Database;
using HoYoShadeHub.Features.GameLauncher;
using HoYoShadeHub.Features.HoYoPlay;
using HoYoShadeHub.Features.PlayTime;
using HoYoShadeHub.Features.RPC;
using HoYoShadeHub.Features.Screenshot;
using HoYoShadeHub.Features.Update;
using HoYoShadeHub.Features.ViewHost;
using HoYoShadeHub.Features.Xxmi;
using HoYoShadeHub.Helpers;
using HoYoShadeHub.RPC.Update;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace HoYoShadeHub;

public static class AppConfig
{



    public static readonly JsonSerializerOptions JsonSerializerOptions = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };


    public static string HoYoShadeHubExecutePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "HoYoShadeHub.exe");





    static AppConfig()
    {
        try
        {
            SystemCulture = CultureInfo.CurrentUICulture;
            AppVersion = typeof(AppConfig).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";

            IsAppInRemovableStorage = DriveHelper.IsDeviceRemovableOrOnUSB(AppContext.BaseDirectory);
            string? parentFolder = new DirectoryInfo(AppContext.BaseDirectory).Parent?.FullName;
            string launcherExe = Path.Join(parentFolder, "HoYoShadeHub.exe");
            if (Directory.Exists(parentFolder) && File.Exists(launcherExe))
            {
                IsPortable = true;
                InstallType = HoYoShadeHub.RPC.Update.Metadata.InstallType.Portable;
                HoYoShadeHubLauncherExecutePath = launcherExe;
            }
            else
            {
                IsPortable = false;
                InstallType = HoYoShadeHub.RPC.Update.Metadata.InstallType.Setup;
            }

            if (IsAppInRemovableStorage && IsPortable)
            {
                CacheFolder = Path.Combine(parentFolder!, ".cache");
                ConfigPath = Path.Combine(parentFolder!, "config.ini");
            }
            else if (IsAppInRemovableStorage)
            {
                CacheFolder = Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, ".HoYoShadeHubCache");
                ConfigPath = Path.Combine(CacheFolder, "config.ini");
            }
            else if (IsPortable)
            {
                CacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoYoShadeHub");
                ConfigPath = Path.Combine(parentFolder!, "config.ini");
            }
            else
            {
                CacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoYoShadeHub");
#if DEBUG || DEV
                ConfigPath = Path.Combine(CacheFolder, "config.ini");
#else
                string roamingFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HoYoShadeHub");
                ConfigPath = Path.Combine(roamingFolder, "config.ini");
#endif
            }
            Directory.CreateDirectory(CacheFolder);
            var webviewFolder = Path.Combine(CacheFolder, "webview");
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", webviewFolder, EnvironmentVariableTarget.Process);

            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new WindowsPrincipal(identity);
            IsAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);

            if (File.Exists(ConfigPath))
            {
                string text = File.ReadAllText(ConfigPath);
                string lang = Regex.Match(text, @"Language=(.+)").Groups[1].Value.Trim();
                string folder = Regex.Match(text, @"UserDataFolder=(.+)").Groups[1].Value.Trim();
                bool.TryParse(Regex.Match(text, @"EnableLoginAuthTicket=(.+)").Groups[1].Value.Trim(), out bool enabled);
                EnableLoginAuthTicket = enabled;
                stoken = Regex.Match(text, @"stoken=(.+)").Groups[1].Value.Trim();
                mid = Regex.Match(text, @"mid=(.+)").Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(lang))
                {
                    try
                    {
                        CultureInfo.CurrentUICulture = new CultureInfo(lang);
                        Language = lang;
                    }
                    catch { }
                }
                if (!string.IsNullOrWhiteSpace(folder))
                {
                    string userDataFolder;
                    if (Path.IsPathFullyQualified(folder))
                    {
                        userDataFolder = folder;
                    }
                    else
                    {
                        userDataFolder = Path.GetFullPath(folder, Path.GetDirectoryName(ConfigPath)!);
                    }
                    if (Directory.Exists(userDataFolder))
                    {
                        UserDataFolder = Path.GetFullPath(userDataFolder);
                        DatabaseService.SetDatabase(userDataFolder);
                    }
                }
            }

            // config.ini 是空的那份（zip 覆盖、或者用户手删过）：找找盘上有没有现成的 profile。
            // 不做这一步的话，App 会用一个「默认目录」当数据目录 —— 用户的游戏列表 / 安装路径全在旧 DB 里，
            // 看起来就像「游戏丢了」（真事，2026-09-20，用户报「新版跳过初始化，找不到之前的游戏」）。
            if (string.IsNullOrWhiteSpace(UserDataFolder))
            {
                UseUserDataFolder(TryFindExistingProfileFolder());
            }
        }
        catch { }
    }



    /// <summary>
    /// 切到某个用户数据目录：**必须同时**把 DB 也指过去。
    ///
    /// <para>
    /// 只设 <see cref="UserDataFolder"/> 是不够的 —— 数据库连接用的是
    /// <c>DatabaseService.SetDatabase()</c> 那个路径。漏掉这一步的后果（2026-09-20 用户踩过）：
    /// 游戏列表、安装路径这些**都在 DB 里**，于是顶部只剩「自定义游戏」（那个走 games.json），
    /// 看起来就像游戏全丢了。
    /// </para>
    /// </summary>
    public static void UseUserDataFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        UserDataFolder = folder;
        DatabaseService.SetDatabase(folder);
    }



    /// <summary>
    /// 盘上现成的 profile 目录（里面有 DB / <c>.hysx\games.json</c> / <c>HoYoShade\ReShade64.dll</c>）；
    /// 多个的时候按「证据文件最近改动」那个算。找不到返回 null。
    /// </summary>
    public static string? TryFindExistingProfileFolder()
    {
        try
        {
            var candidates = new List<string>();

            void AddCandidate(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    return;
                }

                // 便携版只在**自己的目录树内**认领现成 profile。
                // 否则一个干净的新用户测试包会去认领 %LOCALAPPDATA% / %APPDATA% / 同级目录里
                // 别人家那份数据（用户实测：解压出来的新包直接找到了 C 盘的数据文件夹）。
                if (IsPortable)
                {
                    // 这里不能引用后面的 parent 局部变量（前向引用），直接从程序目录推便携根
                    string? portableRoot = new DirectoryInfo(AppContext.BaseDirectory).Parent?.FullName;
                    if (portableRoot is { Length: > 0 })
                    {
                        string root = Path.GetFullPath(portableRoot) + Path.DirectorySeparatorChar;
                        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        {
                            return;
                        }
                    }
                }

                candidates.Add(path);
            }

            string appDirectory = AppContext.BaseDirectory;
            string? parent = new DirectoryInfo(appDirectory).Parent?.FullName;

            AddCandidate(ResolveDefaultUserDataFolder());
            AddCandidate(parent);
            AddCandidate(appDirectory);
            AddCandidate(parent is null ? null : Path.GetDirectoryName(parent));
            AddCandidate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HoYoShadeHub"));
            AddCandidate(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HoYoShadeHub"));

            // 同级目录也扫一层：用户很可能把新 zip 解压到了旁边的文件夹
            if (parent is { Length: > 0 } && Directory.Exists(parent))
            {
                foreach (string sibling in Directory.EnumerateDirectories(parent))
                {
                    AddCandidate(sibling);
                }
            }

            string? best = null;
            DateTimeOffset bestTime = DateTimeOffset.MinValue;

            foreach (string candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                DateTimeOffset time = ProfileEvidenceTime(candidate);
                if (time > bestTime)
                {
                    bestTime = time;
                    best = candidate;
                }
            }

            return best;
        }
        catch
        {
            return null;
        }
    }



    /// <summary>这个目录里「用户数据」的最新改动时间；一点证据都没有就返回 MinValue</summary>
    private static DateTimeOffset ProfileEvidenceTime(string directory)
    {
        DateTimeOffset newest = DateTimeOffset.MinValue;

        foreach (string path in new[]
                 {
                     Path.Combine(directory, "HoYoShadeHubDatabase.db"),
                     Path.Combine(directory, ".hysx", "games.json"),
                     Path.Combine(directory, "HoYoShade", "ReShade64.dll"),
                 })
        {
            try
            {
                if (File.Exists(path))
                {
                    DateTimeOffset time = File.GetLastWriteTime(path);
                    if (time > newest)
                    {
                        newest = time;
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        return newest;
    }




    #region Configuration



    public static string? HoYoShadeHubLauncherExecutePath { get; private set; }


    public static string AppVersion { get; private set; }


    public static bool IsPortable { get; private set; }

    public static HoYoShadeHub.RPC.Update.Metadata.InstallType InstallType { get; set; } = HoYoShadeHub.RPC.Update.Metadata.InstallType.Setup;


    public static bool IsAppInRemovableStorage { get; private set; }


    public static CultureInfo SystemCulture { get; private set; }


    public static string CacheFolder { get; private set; }


    public static string ConfigPath { get; private set; }


    public static string? Language { get; set; }


    public static string? UserDataFolder { get; set; }


    /// <summary>
    /// 默认用户数据目录（跟 <c>WelcomeView.InitializeDefaultUserDataFolder</c> 同一套规则）。
    /// 首次引导那一步要用它来判断「老客户端的数据还在不在」。
    /// </summary>
    public static string ResolveDefaultUserDataFolder()
    {
        try
        {
            string? parentFolder = new DirectoryInfo(AppContext.BaseDirectory).Parent?.FullName;

            if (IsAppInRemovableStorage && IsPortable)
            {
                return parentFolder ?? AppContext.BaseDirectory;
            }

            if (IsAppInRemovableStorage)
            {
                return Path.Combine(Path.GetPathRoot(AppContext.BaseDirectory)!, ".HoYoShadeHubData");
            }

            if (IsPortable)
            {
                return parentFolder ?? AppContext.BaseDirectory;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HoYoShadeHub");
        }
        catch
        {
            return AppContext.BaseDirectory;
        }
    }


    public static bool IsAdmin { get; private set; }


    public static string LogFile { get; private set; }


    public static bool? EnableLoginAuthTicket { get; set; }

    public static string? stoken { get; set; }

    public static string? mid { get; set; }



    public static void SaveConfiguration()
    {
        try
        {
            StringBuilder sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(UserDataFolder))
            {
                string dataFolder = UserDataFolder;
                string? parentFolder = Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrWhiteSpace(parentFolder) && UserDataFolder.StartsWith(parentFolder))
                {
                    dataFolder = Path.GetRelativePath(parentFolder, UserDataFolder);
                }
                sb.AppendLine($"Language={Language}");
                sb.AppendLine($"UserDataFolder={dataFolder}");
            }
            else
            {
                sb.AppendLine($"Language={Language}");
                sb.AppendLine($"UserDataFolder=");
            }
            if (EnableLoginAuthTicket.HasValue)
            {
                sb.AppendLine($"{nameof(EnableLoginAuthTicket)}={EnableLoginAuthTicket}");
            }
            if (!string.IsNullOrWhiteSpace(stoken))
            {
                sb.AppendLine($"{nameof(stoken)}={stoken}");
            }
            if (!string.IsNullOrWhiteSpace(mid))
            {
                sb.AppendLine($"{nameof(mid)}={mid}");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, sb.ToString());
        }
        catch { }
    }



    #endregion



    #region Service Provider



    private static IServiceProvider _serviceProvider;



    private static void BuildServiceProvider()
    {
        if (_serviceProvider == null)
        {
            var logFolder = Path.Combine(CacheFolder, "log");
            Directory.CreateDirectory(logFolder);
            LogFile = Path.Combine(logFolder, $"HoYoShadeHub_{DateTime.Now:yyMMdd}.log");
            Log.Logger = new LoggerConfiguration().WriteTo.File(path: LogFile, shared: true, outputTemplate: $$"""[{Timestamp:HH:mm:ss.fff}] [{Level:u4}] [{{Path.GetFileName(Environment.ProcessPath)}} ({{Environment.ProcessId}})] {SourceContext}{NewLine}{Message}{NewLine}{Exception}{NewLine}""")
                                                  .Enrich.FromLogContext()
                                                  .CreateLogger();
            Log.Information($"Welcome to HoYoShadeHub v{AppVersion}\r\nSystem: {Environment.OSVersion}\r\nCommand Line: {Environment.CommandLine}");

            var sc = new ServiceCollection();
            sc.AddMemoryCache();
            sc.AddLogging(c => c.AddSerilog(Log.Logger));
            DohService.Provider = DohProvider;
            DohService.Enabled = EnableDoh;
            DohService.EnableEch = EnableEch;
            sc.AddHttpClient().ConfigureHttpClientDefaults(config =>
            {
                config.RemoveAllLoggers();
                config.ConfigureHttpClient(client =>
                {
                    client.DefaultRequestHeaders.Add("User-Agent", $"HoYoShadeHub/{AppVersion}");
                    client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrHigher;
                });
                config.ConfigurePrimaryHttpMessageHandler(() => DohService.CreateSocketsHttpHandler());
            });

            sc.AddSingleton<HoYoPlayClient>();

            sc.AddSingleton<HoYoPlayService>();
            sc.AddSingleton<BackgroundService>();
            sc.AddSingleton<GameLauncherService>();
            sc.AddSingleton<PlayTimeService>();


            sc.AddTransient<MetadataClient>();
            sc.AddTransient<UpdateService>();
            sc.AddTransient<SetupService>();

            sc.AddSingleton<RpcService>();

            sc.AddSingleton<ScreenCaptureService>();

            sc.AddSingleton<GameBananaModInstaller>();

            _serviceProvider = sc.BuildServiceProvider();
        }
    }


    public static T GetService<T>()
    {
        BuildServiceProvider();
        return _serviceProvider.GetService<T>()!;
    }


    public static ILogger<T> GetLogger<T>()
    {
        BuildServiceProvider();
        return _serviceProvider.GetService<ILogger<T>>()!;
    }


    public static SqliteConnection CreateDatabaseConnection()
    {
        return DatabaseService.CreateConnection();
    }


    #endregion





    #region Static Setting



    public static bool EnablePreviewRelease
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }


    /// <summary>
    /// 是否已完成首次启动欢迎/OOBE动画
    /// </summary>
    public static bool WelcomeOOBECompleted
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }


    /// <summary>
    /// 启动时自动检测启动器更新
    /// </summary>
    /// <summary>上次「自动」检查启动器更新的时间（自动检查一天只做一次；手动点检查不受限制）</summary>
    public static DateTimeOffset LastUpdateCheckUtc
    {
        get => GetValue(DateTimeOffset.MinValue, "last_update_check_utc");
        set => SetValue(value, "last_update_check_utc");
    }

    public static bool AutoCheckLauncherUpdateOnStartup
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    public static string? IgnoreVersion
    {
        get => GetValue<string>();
        set => SetValue(value);
    }





    public static bool IgnoreRunningGame
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }


    public static bool ShowNoviceGacha
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    public static bool ShowChronicledWish
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    public static string? AccentColor
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    public static int VideoBgVolume
    {
        get => Math.Clamp(GetValue(0), 0, 100);
        set => SetValue(value);
    }


    [Obsolete("已不用", true)]
    public static bool UseOneBg
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }


    public static bool AcceptHoyolabToolboxAgreement
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }


    public static bool HoyolabToolboxPaneOpen
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    public static bool EnableSystemTrayIcon
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    public static bool ExitWhenClosing
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }


    /// <summary>
    /// 主窗口关闭选项，隐藏/退出
    /// </summary>
    public static MainWindowCloseOption CloseWindowOption
    {
        get => GetValue<MainWindowCloseOption>();
        set => SetValue(value);
    }


    public static bool UseSystemThemeColor
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }


    public static bool EnableNavigationViewLeftCompact
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }


    public static bool DisableGameNoticeRedHot
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }


    public static bool DefaultDisableVideoBackgroundPlayback
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    public static bool EnableDoh
    {
        get => GetValue(false, nameof(EnableCloudflareDohViaCloudflare));
        set
        {
            if (EnableDoh == value)
            {
                return;
            }

            SetValue(value, nameof(EnableCloudflareDohViaCloudflare));
            DohService.Enabled = value;

            if (!value)
            {
                EnableEch = false;
            }
        }
    }


    public static bool EnableEch
    {
        get => GetValue(false);
        set
        {
            if (EnableEch == value)
            {
                return;
            }

            SetValue(value);
            DohService.EnableEch = value;
        }
    }



    public static DohProvider DohProvider
    {
        get => GetValue(HoYoShadeHub.Core.Networking.DohProvider.Cloudflare);
        set
        {
            if (DohProvider == value)
            {
                return;
            }

            SetValue(value);
            DohService.Provider = value;
        }
    }


    [Obsolete("Use EnableDoh", false)]
    public static bool EnableCloudflareDohViaCloudflare
    {
        get => EnableDoh;
        set => EnableDoh = value;
    }


    public static StartGameAction StartGameAction
    {
        get => GetValue<StartGameAction>();
        set => SetValue(value);
    }



    public static string? HyperionDeviceId
    {
        get => GetValue<string>();
        set => SetValue(value);
    }



    public static string? HyperionDeviceFp
    {
        get => GetValue<string>();
        set => SetValue(value);
    }



    public static DateTimeOffset HyperionDeviceFpLastUpdateTime
    {
        get => GetValue<DateTimeOffset>();
        set => SetValue(value);
    }



    public static string? LastAppVersion
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    /// <summary>
    /// 当前选择的游戏区服
    /// </summary>
    public static GameBiz CurrentGameBiz
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    public static string? SelectedGameBizs
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    /// <summary>
    /// 固定待选择的游戏区服图标
    /// </summary>
    public static bool IsGameBizSelectorPinned
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }


    public static string? DefaultGameInstallationPath
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    public static int SpeedLimitKBPerSecond
    {
        get => GetValue(0);
        set => SetValue(value);
    }



    /// <summary>
    /// 缓存的游戏信息 <see cref="HoYoShadeHub.Core.HoYoPlay.GameInfo"/>
    /// </summary>
    public static string? CachedGameInfo
    {
        get => DatabaseService.GetValue<string>(nameof(CachedGameInfo), out _, default);
        set => DatabaseService.SetValue(nameof(CachedGameInfo), value);
    }


    /// <summary>
    /// 更新完成后自动重启
    /// </summary>
    public static bool AutoRestartWhenUpdateFinished
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    /// <summary>
    /// 更新完成后显示更新内容
    /// </summary>
    public static bool ShowUpdateContentAfterUpdateRestart
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    /// <summary>
    /// 保持 RPC 服务在后台运行
    /// </summary>
    public static bool KeepRpcServerRunningInBackground
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    /// <summary>
    /// 安装游戏时自动创建子文件夹
    /// </summary>
    public static bool AutomaticallyCreateSubfolderForInstall
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    /// <summary>
    /// 崩坏3国际服多区服选项
    /// </summary>
    public static string? LastGameIdOfBH3Global
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    /// <summary>
    /// 启用硬链接
    /// </summary>
    public static bool EnableHardLink
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    /// <summary>
    /// 原神HDR
    /// </summary>
    public static bool EnableGenshinHDR
    {
        get => GetValue(false);
        set => SetValue(value);
    }


    /// <summary>
    /// 截图文件夹
    /// </summary>
    public static string? ScreenshotFolder
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    /// <summary>
    /// 显示主窗口快捷键
    /// </summary>
    public static string? ShowMainWindowHotkey
    {
        // Alt + H
        get => GetValue("1+72");
        set => SetValue(value);
    }


    /// <summary>
    /// 手柄控制
    /// </summary>
    public static bool EnableGamepadSimulateInput
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }


    public static int GamepadGuideButtonMode
    {
        get => GetValue<int>();
        set => SetValue(value);
    }


    public static string? GamepadShareButtonMapKeys
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    public static string? GamepadGuideButtonMapKeys
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    public static int GamepadShareButtonMode
    {
        get => GetValue<int>();
        set => SetValue(value);
    }


    public static bool AutoConvertScreenshotToSDR
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    public static bool AutoCopyScreenshotToClipboard
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    /// <summary>
    /// 0: PNG, 1: AVIF, 2: JPEG XL
    /// </summary>
    public static int ScreenCaptureSavedFormat
    {
        get => GetValue(0);
        set => SetValue(value);
    }


    /// <summary>
    /// 0: Middle, 1: High, 2: Lossless
    /// </summary>
    public static int ScreenCaptureEncodeQuality
    {
        get => GetValue(1);
        set => SetValue(value);
    }


    /// <summary>
    /// 使用 CMD 启动游戏 <see href="https://github.com/Scighost/HoYoShadeHub/issues/1634"/>
    /// </summary>
    public static bool StartGameWithCMD
    {
        get => GetValue(true);
        set => SetValue(value);
    }


    /// <summary>
    /// 原神Blender/留影机插件路径
    /// </summary>
    public static string? GenshinBlenderPluginPath
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    /// <summary>
    /// 绝区零Blender/留影机插件路径
    /// </summary>
    public static string? ZZZBlenderPluginPath
    {
        get => GetValue<string>();
        set => SetValue(value);
    }


    /// <summary>
    /// 使用Starward启动器启动公开客户端游戏
    /// </summary>
    public static bool UseStarwardLauncher
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// HoYoShade框架 - 加入预览版更新渠道
    /// </summary>
    public static bool EnableHoYoShadePreviewChannel
    {
        get => GetValue<bool>();
        set => SetValue(value);
    }

    /// <summary>
    /// 远端目录（插件 + OptiScaler）的地址前缀，形如
    /// <c>https://raw.githubusercontent.com/&lt;owner&gt;/&lt;repo&gt;/main/catalog/</c>。
    /// 换仓库只改这一处（或用户在设置里改）。
    /// </summary>
    public static string CatalogBaseUrl
    {
        get => GetValue(HoYoShadeHub.Features.Plugins.RemoteCatalogDefaults.BaseUrl);
        set => SetValue(value);
    }

    /// <summary>
    /// 更新渠道：0 = 官方（RPC 元数据），1 = GitHub（本 fork 的仓库，支持一键更新 + 退回旧版本）。
    ///
    /// <para>
    /// **默认 1**：这是本 fork 的发行渠道，用户装的就是这个仓库的包，
    /// 默认走官方只会读到上游的版本（而且我们改了这么多东西，跟上游并不兼容）。
    /// 用户在「设置 → 关于」里手动选过就按他选的来。
    /// </para>
    /// </summary>
    public static int UpdateChannel
    {
        // 本 fork 只从自己的仓库更新，渠道不再给用户切（关于页里的下拉也删了）。
        // 这里直接写死 1，老配置里存过 0 的也会被纠正回来。
        get => 1;
        set { /* 固定本分支，忽略写入 */ }
    }

    /// <summary>上次成功拉取远端目录的时间（UTC，空 = 从没拉过）</summary>
    public static DateTimeOffset LastCatalogFetchUtc
    {
        get
        {
            string raw = GetValue(string.Empty);
            return DateTimeOffset.TryParse(raw, out DateTimeOffset parsed) ? parsed : DateTimeOffset.MinValue;
        }
        set => SetValue(value.ToUniversalTime().ToString("O"));
    }

    /// <summary>
    /// 启动时自动检测 HoYoShade 框架更新
    /// </summary>
    public static bool AutoCheckFrameworkUpdateOnStartup
    {
        get => GetValue(true);
        set => SetValue(value);
    }

    /// <summary>
    /// 下载服务器选择（**全局一份**）：-1=自动选择, 0=GitHub 直连, 1=Cloudflare, 2=腾讯云,
    /// 3=阿里云, 4=gh-proxy.org, 5=ghfast.top。
    ///
    /// <para>
    /// 以前「关于页/更新窗口」和「HoYoShade/ReShade/OptiScaler 下载卡片」各存一份
    /// （LauncherUpdateDownloadServer / HoYoShadeFrameworkDownloadServer），于是出现
    /// 「关于页明明选了 GitHub 直连，下载卡片下面还是自家 CDN」 两个键不是一回事。
    /// 现在读写同一个键 DownloadServer_V3；老键只在第一次读时用来做迁移。
    /// </para>
    /// </summary>
    public static int DownloadServer
    {
        get
        {
            if (HasValue("DownloadServer_V3"))
            {
                return GetValue(-1, "DownloadServer_V3");
            }

            // 迁移：老配置里哪份设过就用哪份（框架那份才是下载卡片用的，优先）
            if (HasValue("HoYoShadeFrameworkDownloadServer_V2"))
            {
                return GetValue(-1, "HoYoShadeFrameworkDownloadServer_V2");
            }

            if (HasValue("LauncherUpdateDownloadServer_V2"))
            {
                return GetValue(-1, "LauncherUpdateDownloadServer_V2");
            }

            return -1;
        }
        set => SetValue(value, "DownloadServer_V3");
    }

    /// <summary>启动器自身更新用的服务器  和 DownloadServer 是同一个设置</summary>
    public static int LauncherUpdateDownloadServer
    {
        get => DownloadServer;
        set => DownloadServer = value;
    }

    /// <summary>HoYoShade / ReShade / OptiScaler 下载用的服务器  和 DownloadServer 是同一个设置</summary>
    public static int HoYoShadeFrameworkDownloadServer
    {
        get => DownloadServer;
        set => DownloadServer = value;
    }


    #endregion





    #region Dynamic Setting


    public static string? GetBg(GameBiz biz)
    {
        return GetValue<string>(default, $"bg_{biz}");
    }

    public static void SetBg(GameBiz biz, string? value)
    {
        SetValue(value, $"bg_{biz}");
    }



    public static bool GetUseVersionPoster(GameBiz biz)
    {
        return GetValue<bool>(default, $"use_version_poster_{biz}");
    }

    public static void SetUseVersionPoster(GameBiz biz, bool value)
    {
        SetValue(value, $"use_version_poster_{biz}");
    }



    public static string? GetVersionPoster(GameBiz biz)
    {
        return GetValue<string>(default, $"version_poster_{biz}");
    }

    public static void SetVersionPoster(GameBiz biz, string? value)
    {
        SetValue(value, $"version_poster_{biz}");
    }



    public static string? GetCustomBg(GameBiz biz)
    {
        return GetValue<string>(default, $"custom_bg_{biz}");
    }

    public static void SetCustomBg(GameBiz biz, string? value)
    {
        SetValue(value, $"custom_bg_{biz}");
    }



    /// <summary>
    /// 这个 dll 类（dlssnr / streamline / …）**我们装的是哪一个变体**。
    /// PE 版本号里没有 <c>SF</c> / <c>SF-v2</c> / <c>RTX40</c> 这种信息，只靠读盘分不出来
    /// （用户反馈：装了 310.8.SF-v2 却显示成 SF），所以装的时候记一笔。
    /// </summary>
    public static string? GetInstalledDllVariant(string familyId)
    {
        return GetValue<string>(default, $"dll_variant_{familyId}");
    }

    public static void SetInstalledDllVariant(string familyId, string? version)
    {
        SetValue(version, $"dll_variant_{familyId}");
    }



    /// <summary>额外注入的 DLL（OptiScaler / DLSS Enabler 那套），每个游戏记一个路径</summary>
    public static string? GetExtraInjectDll(GameBiz biz)
    {
        return GetValue<string>(default, $"extra_inject_dll_{biz}");
    }

    public static void SetExtraInjectDll(GameBiz biz, string? value)
    {
        SetValue(value, $"extra_inject_dll_{biz}");
    }

    // ===================== 模块（要注入游戏进程的东西：DLSS-NR on AMD 那类） =====================

    /// <summary>「模块」根目录：&lt;用户数据目录&gt;\Modules\&lt;模块 id&gt;\</summary>
    public static string ModulesRootPath
    {
        get
        {
            string? userData = UserDataFolder;
            return string.IsNullOrWhiteSpace(userData)
                ? string.Empty
                : Path.Combine(userData, "Modules");
        }
    }

    public static string ModuleDirectory(string moduleId)
    {
        string root = ModulesRootPath;
        return root.Length == 0 ? string.Empty : Path.Combine(root, moduleId);
    }

    /// <summary>模块开关（全局，可以同时开多个）</summary>
    public static bool GetModuleEnabled(string moduleId)
    {
        return GetValue(false, $"module_enabled_{moduleId}");
    }

    public static void SetModuleEnabled(string moduleId, bool value)
    {
        SetValue(value, $"module_enabled_{moduleId}");
    }

    /// <summary>模块要注入的那个 DLL（用户手动指定的；空 = 在模块目录 / 旧的 OptiScaler 库里自动找）</summary>
    public static string? GetModuleDllPath(string moduleId)
    {
        return GetValue<string>(default, $"module_dll_{moduleId}");
    }

    public static void SetModuleDllPath(string moduleId, string? value)
    {
        SetValue(value, $"module_dll_{moduleId}");
    }

    /// <summary>
    /// 模块注入顺序（一行一个 key，列表里没有的排在后面、按默认顺序）。
    /// <para>
    /// 注入顺序是有意义的：桥 / 解锁类模块要在 OptiScaler 之前进进程，
    /// 否则 hook 链顺序不对会互相打架。key 就是 <see cref="ModuleRegistry"/> 里的
    /// 模块 id，或者手动加的那个 DLL 全路径。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> GetModuleOrder()
    {
        string? raw = GetValue<string>(default, "module_order");
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    public static void SetModuleOrder(IEnumerable<string> keys)
    {
        string value = string.Join("\n", keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase));
        SetValue(value.Length == 0 ? null : value, "module_order");
    }

    /// <summary>用户手动加的模块 DLL（一行一个路径）—— 原来「额外注入 DLL」里的那些</summary>
    public static IReadOnlyList<string> GetManualModuleDlls()
    {
        string? raw = GetValue<string>(default, "manual_module_dlls");
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    public static void SetManualModuleDlls(IEnumerable<string> paths)
    {
        string value = string.Join("\n", paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase));
        SetValue(value.Length == 0 ? null : value, "manual_module_dlls");
    }

    /// <summary>用户手动加的那些模块里，被关掉的（默认都是开的）</summary>
    public static IReadOnlyList<string> GetDisabledManualModuleDlls()
    {
        string? raw = GetValue<string>(default, "manual_module_disabled");
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    public static void SetManualModuleDllEnabled(string path, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        List<string> disabled = [.. GetDisabledManualModuleDlls()];
        disabled.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            disabled.Add(path);
        }

        SetValue(disabled.Count == 0 ? null : string.Join("\n", disabled), "manual_module_disabled");
    }

    public static bool IsManualModuleDllEnabled(string path)
        => !GetDisabledManualModuleDlls().Contains(path, StringComparer.OrdinalIgnoreCase);

    /// <summary>老配置「额外注入 DLL」搬进模块页了没有（按游戏记，只搬一次）</summary>
    public static bool GetModuleMigrated(GameBiz biz)
    {
        return GetValue(false, $"module_migrated_{biz}");
    }

    public static void SetModuleMigrated(GameBiz biz, bool value)
    {
        SetValue(value, $"module_migrated_{biz}");
    }

    /// <summary>这个游戏勾了哪些模块（模块 key：内置模块 id，或手动加的 DLL 路径）。
    /// null = 还没选过 → 按「全局开着的都用」算（老配置迁移）。</summary>
    public static IReadOnlyList<string>? GetUsedModuleKeysOrNull(GameId gameId)
    {
        string? raw = GetValue<string>(default, BuildLaunchOptionKey(gameId, "modules_used"));
        return raw is null
            ? null
            : [.. raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    public static void SetUsedModuleKeys(GameId gameId, IEnumerable<string> keys)
    {
        string value = string.Join("\n", keys.Where(k => !string.IsNullOrWhiteSpace(k)).Distinct(StringComparer.OrdinalIgnoreCase));
        SetValue(value, BuildLaunchOptionKey(gameId, "modules_used"));
    }

    /// <summary>启动时注入「已启用的模块」，按游戏记</summary>
    public static bool GetUseModulesLaunchOption(GameId gameId)
    {
        return GetValue(false, BuildLaunchOptionKey(gameId, "use_modules"));
    }

    public static void SetUseModulesLaunchOption(GameId gameId, bool value)
    {
        SetValue(value, BuildLaunchOptionKey(gameId, "use_modules"));
    }

    // ===================== OptiScaler（每个构建一个全局开关） =====================

    /// <summary>OptiScaler 总开关（历史配置，已不在界面显示；新逻辑按构建记全局开关）。</summary>
    public static bool OptiScalerEnabled
    {
        get => GetValue(false, "optiscaler_enabled");
        set => SetValue(value, "optiscaler_enabled");
    }

    /// <summary>被用户「全局关」的 OptiScaler 构建 id（OptiScalerLibrary.MakeId：&lt;来源&gt;/&lt;版本&gt;）</summary>
    public static IReadOnlyList<string> GetDisabledOptiScalerBuilds()
    {
        string? raw = GetValue<string>(default, "optiscaler_disabled_builds");
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    public static bool IsOptiScalerBuildEnabled(string buildId)
        => !string.IsNullOrWhiteSpace(buildId)
           && !GetDisabledOptiScalerBuilds().Contains(buildId, StringComparer.OrdinalIgnoreCase);

    public static void SetOptiScalerBuildEnabled(string buildId, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(buildId))
        {
            return;
        }

        List<string> disabled = [.. GetDisabledOptiScalerBuilds()];
        disabled.RemoveAll(id => string.Equals(id, buildId, StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            disabled.Add(buildId);
        }

        SetValue(disabled.Count == 0 ? null : string.Join("\n", disabled), "optiscaler_disabled_builds");
    }

    /// <summary>这个游戏选的 OptiScaler 构建 id（OptiScalerLibrary.MakeId：&lt;来源&gt;/&lt;版本&gt;；空 = 没选）</summary>
    public static string? GetSelectedOptiScalerId(GameId gameId)
    {
        return GetValue<string>(default, BuildLaunchOptionKey(gameId, "optiscaler_build"));
    }

    public static void SetSelectedOptiScalerId(GameId gameId, string? buildId)
    {
        SetValue(buildId, BuildLaunchOptionKey(gameId, "optiscaler_build"));
    }

    /// <summary>这个游戏实际要注入的 OptiScaler DLL（没开总开关 / 没选 / 那个构建没了 → null）</summary>
    public static string? GetSelectedOptiScalerDll(GameId? gameId)
    {
        try
        {
            string root = OptiScalerRootPath;
            if (root.Length == 0)
            {
                return null;
            }

            var library = new Extensions.Services.OptiScalerLibrary(root);
            string? id = gameId is null ? null : GetSelectedOptiScalerId(gameId);

            // 没选过就退回旧行为（state.json 里那个「选中的构建」），老配置不至于突然失效
            Extensions.Services.OptiScalerBuild? build = string.IsNullOrWhiteSpace(id)
                ? library.GetSelected()
                : library.List().FirstOrDefault(b => b.Id == id);
            if (build is null || !IsOptiScalerBuildEnabled(build.Id))
            {
                return null;
            }

            return build.DllPath;
        }
        catch
        {
            return null;
        }
    }



    /// <summary>
    /// OptiScaler / 模块的本地库根目录。
    ///
    /// <para>
    /// 放在 <b>HoYoShade 目录旁边</b>（<c>&lt;HoYoShade 根&gt;\..\OptiScaler</c>），
    /// 而不是跟用户数据目录走。
    /// </para>
    ///
    /// <para>
    /// 原因（用户反馈）：插件（addon）装在 HoYoShade 目录下，而 OptiScaler/模块 原来跟
    /// &lt;用户数据目录&gt; 走 —— 于是同一台机器上同时装了便携版和安装版时，两边各有各的
    /// OptiScaler 库：从便携版装的构建，在安装版里看不到（反之亦然），用户会以为「装丢了」。
    /// 统一到 HoYoShade 旁边之后，这一类东西整体跟着当前用的 HoYoShade 走。
    /// </para>
    /// </summary>
    /// <summary>
    /// 这个游戏注入 OptiScaler 时用的 DLL 文件名（默认 OptiScaler  OptiScaler.dll）。
    /// 有些游戏会按 dll 名字判断是不是代理，所以允许改（用户要求）。
    /// </summary>
    public static string GetOptiScalerDllName(GameId gameId)
        => GetValue("OptiScaler", $"opti_dll_name_{gameId.GameBiz}") ?? "OptiScaler";

    public static void SetOptiScalerDllName(GameId gameId, string? name)
        => SetValue(string.IsNullOrWhiteSpace(name) ? "OptiScaler" : name.Trim(), $"opti_dll_name_{gameId.GameBiz}");

    public static string OptiScalerRootPath
    {
        get
        {
            // 基准跟着「插件所在的那个 HoYoShade 根」走，和插件同一个根。
            // 不能只看 UserDataFolder：HoYoShade 可能被手动指定到别处
            // （PluginHostLocator.ManualShadeRoot），那样插件在 A 盘、OptiScaler 却跑到 B 盘。
            string? shadeParent = ResolveShadeParentFolder();

            if (!string.IsNullOrWhiteSpace(shadeParent))
            {
                return Path.Combine(shadeParent, "OptiScaler");
            }

            string? userData = UserDataFolder;
            return string.IsNullOrWhiteSpace(userData)
                ? string.Empty
                : Path.Combine(userData, "OptiScaler");
        }
    }

    /// <summary>
    /// 当前 HoYoShade 根目录的上一级（OptiScaler / 模块 就跟它并排）。
    /// 找不到返回 null，调用方退回用户数据目录。
    /// </summary>
    private static string? ResolveShadeParentFolder()
    {
        try
        {
            // 用户在插件页「指定目录」指定过的那份优先
            string? manual = Features.Plugins.PluginHostLocator.ManualShadeRoot;
            if (!string.IsNullOrWhiteSpace(manual))
            {
                string? manualParent = Path.GetDirectoryName(manual.TrimEnd('\\', '/'));
                if (!string.IsNullOrWhiteSpace(manualParent))
                {
                    return manualParent;
                }
            }

            // 否则用默认位置（<用户数据目录>\HoYoShade）的上一级
            string? userData = UserDataFolder;
            if (string.IsNullOrWhiteSpace(userData))
            {
                return null;
            }

            string defaultRoot = Path.Combine(userData,
                HoYoShadeHub.Extensions.Services.ShadeHostLocator.HoYoShadeFolderName);

            return Directory.Exists(defaultRoot) ? userData : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>当前选中的 OptiScaler 主 DLL（没装 / 没选 / 包里没有 dll → null）</summary>
    public static string? GetSelectedOptiScalerDll()
    {
        try
        {
            string root = OptiScalerRootPath;
            return root.Length == 0 ? null : new Extensions.Services.OptiScalerLibrary(root).SelectedDllPath;
        }
        catch
        {
            return null;
        }
    }



    /// <summary>「启动游戏时强制 off」—— 启动/注入之前把 hook 点写 0（用户要求，按游戏）</summary>
    public static bool GetForceHookOffOnLaunch(GameBiz biz)
    {
        return GetValue<bool>(default, $"force_hook_off_{biz}");
    }

    public static void SetForceHookOffOnLaunch(GameBiz biz, bool value)
    {
        SetValue(value, $"force_hook_off_{biz}");
    }



    public static bool GetEnableCustomBg(GameBiz biz)
    {
        return GetValue<bool>(default, $"enable_custom_bg_{biz}");
    }

    public static void SetEnableCustomBg(GameBiz biz, bool value)
    {
        SetValue(value, $"enable_custom_bg_{biz}");
    }



    public static string? GetGameInstallPath(GameBiz biz)
    {
        return GetValue<string>(default, $"install_path_{biz}");
    }

    public static void SetGameInstallPath(GameBiz biz, string? value)
    {
        SetValue(value, $"install_path_{biz}");
    }


    public static bool GetGameInstallPathRemovable(GameBiz biz)
    {
        return GetValue<bool>(default, $"install_path_removable_{biz}");
    }

    public static void SetGameInstallPathRemovable(GameBiz biz, bool value)
    {
        SetValue(value, $"install_path_removable_{biz}");
    }


    public static bool GetEnableThirdPartyTool(GameBiz biz)
    {
        return GetValue<bool>(default, $"enable_third_party_tool_{biz}");
    }

    public static void SetEnableThirdPartyTool(GameBiz biz, bool value)
    {
        SetValue(value, $"enable_third_party_tool_{biz}");
    }



    public static string? GetThirdPartyToolPath(GameBiz biz)
    {
        return GetValue<string>(default, $"third_party_tool_path_{biz}");
    }

    public static void SetThirdPartyToolPath(GameBiz biz, string? value)
    {
        SetValue(value, $"third_party_tool_path_{biz}");
    }



    public static string? GetStartArgument(GameBiz biz)
    {
        return GetValue<string>(default, $"start_argument_{biz}");
    }

    public static void SetStartArgument(GameBiz biz, string? value)
    {
        SetValue(value, $"start_argument_{biz}");
    }


    /// <summary>
    /// 无边框窗口
    /// </summary>
    /// <param name="biz"></param>
    /// <returns></returns>
    private static string BuildLaunchOptionKey(GameId gameId, string optionName)
    {
        return $"launch_option_{optionName}_{gameId.GameBiz}_{gameId.Id}";
    }

    public static bool GetEnableGameLaunchOption(GameId gameId)
    {
        return GetValue(true, BuildLaunchOptionKey(gameId, "enable_game_launch"));
    }

    public static void SetEnableGameLaunchOption(GameId gameId, bool value)
    {
        SetValue(value, BuildLaunchOptionKey(gameId, "enable_game_launch"));
    }

    public static bool GetUseStarwardLaunchOption(GameId gameId)
    {
        return GetValue(false, BuildLaunchOptionKey(gameId, "use_starward"));
    }

    public static void SetUseStarwardLaunchOption(GameId gameId, bool value)
    {
        SetValue(value, BuildLaunchOptionKey(gameId, "use_starward"));
    }

    public static bool GetUseHoYoShadeLaunchOption(GameId gameId)
    {
        return GetValue(false, BuildLaunchOptionKey(gameId, "use_hoyoshade"));
    }

    public static void SetUseHoYoShadeLaunchOption(GameId gameId, bool value)
    {
        SetValue(value, BuildLaunchOptionKey(gameId, "use_hoyoshade"));
    }

    public static bool GetUseOpenHoYoShadeLaunchOption(GameId gameId)
    {
        return GetValue(false, BuildLaunchOptionKey(gameId, "use_open_hoyoshade"));
    }

    public static void SetUseOpenHoYoShadeLaunchOption(GameId gameId, bool value)
    {
        SetValue(value, BuildLaunchOptionKey(gameId, "use_open_hoyoshade"));
    }

    /// <summary>启动时注入 OptiScaler（全局插件页里选中的那个构建），按游戏记</summary>
    public static bool GetUseOptiScalerLaunchOption(GameId gameId)
    {
        return GetValue(false, BuildLaunchOptionKey(gameId, "use_optiscaler"));
    }

    public static void SetUseOptiScalerLaunchOption(GameId gameId, bool value)
    {
        SetValue(value, BuildLaunchOptionKey(gameId, "use_optiscaler"));
    }

    /// <summary>启动时解锁帧率（原神），按游戏记</summary>
    public static bool GetUseFpsUnlockLaunchOption(GameId gameId)
    {
        return GetValue(false, BuildLaunchOptionKey(gameId, "use_fps_unlock"));
    }

    public static void SetUseFpsUnlockLaunchOption(GameId gameId, bool value)
    {
        SetValue(value, BuildLaunchOptionKey(gameId, "use_fps_unlock"));
    }

    /// <summary>帧率解锁目标值（fps），默认 120，按游戏记</summary>
    public static int GetFpsUnlockTarget(GameId gameId)
    {
        return GetValue(120, BuildLaunchOptionKey(gameId, "fps_unlock_target"));
    }

    public static void SetFpsUnlockTarget(GameId gameId, int value)
    {
        SetValue(Math.Clamp(value, 60, 1000), BuildLaunchOptionKey(gameId, "fps_unlock_target"));
    }

    /// <summary>帧率解锁数据已同步到的游戏版本（shellcode 按游戏版本适配），按游戏记</summary>
    public static string? GetFpsUnlockDataVersion(GameId gameId)
        => GetValue<string>(default, BuildLaunchOptionKey(gameId, "fps_unlock_data_version"));
    public static void SetFpsUnlockDataVersion(GameId gameId, string? value)
        => SetValue(value, BuildLaunchOptionKey(gameId, "fps_unlock_data_version"));

    /// <summary>上次自动检查上游数据的时间（UTC ticks），用于节流，按游戏记</summary>
    public static long GetFpsUnlockLastCheckTicks(GameId gameId)
        => GetValue(0L, BuildLaunchOptionKey(gameId, "fps_unlock_last_check"));
    public static void SetFpsUnlockLastCheckTicks(GameId gameId, long ticks)
        => SetValue(ticks, BuildLaunchOptionKey(gameId, "fps_unlock_last_check"));

    /// <summary>启动时注入 XXMI（3DMigoto 的 d3d11.dll），按游戏记</summary>
    public static bool GetUseXxmiInjectLaunchOption(GameId gameId)
    {
        return GetValue(false, BuildLaunchOptionKey(gameId, "use_xxmi_inject"));
    }

    public static void SetUseXxmiInjectLaunchOption(GameId gameId, bool value)
    {
        SetValue(value, BuildLaunchOptionKey(gameId, "use_xxmi_inject"));
    }

    /// <summary>用户手动指定的这个游戏的 MI 实例目录（空 = 自动找）</summary>
    public static string? GetXxmiInstance(string gameBiz)
    {
        return GetValue<string>(null, $"hysx_xxmi_instance_{gameBiz}");
    }

    public static void SetXxmiInstance(string gameBiz, string? path)
    {
        SetValue(path, $"hysx_xxmi_instance_{gameBiz}");
    }

    /// <summary>最近一次 XXMI 启动的结果（「模型替换」页显示用）</summary>
    public static string? XxmiLastLaunch
    {
        get => GetValue<string>(null, "hysx_xxmi_last_launch");
        set => SetValue(value, "hysx_xxmi_last_launch");
    }

    /// <summary>最近一次把 XXMI 加载器铺到哪个游戏目录（「从游戏目录移除」用）</summary>
    public static string? XxmiDeployedGameDir
    {
        get => GetValue<string>(null, "hysx_xxmi_deployed_game_dir");
        set => SetValue(value, "hysx_xxmi_deployed_game_dir");
    }

    /// <summary>用户手动指定的 XXMI 安装根目录（便携版可能在任意盘；空 = 自动找）</summary>
    public static string? XxmiRoot
    {
        get => GetValue<string>(null, "hysx_xxmi_root");
        set => SetValue(value, "hysx_xxmi_root");
    }

    public static bool GetLaunchGenshinBlenderPluginOption(GameId gameId)
    {
        return GetValue(false, BuildLaunchOptionKey(gameId, "genshin_blender_plugin"));
    }

    public static void SetLaunchGenshinBlenderPluginOption(GameId gameId, bool value)
    {
        SetValue(value, BuildLaunchOptionKey(gameId, "genshin_blender_plugin"));
    }

    public static bool GetLaunchZZZBlenderPluginOption(GameId gameId)
    {
        return GetValue(false, BuildLaunchOptionKey(gameId, "zzz_blender_plugin"));
    }

    public static void SetLaunchZZZBlenderPluginOption(GameId gameId, bool value)
    {
        SetValue(value, BuildLaunchOptionKey(gameId, "zzz_blender_plugin"));
    }

    public static bool GetUsePopupWindow(GameBiz biz)
    {
        return GetValue<bool>(false, $"use_popup_window_{biz}");
    }

    /// <summary>
    /// 无边框窗口
    /// </summary>
    /// <param name="biz"></param>
    /// <param name="value"></param>
    public static void SetUsePopupWindow(GameBiz biz, bool value)
    {
        SetValue(value, $"use_popup_window_{biz}");
    }



    [Obsolete("已不用")]
    public static GameBiz GetLastRegionOfGame(GameBiz game)
    {
        return GetValue<GameBiz>(default, $"last_region_of_{game}");
    }


    [Obsolete("已不用")]
    public static void SetLastRegionOfGame(GameBiz game, GameBiz value)
    {
        SetValue(value, $"last_region_of_{game}");
    }


    /// <summary>
    /// 外部截图文件夹
    /// </summary>
    /// <param name="biz"></param>
    /// <returns></returns>
    public static string? GetExternalScreenshotFolder(GameBiz biz)
    {
        return GetValue<string>(default, $"external_screenshot_folder_{biz}");
    }

    /// <summary>
    /// 外部截图文件夹
    /// </summary>
    /// <param name="biz"></param>
    /// <param name="value"></param>
    public static void SetExternalScreenshotFolder(GameBiz biz, string? value)
    {
        SetValue(value, $"external_screenshot_folder_{biz}");
    }


    public static string? GetGameBackgroundIds(GameBiz biz)
    {
        return GetValue<string>(default, $"game_background_ids_{biz}");
    }


    public static void SetGameBackgroundIds(GameBiz biz, string? value)
    {
        SetValue(value, $"game_background_ids_{biz}");
    }

    /// <summary>
    /// 获取游戏的多个安装路径列表（用|分隔）
    /// </summary>
    public static string? GetGameInstallPaths(GameBiz biz)
    {
        return GetValue<string>(default, $"install_paths_{biz}");
    }

    /// <summary>
    /// 设置游戏的多个安装路径列表（用|分隔）
    /// </summary>
    public static void SetGameInstallPaths(GameBiz biz, string? value)
    {
        SetValue(value, $"install_paths_{biz}");
    }

    /// <summary>
    /// 获取当前选中的游戏安装路径索引
    /// </summary>
    public static int GetSelectedGameInstallPathIndex(GameBiz biz)
    {
        return GetValue(0, $"selected_install_path_index_{biz}");
    }

    /// <summary>
    /// 设置当前选中的游戏安装路径索引
    /// </summary>
    public static void SetSelectedGameInstallPathIndex(GameBiz biz, int value)
    {
        SetValue(value, $"selected_install_path_index_{biz}");
    }


    /// <summary>
    /// 获取是否启用 DX12 开关
    /// </summary>
    public static bool GetEnableDX12(GameBiz biz)
    {
        return GetValue<bool>(default, $"enable_dx12_{biz}");
    }

    /// <summary>
    /// 设置是否启用 DX12 开关
    /// </summary>
    public static void SetEnableDX12(GameBiz biz, bool value)
    {
        SetValue(value, $"enable_dx12_{biz}");
    }


    /// <summary>
    /// 获取是否忽略 DX12 兼容性检测
    /// </summary>
    public static bool GetIgnoreDX12Check(GameBiz biz)
    {
        return GetValue<bool>(default, $"ignore_dx12_check_{biz}");
    }

    /// <summary>
    /// 设置是否忽略 DX12 兼容性检测
    /// </summary>
    public static void SetIgnoreDX12Check(GameBiz biz, bool value)
    {
        SetValue(value, $"ignore_dx12_check_{biz}");
    }


    #endregion





    #region Setting Method



    private static Dictionary<string, string?> _settingCache;


    private static void InitializeSettingProvider()
    {
        try
        {
            if (_settingCache is null)
            {
                using var dapper = DatabaseService.CreateConnection();
                _settingCache = dapper.Query<(string Key, string? Value)>("SELECT Key, Value FROM Setting;").ToDictionary(x => x.Key, x => x.Value);
            }
        }
        catch { }
    }



    public static T? GetValue<T>(T? defaultValue = default, [CallerMemberName] string? key = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return defaultValue;
        }
        if (string.IsNullOrWhiteSpace(UserDataFolder))
        {
            return defaultValue;
        }
        InitializeSettingProvider();
        if (_settingCache is null)
        {
            return defaultValue;
        }
        try
        {
            if (_settingCache.TryGetValue(key, out string? value))
            {
                return ConvertFromString(value, defaultValue);
            }
            using var dapper = DatabaseService.CreateConnection();
            value = dapper.QueryFirstOrDefault<string>("SELECT Value FROM Setting WHERE Key=@key LIMIT 1;", new { key });
            _settingCache[key] = value;
            return ConvertFromString(value, defaultValue);
        }
        catch
        {
            return defaultValue;
        }
    }


    /// <summary>这个设置键在库里有没有  用来区分「没设过」和「设成了默认值」</summary>
    public static bool HasValue(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(UserDataFolder))
        {
            return false;
        }

        InitializeSettingProvider();
        if (_settingCache is null)
        {
            return false;
        }

        try
        {
            if (_settingCache.TryGetValue(key, out string? cached))
            {
                return cached is not null;
            }

            using var dapper = DatabaseService.CreateConnection();
            string? value = dapper.QueryFirstOrDefault<string>(
                "SELECT Value FROM Setting WHERE Key=@key LIMIT 1;", new { key });
            _settingCache[key] = value;
            return value is not null;
        }
        catch
        {
            return false;
        }
    }

    private static T? ConvertFromString<T>(string? value, T? defaultValue = default)
    {
        if (value is null)
        {
            return defaultValue;
        }
        var converter = TypeDescriptor.GetConverter(typeof(T));
        if (converter == null)
        {
            return defaultValue;
        }
        return (T?)converter.ConvertFromString(value);
    }


    public static void SetValue<T>(T? value, [CallerMemberName] string? key = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }
        if (string.IsNullOrWhiteSpace(UserDataFolder))
        {
            return;
        }
        InitializeSettingProvider();
        if (_settingCache is null)
        {
            return;
        }
        try
        {
            string? val = value?.ToString();
            if (_settingCache.TryGetValue(key, out string? cacheValue) && cacheValue == val)
            {
                return;
            }
            _settingCache[key] = val;
            using var dapper = DatabaseService.CreateConnection();
            dapper.Execute("INSERT OR REPLACE INTO Setting (Key, Value) VALUES (@key, @val);", new { key, val });
        }
        catch { }
    }



    public static void DeleteAllSettings()
    {
        try
        {
            using var dapper = DatabaseService.CreateConnection();
            dapper.Execute("DELETE FROM Setting WHERE TRUE;");
        }
        catch { }
    }



    public static void ClearCache()
    {
        _settingCache.Clear();
    }



    #endregion





    #region Emoji


    public static Uri EmojiPaimon = new Uri("ms-appx:///Assets/Image/UI_EmotionIcon5.png");

    public static Uri EmojiPom = new Uri("ms-appx:///Assets/Image/20008.png");

    public static Uri EmojiAI = new Uri("ms-appx:///Assets/Image/bdfd19c3bdad27a395890755bb60b162.png");

    public static Uri EmojiBangboo = new Uri("ms-appx:///Assets/Image/pamu.db6c2c7b.png");


    #endregion




}
