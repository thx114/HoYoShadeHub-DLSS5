using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class OptiScalerProfileIntegrityTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "hys-profile-integrity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0;
        void Check(bool value, string name)
        {
            if (!value) throw new InvalidOperationException(name);
            passed++;
        }
        const string original = "[NR]\nQuality=2\nScale=0.67\nWidth=1920\n[HoYoShade]\nDx11ReShadeOnly=true\n";
        string main = Path.Combine(root, OptiScalerRuntime.ConfigFileName);
        string profile = OptiScalerProfiles.ProfilePath(root, "hkrpg_bilibili");
        try
        {
            File.WriteAllText(main, original);
            Check(OptiScalerProfiles.Activate(root, "hkrpg_bilibili"), "initial activation");
            string edited = original.Replace("Quality=2", "Quality=3");
            File.WriteAllText(profile, edited);
            Check(!OptiScalerProfiles.TryWriteProfile(root, "hkrpg_bilibili", original, original)
                && File.ReadAllText(profile) == edited, "stale editor save preserves concurrent changes");
            Check(OptiScalerProfiles.CollectRuntimeChangesChecked(root).Status == OptiScalerProfiles.CollectionStatus.Unchanged,
                "profile-only edit is not a runtime edit");
            Check(OptiScalerProfiles.TryPrepareProfileForRead(root, "hkrpg_bilibili") && File.ReadAllText(main) == original,
                "opening an existing profile leaves runtime untouched");
            Check(OptiScalerProfiles.Activate(root, "hkrpg_bilibili") && File.ReadAllText(main) == edited,
                "activation preserves profile edits and NR/StarRail settings");
            string runtime = edited.Replace("Scale=0.67", "Scale=0.75");
            File.WriteAllText(main, runtime);
            Check(OptiScalerProfiles.Store(root, "hkrpg_bilibili") && File.ReadAllText(profile) == runtime,
                "runtime-only edit collected");
            Check(!OptiScalerProfiles.Store(root, "hk4e_cn"), "foreign owner cannot store");
            File.WriteAllText(main, runtime.Replace("Width=1920", "Width=2560"));
            File.WriteAllText(profile, runtime.Replace("Quality=3", "Quality=4"));
            byte[] beforeMain = File.ReadAllBytes(main);
            byte[] beforeProfile = File.ReadAllBytes(profile);
            Check(OptiScalerProfiles.CollectRuntimeChangesChecked(root).Status == OptiScalerProfiles.CollectionStatus.Blocked,
                "divergent edits block collection");
            Check(!OptiScalerProfiles.Activate(root, "hk4e_cn")
                && File.ReadAllBytes(main).SequenceEqual(beforeMain)
                && File.ReadAllBytes(profile).SequenceEqual(beforeProfile), "blocked activation preserves both edits");
            File.Delete(Path.Combine(root, OptiScalerProfiles.RuntimeBaselineFileName));
            Check(OptiScalerProfiles.CollectRuntimeChangesChecked(root).Status == OptiScalerProfiles.CollectionStatus.Blocked,
                "ambiguous legacy state blocks without guessing");
            File.WriteAllBytes(profile, beforeMain);
            Check(OptiScalerProfiles.CollectRuntimeChangesChecked(root).Succeeded, "matching legacy state migrates");
            File.WriteAllText(OptiScalerProfiles.ProfilePath(root, "hk4e_cn"), original);
            string owner = Path.Combine(root, OptiScalerProfiles.RuntimeOwnerFileName);
            using (var held = new FileStream(owner, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                Check(!OptiScalerProfiles.Activate(root, "hk4e_cn"), "locked owner blocks activation");
                Check(File.ReadAllBytes(main).SequenceEqual(beforeMain), "owner commit failure restores runtime");
            }
            Check(OptiScalerProfiles.RuntimeOwner(root) == "hkrpg_bilibili", "owner remains original after failure");
            string routeRoot = Path.Combine(root, "route");
            Directory.CreateDirectory(routeRoot);
            string routeMain = Path.Combine(routeRoot, OptiScalerRuntime.ConfigFileName);
            File.WriteAllText(routeMain, original.Replace("Dx11ReShadeOnly=true", "Dx11ReShadeOnly=false"));
            Check(OptiScalerProfiles.Activate(routeRoot, "hkrpg_bilibili"), "route fixture activation");
            Check(OptiScalerRuntime.EnsureStarRailDx11InputRoute(routeRoot, "hkrpg_bilibili")
                && OptiScalerRuntime.EnsureConfigDllPath(routeRoot), "trusted route update followed by runtime path pinning");
            Check(OptiScalerProfiles.Store(routeRoot, "hkrpg_bilibili"), "normal StarRail preparation does not create a conflict");
            string routeProfile = File.ReadAllText(OptiScalerProfiles.ProfilePath(routeRoot, "hkrpg_bilibili"));
            Check(routeProfile.Contains("Dx11ReShadeOnly = true") && routeProfile.Contains("Quality=2")
                && routeProfile.Contains("Scale=0.67") && routeProfile.Contains("Width=1920"), "route transaction preserves NR settings");
            return passed;
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
