using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.Modules;
using HoYoShadeHub.Features.OptiScaler;
using HoYoShadeHub.Features.Plugins;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>
/// 无 UI 的完整启动管线：CLI（<c>run ... launch_game</c>）和 auto.json 的 launch_game 走这里，
/// 行为和启动页「启动游戏」一致 —— 不再只是干巴巴地 StartGameAsync（那样插件 / OptiScaler / 帧率解锁全丢）。
///
/// 顺序对齐 <see cref="GameLauncherPage.StartGameAsync"/> 的 Case 5：
/// ① 强制 hook off → ② 游戏目录 dlssg 换 310.9 → ③ HoYoShade/OpenHoYoShade 注入器就绪后启动
/// （没勾 shade 就直接启动）→ ④ 模块 / OptiScaler 额外注入 → ⑤ 帧率解锁。
///
/// 与页面的差异：没有 InAppToast / DispatcherQueue（report 回调打命令行 / 日志）、
/// 注入预热只按时间等（不掐主窗口出现的点）、CLI 模式会**等注入完成才返回**（blockForInjection），
/// UI 上下文则后台 fire-and-forget（和页面一样）。
/// </summary>
public static class GameLaunchPipeline
{
    private static readonly ILogger _logger = AppConfig.GetLogger<GameLauncherService>();

    private sealed record InjectSpec(
        string Path,
        string Label,
        bool WaitForReady = false,
        int DelaySeconds = 0,
        string? BuildDirectory = null,
        string? GameKey = null);

    /// <summary>
    /// 按启动选项完整启动一个游戏。
    /// </summary>
    /// <param name="blockForInjection">true = 命令行：等到模块 / OptiScaler 注进去才返回；
    /// false = UI 上下文：后台注入，函数随即返回。</param>
    /// <param name="report">给人看的进度行（CLI 打控制台）</param>
    public static async Task<Process?> LaunchAsync(
        GameId gameId,
        GameEntry? entry,
        bool blockForInjection,
        Action<string>? report = null,
        CancellationToken ct = default)
    {
        report?.Invoke("准备启动 " + gameId.GameBiz);

        ApplyForceHookOff(gameId, entry);

        // 和启动页同一步骤：覆盖包 auto.json 里标了 runOnLaunch 的动作（含文件覆盖层）先跑。
        // 没同意过 / 被单独禁用的不跑 —— 与页面行为一致（CLI 不替用户做同意决定）。
        await RunPackAutoActionsOnLaunchAsync(gameId, entry, report, ct);

        bool useOptiScaler = AppConfig.GetUseOptiScalerLaunchOption(gameId);
        EnsureGameDlssgForMfg(gameId, entry, useOptiScaler);

        bool useHoYoShade = AppConfig.GetUseHoYoShadeLaunchOption(gameId);
        bool useOpenHoYoShade = AppConfig.GetUseOpenHoYoShadeLaunchOption(gameId);
        if (useHoYoShade && useOpenHoYoShade)
        {
            useOpenHoYoShade = false;
        }

        var service = AppConfig.GetService<GameLauncherService>();
        Process? process;

        if (useHoYoShade || useOpenHoYoShade)
        {
            string shadeName = useHoYoShade ? "HoYoShade" : "OpenHoYoShade";
            string shadePath = Path.Combine(AppConfig.UserDataFolder, shadeName);

            string? shadeError = ValidateShade(shadePath, shadeName);
            if (shadeError is not null)
            {
                report?.Invoke("✗ " + shadeError);
                return null;
            }

            AlignGameIniPaths(gameId, entry, shadePath);

            string installPath = GameLauncherService.GetGameInstallPath(gameId);
            if (string.IsNullOrWhiteSpace(installPath))
            {
                report?.Invoke("✗ 找不到游戏安装目录");
                return null;
            }

            string gameExeName = await service.GetGameExeNameAsync(gameId);
            report?.Invoke($"启动 {shadeName} 注入器（等就绪信号）…");

            var (success, exitCode, injectorProcess) = await InjectorHelper.StartAndWaitForReadyAsync(
                Path.Combine(shadePath, "inject.exe"), gameExeName, shadePath, _logger, shadeName);

            if (!success)
            {
                report?.Invoke(InjectorErrorCodes.IsInjectorError(exitCode)
                    ? $"✗ {InjectorHelper.GetErrorMessage(exitCode, shadeName)}"
                    : $"✗ {shadeName} 注入器没就绪（exit {exitCode}）");
                try { injectorProcess?.Kill(); }
                catch { }
                return null;
            }

            report?.Invoke("注入器就绪，启动游戏…");
            process = await service.StartGameAsync(gameId, installPath);
            if (process is null)
            {
                try { injectorProcess?.Kill(); }
                catch { }
                return null;
            }
        }
        else
        {
            process = await service.StartGameAsync(gameId);
            if (process is null)
            {
                report?.Invoke("✗ 游戏进程没起来（看日志）");
                return null;
            }
        }

        report?.Invoke($"游戏已启动（PID {process.Id}）");

        // ④ 模块 / OptiScaler 注入
        List<InjectSpec> specs = BuildSpecs(gameId, useOptiScaler);
        string processName = ResolveProcessName(service, gameId, entry);

        if (specs.Count > 0 && !string.IsNullOrWhiteSpace(processName))
        {
            if (blockForInjection)
            {
                await InjectLoopAsync(processName, specs, TimeSpan.FromMinutes(5), report, ct);
            }
            else
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await InjectLoopAsync(processName, specs, TimeSpan.FromMinutes(20), null, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Background extra DLL injection");
                    }
                });
            }
        }

        // ⑤ 帧率解锁（fire-and-forget：等游戏稳了再 patch，CLI 不需要等它）
        if (AppConfig.GetUseFpsUnlockLaunchOption(gameId))
        {
            _ = Task.Run(() => FpsUnlockAsync(gameId, service, ct));
        }

        return process;
    }

    // ==================== ① 强制 hook off（页面 ApplyForceHookOffOnLaunch 的移植）====================

    private static void ApplyForceHookOff(GameId gameId, GameEntry? entry)
    {
        try
        {
            if (entry is null || !AppConfig.GetForceHookOffOnLaunch(gameId.GameBiz))
            {
                return;
            }

            var service = GamePluginServiceFactory.Create(entry, PluginHostLocator.Resolve(out _));
            if (service.HasReShadeIni && service.SetHookPoint(0))
            {
                _logger.LogInformation("Force hook point off before launch ({Game})", gameId.GameBiz);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Force hook point off before launch");
        }
    }

    // ==================== ①' 覆盖包 runOnLaunch 动作（页面 RunPackAutoActionsOnLaunchAsync 的 headless 版）====================

    private static async Task RunPackAutoActionsOnLaunchAsync(
        GameId gameId, GameEntry? entry, Action<string>? report, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested();
            string packRoot = GameAddonPack.PackDirectory(AppConfig.CacheRoot, gameId.GameBiz.Value);

            // 与页面一致：没同意过（或 auto.json 变了还没重新确认）一律不跑；被单独禁用的跳过
            string consentHash = PackActionConsent.ComputeHash(packRoot);
            if (!PackActionConsent.IsConsented(gameId.GameBiz.Value, consentHash))
            {
                return;
            }

            HashSet<string> disabled = PackActionConsent.DisabledActions(gameId.GameBiz.Value, consentHash);
            List<PackAutoAction> actions = [.. PackAutoActionFile.Load(packRoot)
                .Where(a => a.RunOnLaunch && !disabled.Contains(a.Name))];
            if (actions.Count == 0)
            {
                return;
            }

            ShadeHost? host = PluginHostLocator.Resolve(out _);
            GamePluginService? plugins = null;
            if (entry is not null)
            {
                try
                {
                    plugins = GamePluginServiceFactory.Create(entry, host);
                }
                catch
                {
                    // 读不出插件状态就只跑不依赖 ini 的步骤
                }
            }

            var context = new LauncherActionContext
            {
                GameId = gameId,
                GameBiz = gameId.GameBiz,
                Entry = entry,
                Host = host,
                Plugins = plugins,
                LauncherPage = null,
                PackRoot = packRoot,
                XamlRoot = null,
                Interactive = false,
                Report = report ?? (text => _logger.LogInformation("{Text}", text)),
            };

            foreach (PackAutoAction action in actions)
            {
                ct.ThrowIfCancellationRequested();
                string summary = await LauncherActionRunner.RunAsync(action, context);
                _logger.LogInformation("runOnLaunch「{Name}」：{Summary}", action.Name, summary);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run pack auto actions on launch (pipeline)");
        }
    }

    // ==================== ② 游戏目录 dlssg 换 310.9（页面 EnsureGameDlssgForMfg 的移植）====================

    private static void EnsureGameDlssgForMfg(GameId gameId, GameEntry? entry, bool useOptiScaler)
    {
        try
        {
            if (!useOptiScaler)
            {
                return;
            }

            string? optiDll = AppConfig.GetSelectedOptiScalerDll(gameId);
            if (string.IsNullOrWhiteSpace(optiDll) || Path.GetDirectoryName(optiDll) is not { Length: > 0 } buildDirectory)
            {
                return;
            }

            string? unlockDll = OptiScalerRuntime.FindUnlockDlssg(buildDirectory);
            if (unlockDll is null)
            {
                return;
            }

            string? gameDirectory = entry?.ExePath is { Length: > 0 } exe
                ? Path.GetDirectoryName(exe)
                : GameLauncherService.GetGameInstallPath(gameId);
            if (string.IsNullOrWhiteSpace(gameDirectory) || !Directory.Exists(gameDirectory))
            {
                return;
            }

            string backupRoot = string.IsNullOrWhiteSpace(AppConfig.UserDataFolder)
                ? Path.Combine(Path.GetTempPath(), "HoYoShadeHub-game-dll-backup")
                : Path.Combine(AppConfig.UserDataFolder, ".hysx", "game-dll-backup");

            OptiScalerRuntime.GameDlssgSwapResult swap =
                OptiScalerRuntime.ReplaceGameDlssg(gameDirectory, unlockDll, backupRoot);
            if (swap.Replaced > 0)
            {
                _logger.LogInformation("Game dlssg replaced: {Message}", swap.Message);
            }
            else if (!swap.Ok)
            {
                _logger.LogWarning("Game dlssg replace failed: {Message}", swap.Message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ensure game dlssg for MFG");
        }
    }

    // ==================== ③ ini 路径对齐（页面 AlignGameIniPaths 的无 UI 版）====================

    private static void AlignGameIniPaths(GameId gameId, GameEntry? entry, string shadePath)
    {
        try
        {
            if (entry?.ReShadeIniPath is not { } gameIni || !File.Exists(gameIni))
            {
                return;
            }

            ShadeHost? host = ShadeHostLocator.FromShadeRoot(shadePath);
            if (host is null)
            {
                return;
            }

            // 先拼「每游戏插件包」，再对齐 —— 顺序和页面一致（Align 会认出 pack.json 并跳过它）
            GameAddonPackService.Sync(gameId, entry, host);

            ShadePathAlignResult align = ShadePathAligner.Align(gameIni, host);
            if (align.Changed)
            {
                _logger.LogInformation("ReShade.ini paths re-pointed from {Old} to {New}: {Keys}",
                    align.PreviousRoot, host.RootPath, string.Join(", ", align.ChangedKeys));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Align ReShade.ini paths");
        }
    }

    private static string? ValidateShade(string shadePath, string shadeName)
    {
        if (!Directory.Exists(shadePath))
        {
            return $"{shadeName} 没有安装（{shadePath} 不存在）";
        }

        if (!File.Exists(Path.Combine(shadePath, "inject.exe")))
        {
            return $"{shadeName} 里找不到 inject.exe";
        }

        return null;
    }

    // ==================== ④ 模块 / OptiScaler 注入（页面 StartExtraDllInjection 的移植）====================

    private static List<InjectSpec> BuildSpecs(GameId gameId, bool useOptiScaler)
    {
        var specs = new List<InjectSpec>();

        // ① 启动选项里勾的「启用模块」
        if (AppConfig.GetUseModulesLaunchOption(gameId))
        {
            foreach ((string key, string name, string path) in ModuleRegistry.ResolveInjectionDlls(gameId))
            {
                if (specs.All(s => !string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase)))
                {
                    bool waitReady = string.Equals(
                        Path.GetFileName(path),
                        OptiScalerRuntime.FsrBridgeDllName,
                        StringComparison.OrdinalIgnoreCase);
                    int delay = AppConfig.GetModuleInjectDelayEffective(key, gameId.GameBiz);
                    specs.Add(new InjectSpec(path, name, WaitForReady: waitReady, DelaySeconds: delay));
                }
            }
        }

        // ①' 桥在场但这次不用 OptiScaler：撤走 autoload 清单（否则桥会把 OptiScaler 又拉回去）
        InjectSpec? bridgeSpec = specs.FirstOrDefault(s =>
            string.Equals(Path.GetFileName(s.Path), OptiScalerRuntime.FsrBridgeDllName, StringComparison.OrdinalIgnoreCase));
        bool optiScalerWanted = useOptiScaler
                                && !string.IsNullOrWhiteSpace(AppConfig.GetSelectedOptiScalerDll(gameId));
        if (bridgeSpec is not null
            && !optiScalerWanted
            && OptiScalerRuntime.RemoveFsrBridgeAutoload(Path.GetDirectoryName(bridgeSpec.Path), out _))
        {
            _logger.LogInformation("已撤走 FSR Bridge 的 OptiScaler autoload 清单（本次没启用 OptiScaler）");
        }

        // ② OptiScaler：勾了「启用OptiScaler」+ 这个游戏选过构建
        if (optiScalerWanted)
        {
            string? optiScaler = AppConfig.GetSelectedOptiScalerDll(gameId);
            optiScaler = EnsureNamedOptiScalerDll(optiScaler, AppConfig.GetOptiScalerDllName(gameId));

            if (!string.IsNullOrWhiteSpace(optiScaler) && File.Exists(optiScaler)
                && specs.All(s => !string.Equals(s.Path, optiScaler, StringComparison.OrdinalIgnoreCase))
                && Path.GetDirectoryName(optiScaler) is { Length: > 0 } buildDirectory)
            {
                string gameKey = gameId.GameBiz.ToString();

                OptiScalerProfiles.Activate(buildDirectory, gameKey);
                OptiScalerRuntime.EnsureConfigDllPath(buildDirectory);
                string? dlssg = OptiScalerRuntime.EnsureDlssgForUnlock(buildDirectory,
                [
                    Path.Combine(buildDirectory, "OptiScaler", OptiScalerRuntime.StreamlineFolderName),
                    buildDirectory,
                    Path.Combine(buildDirectory, "OptiScaler"),
                ]);
                if (dlssg is not null)
                {
                    _logger.LogInformation("OptiScaler dlssg for MFG unlock: {Dlssg}", dlssg);
                }

                // 原神 mhyprot 拒绝外部注入：有桥就让桥在进程内 LoadLibrary（写 sidecar 清单）
                if (bridgeSpec is not null)
                {
                    string? bridgeDirectory = Path.GetDirectoryName(bridgeSpec.Path);
                    if (bridgeDirectory is not null
                        && OptiScalerRuntime.WriteFsrBridgeAutoload(bridgeDirectory, optiScaler) is not null)
                    {
                        _logger.LogInformation("OptiScaler 将由 FSR Bridge 进程内加载（绕过 mhyprot）：{Target}", optiScaler);
                    }
                    else
                    {
                        specs.Add(new InjectSpec(optiScaler, "OptiScaler",
                            BuildDirectory: buildDirectory, GameKey: gameKey,
                            DelaySeconds: AppConfig.GetOptiScalerInjectDelayEffective(gameId.GameBiz)));
                    }
                }
                else
                {
                    specs.Add(new InjectSpec(optiScaler, "OptiScaler",
                        BuildDirectory: buildDirectory, GameKey: gameKey,
                        DelaySeconds: AppConfig.GetOptiScalerInjectDelayEffective(gameId.GameBiz)));
                }
            }
        }

        // 桥的 ini 必须和它被注入的那个 DLL 同目录
        foreach (InjectSpec spec in specs)
        {
            if (string.Equals(Path.GetFileName(spec.Path), OptiScalerRuntime.FsrBridgeDllName, StringComparison.OrdinalIgnoreCase))
            {
                OptiScalerRuntime.EnsureFsrBridgeIni(Path.GetDirectoryName(spec.Path) ?? string.Empty);
            }
        }

        return specs;
    }

    private static string EnsureNamedOptiScalerDll(string? dllPath, string? dllName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dllPath) || string.IsNullOrWhiteSpace(dllName))
            {
                return dllPath ?? string.Empty;
            }

            string name = dllName.Trim();
            if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                name += ".dll";
            }

            if (string.Equals(Path.GetFileName(dllPath), name, StringComparison.OrdinalIgnoreCase))
            {
                return dllPath;
            }

            string directory = Path.GetDirectoryName(dllPath) ?? string.Empty;
            if (directory.Length == 0 || !File.Exists(dllPath))
            {
                return dllPath;
            }

            string target = Path.Combine(directory, name);
            File.Copy(dllPath, target, overwrite: true);
            return File.Exists(target) ? target : dllPath;
        }
        catch
        {
            return dllPath ?? string.Empty;
        }
    }

    /// <summary>等进程 → 按「注入时机」预热 → VirtualAllocEx 注入 → 桥就绪再注后面的。</summary>
    private static async Task InjectLoopAsync(
        string processName,
        IReadOnlyList<InjectSpec> specs,
        TimeSpan budget,
        Action<string>? report,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        TimeSpan survivalCheck = TimeSpan.FromSeconds(8);
        DateTime deadline = DateTime.UtcNow + budget;
        var injectedPids = new HashSet<int>();

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                report?.Invoke($"✗ {budget.TotalMinutes:F0} 分钟内没等到 {processName} 进程，注入放弃");
                return;
            }

            Process? target = await DllInjector.WaitForProcessAsync(processName, remaining, ct, injectedPids);
            if (target is null)
            {
                report?.Invoke($"✗ 没等到 {processName} 进程");
                return;
            }

            int pid = target.Id;
            injectedPids.Add(pid);
            report?.Invoke($"等 {processName}（pid {pid}）预热…");

            bool warmupLost = false;
            foreach (InjectSpec spec in specs)
            {
                // 预热：按配置的「注入时机」等进程稳一稳（进程中途退了 → 换新 pid 重试）
                DateTime steadyUntil = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Max(0, spec.DelaySeconds));
                while (DateTime.UtcNow < steadyUntil)
                {
                    if (!DllInjector.IsProcessAlive(pid))
                    {
                        warmupLost = true;
                        break;
                    }

                    await Task.Delay(250, ct);
                }

                if (warmupLost)
                {
                    break;
                }

                bool ok = DllInjector.Inject(pid, spec.Path, out string error);
                report?.Invoke(ok
                    ? $"已注入 {spec.Label} → {processName}（pid {pid}）"
                    : $"✗ {spec.Label} 注入失败（pid {pid}）：{error}");

                // OptiScaler 依赖桥先把垫片装好：等桥日志出现本次进程的 active 行
                if (spec.WaitForReady && ok)
                {
                    bool ready = await WaitForFsrBridgeReadyAsync(
                        Path.GetDirectoryName(spec.Path) ?? string.Empty, pid,
                        TimeSpan.FromSeconds(30), ct);
                    if (!ready)
                    {
                        _logger.LogWarning("FsrBridge readiness marker not seen (pid {Pid}); continuing", pid);
                    }
                }
            }

            if (warmupLost)
            {
                report?.Invoke($"{processName}（pid {pid}）预热中就退了，等新进程重试（{attempt}/{maxAttempts}）");
                continue;
            }

            await Task.Delay(survivalCheck, ct);
            if (DllInjector.IsProcessAlive(pid))
            {
                HookInjectedTarget(target, processName, pid, specs);
                return;
            }

            report?.Invoke($"{processName}（pid {pid}）注入后立刻退出，等新进程重试（{attempt}/{maxAttempts}）");
        }

        report?.Invoke($"✗ {processName} 连续 {maxAttempts} 次都是壳进程 / 中途退出，注入放弃");
    }

    /// <summary>桥就绪标记：日志里出现 <c>Dx11FsrBridge active pid=&lt;pid&gt;</c>（或 hook 就绪行）。</summary>
    private static async Task<bool> WaitForFsrBridgeReadyAsync(
        string bridgeDirectory,
        int processId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        string logPath = Path.Combine(bridgeDirectory, "Dx11FsrBridge.log");
        string marker = "Dx11FsrBridge active pid=" + processId;
        string[] fallbackMarkers = ["draw_indexed_hook_active", "draw_hook_active", "hook_ready mode=feature_query"];
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (!DllInjector.IsProcessAlive(processId))
            {
                return false;
            }

            try
            {
                if (File.Exists(logPath))
                {
                    using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    string text = reader.ReadToEnd();
                    if (text.Contains(marker, StringComparison.Ordinal)
                        || fallbackMarkers.Any(m => text.Contains(m, StringComparison.Ordinal)))
                    {
                        await Task.Delay(500, ct);
                        return true;
                    }
                }
            }
            catch
            {
                // 桥正在写日志，下一轮再读
            }

            await Task.Delay(200, ct);
        }

        return false;
    }

    /// <summary>注稳了：游戏退出时把 OptiScaler 主 ini 回写 profile（页面 HookInjectedTarget 的移植）。</summary>
    private static void HookInjectedTarget(Process target, string processName, int pid, IReadOnlyList<InjectSpec> specs)
    {
        try
        {
            target.EnableRaisingEvents = true;
            target.Exited += (_, _) =>
            {
                _logger.LogInformation("Injected game exited: {Process} (pid {Pid})", processName, pid);
                foreach (InjectSpec spec in specs)
                {
                    if (spec.BuildDirectory is not null && spec.GameKey is not null
                        && OptiScalerProfiles.Store(spec.BuildDirectory, spec.GameKey))
                    {
                        string? followed = OptiScalerPresets.Follow(spec.BuildDirectory, spec.GameKey);
                        if (followed is not null)
                        {
                            _logger.LogInformation("OptiScaler config synced back: {Config}", followed);
                        }
                    }
                }
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Hook injected game exit");
        }
    }

    // ==================== ⑤ 帧率解锁（页面 StartFpsUnlockAsync 的移植）====================

    private static async Task FpsUnlockAsync(GameId gameId, GameLauncherService service, CancellationToken ct)
    {
        try
        {
            Version? gameVersion = await service.GetLocalGameVersionAsync(gameId);
            string versionText = gameVersion?.ToString() ?? string.Empty;
            string? syncedVersion = AppConfig.GetFpsUnlockDataVersion(gameId);
            bool needFetch = !FpsUnlockDataService.HasLocalData()
                             || !string.Equals(syncedVersion, versionText, StringComparison.Ordinal);
            if (!needFetch)
            {
                DateTime lastCheck = new(AppConfig.GetFpsUnlockLastCheckTicks(gameId), DateTimeKind.Utc);
                needFetch = DateTime.UtcNow - lastCheck >= TimeSpan.FromHours(24);
            }

            if (needFetch)
            {
                AppConfig.SetFpsUnlockLastCheckTicks(gameId, DateTime.UtcNow.Ticks);
                try
                {
                    var result = await FpsUnlockDataService.UpdateAsync(ct);
                    if (result is FpsUnlockDataService.UpdateResult.Updated or FpsUnlockDataService.UpdateResult.Unchanged)
                    {
                        AppConfig.SetFpsUnlockDataVersion(gameId, versionText);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "FPS unlock: sync upstream data failed, use local/fallback");
                }
            }

            byte[]? shellcode;
            (byte[] bytes, bool[] mask)? pattern;
            try
            {
                shellcode = FpsUnlockDataService.LoadShellcode();
                pattern = FpsUnlockDataService.LoadPattern();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "FPS unlock: load shellcode/pattern failed, use fallback");
                shellcode = null;
                pattern = null;
            }

            if (shellcode is null || pattern is null)
            {
                shellcode ??= FpsUnlocker.FallbackShellcode;
                pattern ??= (
                    [0x8B, 0x0D, 0x00, 0x00, 0x00, 0x00, 0xEB, 0x00, 0x33, 0xC0],
                    [true, true, false, false, false, false, true, false, true, true]);
            }

            Process? game = await service.GetGameProcessAsync(gameId, TimeSpan.FromMinutes(2));
            if (game is null)
            {
                _logger.LogWarning("FPS unlock: game process not found, skipped");
                return;
            }

            using var unlocker = new FpsUnlocker(shellcode, pattern.Value);
            bool ok = await unlocker.AttachAsync(game, AppConfig.GetFpsUnlockTarget(gameId), TimeSpan.FromSeconds(60));
            if (ok)
            {
                _logger.LogInformation("FPS unlock armed: {Target} fps (pid {Pid})",
                    AppConfig.GetFpsUnlockTarget(gameId), game.Id);
            }
            else
            {
                _logger.LogWarning("FPS unlock failed: {Detail}", unlocker.LastError);
                if (unlocker.PatternNotFound)
                {
                    // 特征对不上 = 数据滞后于游戏版本；清戳，下次启动重新拉上游
                    AppConfig.SetFpsUnlockDataVersion(gameId, null);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FPS unlock");
        }
    }

    // ==================== 杂项 ====================

    private static string ResolveProcessName(GameLauncherService service, GameId gameId, GameEntry? entry)
    {
        try
        {
            if (entry?.ExePath is { Length: > 0 } exe)
            {
                return Path.GetFileNameWithoutExtension(exe);
            }

            string name = service.GetGameExeNameAsync(gameId).GetAwaiter().GetResult();
            return Path.GetFileNameWithoutExtension(name);
        }
        catch
        {
            return string.Empty;
        }
    }
}
