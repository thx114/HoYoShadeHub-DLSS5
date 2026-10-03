using CommunityToolkit.Mvvm.Messaging;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.GameLauncher;
using HoYoShadeHub.Features.GameSetting;
using HoYoShadeHub.Features.GameSelector;
using HoYoShadeHub.Features.OptiScaler;
using HoYoShadeHub.Features.Xxmi;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>执行 auto.json 自定义动作（或内置动作）时的上下文：当前游戏 / 宿主 / 插件服务 / 页面</summary>
public sealed class LauncherActionContext
{
    public required GameId? GameId { get; init; }

    public required GameBiz? GameBiz { get; init; }

    public GameEntry? Entry { get; init; }

    public ShadeHost? Host { get; init; }

    /// <summary>当前游戏的插件服务（插件开关 / 预设应用都走它）；没有 ini 时可为 null</summary>
    public GamePluginService? Plugins { get; init; }

    /// <summary>启动器页（UseSmoothMotion 这类页面属性要用）；手动从插件页跑时可为 null</summary>
    public GameLauncherPage? LauncherPage { get; init; }

    /// <summary>覆盖包根目录（auto.json / presets\ / *_files\ 所在）；不在包里时 null</summary>
    public string? PackRoot { get; init; }

    public XamlRoot? XamlRoot { get; init; }

    /// <summary>true = 用户点了按钮（可以弹窗）；false = 启动前自动跑（UI 弹窗类动作跳过）</summary>
    public bool Interactive { get; init; }

    /// <summary>进度汇报（写状态栏 / 日志）</summary>
    public Action<string>? Report { get; init; }
}

/// <summary>
/// auto.json 自定义动作的执行器：把每个步骤翻译成**现有的启动器接口**调用。
/// 一个步骤失败不中断后续步骤（✗ 记在返回的摘要里）。
/// 所有接口名大小写不敏感；参数缺失 / 上下文不够时该步骤报 ✗ 跳过。
/// </summary>
public static class LauncherActionRunner
{
    private static readonly ILogger _logger = AppConfig.GetLogger<GamePluginPage>();

    public static async Task<string> RunAsync(PackAutoAction action, LauncherActionContext context)
    {
        var results = new List<string>();
        foreach (PackActionStep step in action.Steps)
        {
            try
            {
                string outcome = await RunStepAsync(step, context);
                results.Add(outcome);
                context.Report?.Invoke($"[{action.Name}] {outcome}");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Action step {Action} failed", step.Action);
                results.Add($"✗ {step.Action}：{ex.Message}");
            }
        }

        return string.Join("；", results);
    }

    private static async Task<string> RunStepAsync(PackActionStep step, LauncherActionContext context)
    {
        // once=true：按动作名 + 步骤内容哈希在包根 .hysx_once.json 记账，只执行一次。
        // 导入覆盖包"改一次就放手"的通用修饰符；改了步骤内容哈希变，会自动重新执行一次。
        bool once = step.GetBool("once") == true;
        if (once && !string.IsNullOrWhiteSpace(context.PackRoot) && OnceMarker.Has(context.PackRoot, step))
        {
            return $"{step.Action}（once）：已执行过，跳过";
        }

        string outcome = await DispatchStepAsync(step, context);

        // 成功失败都记账：失败多半是包里缺文件这类持久状态，不该每启动重复报错刷屏
        if (once && !string.IsNullOrWhiteSpace(context.PackRoot))
        {
            OnceMarker.Mark(context.PackRoot, step);
        }

        return outcome;
    }

    private static async Task<string> DispatchStepAsync(PackActionStep step, LauncherActionContext context)
        => step.Action.Trim().ToLowerInvariant() switch
        {
            // ===== 插件 =====
            "set_addon" or "set_addons" => SetAddons(step, context),

            // ===== 预设 =====
            "import_preset" => ImportPreset(step, context),
            "apply_preset" => ApplyPreset(step, context),

            // ===== ini =====
            "clear_game_ini" => ClearGameIni(context),
            "set_ini_keys" or "set_ini" or "write_ini" => SetIniKeys(step, context),

            // ===== 启动选项 =====
            "set_dx12" => SetDx12(step, context),
            "set_smooth_motion" or "set_ai_frame" or "set_ai_interpolation" => await SetSmoothMotionAsync(step, context),
            "set_inject_mode" => SetInjectMode(step, context),
            "set_cmd_launch" => SetCmdLaunch(step, context),
            "set_cmd_args" => SetCmdArgs(step, context),
            "set_launch_option" => SetLaunchOption(step, context),

            // ===== OptiScaler / 模块 / XXMI =====
            "set_opt" or "set_optiscaler" => SetOptiScaler(step, context),
            "set_opt_build" or "set_optiscaler_build" or "select_opt_build" => SetOptiScalerBuild(step, context),
            "set_module" => SetModule(step, context),
            "import_opt_config" => ImportOptConfig(step, context),
            "update_opt_config" => UpdateOptConfig(step, context),
            "set_opt_config" or "apply_opt_config" or "select_opt_config" => SetOptConfig(step, context),
            "set_opt_dll" => SetOptDll(step, context),
            "import_xxmi" => ImportXxmi(step, context),

            // ===== 其余 =====
            "switch_dll" => await SwitchDllAsync(step, context),
            "set_inject_delay" => SetInjectDelay(step, context),
            "set_game_setting" => SetGameSetting(step, context),
            "set_window_mode" or "force_window_mode" => SetWindowMode(step, context),
            "override_files" => OverrideFiles(step, context),
            "import_shader" => ImportShader(step, context),
            "set_shader" => SetShader(step, context),
            "delete_shader" => DeleteShader(step, context),
            "run_compat_check" => await RunCompatCheckAsync(step, context),
            "add_game_from_registry" => AddGameFromRegistry(step, context),

            // ===== 启动 / 结束游戏（CLI / auto.json 都能用）=====
            "launch_game" or "launch" or "start_game" => await LaunchGameAsync(context),
            "stop_game" or "close_game" or "kill_game" or "stopgame" => await StopGameAsync(context),
            "wait" or "sleep" or "delay" => await WaitAsync(step),
            "click_game" or "click_window" => await ClickGameAsync(step, context),
            "press_key" or "send_key" => await PressGameKeyAsync(step, context),
            "test_game" or "launch_wait_stop" or "self_test" => await TestGameAsync(step, context),
            "test_map" or "map_test" => await TestMapAsync(step, context),

            _ => $"✗ 不支持的接口：{step.Action}",
        };

    /// <summary>launch_game / stop_game 共用的游戏解析</summary>
    private static GameId? ResolveGameId(LauncherActionContext context)
        => context.GameId
           ?? (context.GameBiz is { } biz ? GameId.FromGameBiz(biz) : null);

    // ==================== 启动游戏 ====================

    /// <summary>
    /// launch_game：按启动选项完整启动上下文指定的游戏（GameLaunchPipeline：shade 注入器 /
    /// 模块 / OptiScaler / 帧率解锁一个不少，和启动页「启动游戏」同行为）。
    /// 纯 CLI（无 UI 上下文）会等到注入完成才返回；启动器里触发则后台注入。
    /// </summary>
    private static async Task<string> LaunchGameAsync(LauncherActionContext context)
    {
        GameId? gameId = ResolveGameId(context);
        if (gameId is null)
        {
            return "✗ launch_game 需要先指定游戏（--biz xxx）";
        }

        GameEntry? entry = context.Entry;
        if (entry is null)
        {
            try
            {
                entry = GameCatalog.GetOrCreate(GameCatalog.CreateService(), gameId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "launch_game: resolve game entry");
            }
        }

        bool blockForInjection = context.LauncherPage is null && context.XamlRoot is null;
        System.Diagnostics.Process? process = await GameLaunchPipeline.LaunchAsync(
            gameId, entry, blockForInjection, context.Report);
        return process is null ? "✗ 启动游戏失败（看日志）" : $"已启动游戏（PID {process.Id}）";
    }


    // ==================== 游戏窗口输入 ====================

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(nint hWnd, ref NativePoint point);
    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint hWnd, int command);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(nint hWnd, bool altTab);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);
    [DllImport("user32.dll")]
    private static extern nint SetFocus(nint hWnd);
    [DllImport("user32.dll")]
    private static extern nint SetActiveWindow(nint hWnd);
    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, nint processId);
    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, bool attachState);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, uint dx, uint dy, uint data, nint extraInfo);
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, nint extraInfo);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput { public uint Type; public NativeInputUnion Data; }
    [StructLayout(LayoutKind.Explicit)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public NativeMouseInput Mouse;
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMouseInput
    {
        public int Dx, Dy;
        public uint MouseData, Flags, Time;
        public nint ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey, ScanCode;
        public uint Flags, Time;
        public nint ExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, NativeInput[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpShowWindow = 0x0040;
    private static readonly nint HwndTop = new(-1);
    private static readonly nint HwndNoTopMost = new(-2);


    private static bool SendMouseButton(uint flags)
    {
        var input = new NativeInput {
            Type = 0,
            Data = new NativeInputUnion { Mouse = new NativeMouseInput { Flags = flags } }
        };
        return SendInput(1, new[] { input }, Marshal.SizeOf<NativeInput>()) == 1;
    }

    private static bool SendKey(byte virtualKey, bool keyUp)
    {
        var input = new NativeInput {
            Type = 1,
            Data = new NativeInputUnion { Keyboard = new NativeKeyboardInput {
                VirtualKey = virtualKey, Flags = keyUp ? 0x0002u : 0u
            } }
        };
        return SendInput(1, new[] { input }, Marshal.SizeOf<NativeInput>()) == 1;
    }

    private static async Task<(bool Ok, string Error)> ActivateGameWindowAsync(nint hwnd)
    {
        ShowWindowAsync(hwnd, 9);
        uint currentThread = GetCurrentThreadId();
        uint targetThread = GetWindowThreadProcessId(hwnd, nint.Zero);
        bool attached = targetThread != 0 && currentThread != targetThread &&
            AttachThreadInput(currentThread, targetThread, true);
        SetForegroundWindow(hwnd);
        SwitchToThisWindow(hwnd, true);
        SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        SetWindowPos(hwnd, HwndNoTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpShowWindow);
        BringWindowToTop(hwnd);
        SetActiveWindow(hwnd);
        SetFocus(hwnd);
        await Task.Delay(200);
        nint foreground = GetForegroundWindow();
        if (attached)
            AttachThreadInput(currentThread, targetThread, false);
        if (foreground != hwnd)
            return (false, $"前台激活失败 hwnd=0x{hwnd.ToInt64():X} foreground=0x{foreground.ToInt64():X}");
        return (true, string.Empty);
    }

    /// <summary>
    /// click_game：把游戏窗口激活后点击客户区。x/y 默认是归一化坐标，便于不同分辨率的登录按钮自动化。
    /// 传入 x/y 大于 1 时按客户区像素解释。
    /// </summary>
    private static async Task<string> ClickGameAsync(PackActionStep step, LauncherActionContext context)
    {
        GameId? gameId = ResolveGameId(context);
        if (gameId is null)
            return "✗ click_game 需要先指定游戏（--biz xxx）";

        Process? process = await AppConfig.GetService<GameLauncherService>().GetGameProcessAsync(gameId);
        if (process is null || process.HasExited || process.MainWindowHandle == nint.Zero)
            return "✗ 游戏窗口不存在";

        process.Refresh();
        nint hwnd = process.MainWindowHandle;
        var activation = await ActivateGameWindowAsync(hwnd);
        if (!activation.Ok)
            return "✗ " + activation.Error;
        if (!GetClientRect(hwnd, out NativeRect rect) || rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            return "✗ 无法读取游戏客户区";

        double xValue = step.GetNumber("x") ?? 0.5;
        double yValue = step.GetNumber("y") ?? 0.92;
        int clientX = xValue > 1.0 ? (int)Math.Round(xValue) : (int)Math.Round((rect.Right - rect.Left) * xValue);
        int clientY = yValue > 1.0 ? (int)Math.Round(yValue) : (int)Math.Round((rect.Bottom - rect.Top) * yValue);
        clientX = Math.Clamp(clientX, 0, rect.Right - rect.Left - 1);
        clientY = Math.Clamp(clientY, 0, rect.Bottom - rect.Top - 1);
        var point = new NativePoint { X = clientX, Y = clientY };
        if (!ClientToScreen(hwnd, ref point))
            return "✗ 无法转换游戏坐标";

        SetCursorPos(point.X, point.Y);
        bool down = SendMouseButton(MouseLeftDown);
        await Task.Delay(60);
        bool up = SendMouseButton(MouseLeftUp);
        await Task.Delay(250);
        if (!down || !up)
            return $"✗ 鼠标注入失败 win32={Marshal.GetLastWin32Error()}";
        return $"已点击游戏窗口（{clientX},{clientY}）";
    }


    private static async Task<string> PressGameKeyAsync(PackActionStep step, LauncherActionContext context)
    {
        GameId? gameId = ResolveGameId(context);
        if (gameId is null)
            return "✗ press_key 需要先指定游戏（--biz xxx）";
        Process? process = await AppConfig.GetService<GameLauncherService>().GetGameProcessAsync(gameId);
        if (process is null || process.HasExited || process.MainWindowHandle == nint.Zero)
            return "✗ 游戏窗口不存在";
        string key = (step.GetString("key") ?? "enter").Trim().ToLowerInvariant();
        byte vk = key switch
        {
            "enter" or "return" => 0x0D,
            "space" => 0x20,
            "escape" or "esc" => 0x1B,
            "f5" => 0x74,
            "f6" => 0x75,
            "f10" => 0x79,
            "home" => 0x24,
            _ => 0
        };
        if (vk == 0) return $"✗ 不支持按键：{key}";
        var activation = await ActivateGameWindowAsync(process.MainWindowHandle);
        if (!activation.Ok)
            return "✗ " + activation.Error;
        bool down = SendKey(vk, false);
        await Task.Delay(60);
        bool up = SendKey(vk, true);
        await Task.Delay(250);
        if (!down || !up)
            return $"✗ 键盘注入失败 win32={Marshal.GetLastWin32Error()}";
        return $"已发送按键：{key}";
    }


    // ==================== 结束游戏 ====================

    /// <summary>stop_game：结束上下文指定的游戏。先给主窗口发关闭消息（游戏有机会存档收尾），5 秒不退再强杀。</summary>
    private static async Task<string> StopGameAsync(LauncherActionContext context)
    {
        GameId? gameId = ResolveGameId(context);
        if (gameId is null)
        {
            return "✗ stop_game 需要先指定游戏（--biz xxx）";
        }

        System.Diagnostics.Process? process = await AppConfig.GetService<GameLauncherService>()
            .GetGameProcessAsync(gameId);
        if (process is null)
        {
            return "游戏没在运行";
        }

        try
        {
            process.CloseMainWindow();
            for (int i = 0; i < 20 && !process.HasExited; i++)
            {
                await Task.Delay(250);
            }

            if (!process.HasExited)
            {
                process.Kill();
                return $"已强杀游戏（PID {process.Id}）";
            }

            return $"游戏已退出（PID {process.Id}）";
        }
        catch (Exception ex)
        {
            return "✗ 结束游戏失败：" + ex.Message;
        }
    }

    /// <summary>
    /// test_game：启动 → 等待 → 关闭，压缩成一个 CLI 步骤，适合自动采集日志。
    /// 参数同 wait：seconds / milliseconds / ms / duration_ms；默认 60 秒。
    /// </summary>
    private static async Task<string> TestGameAsync(PackActionStep step, LauncherActionContext context)
    {
        string launched = await LaunchGameAsync(context);
        if (launched.StartsWith("✗", StringComparison.Ordinal))
        {
            return launched;
        }

        string waited = await WaitAsync(step, 60_000);
        if (waited.StartsWith("✗", StringComparison.Ordinal))
        {
            await StopGameAsync(context);
            return waited;
        }

        string stopped = await StopGameAsync(context);
        return $"{launched}；{waited}；{stopped}";
    }


    /// <summary>test_map：启动 → 等待 → 点击登录/进入 → 发送按键 → 等待 → 关闭。</summary>
    private static async Task<string> TestMapAsync(PackActionStep step, LauncherActionContext context)
    {
        string launched = await LaunchGameAsync(context);
        if (launched.StartsWith("✗", StringComparison.Ordinal))
            return launched;

        double preSeconds = step.GetNumber("pre_seconds") ?? 50.0;
        double postSeconds = step.GetNumber("post_seconds") ?? 30.0;
        if (!double.IsFinite(preSeconds) || !double.IsFinite(postSeconds) ||
            preSeconds < 0 || postSeconds < 0 || preSeconds > 600 || postSeconds > 600)
            return "✗ test_map 的 pre_seconds/post_seconds 必须在 0 到 600 之间";

        await Task.Delay((int)Math.Round(preSeconds * 1000.0, MidpointRounding.AwayFromZero));
        string clicked = await ClickGameAsync(step, context);
        string pressed = await PressGameKeyAsync(step, context);
        await Task.Delay((int)Math.Round(postSeconds * 1000.0, MidpointRounding.AwayFromZero));
        string stopped = await StopGameAsync(context);
        return $"{launched}；{clicked}；{pressed}；已等待 {postSeconds:0.###} s；{stopped}";
    }

    /// <summary>wait / sleep / delay：按毫秒或秒暂停后续动作，供 CLI 自动测试使用。</summary>
    private static async Task<string> WaitAsync(PackActionStep step, double? defaultMilliseconds = null)
    {
        double? milliseconds = step.GetNumber("milliseconds")
            ?? step.GetNumber("ms")
            ?? step.GetNumber("duration_ms");
        if (milliseconds is null && step.GetNumber("seconds") is { } seconds)
        {
            milliseconds = seconds * 1000.0;
        }

        milliseconds ??= defaultMilliseconds;
        if (milliseconds is null)
        {
            return "✗ wait 需要 milliseconds/ms/duration_ms 或 seconds 参数";
        }

        if (!double.IsFinite(milliseconds.Value) || milliseconds.Value < 0 || milliseconds.Value > 3_600_000)
        {
            return "✗ wait 时间必须在 0 到 3600000 毫秒之间";
        }

        int delay = (int)Math.Round(milliseconds.Value, MidpointRounding.AwayFromZero);
        await Task.Delay(delay);
        return $"已等待 {delay} ms";
    }

    // ==================== 插件 ====================

    /// <summary>set_addon(s)：开关插件。files 缺省 / 空 = 全部插件（「关闭全部插件」就是这个）。</summary>
    private static string SetAddons(PackActionStep step, LauncherActionContext context)
    {
        bool? enabled = step.GetBool("enabled");
        if (enabled is null)
        {
            return "✗ set_addons 缺 enabled 参数";
        }

        if (context.Plugins is null)
        {
            return "✗ 这个游戏还没有 ReShade.ini，插件开关不可用";
        }

        List<string> files = step.GetStringList("files");
        List<GameAddonState> addons = context.Plugins.GetAddons();
        List<GameAddonState> targets = files.Count == 0
            ? addons
            : addons.Where(a => files.Any(f =>
                string.Equals(a.FileName, f, StringComparison.OrdinalIgnoreCase))).ToList();

        if (targets.Count == 0)
        {
            return $"✗ 没有匹配到插件（files={string.Join(",", files)}）";
        }

        int ok = 0;
        foreach (GameAddonState addon in targets)
        {
            if (context.Plugins.SetAddonEnabled(addon.FileName, enabled.Value))
            {
                ok++;
            }
        }

        return $"{(enabled.Value ? "启用" : "禁用")}插件 {ok}/{targets.Count}"
               + (files.Count == 0 ? "（全部）" : string.Empty);
    }

    // ==================== 预设 ====================

    private static string ImportPreset(PackActionStep step, LauncherActionContext context)
    {
        string? addonDir = context.Plugins?.AddonDirectory
                           ?? context.Host?.AddonsPath;
        if (string.IsNullOrWhiteSpace(addonDir))
        {
            return "✗ 没有插件目录，导不了预设";
        }

        string? shareCode = step.GetString("shareCode") ?? step.GetString("code");
        if (!string.IsNullOrWhiteSpace(shareCode))
        {
            string? target = Dlss5PresetLibrary.ImportShareCode(addonDir, shareCode, step.GetString("name"));
            return target is null ? "✗ 分享码解不开" : "已导入分享码 → " + Path.GetFileName(target);
        }

        // 包内文件（presets\ 下的相对路径）
        string? file = step.GetString("file");
        if (string.IsNullOrWhiteSpace(file))
        {
            return "✗ import_preset 需要 shareCode 或 file 参数";
        }

        string? source = ResolvePackFile(context, file!);
        if (source is null)
        {
            return $"✗ 包里找不到 {file}";
        }

        string? imported = Dlss5PresetLibrary.ImportFile(addonDir, source);
        return imported is null ? "✗ 预设导入失败" : "已导入预设 → " + Path.GetFileName(imported);
    }

    /// <summary>apply_preset：把 DLSS5-Presets 里的一个预设设成这个游戏的当前 ReShade 预设</summary>
    private static string ApplyPreset(PackActionStep step, LauncherActionContext context)
    {
        string? name = step.GetString("name") ?? step.GetString("file");
        if (string.IsNullOrWhiteSpace(name))
        {
            return "✗ apply_preset 需要 name 参数";
        }

        if (context.Entry?.ReShadeIniPath is not { } ini || !File.Exists(ini))
        {
            return "✗ 这个游戏还没有 ReShade.ini";
        }

        string? addonDir = context.Plugins?.AddonDirectory
                           ?? ReShadeProfile.Load(ini).ResolveAddonDirectory();
        if (string.IsNullOrWhiteSpace(addonDir))
        {
            return "✗ 找不到插件目录";
        }

        // 先认 DLSS5-Presets（各级子目录），再认 Addons 顶层，最后认包内文件
        string presetFolder = Path.Combine(addonDir, Dlss5PresetLibrary.PresetFolderName);
        string? preset = Directory.Exists(presetFolder)
            ? Directory.EnumerateFiles(presetFolder, "*.ini", SearchOption.AllDirectories)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase))
            : null;
        preset ??= Directory.EnumerateFiles(addonDir, "*.ini", SearchOption.TopDirectoryOnly)
            .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        preset ??= ResolvePackFile(context, name!);

        if (preset is null)
        {
            return $"✗ 找不到预设 {name}";
        }

        try
        {
            ReShadeProfile profile = ReShadeProfile.Load(ini);
            profile.SetValue("GENERAL", "PresetPath", preset);
            profile.Save();
            return "当前预设 → " + Path.GetFileName(preset);
        }
        catch (Exception ex)
        {
            return "✗ 写 PresetPath 失败：" + ex.Message;
        }
    }

    // ==================== ini ====================

    /// <summary>clear_game_ini：删掉游戏目录的 ReShade.ini / ReShade2.ini（下次启动按模板 + 覆盖包重建）</summary>
    private static string ClearGameIni(LauncherActionContext context)
    {
        if (context.Entry?.ReShadeIniPath is not { } ini)
        {
            return "✗ 不知道这个游戏目录在哪";
        }

        int deleted = 0;
        foreach (string file in new[]
        {
            ini,
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ini))!, GameIniBootstrap.SecondaryIniFileName),
        })
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                    deleted++;
                }
            }
            catch
            {
                // 占用中就跳过
            }
        }

        return deleted > 0 ? $"已删除 {deleted} 份游戏 ini" : "游戏 ini 本来就不在";
    }

    /// <summary>
    /// set_ini_keys：把任意节/键写进 ini（默认游戏 ReShade.ini；file= 可指游戏目录内的其它 ini，
    /// 相对路径）。set = { "节": { "键": "值" } }，remove = { "节": ["键"] }。
    /// once=true 时整个步骤只执行一次 —— 按步骤内容哈希在包根 .hysx_once.json 记账，
    /// 改包里的步骤内容会自动重新执行。导入覆盖包后"改一次 ini 就放手"靠它。
    /// </summary>
    private static string SetIniKeys(PackActionStep step, LauncherActionContext context)
    {
        string? iniPath;
        if (step.GetString("file") is { Length: > 0 } file)
        {
            string? gameDir = context.Entry?.GameDirectory;
            if (string.IsNullOrWhiteSpace(gameDir))
            {
                return "✗ set_ini_keys：没有当前游戏目录";
            }

            iniPath = Path.GetFullPath(Path.Combine(gameDir, file));
        }
        else
        {
            iniPath = context.Entry?.ReShadeIniPath;
        }

        if (string.IsNullOrWhiteSpace(iniPath) || !File.Exists(iniPath))
        {
            return "✗ set_ini_keys：找不到目标 ini（游戏要先启动过一次生成 ReShade.ini）";
        }

        var config = new GamePackIniConfig();
        int setCount = 0, removeCount = 0;

        if (step.Raw.ValueKind == JsonValueKind.Object
            && step.Raw.TryGetProperty("set", out JsonElement set) && set.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty section in set.EnumerateObject())
            {
                if (section.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!config.Set.TryGetValue(section.Name, out Dictionary<string, string>? pairs))
                {
                    pairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                foreach (JsonProperty key in section.Value.EnumerateObject())
                {
                    pairs[key.Name] = key.Value.ValueKind == JsonValueKind.String
                        ? key.Value.GetString() ?? string.Empty
                        : key.Value.ToString();
                    setCount++;
                }

                config.Set[section.Name] = pairs;
            }
        }

        if (step.Raw.ValueKind == JsonValueKind.Object
            && step.Raw.TryGetProperty("remove", out JsonElement remove) && remove.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty section in remove.EnumerateObject())
            {
                if (section.Value.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                if (!config.Remove.TryGetValue(section.Name, out List<string>? keys))
                {
                    keys = [];
                }

                foreach (JsonElement key in section.Value.EnumerateArray())
                {
                    if (key.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(key.GetString()))
                    {
                        keys.Add(key.GetString()!);
                        removeCount++;
                    }
                }

                config.Remove[section.Name] = keys;
            }
        }

        if (config.IsEmpty)
        {
            return "✗ set_ini_keys：没有 set / remove 内容";
        }

        bool changed = config.Apply(iniPath);

        return $"已写入 {Path.GetFileName(iniPath)}：set {setCount} 键 / remove {removeCount} 键"
               + (changed ? string.Empty : "（值相同，无改动）");
    }

    /// <summary>once 步骤记账：包根 .hysx_once.json，键 = 动作名 + 步骤内容哈希。</summary>
    private static class OnceMarker
    {
        private const string FileName = ".hysx_once.json";

        private static string HashOf(PackActionStep step)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(step.Action.Trim().ToLowerInvariant() + (char)10 + step.Raw.GetRawText());
            byte[] hash = System.Security.Cryptography.SHA256.HashData(bytes);
            return Convert.ToHexString(hash).ToLowerInvariant()[..16];
        }

        public static bool Has(string packRoot, PackActionStep step)
        {
            try
            {
                string path = Path.Combine(packRoot, FileName);
                if (!File.Exists(path))
                {
                    return false;
                }

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                return document.RootElement.ValueKind == JsonValueKind.Object
                       && document.RootElement.TryGetProperty(HashOf(step), out JsonElement value)
                       && value.ValueKind == JsonValueKind.True;
            }
            catch
            {
                return false;
            }
        }

        public static void Mark(string packRoot, PackActionStep step)
        {
            try
            {
                string path = Path.Combine(packRoot, FileName);
                Dictionary<string, bool> map = [];
                if (File.Exists(path))
                {
                    try
                    {
                        map = JsonSerializer.Deserialize<Dictionary<string, bool>>(File.ReadAllText(path)) ?? [];
                    }
                    catch
                    {
                        map = [];
                    }
                }

                map[HashOf(step)] = true;
                File.WriteAllText(path, JsonSerializer.Serialize(map));
            }
            catch
            {
                // 记账失败不影响主流程（下次还会执行一次，幂等）
            }
        }
    }

    // ==================== 启动选项 ====================

    private static string SetDx12(PackActionStep step, LauncherActionContext context)
    {
        if (context.GameBiz is not { } biz || step.GetBool("enabled") is not { } enabled)
        {
            return "✗ set_dx12 需要 enabled 参数和当前游戏";
        }

        AppConfig.SetEnableDX12(biz, enabled);
        return enabled ? "已开启 DX12 启动" : "已关闭 DX12 启动";
    }

    private static async Task<string> SetSmoothMotionAsync(PackActionStep step, LauncherActionContext context)
    {
        if (step.GetBool("enabled") is not { } enabled)
        {
            return "✗ set_smooth_motion 需要 enabled 参数";
        }

        // 启动器页在上下文里：走页面属性（连带低延迟 Ultra / 还原那一套）
        if (context.LauncherPage is not null)
        {
            context.LauncherPage.UseSmoothMotion = enabled;
            return enabled ? "已开启 AI 插帧（重启游戏生效）" : "已关闭 AI 插帧";
        }

        // 没有页面（插件页上下文）：直接写驱动配置（不动低延迟，那套还原逻辑是页面私有的）
        string? exeName = context.Entry?.ExePath is { } exe && exe.Length > 0
            ? Path.GetFileName(exe)
            : null;
        if (exeName is null)
        {
            return "✗ 不知道这个游戏的主程序名";
        }

        NvDrsInterop.State state = await Task.Run(() => NvDrsInterop.WriteSmoothMotionEnable(exeName, enabled));
        return state.Ok
            ? (enabled ? "已开启 AI 插帧（重启游戏生效）" : "已关闭 AI 插帧")
            : "✗ 写驱动配置失败：" + (state.Error ?? "未知原因");
    }

    private static string SetInjectMode(PackActionStep step, LauncherActionContext context)
    {
        if (step.GetBool("enabled") is not { } enabled)
        {
            return "✗ set_inject_mode 需要 enabled 参数";
        }

        if (context.Entry is null)
        {
            return "✗ 没有当前游戏";
        }

        GameCatalog.SetInjectMode(GameCatalog.CreateService(), context.Entry, enabled);
        return enabled ? "已开启注入模式" : "已关闭注入模式";
    }

    /// <summary>set_cmd_launch：切换是否用 CMD 启动游戏（启动器全局开关，没有按游戏的）</summary>
    private static string SetCmdLaunch(PackActionStep step, LauncherActionContext context)
    {
        if (step.GetBool("enabled") is not { } enabled)
        {
            return "✗ set_cmd_launch 需要 enabled 参数";
        }

        AppConfig.StartGameWithCMD = enabled;
        return (enabled ? "已开启" : "已关闭") + " CMD 启动游戏（全局）";
    }

    /// <summary>set_cmd_args：增减 / 完全替换游戏命令行（按空格切词）。mode=add|remove|replace，缺省 replace。</summary>
    private static string SetCmdArgs(PackActionStep step, LauncherActionContext context)
    {
        if (context.GameBiz is not { } biz)
        {
            return "✗ 没有当前游戏";
        }

        string mode = (step.GetString("mode") ?? "replace").Trim().ToLowerInvariant();
        List<string> args = step.GetStringList("args");
        if (args.Count == 0 && mode != "replace")
        {
            return "✗ set_cmd_args 需要 args 参数";
        }

        List<string> current = SplitArgs(AppConfig.GetStartArgument(biz));
        switch (mode)
        {
            case "replace":
                AppConfig.SetStartArgument(biz, string.Join(' ', args));
                return $"命令行已替换为：{(args.Count > 0 ? string.Join(' ', args) : "（空）")}";
            case "add":
            {
                int added = 0;
                foreach (string arg in args)
                {
                    if (!current.Contains(arg, StringComparer.OrdinalIgnoreCase))
                    {
                        current.Add(arg);
                        added++;
                    }
                }

                AppConfig.SetStartArgument(biz, string.Join(' ', current));
                return $"命令行新增 {added} 个参数（现有 {current.Count} 个）";
            }
            case "remove":
            {
                int removed = current.RemoveAll(c => args.Contains(c, StringComparer.OrdinalIgnoreCase));
                AppConfig.SetStartArgument(biz, string.Join(' ', current));
                return $"命令行移除 {removed} 个参数（剩 {current.Count} 个）";
            }
            default:
                return $"✗ 未知 mode：{mode}（用 add / remove / replace）";
        }
    }

    private static List<string> SplitArgs(string? raw) => string.IsNullOrWhiteSpace(raw)
        ? []
        : [.. raw.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    /// <summary>
    /// set_launch_option：启动页左下那一组开关。key = useXxmiInject / useHoYoShade / useOpenHoYoShade /
    /// useFpsUnlock / useModules / useOptiScaler / useStarward / genshinBlender / zzzBlender。
    /// </summary>
    private static string SetLaunchOption(PackActionStep step, LauncherActionContext context)
    {
        if (context.GameId is not { } gameId || step.GetBool("enabled") is not { } enabled)
        {
            return "✗ set_launch_option 需要 enabled 参数和当前游戏";
        }

        string key = (step.GetString("key") ?? string.Empty).Trim().ToLowerInvariant();
        switch (key)
        {
            case "usexxmiinject": AppConfig.SetUseXxmiInjectLaunchOption(gameId, enabled); break;
            case "usehoyoshade": AppConfig.SetUseHoYoShadeLaunchOption(gameId, enabled); break;
            case "useopenhoyoshade": AppConfig.SetUseOpenHoYoShadeLaunchOption(gameId, enabled); break;
            case "usefpsunlock": AppConfig.SetUseFpsUnlockLaunchOption(gameId, enabled); break;
            case "usemodules": AppConfig.SetUseModulesLaunchOption(gameId, enabled); break;
            case "useoptiscaler": AppConfig.SetUseOptiScalerLaunchOption(gameId, enabled); break;
            default:
                return $"✗ 未知启动选项：{key}";
        }

        return $"启动选项 {key} = {(enabled ? "开" : "关")}";
    }

    // ==================== OptiScaler / 模块 / XXMI ====================

    private static string SetOptiScaler(PackActionStep step, LauncherActionContext context)
    {
        if (context.GameId is not { } gameId || step.GetBool("enabled") is not { } enabled)
        {
            return "✗ set_opt 需要 enabled 参数和当前游戏";
        }

        AppConfig.SetUseOptiScalerLaunchOption(gameId, enabled);
        return enabled ? "已启用 OptiScaler" : "已关闭 OptiScaler";
    }

    /// <summary>
    /// set_opt_build：给当前游戏选择 OptiScaler 构建（build="source/版本"，或 source+version 分开给），
    /// 顺便启用这个游戏的 OptiScaler 启动项。配合 once=true = 导入时选一次，之后用户在 OptiScaler 页可改。
    /// </summary>
    private static string SetOptiScalerBuild(PackActionStep step, LauncherActionContext context)
    {
        if (context.GameId is not { } gameId)
        {
            return "✗ set_opt_build：没有当前游戏";
        }

        string? id = step.GetString("build") ?? step.GetString("id");
        if (string.IsNullOrWhiteSpace(id))
        {
            string? source = step.GetString("source") ?? step.GetString("sourceId");
            string? version = step.GetString("version");
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(version))
            {
                return "✗ set_opt_build：需要 build=\"source/版本\" 或 source+version";
            }

            id = source.Trim().TrimEnd('/') + "/" + version.Trim();
        }

        AppConfig.SetSelectedOptiScalerId(gameId, id);
        AppConfig.SetUseOptiScalerLaunchOption(gameId, true);
        return $"已为当前游戏选择 OptiScaler 构建 {id}";
    }

    /// <summary>
    /// set_module：带 id = 开关那个模块（全局 + 挂到 / 摘出这个游戏的模块列表）；
    /// 不带 id = 开关这个游戏的「启用模块」总选项。
    /// </summary>
    private static string SetModule(PackActionStep step, LauncherActionContext context)
    {
        if (step.GetBool("enabled") is not { } enabled)
        {
            return "✗ set_module 需要 enabled 参数";
        }

        string? moduleId = step.GetString("id") ?? step.GetString("module");
        if (string.IsNullOrWhiteSpace(moduleId))
        {
            if (context.GameId is null)
            {
                return "✗ 没有当前游戏";
            }

            AppConfig.SetUseModulesLaunchOption(context.GameId, enabled);
            return enabled ? "已启用模块注入" : "已关闭模块注入";
        }

        AppConfig.SetModuleEnabled(moduleId!, enabled);

        if (context.GameId is { } gameId)
        {
            List<string> keys = [.. (AppConfig.GetUsedModuleKeysOrNull(gameId) ?? [])];
            if (enabled && !keys.Contains(moduleId!, StringComparer.OrdinalIgnoreCase))
            {
                keys.Add(moduleId!);
                AppConfig.SetUsedModuleKeys(gameId, keys);
            }
            else if (!enabled)
            {
                keys.RemoveAll(k => string.Equals(k, moduleId, StringComparison.OrdinalIgnoreCase));
                AppConfig.SetUsedModuleKeys(gameId, keys);
            }
        }

        return $"模块 {moduleId} = {(enabled ? "开" : "关")}";
    }

    /// <summary>import_opt_config：把包里的 ini 存成 OptiScaler 配置（OptiScalerPresets）</summary>
    private static string ImportOptConfig(PackActionStep step, LauncherActionContext context)
    {
        string? file = step.GetString("file");
        if (string.IsNullOrWhiteSpace(file))
        {
            return "✗ import_opt_config 需要 file 参数";
        }

        string? source = ResolvePackFile(context, file!);
        if (source is null)
        {
            return $"✗ 包里找不到 {file}";
        }

        string name = step.GetString("name") ?? Path.GetFileNameWithoutExtension(file!);
        return OptiScalerPresets.ImportFile(source, name)
            ? "已导入 OptiScaler 配置：" + name
            : "✗ 配置导入失败（重名 / 读不了 / 写不进配置目录）";
    }

    /// <summary>update_opt_config：把当前游戏生效的那份 OptiScaler 配置回抓成预设（CaptureFromBuild）</summary>
    private static string UpdateOptConfig(PackActionStep step, LauncherActionContext context)
    {
        string? name = step.GetString("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return "✗ update_opt_config 需要 name 参数（存成哪个配置名）";
        }

        string? dll = AppConfig.GetSelectedOptiScalerDll(context.GameId);
        string? buildDir = dll is null ? null : Path.GetDirectoryName(Path.GetFullPath(dll));
        if (string.IsNullOrWhiteSpace(buildDir) || !Directory.Exists(buildDir))
        {
            return "✗ 这个游戏没选 OptiScaler 构建";
        }

        return OptiScalerPresets.CaptureFromBuild(buildDir, name!)
            ? "已把当前配置回抓为：" + name
            : "✗ 回抓失败（构建目录读不了）";
    }

    /// <summary>
    /// set_opt_config：把一份已导入的 OptiScaler 配置选成当前游戏生效的配置 ——
    /// 等于 OptiScaler 页下拉里手动选它：记选择（opti_preset_&lt;游戏&gt;_&lt;构建&gt; + follow 键）、
    /// 写 profiles\&lt;游戏&gt;.ini 并激活成主 ini。要在 set_opt_build 和 import_opt_config 之后跑。
    /// </summary>
    private static string SetOptConfig(PackActionStep step, LauncherActionContext context)
    {
        string? name = step.GetString("name") ?? step.GetString("preset");
        if (string.IsNullOrWhiteSpace(name))
        {
            return "✗ set_opt_config 需要 name 参数";
        }

        if (context.GameId is not { } gameId)
        {
            return "✗ set_opt_config：没有当前游戏";
        }

        string? buildId = AppConfig.GetSelectedOptiScalerId(gameId);
        if (string.IsNullOrWhiteSpace(buildId))
        {
            return "✗ 这个游戏还没选 OptiScaler 构建（先跑 set_opt_build）";
        }

        string? dll = AppConfig.GetSelectedOptiScalerDll(gameId);
        string? buildDir = dll is null ? null : Path.GetDirectoryName(Path.GetFullPath(dll));
        if (string.IsNullOrWhiteSpace(buildDir) || !Directory.Exists(buildDir))
        {
            return "✗ OptiScaler 构建目录不存在：" + buildId;
        }

        string gameKey = gameId.GameBiz.ToString();
        // 与 OptiScalerPage.PresetKey 同规则：opti_preset_<游戏>_<构建id>
        AppConfig.SetValue(name, $"opti_preset_{gameKey}_{buildId}");
        AppConfig.SetValue(name, OptiScalerPresets.FollowKey(gameKey));

        return OptiScalerPresets.Apply(buildDir, gameKey, name!)
            ? $"OptiScaler 配置 → {name}（已写入 profiles 并激活）"
            : $"✗ 套用配置失败（presets 目录里没有 {name}.ini？）";
    }

    /// <summary>set_opt_dll：改 OptiScaler 注入 dll 的名字（默认 OptiScaler.dll → 复制成 &lt;名字&gt;.dll 再注）</summary>
    private static string SetOptDll(PackActionStep step, LauncherActionContext context)
    {
        if (context.GameId is null)
        {
            return "✗ 没有当前游戏";
        }

        string? name = step.GetString("name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return "✗ set_opt_dll 需要 name 参数";
        }

        AppConfig.SetOptiScalerDllName(context.GameId, name!);
        return "OptiScaler 注入 dll 名 → " + name;
    }

    /// <summary>import_xxmi：把包里的 zip / 文件夹导成这个游戏的 XXMI 模型</summary>
    private static string ImportXxmi(PackActionStep step, LauncherActionContext context)
    {
        string? file = step.GetString("file");
        if (string.IsNullOrWhiteSpace(file))
        {
            return "✗ import_xxmi 需要 file 参数（zip 或文件夹）";
        }

        string? source = ResolvePackFile(context, file!);
        if (source is null && Directory.Exists(Path.Combine(context.PackRoot ?? string.Empty, file!)))
        {
            source = Path.Combine(context.PackRoot!, file!);
        }

        if (source is null)
        {
            return $"✗ 包里找不到 {file}";
        }

        string? instance = XxmiLocator.FindInstance(
            context.GameBiz?.Value,
            context.Entry?.DisplayName,
            out string? _);
        if (instance is null)
        {
            return "✗ 找不到这个游戏的 XXMI 实例（先装好 / 定位 XXMI）";
        }

        string modsDirectory = XxmiLocator.ModsDirectory(instance);
        try
        {
            string name = source.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                ? XxmiModManager.ImportZip(modsDirectory, source)
                : XxmiModManager.ImportFolder(modsDirectory, source);
            return "已导入 XXMI 模型：" + name;
        }
        catch (Exception ex)
        {
            return "✗ XXMI 导入失败：" + ex.Message;
        }
    }

    // ==================== 其余 ====================

    /// <summary>switch_dll：换插件运行时 dll 家族版本（streamline / dlssnr），下载装进当前插件目录</summary>
    private static async Task<string> SwitchDllAsync(PackActionStep step, LauncherActionContext context)
    {
        string? familyId = step.GetString("family");
        if (string.IsNullOrWhiteSpace(familyId))
        {
            return "✗ switch_dll 需要 family 参数（streamline / dlssnr）";
        }

        DllFamily? family = DllComponentCatalog.FamilyOf(familyId!);
        if (family is null)
        {
            return $"✗ 不认识 dll 家族：{familyId}";
        }

        string? addonDir = context.Plugins?.AddonDirectory ?? context.Host?.AddonsPath;
        if (string.IsNullOrWhiteSpace(addonDir))
        {
            return "✗ 没有插件目录";
        }

        var catalog = await DllComponentCatalog.LoadAsync();
        IReadOnlyList<DllComponent> versions = catalog.Of(family.Id);
        if (versions.Count == 0)
        {
            return "✗ 拉不到组件清单（网络不通？）";
        }

        DllComponent chosen;
        string? wanted = step.GetString("version");
        if (!string.IsNullOrWhiteSpace(wanted))
        {
            chosen = versions.FirstOrDefault(v =>
                string.Equals(v.Version, wanted, StringComparison.OrdinalIgnoreCase))
                ?? versions[0];
        }
        else
        {
            chosen = versions.FirstOrDefault(v =>
                string.Equals(v.Version, family.PreferredVersion, StringComparison.OrdinalIgnoreCase))
                ?? versions[0];
        }

        var progress = new Progress<DownloadProgress>(p =>
            context.Report?.Invoke($"下载 {family.DisplayName} {chosen.Version}… "
                + (p.Percent is { } percent ? $"{percent:F0}%" : string.Empty)));

        DllInstallResult install = await DllInstaller.InstallAsync(addonDir, chosen, progress);
        if (!install.Ok)
        {
            return "✗ 安装失败：" + install.Error;
        }

        AppConfig.SetInstalledDllVariant(family.Id, chosen.Version);
        return $"已切换 {family.DisplayName} → {chosen.Version}（重启游戏生效）";
    }

    /// <summary>set_inject_delay：target = plugin|module|opt；秒数留空 = 清除（跟随全局）</summary>
    private static string SetInjectDelay(PackActionStep step, LauncherActionContext context)
    {
        string target = (step.GetString("target") ?? string.Empty).Trim().ToLowerInvariant();
        int? seconds = step.GetNumber("seconds") is { } value ? (int)Math.Clamp(value, 0, 60) : null;
        string text = seconds is { } s ? $"{s} 秒" : "跟随全局";

        switch (target)
        {
            case "plugin" or "shade" or "reshade":
                if (context.GameBiz is not { } biz)
                {
                    return "✗ 没有当前游戏";
                }

                AppConfig.SetShadeInjectDelaySeconds(biz, seconds);
                return $"插件注入时机 → {text}";
            case "module":
                string? moduleId = step.GetString("id") ?? step.GetString("module");
                if (string.IsNullOrWhiteSpace(moduleId))
                {
                    return "✗ module 目标需要 id 参数";
                }

                AppConfig.SetModuleInjectDelaySeconds(moduleId!, seconds);
                return $"模块 {moduleId} 注入时机 → {text}";
            case "opt" or "optiscaler":
                if (context.GameBiz is not { } biz2)
                {
                    return "✗ 没有当前游戏";
                }

                AppConfig.SetOptiScalerInjectDelaySeconds(biz2, seconds);
                return $"OptiScaler 注入时机 → {text}";
            default:
                return "✗ target 需要 plugin / module / opt";
        }
    }

    /// <summary>set_game_setting：key = fps_target(int) / start_argument(串) / use_popup_window(布尔)</summary>
    private static string SetGameSetting(PackActionStep step, LauncherActionContext context)
    {
        string key = (step.GetString("key") ?? string.Empty).Trim().ToLowerInvariant();
        switch (key)
        {
            case "fps_target":
            {
                if (context.GameId is not { } gameId)
                {
                    return "✗ 没有当前游戏";
                }

                int? value = step.GetNumber("value") is { } v ? (int)v : null;
                if (value is null)
                {
                    return "✗ fps_target 需要数字 value";
                }

                AppConfig.SetFpsUnlockTarget(gameId, Math.Clamp(value.Value, 60, 1000));
                return $"帧率解锁目标 → {Math.Clamp(value.Value, 60, 1000)} fps";
            }
            case "start_argument":
                if (context.GameBiz is not { } biz)
                {
                    return "✗ 没有当前游戏";
                }

                AppConfig.SetStartArgument(biz, step.GetString("value"));
                return "启动参数已写入";
            case "use_popup_window":
                if (context.GameBiz is not { } biz2 || step.GetBool("value") is not { } popup)
                {
                    return "✗ use_popup_window 需要布尔 value 和当前游戏";
                }

                AppConfig.SetUsePopupWindow(biz2, popup);
                return $"弹窗模式 = {(popup ? "开" : "关")}";
            default:
                return $"✗ 未知设置项：{key}（支持 fps_target / start_argument / use_popup_window）";
        }
    }

    /// <summary>
    /// set_window_mode：设置游戏栏窗口模式。value = fullscreen / borderless / windowed；
    /// borderless = 关全屏（Unity registry）+ 无边框启动参数，fullscreen / windowed 同理。
    /// only_if = fullscreen|windowed|borderless 时仅当当前模式匹配才改 —— 配合**不带 once**
    /// 每次启动执行，即为"禁止全屏：全屏才改无边框，其余不动"。
    /// </summary>
    private static string SetWindowMode(PackActionStep step, LauncherActionContext context)
    {
        if (context.GameBiz is not { } biz)
        {
            return "✗ set_window_mode：没有当前游戏";
        }

        string? value = step.GetString("value");
        if (string.IsNullOrWhiteSpace(value))
        {
            return "✗ set_window_mode：需要 value=fullscreen|borderless|windowed";
        }

        var current = GameSettingService.GetGameResolutionSetting(biz);
        bool curFull = current?.IsFullScreen == true;
        bool curPopup = AppConfig.GetUsePopupWindow(biz);

        string? onlyIf = step.GetString("only_if");
        if (!string.IsNullOrWhiteSpace(onlyIf))
        {
            bool matches = onlyIf.Trim().ToLowerInvariant() switch
            {
                "fullscreen" or "full" => curFull,
                "windowed" or "window" => !curFull && !curPopup,
                "borderless" => !curFull && curPopup,
                _ => false,
            };
            if (!matches)
            {
                return $"set_window_mode：当前模式不满足 only_if={onlyIf}，跳过";
            }
        }

        bool full; bool popup;
        switch (value.Trim().ToLowerInvariant())
        {
            case "fullscreen": full = true; popup = false; break;
            case "borderless": full = false; popup = true; break;
            case "windowed": full = false; popup = false; break;
            default: return $"✗ set_window_mode：不认识 value={value}";
        }

        if (full == curFull && popup == curPopup)
        {
            return "set_window_mode：已是目标模式，无改动";
        }

        if (current is not null)
        {
            current.IsFullScreen = full;
            GameSettingService.SetGameResolutionSetting(biz, current);
        }

        AppConfig.SetUsePopupWindow(biz, popup);
        return $"窗口模式 → {value.Trim().ToLowerInvariant()}（registry 全屏标志 + 无边框启动参数已更新）";
    }

    /// <summary>override_files：把包里的目录覆盖到目标。target = game（游戏目录）| launcher（启动器目录）。</summary>
    private static string OverrideFiles(PackActionStep step, LauncherActionContext context)
    {
        string target = (step.GetString("target") ?? "game").Trim().ToLowerInvariant();
        string folder = step.GetString("folder") ?? (target == "launcher"
            ? GameAddonPackUserContent.LauncherFilesFolderName
            : GameAddonPackUserContent.GameFilesFolderName);

        string? targetRoot = target switch
        {
            "game" => context.Entry?.GameDirectory,
            "launcher" => AppContext.BaseDirectory,
            _ => null,
        };
        if (string.IsNullOrWhiteSpace(targetRoot))
        {
            return $"✗ 不知道{(target == "launcher" ? "启动器" : "游戏")}目录在哪";
        }

        int copied = GameAddonPackUserContent.OverlayFiles(context.PackRoot, folder, targetRoot);
        return copied > 0 ? $"已覆盖 {copied} 个文件到{target switch { "game" => "游戏", "launcher" => "启动器", _ => target }}目录"
            : "✗ 没有可覆盖的文件（包里没有 " + folder + "\\）";
    }

    // ==================== ReShade 滤镜 ====================

    private static string HostShadersDirectory(LauncherActionContext context)
        => context.Host is { } host ? Path.Combine(host.RootPath, "reshade-shaders") : string.Empty;

    /// <summary>import_shader：把包里的 .fx 复制进宿主 reshade-shaders 目录（EffectSearchPaths 指的那里）</summary>
    private static string ImportShader(PackActionStep step, LauncherActionContext context)
    {
        string? file = step.GetString("file");
        if (string.IsNullOrWhiteSpace(file))
        {
            return "✗ import_shader 需要 file 参数";
        }

        string? source = ResolvePackFile(context, file!);
        string shaders = HostShadersDirectory(context);
        if (source is null || shaders.Length == 0)
        {
            return source is null ? $"✗ 包里找不到 {file}" : "✗ 没有 HoYoShade 宿主";
        }

        try
        {
            string target = Path.Combine(shaders, Path.GetFileName(source));
            Directory.CreateDirectory(shaders);
            File.Copy(source, target, overwrite: true);
            return "已导入滤镜：" + Path.GetFileName(target);
        }
        catch (Exception ex)
        {
            return "✗ 滤镜导入失败：" + ex.Message;
        }
    }

    /// <summary>set_shader：开关当前 ReShade 预设里的 technique（"Name@file.fx" 或裸 technique 名）</summary>
    private static string SetShader(PackActionStep step, LauncherActionContext context)
    {
        string? name = step.GetString("name") ?? step.GetString("technique");
        if (string.IsNullOrWhiteSpace(name) || step.GetBool("enabled") is not { } enabled)
        {
            return "✗ set_shader 需要 name 和 enabled 参数";
        }

        if (context.Entry?.ReShadeIniPath is not { } ini || !File.Exists(ini))
        {
            return "✗ 这个游戏还没有 ReShade.ini";
        }

        string? presetPath = ReShadeProfile.Load(ini).ResolvePresetPath();
        if (presetPath is null || !File.Exists(presetPath))
        {
            return "✗ 当前预设不存在（先在游戏里 / 预设管理里选好预设）";
        }

        try
        {
            IniDocument preset = IniDocument.Load(presetPath);
            bool changed = enabled ? EnableTechnique(preset, name!) : DisableTechnique(preset, name!);
            if (changed)
            {
                preset.Save(presetPath);
            }

            return $"{(changed ? "已" : "本来就在目标状态，")}{(enabled ? "开启" : "关闭")} technique {name}";
        }
        catch (Exception ex)
        {
            return "✗ 写预设失败：" + ex.Message;
        }
    }

    /// <summary>delete_shader：删掉宿主 reshade-shaders 里的 .fx，并从当前预设摘掉它的 technique</summary>
    private static string DeleteShader(PackActionStep step, LauncherActionContext context)
    {
        string? name = step.GetString("name") ?? step.GetString("file");
        if (string.IsNullOrWhiteSpace(name))
        {
            return "✗ delete_shader 需要 name 参数（.fx 文件名）";
        }

        string fxName = name!.EndsWith(".fx", StringComparison.OrdinalIgnoreCase) ? name! : name! + ".fx";
        int deleted = 0;
        string shaders = HostShadersDirectory(context);
        if (shaders.Length > 0 && Directory.Exists(shaders))
        {
            foreach (string file in Directory.EnumerateFiles(shaders, fxName, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch
                {
                    // 占用中就跳过
                }
            }
        }

        // 预设里的 technique 一并摘掉（按 @文件名 认）
        string presetNote = string.Empty;
        if (context.Entry?.ReShadeIniPath is { } ini && File.Exists(ini))
        {
            string? presetPath = ReShadeProfile.Load(ini).ResolvePresetPath();
            if (presetPath is not null && File.Exists(presetPath))
            {
                try
                {
                    IniDocument preset = IniDocument.Load(presetPath);
                    List<string> all = SplitList(preset.GetValue(IniDocument.RootSection, "Techniques"));
                    List<string> kept = [.. all.Where(e => !MatchesEffectFile(e, fxName))];
                    if (kept.Count != all.Count)
                    {
                        preset.SetValue(IniDocument.RootSection, "Techniques", string.Join(',', kept));
                        preset.Save(presetPath);
                        presetNote = "，预设里的 technique 已摘";
                    }
                }
                catch
                {
                    // 预设写不动就算了
                }
            }
        }

        return deleted > 0 ? $"已删除滤镜 {fxName}{presetNote}" : $"✗ reshade-shaders 里没有 {fxName}";
    }

    private static bool EnableTechnique(IniDocument preset, string technique)
    {
        bool changed = false;
        List<string> techniques = SplitList(preset.GetValue(IniDocument.RootSection, "Techniques"));
        if (!techniques.Any(e => EntryMatches(e, technique)))
        {
            techniques.Add(technique);
            preset.SetValue(IniDocument.RootSection, "Techniques", string.Join(',', techniques));
            changed = true;
        }

        List<string> sorting = SplitList(preset.GetValue(IniDocument.RootSection, "TechniqueSorting"));
        if (!sorting.Any(e => EntryMatches(e, technique)))
        {
            sorting.Add(technique);
            preset.SetValue(IniDocument.RootSection, "TechniqueSorting", string.Join(',', sorting));
            changed = true;
        }

        return changed;
    }

    private static bool DisableTechnique(IniDocument preset, string technique)
    {
        string? raw = preset.GetValue(IniDocument.RootSection, "Techniques");
        if (raw is null)
        {
            return false;
        }

        List<string> all = SplitList(raw);
        List<string> kept = [.. all.Where(e => !EntryMatches(e, technique))];
        if (kept.Count == all.Count)
        {
            return false;
        }

        preset.SetValue(IniDocument.RootSection, "Techniques", string.Join(',', kept));
        return true;
    }

    /// <summary>technique 条目匹配：全等、或用户给的名字是条目 @ 前缀部分 / 条目 @ 后面的 .fx 文件名</summary>
    private static bool EntryMatches(string entry, string name)
    {
        string trimmed = entry.Trim();
        return string.Equals(trimmed, name, StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith(name + "@", StringComparison.OrdinalIgnoreCase)
               || MatchesEffectFile(trimmed, name);
    }

    private static bool MatchesEffectFile(string entry, string fxName)
    {
        int at = entry.LastIndexOf('@');
        return at >= 0 && string.Equals(entry[(at + 1)..].Trim(), fxName, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> SplitList(string? raw) => string.IsNullOrWhiteSpace(raw)
        ? []
        : [.. raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    // ==================== 兼容性检测 / 注册表加游戏 ====================

    /// <summary>run_compat_check：跑 DLSS5 兼容性检测。交互模式弹结果摘要；启动前自动跑只汇总到日志。</summary>
    private static async Task<string> RunCompatCheckAsync(PackActionStep step, LauncherActionContext context)
    {
        ShadeHost? host = context.Host ?? PluginHostLocator.Resolve(out _);

        GamePluginService? plugins = context.Plugins;
        if (context.Entry is not null)
        {
            try
            {
                plugins = GamePluginServiceFactory.Create(context.Entry, host);
            }
            catch
            {
                // 用上下文里那份
            }
        }

        var compatContext = new Dlss5CompatContext
        {
            ShadeHost = host,
            Game = context.Entry,
            GameId = context.GameId,
            Profile = plugins?.Profile,
            ProfileError = plugins?.ProfileError,
            AddonStates = plugins?.GetAddons(),
            HookPoint = plugins?.GetHookPoint() ?? 0,
            PluginService = plugins,
            Delivery = Dlss5CompatContext.ResolveDelivery(context.GameId, host),
            OptiScalerDllPath = AppConfig.GetSelectedOptiScalerDll(context.GameId),
        };

        List<Dlss5CompatItem> items = await Dlss5CompatibilityCheck.RunAsync(compatContext);
        List<Dlss5CompatItem> problems = [.. items.Where(i => i.Level is Dlss5CheckLevel.Warning or Dlss5CheckLevel.Error)];
        string summary = problems.Count == 0
            ? $"兼容性检测通过（{items.Count} 项全绿）"
            : $"兼容性检测：{problems.Count} 项要注意（共 {items.Count} 项）";

        if (context.Interactive && context.XamlRoot is not null)
        {
            var lines = new StringBuilder();
            foreach (Dlss5CompatItem item in items)
            {
                string mark = item.Level switch
                {
                    Dlss5CheckLevel.Ok => "✓",
                    Dlss5CheckLevel.Warning => "⚠",
                    Dlss5CheckLevel.Error => "✗",
                    _ => "·",
                };
                lines.AppendLine($"{mark} {item.HeaderText}  {item.Message}");
            }

            var scroll = new ScrollViewer
            {
                Content = new TextBlock
                {
                    Text = lines.ToString(),
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.Wrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12,
                },
            };
            var dialog = new ContentDialog
            {
                XamlRoot = context.XamlRoot,
                Title = "DLSS5 兼容性检测结果",
                Content = scroll,
                CloseButtonText = "关闭",
                MaxWidth = 560,
            };
            await dialog.ShowAsync();
        }

        return summary;
    }

    /// <summary>
    /// add_game_from_registry：从注册表读游戏目录，找到主程序 exe 并加进游戏列表。
    /// 参数：key（注册表完整路径，如 HKEY_LOCAL_MACHINE\SOFTWARE\...）、valueName（默认 GameInstallPath）、
    /// exe（可选，目录下主程序名；不给就挑目录下第一个 exe）。
    /// </summary>
    private static string AddGameFromRegistry(PackActionStep step, LauncherActionContext context)
    {
        string? key = step.GetString("key");
        if (string.IsNullOrWhiteSpace(key))
        {
            return "✗ add_game_from_registry 需要 key 参数（注册表完整路径）";
        }

        string? valueName = step.GetString("valueName") ?? "GameInstallPath";
        string? directory;
        try
        {
            directory = Registry.GetValue(key!, valueName, null) as string;
        }
        catch (Exception ex)
        {
            return "✗ 读注册表失败：" + ex.Message;
        }

        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return $"✗ 注册表 {key} 的 {valueName} 不是有效目录";
        }

        string? exe = null;
        string? exeHint = step.GetString("exe");
        if (!string.IsNullOrWhiteSpace(exeHint))
        {
            string candidate = Path.Combine(directory, exeHint!);
            if (File.Exists(candidate))
            {
                exe = candidate;
            }
        }

        exe ??= Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
        if (exe is null)
        {
            return $"✗ {directory} 下找不到 exe";
        }

        try
        {
            GameDiscoveryService service = GameCatalog.CreateService();
            AddCustomResult result = service.AddCustom(exe);
            if (result.Entry is not { } added)
            {
                return result.Added ? "✗ 添加失败" : ("✗ " + (result.Error ?? "这个游戏已经在列表里了"));
            }

            GameCatalog.RegisterCustomGame(added);
            WeakReferenceMessenger.Default.Send(new CustomGameAddedMessage());
            return "已添加游戏：" + added.DisplayName + "（" + exe + "）";
        }
        catch (Exception ex)
        {
            return "✗ 添加游戏失败：" + ex.Message;
        }
    }

    // ==================== 公共工具 ====================

    /// <summary>包内文件解析：相对路径按包根找，绝对路径原样校验存在性</summary>
    private static string? ResolvePackFile(LauncherActionContext context, string relativeOrAbsolute)
    {
        try
        {
            if (Path.IsPathFullyQualified(relativeOrAbsolute))
            {
                return File.Exists(relativeOrAbsolute) ? relativeOrAbsolute : null;
            }

            if (context.PackRoot is null)
            {
                return null;
            }

            string candidate = Path.GetFullPath(Path.Combine(context.PackRoot, relativeOrAbsolute));
            return File.Exists(candidate) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }
}

