namespace HoYoShadeHub.Extensions.Games;

/// <summary>Only these game families disallow driver AI interpolation alongside OptiScaler.</summary>
public static class OptiSmoothMotionPolicy
{
    public sealed record DriverState(bool Known, uint Value, string? Error = null);
    public sealed record Result(bool Changed, bool ConfirmedOff, string? Note = null);

    public static bool Applies(string? gameBiz, bool optEnabled)
    {
        string family = (gameBiz ?? "").Split('_')[0];
        return optEnabled && family is "hk4e" or "hkrpg" or "nap";
    }

    // Delegates keep tests independent of the user's real NVIDIA driver configuration.
    public static Result EnsureDisabled(string? gameBiz, bool optEnabled, string? exeName,
        Func<string, DriverState> read, Func<DriverState> readGlobal, Func<string, DriverState> disable)
    {
        if (!Applies(gameBiz, optEnabled)) return new(false, false);
        if (string.IsNullOrWhiteSpace(exeName)) return new(false, false, "没有游戏 EXE，尚未核实 AI 插帧状态。");
        DriverState state = read(exeName);
        if (!state.Known)
        {
            // A missing per-EXE profile may inherit a globally enabled driver setting.
            DriverState global = readGlobal();
            if (!global.Known || global.Value == 0)
                return new(false, false, state.Error ?? "驱动状态不可读，尚未核实 AI 插帧状态。");
        }
        else if (state.Value == 0) return new(false, true);

        DriverState written = disable(exeName);
        if (!written.Known || written.Value != 0)
            throw new InvalidOperationException("OptiScaler 要求关闭此游戏的 AI 插帧，但驱动写入失败：" + written.Error);
        DriverState verified = read(exeName);
        if (!verified.Known || verified.Value != 0)
            throw new InvalidOperationException("此游戏的 AI 插帧关闭后回读未通过：" + verified.Error);
        return new(true, true, "已关闭当前游戏的 AI 插帧；未修改全局设置或 OptiScaler 帧生成。");
    }
}
