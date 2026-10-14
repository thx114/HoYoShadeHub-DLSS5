using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Features.GameLauncher;
using HoYoShadeHub.Helpers;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

internal static class OptiSmoothMotionInterlock
{
    // All launcher Smooth Motion writes use this gate, including imported pack actions.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static bool IsBlocked(GameId? game, bool optEnabled) =>
        OptiSmoothMotionPolicy.Applies(game?.GameBiz.Value, optEnabled);

    public static async Task<T> RunSerializedAsync<T>(Func<T> work)
    {
        await Gate.WaitAsync();
        try { return await Task.Run(work); }
        finally { Gate.Release(); }
    }

    public static Task<OptiSmoothMotionPolicy.Result> EnsureAsync(GameId? game, string? exePath, bool optEnabled)
    {
        // Freeze identity before awaiting; switching the selected game must not retarget a write.
        string? biz = game?.GameBiz.Value;
        if (!OptiSmoothMotionPolicy.Applies(biz, optEnabled))
            return Task.FromResult(new OptiSmoothMotionPolicy.Result(false, false));
        string? exe = string.IsNullOrWhiteSpace(exePath) ? GameLauncherService.GetGameExeName(game!.GameBiz) : Path.GetFileName(exePath);
        return RunSerializedAsync(() =>
        {
            var result = OptiSmoothMotionPolicy.EnsureDisabled(biz, optEnabled, exe,
                name => Map(NvDrsInterop.ReadSmoothMotionEnable(name)),
                () => Map(NvDrsInterop.ReadGlobal(NvDrsInterop.SmoothMotionEnableSettingId, NvDrsInterop.SmoothMotionSettingName, NvDrsInterop.DescribeBinary)),
                name => Map(NvDrsInterop.WriteSmoothMotionEnable(name, false)));
            var logger = AppConfig.GetLogger<LogToken>();
            if (result.Changed) logger.LogInformation("Opti AI interlock: {Game} {Exe}: OFF", biz, exe);
            else if (!result.ConfirmedOff) logger.LogWarning("Opti AI interlock: {Game} {Exe}: unverified: {Note}", biz, exe, result.Note);
            return result;
        });
    }

    public static void OnOptiEnabled(GameId game)
    {
        if (!IsBlocked(game, true)) return;
        var identity = new GameId { Id = game.Id, GameBiz = game.GameBiz };
        _ = ApplyOnEnableAsync(identity);
    }

    private static async Task ApplyOnEnableAsync(GameId game)
    {
        try
        {
            var result = await EnsureAsync(game, null, AppConfig.GetUseOptiScalerLaunchOption(game));
            if (result.Changed)
                InAppToast.MainWindow?.Success("OptiScaler", "已自动关闭此游戏的 AI 插帧，重启游戏生效。", 8000);
            else if (!result.ConfirmedOff && !string.IsNullOrWhiteSpace(result.Note))
                InAppToast.MainWindow?.Warning("AI 插帧状态未确认", result.Note, 10000);
        }
        catch (Exception ex)
        {
            AppConfig.GetLogger<LogToken>().LogWarning(ex, "Disable AI interpolation when enabling Opti: {Game}", game.GameBiz);
            InAppToast.MainWindow?.Error("AI 插帧未关闭", ex.Message, 12000);
        }
    }

    private static OptiSmoothMotionPolicy.DriverState Map(NvDrsInterop.State state) => new(state.Ok, state.Value, state.Error);
    private sealed class LogToken { }
}
