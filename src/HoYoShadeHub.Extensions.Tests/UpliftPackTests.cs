using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using System.Security.Cryptography;

namespace HoYoShadeHub.Extensions.Tests;

internal static class UpliftPackTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "hys-uplift-pack-" + Guid.NewGuid().ToString("N"));
        int failed = 0;
        void Check(bool ok, string message) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {message}"); if (!ok) failed++; }
        void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
        string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        try
        {
            var host = new ShadeHost(Path.Combine(root, "host"));
            string cache = Path.Combine(root, "cache");
            string pack = GameAddonPack.AddonDirectory(cache, "nap_cn");
            string packRoot = Path.GetDirectoryName(pack)!;
            Write(Path.Combine(host.AddonsPath, "old.addon64"), "old");
            Write(Path.Combine(pack, "old.addon64"), "old");
            Write(Path.Combine(pack, GameAddonPack.MarkerFileName), "{\"schema\":2,\"selections\":[],\"identity\":\"legacy\"}");
            Write(Path.Combine(packRoot, "auto.json"), "{\"actions\":[]}");
            Write(Path.Combine(packRoot, ".hysx_once.json"), "{\"done\":true}");
            string exe = Path.Combine(root, "game", "Game.exe");
            Write(exe, "fixture");
            string ini = Path.Combine(Path.GetDirectoryName(exe)!, "ReShade.ini");
            Write(ini, "[ADDON]\nAddonPath=" + pack + "\nDisabledAddons=\n[RenoDX.DLSS5]\nNRStyle=3\n");
            var game = new GameEntry("test", "fixture") { ExePath = exe };
            var service = new GamePluginService(game, host);
            Write(Path.Combine(host.AddonsPath, "gitc-uplift.addon64"), "uplift-addon");
            Write(Path.Combine(host.AddonsPath, "gitc-uplift-helper64.exe"), "uplift-helper");
            Write(Path.Combine(host.AddonsPath, "GITC-Uplift", "a", "data.bin"), "a");
            Write(Path.Combine(host.AddonsPath, "GITC-Uplift", "b", "data.bin"), "b");
            Write(Path.Combine(host.AddonsPath, "settings.ini"), "shared-default");
            Write(Path.Combine(pack, "settings.ini"), "user-nr-settings");
            Write(Path.Combine(pack, "user.addon64"), "user-addon");
            Check(!service.GetAddons().Any(a => a.FileName == "gitc-uplift.addon64"), "reproduce: shared Uplift absent from old per-game pack");
            string[] protectedFiles = [ini, Path.Combine(packRoot, "auto.json"), Path.Combine(packRoot, ".hysx_once.json"), Path.Combine(pack, "settings.ini"), Path.Combine(pack, "user.addon64")];
            string[] hashes = protectedFiles.Select(Hash).ToArray();
            var plan = GameAddonPack.Plan(host.AddonsPath, null, null);
            string identity = GameAddonPack.ComputeIdentity("nap_cn", plan);
            GameAddonPack.Sync(pack, plan, identity: identity, preserveSettings: true);
            Check(service.GetAddons().Any(a => a.FileName == "gitc-uplift.addon64"), "same game plugin service lists Uplift after background pack refresh");
            Check(File.ReadAllText(Path.Combine(pack, "gitc-uplift-helper64.exe")) == "uplift-helper"
                && File.ReadAllText(Path.Combine(pack, "GITC-Uplift", "a", "data.bin")) == "a"
                && File.ReadAllText(Path.Combine(pack, "GITC-Uplift", "b", "data.bin")) == "b", "helper and nested companion files keep original paths without collisions");
            GameAddonPack.Sync(pack, plan, identity: identity, preserveSettings: true);
            Check(protectedFiles.Select(Hash).SequenceEqual(hashes), "game INI, NR settings, auto, once ledger and manual addon unchanged");
            Check(GameAddonPackAudit.Inspect(cache, "nap_cn", host.AddonsPath).MissingFiles.Count == 0, "recursive audit finds no missing companion files");

            // 版本归档中的嵌套目录也必须保留，且应覆盖共享目录的同一相对路径。
            string archive = Path.Combine(root, "archive", "reshade-shaders", "Addons", "GITC-Uplift", "a", "data.bin");
            Write(archive, "archive-a");
            var selected = GameAddonPack.Plan(host.AddonsPath, new Dictionary<string, string> { ["gitc.uplift"] = "v1" }, (_, _) => [archive]);
            Check(selected.Single(e => e.FileName == Path.Combine("GITC-Uplift", "a", "data.bin")).SourcePath == archive,
                "archive overrides matching relative path without flattening");

            File.Move(Path.Combine(host.AddonsPath, "gitc-uplift.addon64"), Path.Combine(host.AddonsPath, "gitc-uplift.addon64x"));
            plan = GameAddonPack.Plan(host.AddonsPath, null, null);
            // 验证旧 marker 的第一次禁用也能清除内容相同的启用副本。
            Write(Path.Combine(pack, GameAddonPack.MarkerFileName), "{\"schema\":2,\"selections\":[]}");
            GameAddonPack.Sync(pack, plan, identity: GameAddonPack.ComputeIdentity("nap_cn", plan), preserveSettings: true);
            Check(!File.Exists(Path.Combine(pack, "gitc-uplift.addon64")) && File.Exists(Path.Combine(pack, "gitc-uplift.addon64x")), "first legacy sync propagates global disable without leaving enabled twin");
            File.Delete(Path.Combine(host.AddonsPath, "gitc-uplift.addon64x"));
            plan = GameAddonPack.Plan(host.AddonsPath, null, null);
            GameAddonPack.Sync(pack, plan, identity: GameAddonPack.ComputeIdentity("nap_cn", plan), preserveSettings: true);
            Check(!File.Exists(Path.Combine(pack, "gitc-uplift.addon64x")) && File.Exists(Path.Combine(pack, "user.addon64")), "uninstall removes managed addon and preserves manual addon");
            Write(Path.Combine(pack, "gitc-uplift.addon64"), "legacy-uplift");
            Write(Path.Combine(pack, GameAddonPack.MarkerFileName), "{\"schema\":2,\"selections\":[]}");
            GameAddonPack.RemoveMatchingAddons(cache, ["gitc-uplift*"]);
            Check(!File.Exists(Path.Combine(pack, "gitc-uplift.addon64")) && File.Exists(Path.Combine(pack, "user.addon64")),
                "explicit uninstall cleans legacy unregistered pack by extension pattern");
            bool blocked = false;
            try { GameAddonPack.Sync(pack, [new AddonPackEntry { FileName = "../escape", SourcePath = archive }]); }
            catch (IOException) { blocked = true; }
            Check(blocked && !File.Exists(Path.Combine(packRoot, "escape")), "relative paths cannot escape Addons");
            return failed == 0 ? 0 : 1;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
