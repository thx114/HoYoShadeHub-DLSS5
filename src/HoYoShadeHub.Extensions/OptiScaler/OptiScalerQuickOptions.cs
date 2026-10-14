using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.OptiScaler;

/// <summary>OptiScaler 卡片展开配置的控件形态</summary>
public enum OptiQuickKind
{
    /// <summary>auto / 开 / 关 三态（写 auto、true、false；OptiScaler 把 auto 当「跟随默认」）</summary>
    TriState,

    /// <summary>从给定取值里选一个</summary>
    Option,

    /// <summary>数字输入</summary>
    Number,
}

/// <summary>一个可选项（Option 形态用；<see cref="Value"/> 是写进 ini 的原文）</summary>
public sealed record OptiQuickChoice(string Value, string Label);

/// <summary>OptiScaler 卡片内快捷配置项的静态描述（节 / 键 / 文案 / 形态）</summary>
public sealed record OptiQuickDescriptor(
    string Section,
    string Key,
    string Label,
    string ToolTip,
    OptiQuickKind Kind,
    OptiQuickChoice[]? Choices = null,
    double NumberMin = 0,
    double NumberMax = 1000,
    bool NumberInteger = false);

/// <summary>
/// OptiScaler 卡片「扩展配置」的清单：都是 OptiScaler 真正认的 ini 键，写进
/// 当前生效的那份配置（挂着命名配置 → 写配置；否则写这个游戏自己的 profile）。
/// </summary>
public static class OptiScalerQuickOptions
{
    private static OptiQuickChoice[] C(params (string Value, string Label)[] items)
        => [.. items.Select(i => new OptiQuickChoice(i.Value, i.Label))];

    public static IReadOnlyList<OptiQuickDescriptor> Descriptors { get; } =
    [
        new("FrameGen", "Enabled", "FG 帧生成",
            "[FrameGen] Enabled：OptiScaler 的帧生成总开关。auto = 跟随游戏内/叠加层设置。改完重启游戏生效。",
            OptiQuickKind.TriState),
        new("FrameGen", "FGInput", "FG Input（帧生成输入）",
            "[FrameGen] FGInput：帧生成吃哪路输入。auto = 默认；DLSSG/NvngxFG = 吃 DLSS-G 输入；FSRFG = 吃 FSR 输入。",
            OptiQuickKind.Option,
            C(("auto", "auto"), ("nofg", "NoFG"), ("upscaler", "Upscaler"), ("nvngxfg", "NVNGX FG"), ("dlssg", "DLSSG"), ("fsrfg", "FSRFG"))),
        new("FrameGen", "FGOutput", "FG Output（帧生成输出）",
            "[FrameGen] FGOutput：用哪家帧生成输出。DLSSG = DLSS-G（配合 AdaMfgUnlock 可解锁）；FSRFG = FSR3；XeFG = XeSS-FG。",
            OptiQuickKind.Option,
            C(("auto", "auto"), ("dlssg", "DLSSG"), ("fsrfg", "FSRFG"), ("xefg", "XeFG"), ("nofg", "NoFG（关闭）"))),
        new("DlssNr", "Enabled", "NR 神经渲染",
            "[DlssNr] Enabled：DLSS 神经渲染（NR）总开关。auto = 跟随叠加层。",
            OptiQuickKind.TriState),
        new("DlssNr", "WorkingScale", "NR 工作精度",
            "NR 工作分辨率比例：0.5 = 宽高各一半；1.0 = 原始分辨率。auto = 默认。",
            OptiQuickKind.Option,
            C(("auto", "auto"), ("1.0", "1.0（原分辨率）"), ("0.75", "0.75"), ("0.67", "0.67"), ("0.5", "0.5（半分辨率）"), ("0.33", "0.33"))),
        new("DLSSG", "AdaMfgUnlock", "Ada MFG 解锁（40 系）",
            "[DLSSG] AdaMfgUnlock：RTX 40 系的 DLSS-G 帧生成解锁。20/30 系请用 AmpereMFG；两者互斥。",
            OptiQuickKind.TriState),
        new("DLSSG", "AmpereMfgUnlock", "Ampere MFG 解锁（20/30 系）",
            "[DLSSG] AmpereMfgUnlock：RTX 20/30 系的 DLSS-G 解锁（SM86/SM75 内核）。40/50 系开了会被加载器拒绝。",
            OptiQuickKind.TriState),
        new("DLSSG", "SmoothMotion", "DLSSG 平滑运动",
            "[DLSSG] SmoothMotion：DLSS-G 平滑运动（降低帧生成抖动）。auto = 默认。",
            OptiQuickKind.TriState),
        new("Upscalers", "Dx11Upscaler", "DX11 超分后端",
            "[Upscalers] Dx11Upscaler：DX11 游戏用哪家超分。auto = 自动选择。",
            OptiQuickKind.Option,
            C(("auto", "auto"), ("dlss", "DLSS"), ("fsr21", "FSR 2.1"), ("fsr22", "FSR 2.2"), ("fsr31", "FSR 3.1"), ("ffx_12", "FFX DX12"), ("xess", "XeSS"))),
        new("Upscalers", "Dx12Upscaler", "DX12 超分后端",
            "[Upscalers] Dx12Upscaler：DX12 游戏用哪家超分。auto = 自动选择。",
            OptiQuickKind.Option,
            C(("auto", "auto"), ("dlss", "DLSS"), ("ffx", "FFX / FSR"), ("xess", "XeSS"))),
        new("Hooks", "HookOriginalNvngxOnly", "只挂钩原版 nvngx",
            "[Hooks] HookOriginalNvngxOnly：true = 只挂钩游戏原版 nvngx 路径，减少与其它注入器的冲突。",
            OptiQuickKind.TriState),
        new("Hooks", "EarlyHooking", "提前挂钩",
            "[Hooks] EarlyHooking：更早挂 hook。某些游戏需要它才能截获输入；出问题就关。",
            OptiQuickKind.TriState),
        new("InitFlags", "AutoExposure", "自动曝光",
            "[InitFlags] AutoExposure：自动曝光采样。画面过曝/偏暗时可切换。",
            OptiQuickKind.TriState),
        new("InitFlags", "DepthInverted", "深度反转",
            "[InitFlags] DepthInverted：深度缓冲是反向（1=近 0=远）时打开。崩铁/绝区零诊断常用。",
            OptiQuickKind.TriState),
        new("InitFlags", "JitterCancellation", "抖动抵消",
            "[InitFlags] JitterCancellation：超分抖动抵消。画面抖动异常时可切换。",
            OptiQuickKind.TriState),
        new("Spoofing", "SpoofHAGS", "伪装 HAGS",
            "[Spoofing] SpoofHAGS：对游戏伪装硬件加速 GPU 计划状态。FG 兼容性排查用。",
            OptiQuickKind.TriState),
        new("DlssNr", "FinishedPicture", "NR 成品图",
            "[DlssNr] FinishedPicture：NR 作用在最终成品画面上（而非超分输入）。auto = 默认。",
            OptiQuickKind.TriState),
        new("DlssNr", "RunBeforeSR", "NR 在超分之前运行",
            "[DlssNr] RunBeforeSR：true = NR 跑在超分前（先降噪再放大）。auto = 默认。",
            OptiQuickKind.TriState),
        new("Framerate", "FramerateLimit", "帧率上限",
            "[Framerate] FramerateLimit：OptiScaler 的帧率限制（0 = 不限）。部分游戏用它防 FG 溢出。",
            OptiQuickKind.Number, null, 0, 480, true),
        new("OutputScaling", "Enabled", "输出缩放",
            "[OutputScaling] Enabled：OptiScaler 输出缩放（后处理锐化/缩放链）。",
            OptiQuickKind.TriState),
    ];

    /// <summary>读当前值；键不存在（= auto 默认）返回 "auto"。</summary>
    public static string ReadValue(string iniText, OptiQuickDescriptor descriptor)
        => OptiScalerRuntime.ReadIniValue(iniText, descriptor.Section, descriptor.Key) ?? "auto";

    /// <summary>把某个键写成给定值，其余字节不动。</summary>
    public static string ApplyValue(string iniText, OptiQuickDescriptor descriptor, string value)
        => OptiScalerRuntime.WriteIniValue(iniText, descriptor.Section, descriptor.Key, value);
}
