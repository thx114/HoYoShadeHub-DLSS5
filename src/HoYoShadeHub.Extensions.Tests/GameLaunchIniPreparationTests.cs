using System.Text;
using System.Text.Json;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Tests;

internal static class GameLaunchIniPreparationTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "hys-launch-ini-" + Guid.NewGuid().ToString("N"));
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
        }
        GameEntry Entry(string name)
        {
            string directory = Path.Combine(root, name);
            Directory.CreateDirectory(directory);
            return new GameEntry("biz:hkrpg_cn", name) { ExePath = Path.Combine(directory, "StarRail.exe") };
        }
        try
        {
            string hostRoot = Path.Combine(root, "host");
            Directory.CreateDirectory(hostRoot);
            var host = new ShadeHost(hostRoot);
            byte[] template = [.. Encoding.UTF8.Preamble,
                .. Encoding.UTF8.GetBytes("; host template\r\n[GENERAL]\r\nPresetPath=Default.ini\r\n")];
            File.WriteAllBytes(host.ReShadeIniPath, template);
            foreach (string actionName in new[] { "set_addons", "set_addon", "apply_preset", "set_ini_keys" })
            {
                var entry = Entry("missing-" + actionName);
                Check(!File.Exists(entry.ReShadeIniPath), actionName + " fixture starts without target");
                GameLaunchIniPreparation.EnsurePackActionTarget(entry, host, [Action(actionName)]);
                Check(File.ReadAllBytes(entry.ReShadeIniPath!).SequenceEqual(template),
                    actionName + " creates missing target using exact template bytes");
                Check(!File.Exists(Path.Combine(entry.GameDirectory!, PackOnceLedger.FileName)),
                    actionName + " preparation does not reserve a ledger");
            }

            var old = Entry("existing");
            byte[] original = Encoding.UTF8.GetBytes("; user notes\n[GENERAL]\nPresetPath=Old.ini\n[OVERLAY]\nShowFPS=0\n[USER]\nKeep=yes\n");
            File.WriteAllBytes(old.ReShadeIniPath!, original);
            string ledger = Path.Combine(old.GameDirectory!, PackOnceLedger.FileName);
            byte[] ledgerBytes = Encoding.UTF8.GetBytes("existing once state must remain untouched");
            File.WriteAllBytes(ledger, ledgerBytes);
            GameLaunchIniPreparation.EnsurePackActionTarget(old, null, [Action("set_ini_keys")]);
            Check(File.ReadAllBytes(old.ReShadeIniPath!).SequenceEqual(original),
                "existing target remains byte-identical before original action dispatch");
            var config = new GamePackIniConfig();
            config.Set["GENERAL"] = new(StringComparer.OrdinalIgnoreCase) { ["PresetPath"] = "Pack.ini" };
            config.Set["OVERLAY"] = new(StringComparer.OrdinalIgnoreCase) { ["ShowFPS"] = "1" };
            Check(config.Apply(old.ReShadeIniPath!), "explicit package values replace old INI settings");
            var ini = IniDocument.Load(old.ReShadeIniPath!);
            Check(ini.GetValue("GENERAL", "PresetPath") == "Pack.ini"
                && ini.GetValue("OVERLAY", "ShowFPS") == "1" && ini.GetValue("USER", "Keep") == "yes",
                "package sets requested values while retaining unrelated keys");
            ini.SetValue("GENERAL", "PresetPath", "UserEdited.ini");
            ini.Save(old.ReShadeIniPath!);
            byte[] userEdited = File.ReadAllBytes(old.ReShadeIniPath!);
            GameLaunchIniPreparation.EnsurePackActionTarget(old, host, [Action("set_ini_keys")]);
            Check(File.ReadAllBytes(old.ReShadeIniPath!).SequenceEqual(userEdited),
                "repeated target preparation never replaces user's existing settings");
            Check(File.ReadAllBytes(ledger).SequenceEqual(ledgerBytes), "successful preparation leaves once ledger bytes unchanged");

            GameLaunchIniPreparation.EnsurePackActionTarget(null, null,
                [Action("set_opti_config"), Action("set_launch_option"), Action("set_ini_keys", "OptiScaler.ini")]);
            Check(true, "OptiScaler-only actions need neither ReShade target nor host template");
            GameLaunchIniPreparation.EnsurePackActionTarget(null, null, []);
            Check(true, "empty action plan needs no target");

            var noTemplate = Entry("no-template");
            var missingHost = new ShadeHost(Path.Combine(root, "absent-host"));
            string noTemplateLedger = Path.Combine(noTemplate.GameDirectory!, PackOnceLedger.FileName);
            File.WriteAllBytes(noTemplateLedger, ledgerBytes);
            foreach (string actionName in new[] { "set_addons", "set_addon", "apply_preset", "set_ini_keys" })
            {
                bool threw = false;
                try { GameLaunchIniPreparation.EnsurePackActionTarget(noTemplate, missingHost, [Action(actionName)]); }
                catch (IOException) { threw = true; }
                Check(threw && !File.Exists(noTemplate.ReShadeIniPath), actionName + " missing template fails before target creation");
                Check(File.ReadAllBytes(noTemplateLedger).SequenceEqual(ledgerBytes),
                    actionName + " failure leaves existing ledger untouched");
            }
            bool noTargetThrew = false;
            try { GameLaunchIniPreparation.EnsurePackActionTarget(null, host, [Action("set_ini_keys")]); }
            catch (IOException) { noTargetThrew = true; }
            Check(noTargetThrew, "ReShade action rejects missing game target before dispatch");

            string sourceRoot = FindSourceRoot();
            foreach (string file in new[] { "GameLauncherPage.StartGame.cs", "GameLaunchPipeline.cs" })
            {
                string source = File.ReadAllText(Path.Combine(sourceRoot, "src", "HoYoShadeHub", "Features", "GameLauncher", file));
                int prepare = source.IndexOf("GameLaunchIniPreparation.EnsurePackActionTarget(", StringComparison.Ordinal);
                int dispatch = source.IndexOf("LauncherActionRunner.RunResultAsync(", StringComparison.Ordinal);
                Check(prepare >= 0 && dispatch > prepare, file + " prepares target before package action dispatch");
            }
            Console.WriteLine($"Launch INI target preparation: PASS {passed} / FAIL 0; GameTested=false.");
            return passed;
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static PackAutoAction Action(string name, string? file = null)
    {
        var action = new PackAutoAction { Name = name, RunOnLaunch = true };
        action.Steps.Add(new PackActionStep { Action = name, Raw = JsonSerializer.SerializeToElement(new { file }) });
        return action;
    }

    private static string FindSourceRoot()
    {
        foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "src", "HoYoShadeHub", "Features", "GameLauncher", "GameLaunchPipeline.cs")))
                    return directory.FullName;
        }
        throw new DirectoryNotFoundException("Launcher source root is required for dispatch-order verification.");
    }
}
