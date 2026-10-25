using System.IO.Compression;
using System.Security.Cryptography;

namespace HoYoShadeHub.Extensions.Games;

public enum ModBackend { Xxmi, Rocket }
public enum ModDisableStyle { Suffix, Prefix }
public sealed record ModDirectoryEntry(string Name, string Path, bool IsDirectory, bool Enabled, string DisplayName, string? Category = null);
public sealed record ModConfigStamp(string Path, string Sha256);
public sealed record ModDirectoryContext(string OwnerKey, ModBackend Backend, string InstanceDirectory,
    string ModsDirectory, ModDisableStyle DisableStyle, IReadOnlyList<ModConfigStamp> ConfigStamps);
public sealed record ModDirectoryLease(long Revision, ModDirectoryContext Context);

/// <summary>Page-local ownership fence. A picker/dialog or background enumeration cannot outlive a game/backend switch.</summary>
public sealed class ModDirectorySession
{
    private readonly object _gate = new();
    private long _revision;
    private string _owner = string.Empty;
    private ModBackend _backend;
    private ModDirectoryLease? _lease;

    public long Select(string ownerKey, ModBackend backend)
    {
        lock (_gate) { _owner = ownerKey; _backend = backend; _lease = null; return ++_revision; }
    }

    public bool IsCurrent(long revision) { lock (_gate) return revision == _revision; }
    public bool IsCurrent(ModDirectoryLease lease)
    {
        lock (_gate) return ReferenceEquals(_lease, lease) && lease.Revision == _revision;
    }

    public ModDirectoryLease Bind(long revision, ModDirectoryContext context)
    {
        lock (_gate)
        {
            if (revision != _revision || context.OwnerKey != _owner || context.Backend != _backend)
                throw new InvalidOperationException("游戏或 Mod 后端已切换，请刷新后重试。");
            return _lease = new(revision, context);
        }
    }

    public ModDirectoryContext Require(ModDirectoryLease lease)
    {
        if (!IsCurrent(lease)) throw new InvalidOperationException("游戏或 Mod 后端已切换，旧操作已取消。");
        ModDirectoryPolicy.Validate(lease.Context);
        if (!IsCurrent(lease)) throw new InvalidOperationException("游戏或 Mod 后端在校验期间切换，旧操作已取消。");
        return lease.Context;
    }
}

/// <summary>Read configured Rocket backend target/Include rules; never infer YS/SR/ZZZ paths or rename launch identifiers.</summary>
public static class ModDirectoryPolicy
{
    public static ModDirectoryContext ResolveXxmi(string ownerKey, string instance)
    {
        string root = Absolute(instance);
        NoReparse(root);
        if (!Directory.Exists(root) || (!File.Exists(Path.Combine(root, "d3dx.ini")) && !File.Exists(Path.Combine(root, "d3d11.dll"))))
            throw new InvalidDataException("没有有效的 XXMI 实例，请先选择此游戏的 MI 目录。");
        return new(ownerKey, ModBackend.Xxmi, root, Path.Combine(root, "Mods"), ModDisableStyle.Suffix, []);
    }

    public static ModDirectoryContext ResolveRocket(string ownerKey, string? configPath, string? gameName, string? selectedExe)
    {
        if (string.IsNullOrWhiteSpace(configPath) || string.IsNullOrWhiteSpace(gameName) || string.IsNullOrWhiteSpace(selectedExe))
            throw new InvalidDataException("无法解析 Rocket：请先配置当前游戏路径及 Rocket config.ini；不会猜测或创建 Mod 目录。");
        string config = Absolute(configPath);
        NoReparse(config);
        string configHash = Hash(config);
        var settings = ReadIni(config);
        string configuredExe = One(settings, gameName, gameName + "路径");
        string exe = Absolute(configuredExe);
        if (!SamePath(exe, Absolute(selectedExe)))
            throw new InvalidDataException("Rocket 配置的游戏路径与当前游戏不一致；已拒绝跨游戏管理。");
        string executableName = Path.GetFileName(exe);
        string backing = Path.GetDirectoryName(config)!;
        string configRoot = Path.Combine(backing, "Config");
        NoReparse(configRoot);
        if (!Directory.Exists(configRoot)) throw new InvalidDataException("Rocket 没有已安装的 Config 后端目录。");
        var matches = new List<(string Path, string Hash, List<(string Section, string Key, string Value)> Ini)>();
        var stamps = new List<ModConfigStamp> { new(config, configHash) };
        foreach (string dir in Directory.EnumerateDirectories(configRoot))
        {
            NoReparse(dir);
            string iniPath = Path.Combine(dir, "d3dx.ini");
            if (!File.Exists(iniPath)) continue;
            NoReparse(iniPath);
            string iniHash = Hash(iniPath);
            var ini = ReadIni(iniPath);
            if (Hash(iniPath) != iniHash) throw new IOException("Rocket 后端配置正在改变，请刷新后重试。");
            stamps.Add(new(iniPath, iniHash));
            string[] targets = Values(ini, "Loader", "target");
            if (targets.Any(v => string.Equals(v, executableName, StringComparison.OrdinalIgnoreCase)))
            {
                if (targets.Length != 1) throw new InvalidDataException("Rocket 后端的 target 重复，不能确定当前游戏。");
                matches.Add((iniPath, iniHash, ini));
            }
        }
        if (matches.Count != 1) throw new InvalidDataException("Rocket 当前游戏的实际后端不唯一或未安装；不会猜测 Mod 目录。");
        var match = matches[0];
        string include = One(match.Ini, "Include", "include_recursive");
        string mods;
        if (Path.IsPathFullyQualified(include)) mods = Absolute(include);
        else
        {
            // Rocket's real relative Include commonly contains ../..; canonical result must remain under backing.
            if (Path.IsPathRooted(include) || include.Contains(':')) throw new InvalidDataException("Rocket Include 路径不安全。");
            mods = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(match.Path)!, include));
            if (!Inside(mods, backing)) throw new InvalidDataException("Rocket Include 穿越了安装目录，拒绝管理。");
            mods = Absolute(mods);
        }
        if (SamePath(mods, backing) || Inside(mods, configRoot) || SamePath(mods, configRoot))
            throw new InvalidDataException("Rocket Include 不是独立的 Mod 目录。");
        string[] rules = Values(match.Ini, "Include", "exclude_recursive");
        // Only observed/supported loader exclusion rules are actionable. Unknown formats fail closed.
        string[] disabledRules = rules.Where(v => v.Contains("DISABLED", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (disabledRules.Length != 1 || !string.Equals(disabledRules[0], "DISABLED*", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Rocket 后端的禁用规则无法确认（需要明确 DISABLED*），不猜测前后缀。");
        if (rules.Any(r => !string.Equals(r, "DISABLED*", StringComparison.OrdinalIgnoreCase) && !string.Equals(r, "desktop.ini", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Rocket 有未支持的排除规则，无法安全判断 Mod 启禁状态。");
        NoReparse(mods);
        if (!Directory.Exists(mods)) throw new InvalidDataException("Rocket 已配置的 Mod 目录不存在，请先在 Rocket 中创建/配置；本页不会自动创建。");
        if (Hash(config) != configHash || Hash(match.Path) != match.Hash)
            throw new IOException("Rocket 配置在解析期间改变，请刷新后重试。");
        return new(ownerKey, ModBackend.Rocket, Path.GetDirectoryName(match.Path)!, mods,
            ModDisableStyle.Prefix, stamps.AsReadOnly());
    }

    public static void Validate(ModDirectoryContext context)
    {
        NoReparse(context.InstanceDirectory);
        NoReparse(context.ModsDirectory);
        foreach (var stamp in context.ConfigStamps)
        {
            NoReparse(stamp.Path);
            if (!File.Exists(stamp.Path) || Hash(stamp.Path) != stamp.Sha256)
                throw new InvalidOperationException("Mod 后端配置已改变，请刷新后重试；未修改 Rocket INI。");
        }
        if (context.Backend == ModBackend.Rocket && !Directory.Exists(context.ModsDirectory))
            throw new DirectoryNotFoundException("Rocket Mod 目录已不存在；不会自动创建。");
        if (context.Backend == ModBackend.Rocket)
        {
            string root = Path.Combine(Path.GetDirectoryName(context.ConfigStamps[0].Path)!, "Config");
            var actual = new List<string>();
            foreach (string dir in Directory.EnumerateDirectories(root))
            {
                NoReparse(dir);
                string ini = Path.Combine(dir, "d3dx.ini");
                if (File.Exists(ini)) actual.Add(ini);
            }
            if (!actual.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(
                context.ConfigStamps.Skip(1).Select(s => s.Path).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException("Rocket 后端目录集合已改变，请重新解析当前游戏。");
        }
    }

    public static bool IsEnabled(ModDirectoryContext context, string name) => context.DisableStyle == ModDisableStyle.Prefix
        ? !name.StartsWith("DISABLED", StringComparison.OrdinalIgnoreCase)
        : !name.TrimEnd().EndsWith("DISABLED", StringComparison.OrdinalIgnoreCase);

    public static string DisplayName(ModDirectoryContext context, string name)
    {
        string result = name.TrimEnd();
        if (context.DisableStyle == ModDisableStyle.Prefix)
        {
            while (result.StartsWith("DISABLED", StringComparison.OrdinalIgnoreCase)) result = result[8..].TrimStart();
        }
        else
        {
            while (result.EndsWith("DISABLED", StringComparison.OrdinalIgnoreCase)) result = result[..^8].TrimEnd();
        }
        return result;
    }

    public static bool IsCategoryDirectory(string path)
        => Directory.Exists(path) && !Directory.EnumerateFiles(path, "*.ini")
            .Any(p => !Path.GetFileName(p).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase));

    public static List<ModDirectoryEntry> ListGrouped(ModDirectorySession session, ModDirectoryLease lease)
    {
        var result = new List<ModDirectoryEntry>();
        foreach (var entry in List(session, lease))
        {
            if (entry.IsDirectory && IsCategoryDirectory(entry.Path)) result.AddRange(List(session, lease, entry.Path));
            else result.Add(entry);
        }
        return result;
    }

    public static List<ModDirectoryEntry> List(ModDirectorySession session, ModDirectoryLease lease, string? categoryDirectory = null)
    {
        var context = session.Require(lease);
        string directoryRoot = categoryDirectory is null ? context.ModsDirectory : CategoryDirectory(context, categoryDirectory);
        if (!Directory.Exists(directoryRoot)) return [];
        var result = new List<ModDirectoryEntry>();
        foreach (string path in Directory.EnumerateFileSystemEntries(directoryRoot))
        {
            string name = Path.GetFileName(path);
            if (name.StartsWith('.') || name.StartsWith('_')) continue;
            bool directory = Directory.Exists(path);
            if (!directory && !DisplayName(context, name).EndsWith(".ini", StringComparison.OrdinalIgnoreCase)) continue;
            ValidateEntry(context, path);
            result.Add(new(name, path, directory, IsEnabled(context, name) && (categoryDirectory is null || IsEnabled(context, Path.GetFileName(directoryRoot))), DisplayName(context, name),
                categoryDirectory is null ? null : DisplayName(context, Path.GetFileName(directoryRoot))));
        }
        session.Require(lease);
        return result.OrderByDescending(m => m.Enabled).ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string EntryPath(ModDirectorySession session, ModDirectoryLease lease, string path)
    {
        var context = session.Require(lease);
        ValidateEntry(context, path);
        return Absolute(path);
    }

    public static string SetEnabled(ModDirectorySession session, ModDirectoryLease lease, string path, bool enabled)
    {
        var context = session.Require(lease);
        path = EntryPath(session, lease, path);
        string name = Path.GetFileName(path);
        if (enabled && !SamePath(Path.GetDirectoryName(path)!, context.ModsDirectory)
            && !IsEnabled(context, Path.GetFileName(Path.GetDirectoryName(path)!)))
            throw new InvalidOperationException("角色文件夹已禁用，请先在后端启用该角色文件夹。");
        if (IsEnabled(context, name) == enabled) return path;
        string stem = DisplayName(context, name);
        Leaf(stem);
        string wanted = enabled ? stem : context.DisableStyle == ModDisableStyle.Prefix ? "DISABLED " + stem : stem + " DISABLED";
        string target = Path.Combine(Path.GetDirectoryName(path)!, wanted);
        NoReparse(target);
        if (Exists(target)) throw new IOException("目标 Mod 名称冲突，未覆盖任何文件：" + wanted);
        session.Require(lease);
        if (Directory.Exists(path)) Directory.Move(path, target); else File.Move(path, target);
        return target;
    }

    public static void Delete(ModDirectorySession session, ModDirectoryLease lease, string path)
    {
        path = EntryPath(session, lease, path);
        session.Require(lease);
        if (Directory.Exists(path)) Directory.Delete(path, true); else File.Delete(path);
    }

    public static string ImportFolder(ModDirectorySession session, ModDirectoryLease lease, string source, string? categoryDirectory = null)
    {
        var context = session.Require(lease);
        source = Absolute(source);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        if (SamePath(source, context.ModsDirectory) || Inside(source, context.ModsDirectory) || Inside(context.ModsDirectory, source))
            throw new InvalidDataException("导入源和 Mod 目录重叠，拒绝递归复制。");
        NoReparseTree(source);
        string name = Path.GetFileName(source);
        Leaf(name);
        string destination = Destination(categoryDirectory is null ? context : context with { ModsDirectory = CategoryDirectory(context, categoryDirectory) }, name);
        string stage = Stage(session, lease);
        try
        {
            CopyTree(source, stage, () => session.Require(lease));
            session.Require(lease);
            if (categoryDirectory is not null) CategoryDirectory(context, categoryDirectory);
            Directory.Move(stage, destination);
            return destination;
        }
        finally { Cleanup(stage); }
    }

    public static string ImportZip(ModDirectorySession session, ModDirectoryLease lease, string zip, string? categoryDirectory = null)
    {
        var context = session.Require(lease);
        zip = Absolute(zip);
        NoReparse(zip);
        string name = Path.GetFileNameWithoutExtension(zip);
        Leaf(name);
        string destination = Destination(categoryDirectory is null ? context : context with { ModsDirectory = CategoryDirectory(context, categoryDirectory) }, name);
        using var archive = ZipFile.OpenRead(zip);
        var paths = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            string relative = entry.FullName.Replace('\\', '/');
            if (relative.Length == 0 || relative.StartsWith('/') || relative.Contains(':')
                || relative.Split('/').Any(s => s is "." or "..")
                || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                throw new InvalidDataException("压缩包包含路径穿越/链接，未导入。");
            string trimmed = relative.TrimEnd('/');
            foreach (string segment in trimmed.Split('/')) Leaf(segment, allowInternal: true);
            if (!paths.TryAdd(trimmed, relative.EndsWith('/'))) throw new InvalidDataException("压缩包路径冲突，未导入。");
        }
        foreach (string path in paths.Keys)
        {
            string? parent = Path.GetDirectoryName(path.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(parent))
            {
                if (paths.TryGetValue(parent.Replace('\\', '/'), out bool directory) && !directory)
                    throw new InvalidDataException("压缩包文件/目录冲突，未导入。");
                parent = Path.GetDirectoryName(parent);
            }
        }
        string stage = Stage(session, lease);
        try
        {
            foreach (var entry in archive.Entries)
            {
                session.Require(lease);
                string target = Path.GetFullPath(Path.Combine(stage, entry.FullName.Replace('\\', '/')));
                if (!Inside(target, stage)) throw new InvalidDataException("压缩包路径越界。");
                NoReparse(target);
                if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) Directory.CreateDirectory(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    using var input = entry.Open();
                    using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(output);
                }
            }
            session.Require(lease);
            NoReparseTree(stage);
            if (categoryDirectory is not null) CategoryDirectory(context, categoryDirectory);
            Directory.Move(stage, destination);
            return destination;
        }
        finally { Cleanup(stage); }
    }

    private static string Stage(ModDirectorySession session, ModDirectoryLease lease)
    {
        var context = session.Require(lease);
        if (context.Backend == ModBackend.Xxmi && !Directory.Exists(context.ModsDirectory)) Directory.CreateDirectory(context.ModsDirectory);
        NoReparse(context.ModsDirectory);
        string path = Path.Combine(context.ModsDirectory, "_import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string Destination(ModDirectoryContext context, string name)
    {
        string path = Path.Combine(context.ModsDirectory, name);
        NoReparse(path);
        if (Exists(path)) throw new IOException("目标 Mod 已存在，未覆盖：" + name);
        return path;
    }

    private static void ValidateEntry(ModDirectoryContext context, string path)
    {
        path = Absolute(path);
        if (!SamePath(Path.GetDirectoryName(path)!, context.ModsDirectory))
            CategoryDirectory(context, Path.GetDirectoryName(path)!);
        Leaf(Path.GetFileName(path));
        if (!Exists(path)) throw new FileNotFoundException("Mod 已不存在。", path);
        NoReparseTree(path);
    }

    private static string CategoryDirectory(ModDirectoryContext context, string path)
    {
        path = Absolute(path);
        if (!SamePath(Path.GetDirectoryName(path)!, context.ModsDirectory))
            throw new InvalidDataException("角色分类不属于当前游戏/后端。");
        Leaf(Path.GetFileName(path));
        NoReparseTree(path);
        if (!IsCategoryDirectory(path)) throw new InvalidDataException("目标不是角色分类文件夹。");
        return path;
    }

    private static void CopyTree(string source, string destination, Action check)
    {
        foreach (string path in Directory.EnumerateFileSystemEntries(source))
        {
            check();
            NoReparse(path);
            string target = Path.Combine(destination, Path.GetFileName(path));
            NoReparse(target);
            if (Directory.Exists(path)) { Directory.CreateDirectory(target); CopyTree(path, target, check); }
            else File.Copy(path, target, false);
        }
    }

    private static void Cleanup(string stage)
    {
        if (!Directory.Exists(stage)) return;
        NoReparseTree(stage);
        Directory.Delete(stage, true);
    }

    private static string Absolute(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('"') || path.Any(char.IsControl)
            || path[2..].Contains(':') || path.Split(['\\', '/']).Any(p => p is "." or ".."))
            throw new InvalidDataException("Mod 路径必须是明确的绝对路径，不能包含穿越/设备流。");
        string result = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (SamePath(result, Path.GetPathRoot(result)!)) throw new InvalidDataException("不能管理磁盘根目录。");
        foreach (string part in result.Split(['\\', '/']).Where(p => p.Length > 0 && !p.EndsWith(':'))) Leaf(part, allowInternal: true);
        return result;
    }

    private static void Leaf(string name, bool allowInternal = false)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || name.EndsWith('.') || name.EndsWith(' ') || (!allowInternal && (name.StartsWith('.') || name.StartsWith('_'))))
            throw new InvalidDataException("Mod 文件名不安全或与内部目录冲突。");
        string device = name.Split('.')[0].ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" || (device.Length == 4 && (device.StartsWith("COM") || device.StartsWith("LPT")) && device[3] is >= '0' and <= '9'))
            throw new InvalidDataException("不允许 Windows 设备文件名。");
    }

    private static bool SamePath(string a, string b) => string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
    private static bool Inside(string path, string root) => path.StartsWith(root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static bool Exists(string path) { try { _ = File.GetAttributes(path); return true; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } }
    private static void NoReparse(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Mod 路径包含 reparse/link，拒绝管理：" + current);
    }
    private static void NoReparseTree(string path)
    {
        NoReparse(path);
        if (!Directory.Exists(path)) return;
        foreach (string child in Directory.EnumerateFileSystemEntries(path)) NoReparseTree(child);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static List<(string Section, string Key, string Value)> ReadIni(string path)
    {
        var result = new List<(string Section, string Key, string Value)>();
        string section = string.Empty;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1].Trim(); continue; }
            int equals = line.IndexOf('=');
            if (equals < 1) continue;
            string value = line[(equals + 1)..].Trim();
            bool quoted = false;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '"') quoted = !quoted;
                if (!quoted && i > 0 && (value[i] is ';' or '#') && char.IsWhiteSpace(value[i - 1])) { value = value[..i].TrimEnd(); break; }
            }
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
            result.Add((section, line[..equals].Trim(), value));
        }
        return result;
    }
    private static string[] Values(List<(string Section, string Key, string Value)> ini, string section, string key) => ini
        .Where(v => string.Equals(v.Section, section, StringComparison.OrdinalIgnoreCase) && string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase))
        .Select(v => v.Value).ToArray();
    private static string One(List<(string Section, string Key, string Value)> ini, string section, string key)
    {
        string[] values = Values(ini, section, key);
        if (values.Length != 1 || string.IsNullOrWhiteSpace(values[0])) throw new InvalidDataException("配置缺失/重复：" + section + "/" + key);
        return values[0];
    }
}
