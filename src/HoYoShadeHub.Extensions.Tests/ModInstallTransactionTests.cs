using System.IO.Compression;
using HoYoShadeHub.Extensions.Games;

namespace HoYoShadeHub.Extensions.Tests;

internal static class ModInstallTransactionTests
{
    public static int Run(string root)
    {
        if (Directory.Exists(root)) throw new IOException("Fresh isolated fixture root required.");
        Directory.CreateDirectory(root);
        string instance = Path.Combine(root, "ZZMI");
        Directory.CreateDirectory(instance);
        File.WriteAllText(Path.Combine(instance, "d3dx.ini"), "fixture");
        var context = ModDirectoryPolicy.ResolveXxmi("fixture", instance);
        using var lease = ModInstallTransaction.AcquireAsync(context, 42, default).GetAwaiter().GetResult();
        string destination = Path.Combine(context.ModsDirectory, "gb_42");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "old.ini"), "OLD");
        string broken = Path.Combine(root, "broken.zip");
        File.WriteAllText(broken, "invalid archive");
        Reject(() => ModInstallTransaction.PrepareZip(broken, Path.Combine(root, "bad-stage")));
        Check(File.ReadAllText(Path.Combine(destination, "old.ini")) == "OLD", "Bad archive removed old Mod.");
        string zip = Path.Combine(root, "valid.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var writer = new StreamWriter(archive.CreateEntry("new.ini").Open())) writer.Write("NEW");
        string content = ModInstallTransaction.PrepareZip(zip, Path.Combine(root, "valid-stage"));
        Reject(() => ModInstallTransaction.Commit(context, 42, content, true, () => throw new IOException("Injected publish failure")));
        Check(File.ReadAllText(Path.Combine(destination, "old.ini")) == "OLD", "Rollback lost old Mod.");
        Check(File.ReadAllText(Path.Combine(content, "new.ini")) == "NEW", "Failed publish lost staged Mod.");
        using (var cancellation = new CancellationTokenSource(250))
            Reject(() => { using var blocked = ModInstallTransaction.AcquireAsync(context, 42, cancellation.Token).GetAwaiter().GetResult(); });
        ModInstallTransaction.Commit(context, 42, content, true);
        Check(File.ReadAllText(Path.Combine(destination, "new.ini")) == "NEW", "Valid replacement failed.");
        Check(Directory.GetDirectories(Path.Combine(context.ModsDirectory, "_backup")).Length == 1, "Backup was not retained.");
        if (OperatingSystem.IsWindows())
        {
            string outside = Path.Combine(root, "outside");
            Directory.CreateDirectory(outside);
            string link = Path.Combine(context.ModsDirectory, "_cache");
            try { Directory.CreateSymbolicLink(link, outside); }
            catch (IOException ex) when ((ex.HResult & 0xffff) == 1314)
            {
                var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                };
                foreach (string arg in new[] { "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(arg);
                using var process = System.Diagnostics.Process.Start(start)!;
                process.WaitForExit();
                if (process.ExitCode != 0) throw new IOException("Could not create isolated junction fixture.");
            }
            Reject(() => ModInstallTransaction.PrepareZip(zip, Path.Combine(link, "stage")));
            Check(!Directory.EnumerateFileSystemEntries(outside).Any(), "Link target was modified.");
            Directory.Delete(link);
        }
        Console.WriteLine("ModInstallTransaction fixtures passed.");
        return 0;
    }

    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    private static void Reject(Action action)
    {
        bool rejected = false;
        try { action(); } catch (Exception) { rejected = true; }
        Check(rejected, "Expected rejection.");
    }
}
