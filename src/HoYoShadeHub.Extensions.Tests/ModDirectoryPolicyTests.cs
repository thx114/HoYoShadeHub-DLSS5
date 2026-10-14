using System.IO.Compression;
using System.Text.Json;
using HoYoShadeHub.Extensions.Games;

namespace HoYoShadeHub.Extensions.Tests;

/// <summary>Independent fixture runner: no live configuration, game processes, or shared test-host edits.</summary>
internal static class ModDirectoryPolicyTests
{
    private sealed record Fixture(string Root, string Config, string Backend, string Mods, string Exe,
        ModDirectorySession Session, ModDirectoryLease Lease);
    private static string _root = string.Empty;
    private static readonly List<object> Results = [];
    private static int _passed, _failed;

    public static int Run(string root, string? reparseFixture = null)
    {
        _root = Path.GetFullPath(root);
        if (Directory.Exists(_root)) throw new IOException("Fresh isolated fixture root required.");
        Directory.CreateDirectory(_root);
        Test("Rocket resolves configured target and Include, not backend-directory name", () =>
        {
            var f = New("resolve");
            Check(f.Lease.Context.ModsDirectory == f.Mods && f.Lease.Context.InstanceDirectory == Path.GetDirectoryName(f.Backend), "wrong actual directory");
            Check(f.Lease.Context.DisableStyle == ModDisableStyle.Prefix, "did not read prefix exclusion");
        });
        Test("Rocket preserves config and backend bytes across fixture management", () =>
        {
            var f = New("ini-stable"); byte[] config = File.ReadAllBytes(f.Config), backend = File.ReadAllBytes(f.Backend);
            string p = Mod(f, "sample"); p = ModDirectoryPolicy.SetEnabled(f.Session, f.Lease, p, false);
            ModDirectoryPolicy.SetEnabled(f.Session, f.Lease, p, true);
            Check(config.SequenceEqual(File.ReadAllBytes(f.Config)) && backend.SequenceEqual(File.ReadAllBytes(f.Backend)), "INI mutated");
        });
        Test("Rocket prefix disable/enable is idempotent", () =>
        {
            var f = New("prefix"); string p = Mod(f, "Alice");
            string disabled = ModDirectoryPolicy.SetEnabled(f.Session, f.Lease, p, false);
            Check(Path.GetFileName(disabled) == "DISABLED Alice", "wrong disable convention");
            Check(ModDirectoryPolicy.SetEnabled(f.Session, f.Lease, disabled, false) == disabled, "not idempotent");
            Check(ModDirectoryPolicy.SetEnabled(f.Session, f.Lease, disabled, true) == p, "failed enable");
        });
        Test("Rocket suffix DISABLED is not falsely treated as disabled", () =>
        {
            var f = New("suffix-not-rocket"); Mod(f, "Alice DISABLED"); Mod(f, "DISABLEDBob");
            var entries = ModDirectoryPolicy.List(f.Session, f.Lease);
            Check(entries.Single(e => e.Name == "Alice DISABLED").Enabled, "guessed suffix");
            Check(!entries.Single(e => e.Name == "DISABLEDBob").Enabled, "missed actual wildcard prefix");
        });
        Test("XXMI existing suffix convention retained; missing Mods created only by explicit import", () =>
        {
            var root = Sub("xxmi"); string instance = Path.Combine(root, "MI"); Directory.CreateDirectory(instance);
            File.WriteAllText(Path.Combine(instance, "d3dx.ini"), "[Loader]\ntarget=Example.exe\n");
            var session = new ModDirectorySession(); long revision = session.Select("xxmi-game", ModBackend.Xxmi);
            var lease = session.Bind(revision, ModDirectoryPolicy.ResolveXxmi("xxmi-game", instance));
            Check(!Directory.Exists(lease.Context.ModsDirectory) && ModDirectoryPolicy.List(session, lease).Count == 0, "guessed/created on list");
            string source = Path.Combine(root, "Alice"); Directory.CreateDirectory(source); File.WriteAllText(Path.Combine(source, "a.ini"), "KEEP");
            string p = ModDirectoryPolicy.ImportFolder(session, lease, source);
            string disabled = ModDirectoryPolicy.SetEnabled(session, lease, p, false);
            Check(Path.GetFileName(disabled) == "Alice DISABLED", "XXMI convention changed");
            Check(ModDirectoryPolicy.SetEnabled(session, lease, disabled, true) == p, "XXMI enable failed");
        });
        Test("Top-level disabled ini files remain listable", () =>
        {
            var f = New("ini-files"); File.WriteAllText(Path.Combine(f.Mods, "DISABLED sample.ini"), "KEEP");
            var item = ModDirectoryPolicy.List(f.Session, f.Lease).Single(); Check(!item.Enabled && !item.IsDirectory, "disabled ini lost");
        });
        Test("Internal cache/staging directories stay hidden", () =>
        {
            var f = New("internal"); Directory.CreateDirectory(Path.Combine(f.Mods, "_cache")); Directory.CreateDirectory(Path.Combine(f.Mods, ".hidden")); Mod(f, "visible");
            Check(ModDirectoryPolicy.List(f.Session, f.Lease).Count == 1, "internal directory exposed");
        });
        Test("Missing selected game rejects without directory creation", () =>
        {
            var f = New("no-game"); Reject(() => ModDirectoryPolicy.ResolveRocket("owner", f.Config, null, f.Exe));
        });
        Test("Configured executable must match selected game path exactly", () =>
        {
            var f = New("wrong-owner"); Reject(() => ModDirectoryPolicy.ResolveRocket("owner", f.Config, "Fixture Game", Path.Combine(f.Root, "Other.exe")));
        });
        Test("Missing configured path is not inferred from package name", () =>
        {
            var f = New("missing-path"); File.WriteAllText(f.Config, "[Fixture Game]\nFixture Game包名=Example.exe\n"); Reject(() => Resolve(f));
        });
        Test("Duplicate configured executable is ambiguous", () =>
        {
            var f = New("duplicate-path"); File.AppendAllText(f.Config, "Fixture Game路径=" + f.Exe + "\n"); Reject(() => Resolve(f));
        });
        Test("Zero backend target matches rejects", () =>
        {
            var f = New("zero-backend"); File.WriteAllText(f.Backend, File.ReadAllText(f.Backend).Replace("Example.exe", "Other.exe")); Reject(() => Resolve(f));
        });
        Test("Multiple matching backends reject rather than choosing a prefix", () =>
        {
            var f = New("multiple-backends"); string other = Path.Combine(f.Root, "backing", "Config", "UnrelatedName", "d3dx.ini");
            Directory.CreateDirectory(Path.GetDirectoryName(other)!); File.Copy(f.Backend, other); Reject(() => Resolve(f));
        });
        Test("Duplicate backend target rejects", () =>
        {
            var f = New("duplicate-target"); File.WriteAllText(f.Backend, File.ReadAllText(f.Backend).Replace("target = Example.exe", "target = Example.exe\ntarget = Example.exe")); Reject(() => Resolve(f));
        });
        Test("Missing recursive Include rejects", () =>
        {
            var f = New("missing-include"); File.WriteAllText(f.Backend, File.ReadAllText(f.Backend).Replace("include_recursive", "unsupported_key")); Reject(() => Resolve(f));
        });
        Test("Multiple recursive Includes reject", () =>
        {
            var f = New("multiple-includes"); File.AppendAllText(f.Backend, "include_recursive=..\\..\\Other\n"); Reject(() => Resolve(f));
        });
        Test("Relative Include cannot escape backing", () =>
        {
            var f = New("include-traversal"); ReplaceInclude(f, "..\\..\\..\\escaped"); Reject(() => Resolve(f)); Check(!Directory.Exists(Path.Combine(f.Root, "escaped")), "escape created");
        });
        Test("Missing Rocket Mods is not auto-created", () =>
        {
            var f = New("missing-mods"); ReplaceInclude(f, "..\\..\\NeverCreated"); Reject(() => Resolve(f)); Check(!Directory.Exists(Path.Combine(f.Root, "backing", "NeverCreated")), "guessed directory created");
        });
        Test("Missing or unrecognized exclusion does not guess disable rules", () =>
        {
            var f = New("unknown-rule"); File.WriteAllText(f.Backend, File.ReadAllText(f.Backend).Replace("DISABLED*", "*DISABLED")); Reject(() => Resolve(f));
        });
        Test("Additional unsupported exclusion fails closed", () =>
        {
            var f = New("extra-rule"); File.AppendAllText(f.Backend, "exclude_recursive = Hidden*\n"); Reject(() => Resolve(f));
        });
        Test("Explicit absolute configured Include is respected, not a guessed Mods prefix", () =>
        {
            var f = New("absolute-include"); string actual = Path.Combine(f.Root, "UserChosenDirectory"); Directory.CreateDirectory(actual);
            ReplaceInclude(f, actual); Check(Resolve(f).ModsDirectory == actual, "absolute actual path not respected");
        });
        Test("Quoted configured Include and CRLF parse", () =>
        {
            var f = New("quoted"); ReplaceInclude(f, "\"..\\..\\UserSelectedMods\" ; retained comment");
            File.WriteAllText(f.Backend, File.ReadAllText(f.Backend).Replace("\n", "\r\n")); Check(Resolve(f).ModsDirectory == f.Mods, "CRLF/quote/comment parsing failed");
        });
        Test("Quoted Include preserves comment characters inside actual folder name", () =>
        {
            var f = New("quoted-comment-name"); string actual = Path.Combine(f.Root, "backing", "User # Mods"); Directory.CreateDirectory(actual);
            ReplaceInclude(f, "\"..\\..\\User # Mods\" ; comment"); Check(Resolve(f).ModsDirectory == actual, "quoted path was truncated");
        });
        Test("Stale config and changed backend reject operations", () =>
        {
            var f = New("config-cas"); string p = Mod(f, "Alice"); File.AppendAllText(f.Config, "; changed\n");
            Reject(() => ModDirectoryPolicy.SetEnabled(f.Session, f.Lease, p, false)); Check(Directory.Exists(p), "stale operation wrote");
            var g = New("backend-cas"); string q = Mod(g, "Alice"); File.AppendAllText(g.Backend, "; changed\n"); Reject(() => ModDirectoryPolicy.Delete(g.Session, g.Lease, q)); Check(Directory.Exists(q), "changed backend deleted mod");
        });
        Test("Added competing backend invalidates existing lease", () =>
        {
            var f = New("backend-set-cas"); string p = Mod(f, "Alice");
            string extra = Path.Combine(f.Root, "backing", "Config", "NewBackend", "d3dx.ini"); Directory.CreateDirectory(Path.GetDirectoryName(extra)!); File.Copy(f.Backend, extra);
            Reject(() => ModDirectoryPolicy.SetEnabled(f.Session, f.Lease, p, false)); Check(Directory.Exists(p), "changed backend set wrote");
        });
        Test("Game switch invalidates old dialog/picker lease", () =>
        {
            var f = New("game-switch"); string p = Mod(f, "Alice"); f.Session.Select("another-game", ModBackend.Rocket);
            Reject(() => ModDirectoryPolicy.SetEnabled(f.Session, f.Lease, p, false)); Reject(() => ModDirectoryPolicy.Delete(f.Session, f.Lease, p)); Check(Directory.Exists(p), "stale game wrote");
        });
        Test("Backend switch invalidates old lease and stale async bind", () =>
        {
            var f = New("backend-switch"); string p = Mod(f, "Alice"); long old = f.Lease.Revision;
            f.Session.Select("owner", ModBackend.Xxmi); Reject(() => f.Session.Bind(old, f.Lease.Context)); Reject(() => ModDirectoryPolicy.Delete(f.Session, f.Lease, p));
        });
        Test("Same-game refresh invalidates earlier operation", () =>
        {
            var f = New("refresh"); string p = Mod(f, "Alice"); f.Session.Select("owner", ModBackend.Rocket); Reject(() => ModDirectoryPolicy.Delete(f.Session, f.Lease, p));
        });
        Test("Bind cannot attach another owner or backend", () =>
        {
            var f = New("wrong-bind"); long revision = f.Session.Select("owner", ModBackend.Rocket);
            Reject(() => f.Session.Bind(revision, f.Lease.Context with { OwnerKey = "wrong-game" }));
            Reject(() => f.Session.Bind(revision, f.Lease.Context with { Backend = ModBackend.Xxmi }));
        });
        Test("Sibling-prefix and traversal entry paths reject", () =>
        {
            var f = New("entry-traversal"); string outside = Path.Combine(f.Mods + "Sibling", "Alice"); Directory.CreateDirectory(outside);
            Reject(() => ModDirectoryPolicy.Delete(f.Session, f.Lease, outside));
            Reject(() => ModDirectoryPolicy.Delete(f.Session, f.Lease, Path.Combine(f.Mods, "Alice", "..", "victim"))); Check(Directory.Exists(outside), "outside deleted");
        });
        Test("Root and internal reserved entry cannot be deleted", () =>
        {
            var f = New("root-delete"); Reject(() => ModDirectoryPolicy.Delete(f.Session, f.Lease, f.Mods));
            string hidden = Path.Combine(f.Mods, "_cache"); Directory.CreateDirectory(hidden); Reject(() => ModDirectoryPolicy.Delete(f.Session, f.Lease, hidden));
        });
        Test("Enable conflict preserves both original directories", () =>
        {
            var f = New("enable-conflict"); string a = Mod(f, "Alice"), b = Mod(f, "DISABLED Alice");
            Reject(() => ModDirectoryPolicy.SetEnabled(f.Session, f.Lease, b, true)); Check(Directory.Exists(a) && Directory.Exists(b), "conflict overwritten");
        });
        Test("Disable file conflict never overwrites existing ini", () =>
        {
            var f = New("file-conflict"); string a = Path.Combine(f.Mods, "a.ini"), b = Path.Combine(f.Mods, "DISABLED a.ini"); File.WriteAllText(a, "A"); File.WriteAllText(b, "B");
            Reject(() => ModDirectoryPolicy.SetEnabled(f.Session, f.Lease, a, false)); Check(File.ReadAllText(a) == "A" && File.ReadAllText(b) == "B", "file overwritten");
        });
        Test("Folder import is byte-exact and source remains intact", () =>
        {
            var f = New("folder-import"); string source = Source(f, "Imported"); string p = ModDirectoryPolicy.ImportFolder(f.Session, f.Lease, source);
            Check(File.ReadAllText(Path.Combine(p, "a.ini")) == "UNCHANGED" && Directory.Exists(source), "copy changed/moved source");
        });
        Test("Folder duplicate target rejects without overwrite", () =>
        {
            var f = New("folder-conflict"); string source = Source(f, "Imported"); Mod(f, "Imported"); Reject(() => ModDirectoryPolicy.ImportFolder(f.Session, f.Lease, source));
        });
        Test("Folder source and destination overlap rejects", () =>
        {
            var f = New("folder-overlap"); Reject(() => ModDirectoryPolicy.ImportFolder(f.Session, f.Lease, f.Mods)); Reject(() => ModDirectoryPolicy.ImportFolder(f.Session, f.Lease, f.Root));
        });
        Test("ZIP import is explicit and byte-exact", () =>
        {
            var f = New("zip-import"); string zip = Zip(f, "Imported.zip", [("nested/a.ini", "UNCHANGED")]);
            string p = ModDirectoryPolicy.ImportZip(f.Session, f.Lease, zip); Check(File.ReadAllText(Path.Combine(p, "nested", "a.ini")) == "UNCHANGED", "zip content changed");
        });
        foreach (string bad in new[] { "../escape.ini", "/root.ini", "C:/escape.ini", "a.ini:ads", "dir/../escape.ini", "dir\\..\\escape.ini", "CON.ini", "dir./a.ini" })
            Test("ZIP rejects unsafe path " + bad, () => { var f = New("zip-path-" + (_passed + _failed)); string zip = Zip(f, "Unsafe.zip", [(bad, "UNSAFE")]); Reject(() => ModDirectoryPolicy.ImportZip(f.Session, f.Lease, zip)); Check(!Directory.Exists(Path.Combine(f.Mods, "Unsafe")), "unsafe zip published"); });
        Test("ZIP case-insensitive duplicate path rejects", () =>
        {
            var f = New("zip-duplicate"); string zip = Zip(f, "Dup.zip", [("a.ini", "A"), ("A.ini", "B")]); Reject(() => ModDirectoryPolicy.ImportZip(f.Session, f.Lease, zip));
        });
        Test("ZIP file/directory overlap rejects before write", () =>
        {
            var f = New("zip-file-directory"); string zip = Zip(f, "Dup.zip", [("folder", "FILE"), ("folder/a.ini", "A")]); Reject(() => ModDirectoryPolicy.ImportZip(f.Session, f.Lease, zip));
        });
        Test("ZIP symlink entry rejects", () =>
        {
            var f = New("zip-symlink"); string zip = Path.Combine(f.Root, "Link.zip");
            using (var a = ZipFile.Open(zip, ZipArchiveMode.Create)) { var e = a.CreateEntry("link.ini"); e.ExternalAttributes = (0xa000 | 0x1ff) << 16; using var w = new StreamWriter(e.Open()); w.Write("outside"); }
            Reject(() => ModDirectoryPolicy.ImportZip(f.Session, f.Lease, zip));
        });
        Test("ZIP destination conflict refuses overwrite", () =>
        {
            var f = New("zip-conflict"); string zip = Zip(f, "Existing.zip", [("a.ini", "A")]); string p = Mod(f, "Existing"); Reject(() => ModDirectoryPolicy.ImportZip(f.Session, f.Lease, zip)); Check(Directory.Exists(p), "old mod lost");
        });
        Test("Stale import lease cannot create directories", () =>
        {
            var f = New("import-stale"); string source = Source(f, "Imported"); f.Session.Select("new-game", ModBackend.Rocket); Reject(() => ModDirectoryPolicy.ImportFolder(f.Session, f.Lease, source)); Check(!Directory.Exists(Path.Combine(f.Mods, "Imported")), "stale import wrote");
        });
        Test("Explicit delete is confined to owned fixture entry", () =>
        {
            var f = New("delete"); string p = Mod(f, "DeleteMe"), keep = Mod(f, "KeepMe"); ModDirectoryPolicy.Delete(f.Session, f.Lease, p); Check(!Directory.Exists(p) && Directory.Exists(keep), "delete escaped owner");
        });
        Test("Real junction ancestor rejects XXMI resolution", () =>
        {
            Check(reparseFixture is not null, "Required isolated junction fixtures missing");
            Reject(() => ModDirectoryPolicy.ResolveXxmi("owner", Path.Combine(reparseFixture!, "link1")));
        });
        Test("Real junction source import rejects", () =>
        {
            var f = New("junction-source"); Reject(() => ModDirectoryPolicy.ImportFolder(f.Session, f.Lease, Path.Combine(reparseFixture!, "link2")));
        });
        Test("Real junction top-level Mod refuses list/delete", () =>
        {
            var f = New("junction-entry"); string link = Path.Combine(f.Mods, "LinkedMod"); Directory.Move(Path.Combine(reparseFixture!, "link3"), link);
            Reject(() => ModDirectoryPolicy.List(f.Session, f.Lease)); Reject(() => ModDirectoryPolicy.Delete(f.Session, f.Lease, link));
        });
        Test("Real nested junction refuses recursive delete and import", () =>
        {
            var f = New("junction-nested"); string p = Mod(f, "NestedLink"); Directory.Move(Path.Combine(reparseFixture!, "link4"), Path.Combine(p, "nested"));
            Reject(() => ModDirectoryPolicy.Delete(f.Session, f.Lease, p)); Reject(() => ModDirectoryPolicy.ImportFolder(f.Session, f.Lease, p));
            Check(Directory.Exists(p), "unsafe tree deleted");
        });
        Test("Real config-ancestor junction rejects Rocket resolution", () =>
        {
            var f = New("junction-config"); Reject(() => ModDirectoryPolicy.ResolveRocket("owner", Path.Combine(reparseFixture!, "link5", "config.ini"), "Fixture Game", f.Exe));
        });
        Test("Real Mod-root replacement by junction invalidates active lease", () =>
        {
            var f = New("junction-root"); Directory.Delete(f.Mods); Directory.Move(Path.Combine(reparseFixture!, "link6"), f.Mods);
            Reject(() => ModDirectoryPolicy.List(f.Session, f.Lease));
            Check(File.ReadAllText(Path.Combine(reparseFixture!, "target", "sentinel.txt")) == "DO NOT MODIFY", "junction target changed");
        });
        File.WriteAllText(Path.Combine(_root, "results.json"), JsonSerializer.Serialize(new { passed = _passed, failed = _failed, GameTested = false, fixtureRoot = _root, tests = Results }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"TOTAL PASS={_passed} FAIL={_failed}; fixture only; GameTested=false");
        return _failed == 0 ? 0 : 1;
    }

    private static Fixture New(string name)
    {
        string root = Sub(name), backing = Path.Combine(root, "backing"), backend = Path.Combine(backing, "Config", "Configured-Not-A-Game-Prefix", "d3dx.ini"), mods = Path.Combine(backing, "UserSelectedMods");
        Directory.CreateDirectory(Path.GetDirectoryName(backend)!); Directory.CreateDirectory(mods);
        string exe = Path.Combine(root, "game", "Example.exe"); Directory.CreateDirectory(Path.GetDirectoryName(exe)!); File.WriteAllText(exe, "INERT");
        string config = Path.Combine(backing, "config.ini"); File.WriteAllText(config, "[Fixture Game]\nFixture Game路径=" + exe + "\n");
        File.WriteAllText(backend, "[Loader]\ntarget = Example.exe\n[Include]\ninclude_recursive = ..\\..\\UserSelectedMods\nexclude_recursive = DISABLED*\nexclude_recursive = desktop.ini\n");
        var session = new ModDirectorySession(); long revision = session.Select("owner", ModBackend.Rocket);
        var context = ModDirectoryPolicy.ResolveRocket("owner", config, "Fixture Game", exe);
        return new(root, config, backend, mods, exe, session, session.Bind(revision, context));
    }
    private static ModDirectoryContext Resolve(Fixture f) => ModDirectoryPolicy.ResolveRocket("owner", f.Config, "Fixture Game", f.Exe);
    private static string Sub(string name) { string p = Path.Combine(_root, name); Directory.CreateDirectory(p); return p; }
    private static string Mod(Fixture f, string name) { string p = Path.Combine(f.Mods, name); Directory.CreateDirectory(p); File.WriteAllText(Path.Combine(p, "a.ini"), "UNCHANGED"); return p; }
    private static string Source(Fixture f, string name) { string p = Path.Combine(f.Root, "source", name); Directory.CreateDirectory(p); File.WriteAllText(Path.Combine(p, "a.ini"), "UNCHANGED"); return p; }
    private static string Zip(Fixture f, string name, (string Path, string Data)[] entries)
    {
        string p = Path.Combine(f.Root, name);
        using var archive = ZipFile.Open(p, ZipArchiveMode.Create);
        foreach (var (path, data) in entries) { var e = archive.CreateEntry(path); using var w = new StreamWriter(e.Open()); w.Write(data); }
        return p;
    }
    private static void ReplaceInclude(Fixture f, string value) => File.WriteAllText(f.Backend, File.ReadAllText(f.Backend).Replace("..\\..\\UserSelectedMods", value));
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or ArgumentException) { return; } throw new Exception("unsafe operation was accepted"); }
    private static void Test(string name, Action body)
    {
        try { body(); _passed++; Results.Add(new { name, passed = true }); }
        catch (Exception e) { _failed++; Results.Add(new { name, passed = false, error = e.Message }); Console.WriteLine("FAIL " + name + ": " + e.Message); }
    }
}
