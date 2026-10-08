using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Diagnostics;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.Modules;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>
/// 每启动一局游戏，就把这一局的诊断日志（Bridge / OptiScaler / ReShade / Hub 自己）
/// 快照到 <c>&lt;LogFolder&gt;\sessions\&lt;yyyyMMdd-HHmmss&gt;_&lt;进程名&gt;_&lt;pid&gt;\</c>，
/// 连同源路径、大小、修改时间和模块版本写进 <c>session.txt</c>。
///
/// <para>
/// 为什么需要：Bridge、ReShade、OptiScaler **三家都是每次启动重写自己的日志**
/// （OptiScaler 是 <c>Logger.cpp</c> 里 basic_file_sink 的 truncate 参数写死 true；
/// Bridge 是 ini 的 <c>truncate_on_start=1</c>）。客户手动收集日志时几乎必然把不同几次运行
/// 的文件混着发过来 —— 实测两起工单都是「发来的 OptiScaler.log 是上一次运行的」，
/// 于是没法判断 opt 到底有没有初始化。自动快照保证「一个文件夹 = 一局」，且带 pid。
/// </para>
///
/// <para>
/// 快照时机：启动瞬间（只记各日志文件的状态，抄配置）；启动后 45 秒；之后每 5 分钟；进程退出。
/// 定时那一档是为了「进程被强杀 / 卡死 / Hub 先关掉」也能留下 5 分钟内的现场。
/// 所有异常都吞掉并只写 Hub 日志，绝不干扰启动与注入。
/// </para>
/// </summary>
internal sealed class GameSessionLogCollector
{
    private static readonly ILogger _logger = AppConfig.GetLogger<GameSessionLogCollector>();
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<int, GameSessionLogCollector> Active = new();

    /// <summary>会话目录名形状：<c>yyyyMMdd-HHmmss_进程名_pid</c>。清理旧会话时只认这个形状。</summary>
    private static readonly Regex SessionFolderPattern = new(@"^\d{8}-\d{6}_.+_\d+$", RegexOptions.Compiled);

    /// <summary>保留最近多少局（含正在建的这一局）。超出的按名字（=时间）倒序删掉。</summary>
    private const int KeepSessions = 15;

    /// <summary>单个文件最多抄多少字节；超了只抄末尾这一段（日志看尾部最有价值）。</summary>
    private const long MaxCopyBytes = 16L * 1024 * 1024;

    /// <summary>所有会话加起来最多占多少磁盘；超了继续从最旧的删。</summary>
    private const long MaxTotalBytes = 256L * 1024 * 1024;

    private const string ManifestName = "session.txt";

    /// <summary>AI 摘要文件名：把本局几 MB 的日志压成几 KB，专门给人/模型快速读。</summary>
    private const string DigestName = "digest.txt";

    /// <summary>配置的紧凑视图（去注释/空行/连续重复），原文仍在同目录 <c>cfg-*</c>。</summary>
    private const string ConfigCompactName = "config-compact.txt";

    private static readonly TimeSpan FirstSnapshotDelay = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromMinutes(5);

    private readonly object _writeGate = new();
    private readonly List<SnapshotSource> _logs;
    private readonly List<SnapshotSource> _configs;
    private readonly List<string> _bridgeDirectories;
    private readonly List<string> _optiScalerDirectories;

    /// <summary>本局开始时刻，快照行里的相对秒数以它为原点。</summary>
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    /// <summary>紧凑清单写入器（v2，见 SessionLogManifest）。</summary>
    private readonly SessionLogManifest _manifest;

    /// <summary>清单里的文件目录（id 稳定，后续快照只写变化项）。</summary>
    private readonly List<SessionLogManifest.Entry> _entries;

    private readonly int _pid;
    private readonly string _processName;
    private readonly string _exePath;
    private readonly string? _commandLine;
    private readonly string? _gameKey;

    private Process? _targetProcess;
    private Timer? _timer;
    private readonly bool _manual;
    private volatile bool _finished;

    private GameSessionLogCollector(
        GameId? gameId, int pid, string processName, string? exePath, string? commandLine, Process? process,
        bool manual = false)
    {
        _pid = pid;
        _manual = manual;
        _processName = string.IsNullOrWhiteSpace(processName) ? $"pid{pid}" : processName.Trim();
        _commandLine = commandLine;
        _gameKey = gameId?.GameBiz.Value;

        // 进程真实路径优先（启动器里的 exe 名可能只是 "YuanShen"）；取不到再退回安装目录拼。
        string? resolvedExe = exePath;
        if (string.IsNullOrWhiteSpace(resolvedExe) && pid > 0)
        {
            try
            {
                resolvedExe = (process ?? Process.GetProcessById(pid)).MainModule?.FileName;
            }
            catch
            {
                // 权限 / 位数不同：退回安装目录猜想
            }
        }

        if (string.IsNullOrWhiteSpace(resolvedExe) && gameId is not null)
        {
            string? install = AppConfig.GetGameInstallPath(gameId.GameBiz);
            if (!string.IsNullOrWhiteSpace(install))
            {
                string name = _processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? _processName
                    : _processName + ".exe";
                resolvedExe = Path.Combine(install, name);
            }
        }

        _exePath = resolvedExe ?? "(未知)";
        string? gameDirectory = string.IsNullOrWhiteSpace(resolvedExe) ? null : Path.GetDirectoryName(resolvedExe);

        // 先清旧局再建本局：这里最多留 KeepSessions - 1 个旧目录，加上本局正好 KeepSessions
        PruneOldSessions(KeepSessions - 1);

        SessionDirectory = UniqueSessionDirectory();
        Directory.CreateDirectory(SessionDirectory);

        (_logs, _configs, _bridgeDirectories, _optiScalerDirectories) = Discover(gameId, gameDirectory);

        _manifest = new SessionLogManifest(SessionDirectory, ManifestName);
        _entries = BuildEntries();

        WriteHeader();
        TakeSnapshot(manual ? "手动导出" : "启动", includeLogs: false);

        if (!manual)
        {
            HookProcessExit(process);
            _timer = new Timer(_ => OnTimerTick(), null, FirstSnapshotDelay, SnapshotInterval);
        }

        _logger.LogInformation(
            "本局日志快照目录：{Directory}（桥目录 {BridgeCount} 个，OptiScaler 目录 {OptiCount} 个，日志文件 {LogCount} 个）",
            SessionDirectory, _bridgeDirectories.Count, _optiScalerDirectories.Count, _logs.Count);
    }

    /// <summary>本会话的快照目录。</summary>
    public string SessionDirectory { get; }

    /// <summary>所有会话快照的根目录。</summary>
    public static string SessionsRoot => Path.Combine(AppConfig.LogFolder, "sessions");

    /// <summary>
    /// 开始给一个游戏进程做日志快照。同一个 pid 重复调用只会建一个会话（注入路径有多处入口）。
    /// 任何失败都只是记日志，不抛。
    /// </summary>
    public static GameSessionLogCollector? Begin(
        GameId? gameId, int pid, string processName, string? exePath = null, string? commandLine = null, Process? process = null)
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            lock (Gate)
            {
                if (Active.TryGetValue(pid, out GameSessionLogCollector? existing) && !existing._finished)
                {
                    return existing;
                }

                var session = new GameSessionLogCollector(gameId, pid, processName, exePath, commandLine, process);
                Active[pid] = session;
                return session;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "建立会话日志快照失败（pid {Pid}）", pid);
            return null;
        }
    }

    /// <summary>
    /// 立刻给「当前这一局」拍一张快照并收尾 —— 游戏是官方启动器起的、Hub 没参与时用得上
    /// （CLI：<c>collectlogs --biz hk4e_cn</c>）。返回快照目录，失败返回 null。
    /// </summary>
    public static string? CaptureNow(
        GameId? gameId, int pid, string processName, string? exePath = null, string? commandLine = null, Process? process = null)
    {
        try
        {
            var session = new GameSessionLogCollector(
                gameId, pid, processName, exePath, commandLine, process, manual: true);
            session.Finish("手动导出");
            return session.SessionDirectory;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "手动导出会话日志失败");
            return null;
        }
    }

    /// <summary>收尾：最后抄一次（进程退出那一刻的日志才算完整），停掉定时器并释放会话槽。</summary>
    public void Finish(string reason, int? exitCode = null)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;

        try
        {
            _timer?.Dispose();
        }
        catch
        {
            // 退出路径上定时器可能已经在跑，忽略
        }

        try
        {
            string suffix = exitCode is null ? string.Empty : $"（退出码 {exitCode}）";
            TakeSnapshot($"收尾：{reason}{suffix}", includeLogs: true);
            WriteDigest(exitCode);
            _logger.LogInformation("本局日志快照已完成：{Directory}（{Reason}）", SessionDirectory, reason);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "会话日志收尾快照失败");
        }
        finally
        {
            Active.TryRemove(_pid, out _);
        }
    }

    public void Dispose() => Finish("释放");

    /// <summary>
    /// 收尾时把本局的日志压成一份给 AI 读的摘要（<c>digest.txt</c>）。
    /// </summary>
    /// <remarks>
    /// 一局游戏四个日志合计约 7.5 MB（≈200 万 token），里面 99.9% 是逐帧重复行；
    /// 摘要只保留错误/警告的形状 + 计数 + 末尾原文，实测 3.6 MB 的桥日志压到 1～4 KB。
    /// 任何失败都只记 Hub 日志，绝不影响游戏退出。
    /// </remarks>
    private void WriteDigest(int? exitCode)
    {
        try
        {
            string banner = $"sess={Path.GetFileName(SessionDirectory)} game={(_gameKey is { Length: > 0 } ? _gameKey : "?")} " +
                            $"hub={AppConfig.AppVersion} pid={_pid} exit={(exitCode?.ToString() ?? "?")}";

            var sources = new List<LogDigest.Source>(_logs.Count);
            foreach (SnapshotSource source in _logs)
            {
                string copied = Path.Combine(SessionDirectory, source.Name);
                sources.Add(new LogDigest.Source(source.Name, File.Exists(copied) ? copied : source.Path));
            }

            var summary = new System.Text.StringBuilder(160);
            if (sources.Count > 0)
            {
                LogDigest.DocumentResult doc = LogDigest.BuildDocument(banner, sources);
                File.WriteAllText(
                    Path.Combine(SessionDirectory, DigestName),
                    doc.Text,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                summary.Append($"digest={DigestName} in={doc.InputBytes} out={doc.OutputBytes} E={doc.Errors} W={doc.Warnings}");
                _logger.LogInformation(
                    "本局日志已压成摘要：{File}（{Input} → {Output} 字节，E={Errors} W={Warnings}）",
                    Path.Combine(SessionDirectory, DigestName), doc.InputBytes, doc.OutputBytes, doc.Errors, doc.Warnings);
            }

            // 配置原文里注释能占七成（OptiScaler.ini 60 KB → 14 KB），但 AI 要的是键值，
            // 所以另给一份去注释的紧凑视图；原文照旧留着给人工核对。
            var configs = new List<LogDigest.Source>(_configs.Count);
            foreach (SnapshotSource source in _configs)
            {
                string copied = Path.Combine(SessionDirectory, source.Name);
                configs.Add(new LogDigest.Source(source.Name, File.Exists(copied) ? copied : source.Path));
            }

            if (configs.Count > 0)
            {
                LogDigest.ConfigDocumentResult cfg = LogDigest.BuildConfigs(banner, configs);
                File.WriteAllText(
                    Path.Combine(SessionDirectory, ConfigCompactName),
                    cfg.Text,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

                if (summary.Length > 0)
                {
                    summary.Append(' ');
                }

                summary.Append($"cfgcompact={ConfigCompactName} in={cfg.InputBytes} out={cfg.OutputBytes}");
                _logger.LogInformation(
                    "本局配置已压成紧凑视图：{File}（{Input} → {Output} 字节）",
                    Path.Combine(SessionDirectory, ConfigCompactName), cfg.InputBytes, cfg.OutputBytes);
            }

            if (summary.Length == 0)
            {
                return;
            }

            lock (_writeGate)
            {
                _manifest.AppendSection("AI 摘要", summary.ToString());
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "写本局日志摘要失败");
        }
    }

    /// <summary>立刻抄一次（定时器 / 退出 / 手动导出都用它）。</summary>
    public void SnapshotNow(string reason) => TakeSnapshot(reason, includeLogs: true);

    private void OnTimerTick()
    {
        if (_finished)
        {
            return;
        }

        try
        {
            if (!DllInjector.IsProcessAlive(_pid))
            {
                // Exited 事件偶尔收不到（句柄被提前释放 / 事件没挂上），这里兜住
                Finish("定时快照发现进程已退出");
                return;
            }

            TakeSnapshot("定时快照", includeLogs: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "会话定时快照失败");
        }
    }

    private void HookProcessExit(Process? process)
    {
        try
        {
            _targetProcess = process ?? Process.GetProcessById(_pid);
            Process captured = _targetProcess;
            captured.EnableRaisingEvents = true;
            captured.Exited += (_, _) =>
            {
                int? code = null;
                try
                {
                    code = captured.ExitCode;
                }
                catch
                {
                    // 进程对象已释放 / 拿不到退出码：不影响快照
                }

                _ = Task.Run(() => Finish("游戏进程退出", code));
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "挂会话退出快照失败（pid {Pid}），改用定时快照兜底", _pid);
        }
    }

    // ==================== 快照本体 ====================

    private void TakeSnapshot(string reason, bool includeLogs)
    {
        try
        {
            if (includeLogs)
            {
                foreach (SnapshotSource source in _logs)
                {
                    TryCopyOne(source);
                }
            }

            foreach (SnapshotSource source in _configs)
            {
                TryCopyOne(source);
            }

            var observations = new List<SessionLogManifest.Observation>(_entries.Count);
            foreach (SessionLogManifest.Entry entry in _entries)
            {
                try
                {
                    var info = new FileInfo(entry.SourcePath);
                    observations.Add(new SessionLogManifest.Observation(entry.Id, info.Exists, info.Exists ? info.Length : 0));
                }
                catch
                {
                    observations.Add(new SessionLogManifest.Observation(entry.Id, false, 0));
                }
            }

            lock (_writeGate)
            {
                _manifest.AppendSnapshot(reason, DateTimeOffset.Now - _startedAt, observations);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "写会话快照失败：{Directory}", SessionDirectory);
        }
    }

    /// <summary>把一个文件抄进本局目录；只记 Hub 日志，永远不抛。</summary>
    private void TryCopyOne(SnapshotSource source)
    {
        try
        {
            var info = new FileInfo(source.Path);
            if (!info.Exists)
            {
                // 缺失会由清单里的 miss 标记体现，这里不用重复写一行
                return;
            }

            CopyWithRetry(info, Path.Combine(SessionDirectory, source.Name));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "抄日志失败：{Path}", source.Path);
        }
    }

    private static (long Written, bool Tail) CopyWithRetry(FileInfo source, string target)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var input = new FileStream(
                    source.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

                long length = input.Length;
                bool tail = length > MaxCopyBytes;
                if (tail)
                {
                    input.Seek(-MaxCopyBytes, SeekOrigin.End);
                    SkipPartialLine(input);
                }

                using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.Read);
                input.CopyTo(output, 1 << 17);
                return (tail ? MaxCopyBytes : length, tail);
            }
            catch (Exception ex)
            {
                // 正在写入的文件偶尔会锁一瞬间，等一下再抄
                last = ex;
                Thread.Sleep(200);
            }
        }

        throw last!;
    }

    private static void SkipPartialLine(FileStream stream)
    {
        int b;
        while ((b = stream.ReadByte()) >= 0 && b != '\n')
        {
            // 丢掉被切断的半行，让快照从整行开始
        }
    }

    /// <summary>文件目录：id 一旦定下就不变，后面的快照只按 id 写变化。</summary>
    private List<SessionLogManifest.Entry> BuildEntries()
    {
        var entries = new List<SessionLogManifest.Entry>(_logs.Count + _configs.Count);
        int index = 1;
        foreach (SnapshotSource source in _logs)
        {
            entries.Add(new SessionLogManifest.Entry(index.ToString("D2"), source.Name, source.Path, 'L'));
            index++;
        }

        foreach (SnapshotSource source in _configs)
        {
            entries.Add(new SessionLogManifest.Entry(index.ToString("D2"), source.Name, source.Path, 'C'));
            index++;
        }

        return entries;
    }

    private List<SessionLogManifest.Module> CollectModules()
    {
        var modules = new List<SessionLogManifest.Module>(_bridgeDirectories.Count + _optiScalerDirectories.Count);
        foreach (string directory in _bridgeDirectories)
        {
            // 1.4.3.1 的下载器可能把桥改名成了 OptiScaler.dll：按名字硬找会把装了的那份报成「缺失」
            modules.Add(ReadModule("Bridge",
                ModuleRegistry.FindGenshinFsrBridgeDll(directory)
                ?? Path.Combine(directory, OptiScalerRuntime.FsrBridgeDllName)));
        }

        foreach (string directory in _optiScalerDirectories)
        {
            modules.Add(ReadModule("OptiScaler", Path.Combine(directory, "OptiScaler.dll")));
        }

        return modules;
    }

    private static SessionLogManifest.Module ReadModule(string label, string dllPath)
    {
        try
        {
            if (!File.Exists(dllPath))
            {
                return new SessionLogManifest.Module(label, null, dllPath);
            }

            var info = FileVersionInfo.GetVersionInfo(dllPath);
            string version = string.IsNullOrWhiteSpace(info.FileVersion) ? info.ProductVersion ?? "?" : info.FileVersion;
            return new SessionLogManifest.Module(label, version, dllPath);
        }
        catch
        {
            return new SessionLogManifest.Module(label, "?", dllPath);
        }
    }

    // ==================== 清单头 / 版本 ====================

    private void WriteHeader()
    {
        var header = new SessionLogManifest.Header(
            _startedAt,
            _pid,
            _processName,
            _commandLine,
            _gameKey,
            AppConfig.AppVersion,
            AppConfig.IsPortable,
            AppConfig.UserDataFolder,
            AppConfig.LogFolder,
            SessionDirectory);

        _manifest.WriteHeader(header, CollectModules(), _entries);
    }

    // ==================== 目录 / 文件发现 ====================

    private (List<SnapshotSource> Logs, List<SnapshotSource> Configs, List<string> BridgeDirectories, List<string> OptiDirectories)
        Discover(GameId? gameId, string? gameDirectory)
    {
        var logs = new List<SnapshotSource>();
        var configs = new List<SnapshotSource>();
        var bridgeDirectories = new List<string>();
        var optiDirectories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(List<SnapshotSource> target, string name, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                return;
            }

            if (!seen.Add(full))
            {
                return;
            }

            target.Add(new SnapshotSource(UniqueName(target, name), full));
        }

        void AddDirectory(List<string> target, string? directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            try
            {
                string full = Path.GetFullPath(directory);
                if (!target.Contains(full, StringComparer.OrdinalIgnoreCase))
                {
                    target.Add(full);
                }
            }
            catch
            {
                // 路径非法：跳过
            }
        }

        // ① 游戏目录：ReShade / RenoDX 的日志与 ini
        if (!string.IsNullOrWhiteSpace(gameDirectory))
        {
            Add(logs, "shade-ReShade.log", Path.Combine(gameDirectory, "ReShade.log"));
            Add(logs, "shade-ReShade2.log", Path.Combine(gameDirectory, "ReShade2.log"));
            Add(logs, "shade-RenoDX-DLSS5-crash.log", Path.Combine(gameDirectory, "RenoDX-DLSS5-crash.log"));
            Add(configs, "cfg-ReShade.ini", Path.Combine(gameDirectory, "ReShade.ini"));
            Add(configs, "cfg-ReShade2.ini", Path.Combine(gameDirectory, "ReShade2.ini"));
        }

        // ② 桥：先用注册表解析出来的注入 DLL 所在目录，再兜历史 / 迁移前的模块目录
        if (gameId is not null)
        {
            try
            {
                foreach (var entry in ModuleRegistry.ResolveInjectionDlls(gameId))
                {
                    if (ModuleRegistry.IsGenshinFsrBridgeDll(entry.DllPath))
                    {
                        AddDirectory(bridgeDirectories, Path.GetDirectoryName(entry.DllPath));
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "会话快照：解析桥路径失败");
            }
        }

        foreach (string? root in new[]
                 {
                     AppConfig.UserDataFolder is { Length: > 0 } userData ? Path.Combine(userData, "Modules") : null,
                     string.IsNullOrWhiteSpace(AppConfig.ModulesCachePath) ? null : AppConfig.ModulesCachePath,
                 })
        {
            if (root is null || bridgeDirectories.Count > 0)
            {
                continue;
            }

            try
            {
                foreach (string log in FindFilesShallow(root, "Dx11FsrBridge.log"))
                {
                    AddDirectory(bridgeDirectories, Path.GetDirectoryName(log));
                }
            }
            catch
            {
                // 目录不存在 / 没权限：跳过
            }
        }

        foreach (string directory in bridgeDirectories)
        {
            Add(logs, "bridge-Dx11FsrBridge.log", Path.Combine(directory, "Dx11FsrBridge.log"));
            Add(configs, "cfg-Dx11FsrBridge.ini", Path.Combine(directory, OptiScalerRuntime.FsrBridgeIniName));
            Add(configs, "cfg-Dx11FsrBridge.autoload.txt", Path.Combine(directory, "Dx11FsrBridge.autoload.txt"));
            Add(configs, "cfg-bridge-build.json", Path.Combine(directory, "build.json"));
        }

        // ③ OptiScaler：选中的构建目录 + 它的上一级（日志/ini 默认都在上一级，例如
        //    OptiScaler\mfg-ada\mfg-ada-0.1.9\OptiScaler.log）；再兜整个 OptiScaler 库。
        try
        {
            string? optiDll = gameId is null
                ? AppConfig.GetSelectedOptiScalerDll()
                : AppConfig.GetSelectedOptiScalerDll(gameId);
            string? buildDirectory = string.IsNullOrWhiteSpace(optiDll) ? null : Path.GetDirectoryName(optiDll);
            if (!string.IsNullOrWhiteSpace(buildDirectory))
            {
                AddDirectory(optiDirectories, buildDirectory);
                AddDirectory(optiDirectories, Path.GetDirectoryName(buildDirectory));
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "会话快照：解析 OptiScaler 路径失败");
        }

        if (optiDirectories.Count == 0)
        {
            try
            {
                string root = AppConfig.OptiScalerRootPath;
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    foreach (string log in FindFilesShallow(root, "OptiScaler.log"))
                    {
                        AddDirectory(optiDirectories, Path.GetDirectoryName(log));
                    }
                }
            }
            catch
            {
                // 库目录读不出来不算错
            }
        }

        foreach (string directory in optiDirectories)
        {
            string tag = FolderTag(directory);
            Add(logs, $"opti-{tag}-OptiScaler.log", Path.Combine(directory, "OptiScaler.log"));
            Add(configs, $"cfg-{tag}-OptiScaler.ini", Path.Combine(directory, "OptiScaler.ini"));
            Add(configs, $"cfg-{tag}-build.json", Path.Combine(directory, "build.json"));
        }

        // ④ Hub 自己当天的日志：一个文件夹里就能拿到全部现场
        try
        {
            string hubLog = string.IsNullOrWhiteSpace(AppConfig.LogFile)
                ? Path.Combine(AppConfig.LogFolder, $"HoYoShadeHub_{DateTime.Now:yyMMdd}.log")
                : AppConfig.LogFile;
            Add(logs, Path.GetFileName(hubLog), hubLog);
        }
        catch
        {
            // 日志路径读不出来就少一个文件
        }

        return (logs, configs, bridgeDirectories, optiDirectories);
    }

    private static string FolderTag(string directory)
    {
        string trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? "root" : Sanitize(name);
    }

    /// <summary>
    /// 在 <paramref name="root"/> 下最多 <paramref name="maxDepth"/> 层里找同名文件。
    /// 用它而不是 <c>SearchOption.AllDirectories</c>：Modules / OptiScaler 库底下有几万个文件，
    /// 而这里是在启动路径上跑的，不能全递归。
    /// （桥固定在 <c>Modules\&lt;模块名&gt;\</c>；OptiScaler 日志在 <c>&lt;库&gt;\&lt;作者&gt;\&lt;构建&gt;\</c>。）
    /// </summary>
    private static IEnumerable<string> FindFilesShallow(string root, string fileName, int maxDepth = 3)
    {
        var pending = new Queue<(string Directory, int Depth)>();
        pending.Enqueue((root, 0));

        while (pending.Count > 0)
        {
            (string directory, int depth) = pending.Dequeue();

            string direct = Path.Combine(directory, fileName);
            if (File.Exists(direct))
            {
                yield return direct;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            try
            {
                foreach (string child in Directory.EnumerateDirectories(directory))
                {
                    pending.Enqueue((child, depth + 1));
                }
            }
            catch
            {
                // 单个子目录读不了就跳过
            }
        }
    }

    private static string UniqueName(List<SnapshotSource> existing, string name)
    {
        if (existing.All(s => !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            return name;
        }

        string stem = Path.GetFileNameWithoutExtension(name);
        string extension = Path.GetExtension(name);
        for (int index = 2; index < 100; index++)
        {
            string candidate = $"{stem}-{index}{extension}";
            if (existing.All(s => !string.Equals(s.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return name;
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
        {
            sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
        }

        return sb.ToString();
    }

    private string UniqueSessionDirectory()
    {
        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        string safeName = Sanitize(_processName);
        string candidate = Path.Combine(SessionsRoot, $"{stamp}_{safeName}_{_pid}");
        if (!Directory.Exists(candidate))
        {
            return candidate;
        }

        for (int index = 2; index < 100; index++)
        {
            string next = Path.Combine(SessionsRoot, $"{stamp}_{safeName}_{_pid}-{index}");
            if (!Directory.Exists(next))
            {
                return next;
            }
        }

        return candidate;
    }

    /// <summary>
    /// 只保留最近 <paramref name="keepExisting"/> 个已有会话（总量不超过 <see cref="MaxTotalBytes"/>）。
    /// 删除前逐项校验：必须在会话根目录下、且目录名符合本类自己生成的那套形状
    /// —— 绝不对算出来的路径乱删。
    /// </summary>
    private static void PruneOldSessions(int keepExisting)
    {
        try
        {
            string root = Path.GetFullPath(SessionsRoot);
            if (!Directory.Exists(root))
            {
                return;
            }

            string prefix = root + Path.DirectorySeparatorChar;
            List<DirectoryInfo> sessions = new DirectoryInfo(root).GetDirectories()
                .Where(d => SessionFolderPattern.IsMatch(d.Name))
                .OrderByDescending(d => d.Name, StringComparer.Ordinal)
                .ToList();

            void TryDelete(DirectoryInfo directory)
            {
                string full = Path.GetFullPath(directory.FullName);
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || !SessionFolderPattern.IsMatch(Path.GetFileName(full)))
                {
                    _logger.LogWarning("会话日志清理：跳过可疑路径 {Path}", full);
                    return;
                }

                try
                {
                    directory.Delete(recursive: true);
                    _logger.LogInformation("会话日志清理：已删除旧会话 {Directory}", full);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "会话日志清理失败：{Directory}", full);
                }
            }

            static long SizeOf(DirectoryInfo directory)
            {
                try
                {
                    return directory.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
                }
                catch
                {
                    return 0;
                }
            }

            foreach (DirectoryInfo stale in sessions.Skip(keepExisting))
            {
                TryDelete(stale);
            }

            // 数量没超但太占地方（OptiScaler 开 trace 时一局能到几十 MB）：继续从最旧的删
            List<DirectoryInfo> kept = sessions.Take(keepExisting).ToList();
            long total = kept.Sum(SizeOf);
            for (int index = kept.Count - 1; index >= 0 && total > MaxTotalBytes; index--)
            {
                total -= SizeOf(kept[index]);
                TryDelete(kept[index]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "会话日志清理失败");
        }
    }

    private sealed record SnapshotSource(string Name, string Path);
}
