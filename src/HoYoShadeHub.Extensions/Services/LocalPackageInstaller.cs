using System.IO.Compression;
using System.Text.Json;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>本地包的识别结果（决定走哪条安装流水线）。</summary>
public enum LocalPackageKind
{
    /// <summary>带 manifest.json / hysx.json 的标准 hysx 扩展包</summary>
    Extension,

    /// <summary>OptiScaler 构建包（含 OptiScaler.dll 或其代理 dll + ini）</summary>
    OptiScaler,

    /// <summary>ReShade 插件包（含 .addon64 / .addon32）</summary>
    Addon,

    /// <summary>模块 DLL（version.dll / dxgi.dll 之类，要单独注入的原生 DLL）</summary>
    Module,
}

/// <summary>本地包安装结果。</summary>
public sealed record LocalPackageInstallResult(
    LocalPackageKind Kind,
    string DisplayName,
    string? Version,
    string TargetPath,
    List<string> Details)
{
    /// <summary>给界面状态栏用的一句话。</summary>
    public string Summary =>
        $"[{KindText}] {DisplayName}{(string.IsNullOrWhiteSpace(Version) ? string.Empty : " " + Version)}：{string.Join("；", Details)}";

    public string KindText => Kind switch
    {
        LocalPackageKind.Extension => "扩展",
        LocalPackageKind.OptiScaler => "OptiScaler",
        LocalPackageKind.Addon => "插件",
        LocalPackageKind.Module => "模块",
        _ => "未知",
    };
}

/// <summary>
/// 把用户从 GitHub / Discord 拿到的「原始包」装进 Hub：
/// 标准 hysx 扩展包、OptiScaler 整包、单个或打包的 addon、模块 DLL 都走这里。
///
/// <para>
/// 用户手里的包通常没有 Hub 的 manifest.json：OptiScaler 的 release zip 只有
/// <c>OptiScaler.dll / OptiScaler.ini</c>，插件经常只拖一个 <c>.addon64</c>。
/// 这个类按包内文件识别类型、各自走对应路由，并把缺的运行时依赖自动补齐。
/// </para>
/// </summary>
public sealed class LocalPackageInstaller
{
    private readonly string _optiscalerRoot;
    private readonly string _addonsDirectory;
    private readonly string _modulesRoot;
    private readonly IEnumerable<string> _otherOptiBuildDirectories;

    /// <param name="optiscalerRoot">OptiScaler 本地库根目录</param>
    /// <param name="addonsDirectory">HoYoShade 的 addons 目录（DLL 运行时来源）</param>
    /// <param name="modulesRoot">模块根目录</param>
    /// <param name="otherOptiBuildDirectories">已有的其它 OptiScaler 构建目录（补依赖用）</param>
    public LocalPackageInstaller(
        string optiscalerRoot,
        string addonsDirectory,
        string modulesRoot,
        IEnumerable<string>? otherOptiBuildDirectories = null)
    {
        _optiscalerRoot = optiscalerRoot;
        _addonsDirectory = addonsDirectory;
        _modulesRoot = modulesRoot;
        _otherOptiBuildDirectories = otherOptiBuildDirectories ?? [];
    }

    /// <summary>识别一个文件是什么包。zip 看包内文件；裸文件按扩展名。</summary>
    public static LocalPackageKind DetectKind(string file)
    {
        string ext = Path.GetExtension(file);

        if (ext.Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(file);
            return DetectKindFromEntries(archive.Entries.Select(e => e.FullName));
        }

        if (ext.Equals(".addon64", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".addon32", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".addon", StringComparison.OrdinalIgnoreCase))
        {
            return LocalPackageKind.Addon;
        }

        if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
        {
            string name = Path.GetFileName(file);
            if (string.Equals(name, "OptiScaler.dll", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase))
            {
                return LocalPackageKind.OptiScaler;
            }

            return LocalPackageKind.Module;
        }

        return LocalPackageKind.Module;
    }

    /// <summary>按包内文件清单识别类型：hysx 清单 &gt; OptiScaler &gt; addon。</summary>
    public static LocalPackageKind DetectKindFromEntries(IEnumerable<string> entryNames)
    {
        var names = entryNames.ToList();

        if (names.Any(n => string.Equals(Path.GetFileName(n), "manifest.json", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Path.GetFileName(n), "hysx.json", StringComparison.OrdinalIgnoreCase)))
        {
            return LocalPackageKind.Extension;
        }

        if (names.Any(IsOptiScalerFile))
        {
            return LocalPackageKind.OptiScaler;
        }

        if (names.Any(IsAddonFile))
        {
            return LocalPackageKind.Addon;
        }

        return LocalPackageKind.Module;
    }

    private static bool IsAddonFile(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".addon64", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".addon32", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".addon", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOptiScalerFile(string path)
    {
        string name = Path.GetFileName(path);
        if (string.Equals(name, "OptiScaler.dll", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "OptiScaler.ini", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase)
               && Path.GetExtension(name).Equals(".dll", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>识别并安装一个本地文件（zip / addon / dll）。</summary>
    public async Task<LocalPackageInstallResult> InstallAsync(
        string file,
        LocalPackageKind? kindOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(file))
        {
            throw new FileNotFoundException("找不到这个文件", file);
        }

        LocalPackageKind kind = kindOverride ?? DetectKind(file);

        return kind switch
        {
            LocalPackageKind.Addon => await InstallAddonAsync(file, cancellationToken),
            LocalPackageKind.OptiScaler => await InstallOptiScalerAsync(file, cancellationToken),
            LocalPackageKind.Module => InstallModule(file),
            _ => throw new NotSupportedException("标准 hysx 扩展包请走扩展安装器。"),
        };
    }

    // ───────────────────────── addon / 插件 ─────────────────────────

    /// <summary>
    /// 安装插件：裸 addon 直接复制；zip 解压后把里面所有 addon 文件收进 Addons 目录。
    /// </summary>
    private async Task<LocalPackageInstallResult> InstallAddonAsync(string file, CancellationToken cancellationToken)
    {
        string work = Path.Combine(Path.GetTempPath(), "HoYoShadeHub.Local", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            var addonFiles = new List<string>();

            if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(file, Path.Combine(work, "payload"), overwriteFiles: true);
                addonFiles.AddRange(Directory.EnumerateFiles(
                    Path.Combine(work, "payload"), "*", SearchOption.AllDirectories)
                    .Where(f => IsAddonFile(f)));
            }
            else
            {
                addonFiles.Add(file);
            }

            if (addonFiles.Count == 0)
            {
                throw new FileNotFoundException("包里没有找到 .addon64 / .addon32 文件。");
            }

            Directory.CreateDirectory(_addonsDirectory);

            var installed = new List<string>();
            foreach (string source in addonFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = Path.Combine(_addonsDirectory, Path.GetFileName(source));
                File.Copy(source, target, overwrite: true);
                installed.Add(Path.GetFileName(target));
            }

            await Task.CompletedTask;

            string primary = installed[0];
            return new LocalPackageInstallResult(
                LocalPackageKind.Addon,
                Path.GetFileNameWithoutExtension(primary),
                ReadPeVersion(Path.Combine(_addonsDirectory, primary)),
                _addonsDirectory,
                installed.Count == 1
                    ? [$"已安装 {primary}"]
                    : [$"共安装 {installed.Count} 个插件", string.Join("、", installed.Take(5))]);
        }
        finally
        {
            HysxUtil.TryDeleteDirectory(work);
        }
    }

    // ───────────────────────── OptiScaler ─────────────────────────

    /// <summary>
    /// 安装 OptiScaler：zip 解到库目录（source=local）；裸 dll 放进 <c>local\&lt;名字&gt;</c>。
    /// 装完按 wilsjo2 同款流程补齐 dlssnr / streamline / 钉 OptiDllPath / 统一 dlssg。
    /// </summary>
    private async Task<LocalPackageInstallResult> InstallOptiScalerAsync(string file, CancellationToken cancellationToken)
    {
        string sourceId = "local";

        string version;
        string target;

        if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            version = DeriveVersionFromZipName(file);
            target = Path.Combine(_optiscalerRoot, OptiScalerLibrary.Sanitize(sourceId),
                OptiScalerLibrary.Sanitize(version));

            if (Directory.Exists(target))
            {
                HysxUtil.TryDeleteDirectory(target);
            }

            Directory.CreateDirectory(target);

            string extract = Path.Combine(Path.GetTempPath(), "HoYoShadeHub.Local", Guid.NewGuid().ToString("N"));
            try
            {
                ZipFile.ExtractToDirectory(file, extract, overwriteFiles: true);
                CopyTree(extract, target);

                // dlss-unlocked 发布出来的正身叫 dxgi.dll，落库后统一成 OptiScaler.dll
                OptiScalerLibrary.NormalizePrimaryDll(target);
            }
            finally
            {
                HysxUtil.TryDeleteDirectory(extract);
            }
        }
        else
        {
            version = ReadPeVersion(file) ?? Path.GetFileNameWithoutExtension(file);
            target = Path.Combine(_optiscalerRoot, OptiScalerLibrary.Sanitize(sourceId),
                OptiScalerLibrary.Sanitize(version));
            Directory.CreateDirectory(target);
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        WriteBuildManifest(target, sourceId, version, Path.GetFileName(file));

        var details = await CompleteOptiScalerBuildAsync(target, cancellationToken);

        return new LocalPackageInstallResult(
            LocalPackageKind.OptiScaler,
            "OptiScaler",
            version,
            target,
            details);
    }

    /// <summary>装完构建后补齐依赖，返回给界面的明细。</summary>
    private async Task<List<string>> CompleteOptiScalerBuildAsync(string buildDirectory, CancellationToken cancellationToken)
    {
        var details = new List<string>();

        NrdllPlaceResult nrdll = OptiScalerRuntime.EnsureNrdll(
            buildDirectory, _addonsDirectory, _otherOptiBuildDirectories);
        if (nrdll.Status is NrdllPlaceStatus.Copied)
        {
            details.Add("已补齐 nvngx_dlssnr.dll");
        }

        List<string> streamline = OptiScalerRuntime.EnsureStreamline(buildDirectory, _addonsDirectory);
        if (streamline.Count > 0)
        {
            details.Add($"已补齐 Streamline {streamline.Count} 个文件");
        }

        if (OptiScalerRuntime.EnsureConfigDllPath(buildDirectory))
        {
            details.Add("已把 OptiDllPath 钉到构建目录");
        }

        DlssgEnsureResult dlssg = await OptiScalerRuntime.EnsureDlssgForUnlockAsync(
            buildDirectory,
            [
                _addonsDirectory,
                Path.Combine(buildDirectory, "OptiScaler", OptiScalerRuntime.StreamlineFolderName),
                buildDirectory,
                Path.Combine(buildDirectory, "OptiScaler"),
                .. _otherOptiBuildDirectories,
            ],
            cancellationToken: cancellationToken);
        details.Add(dlssg.Ok ? "已统一 310.9 nvngx_dlssg.dll" : dlssg.Message);

        if (details.Count == 0)
        {
            details.Add("依赖已齐全");
        }

        return details;
    }

    // ───────────────────────── 模块 ─────────────────────────

    /// <summary>
    /// 安装模块：zip 解到 <c>Modules\&lt;名字&gt;</c>；裸 dll 复制进模块目录。
    /// 模块是全局开关，装完直接启用。
    /// </summary>
    private LocalPackageInstallResult InstallModule(string file)
    {
        string id;
        string target;
        string? version = null;
        var details = new List<string>();

        if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            id = Path.GetFileNameWithoutExtension(file);
            target = Path.Combine(_modulesRoot, OptiScalerLibrary.Sanitize(id));

            if (Directory.Exists(target))
            {
                HysxUtil.TryDeleteDirectory(target);
            }

            Directory.CreateDirectory(target);

            string extract = Path.Combine(Path.GetTempPath(), "HoYoShadeHub.Local", Guid.NewGuid().ToString("N"));
            try
            {
                ZipFile.ExtractToDirectory(file, extract, overwriteFiles: true);
                CopyTree(extract, target);
            }
            finally
            {
                HysxUtil.TryDeleteDirectory(extract);
            }
        }
        else
        {
            id = Path.GetFileNameWithoutExtension(file);
            version = ReadPeVersion(file);
            target = Path.Combine(_modulesRoot, OptiScalerLibrary.Sanitize(id));
            Directory.CreateDirectory(target);
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        details.Add("已安装到模块目录");

        return new LocalPackageInstallResult(
            LocalPackageKind.Module,
            id,
            version,
            target,
            details);
    }

    // ───────────────────────── helpers ─────────────────────────

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>
    /// 从 zip 文件名推导版本：OptiScaler_0.7.0.zip → 0.7.0；
    /// 没有数字版本号就用文件名（去扩展名）。
    /// </summary>
    private static string DeriveVersionFromZipName(string file)
    {
        string stem = Path.GetFileNameWithoutExtension(file);

        System.Text.RegularExpressions.Match match =
            System.Text.RegularExpressions.Regex.Match(stem, @"\d+(?:\.\d+)+(?:[-.]\w+)?");

        return match.Success ? match.Value : stem;
    }

    /// <summary>读 PE FileVersion；没有或读不出来返 null。</summary>
    private static string? ReadPeVersion(string path)
    {
        try
        {
            return System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileVersion is { } text
                   && !string.IsNullOrWhiteSpace(text)
                ? text.Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteBuildManifest(string directory, string sourceId, string version, string? assetName)
    {
        File.WriteAllText(
            Path.Combine(directory, OptiScalerLibrary.BuildManifestName),
            JsonSerializer.Serialize(new OptiScalerBuildManifest
            {
                SourceId = sourceId,
                Version = version,
                AssetName = assetName,
                InstalledAt = DateTimeOffset.Now,
            }, _jsonOptions));
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
