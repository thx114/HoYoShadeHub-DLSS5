using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using HoYoShadeHub.Extensions.Dlls;

namespace HoYoShadeHub.Extensions.Tests;

internal static class NrRuntimeTests
{
    public static async Task<int> RunAsync(string[] args)
    {
        int failed = 0, passed = 0;
        void Check(bool ok, string name)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
            if (ok) passed++; else failed++;
        }
        var family = new DllFamily("dlssnr", "NR", "nvngx_dlssnr.dll", DllRequirementLevel.Required, "");
        DllComponent[] components = [new("dlssnr", "310.8.3 (50xx)", "old"), .. NrRuntimeCatalog.Components];
        foreach (string gpu in new[] { "turing", "ampere", "TURING" })
            Check(DllSelectionPolicy.SelectDefault(family, components, gpu)?.Version == NrRuntimeCatalog.F5Version, $"{gpu} defaults to F5");
        foreach (string? gpu in new[] { "ada", "blackwell", "pascal", "rdna3", null, "" })
            Check(DllSelectionPolicy.SelectDefault(family, components, gpu)?.Version == NrRuntimeCatalog.SfVersion, $"{gpu ?? "unknown"} defaults to SF");
        Check(DllSelectionPolicy.SelectDefault(family, [components[0]], "ada") is null, "missing safe default never silently picks old 50xx");
        Check(DllSelectionPolicy.SelectDefault(family, [NrRuntimeCatalog.Components[1]], "ada") is null, "40/50 never default to F5");
        var sl = new DllFamily("streamline", "SL", "sl.*.dll", DllRequirementLevel.Required, "", "2.14.0.0");
        Check(DllSelectionPolicy.SelectDefault(sl, [new("streamline", "2.14.16.0", "new"), new("streamline", "2.14.0.0", "safe")], "ada")?.Version == "2.14.0.0", "Streamline preferred version preserved");

        string root = Path.Combine(Path.GetTempPath(), "nr-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string old = Path.Combine(root, "old", "nvngx_dlssnr.dll");
            string active = Path.Combine(root, "active", "nvngx_dlssnr.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(old)!);
            Directory.CreateDirectory(Path.GetDirectoryName(active)!);
            File.WriteAllText(old, "aaaa");
            File.WriteAllText(active, "bbbb");
            var store = new DllVersionStore(Path.Combine(root, "cache"));
            store.Archive("dlssnr", "310.8.SF", [old]);
            var installed = new InstalledDll("nvngx_dlssnr.dll", 4, "310.8.0.0") { Path = active };
            Check(!DllVariantResolver.Identify(store, family, installed, "310.8.SF", 4).Known,
                "same PE and size with different bytes does not trust stale record or archive");
            Check(!DllVariantResolver.Identify(null, family, installed, "310.8.0 (50xx)", 4).Known,
                "unverified real file does not inherit 50xx hardware label");
            File.WriteAllText(active, "aaaa");
            Check(DllVariantResolver.Identify(store, family, installed, null, 0).Variant == "310.8.SF",
                "legacy variant recovered by archive hash");
            Check(!DllVariantResolver.Identify(store, family, installed with { Path = active + ".missing" }, null, 0).Known,
                "unreadable file does not pass archive hash check");

            if (args.Length < 2) throw new ArgumentException("Provide the directory containing the two normalized release ZIPs.");
            string assets = Path.GetFullPath(args[1]);
            foreach (DllComponent component in NrRuntimeCatalog.Components)
            {
                string zip = Path.Combine(assets, Path.GetFileName(new Uri(component.Url).LocalPath));
                using (var stream = File.OpenRead(zip))
                    Check(Convert.ToHexString(SHA256.HashData(stream)) == component.Sha256, component.Version + " ZIP SHA256");
                string extracted = Path.Combine(root, component.Version);
                using (var archive = ZipFile.OpenRead(zip))
                    Check(archive.Entries.Count == 1 && archive.Entries[0].FullName == "nvngx_dlssnr.dll", component.Version + " standard ZIP layout");
                ZipFile.ExtractToDirectory(zip, extracted);
                InstalledDll dll = DllInstaller.Scan(extracted).Single();
                Check(DllVersion.IsSame(dll.Version, "310.9.16"), component.Version + " real PE 310.9.16");
                Check(DllVariantResolver.Identify(null, family, dll, "310.8.3 (50xx)", dll.Size).Variant == component.Version,
                    component.Version + " fingerprint overrides stale 50xx record without archive");
                Check(DllVariantResolver.Identify(null, family, dll, component.Version == NrRuntimeCatalog.SfVersion
                    ? NrRuntimeCatalog.F5Version : NrRuntimeCatalog.SfVersion, dll.Size).Variant == component.Version,
                    component.Version + " fingerprint overrides wrong variant with same PE");

                // Exercise the actual download/hash/extraction path against the supplied ZIP.
                var portProbe = new TcpListener(IPAddress.Loopback, 0);
                portProbe.Start();
                int port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
                portProbe.Stop();
                using var listener = new HttpListener();
                string url = $"http://127.0.0.1:{port}/";
                listener.Prefixes.Add(url);
                listener.Start();
                async Task Serve()
                {
                    var context = await listener.GetContextAsync().WaitAsync(TimeSpan.FromSeconds(60));
                    context.Response.ContentLength64 = new FileInfo(zip).Length;
                    await using var input = File.OpenRead(zip);
                    try { await input.CopyToAsync(context.Response.OutputStream); }
                    finally { context.Response.Close(); }
                }
                string target = Path.Combine(root, "install-" + component.Version);
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "nvngx_dlssnr.dll"), "previous-runtime");
                Task serving = Serve();
                DllInstallResult bad = await DllInstaller.InstallAsync(target, component with { Url = url, Sha256 = new string('0', 64) });
                await serving;
                Check(!bad.Ok && File.ReadAllText(Path.Combine(target, "nvngx_dlssnr.dll")) == "previous-runtime",
                    component.Version + " rejected hash leaves existing runtime intact");
                serving = Serve();
                DllInstallResult good = await DllInstaller.InstallAsync(target, component with { Url = url });
                await serving;
                Check(good.Ok && good.Installed.SequenceEqual(new[] { "nvngx_dlssnr.dll" }), component.Version + " actual ZIP install");
                Check(DllVariantResolver.Identify(null, family, DllInstaller.Scan(target).Single(), null, 0).Variant == component.Version,
                    component.Version + " installed DLL content verified");
                listener.Stop();
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"NR runtime tests: PASS {passed}, FAIL {failed}");
        return failed == 0 ? 0 : 1;
    }
}
