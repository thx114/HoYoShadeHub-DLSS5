using System.Security.Cryptography;
using HoYoShadeHub.Extensions.Services;
using System.Text.Json;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Dlls;

/// <summary>NR 内容寻址库；同内容、同卷的所有受管位置共用一个文件。</summary>
public static class NrRuntimeLibrary
{
    public const string FileName = "nvngx_dlssnr.dll";
    private static readonly object RegistrationGate = new();
    private sealed record VersionRegistration(string Version, string Sha256);

    // 内容去重不代表用户收藏。只有显式归档才留下独立记账，构建默认 NR 的 Intern 不写。
    internal static void RememberVersion(string cacheRoot, string version, string canonical)
    {
        lock (RegistrationGate)
        {
            string hash = Path.GetFileName(Path.GetDirectoryName(canonical)!);
            string path = RegistrationPath(cacheRoot, version);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new VersionRegistration(version, hash)));
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal static void ForgetVersion(string cacheRoot, string version)
    {
        lock (RegistrationGate) File.Delete(RegistrationPath(cacheRoot, version));
    }

    private static string RegistrationPath(string cacheRoot, string version) =>
        Path.Combine(cacheRoot, "nr-version-index", OptiScalerLibrary.Sanitize(version) + ".json");

    public static IReadOnlyList<string> SelectableVersions(IEnumerable<string> installed, Func<string, bool> enabled)
        => installed.Where(enabled).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static string Hash(string path)
    {
        using var input = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(input));
    }

    public static string Intern(string cacheRoot, string source)
    {
        string hash = Hash(source);
        string canonical = Path.Combine(cacheRoot, "nr-content", hash, FileName);
        if (File.Exists(canonical) && !Hash(canonical).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new IOException("NR 内容库 SHA256 校验失败：" + canonical);
        if (!File.Exists(canonical)) HysxFileLink.LinkNeuralRuntime(source, canonical);
        HysxFileLink.LinkNeuralRuntime(canonical, source);
        return canonical;
    }

    public static string VersionFile(string cacheRoot, string version)
    {
        string path = new DllVersionStore(cacheRoot).FilePath("dlssnr", version, FileName);
        if (path.Length == 0) throw new FileNotFoundException("NR 版本归档缺失，请先在 DLL 配置中安装：" + version);
        DllFingerprintCatalog.ValidateKnownFile("dlssnr", version, path);
        return Intern(cacheRoot, path);
    }

    /// <summary>仅恢复显式归档的版本；内容缓存也含默认库和旧构建，不能据此生成用户版本。</summary>
    public static IReadOnlyList<string> RecoverContentVersions(string cacheRoot)
    {
        lock (RegistrationGate) return RecoverRegisteredVersions(cacheRoot);
    }

    private static IReadOnlyList<string> RecoverRegisteredVersions(string cacheRoot)
    {
        var errors = new List<string>();
        string indexRoot = Path.Combine(cacheRoot, "nr-version-index");
        if (!Directory.Exists(indexRoot)) return errors;
        if ((File.GetAttributes(indexRoot) & FileAttributes.ReparsePoint) != 0) return errors;
        var store = new DllVersionStore(cacheRoot);
        foreach (string registrationPath in Directory.EnumerateFiles(indexRoot, "*.json"))
        {
            try
            {
                if ((File.GetAttributes(registrationPath) & FileAttributes.ReparsePoint) != 0) continue;
                var registration = JsonSerializer.Deserialize<VersionRegistration>(File.ReadAllText(registrationPath));
                if (registration is null || string.IsNullOrWhiteSpace(registration.Version)
                    || registration.Sha256 is not { Length: 64 } || !registration.Sha256.All(char.IsAsciiHexDigit)
                    || !Path.GetFullPath(registrationPath).Equals(Path.GetFullPath(RegistrationPath(cacheRoot, registration.Version)), StringComparison.OrdinalIgnoreCase))
                    throw new IOException("NR 版本记账无效。");
                string source = Path.Combine(cacheRoot, "nr-content", registration.Sha256, FileName);
                string directory = Path.GetDirectoryName(source)!;
                if (!File.Exists(source)) throw new FileNotFoundException("已归档 NR 的内容文件缺失。", source);
                if ((File.GetAttributes(Path.GetDirectoryName(directory)!) & FileAttributes.ReparsePoint) != 0
                    || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0
                    || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("NR 内容库不能使用重解析点。");
                string hash = Hash(source);
                if (!hash.Equals(registration.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("NR 内容目录与 SHA256 不符。");
                string label = registration.Version;
                string target = store.FilePath("dlssnr", label, FileName);
                if (target.Length > 0)
                {
                    if (!Hash(target).Equals(hash, StringComparison.OrdinalIgnoreCase))
                        throw new IOException("NR 版本与记账内容冲突，保留当前版本。");
                    continue;
                }
                var fingerprint = DllFingerprintCatalog.Find("dlssnr", new FileInfo(source).Length, hash);
                if (!DllFingerprintCatalog.ValidatePe(source, fingerprint, out string error)) throw new IOException(error);
                string destination = Path.Combine(store.DirectoryFor("dlssnr", label), FileName);
                HysxFileLink.LinkNeuralRuntime(source, destination);
                target = store.FilePath("dlssnr", label, FileName);
                if (target.Length == 0 || !HysxFileLink.AreHardLinked(source, target))
                    throw new IOException("恢复 NR 版本硬链接失败。");
            }
            catch (Exception ex) { errors.Add(registrationPath + ": " + ex.Message); }
        }
        return errors;
    }

    /// <summary>下载到版本库，不改变任何游戏或共享目录当前加载的 NR。</summary>
    public static async Task<string> DownloadVersionAsync(string cacheRoot, DllComponent component,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!component.Family.Equals("dlssnr", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("不是 NR 下载条目。", nameof(component));
        string temporary = Path.Combine(cacheRoot, "nr-download-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = await DllInstaller.InstallAsync(temporary, component, progress, cancellationToken);
            if (!result.Ok) throw new IOException(result.Error ?? "NR 下载失败。");
            string source = Path.Combine(temporary, FileName);
            if (!File.Exists(source)) throw new IOException("下载包缺少 nvngx_dlssnr.dll。");
            if (!DllFingerprintCatalog.ValidatePe(source, null, out string error)) throw new IOException(error);
            string hash = Hash(source);
            new DllVersionStore(cacheRoot).Archive("dlssnr", component.Version, [source]);
            string archived = VersionFile(cacheRoot, component.Version);
            if (!Hash(archived).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("NR 归档校验失败。");
            return archived;
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }

    public static string OptiDirectory(string cacheRoot, string gameKey, string buildId) =>
        Path.Combine(GameAddonPack.PackDirectory(cacheRoot, gameKey), "OptiRuntime",
            Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(buildId)))[..16]);

    /// <summary>独立加载目录避免共享构建被不同游戏的 NR 选择互相覆盖；配置独立、DLL 硬链接。</summary>
    public static string PrepareOpti(string cacheRoot, string gameKey, string buildId, string dllPath, string? version)
    {
        string sourceRoot = Path.GetDirectoryName(dllPath)!;
        string targetRoot = OptiDirectory(cacheRoot, gameKey, buildId);
        string defaultNr = Path.Combine(sourceRoot, FileName);
        string nr = version is not null ? VersionFile(cacheRoot, version)
            : File.Exists(defaultNr) ? Intern(cacheRoot, defaultNr)
            : throw new FileNotFoundException("构建默认 NR 缺失，请先在全局插件中补齐。", defaultNr);
        foreach (string source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceRoot, source);
            if (HysxFileLink.IsNeuralRuntime(source)) continue;
            if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("OptiScaler 构建包含不支持的重解析文件：" + source);
            string target = Path.Combine(targetRoot, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (Path.GetExtension(source).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (!HysxFileLink.AreHardLinked(source, target)) HysxFileLink.LinkOrCopy(source, target);
            }
            else if (!File.Exists(target)) File.Copy(source, target);
        }
        HysxFileLink.LinkNeuralRuntime(nr, Path.Combine(targetRoot, FileName));
        // 某些构建从组件子目录加载 NR，同时绑定，避免落回包自带版本。
        foreach (string source in Directory.EnumerateFiles(sourceRoot, FileName, SearchOption.AllDirectories))
            HysxFileLink.LinkNeuralRuntime(nr, Path.Combine(targetRoot, Path.GetRelativePath(sourceRoot, source)));
        return Path.Combine(targetRoot, Path.GetFileName(dllPath));
    }

    /// <summary>显式刷新/安装时整合启动器受管库，逐文件报告失败，不接触游戏进程。</summary>
    public static IReadOnlyList<string> Consolidate(string cacheRoot, params string?[] roots)
    {
        var errors = new List<string>();
        foreach (string root in roots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase)!)
        {
            if (!Directory.Exists(root)) continue;
            string[] files;
            try { files = Directory.GetFiles(root, FileName, SearchOption.AllDirectories); }
            catch (Exception ex) { errors.Add(root + ": " + ex.Message); continue; }
            foreach (string file in files)
            {
                try
                {
                    string canonical = Intern(cacheRoot, file);
                    HysxFileLink.LinkNeuralRuntime(canonical, file);
                }
                catch (Exception ex) { errors.Add(file + ": " + ex.Message); }
            }
        }
        return errors;
    }
}
