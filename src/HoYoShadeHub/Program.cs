global using HoYoShadeHub.Language;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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
using System.Linq;
using System.Text;

namespace HoYoShadeHub;

#if DISABLE_XAML_GENERATED_MAIN

/// <summary>
/// Program class
/// </summary>
public static class Program
{


    /// <summary>提权副本标记：后面跟接力文件路径，输出经它回流到原控制台。</summary>
    private const string ElevatedChildFlag = "--elevated-child";

    private static string? _relayFile;



    [global::System.CodeDom.Compiler.GeneratedCodeAttribute("Microsoft.UI.Xaml.Markup.Compiler", " 3.0.0.2411")]
    //[global::System.Diagnostics.DebuggerNonUserCodeAttribute()]
    [global::System.STAThreadAttribute]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        // 多人/反作弊游戏后台守卫：只读轮询进程名，发现即暂停一切注入/解锁
        Features.GameLauncher.MultiplayerGameGuard.EnsureStarted();

        // 提权副本：摘掉接力标记（原样留在 args 里会把 ConfigurationBuilder 搞糊涂）
        var argList = new List<string>(args);
        int flagIndex = argList.IndexOf(ElevatedChildFlag);
        if (flagIndex >= 0)
        {
            argList.RemoveAt(flagIndex);
            if (flagIndex < argList.Count)
            {
                _relayFile = argList[flagIndex];
                argList.RemoveAt(flagIndex);
            }

            args = [.. argList];
        }

        if (!AppConfig.IsAdmin)
        {
            bool skip = false;
            if (args.Length > 0)
            {
                string arg = args[0].ToLower();
                // CLI 动词走免 UAC 通道：让 RunStepsJson 自己判断要不要提权（带输出接力），
                // 而不是在这里走 GUI 的 runas 重启（不等待、无输出、退出码恒 0）。
                // rpc / playtime：静默通道，永不提权。
                if (arg is "rpc" or "playtime" or "run" or "auto" or "action"
                    or "stopgame" or "killgame" or "startgame" or "testgame" or "test-game" or "selftest")
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
                return RunCliActions(config, args);
            }

            if (args[0].ToLower() is "testgame" or "test-game" or "selftest")
            {
                string secondsText = config.GetValue<string>("seconds") ?? config.GetValue<string>("duration") ?? "60";
                if (!double.TryParse(secondsText, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double seconds) || !double.IsFinite(seconds) || seconds < 0)
                {
                    CliPrint("--seconds 必须是非负数字。", error: true);
                    return 1;
                }

                string json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    steps = new[] { new { action = "test_game", seconds } }
                });
                return RunStepsJson(config.GetValue<string>("biz"), json, args);
            }

            if (args[0].ToLower() is "stopgame" or "killgame")
            {
                // 结束游戏：就是跑一个 stop_game 步骤
                return RunStepsJson(config.GetValue<string>("biz"), """{"steps":[{"action":"stop_game"}]}""", args);
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
    private static int RunCliActions(IConfiguration config, string[] originalArgs)
    {
        string? json = config.GetValue<string>("json");
        string? file = config.GetValue<string>("file");
        if (string.IsNullOrWhiteSpace(json) && !string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file))
            {
                CliPrint("动作文件不存在：" + file, error: true);
                return 1;
            }

            json = File.ReadAllText(file);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            CliPrint("需要 --file <动作.json> 或 --json \"<动作 JSON>\"", error: true);
            return 1;
        }

        return RunStepsJson(config.GetValue<string>("biz"), json, originalArgs);
    }

    /// <summary>解析 --biz + 动作 JSON，headless 执行；退出码 0 = 全成功，1 = 有步骤失败 / 参数不对。</summary>
    private static int RunStepsJson(string? bizText, string json, string[]? originalArgs = null)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch { }

        // CLI 调用先落日志：不管后面哪一步挂了，日志里都能看见「这次调用确实进来了」
        AppConfig.EnsureLoggingInitialized();
        Serilog.Log.Information("CLI steps: biz={Biz}, json length={Length}", bizText, json?.Length ?? 0);

        GameId? gameId = string.IsNullOrWhiteSpace(bizText) ? null : GameId.FromGameBiz((GameBiz)bizText);
        if (gameId is null)
        {
            CliPrint("需要 --biz <游戏代码>，例如 hkrpg_cn（星铁国服）/ hk4e_cn（原神国服）/ nap_cn（绝区零国服）", error: true);
            return 1;
        }

        List<PackAutoAction> actions = PackAutoActionFile.Parse(json);
        if (actions.Count == 0)
        {
            CliPrint("没有可执行的动作（检查 JSON：{\"steps\":[{\"action\":\"...\"}]} 或 {\"actions\":[...]}）", error: true);
            return 1;
        }

        // 要碰游戏进程的步骤（启动/结束）必须提权跑：HoYoShade 注入器需要管理员才能把
        // ReShade 注进游戏（GUI 启动器本来就是提权的，CLI 从普通控制台跑不是 —— 注不进去，
        // 游戏起来没插件，2026-10-01 实测）。提权副本的输出经接力文件回流到本控制台。
        if (!AppConfig.IsAdmin && originalArgs is not null && actions.Any(a => a.Steps.Any(s =>
                s.Action is "launch_game" or "launch" or "start_game"
                    or "stop_game" or "close_game" or "kill_game" or "stopgame"
                    or "test_game" or "launch_wait_stop" or "self_test")))
        {
            return RelaunchElevatedWithRelay(originalArgs);
        }

        // headless 上下文：无页面 / 无弹窗，UI 依赖型步骤自己会报 ✗ 跳过
        GameEntry? entry = null;
        try
        {
            entry = GameCatalog.GetOrCreate(GameCatalog.CreateService(), gameId);
        }
        catch (Exception ex)
        {
            CliPrint("!! 读取游戏信息失败：" + ex.Message, error: true);
        }

        ShadeHost? host = PluginHostLocator.Resolve(out string hostReason);
        if (host is null)
        {
            CliPrint("!! " + hostReason, error: true);
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
            Report = line => CliPrint("  " + line),
        };

        bool failed = false;
        foreach (PackAutoAction action in actions)
        {
            CliPrint("== " + action.Name + " ==");
            string summary = LauncherActionRunner.RunAsync(action, context).ConfigureAwait(false).GetAwaiter().GetResult();
            CliPrint(summary);
            if (summary.Contains('✗'))
            {
                failed = true;
            }
        }

        return failed ? 1 : 0;
    }

    /// <summary>
    /// 以管理员重启自己执行同一组 CLI 参数：提权副本把输出写进接力文件，
    /// 本进程（中等完整性）轮询把内容转印回控制台，并透传退出码。
    /// 跨完整性等对方进程句柄是允许的（有限权限即可）。
    /// </summary>
    private static int RelaunchElevatedWithRelay(string[] args)
    {
        string relay = Path.Combine(Path.GetTempPath(), $"hysx-cli-{Environment.ProcessId}-{Guid.NewGuid():N}.log");
        _relayFile = relay;

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Environment.ProcessPath ?? AppConfig.HoYoShadeHubExecutePath,
            UseShellExecute = true,
            Verb = "runas",
        };
        foreach (string arg in args)
        {
            if (arg == ElevatedChildFlag)
            {
                continue; // 防重复套娃
            }
            startInfo.ArgumentList.Add(arg);
        }
        startInfo.ArgumentList.Add(ElevatedChildFlag);
        startInfo.ArgumentList.Add(relay);

        System.Diagnostics.Process? child;
        try
        {
            child = System.Diagnostics.Process.Start(startInfo);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            CliPrint("用户取消了管理员授权（UAC），未执行。", error: true);
            return 1;
        }
        if (child is null)
        {
            CliPrint("提权启动失败。", error: true);
            return 1;
        }

        Console.WriteLine("已请求管理员权限（UAC），输出实时回流：");
        long position = 0;
        while (!child.HasExited)
        {
            position = DumpRelay(relay, position);
            child.WaitForExit(150);
        }
        DumpRelay(relay, position);

        // 接力文件顺手清掉；读不到的（被占用等）就留给 Temp 清理
        try { File.Delete(relay); } catch { }
        _relayFile = null;
        return child.ExitCode;
    }

    /// <summary>控制台输出一份、接力文件写一份（提权副本的双通道输出）。</summary>
    private static void CliPrint(string line, bool error = false)
    {
        if (error)
        {
            Console.Error.WriteLine(line);
        }
        else
        {
            Console.WriteLine(line);
        }

        string? relay = _relayFile;
        if (relay is null)
        {
            return;
        }
        try
        {
            File.AppendAllText(relay, line + Environment.NewLine);
        }
        catch { }
    }

    /// <summary>把接力文件自 position 起的新内容转印到控制台，返回新位置。</summary>
    private static long DumpRelay(string relay, long position)
    {
        if (!File.Exists(relay))
        {
            return position;
        }
        try
        {
            using var fs = new FileStream(relay, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (position > fs.Length)
            {
                position = 0; // 文件被截断/重写，从头读
            }
            fs.Seek(position, SeekOrigin.Begin);
            using var reader = new StreamReader(fs);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                Console.WriteLine(line);
            }
            return fs.Position;
        }
        catch
        {
            return position;
        }
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





