using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("HoYoShadeHub.Extensions.Tests")]

namespace HoYoShadeHub.Extensions.Services;

public enum RtssProfileMode { Low, Disabled }

/// <summary>Explicit per-EXE RTSS configuration; never writes Global or changes OSD/limiter settings.</summary>
public static class RtssProfileService
{
    // Unverified RTSS installations must use the UI for Low rather than a guessed numeric mapping.
    public static int? VerifiedLowValue { get; } = null;
    public sealed record State(string Profile, string? Hooking, string? Osd, bool CanRestore);
    private sealed record Receipt(string Profile, bool Existed, string OriginalHash, string AppliedHash);

    public static string ProfilePath(string rtssRoot, string exeName)
    {
        if (string.IsNullOrWhiteSpace(exeName) || Path.GetFileName(exeName) != exeName
            || exeName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("必须选择有效的游戏 EXE 文件名。");
        string root = Path.GetFullPath(rtssRoot);
        if (!File.Exists(Path.Combine(root, "RTSS.exe")) || !Directory.Exists(Path.Combine(root, "Profiles")))
            throw new DirectoryNotFoundException("没有找到 RTSS.exe / Profiles；请确认 RTSS 安装目录。");
        return Path.Combine(root, "Profiles", exeName + ".cfg");
    }

    public static State Read(string root, string exe, string backupRoot)
    {
        string p = ProfilePath(root, exe);
        string text = File.Exists(p) ? Decode(File.ReadAllBytes(p)).Text : string.Empty;
        string journal = Journal(backupRoot, p);
        string meta = Path.Combine(journal, "receipt.json");
        bool canRestore = false;
        try
        {
            if (File.Exists(meta))
            {
                Receipt receipt = LoadReceipt(meta, p);
                byte[] original = File.ReadAllBytes(Path.Combine(journal, "original.bin"));
                canRestore = Hash(original) == receipt.OriginalHash
                    && File.Exists(p) && Hash(File.ReadAllBytes(p)) == receipt.AppliedHash;
            }
        }
        catch { /* Invalid or stale restore data should not be offered as actionable. */ }
        return new(p, Value(text, "Hooking", "EnableHooking"), Value(text, "OSD", "EnableOSD"), canRestore);
    }

    public static string Apply(string root, string exe, string backupRoot, RtssProfileMode mode)
        => ApplyCore(root, exe, backupRoot, mode, EnsureStopped);

    internal static string ApplyCore(string root, string exe, string backupRoot, RtssProfileMode mode, Action<string> ensureStopped)
    {
        int value = mode == RtssProfileMode.Disabled ? 0
            : VerifiedLowValue ?? throw new InvalidOperationException("尚未核实此 RTSS 版本的 Low 映射，请在 RTSS 界面手动选择 Low。");
        string p = ProfilePath(root, exe);
        RejectLinks(p);
        ensureStopped(exe);
        bool existed = File.Exists(p);
        byte[] before = existed ? File.ReadAllBytes(p) : [];
        var decoded = Decode(before);
        byte[] after = decoded.Bom.Concat(decoded.Encoding.GetBytes(SetHooking(decoded.Text, value))).ToArray();
        string journal = Journal(backupRoot, p);
        string meta = Path.Combine(journal, "receipt.json");
        Receipt receipt;
        if (File.Exists(meta))
        {
            receipt = LoadReceipt(meta, p);
            if (!existed || Hash(before) != receipt.AppliedHash)
                throw new IOException("RTSS profile 在上次修改后被其他程序改动；请先备份并核对，不覆盖用户的新设置。");
        }
        else
        {
            Directory.CreateDirectory(journal);
            string originalFile = Path.Combine(journal, "original.bin");
            if (File.Exists(originalFile)) File.Move(originalFile, Path.Combine(journal, "original-" + Guid.NewGuid().ToString("N") + ".bin"));
            File.WriteAllBytes(originalFile, before);
            receipt = new(p, existed, Hash(before), Hash(before));
            AtomicWrite(meta, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt)));
        }
        bool replaced = false;
        try
        {
            ensureStopped(exe);
            AtomicWrite(p, after);
            replaced = true;
            if (!File.ReadAllBytes(p).SequenceEqual(after)) throw new IOException("写入后的 profile 校验失败。");
            AtomicWrite(meta, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt with { AppliedHash = Hash(after) })));
        }
        catch
        {
            // A guard failure must not enter the rollback writer while RTSS is running.
            if (replaced)
            {
                if (existed) AtomicWrite(p, before); else if (File.Exists(p)) File.Delete(p);
            }
            throw;
        }
        return $"已将 {exe} 的 RTSS 检测设置为 {(mode == RtssProfileMode.Disabled ? "禁用" : "Low")}；未修改 OSD/限帧/其他游戏。备份：{journal}。请重新启动 RTSS 和游戏后对照测试；这不代表冲突已修复。";
    }

    public static string Restore(string root, string exe, string backupRoot)
        => RestoreCore(root, exe, backupRoot, EnsureStopped);

    internal static string RestoreCore(string root, string exe, string backupRoot, Action<string> ensureStopped)
    {
        string p = ProfilePath(root, exe);
        RejectLinks(p);
        ensureStopped(exe);
        string journal = Journal(backupRoot, p);
        string meta = Path.Combine(journal, "receipt.json");
        Receipt receipt = LoadReceipt(meta, p);
        byte[] original = File.ReadAllBytes(Path.Combine(journal, "original.bin"));
        if (Hash(original) != receipt.OriginalHash) throw new IOException("原配置备份校验失败，未恢复。");
        if (!File.Exists(p) || Hash(File.ReadAllBytes(p)) != receipt.AppliedHash)
            throw new IOException("当前 profile 已被其他程序改动，未覆盖；请先核对备份。");
        ensureStopped(exe);
        if (receipt.Existed) AtomicWrite(p, original); else File.Delete(p);
        // Retain the immutable original backup, but do not offer a stale restore operation.
        File.Move(meta, Path.Combine(journal, "restored-" + Guid.NewGuid().ToString("N") + ".json"));
        return $"已恢复 {exe} 修改前的配置（原来没有 profile 时移除本次新建的 profile）。备份保留：{journal}。请重新启动 RTSS 和游戏。";
    }

    public static string SetHooking(string text, int value)
    {
        if (value is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(value));
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        var section = Regex.Match(text, @"(?im)^\[Hooking\][^\r\n]*(?:\r?\n|$)");
        if (Regex.Matches(text, @"(?im)^\[Hooking\]").Count > 1) throw new IOException("重复的 Hooking 段，无法安全修改。");
        if (!section.Success) return text + (text.Length > 0 && !text.EndsWith('\n') ? nl : "") + "[Hooking]" + nl + "EnableHooking=" + value + nl;
        int start = section.Index + section.Length;
        if (!section.Value.EndsWith('\n')) { text += nl; start += nl.Length; }
        var next = Regex.Match(text[start..], @"(?m)^\[");
        int end = next.Success ? start + next.Index : text.Length;
        string body = text[start..end];
        var key = new Regex(@"(?im)^(\s*EnableHooking\s*=\s*)[^\r\n]*");
        body = key.IsMatch(body) ? key.Replace(body, m => m.Groups[1].Value + value)
            : "EnableHooking=" + value + nl + body;
        return text[..start] + body + text[end..];
    }

    private static string? Value(string text, string section, string key)
    {
        var header = Regex.Match(text, @"(?im)^\[" + Regex.Escape(section) + @"\][^\r\n]*(?:\r?\n|$)");
        if (!header.Success) return null;
        string body = text[(header.Index + header.Length)..];
        var next = Regex.Match(body, @"(?m)^\[");
        if (next.Success) body = body[..next.Index];
        return Regex.Match(body, @"(?im)^\s*" + Regex.Escape(key) + @"\s*=\s*([^\r\n]*)") is { Success: true } m ? m.Groups[1].Value.Trim() : null;
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Journal(string root, string profile) => Path.Combine(Path.GetFullPath(root), "rtss", Hash(Encoding.UTF8.GetBytes(profile.ToUpperInvariant())));
    private static Receipt LoadReceipt(string meta, string profile)
    {
        var r = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(meta)) ?? throw new IOException("备份记录损坏。");
        if (!string.Equals(r.Profile, profile, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份与当前 profile 不匹配。");
        return r;
    }
    private static void RejectLinks(string path)
    {
        string profiles = Path.GetDirectoryName(path)!;
        if ((File.GetAttributes(profiles) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("RTSS Profiles 目录是链接，无法保证仅影响当前游戏；未修改。");
        if (File.Exists(path) && ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
            throw new IOException("目标 profile 是链接，无法保证仅影响当前游戏；未修改。");
    }
    private static void EnsureStopped(string exe)
    {
        string[] names = ["RTSS", "RTSSHooksLoader", "RTSSHooksLoader64", Path.GetFileNameWithoutExtension(exe)];
        foreach (string name in names)
        {
            Process[] processes = Process.GetProcessesByName(name);
            bool running = processes.Length > 0;
            foreach (Process process in processes) process.Dispose();
            if (running) throw new InvalidOperationException($"请先正常退出 {name}，未写入 RTSS profile。");
        }
    }
    private static (string Text, Encoding Encoding, byte[] Bom) Decode(byte[] bytes)
    {
        Encoding encoding = Encoding.Latin1; int skip = 0;
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) { encoding = new UTF8Encoding(false, true); skip = 3; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) { encoding = new UnicodeEncoding(false, false, true); skip = 2; }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) { encoding = new UnicodeEncoding(true, false, true); skip = 2; }
        // RTSS keys are ASCII. Latin1 round-trips unmarked ANSI/UTF8 bytes without lossy decoding.
        return (encoding.GetString(bytes, skip, bytes.Length - skip), encoding, bytes[..skip]);
    }
    private static void AtomicWrite(string path, byte[] data)
    {
        string temp = path + ".hysx-" + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temp, data); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
