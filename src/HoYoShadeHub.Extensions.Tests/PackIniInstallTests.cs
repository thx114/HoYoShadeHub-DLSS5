using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Tests;

internal static class PackIniInstallTests
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "hys-pack-ini-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string pack = Path.Combine(root, "pack");
            string addons = Path.Combine(pack, "Addons");
            Directory.CreateDirectory(addons);
            File.WriteAllText(Path.Combine(addons, GameAddonPack.MarkerFileName), "{}");
            string config = Path.Combine(pack, GameAddonPackUserContent.IniConfigFileName);
            string ini = Path.Combine(root, "ReShade.ini");
            File.WriteAllText(ini, "[GENERAL]\nAddonPath=" + addons + "\n[PACKAGE_TEST]\nValue=initial\nRemoveMe=old\n");
            const string packageA = "{\"set\":{\"PACKAGE_TEST\":{\"Value\":\"A\"}},\"remove\":{\"PACKAGE_TEST\":[\"RemoveMe\"]}}";
            File.WriteAllText(config, packageA);
            check(GamePackIniConfig.ApplyForInstall(pack, ini), "Explicit install A changes the INI");
            check(IniDocument.Load(ini).GetValue("PACKAGE_TEST", "Value") == "A"
                && !IniDocument.Load(ini).ContainsKey("PACKAGE_TEST", "RemoveMe"), "Install applies set and remove");

            var manual = IniDocument.Load(ini);
            manual.SetValue("PACKAGE_TEST", "Value", "manual");
            manual.Save(ini);
            var entry = new GameEntry("test:pack-install", "Pack install") { ExePath = Path.Combine(root, "Game.exe") };
            GameIniBootstrap.Ensure(entry, null);
            GameIniBootstrap.Ensure(entry, null);
            check(IniDocument.Load(ini).GetValue("PACKAGE_TEST", "Value") == "manual",
                "Repeated startup preserves manual values even without an applied marker");
            check(!File.Exists(Path.Combine(pack, ".hysx_ini_config_applied")), "Startup does not create an INI application transaction");

            File.WriteAllText(Path.Combine(pack, ".hysx_ini_config_applied"), "legacy marker");
            check(GamePackIniConfig.ApplyForInstall(pack, ini)
                && IniDocument.Load(ini).GetValue("PACKAGE_TEST", "Value") == "A", "Explicit reinstall ignores legacy marker and reapplies identical A");
            File.WriteAllText(config, "{\"set\":{\"PACKAGE_TEST\":{\"Value\":\"B\"}}}");
            check(GamePackIniConfig.ApplyForInstall(pack, ini)
                && IniDocument.Load(ini).GetValue("PACKAGE_TEST", "Value") == "B", "Installing B overrides A");

            bool ThrowsInstall(Action install)
            {
                try { install(); return false; }
                catch (InvalidDataException error) { return error.Message.Contains("ini_config.json", StringComparison.Ordinal); }
            }
            check(ThrowsInstall(() => GamePackIniConfig.ApplyForInstall(pack, Path.Combine(root, "missing.ini"))),
                "Missing target is an explicit installation failure");
            byte[] before = File.ReadAllBytes(ini);
            File.WriteAllText(config, "{ invalid json");
            check(ThrowsInstall(() => GamePackIniConfig.ApplyForInstall(pack, ini)) && before.SequenceEqual(File.ReadAllBytes(ini)),
                "Malformed package reports failure without changing target");
            File.WriteAllText(config, "{\"set\":{\"PACKAGE_TEST\":{\"Value\":\"C\"}}}");
            using (var held = new FileStream(ini, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                check(ThrowsInstall(() => GamePackIniConfig.ApplyForInstall(pack, ini)), "Locked target reports installation failure");
            check(IniDocument.Load(ini).GetValue("PACKAGE_TEST", "Value") == "B", "Failed installation preserves existing value");
            var actionConfig = new GamePackIniConfig();
            actionConfig.Set["PACKAGE_TEST"] = new Dictionary<string, string> { ["Value"] = "action" };
            actionConfig.Remove["PACKAGE_TEST"] = ["OldActionKey"];
            bool actionFailed = false;
            using (var held = new FileStream(ini, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { actionConfig.ApplyForInstall(ini); }
                catch (InvalidDataException error) { actionFailed = error.Message.Contains(ini, StringComparison.Ordinal); }
            }
            check(actionFailed, "Explicit INI action propagates locked-target failure");
            check(actionConfig.ApplyForInstall(ini) && IniDocument.Load(ini).GetValue("PACKAGE_TEST", "Value") == "action",
                "Explicit INI action writes after a new installation attempt");
            File.Delete(config);
            check(!GamePackIniConfig.ApplyForInstall(pack, ini), "Packages without ini_config need no INI application");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
