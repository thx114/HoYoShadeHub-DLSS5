using HoYoShadeHub.Core;
using HoYoShadeHub.Extensions.Games;

namespace HoYoShadeHub.Extensions.Tests;

internal static class OptiSmoothMotionTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; }
        void Throws(Action action, string name) { try { action(); } catch (InvalidOperationException) { passed++; return; } throw new Exception(name); }
        try
        {
            foreach (GameBiz biz in GameBiz.AllGameBizs)
                Check(OptiSmoothMotionPolicy.Applies(biz.Value, true) == (biz.Game is GameBiz.hk4e or GameBiz.hkrpg or GameBiz.nap), "Family isolation " + biz);
            foreach (string biz in new[] { "hk4e_global", "hkrpg_cn", "hkrpg_bilibili", "hkrpg_global", "hkrpg_beta", "nap_global", "nap_beta_postbeta" })
                Check(OptiSmoothMotionPolicy.Applies(biz, true), "Not limited to CN/Bilibili: " + biz);
            Check(!OptiSmoothMotionPolicy.Applies("hkrpg_cn", false), "Opti off has no interlock");
            foreach (string biz in new[] { "", "custom_hkrpg", "hk4ex_cn", "bh3_cn", "wuwa_global" })
                Check(!OptiSmoothMotionPolicy.Applies(biz, true), "Unrelated identity: " + biz);

            int writes = 0, reads = 0, globals = 0;
            uint current = 1;
            var others = new Dictionary<string, uint> { ["GLOBAL"] = 1, ["OtherGame.exe"] = 1 };
            OptiSmoothMotionPolicy.DriverState Read(string exe) { reads++; return new(true, current); }
            OptiSmoothMotionPolicy.DriverState Global() { globals++; return new(true, others["GLOBAL"]); }
            OptiSmoothMotionPolicy.DriverState Disable(string exe) { Check(exe == "StarRail.exe", "Only target EXE written"); writes++; current = 0; return new(true, 0); }
            var result = OptiSmoothMotionPolicy.EnsureDisabled("hkrpg_global", true, "StarRail.exe", Read, Global, Disable);
            Check(result.Changed && result.ConfirmedOff && writes == 1 && reads == 2, "Enabled -> disable -> verified readback");
            Check(others["GLOBAL"] == 1 && others["OtherGame.exe"] == 1 && globals == 0, "Global/other games untouched");
            result = OptiSmoothMotionPolicy.EnsureDisabled("hkrpg_cn", true, "StarRail.exe", Read, Global, Disable);
            Check(!result.Changed && result.ConfirmedOff && writes == 1, "Already off: no write");
            reads = 0;
            result = OptiSmoothMotionPolicy.EnsureDisabled("bh3_cn", true, "StarRail.exe", Read, Global, Disable);
            Check(reads == 0 && writes == 1 && !result.Changed, "Unrelated game: no driver access");
            result = OptiSmoothMotionPolicy.EnsureDisabled("hkrpg_cn", false, "StarRail.exe", Read, Global, Disable);
            Check(reads == 0 && writes == 1 && !result.Changed, "Disabling Opti never restores AI");

            int inheritedRead = 0;
            result = OptiSmoothMotionPolicy.EnsureDisabled("hkrpg_cn", true, "StarRail.exe",
                _ => ++inheritedRead == 1 ? new(false, 0, "missing profile") : new(true, 0), Global, Disable);
            Check(result.Changed && globals == 1 && others["GLOBAL"] == 1, "Global ON -> per-game OFF override only");
            result = OptiSmoothMotionPolicy.EnsureDisabled("nap_cn", true, "ZenlessZoneZero.exe",
                _ => new(false, 0, "not supported"), () => new(false, 0), _ => throw new Exception("Must not write"));
            Check(!result.Changed && !result.ConfirmedOff && result.Note is not null, "Unknown driver remains unverified");
            Throws(() => OptiSmoothMotionPolicy.EnsureDisabled("hk4e_cn", true, "YuanShen.exe",
                _ => new(true, 1), Global, _ => new(false, 1, "denied")), "Known enabled + write failure blocks launch");
            Throws(() => OptiSmoothMotionPolicy.EnsureDisabled("nap_global", true, "ZenlessZoneZero.exe",
                _ => new(true, 1), Global, _ => new(true, 0)), "Readback mismatch not reported as disabled");
            Console.WriteLine($"Opti AI interlock tests: PASS {passed} / FAIL 0; fake driver only; real NVIDIA configuration untouched.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("Opti AI interlock tests FAILED: " + ex.Message); return 1; }
    }
}
