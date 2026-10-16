using System.IO.Compression;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class PackageInstallSafetyTests
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "hysx-package-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (string game in new[] { "../..", "..\\..", "C:\\outside", "hk4e/child", ".", "" })
                Reject(() => new OverlayManifest { Schema = 1, Game = game }.ValidatePaths(root));
            foreach (string from in new[] { "../outside", "C:\\outside", "\\outside", "a/../../outside" })
                Reject(() => new OverlayManifest { Schema = 1, Targets = [new() { From = from, To = "shade" }] }.ValidatePaths(root));
            new OverlayManifest { Schema = 1, Game = "hkrpg_bilibili", Targets = [new() { From = "HoYoShade", To = "shade" }] }.ValidatePaths(root);

            string escaped = Path.Combine(root, "reparse-source"), link = Path.Combine(root, "reparse-link");
            Directory.CreateDirectory(escaped);
            try
            {
                Directory.CreateSymbolicLink(link, escaped);
                Reject(() => PackageTree.ResolveRelative(root, "reparse-link/child"));
                Reject(() => PackageTree.ValidateTree(link));
            }
            catch (UnauthorizedAccessException) { Console.WriteLine("SKIP package reparse fixture: symbolic links unavailable"); }
            catch (IOException ex) when (!Directory.Exists(link)) { Console.WriteLine("SKIP package reparse fixture: " + ex.Message); }

            string target = Path.Combine(root, "target.dll");
            File.WriteAllText(target, "old target");
            Reject(() => HysxFileLink.LinkOrCopy(Path.Combine(root, "missing.dll"), target));
            Check(File.ReadAllText(target) == "old target", "Missing source destroyed target");

            string source = Path.Combine(root, "source"), dest = Path.Combine(root, "destination");
            Directory.CreateDirectory(Path.Combine(source, "ini_config", "nested"));
            File.WriteAllText(Path.Combine(source, "ini_config", "nested", "config.ini"), "payload");
            Check(PackageTree.Copy(source, dest) == 1, "Incorrect recursive copy count");
            Directory.Delete(source, recursive: true);
            Check(File.ReadAllText(Path.Combine(dest, "ini_config", "nested", "config.ini")) == "payload", "Nested payload lost");

            string modules = Path.Combine(root, "modules"), opti = Path.Combine(root, "opti");
            var installer = new LocalPackageInstaller(opti, Path.Combine(root, "shade", "reshade-shaders", "Addons"), modules);
            foreach (var kind in new[] { LocalPackageKind.Module, LocalPackageKind.OptiScaler })
            {
                string package = Path.Combine(root, "broken.zip");
                File.WriteAllText(package, "not a zip");
                string old = kind == LocalPackageKind.Module ? Path.Combine(modules, "broken") : Path.Combine(opti, "local", "broken");
                Directory.CreateDirectory(old);
                File.WriteAllText(Path.Combine(old, "sentinel"), "preserve");
                await RejectAsync(() => installer.InstallAsync(package, kind));
                Check(File.ReadAllText(Path.Combine(old, "sentinel")) == "preserve", "Bad ZIP destroyed previous install");
            }

            string validZip = Path.Combine(root, "valid-module.zip");
            using (var zip = ZipFile.Open(validZip, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("nested/module.dll").Open())) writer.Write("module");
            await installer.InstallAsync(validZip, LocalPackageKind.Module);
            Check(File.ReadAllText(Path.Combine(modules, "valid-module", "nested", "module.dll")) == "module", "Module commit incomplete");

            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await RejectAsync(() => installer.InstallAsync(validZip, LocalPackageKind.Module, canceled.Token));
            Check(File.ReadAllText(Path.Combine(modules, "valid-module", "nested", "module.dll")) == "module", "Canceled install changed existing module");

            string cache = Path.Combine(root, "cache");
            Directory.CreateDirectory(cache);
            File.WriteAllText(Path.Combine(cache, "sentinel"), "cache state");
            var overlayInstaller = new LocalPackageInstaller(opti, Path.Combine(root, "shade", "reshade-shaders", "Addons"), modules, cacheRoot: cache);
            string badOverlay = Path.Combine(root, "bad-overlay.zip");
            MakeOverlay(badOverlay, "../..");
            await RejectAsync(() => overlayInstaller.InstallAsync(badOverlay, LocalPackageKind.Overlay));
            Check(File.ReadAllText(Path.Combine(cache, "sentinel")) == "cache state", "Unsafe game deleted cache");
            Check(!File.Exists(Path.Combine(modules, "payload.dll")), "Unsafe game wrote before preflight");
            string goodOverlay = Path.Combine(root, "good-overlay.zip");
            MakeOverlay(goodOverlay, "hkrpg_bilibili");
            var reports = new List<LocalInstallProgress>();
            int callerThread = Environment.CurrentManagedThreadId;
            bool workerObserved = false;
            var originalContext = SynchronizationContext.Current;
            Task<LocalPackageInstallResult> installing;
            try
            {
                // A UI context that cannot pump: installation must never capture it.
                SynchronizationContext.SetSynchronizationContext(new NoPumpContext());
                installing = overlayInstaller.InstallAsync(goodOverlay, LocalPackageKind.Overlay,
                    progress: new LocalInstallProgressReporter(p =>
                    {
                        workerObserved |= Environment.CurrentManagedThreadId != callerThread;
                        reports.Add(p);
                    }));
            }
            finally { SynchronizationContext.SetSynchronizationContext(originalContext); }
            await installing.WaitAsync(TimeSpan.FromSeconds(20));
            Check(workerObserved, "Installation ran on UI thread");
            Check(reports.Any(p => p.Percent is > 2 and <= 45), "Extraction progress missing");
            Check(reports.Any(p => p.Percent == 82) && reports.Last().Percent == 90, "Finalization progress missing");
            double[] percentages = reports.Where(p => p.Percent.HasValue).Select(p => p.Percent!.Value).ToArray();
            Check(percentages.Zip(percentages.Skip(1)).All(p => p.First <= p.Second), "Progress moved backwards");
            Check(File.ReadAllText(Path.Combine(cache, "games", ".pending-gamepacks", "hkrpg_bilibili", "ini_config", "nested", "preset.ini")) == "nested", "Pending staging dropped subdirectory");

            string extraction = Path.Combine(root, "cancel-extract");
            using var cancelExtract = new CancellationTokenSource();
            cancelExtract.Cancel();
            Reject(() => HoYoShadeHub.Extensions.Archives.ZipExtractor.ExtractToDirectory(goodOverlay, extraction,
                cancelExtract.Token, _ => { }));
            Check(!Directory.EnumerateFiles(extraction, "*", SearchOption.AllDirectories).Any(), "Canceled extraction wrote files");

            string traversal = Path.Combine(root, "traversal.zip");
            using (var zip = ZipFile.Open(traversal, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open())) writer.Write("escape");
            Reject(() => HoYoShadeHub.Extensions.Archives.ZipExtractor.ExtractToDirectory(traversal,
                Path.Combine(root, "traversal-extract"), default, _ => { }));
            Check(!File.Exists(Path.Combine(root, "escape.txt")), "ZIP traversal escaped root");

            string largeOverlay = Path.Combine(root, "large-overlay.zip");
            using (var zip = ZipFile.Open(largeOverlay, ZipArchiveMode.Create))
            {
                using (var writer = new StreamWriter(zip.CreateEntry("filelist.json").Open()))
                    writer.Write("{\"hysxOverlay\":1,\"targets\":[{\"from\":\"Payload\",\"to\":\"modules\"}]}");
                byte[] bytes = new byte[128 * 1024];
                Random.Shared.NextBytes(bytes);
                for (int i = 0; i < 80; i++)
                {
                    using var stream = zip.CreateEntry($"Payload/bulk/{i}.bin", CompressionLevel.NoCompression).Open();
                    for (int chunk = 0; chunk < 4; chunk++) stream.Write(bytes);
                }
            }
            reports.Clear();
            await overlayInstaller.InstallAsync(largeOverlay, LocalPackageKind.Overlay,
                progress: new LocalInstallProgressReporter(reports.Add)).WaitAsync(TimeSpan.FromSeconds(30));
            Check(Directory.GetFiles(Path.Combine(modules, "bulk")).Length == 80, "Large overlay copy incomplete");
            Check(reports.Any(p => p.Percent is >= 45 and < 82), "Copy progress missing");

            string lockedFile = Path.Combine(modules, "payload.dll");
            using (File.Open(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var partial = await overlayInstaller.InstallAsync(goodOverlay, LocalPackageKind.Overlay).WaitAsync(TimeSpan.FromSeconds(20));
                Check(partial.Details.Any(d => d.Contains("占用")), "Locked file failure not surfaced");
            }
            Check(!Directory.EnumerateFiles(modules, "*.hysx-new-*", SearchOption.AllDirectories).Any(), "Staging copy leaked after failure");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    private sealed class NoPumpContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
            => throw new InvalidOperationException("Worker captured UI synchronization context");
    }

    private static void MakeOverlay(string path, string game)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var pair in new[]
        {
            ("filelist.json", System.Text.Json.JsonSerializer.Serialize(new { hysxOverlay = 1, game, targets = new[] { new { from = "Payload", to = "modules" } } })),
            ("Payload/payload.dll", "payload"),
            ("GamePack/ini_config/nested/preset.ini", "nested")
        })
        {
            using var writer = new StreamWriter(zip.CreateEntry(pair.Item1).Open());
            writer.Write(pair.Item2);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected operation to reject unsafe input");
    }
    private static async Task RejectAsync(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected installation failure");
    }
}
