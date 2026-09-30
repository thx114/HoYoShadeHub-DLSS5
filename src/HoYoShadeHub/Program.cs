global using HoYoShadeHub.Language;
using Microsoft.Extensions.Configuration;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Features.GameLauncher;
using HoYoShadeHub.Features.Plugins;
using HoYoShadeHub.Features.UrlProtocol;
using HoYoShadeHub.RPC;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace HoYoShadeHub;

#if DISABLE_XAML_GENERATED_MAIN

/// <summary>
/// Program class
/// </summary>
public static class Program
{


    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2411")]
    //[global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
    [global::System.STAThreadAttribute]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        if (!AppConfig.IsAdmin)
        {
            bool skip = false;
            if (args.Length > 0)
            {
                string arg = args[0].ToLower();
                // rpc / playtime / run / stopgame：静默通道，不弹 UAC 重启（保持调用方控制台 / 无窗口）
                if (arg is "rpc" or "playtime" or "run" or "auto" or "action" or "stopgame" or "killgame")
                {
                    skip = true;
                }
            }
            if (!skip)
            {
                try
                {
                    var info = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = AppConfig.HoYoShadeHubExecutePath,
                        UseShellExecute = true,
                        Verb = "runas",
                        Arguments = string.Join(" ", args)
                    };
                    System.Diagnostics.Process.Start(info);
                }
                catch { }
                return 0;
            }
        }

        if (AppConfig.IsAdmin)
        {
            try
            {
                if (Helpers.ZoneIdentifierHelper.HasZoneIdentifier(AppConfig.HoYoShadeHubExecutePath))
                {
                    string targetDir = AppContext.BaseDirectory;
                    if (AppConfig.IsPortable)
                    {
                        var parentDir = new DirectoryInfo(AppContext.BaseDirectory).Parent;
                        if (parentDir != null)
                        {
                            targetDir = parentDir.FullName;
                        }
                    }
                    Helpers.ZoneIdentifierHelper.ClearDirectoryZoneIdentifiers(targetDir);
                }
            }
            catch { }
        }

        if (args.Length > 0)
        {
            IConfiguration config = new ConfigurationBuilder().AddCommandLine(args).Build();
            if (args[0].ToLower() is "rpc")
            {
                RpcRunner.Run(args);
                return 0;
            }
            if (args[0].ToLower() is "playtime")
            {
                int pid = config.GetValue<int>("pid");
                GameBiz biz = (GameBiz)config.GetValue<string>("biz");
                if (pid > 0)
                {
                    var playtime = AppConfig.GetService<Features.PlayTime.PlayTimeService>();
                    playtime.LogPlayTimeAsync(biz, pid).GetAwaiter().GetResult();
                }
                return 0;
            }

            if (args[0].ToLower() is "run" or "auto" or "action")
            {
                return RunCliActions(config);
            }

            if (args[0].ToLower() is "stopgame" or "killgame")
            {
                // 结束游戏：就是跑一个 stop_game 步骤
                return RunStepsJson(config.GetValue<string>("biz"), """{"steps":[{"action":"stop_game"}]}""");
            }

            if (args[0].ToLower() is "startgame")
            {
                GameBiz biz = (GameBiz)config.GetValue<string>("biz");
                GameId? gameId = GameId.FromGameBiz(biz);
                if (gameId is not null)
                {
                    AppConfig.GetService<GameLauncherService>().StartGameAsync(gameId).GetAwaiter().GetResult();
                }
                return 0;
            }

            if (args[0].ToLower().StartsWith("hoyoshadehub://"))
            {
                // ConfigureAwait(false)：协议处理在主线程上同步阻塞等待，
                // 内部 HttpClient 的异步延续若要回到主线程会死锁。
                if (UrlProtocolService.HandleUrlProtocolAsync(args[0]).ConfigureAwait(false).GetAwaiter().GetResult())
                {
                    return 0;
                }
            }
        }


        using var singleInstanceMutex = new System.Threading.Mutex(true, "Local\\HoYoShadeHub.SingleInstance.v1", out bool createdNew);
        if (!createdNew)
        {
            try
            {
                using var existing = System.Threading.EventWaitHandle.OpenExisting(App.ActivateEventName);
                existing.Set();
            }
            catch { }
            return 0;
        }

        global::WinRT.ComWrappersSupport.InitializeComWrappers();
        global::Microsoft.UI.Xaml.Application.Start((p) =>
        {
            var context = new global::Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(global::Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            global::System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }

    /// <summary>
    /// <c>run</c>：headless 执行动作步骤（与 auto.json 同一套接口词汇），可静默启动游戏。
    /// 步骤直接来自命令行，不走覆盖包 / 同意弹窗 —— 显式命令行调用本身就是授权。
    /// 用法：
    ///   HoYoShadeHub.exe run --biz hkrpg_cn --file steps.json
    ///   HoYoShadeHub.exe run --biz hkrpg_cn --json "{\"steps\":[{\"action\":\"set_dx12\",\"enabled\":true},{\"action\":\"launch_game\"}]}"
    /// 退出码：0 全成功；1 有步骤失败 / 参数不对。
    /// </summary>
    private static int RunCliActions(IConfiguration config)
    {
        string? json = config.GetValue<string>("json");
        string? file = config.GetValue<string>("file");
        if (string.IsNullOrWhiteSpace(json) && !string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file))
            {
                Console.Error.WriteLine("动作文件不存在：" + file);
                return 1;
            }

            json = File.ReadAllText(file);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            Console.Error.WriteLine("需要 --file <动作.json> 或 --json \"<动作 JSON>\"");
            return 1;
        }

        return RunStepsJson(config.GetValue<string>("biz"), json);
    }

    /// <summary>解析 --biz + 动作 JSON，headless 执行；退出码 0 = 全成功，1 = 有步骤失败 / 参数不对。</summary>
    private static int RunStepsJson(string? bizText, string json)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch { }

        GameId? gameId = string.IsNullOrWhiteSpace(bizText) ? null : GameId.FromGameBiz((GameBiz)bizText);
        if (gameId is null)
        {
            Console.Error.WriteLine("需要 --biz <游戏代码>，例如 hkrpg_cn（星铁国服）/ hk4e_cn（原神国服）/ nap_cn（绝区零国服）");
            return 1;
        }

        List<PackAutoAction> actions = PackAutoActionFile.Parse(json);
        if (actions.Count == 0)
        {
            Console.Error.WriteLine("没有可执行的动作（检查 JSON：{\"steps\":[{\"action\":\"...\"}]} 或 {\"actions\":[...]}）");
            return 1;
        }

        // headless 上下文：无页面 / 无弹窗，UI 依赖型步骤自己会报 ✗ 跳过
        GameEntry? entry = null;
        try
        {
            entry = GameCatalog.GetOrCreate(GameCatalog.CreateService(), gameId);
        }
        catch (Exception ex)
        {
            Console.WriteLine("!! 读取游戏信息失败：" + ex.Message);
        }

        ShadeHost? host = PluginHostLocator.Resolve(out string hostReason);
        if (host is null)
        {
            Console.WriteLine("!! " + hostReason);
        }

        GamePluginService? plugins = null;
        try
        {
            if (entry is not null)
            {
                plugins = GamePluginServiceFactory.Create(entry, host);
            }
        }
        catch
        {
            // 读不出插件状态就只跑不依赖 ini 的步骤
        }

        var context = new LauncherActionContext
        {
            GameId = gameId,
            GameBiz = gameId.GameBiz,
            Entry = entry,
            Host = host,
            Plugins = plugins,
            LauncherPage = null,
            PackRoot = null,
            XamlRoot = null,
            Interactive = false,
            Report = line => Console.WriteLine("  " + line),
        };

        bool failed = false;
        foreach (PackAutoAction action in actions)
        {
            Console.WriteLine("== " + action.Name + " ==");
            string summary = LauncherActionRunner.RunAsync(action, context).ConfigureAwait(false).GetAwaiter().GetResult();
            Console.WriteLine(summary);
            if (summary.Contains('✗'))
            {
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }

    private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        string logFile = AppConfig.LogFile;
        if (string.IsNullOrWhiteSpace(logFile))
        {
            Directory.CreateDirectory(AppConfig.LogFolder);
            logFile = Path.Combine(AppConfig.LogFolder, $"HoYoShadeHub_{DateTime.Now:yyMMdd}.log");
        }
        var sb = new StringBuilder();
        sb.AppendLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Program Crash:");
        sb.AppendLine(e.ExceptionObject.ToString());
        if (e.ExceptionObject is Exception { Data.Count: > 0 } ex)
        {
            foreach (DictionaryEntry item in ex.Data)
            {
                sb.AppendLine($"{item.Key}: {item.Value}");
            }
        }
        using var fs = File.Open(logFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        using var sw = new StreamWriter(fs);
        sw.Write(sb);
    }
}

#endif


