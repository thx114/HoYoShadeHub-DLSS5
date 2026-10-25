using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class NrArchiveDownloadTests
{
    public static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "nr-download-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int passed = 0;
        try
        {
            string fixture = typeof(NrRuntimeLibrary).Assembly.Location;
            using var zipBytes = new MemoryStream();
            using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
            {
                using var entry = zip.CreateEntry(NrRuntimeLibrary.FileName).Open();
                await using var file = File.OpenRead(fixture);
                await file.CopyToAsync(entry);
            }
            byte[] payload = zipBytes.ToArray();
            string sha = Convert.ToHexString(SHA256.HashData(payload));
            string shared = Path.Combine(root, "shared", NrRuntimeLibrary.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(shared)!);
            File.WriteAllText(shared, "keep-current-runtime");
            string cache = Path.Combine(root, "cache");
            async Task<string> Download(string version, string expected)
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var server = Task.Run(async () =>
                {
                    using var client = await listener.AcceptTcpClientAsync();
                    await using var stream = client.GetStream();
                    using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                    while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n"));
                    await stream.WriteAsync(payload);
                });
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    return await NrRuntimeLibrary.DownloadVersionAsync(cache,
                        new DllComponent("dlssnr", version, $"http://127.0.0.1:{port}/nr.zip", Sha256: expected), cancellationToken: timeout.Token);
                }
                finally { listener.Stop(); await server; }
            }
            string archived = await Download("download-fixture", sha);
            void Check(bool ok, string message)
            {
                if (!ok) throw new Exception(message);
                passed++;
                Console.WriteLine("PASS " + message);
            }
            Check(NrRuntimeLibrary.Hash(archived) == NrRuntimeLibrary.Hash(fixture), "downloaded ZIP archive contains verified DLL bytes");
            Check(File.ReadAllText(shared) == "keep-current-runtime", "downloading NR does not change active shared runtime");
            string stored = new DllVersionStore(cache).FilePath("dlssnr", "download-fixture", NrRuntimeLibrary.FileName);
            Check(HysxFileLink.AreHardLinked(stored, archived), "download archive and content store share file ID");
            Check(!Directory.EnumerateDirectories(cache, "nr-download-*").Any(), "download staging is cleaned up");
            bool rejected = false;
            try { await Download("bad-hash-fixture", new string('0', 64)); }
            catch (IOException) { rejected = true; }
            Check(rejected && !new DllVersionStore(cache).Has("dlssnr", "bad-hash-fixture"), "corrupt download is rejected before publication");
            Directory.Delete(new DllVersionStore(cache).DirectoryFor("dlssnr", "download-fixture"), true);
            Check(File.Exists(archived), "content survives loss of version index");
            Check(NrRuntimeLibrary.RecoverContentVersions(cache).Count == 0, "orphaned content index recovery succeeds");
            string recovered = new DllVersionStore(cache).FilePath("dlssnr", "download-fixture", NrRuntimeLibrary.FileName);
            Check(recovered.Length > 0 && HysxFileLink.AreHardLinked(archived, recovered), "recovered version uses original content hardlink");
            Check(NrRuntimeLibrary.RecoverContentVersions(cache).Count == 0 && new DllVersionStore(cache).ListVersions("dlssnr").Count == 1,
                "repeated recovery preserves existing index without duplicates");
            var store = new DllVersionStore(cache);
            string defaultRoot = Path.Combine(root, "opti-build");
            Directory.CreateDirectory(defaultRoot);
            string opti = Path.Combine(defaultRoot, "OptiScaler.dll");
            File.Copy(fixture, opti);
            string defaultNr = Path.Combine(defaultRoot, NrRuntimeLibrary.FileName);
            File.Copy(typeof(HoYoShadeHub.Core.HoYoPlay.GameId).Assembly.Location, defaultNr);
            string prepared = NrRuntimeLibrary.PrepareOpti(cache, "game", "build", opti, null);
            string oldDefault = NrRuntimeLibrary.Intern(cache, defaultNr);
            Check(HysxFileLink.AreHardLinked(defaultNr, oldDefault)
                && HysxFileLink.AreHardLinked(defaultNr, Path.Combine(Path.GetDirectoryName(prepared)!, NrRuntimeLibrary.FileName)),
                "build default and game runtime share canonical content");
            Check(NrRuntimeLibrary.RecoverContentVersions(cache).Count == 0 && store.ListVersions("dlssnr").Count == 1,
                "build default is not promoted to a user version");
            string updated = defaultNr + ".new";
            File.Copy(defaultNr, updated);
            using (var append = File.Open(updated, FileMode.Append)) append.WriteByte(0);
            File.Move(updated, defaultNr, true);
            string newDefault = NrRuntimeLibrary.Intern(cache, defaultNr);
            NrRuntimeLibrary.PrepareOpti(cache, "game", "build", opti, null);
            Check(NrRuntimeLibrary.RecoverContentVersions(cache).Count == 0 && store.ListVersions("dlssnr").Count == 1
                && File.Exists(oldDefault) && !HysxFileLink.AreHardLinked(oldDefault, defaultNr),
                "default update leaves no custom cards or old version cards");
            Check(HysxFileLink.AreHardLinked(newDefault, Path.Combine(Path.GetDirectoryName(prepared)!, NrRuntimeLibrary.FileName)),
                "game follows updated build default via hardlink");
            Check(store.Archive("dlssnr", "imported-default", [defaultNr]) == 1,
                "explicit import may archive the same bytes as a build default");
            Directory.Delete(store.DirectoryFor("dlssnr", "imported-default"), true);
            Check(NrRuntimeLibrary.RecoverContentVersions(cache).Count == 0
                && HysxFileLink.AreHardLinked(defaultNr, store.FilePath("dlssnr", "imported-default", NrRuntimeLibrary.FileName)),
                "explicitly imported default is recoverable under original label");
            Check(store.DeleteVersion("dlssnr", "imported-default")
                && NrRuntimeLibrary.RecoverContentVersions(cache).Count == 0 && store.ListVersions("dlssnr").Count == 1,
                "deleting a user version prevents refresh from resurrecting it");
            Check(HysxFileLink.AreHardLinked(newDefault, defaultNr), "deleting archive leaves build default intact");
            store.Archive("dlssnr", "download-fixture", [defaultNr]);
            Directory.Delete(store.DirectoryFor("dlssnr", "download-fixture"), true);
            Check(NrRuntimeLibrary.RecoverContentVersions(cache).Count == 0 && store.ListVersions("dlssnr").Count == 1
                && HysxFileLink.AreHardLinked(newDefault, store.FilePath("dlssnr", "download-fixture", NrRuntimeLibrary.FileName)),
                "updating one label recovers latest content without exposing superseded content");
            Check(File.Exists(archived), "superseded content remains available to existing hardlinks");
            string emptyCache = Path.Combine(root, "content-only-cache");
            NrRuntimeLibrary.Intern(emptyCache, defaultNr);
            Check(NrRuntimeLibrary.RecoverContentVersions(emptyCache).Count == 0 && new DllVersionStore(emptyCache).ListVersions("dlssnr").Count == 0,
                "cache without explicit registrations creates no user versions");
            string corrupt = Path.Combine(cache, "nr-content", new string('0', 64), NrRuntimeLibrary.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
            File.Copy(fixture, corrupt);
            Check(NrRuntimeLibrary.RecoverContentVersions(cache).Count == 0,
                "unregistered leftover content is not scanned for user versions");
            string badRegistration = Path.Combine(cache, "nr-version-index", "corrupt.json");
            File.WriteAllText(badRegistration, JsonSerializer.Serialize(new { Version = "corrupt", Sha256 = new string('0', 64) }));
            Check(NrRuntimeLibrary.RecoverContentVersions(cache).Count == 1 && store.ListVersions("dlssnr").Count == 1,
                "registered content hash mismatch is reported without publishing a version");
            File.WriteAllText(badRegistration, "broken JSON");
            Check(NrRuntimeLibrary.RecoverContentVersions(cache).Count == 1 && store.ListVersions("dlssnr").Count == 1,
                "invalid registration cannot create a user version");
            File.Delete(badRegistration);
            Check(store.DeleteVersion("dlssnr", "download-fixture")
                && NrRuntimeLibrary.RecoverContentVersions(cache).Count == 0 && store.ListVersions("dlssnr").Count == 0,
                "deleting all explicit versions leaves default and old content out of list");
            Console.WriteLine($"NR archive download tests: PASS {passed}, FAIL 0");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }
}
