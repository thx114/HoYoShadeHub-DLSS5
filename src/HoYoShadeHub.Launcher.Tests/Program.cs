using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using HoYoShadeHub.Helpers;

if (Environment.GetEnvironmentVariable("HYS_LAUNCHER_FIXTURE") == "1")
{
    await Task.Delay(150);
    Console.WriteLine(JsonSerializer.Serialize(args));
    return 7;
}
if (args.Length == 1 && args[0] == "parent-worker")
{
    await Task.Delay(500);
    return 0;
}

int passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    Console.WriteLine("[PASS] " + name);
    passed++;
}
void Reject(Action action, string name)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException)
    {
        Check(true, name);
        return;
    }
    throw new InvalidOperationException(name);
}
string[] samples = ["", "run", "D:\\Folder With Spaces\\file.ini", "D:\\Folder With Spaces\\", "a\"b",
    "{\"name\":\"a b\"}", "\u539f\u795e"];
string command = LauncherCommandLine.BuildArguments(["fixture.exe", .. samples]);
IntPtr argv = Native.CommandLineToArgvW(command, out int count);
if (argv == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
try
{
    var parsed = Enumerable.Range(1, count - 1).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))).ToArray();
    Check(parsed.SequenceEqual(samples), "Windows argument round trip");
}
finally { Native.LocalFree(argv); }
foreach (string verb in new[] { "collectlogs", "collect-logs", "sessionlogs" })
    Check(LauncherCommandLine.IsCliVerb(verb), "CLI alias " + verb);
Reject(() => LauncherCommandLine.WaitForParentExit(["--wait-for-parent", "bad", "0"]), "Malformed restart identity rejected");

var workerStart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
if (Path.GetFileNameWithoutExtension(workerStart.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
    workerStart.ArgumentList.Add(Assembly.GetEntryAssembly()!.Location);
workerStart.ArgumentList.Add("parent-worker");
using (var worker = Process.Start(workerStart)!)
{
    string[] forwarded = LauncherCommandLine.WaitForParentExit(["run", "--wait-for-parent", worker.Id.ToString(),
        worker.StartTime.ToUniversalTime().Ticks.ToString(), "a b"]);
    Check(worker.HasExited && forwarded.SequenceEqual(["run", "a b"]), "Restart waits for exact parent before forwarding");
}

string root = Path.Combine(Path.GetTempPath(), "hys-launcher-policy-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Check(PortableUpdatePolicy.ReadVersionTarget(root, "exe_path=app-1.2.3/HoYoShadeHub.exe")
        == Path.Combine(root, "app-1.2.3", "HoYoShadeHub.exe"), "Contained version selection");
    Reject(() => PortableUpdatePolicy.GetArchiveTarget(root, "../outside.exe"), "Update path traversal rejected");
    Reject(() => PortableUpdatePolicy.ReadVersionTarget(root, "exe_path=app-a/HoYoShadeHub.exe\nexe_path=app-b/HoYoShadeHub.exe"),
        "Ambiguous version selection rejected");
    string version = Path.Combine(root, "version.ini");
    File.WriteAllText(version, "old");
    using (var held = new FileStream(version, FileMode.Open, FileAccess.Read, FileShare.Read))
        Reject(() => PortableUpdatePolicy.WriteVersionAtomically(version, "new"u8.ToArray()), "Failed version commit reported");
    Check(File.ReadAllText(version) == "old" && Directory.GetFiles(root).Length == 1, "Failed version commit preserves prior bytes");
    PortableUpdatePolicy.WriteVersionAtomically(version, "new"u8.ToArray());
    Check(File.ReadAllText(version) == "new", "Successful version commit");

    foreach (string launcher in args)
    {
        string fixture = Path.Combine(root, "shell-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        string outer = Path.Combine(fixture, "HoYoShadeHub.exe");
        File.Copy(launcher, outer);
        foreach (string file in Directory.EnumerateFiles(Path.GetDirectoryName(launcher)!))
            if (Path.GetExtension(file) is ".dll" or ".json") File.Copy(file, Path.Combine(fixture, Path.GetFileName(file)));
        string app = Path.Combine(fixture, "app-1");
        Directory.CreateDirectory(app);
        foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
            File.Copy(file, Path.Combine(app, Path.GetFileName(file)));
        File.Copy(Path.Combine(AppContext.BaseDirectory, "LauncherPolicyFixture.exe"), Path.Combine(app, "HoYoShadeHub.exe"));
        File.WriteAllText(Path.Combine(fixture, "version.ini"), "exe_path=app-1/HoYoShadeHub.exe");
        string previous = Path.Combine(fixture, "app-previous");
        Directory.CreateDirectory(previous);
        File.WriteAllText(Path.Combine(previous, "keep.txt"), "rollback");
        foreach (string verb in new[] { "collectlogs", "collect-logs", "sessionlogs" })
        {
            string[] input = [verb, .. samples];
            var start = new ProcessStartInfo(outer)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (string arg in input) start.ArgumentList.Add(arg);
            start.Environment["HYS_LAUNCHER_FIXTURE"] = "1";
            using var child = Process.Start(start)!;
            Task<string> output = child.StandardOutput.ReadToEndAsync();
            Task<string> error = child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            string stdout = await output;
            Check(child.ExitCode == 7 && JsonSerializer.Deserialize<string[]>(stdout.Trim())!.SequenceEqual(input),
                Path.GetDirectoryName(launcher) + " " + verb + " output, arguments and exit code");
            Check(File.ReadAllText(Path.Combine(previous, "keep.txt")) == "rollback", "Previous version preserved");
            if (!string.IsNullOrWhiteSpace(await error)) throw new InvalidOperationException(await error);
        }
    }
}
finally { Directory.Delete(root, recursive: true); }
Console.WriteLine($"Startup policy checks: PASS {passed}");
return 0;

internal static class Native
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr memory);
}
