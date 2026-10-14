using HoYoShadeHub.Extensions.OptiScaler;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class QuickConfigOptionTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
        }

        // ===== OptiScaler 快捷配置 =====
        Check(OptiScalerQuickOptions.Descriptors.Count == 20, "OptiScaler 快捷项 = 20");
        Check(OptiScalerQuickOptions.Descriptors.Select(d => d.Section + "/" + d.Key).Distinct().Count() == 20,
            "OptiScaler 快捷项节/键无重复");

        const string optiIni = "[FrameGen]\nEnabled = true\nFGInput = upscaler\n\n[DLSSG]\nAdaMfgUnlock = true\n";
        Check(OptiScalerQuickOptions.ReadValue(optiIni, OptiScalerQuickOptions.Descriptors[0]) == "true",
            "读已有 FG Enabled");
        Check(OptiScalerQuickOptions.ReadValue(optiIni, OptiScalerQuickOptions.Descriptors[2]) == "auto",
            "缺键读 auto");
        string updated = OptiScalerQuickOptions.ApplyValue(optiIni, OptiScalerQuickOptions.Descriptors[2], "dlssg");
        Check(OptiScalerQuickOptions.ReadValue(updated, OptiScalerQuickOptions.Descriptors[2]) == "dlssg",
            "写 FGOutput 后读回");
        Check(updated.Contains("Enabled = true") && updated.Contains("AdaMfgUnlock = true"),
            "写 FGOutput 不动其它键");
        string appended = OptiScalerQuickOptions.ApplyValue("[Upscalers]\nDx11Upscaler = auto\n",
            OptiScalerQuickOptions.Descriptors.First(d => d.Section == "Spoofing"), "true");
        Check(appended.Contains("[Spoofing]") && appended.EndsWith("SpoofHAGS = true\n"),
            "缺节时补节");
        Check(OptiScalerQuickOptions.ReadValue(appended,
            OptiScalerQuickOptions.Descriptors.First(d => d.Section == "Spoofing")) == "true", "补节后读回");

        // ===== ReShade 深度宏 =====
        Check(ReShadeDepthOptions.All.Count == 6, "深度项 = 6");
        var preset = IniDocument.LoadFromText(
            "PreprocessorDefinitions=RESHADE_DEPTH_MULTIPLIER=1.0,fLUT_TextureName=\"a,b.png\",RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=1\n[Defines]\nx=1\n");
        Check(ReShadeDepthOptions.Get(preset, ReShadeDepthOptions.UpsideDown) == "1", "读上下翻转 = 1");
        Check(ReShadeDepthOptions.Get(preset, ReShadeDepthOptions.FarPlane) is null, "未写的宏 = null");
        ReShadeDepthOptions.Set(preset, ReShadeDepthOptions.UpsideDown, "0");
        Check(ReShadeDepthOptions.Get(preset, ReShadeDepthOptions.UpsideDown) == "0", "改上下翻转 = 0");
        ReShadeDepthOptions.Set(preset, ReShadeDepthOptions.FarPlane, "2000.0");
        Check(ReShadeDepthOptions.Get(preset, ReShadeDepthOptions.FarPlane) == "2000.0", "追加远平面");
        Check(ReShadeDepthOptions.Get(preset, ReShadeDepthOptions.Multiplier) == "1.0", "引号内逗号的 token 原样保留");
        ReShadeDepthOptions.Set(preset, ReShadeDepthOptions.Multiplier, "2.0");
        Check(ReShadeDepthOptions.Get(preset, ReShadeDepthOptions.Multiplier) == "2.0"
            && ReShadeDepthOptions.Get(preset, ReShadeDepthOptions.UpsideDown) == "0"
            && ReShadeDepthOptions.Get(preset, ReShadeDepthOptions.FarPlane) == "2000.0", "多次写入互不影响");

        // 裸 token（没有 = 值）按 1 读
        var bare = IniDocument.LoadFromText("PreprocessorDefinitions=RESHADE_DEPTH_INPUT_IS_MIRRORED\n");
        Check(ReShadeDepthOptions.Get(bare, ReShadeDepthOptions.Mirrored) == "1", "裸宏 = 1");
        ReShadeDepthOptions.Set(bare, ReShadeDepthOptions.Mirrored, "0");
        Check(ReShadeDepthOptions.Get(bare, ReShadeDepthOptions.Mirrored) == "0", "裸宏可改写");

        // ===== FSR 桥选项 =====
        Check(FsrBridgeOptions.Descriptors.Count == 13, "桥选项 = 13");
        Check(FsrBridgeOptions.Descriptors.First(d => d.Key == "Ffx12DepthInverted").DefaultValue == "1", "桥默认反向深度开启");
        Check(FsrBridgeOptions.Descriptors.First(d => d.Key == "Ffx12JitterDelay").Kind == FsrBridgeOptionKind.Bool, "桥 jitter 延迟是二态开关");
        Check(FsrBridgeOptions.Descriptors.First(d => d.Key == "Fsr2FamilySkip").DefaultValue == "0", "桥跳过预处理默认关闭");
        string bridgeIni = Path.Combine(Path.GetTempPath(), "hys-bridge-opt-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            File.WriteAllText(bridgeIni, "; 注释保留\n[Dx11FsrBridge]\nEnabled=1\nFfx12DepthFlipY=0\n");
            var ini = IniDocument.Load(bridgeIni);
            Check(FsrBridgeOptions.ReadValue(ini,
                FsrBridgeOptions.Descriptors.First(d => d.Key == "Enabled")) == "1", "桥读 Enabled");
            Check(FsrBridgeOptions.ReadValue(ini,
                FsrBridgeOptions.Descriptors.First(d => d.Key == "Fsr2FamilySkip")) is null, "桥缺键 = null");
            Check(FsrBridgeOptions.WriteValue(bridgeIni,
                FsrBridgeOptions.Descriptors.First(d => d.Key == "Fsr2FamilySkip"), "1"), "桥写新键");
            string after = File.ReadAllText(bridgeIni);
            Check(after.Contains("; 注释保留") && after.Contains("Enabled=1") && after.Contains("Fsr2FamilySkip=1"),
                "桥写后注释和旧键保留");
            Check(FsrBridgeOptions.WriteValue(bridgeIni,
                FsrBridgeOptions.Descriptors.First(d => d.Key == "Enabled"), "0"), "桥改 Enabled");
            Check(IniDocument.Load(bridgeIni).GetValue("Dx11FsrBridge", "Enabled") == "0", "桥改后读回");
        }
        finally { if (File.Exists(bridgeIni)) File.Delete(bridgeIni); }

        // ===== 热键格式化 =====
        Check(ReShadeHotkeys.Format(36, false, false, false) == "36,0,0,0", "Format Home");
        Check(ReShadeHotkeys.Format(0x72, true, false, true) == "114,1,0,1", "Format Ctrl+Shift+F3（ctrl/alt/shift 顺序）");
        Check(ReShadeHotkeys.TryParse("113,0,0,0", out int vk, out bool c, out bool a, out bool s)
            && vk == 113 && !c && !a && !s, "TryParse F2");
        Check(ReShadeHotkeys.Describe("36,0,0,0") == "Home", "Describe Home");
        Check(ReShadeHotkeys.Describe("36,1,0,1") == "Ctrl + Shift + Home", "Describe 组合键");
        Check(ReShadeHotkeys.Describe("0,0,0,0") == "未绑定", "Describe 未绑定");
        Check(!ReShadeHotkeys.TryParse("36,bad,0,0", out _, out _, out _, out _), "拒绝错误修饰键");
        Check(!ReShadeHotkeys.TryParse("999,0,0,0", out _, out _, out _, out _), "拒绝超范围键码");
        Check(ReShadeDepthOptions.Reversed.DefaultValue == "1", "反向深度默认与 shader 一致");
        var global = IniDocument.LoadFromText("PreprocessorDefinitions=PRESET_ONLY=1\n[GENERAL]\nPreprocessorDefinitions=OTHER=1,FILE=\"a,RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=1,b\"\n[INPUT]\nKeyOverlay=36,0,0,0\n");
        ReShadeDepthOptions.Set(global, ReShadeDepthOptions.UpsideDown, "0", "GENERAL");
        Check(ReShadeDepthOptions.Get(global, ReShadeDepthOptions.UpsideDown, "GENERAL") == "0", "全局深度宏可读回");
        Check(global.GetValue("GENERAL", "PreprocessorDefinitions")!.Contains("FILE=\"a,RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=1,b\""), "引号内伪宏不被修改");
        Check(global.GetValue(IniDocument.RootSection, "PreprocessorDefinitions") == "PRESET_ONLY=1"
            && global.GetValue("INPUT", "KeyOverlay") == "36,0,0,0", "全局深度写入保留其它节");
        Check(OptiScalerQuickOptions.Descriptors.Where(d => d.Section == "Upscalers")
            .All(d => d.Choices!.All(c => c.Value != "fsr")), "超分选项不写无效 fsr 键值");
        Check(ReShadeHotkeys.DescribeKey(0x1B) == "Esc" && ReShadeHotkeys.DescribeKey(0x70) == "F1"
            && ReShadeHotkeys.DescribeKey(0x60) == "小键盘0", "按键名表");

        Console.WriteLine($"Quick config options: PASS {passed} / FAIL 0; RealUiTested=false.");
        return passed;
    }
}
