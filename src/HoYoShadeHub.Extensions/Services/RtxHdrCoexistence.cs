namespace HoYoShadeHub.Extensions.Services;

/// <summary>RTX HDR 开关和「显示器有没有开 Windows HDR」搭配起来是什么结论。</summary>
public enum RtxHdrVerdict
{
    /// <summary>RTX HDR 明确没开 —— 不用管。</summary>
    NotEnabled,

    /// <summary>RTX HDR 开着、系统 HDR 也开着 —— 能用，但会和 DLSS5 插件的色调映射叠加。</summary>
    EnabledWithSystemHdr,

    /// <summary>
    /// RTX HDR 开着，但**没有任何显示器开着 Windows HDR**。
    /// NVIDIA 要求显示器的 Windows HDR 打开，RTX HDR 才真的有效果；
    /// 这种情况下开关是「开着」的，画面却一点没变 —— 用户反馈的就是这个。
    /// </summary>
    EnabledWithoutSystemHdr,

    /// <summary>RTX HDR 开着，但读不到显示器 HDR 状态（DisplayConfig 读失败之类）。</summary>
    EnabledWithUnknownSystemHdr,

    /// <summary>驱动里读不到 RTX HDR 开关，但系统 HDR 开着 —— 只能提醒用户自己去 NVIDIA app 确认。</summary>
    UnknownToggleWithSystemHdr,

    /// <summary>驱动里读不到 RTX HDR 开关，系统 HDR 也关着 —— 一般可以放心。</summary>
    UnknownToggleWithoutSystemHdr,
}

/// <summary>
/// 第 4 条（RTX HDR）的判定逻辑，单独拎出来是为了能自测 ——
/// 真正的「显示器 HDR 开没开」由 App 层 <c>DisplayHdrState</c> 读出来喂进来。
/// </summary>
public static class RtxHdrCoexistence
{
    /// <param name="rtxHdrEnabled">RTX HDR 开关：true 开 / false 关 / null 读不到。</param>
    /// <param name="anyDisplayHdrEnabled">有没有任何一台活动显示器开着 Windows HDR：true / false / null 读不到。</param>
    public static RtxHdrVerdict Evaluate(bool? rtxHdrEnabled, bool? anyDisplayHdrEnabled)
    {
        if (rtxHdrEnabled is false)
        {
            return RtxHdrVerdict.NotEnabled;
        }

        if (rtxHdrEnabled is null)
        {
            return anyDisplayHdrEnabled is true
                ? RtxHdrVerdict.UnknownToggleWithSystemHdr
                : RtxHdrVerdict.UnknownToggleWithoutSystemHdr;
        }

        return anyDisplayHdrEnabled switch
        {
            true => RtxHdrVerdict.EnabledWithSystemHdr,
            false => RtxHdrVerdict.EnabledWithoutSystemHdr,
            _ => RtxHdrVerdict.EnabledWithUnknownSystemHdr,
        };
    }
}
