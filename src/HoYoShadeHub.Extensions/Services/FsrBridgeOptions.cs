using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>FSR 桥（Dx11FsrBridge）卡片展开配置的形态</summary>
public enum FsrBridgeOptionKind { Bool, Option, Number }

/// <summary>FSR 桥一项配置（<c>Dx11FsrBridge.ini</c> 的 <c>[Dx11FsrBridge]</c> 段）</summary>
public sealed record FsrBridgeDescriptor(
    string Key,
    string Label,
    string ToolTip,
    FsrBridgeOptionKind Kind,
    (string Value, string Label)[]? Choices = null,
    double NumberMin = 0,
    double NumberMax = 100000,
    bool NumberInteger = false,
    string DefaultValue = "0");

/// <summary>
/// FSR 桥模块卡片「扩展配置」清单。桥从 DLL 同目录的 <c>Dx11FsrBridge.ini</c> 读这些键，
/// 值是 0/1 / 整数 / 浮点（<strong>不是</strong> OptiScaler 的 auto/true/false —— 写 auto 会被
/// <c>GetPrivateProfileInt</c> 当成 0）。所以开关是二态：勾选写 1，不勾写 0。
/// </summary>
public static class FsrBridgeOptions
{
    private static (string, string)[] C(params (string Value, string Label)[] items) => items;

    public const string IniFileName = "Dx11FsrBridge.ini";
    public const string Section = "Dx11FsrBridge";

    public static IReadOnlyList<FsrBridgeDescriptor> Descriptors { get; } =
    [
        new("Enabled", "桥总开关", "桥自身的总开关：0 = 完全旁路（等于没装），1 = 接管游戏原生 FSR。", FsrBridgeOptionKind.Bool, null, 0, 1, true, "1"),
        new("Ffx12DepthInverted", "深度反转", "深度缓冲是反向 Z（1=近 0=远）时打开。默认开启。", FsrBridgeOptionKind.Bool, DefaultValue: "1"),
        new("Ffx12DepthFlipY", "深度上下翻转", "深度缓冲上下颠倒时打开（给 FSR 的输入翻回正方向）。", FsrBridgeOptionKind.Bool),
        new("Ffx12MotionFlipY", "运动矢量上下翻转", "运动矢量 Y 方向与约定相反时打开。", FsrBridgeOptionKind.Bool),
        new("Fsr2MotionVectorsJittered", "运动矢量已含抖动", "游戏原生运动矢量已经包含 jitter 时打开（默认 0 = 未含，由桥补 jitter）。", FsrBridgeOptionKind.Bool),
        new("Fsr2MotionVectorScaleMode", "运动矢量方向", "运动矢量符号约定：0 = 反方向，1 = 正方向。", FsrBridgeOptionKind.Option, C(("0", "0（反方向）"), ("1", "1（正方向）")), 0, 1, true, "1"),
        new("Ffx12AutoExposure", "自动曝光", "从画面估计曝光（默认 1）。关闭则使用游戏上报的曝光值。", FsrBridgeOptionKind.Bool, null, 0, 1, true, "1"),
        new("Ffx12JitterMode", "jitter 符号修正", "游戏原生 jitter 读取的符号修正：0-2 = 单符号翻转组合，3 = 双符号翻转（本游戏约定）。", FsrBridgeOptionKind.Option, C(("0", "0"), ("1", "1"), ("2", "2"), ("3", "3（双翻转）")), 0, 3, true, "3"),
        new("Ffx12JitterDelay", "jitter 延迟一帧", "使用上一帧的 jitter（默认关闭）。", FsrBridgeOptionKind.Bool),
        new("Ffx12ForceReset", "每帧强制重置", "每帧强制重置 FSR 内部状态（排障用，默认 0）。", FsrBridgeOptionKind.Bool),
        new("Fsr2FamilySkip", "跳过原生预处理", "跳过游戏原生 FSR 冗余预处理（默认关闭）。", FsrBridgeOptionKind.Bool),
        new("Fsr2FamilyExpireMs", "合成族过期毫秒", "合成族状态多久没有新提交就算过期（默认 500）。", FsrBridgeOptionKind.Number, null, 100, 5000, true, "500"),
        new("Fsr2SharpnessPercent", "RCAS 锐化 %", "FSR RCAS 锐化强度 0-100（默认 0 = 不额外锐化）。", FsrBridgeOptionKind.Number, null, 0, 100, true, "0"),
    ];

    /// <summary>读当前值（Trim 后）；键不存在返回 null（按 DefaultValue 展示）。</summary>
    public static string? ReadValue(IniDocument ini, FsrBridgeDescriptor descriptor)
        => ini.GetValue(Section, descriptor.Key);

    /// <summary>写值并落盘；失败（文件被占用等）返回 false。</summary>
    public static bool WriteValue(string iniPath, FsrBridgeDescriptor descriptor, string value)
    {
        try
        {
            IniDocument ini = File.Exists(iniPath) ? IniDocument.Load(iniPath) : IniDocument.CreateEmpty();
            ini.SetValue(Section, descriptor.Key, value);
            ini.Save(iniPath);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
