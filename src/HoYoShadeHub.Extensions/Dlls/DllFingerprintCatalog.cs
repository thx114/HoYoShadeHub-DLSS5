using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Text.Json;

namespace HoYoShadeHub.Extensions.Dlls;

public sealed record DllFingerprint(string Family, string FileName, string Version, long Size,
    string Sha256, string PeVersion, string Machine, bool RequireDll = true);

public sealed class DllFingerprintDocument
{
    public int SchemaVersion { get; set; }
    public DllFingerprint[] Fingerprints { get; set; } = [];
    public DllComponent[] Packages { get; set; } = [];
}

/// <summary>云端目录的本地快照；数据可热更新，代码不内置任何 DLL 指纹。</summary>
public static class DllFingerprintCatalog
{
    public const string FileName = "dll-fingerprints.json";
    private static DllFingerprintDocument _current = new() { SchemaVersion = 1 };
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    static DllFingerprintCatalog()
    {
        string seed = Path.Combine(AppContext.BaseDirectory, "catalog", FileName);
        if (File.Exists(seed)) TryLoadFile(seed);
    }

    public static IReadOnlyList<DllFingerprint> Fingerprints => Volatile.Read(ref _current).Fingerprints;
    public static IReadOnlyList<DllComponent> Packages => Volatile.Read(ref _current).Packages;

    public static DllFingerprintDocument Parse(string json)
    {
        var doc = JsonSerializer.Deserialize<DllFingerprintDocument>(json, Options)
            ?? throw new InvalidDataException("DLL 指纹清单为空。");
        if (doc.SchemaVersion != 1 || doc.Fingerprints is null || doc.Packages is null)
            throw new InvalidDataException("DLL 指纹清单 schemaVersion 不受支持。");
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in doc.Fingerprints)
        {
            if (f is null || string.IsNullOrWhiteSpace(f.Family) || string.IsNullOrWhiteSpace(f.Version)
                || string.IsNullOrWhiteSpace(f.FileName) || Path.GetFileName(f.FileName) != f.FileName
                || f.Size <= 0 || !ValidHash(f.Sha256) || string.IsNullOrWhiteSpace(f.PeVersion)
                || !Enum.TryParse<System.Reflection.PortableExecutable.Machine>(f.Machine, true, out var machine)
                || !Enum.IsDefined(machine) || !identities.Add(f.Family + "/" + f.FileName + "/" + f.Sha256))
                throw new InvalidDataException("DLL 指纹条目无效或重复。");
        }
        foreach (var p in doc.Packages)
            if (p is null || string.IsNullOrWhiteSpace(p.Family) || string.IsNullOrWhiteSpace(p.Version)
                || !Uri.TryCreate(p.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !ValidHash(p.Sha256))
                throw new InvalidDataException("DLL 下载条目无效。");
        return doc;
    }

    private static bool ValidHash(string? hash) => hash?.Length == 64 && hash.All(Uri.IsHexDigit);

    public static void LoadJson(string json) => Interlocked.Exchange(ref _current, Parse(json));

    public static bool TryLoadFile(string path)
    {
        try { LoadJson(File.ReadAllText(path)); return true; }
        catch { return false; } // 坏更新不得清空上一次有效快照。
    }

    public static DllFingerprint? Find(string family, long size, string hash) => Fingerprints.FirstOrDefault(f =>
        f.Family.Equals(family, StringComparison.OrdinalIgnoreCase) && f.Size == size
        && f.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));

    public static void ValidateKnownFile(string family, string version, string path, string? fileName = null)
    {
        fileName ??= Path.GetFileName(path);
        var expected = Fingerprints.Where(f => f.Family.Equals(family, StringComparison.OrdinalIgnoreCase)
            && f.Version.Equals(version, StringComparison.OrdinalIgnoreCase)
            && f.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (expected.Length == 0) return;
        long size = new FileInfo(path).Length;
        string hash = NrRuntimeLibrary.Hash(path);
        var match = expected.FirstOrDefault(f => f.Size == size && f.Sha256.Equals(hash, StringComparison.OrdinalIgnoreCase));
        if (match is null) throw new InvalidDataException("DLL 内容与云端版本指纹不符：" + version);
        if (!ValidatePe(path, match, out string error)) throw new InvalidDataException("DLL PE 验证失败：" + error);
    }

    /// <summary>PE 字段只能辅助验证，不能把相同 PE 版本误认成某个变体。</summary>
    public static bool ValidatePe(string path, DllFingerprint? fingerprint, out string error)
    {
        try
        {
            using var input = File.OpenRead(path);
            using var reader = new PEReader(input);
            var headers = reader.PEHeaders;
            if (headers.PEHeader is null) throw new InvalidDataException("缺少 PE optional header。");
            bool dll = (headers.CoffHeader.Characteristics & Characteristics.Dll) != 0;
            if ((fingerprint?.RequireDll ?? true) && !dll) throw new InvalidDataException("PE 不是 DLL。");
            if (fingerprint is not null)
            {
                if (!headers.CoffHeader.Machine.ToString().Equals(fingerprint.Machine, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("PE 架构与云端清单不符。");
                string? actual = FileVersionInfo.GetVersionInfo(path).FileVersion;
                if (!DllVersion.IsSame(actual, fingerprint.PeVersion)) throw new InvalidDataException("PE 版本与云端清单不符。");
            }
            error = string.Empty;
            return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }
}
