using HoYoShadeHub.Extensions.Services;
using System.Text;

namespace HoYoShadeHub.Extensions.Tests;

internal static class RtssProfileTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "hysx-rtss-tests-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(root, "backup");
        Directory.CreateDirectory(Path.Combine(root, "Profiles"));
        File.WriteAllText(Path.Combine(root, "RTSS.exe"), "stub");
        string profile = Path.Combine(root, "Profiles", "YuanShen.exe.cfg");
        string global = Path.Combine(root, "Profiles", "Global");
        byte[] original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("; 保留中文\r\n[OSD]\r\nEnableOSD=1\r\n[Hooking]\r\nEnableHooking=3\r\n[Framerate]\r\nLimit=120\r\n")).ToArray();
        File.WriteAllBytes(profile, original);
        File.WriteAllText(global, "[Hooking]\nEnableHooking=2\n");
        int passed = 0;
        void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; }
        void Throws(Action action, string name) { try { action(); } catch { passed++; return; } throw new Exception(name); }
        try
        {
            Check(!RtssProfileService.Read(root, "YuanShen.exe", backup).CanRestore, "Read has no writes");
            Check(!Directory.Exists(backup), "Detection does not create backup");
            int guardCalls = 0;
            Throws(() => RtssProfileService.ApplyCore(root, "YuanShen.exe", backup, RtssProfileMode.Disabled,
                _ => { if (++guardCalls == 2) throw new InvalidOperationException("simulated RTSS restart"); }), "Late running guard aborts write");
            Check(File.ReadAllBytes(profile).SequenceEqual(original), "Guard failure leaves profile bytes intact");
            RtssProfileService.ApplyCore(root, "YuanShen.exe", backup, RtssProfileMode.Disabled, ensureStopped: _ => { });
            string text = File.ReadAllText(profile);
            Check(text.Contains("EnableHooking=0"), "Disabled only current game");
            Check(text.Contains("EnableOSD=1") && text.Contains("Limit=120") && text.Contains("保留中文"), "Other fields and encoding preserved");
            Check(File.ReadAllText(global).Contains("EnableHooking=2"), "Global unchanged");
            Check(RtssProfileService.Read(root, "YuanShen.exe", backup).CanRestore, "Backup available");
            RtssProfileService.RestoreCore(root, "YuanShen.exe", backup, ensureStopped: _ => { });
            Check(File.ReadAllBytes(profile).SequenceEqual(original), "Restore original bytes");
            Check(!RtssProfileService.Read(root, "YuanShen.exe", backup).CanRestore, "Restore action no longer offered");
            foreach (Encoding encoding in new Encoding[] { new UnicodeEncoding(false, true), new UnicodeEncoding(true, true), Encoding.Latin1 })
            {
                byte[] encoded = encoding.GetPreamble().Concat(encoding.GetBytes("; preserved é\r\n[OSD]\r\nEnableOSD=1\r\n[Framerate]\r\nLimit=72")).ToArray();
                File.WriteAllBytes(profile, encoded);
                RtssProfileService.ApplyCore(root, "YuanShen.exe", backup, RtssProfileMode.Disabled, ensureStopped: _ => { });
                Check(RtssProfileService.Read(root, "YuanShen.exe", backup).Hooking == "0", "Missing section inserted: " + encoding.WebName);
                RtssProfileService.ApplyCore(root, "YuanShen.exe", backup, RtssProfileMode.Disabled, ensureStopped: _ => { });
                RtssProfileService.RestoreCore(root, "YuanShen.exe", backup, ensureStopped: _ => { });
                Check(File.ReadAllBytes(profile).SequenceEqual(encoded), "Repeated apply retains original bytes: " + encoding.WebName);
            }
            File.WriteAllBytes(profile, original);
            RtssProfileService.ApplyCore(root, "StarRail.exe", backup, RtssProfileMode.Disabled, ensureStopped: _ => { });
            Check(File.Exists(Path.Combine(root, "Profiles", "StarRail.exe.cfg")), "Create per-game profile");
            RtssProfileService.RestoreCore(root, "StarRail.exe", backup, ensureStopped: _ => { });
            Check(!File.Exists(Path.Combine(root, "Profiles", "StarRail.exe.cfg")), "Restore absent profile");
            Throws(() => RtssProfileService.ProfilePath(root, "../Global"), "Reject traversal/global");
            Throws(() => RtssProfileService.ProfilePath(root, "Global"), "Require exe");
            Check(RtssProfileService.SetHooking("[Hooking]", 0).Contains("[Hooking]\nEnableHooking=0"), "Section newline");
            Throws(() => RtssProfileService.SetHooking("[Hooking]\n[Hooking]\n", 0), "Reject duplicate sections");
            RtssProfileService.ApplyCore(root, "YuanShen.exe", backup, RtssProfileMode.Disabled, ensureStopped: _ => { });
            File.AppendAllText(profile, "USER_NEW=1\n");
            Check(!RtssProfileService.Read(root, "YuanShen.exe", backup).CanRestore, "Stale receipt not offered");
            Throws(() => RtssProfileService.RestoreCore(root, "YuanShen.exe", backup, ensureStopped: _ => { }), "No overwrite external user changes");
            Throws(() => RtssProfileService.ApplyCore(root, "YuanShen.exe", backup, RtssProfileMode.Disabled, ensureStopped: _ => { }), "No overwrite external changes on repeated apply");
            if (RtssProfileService.VerifiedLowValue is null)
                Throws(() => RtssProfileService.ApplyCore(root, "YuanShen.exe", backup, RtssProfileMode.Low, ensureStopped: _ => { }), "Never guess Low");
            Console.WriteLine($"RTSS profile tests: PASS {passed} / FAIL 0; temporary files only; no processes started/stopped.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("RTSS profile tests FAILED: " + ex.Message); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
