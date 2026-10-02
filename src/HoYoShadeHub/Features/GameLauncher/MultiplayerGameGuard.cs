using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>
/// 多人/强反作弊游戏后台检测守卫。
///
/// <para>
/// 背景：有反馈"启动器后台运行导致三角洲（ACE 反作弊）封号"。ACE 是内核级驱动，其典型
/// 判定链是"枚举进程 → 发现注入工具链进程 → 定位其目录 → 扫盘取证"，因此只挡注入没用 ——
/// 命中名单时这里直接<strong>静默完全退出整个启动器</strong>（不留 UI 提示、不残留子进程），
/// 让"扫进程定位目录"这条链在我们这边断掉。名单内进程退出后由用户手动重开启动器。
/// </para>
///
/// <para>轮询只读进程名（Process.GetProcesses），不打开目标进程句柄、不读内存、不注入。</para>
/// </summary>
internal static class MultiplayerGameGuard
{
    private static readonly ILogger _logger = AppConfig.GetLogger<GuardLogToken>();

    /// <summary>日志类别占位（静态类不能做泛型参数）</summary>
    private sealed class GuardLogToken { }

    /// <summary>多人游戏主进程与第三方反作弊服务（不含 .exe，不区分大小写）。改这里扩名单。</summary>
    private static readonly HashSet<string> WatchedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // 三角洲行动（腾讯 ACE）
        "DeltaForceClient-Win64-Shipping",
        // 拳头 / Vanguard
        "VALORANT-Win64-Shipping",
        // Valve / CS2
        "cs2",
        // 绝地求生
        "TslGame",
        // Apex
        "r5apex",
        // 堡垒之夜 / EAC
        "FortniteClient-Win64-Shipping",
        // 使命召唤
        "cod", "codmw",
        // 战地
        "bf2042", "bf1", "bfv", "bf4",
        // 守望先锋
        "Overwatch",
        // The Finals / EAC
        "Discovery",
        // 暗区突围无限
        "ArenaBreakoutInfinite",
        // 战术小队
        "SquadGame",
        // 猎杀对决 / EAC
        "HuntGame",
        // 腐蚀 / EAC
        "RustClient",
        // 逃离塔科夫
        "EscapeFromTarkov", "TarkovArena",
        // 命运 2（对注入极敏感）
        "destiny2",
        // 注意：只收攻击性多人游戏本体进程。反作弊服务名（SGuardSvc64 / BEService / EAC 等）
        // 不收 —— 一些二游同样带 ACE 但扫描宽松，按服务名匹配会把它们误伤、把启动器自己退掉。
    };

    private static readonly SemaphoreSlim _startLock = new(1, 1);
    private static volatile bool _started;
    private static volatile bool _active;
    private static volatile bool _exitStarted;
    private static string? _activeName;

    /// <summary>true = 检测到名单内的多人游戏/反作弊正在运行，注入类操作必须拒绝</summary>
    public static bool IsActive => _active;

    /// <summary>当前命中的进程名（IsActive 时有效，便于日志/提示）</summary>
    public static string? ActiveName => _activeName;

    /// <summary>幂等启动后台轮询（Program.Main 里调一次即可）</summary>
    public static void EnsureStarted()
    {
        if (_started)
        {
            return;
        }

        _startLock.Wait();
        try
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _ = Task.Run(WatchLoop);
        }
        finally
        {
            _startLock.Release();
        }
    }

    private static async Task WatchLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (true)
        {
            try
            {
                await timer.WaitForNextTickAsync();
                ScanOnce();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "多人游戏守卫轮询异常");
            }
        }
    }

    internal static void ScanOnce()
    {
        string? hit = null;
        Process[]? processes = null;
        try
        {
            processes = Process.GetProcesses();
            foreach (Process process in processes)
            {
                try
                {
                    if (WatchedNames.Contains(process.ProcessName))
                    {
                        hit = process.ProcessName;
                        break;
                    }
                }
                catch
                {
                    // 单个进程名读不到（正在退出）就跳过
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "多人游戏守卫枚举进程失败");
        }
        finally
        {
            if (processes is not null)
            {
                foreach (Process process in processes)
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }
        }

        bool wasActive = _active;
        _active = hit is not null;
        _activeName = hit;

        if (_active && !wasActive)
        {
            _logger.LogWarning("多人游戏守卫：检测到 {Process} 在运行 —— 静默完全退出启动器（含子进程）", hit);
            SilentSelfDestruct();
        }
    }

    /// <summary>
    /// 静默完全退出：先清掉所有同名兄弟进程（playtime / rpc / run 子进程都是
    /// HoYoShadeHub.exe 拉起来的）和 hub 目录内的 inject.exe，再优雅退出，
    /// 2.5 秒后看门狗强制 Environment.Exit 兜底，确保零残留。
    /// </summary>
    private static void SilentSelfDestruct()
    {
        if (_exitStarted)
        {
            return;
        }

        _exitStarted = true;

        try
        {
            int currentId = Environment.ProcessId;

            // 1) 同名兄弟进程（playtime / rpc / run / auto 子进程）
            foreach (Process process in Process.GetProcessesByName("HoYoShadeHub"))
            {
                if (process.Id == currentId)
                {
                    continue;
                }

                try
                {
                    process.Kill();
                    _logger.LogInformation("多人游戏守卫：已结束兄弟进程 pid={Pid}", process.Id);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "多人游戏守卫：结束兄弟进程 pid={Pid} 失败", process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }

            // 2) hub 目录内的 HoYoShade inject.exe（注入器不残留）
            string hubRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            foreach (Process process in Process.GetProcessesByName("inject"))
            {
                try
                {
                    string? path = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(path)
                        && path.StartsWith(hubRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        _logger.LogInformation("多人游戏守卫：已结束注入器 pid={Pid}", process.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "多人游戏守卫：结束注入器 pid={Pid} 失败", process.Id);
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "多人游戏守卫：清子进程阶段异常");
        }

        // 3) 优雅退出；2.5s 后强制退出兜底，保证进程不残留
        _ = Task.Run(async () =>
        {
            await Task.Delay(2500);
            try
            {
                _logger.LogWarning("多人游戏守卫：看门狗强制退出（优雅退出未在 2.5s 内完成）");
                Environment.Exit(0);
            }
            catch
            {
                // 到此为止
            }
        });

        try
        {
            Application.Current?.Exit();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "多人游戏守卫：优雅退出异常，交给看门狗");
            Environment.Exit(0);
        }
    }

    /// <summary>注入类操作的统一拒绝理由（退出完成前与其它调用方共用）</summary>
    public static string BlockReason()
        => $"检测到多人/反作弊游戏正在运行（{ActiveName}），启动器已退出；关闭该游戏后重新打开启动器";
}
