using System.IO.Compression;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class PackInstallTransactionTests
{
    public static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "hys-pack-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0;
        void Check(bool value, string message) { if (!value) throw new Exception(message); passed++; }
        try
        {
            string pack = Path.Combine(root, "pack");
            Check(!await PackInstallTransaction.PrepareAsync(pack), "Missing transaction is inert");
            PackInstallTransaction.Start(pack);
            string first = File.ReadAllText(Path.Combine(pack, PackInstallTransaction.FileName));
            File.WriteAllText(Path.Combine(pack, PackOnceLedger.FileName), "corrupt old ledger");
            File.WriteAllText(Path.Combine(pack, ".hysx_ini_config_applied"), "old ini");
            Check(await PackInstallTransaction.PrepareAsync(pack), "Explicit install prepares");
            Check(!File.Exists(Path.Combine(pack, PackOnceLedger.FileName)), "Old ledger cleared");
            Check(!File.Exists(Path.Combine(pack, ".hysx_ini_config_applied")), "Old ini marker cleared");
            Check(File.ReadAllText(Path.Combine(pack, ".install-history", first, PackOnceLedger.FileName)) == "corrupt old ledger", "Old ledger archived without parsing");
            Check(File.ReadAllText(Path.Combine(pack, ".install-history", first, ".hysx_ini_config_applied")) == "old ini", "Old ini marker archived");
            File.WriteAllText(Path.Combine(pack, PackOnceLedger.FileName), "new pending reservation");
            Check(await PackInstallTransaction.PrepareAsync(pack), "Prepared install can resume");
            Check(File.ReadAllText(Path.Combine(pack, PackOnceLedger.FileName)) == "new pending reservation", "Resume preserves new ledger");
            PackInstallTransaction.Complete(pack);
            Check(!PackInstallTransaction.IsPending(pack) && !await PackInstallTransaction.PrepareAsync(pack), "Completion prevents automatic reprepare");
            PackInstallTransaction.Start(pack);
            Check(File.ReadAllText(Path.Combine(pack, PackInstallTransaction.FileName)) != first, "Same package reinstall creates fresh generation");
            await PackInstallTransaction.PrepareAsync(pack);
            Check(!File.Exists(Path.Combine(pack, PackOnceLedger.FileName)), "Same package reinstall resets old pending reservation");

            string cache = Path.Combine(root, "cache");
            string game = "hkrpg_bilibili";
            Directory.CreateDirectory(Path.Combine(cache, "games", game));
            string installed = GameAddonPack.PackDirectory(cache, game);
            var installer = new LocalPackageInstaller(Path.Combine(root, "opti"), Path.Combine(root, "shade", "reshade-shaders", "Addons"), Path.Combine(root, "modules"), cacheRoot: cache);
            string Zip(string name, string value)
            {
                string path = Path.Combine(root, name + ".zip");
                using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
                void Add(string entry, string text) { using var writer = new StreamWriter(zip.CreateEntry(entry).Open()); writer.Write(text); }
                Add("filelist.json", "{\"schema\":1,\"game\":\"hkrpg\",\"targets\":[{\"from\":\"HoYoShade\",\"to\":\"shade\"}]}");
                Add("HoYoShade/payload.txt", value);
                Add("GamePack/ini_config.json", "{\"set\":{\"TEST\":{\"Value\":\"" + value + "\"}}}");
                Add("GamePack/auto.json", "{\"name\":\"install\",\"steps\":[{\"action\":\"set_ini_keys\",\"once\":false}]}");
                Add("GamePack/.hysx_install_pending", new string('0', 32));
                Add("GamePack/.hysx_install_prepared", new string('0', 32));
                return path;
            }
            string a = Zip("A", "A");
            string gameDir = Path.Combine(root, "game");
            Directory.CreateDirectory(gameDir);
            var entry = new GameEntry("test:transaction", "Transaction") { ExePath = Path.Combine(gameDir, "Game.exe") };
            string iniPath = entry.ReShadeIniPath!;
            File.WriteAllText(iniPath, "[TEST]\nValue=initial\n");
            int dispatches = 0;
            async Task ApplyInstall(string expected)
            {
                await PackInstallTransaction.PrepareAsync(installed);
                GamePackIniConfig.ApplyForInstall(installed, iniPath);
                Check(IniDocument.Load(iniPath).GetValue("TEST", "Value") == expected, "Installed ini_config overwrites target: " + expected);
                var snapshot = PackAutoActionSnapshot.TryLoad(installed)!;
                var action = snapshot.LoadActions()[0];
                int before = dispatches;
                var result = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, installed, () => true, _ =>
                {
                    dispatches++;
                    var ini = IniDocument.Load(iniPath);
                    ini.SetValue("TEST", "Action", expected);
                    ini.Save(iniPath);
                    return Task.FromResult("已完成");
                });
                Check(!result.Blocked && dispatches == before + 1, "Fresh install dispatches set_ini_keys exactly once");
                var again = await PackActionOncePolicy.RunSnapshotAsync(action, snapshot, installed, () => true,
                    _ => { dispatches++; return Task.FromResult("unexpected"); });
                Check(!again.Blocked && dispatches == before + 1, "Same install refuses second set_ini_keys dispatch");
                PackInstallTransaction.Complete(installed);
            }
            await installer.InstallAsync(a, LocalPackageKind.Overlay);
            string a1 = File.ReadAllText(Path.Combine(installed, PackInstallTransaction.FileName));
            Check(a1 != new string('0', 32), "ZIP markers replaced after copy");
            Check(!File.Exists(Path.Combine(installed, ".hysx_install_prepared")), "ZIP prepared marker removed");
            File.WriteAllText(Path.Combine(installed, PackOnceLedger.FileName), "{\"schema\":3,\"fence\":\"old\",\"entries\":{},\"states\":{\"stale\":\"pending\"}}");
            await ApplyInstall("A");
            var manual = IniDocument.Load(iniPath);
            manual.SetValue("TEST", "Value", "manual");
            manual.Save(iniPath);
            GameIniBootstrap.Ensure(entry, null);
            GameIniBootstrap.Ensure(entry, null);
            Check(IniDocument.Load(iniPath).GetValue("TEST", "Value") == "manual", "Two launch bootstraps preserve manual ini edits");
            await installer.InstallAsync(a, LocalPackageKind.Overlay);
            Check(File.ReadAllText(Path.Combine(installed, PackInstallTransaction.FileName)) != a1, "Actual same ZIP reinstall opens new generation");
            await ApplyInstall("A");
            await installer.InstallAsync(Zip("B", "B"), LocalPackageKind.Overlay);
            Check(GamePackIniConfig.Load(installed)!.Set["TEST"]["Value"] == "B", "Package B overwrites package A payload");
            Check(PackInstallTransaction.IsPending(installed), "Package B has pending explicit install");
            await ApplyInstall("B");

            string emptyCache = Path.Combine(root, "empty-cache");
            var deferredInstaller = new LocalPackageInstaller(Path.Combine(root, "opti2"), Path.Combine(root, "shade2", "reshade-shaders", "Addons"), Path.Combine(root, "modules2"), cacheRoot: emptyCache);
            await deferredInstaller.InstallAsync(a, LocalPackageKind.Overlay);
            string staged = Path.Combine(emptyCache, "games", ".pending-gamepacks", "hkrpg");
            Check(PackInstallTransaction.IsPending(staged), "Deferred install carries generation");
            Check(File.ReadAllText(Path.Combine(staged, PackInstallTransaction.FileName)) != new string('0', 32), "Deferred generation replaces ZIP marker");
            Console.WriteLine($"PASS pack installation transactions ({passed} checks)");
            return 0;
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
