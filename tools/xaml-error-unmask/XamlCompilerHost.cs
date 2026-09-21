// ============================================================================
//  XamlCompiler 静默失败 —— 把真错误挖出来的最小宿主
//
//  背景：WindowsAppSDK 1.7 的 net472 XamlCompiler.exe 报错时会抛
//  MissingManifestResourceException（错误消息资源名不匹配 + 中文系统附属程序集缺失），
//  结果 MSBuild 只看到 "已退出，代码为 1"，真正的 XAML 解析错误被吞掉。
//
//  用法（Windows PowerShell 5.1 / pwsh 都行）：
//    $csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
//    & $csc /nologo /target:exe /out:$env:TEMP\xcu\XamlCompilerHost.exe XamlCompilerHost.cs
//
//    # 把 tools\net472 整个复制到 $env:TEMP\xcu\（宿主和编译器要在同一个 ApplicationBase 里）
//    Copy-Item "$env:USERPROFILE\.nuget\packages\microsoft.windowsappsdk\<版本>\tools\net472\*" $env:TEMP\xcu\ -Force
//
//    # 注意：$env:TEMP\xcu 下面**不要**有 zh-CN\XamlCompiler.resources.dll
//    #（版本对不上的附属程序集会抛 FileLoadException，反而让它更早死）
//    & $env:TEMP\xcu\XamlCompilerHost.exe $env:TEMP\xcu\XamlCompiler.exe <obj>\input.json $env:TEMP\xcu\output.json
//
//    # 真错误在 %TEMP%\xc_probe.log 里，形如：
//    #   [FCE] Microsoft.UI.Xaml.Markup.Compiler.ParseException: Property 'Xxx' not found on type 'Yyy'.
//
//  也可以顺带看到 [RESOLVE-FAIL] / [LOAD]，排查程序集解析问题。
// ============================================================================

using System;
using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;

public class Listener2 : MarshalByRefObject
{
    public static string LogPath = Path.Combine(Path.GetTempPath(), "xc_probe.log");

    public void Start()
    {
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.AssemblyResolve += OnResolve;
        AppDomain.CurrentDomain.AssemblyLoad += OnLoad;
    }

    static void Write(string text)
    {
        try { File.AppendAllText(LogPath, text + Environment.NewLine); } catch { }
    }

    static void OnFirstChance(object sender, FirstChanceExceptionEventArgs e)
    {
        Write("[FCE] " + e.Exception.GetType().FullName + ": " + e.Exception.Message);
    }

    static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
    {
        Write("[UNHANDLED] " + e.ExceptionObject);
    }

    static Assembly OnResolve(object sender, ResolveEventArgs e)
    {
        Write("[RESOLVE-FAIL] " + e.Name + "   (requesting: " + (e.RequestingAssembly == null ? "null" : e.RequestingAssembly.GetName().Name) + ")");
        return null;
    }

    static void OnLoad(object sender, AssemblyLoadEventArgs e)
    {
        try { Write("[LOAD] " + e.LoadedAssembly.GetName().Name + " " + e.LoadedAssembly.GetName().Version); } catch { }
    }
}

class XamlCompilerHost3
{
    static int Main(string[] args)
    {
        string toolsDir = Path.GetDirectoryName(Path.GetFullPath(args[0]));
        string compiler = Path.GetFullPath(args[0]);
        string input = Path.GetFullPath(args[1]);
        string output = Path.GetFullPath(args[2]);
        string log = Listener2.LogPath;

        try { File.Delete(log); } catch { }

        var setup = new AppDomainSetup();
        setup.ApplicationBase = toolsDir;
        setup.ConfigurationFile = Path.Combine(toolsDir, "XamlCompiler.exe.config");

        AppDomain domain = AppDomain.CreateDomain("XamlCompiler", null, setup);

        try
        {
            var listener = (Listener2)domain.CreateInstanceAndUnwrap(
                typeof(Listener2).Assembly.FullName, typeof(Listener2).FullName);
            listener.Start();

            int code = domain.ExecuteAssembly(compiler, new string[] { input, output });
            Console.Error.WriteLine("[host] domain returned " + code);
            return code;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[host] " + ex);
            return 96;
        }
        finally
        {
            try { AppDomain.Unload(domain); } catch { }
        }
    }
}