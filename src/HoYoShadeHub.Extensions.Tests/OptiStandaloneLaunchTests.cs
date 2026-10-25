namespace HoYoShadeHub.Extensions.Tests;

internal static class OptiStandaloneLaunchTests
{
    public static int Run()
    {
        string root = FindSourceRoot();
        string page = File.ReadAllText(Path.Combine(root, "src", "HoYoShadeHub", "Features", "GameLauncher", "GameLauncherPage.StartGame.cs"));
        string pipeline = File.ReadAllText(Path.Combine(root, "src", "HoYoShadeHub", "Features", "GameLauncher", "GameLaunchPipeline.cs"));
        string rocket = File.ReadAllText(Path.Combine(root, "src", "HoYoShadeHub", "Features", "GameLauncher", "RocketLaunchPreparation.cs"));
        string registry = File.ReadAllText(Path.Combine(root, "src", "HoYoShadeHub", "Features", "Modules", "ModuleRegistry.cs"));
        string routing = File.ReadAllText(Path.Combine(root, "src", "HoYoShadeHub.Extensions", "Games", "GenshinLaunchRouting.cs"));

        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
        }

        Check(page.Contains("继续独立启动 OptiScaler", StringComparison.Ordinal),
            "启动页 Bridge 检查必须放行独立 OptiScaler");
        Check(page.Contains("OptiScaler 将独立外部注入", StringComparison.Ordinal),
            "启动页缺 Bridge 时必须保留 OptiScaler 注入规格");
        Check(!page.Contains("已跳过 OptiScaler 注入", StringComparison.Ordinal),
            "启动页不能再把缺 Bridge 当成 OptiScaler 硬失败");
        Check(pipeline.Contains("OptiScaler 将独立外部注入", StringComparison.Ordinal),
            "CLI 管线缺 Bridge 时必须保留 OptiScaler 注入规格");
        Check(!pipeline.Contains("将拒绝 OptiScaler 外部注入", StringComparison.Ordinal),
            "CLI 管线不能再因缺 Bridge 拒绝 OptiScaler");
        Check(rocket.Contains("PrepareGenshinWithoutBridge", StringComparison.Ordinal)
            && rocket.Contains("PreparePluginList(config, \"原神\"", StringComparison.Ordinal),
            "Rocket 原神缺 Bridge 时必须回退到插件列表");
        Check(routing.Contains("&& !string.IsNullOrWhiteSpace(bridgeDllPath)", StringComparison.Ordinal),
            "有序 Bridge 链仍必须要求 Bridge 路径");

        int ensureStart = registry.IndexOf("public static bool EnsureGenshinFsrBridgeForLaunch", StringComparison.Ordinal);
        int ensureEnd = registry.IndexOf("private static void RemoveManualGenshinFsrBridgeSelections", ensureStart, StringComparison.Ordinal);
        Check(ensureStart >= 0 && ensureEnd > ensureStart,
            "Bridge 启动准备方法边界必须存在");
        string ensure = registry[ensureStart..ensureEnd];
        Check(!ensure.Contains("SetUseModulesLaunchOption", StringComparison.Ordinal)
            && !ensure.Contains("SetUsed(gameId", StringComparison.Ordinal)
            && !ensure.Contains("SetBundledModuleRemoved", StringComparison.Ordinal),
            "OptiScaler 启动准备不能持久化打开模块或改写模块选择");
        Check(!pipeline.Contains("EnsureGenshinFsrBridgeForLaunch", StringComparison.Ordinal),
            "CLI 管线不能因 OptiScaler 自动准备并启用 Bridge");
        Check(!page.Contains("UseModules = true;", StringComparison.Ordinal),
            "启动页不能因 OptiScaler 自动打开启用模块");
        int earlyStart = page.IndexOf("Keep early graphics loading for Genshin without XXMI", StringComparison.Ordinal);
        Check(earlyStart >= 0,
            "原神早期启动分支必须存在");
        string early = page[earlyStart..];
        Check(early.Contains("&& UseModules", StringComparison.Ordinal),
            "原神早期 Bridge 批注入必须受启用模块总开关保护");
        Check(early.Contains("string? bridgePath = UseModules", StringComparison.Ordinal),
            "原神早期 Bridge 路径解析必须再次绑定启用模块状态");
        Check(page.Contains("string? chainBridgeDll = UseModules", StringComparison.Ordinal),
            "XXMI 有序 Bridge 链路径解析必须受启用模块总开关保护");
        Check(rocket.Contains("GetUseModulesLaunchOption(gameId)", StringComparison.Ordinal),
            "Rocket 只允许在用户显式启用模块时使用 Bridge");

        Console.WriteLine($"Opti standalone launch: PASS {passed} / FAIL 0; GameTested=false.");
        return passed;
    }

    private static string FindSourceRoot()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "HoYoShadeHub", "Features", "GameLauncher", "GameLaunchPipeline.cs")))
                    return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Launcher source root is required for Opti standalone verification.");
    }
}
