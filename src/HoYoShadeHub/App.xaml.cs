using Microsoft.UI.Dispatching;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using HoYoShadeHub.Features.UrlProtocol;
using HoYoShadeHub.Features.ViewHost;
using System;
using System.Collections;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Timers;


namespace HoYoShadeHub;

public partial class App : Application
{
    private static readonly Microsoft.Extensions.Logging.ILogger _log =
        AppConfig.GetLogger<AppLogToken>();
    private sealed class AppLogToken { }


    private readonly DispatcherQueue _uiDispatcherQueue;

    private readonly Timer _gcTimer = new(TimeSpan.FromSeconds(60));

    // 跨版本单实例：第二个实例通过这个命名事件把已有窗口叫到前台
    public const string ActivateEventName = "Local\\HoYoShadeHub.Activate.v1";

    private System.Threading.EventWaitHandle? _activateEvent;

    public static new App Current => (App)Application.Current;


    public App()
    {
        this.InitializeComponent();
        RequestedTheme = ApplicationTheme.Dark;
        _uiDispatcherQueue = DispatcherQueue.GetForCurrentThread();
        UnhandledException += App_UnhandledException;
        _gcTimer.Elapsed += (_, _) => GC.Collect();
        _ = AppConfig.Language;
    }


    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        string logFile = AppConfig.LogFile;
        if (string.IsNullOrWhiteSpace(logFile))
        {
            Directory.CreateDirectory(AppConfig.LogFolder);
            logFile = Path.Combine(AppConfig.LogFolder, $"HoYoShadeHub_{DateTime.Now:yyMMdd}.log");
        }
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] App Crash:");
        sb.AppendLine(e.Exception.ToString());
        if (e.Exception.Data.Count > 0)
        {
            foreach (DictionaryEntry item in e.Exception.Data)
            {
                sb.AppendLine($"{item.Key}: {item.Value}");
            }
        }
        using var fs = File.Open(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var sw = new StreamWriter(fs);
        sw.Write(sb);
    }


    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs _)
    {
        instance = AppInstance.GetCurrent();
        instance.Activated += AppInstance_Activated;
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1)
        {
            if (Uri.TryCreate(args[1], UriKind.Absolute, out Uri? uri))
            {
                if (uri.Host is "test")
                {
                    new TestUrlProtocolWindow().Activate();
                    return;
                }
            }
        }
        var main = AppInstance.FindOrRegisterForKey("main");
        if (!main.IsCurrent)
        {
            // 重定向到已运行实例；对方以更高权限运行时 Redirect 会挂起/拒绝 ——
            // 加 3s 超时并吞异常直接退出，避免每次重开都留下一个吃内存的后台进程
            try
            {
                Task redirect = main.RedirectActivationToAsync(instance.GetActivatedEventArgs()).AsTask();
                if (await Task.WhenAny(redirect, Task.Delay(3000)) != redirect)
                {
                    _log.LogWarning("单实例重定向超时（已运行实例可能为管理员权限），本实例直接退出");
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "单实例重定向失败，本实例直接退出");
            }

            this.Exit();
            return;
        }
        if (Environment.GetCommandLineArgs().Contains("--hide"))
        {
            m_SystemTrayWindow = new SystemTrayWindow();
        }
        else
        {
            m_MainWindow = new MainWindow();
            m_MainWindow.Activate();
        }

        StartActivateWatcher();
    }

    private void StartActivateWatcher()
    {
        _activateEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, ActivateEventName);
        System.Threading.ThreadPool.RegisterWaitForSingleObject(
            _activateEvent,
            (_, _) => _uiDispatcherQueue.TryEnqueue(() =>
            {
                EnsureMainWindow();
            }),
            null,
            System.Threading.Timeout.Infinite,
            false);
    }



    private AppInstance instance;

    private MainWindow m_MainWindow;

    private SystemTrayWindow m_SystemTrayWindow;



    public void EnsureMainWindow()
    {
        m_MainWindow ??= new MainWindow();
        m_MainWindow.Activate();
        m_MainWindow.Show();
    }


    public void EnsureSystemTray()
    {
        m_SystemTrayWindow ??= new SystemTrayWindow();
    }



    private void AppInstance_Activated(object? sender, AppActivationArguments e)
    {
        _uiDispatcherQueue.TryEnqueue(EnsureMainWindow);
    }



    public static AppInstance? FindInstanceForKey(string key)
    {
        foreach (var item in AppInstance.GetInstances())
        {
            if (item.Key == key)
            {
                return item;
            }
        }
        return null;
    }



    public new void Exit()
    {
        m_MainWindow?.Close();
        m_SystemTrayWindow?.Close();
        Application.Current.Exit();
    }



}
