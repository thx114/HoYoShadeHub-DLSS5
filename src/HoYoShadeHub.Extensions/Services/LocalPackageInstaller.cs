using HoYoShadeHub.Core;
using System.IO.Compression;
using HoYoShadeHub.Extensions.Archives;
using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
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

    /// <summary>
    /// 「一键覆盖包」：顶层整棵 <c>HoYoShade\</c>（ReShade 框架 + 滤镜 + 插件）和 / 或
    /// <c>OptiScaler\</c>（含 state.json 的本地库）盖到对应目录上。
    /// </summary>
    Overlay,

    /// <summary>启动器本体的包（根上有 version.ini / app-&lt;版本&gt;\HoYoShadeHub.exe）—— 不是插件包</summary>
    AppPackage,
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
        LocalPackageKind.Overlay => "覆盖包",
        LocalPackageKind.AppPackage => "启动器包",
        _ => "未知",
    };
}

/// <summary>
/// 把用户从 GitHub / Discord 拿到的「原始包」装进 Hub：
/// 标准 hysx 扩展包、OptiScaler 整包、单个或打包的 addon、模块 DLL、
/// 以及别人配好整棵 HoYoShade / OptiScaler 的「一键覆盖包」都走这里。
///
/// <para>
/// 用户手里的包通常没有 Hub 的 manifest.json：OptiScaler 的 release zip 只有
/// <c>OptiScaler.dll / OptiScaler.ini</c>，插件经常只拖一个 <c>.addon64</c>。
/// 这个类按包内文件识别类型、各自走对应路由，并把缺的运行时依赖自动补齐。
/// </para>
/// </summary>
public sealed class LocalPackageInstaller
{
    /// <summary>覆盖包里 HoYoShade 框架那一层的目录名</summary>
    public const string ShadeOverlayFolder = ShadeHostLocator.HoYoShadeFolderName;

    /// <summary>覆盖包里 OptiScaler 本地库那一层的目录名</summary>
    public const string OptiOverlayFolder = "OptiScaler";

    private readonly string _optiscalerRoot;
    private readonly string _addonsDirectory;
    private readonly string _modulesRoot;
    private readonly IEnumerable<string> _otherOptiBuildDirectories;
    private readonly string _shadeRoot;
    private readonly ShadeHost? _shadeHost;
    private readonly string _cacheRoot;

    /// <param name="optiscalerRoot">OptiScaler 本地库根目录</param>
    /// <param name="addonsDirectory">HoYoShade 的 addons 目录（DLL 运行时来源）</param>
    /// <param name="modulesRoot">模块根目录</param>
    /// <param name="otherOptiBuildDirectories">已有的其它 OptiScaler 构建目录（补依赖用）</param>
    /// <param name="shadeRoot">HoYoShade 框架根目录（覆盖包往这儿盖）；不传就从 addonsDirectory 反推</param>
    /// <param name="shadeHost">宿主 —— 归档「已装插件的当前版本」要用；没有就跳过插件归档</param>
    /// <param name="cacheRoot">缓存根 —— dll / 插件的版本归档落在它下面；没有就跳过归档</param>
    public LocalPackageInstaller(
        string optiscalerRoot,
        string addonsDirectory,
        string modulesRoot,
        IEnumerable<string>? otherOptiBuildDirectories = null,
        string? shadeRoot = null,
        ShadeHost? shadeHost = null,
        string? cacheRoot = null)
    {
        _optiscalerRoot = optiscalerRoot;
        _addonsDirectory = addonsDirectory;
        _modulesRoot = modulesRoot;
        _otherOptiBuildDirectories = otherOptiBuildDirectories ?? [];
        _shadeRoot = string.IsNullOrWhiteSpace(shadeRoot) ? DeriveShadeRoot(addonsDirectory) : shadeRoot;
        _shadeHost = shadeHost;
        _cacheRoot = cacheRoot ?? string.Empty;
    }

    /// <summary>从 <c>&lt;HoYoShade&gt;\reshade-shaders\Addons</c> 反推 HoYoShade 根目录</summary>
    private static string DeriveShadeRoot(string addonsDirectory)
    {
        if (string.IsNullOrWhiteSpace(addonsDirectory))
        {
            return string.Empty;
        }

        string trimmed = addonsDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? shaders = Path.GetDirectoryName(trimmed);
        string? root = shaders is null ? null : Path.GetDirectoryName(shaders);

        return root ?? string.Empty;
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

    /// <summary>按包内文件清单识别类型：hysx 清单 &gt; 启动器包 &gt; 覆盖包 &gt; OptiScaler &gt; addon。</summary>
    public static LocalPackageKind DetectKindFromEntries(IEnumerable<string> entryNames)
    {
        // 打包工具常把整包再套一层同名目录（「星穹铁道6倍覆盖包_1.1/…」），先脱掉那层
        List<string> raw = entryNames.Select(NormalizeEntry).ToList();

        // 打包工具常把整包再套一层同名目录（「星穹铁道6倍覆盖包_1.1/…」），再算一份脱壳后的
        List<string> names = StripCommonWrapper(raw);

        if (names.Any(n => string.Equals(Path.GetFileName(n), "manifest.json", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(Path.GetFileName(n), "hysx.json", StringComparison.OrdinalIgnoreCase)))
        {
            return LocalPackageKind.Extension;
        }

        // 启动器本体包和覆盖包都带着 HoYoShade\，先按根上的特征把两者分开
        if (IsLauncherAppPackage(raw))
        {
            return LocalPackageKind.AppPackage;
        }

        // 包里只有 HoYoShade 一层时，「多套了一层壳」和「真的只有框架」长得一模一样 —— 两边都试
        if (IsOverlayPackage(raw) || IsOverlayPackage(names))
        {
            return LocalPackageKind.Overlay;
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

    /// <summary>包内路径统一成 <c>a/b/c</c> 形式，去掉 <c>.\</c> 前缀，便于按顶层目录判断</summary>
    private static string NormalizeEntry(string path)
        => path.Replace('\\', '/').TrimStart('.', '/');

    /// <summary>
    /// 脱掉「所有条目都在同一个顶层目录下」的那层套壳 ——
    /// 打包工具（右键压缩 / Compress-Archive）经常把整个包再包一层同名目录。
    /// 根上只要还夹着别的文件就认为没有套壳，原样返回。
    /// </summary>
    private static List<string> StripCommonWrapper(List<string> names)
    {
        string? wrapper = null;

        foreach (string name in names)
        {
            int slash = name.IndexOf('/');

            if (slash <= 0)
            {
                // 根上直接躺着文件 / 空条目 → 没有公共套壳
                return names;
            }

            string head = name[..slash];

            if (wrapper is null)
            {
                wrapper = head;
            }
            else if (!string.Equals(wrapper, head, StringComparison.OrdinalIgnoreCase))
            {
                return names;
            }
        }

        if (wrapper is null)
        {
            return names;
        }

        return
        [
            .. names.Where(n => n.Length > wrapper.Length + 1)
                .Select(n => n[(wrapper.Length + 1)..]),
        ];
    }

    /// <summary>是不是启动器本体的包：根上有 version.ini，或有 app-&lt;版本&gt;\HoYoShadeHub.exe</summary>
    private static bool IsLauncherAppPackage(List<string> names)
        => names.Any(n => n.Equals("version.ini", StringComparison.OrdinalIgnoreCase))
           || names.Any(n => n.StartsWith("app-", StringComparison.OrdinalIgnoreCase)
                             && n.EndsWith("/HoYoShadeHub.exe", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 是不是「一键覆盖包」：顶层带 <c>filelist.json</c>（权威标记），
    /// 或者带着整棵 <c>HoYoShade</c>（认 ReShade64.dll）/ <c>OptiScaler</c>（认 state.json）；
    /// 也收「内容直接铺在根上」的那种。
    /// </summary>
    private static bool IsOverlayPackage(List<string> names)
        => names.Any(n => n.Equals(OverlayManifest.FileName, StringComparison.OrdinalIgnoreCase))
           || names.Any(n => n.Equals(ShadeOverlayFolder + "/ReShade64.dll", StringComparison.OrdinalIgnoreCase))
           || names.Any(n => n.Equals(OptiOverlayFolder + "/" + OptiScalerLibrary.StateFileName, StringComparison.OrdinalIgnoreCase))
           || (names.Any(n => n.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase))
               && names.Any(n => n.StartsWith("reshade-shaders/", StringComparison.OrdinalIgnoreCase)))
           || IsVanillaReShadePackage(names);

    /// <summary>
    /// 是不是「原版 ReShade 官方包」：<c>ReShade64.dll</c> + <c>ReShade.ini</c> 在根上（不在 HoYoShade/ 下）。
    /// 用户从 reshade.me 下的一键安装 exe 内置 zip 就长这样 —— 也当覆盖包处理，装到 HoYoShade 框架根上。
    /// </summary>
    private static bool IsVanillaReShadePackage(List<string> names)
        => names.Any(n => n.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase))
           && names.Any(n => n.Equals("ReShade.ini", StringComparison.OrdinalIgnoreCase));

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
            LocalPackageKind.Overlay => await InstallOverlayAsync(file, cancellationToken),
            LocalPackageKind.AppPackage => throw new NotSupportedException(
                "这是启动器本体的包（根上有 version.ini / app- 目录），不是插件覆盖包；"
                + "把它解压覆盖到启动器目录，或者用「检查更新」。"),
            _ => throw new NotSupportedException("标准 hysx 扩展包请走扩展安装器。"),
        };
    }

    // ───────────────────────── addon / 插件 ─────────────────────────

    /// <summary>
    /// 安装插件：裸 addon 直接复制；zip 解压后把里面所有 addon 文件收进 Addons 目录。
    /// </summary>
    private async Task<LocalPackageInstallResult> InstallAddonAsync(string file, CancellationToken cancellationToken)
    {
        string work = Path.Combine(TemporaryFolder.Path, "HoYoShadeHub.Local", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            var addonFiles = new List<string>();

            if (Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipExtractor.ExtractToDirectory(file, Path.Combine(work, "payload"));
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
                // 与 ExtensionInstaller 相同的「临时文件 + Move 换 inode」：直接 Copy 覆盖会保留目标 inode，
                // 指着它的硬链接（版本归档 / 每游戏插件包）内容会被一起改掉 —— 老版本就白归档了
                string target = Path.Combine(_addonsDirectory, Path.GetFileName(source));
                string staged = target + ".hysx-new";
                File.Copy(source, staged, overwrite: true);
                File.Move(staged, target, overwrite: true);
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

            string extract = Path.Combine(TemporaryFolder.Path, "HoYoShadeHub.Local", Guid.NewGuid().ToString("N"));
            try
            {
                ZipExtractor.ExtractToDirectory(file, extract);
                CopyTree(extract, target);

                // dlss-unlocked 发布出来的正身叫 dxgi.dll，落库后统一成 OptiScaler.dll。
                // 原神 FSR 桥的包不走这条路：它的正身名（Dx11FsrBridge.dll）被改掉之后，
                // 模块解析 / ini / 启动体检就都找不到桥了（见 FsrBridgePayload）。
                if (FsrBridgePayload.LooksLikeBridgeDirectory(target))
                {
                    FsrBridgePayload.NormalizeDll(target);
                }
                else
                {
                    OptiScalerLibrary.NormalizePrimaryDll(target);
                }
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

            string extract = Path.Combine(TemporaryFolder.Path, "HoYoShadeHub.Local", Guid.NewGuid().ToString("N"));
            try
            {
                ZipExtractor.ExtractToDirectory(file, extract);
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

    // ───────────────────────── 一键覆盖包 ─────────────────────────

    /// <summary>
    /// 「一键覆盖包」：把包里的 <c>HoYoShade</c> 整棵盖到 HoYoShade 根、
    /// <c>OptiScaler</c> 盖到本地库根，然后让启动器的版本归档认下这批文件。
    ///
    /// <para>
    /// 覆盖包是别人已经配好的一整套（ReShade 框架 + 滤镜 + 插件 + OptiScaler 构建 + 预设）。
    /// </para>
    ///
    /// <para>
    /// 包里有 <c>filelist.json</c> 就照清单装（目录名、落位、dll / OptiScaler / 插件的版本都由清单说了算）；
    /// 没有清单的老包才退回按目录结构猜。清单是「这个包是什么」的唯一权威，猜只是兼容手段。
    /// </para>
    /// </summary>
    private async Task<LocalPackageInstallResult> InstallOverlayAsync(string file, CancellationToken cancellationToken)
    {
        if (!Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("覆盖包要的是 zip。");
        }

        if (_shadeRoot.Length == 0 && _optiscalerRoot.Length == 0)
        {
            throw new InvalidOperationException("还没定位到 HoYoShade / OptiScaler 目录，没法覆盖安装。");
        }

        string work = Path.Combine(TemporaryFolder.Path, "HoYoShadeHub.Local", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);

        try
        {
            string payload = Path.Combine(work, "payload");
            ZipExtractor.ExtractToDirectory(file, payload);
            string root = DescendSingleWrapper(payload);

            OverlayManifest? manifest = OverlayManifest.Load(root);

            // 包里带 HoYoShade 那一半、但本机还没定位到 HoYoShade 目录：直接说清楚，别默默只装一半。
            // （实测：全新便携包里先导覆盖包 → 只有 OptiScaler 那半进去，DLL 页报「缺必需文件」，
            //  用户根本看不出来框架那半被跳过了。）
            if (_shadeRoot.Length == 0 && OverlayHasShadeHalf(root, manifest))
            {
                throw new InvalidOperationException(
                    "这个覆盖包里有 HoYoShade 那一半（ReShade 框架 / 滤镜 / 插件），"
                    + "但启动器现在没定位到 HoYoShade 的目录，没地方落。\n\n"
                    + "· 完整包 / 便携包（包里自带 HoYoShade\\ 目录）：多半是首进页面时启动器还没找到它 —— "
                    + "刷新一次插件页，或点页面上的「指定目录」直接指到 <包目录>\\HoYoShade，再导入；\n"
                    + "· 本机确实没装过：先到「启动器」页装一次 HoYoShade 框架，再导入这个包。\n\n"
                    + "现在导进去只有 OptiScaler 那一半生效，插件和运行时 dll（nvngx_dlssnr / sl.*）都不会有。");
            }

            var details = new List<string>();
            var stuck = new List<string>();
            string target = _shadeRoot.Length > 0 ? _shadeRoot : _optiscalerRoot;
            int total = 0;

            if (manifest is not null)
            {
                // 有清单就照清单装：目录名、装到哪儿、带的是什么版本，全由清单说了算
                details.Add("按清单安装：" + manifest.DisplayName
                            + (string.IsNullOrWhiteSpace(manifest.Version) ? string.Empty : " " + manifest.Version));

                foreach ((string from, string to) in ResolveTargets(manifest))
                {
                    string source = Path.Combine(root, from.Replace('/', Path.DirectorySeparatorChar));

                    if (!Directory.Exists(source))
                    {
                        details.Add($"清单里的 {from} 在包里没有，跳过");
                        continue;
                    }

                    if (to == OverlayManifest.TargetSkip)
                    {
                        details.Add($"{from}：清单说不用装，跳过");
                        continue;
                    }

                    string destination = DestinationOf(to);
                    if (destination.Length == 0)
                    {
                        details.Add($"{from}：不认识的目标「{to}」，跳过");
                        continue;
                    }

                    OverlayCopyResult copied = OverlayTree(source, destination, stuck);
                    total += copied.Copied;
                    details.Add($"{from} → {TargetNameOf(to)}：{copied.Copied} 个文件"
                                + (copied.Failed > 0 ? $"（{copied.Failed} 个没换成）" : string.Empty));
                }
            }
            else
            {
                // 没有清单的老包：按目录结构猜（HoYoShade → 框架根，OptiScaler → 本地库根）
                string? shadeSource = ResolveOverlayFolder(root, ShadeOverlayFolder, "ReShade64.dll");
                if (shadeSource is not null && _shadeRoot.Length > 0)
                {
                    OverlayCopyResult copied = OverlayTree(shadeSource, _shadeRoot, stuck);
                    total += copied.Copied;
                    details.Add($"HoYoShade 框架覆盖 {copied.Copied} 个文件"
                                + (copied.Failed > 0 ? $"（{copied.Failed} 个没换成）" : string.Empty));
                }

                string? optiSource = ResolveOverlayFolder(root, OptiOverlayFolder, OptiScalerLibrary.StateFileName);
                if (optiSource is not null && _optiscalerRoot.Length > 0)
                {
                    OverlayCopyResult copied = OverlayTree(optiSource, _optiscalerRoot, stuck);
                    total += copied.Copied;
                    details.Add($"OptiScaler 库覆盖 {copied.Copied} 个文件"
                                + (copied.Failed > 0 ? $"（{copied.Failed} 个没换成）" : string.Empty));
                }
            }

            if (total == 0 && stuck.Count == 0)
            {
                throw new FileNotFoundException(
                    $"包里没找到 {ShadeOverlayFolder} / {OptiOverlayFolder} 目录，也没有 {OverlayManifest.FileName} 清单。");
            }

            if (stuck.Count > 0)
            {
                details.Add("有文件被占用没换成（游戏或启动器正开着就会这样）：" + string.Join("、", stuck.Take(4)));
            }

            // 覆盖包的 GamePack\（auto.json / ini_config / 预设 / README）随包分发：
            // 落进每个匹配游戏的覆盖包目录。不做这步的话，新机器导入后动作文件根本不在
            // 包目录里 —— 没有确认框、13 个 once 步骤永远不会执行（启动选项/预设绑定全丢）。
            if (manifest is { Game: { Length: > 0 } gameHint } && _cacheRoot.Length > 0)
            {
                string gamePackSource = Path.Combine(root, "GamePack");
                string gamesRoot = Path.Combine(_cacheRoot, "games");
                bool placed = false;
                if (Directory.Exists(gamePackSource) && Directory.Exists(gamesRoot))
                {
                    foreach (string dir in Directory.EnumerateDirectories(gamesRoot))
                    {
                        string gameKey = Path.GetFileName(dir);
                        if (gameKey.StartsWith(".", StringComparison.Ordinal))
                            continue;
                        if (!gameKey.StartsWith(gameHint, StringComparison.OrdinalIgnoreCase))
                            continue;
                        string packDir = GameAddonPack.PackDirectory(_cacheRoot, gameKey);
                        Directory.CreateDirectory(packDir);
                        int copied = 0;
                        foreach (string packFile in Directory.EnumerateFiles(gamePackSource))
                        {
                            try
                            {
                                File.Copy(packFile, Path.Combine(packDir, Path.GetFileName(packFile)), overwrite: true);
                                copied++;
                            }
                            catch
                            {
                                // 单个文件占用不挡其它
                            }
                        }
                        if (copied > 0)
                        {
                            placed = true;
                            details.Add($"GamePack → {gameKey} 覆盖包目录：{copied} 个文件（打开该游戏插件页确认动作后即生效）");
                        }
                    }
                }

                // 游戏还没装/没扫到（games 下没有匹配目录）：暂存，游戏一出现就由

                // GameAddonPackService.Sync 铺进它的包目录 —— 否则新机器导入即丢动作。

                if (!placed && Directory.Exists(gamePackSource))

                {

                    string pending = Path.Combine(gamesRoot, ".pending-gamepacks", gameHint);

                    if (Directory.Exists(pending))

                        Directory.Delete(pending, recursive: true);

                    Directory.CreateDirectory(pending);

                    foreach (string packFile in Directory.EnumerateFiles(gamePackSource))

                        File.Copy(packFile, Path.Combine(pending, Path.GetFileName(packFile)), overwrite: true);

                    details.Add($"GamePack 已暂存（游戏 {gameHint}* 尚未注册）；游戏出现后会自动铺进其覆盖包目录");

            }
                }
            // 归档：让 DLL 页 / 插件页认下「盘上这份是哪个版本」
            var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            details.AddRange(ArchiveRuntimeDlls(handled));

            if (manifest is not null)
            {
                details.AddRange(ApplyManifestCatalog(manifest, handled));
            }

            details.AddRange(await ArchiveInstalledExtensionsAsync(cancellationToken));
            details.Add("重启游戏生效");

            OptiScalerBuild? selected = new OptiScalerLibrary(_optiscalerRoot).GetSelected();

            return new LocalPackageInstallResult(
                LocalPackageKind.Overlay,
                manifest?.DisplayName ?? Path.GetFileNameWithoutExtension(file),
                manifest?.Version ?? selected?.Version,
                target,
                details);
        }
        finally
        {
            HysxUtil.TryDeleteDirectory(work);
        }
    }

    private sealed record OverlayCopyResult(int Copied, int Failed);

    /// <summary>
    /// 包里有没有 HoYoShade 那一半：有清单就看清单里有没有 from 存在、to = shade 的目录；
    /// 老包（没清单）就靠 <c>HoYoShade\ReShade64.dll</c> 这个特征文件认。
    /// </summary>
    private static bool OverlayHasShadeHalf(string root, OverlayManifest? manifest)
    {
        if (manifest is not null)
        {
            foreach ((string from, string to) in ResolveTargets(manifest))
            {
                if (to == OverlayManifest.TargetShade
                    && Directory.Exists(Path.Combine(root, from.Replace('/', Path.DirectorySeparatorChar))))
                {
                    return true;
                }
            }

            return false;
        }

        return ResolveOverlayFolder(root, ShadeOverlayFolder, "ReShade64.dll") is not null;
    }

    /// <summary>清单里的「从哪个目录盖到哪儿」；没写 targets 就按约定俗成的两个目录</summary>
    private static IEnumerable<(string From, string To)> ResolveTargets(OverlayManifest manifest)
    {
        if (manifest.Targets.Count > 0)
        {
            foreach (OverlayManifestTarget written in manifest.Targets)
            {
                if (string.IsNullOrWhiteSpace(written.From))
                {
                    continue;
                }

                string to = NormalizeTarget(written.To);
                if (to.Length == 0)
                {
                    continue;
                }

                yield return (written.From.Trim(), to);
            }

            yield break;
        }

        yield return (ShadeOverlayFolder, OverlayManifest.TargetShade);
        yield return (OptiOverlayFolder, OverlayManifest.TargetOptiScaler);
    }

    /// <summary>目标代号归一化；认不出来返回空串（不认识的目标就不装，绝不乱盖）</summary>
    private static string NormalizeTarget(string? to) => (to ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "shade" or "hoyoshade" or "hyshade" or "framework" => OverlayManifest.TargetShade,
        "opti" or "optiscaler" => OverlayManifest.TargetOptiScaler,
        "module" or "modules" => OverlayManifest.TargetModules,
        "skip" or "ignore" or "none" => OverlayManifest.TargetSkip,
        _ => string.Empty,
    };

    private string DestinationOf(string to) => to switch
    {
        OverlayManifest.TargetShade => _shadeRoot,
        OverlayManifest.TargetOptiScaler => _optiscalerRoot,
        OverlayManifest.TargetModules => _modulesRoot,
        _ => string.Empty,
    };

    private static string TargetNameOf(string to) => to switch
    {
        OverlayManifest.TargetShade => "HoYoShade 框架",
        OverlayManifest.TargetOptiScaler => "OptiScaler 库",
        OverlayManifest.TargetModules => "模块目录",
        _ => to,
    };

    /// <summary>
    /// 按清单把 dll / OptiScaler 构建 / 插件的版本信息补齐。
    ///
    /// <para>
    /// 盘上那份读得出 PE 版本就以盘上为准（<paramref name="handled"/> 里已经记下了）；
    /// 读不出来（第三方 dll、被改过的、加了壳的）就照清单写的版本归档 ——
    /// 这样「包里带的是哪个版本」不会丢，DLL 页也看得出盘上这份是什么。
    /// </para>
    /// </summary>
    private List<string> ApplyManifestCatalog(OverlayManifest manifest, HashSet<string> handled)
    {
        var details = new List<string>();

        foreach (OverlayManifestDll declared in manifest.Dlls)
        {
            if (string.IsNullOrWhiteSpace(declared.Family) || string.IsNullOrWhiteSpace(declared.Version))
            {
                continue;
            }

            DllFamily? family = DllComponentCatalog.FamilyOf(declared.Family);
            if (family is null || handled.Contains(family.Id))
            {
                continue;
            }

            List<string> files = ResolveDeclaredDllFiles(declared, family);
            if (files.Count == 0)
            {
                details.Add($"清单里的 {family.Id} {declared.Version} 在盘上没找到");
                continue;
            }

            string declaredVersion = DllVersion.Normalize(declared.Version);

            if (_cacheRoot.Length > 0 && new DllVersionStore(_cacheRoot).Archive(family.Id, declaredVersion, files) > 0)
            {
                handled.Add(family.Id);
                details.Add($"{family.Id} {declaredVersion} 已归档（按清单）");
            }
        }

        if (manifest.OptiScaler is { } opti
            && !string.IsNullOrWhiteSpace(opti.SourceId)
            && !string.IsNullOrWhiteSpace(opti.Version)
            && _optiscalerRoot.Length > 0)
        {
            string buildDirectory = Path.Combine(
                _optiscalerRoot,
                OptiScalerLibrary.Sanitize(opti.SourceId),
                OptiScalerLibrary.Sanitize(opti.Version));

            if (Directory.Exists(buildDirectory))
            {
                if (!File.Exists(Path.Combine(buildDirectory, OptiScalerLibrary.BuildManifestName)))
                {
                    WriteBuildManifest(buildDirectory, opti.SourceId, opti.Version, null);
                    details.Add($"OptiScaler {opti.Version} 已登记进本地库");
                }

                // 包里带了 state.json 就听包里的；没有才按清单补一个
                if (opti.Select != false && string.IsNullOrWhiteSpace(ReadSelectedBuild()))
                {
                    WriteSelectedBuild(opti.SourceId + "/" + opti.Version);
                    details.Add($"已把 OptiScaler {opti.Version} 设为当前启用");
                }
            }
            else
            {
                details.Add($"清单里的 OptiScaler 构建 {opti.Version} 没落位，跳过登记");
            }
        }

        if (manifest.Addons.Count > 0)
        {
            IEnumerable<string> names = manifest.Addons
                .Select(a => string.IsNullOrWhiteSpace(a.Name) ? a.File : a.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Take(4);

            details.Add($"{manifest.Addons.Count} 个插件：" + string.Join("、", names));
        }

        return details;
    }

    /// <summary>清单里声明的那条 dll 在盘上是哪个文件：先按写的路径找，找不到就按家族把 Addons 里那几个都收进来</summary>
    private List<string> ResolveDeclaredDllFiles(OverlayManifestDll declared, DllFamily family)
    {
        string written = (declared.File ?? string.Empty).Replace('\\', '/').Trim().TrimStart('.', '/');

        if (written.Length > 0)
        {
            // 带路径的按 HoYoShade 根算，纯文件名的就在 Addons 目录里
            string path = written.Contains('/')
                ? Path.Combine(_shadeRoot, written.Replace('/', Path.DirectorySeparatorChar))
                : Path.Combine(_addonsDirectory, written);

            if (File.Exists(path))
            {
                return [path];
            }
        }

        if (!Directory.Exists(_addonsDirectory))
        {
            return [];
        }

        return
        [
            .. DllInstaller.Scan(_addonsDirectory)
                .Where(d => DllInstaller.Matches(d.FileName, family))
                .Select(d => Path.Combine(_addonsDirectory, d.FileName)),
        ];
    }

    /// <summary>库根 state.json 里记的「当前启用」；没有 / 坏了返回 null</summary>
    private string? ReadSelectedBuild()
    {
        try
        {
            string path = Path.Combine(_optiscalerRoot, OptiScalerLibrary.StateFileName);
            if (!File.Exists(path))
            {
                return null;
            }

            OptiScalerState? state = JsonSerializer.Deserialize<OptiScalerState>(File.ReadAllText(path), _jsonOptions);
            return string.IsNullOrWhiteSpace(state?.Selected) ? null : state.Selected;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写库根 state.json（OptiScalerState 是库里的类型，直接序列化成同样的形状）</summary>
    private void WriteSelectedBuild(string buildId)
    {
        Directory.CreateDirectory(_optiscalerRoot);
        File.WriteAllText(
            Path.Combine(_optiscalerRoot, OptiScalerLibrary.StateFileName),
            JsonSerializer.Serialize(new OptiScalerState { Selected = buildId }, _jsonOptions));
    }

    /// <summary>zip 里可能多套了一层同名目录，往下钻；最多钻 3 层</summary>
    private static string DescendSingleWrapper(string directory)
    {
        string current = directory;

        for (int i = 0; i < 3; i++)
        {
            string[] dirs = Directory.GetDirectories(current);

            if (dirs.Length != 1 || Directory.GetFiles(current).Length != 0)
            {
                break;
            }

            current = dirs[0];
        }

        return current;
    }

    /// <summary>找覆盖包里某一层：优先 <c>&lt;root&gt;\&lt;名字&gt;</c>，没有就认「内容直接铺在根上」</summary>
    private static string? ResolveOverlayFolder(string root, string folderName, string markerFile)
    {
        string nested = Path.Combine(root, folderName);
        if (Directory.Exists(nested))
        {
            return nested;
        }

        return File.Exists(Path.Combine(root, markerFile)) ? root : null;
    }

    /// <summary>
    /// 把一棵目录盖到目标上：逐文件「临时文件 + Move 换 inode」。
    ///
    /// <para>
    /// 直接 Copy 覆盖会保留目标 inode，指着它的硬链接（版本归档 / 每游戏插件包）内容会被一起改掉 ——
    /// 老版本就白归档了。换 inode 之后被占用的文件会失败，所以逐个 catch 并记下来，不让整包失败。
    /// </para>
    /// </summary>
    private static OverlayCopyResult OverlayTree(string source, string destination, List<string> failed)
    {
        Directory.CreateDirectory(destination);

        int copied = 0;
        int failures = 0;

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(source, file);

            // 账本 / 备份是我们自己的状态，包里的那份不要盖掉用户的
            if (relative.Equals(ShadeHost.MetadataFolderName, StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith(ShadeHost.MetadataFolderName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string target = Path.Combine(destination, relative);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                string staged = target + ".hysx-new";
                File.Copy(file, staged, overwrite: true);
                File.Move(staged, target, overwrite: true);
                copied++;
            }
            catch
            {
                failures++;

                if (failed.Count < 16)
                {
                    failed.Add(relative);
                }
            }
        }

        return new OverlayCopyResult(copied, failures);
    }

    /// <summary>把盘上现存的运行时 dll 归一份档（<c>&lt;CacheRoot&gt;\dlls\&lt;family&gt;\&lt;version&gt;\</c>）</summary>
    private List<string> ArchiveRuntimeDlls(HashSet<string> handled)
    {
        var details = new List<string>();

        if (_cacheRoot.Length == 0 || !Directory.Exists(_addonsDirectory))
        {
            return details;
        }

        try
        {
            List<InstalledDll> installed = DllInstaller.Scan(_addonsDirectory);
            var store = new DllVersionStore(_cacheRoot);

            foreach (DllFamily family in DllComponentCatalog.Families)
            {
                string? version = DllInstaller.GetInstalledVersion(installed, family);
                if (string.IsNullOrWhiteSpace(version))
                {
                    continue;
                }

                List<string> files =
                [
                    .. installed.Where(d => DllInstaller.Matches(d.FileName, family))
                        .Select(d => Path.Combine(_addonsDirectory, d.FileName)),
                ];

                // PE 里读出来是 310,8,3,0 这种写法，归一成 310.8.3 —— 和 dll 清单里一个样式
                string normalized = DllVersion.Normalize(version);

                if (store.Archive(family.Id, normalized, files) > 0)
                {
                    handled.Add(family.Id);
                    details.Add($"{family.Id} {normalized} 已归档");
                }
            }
        }
        catch
        {
            // 归档失败绝不能让覆盖安装失败
        }

        return details;
    }

    /// <summary>把账本里已装插件的当前版本归一份档 —— 覆盖包换了插件文件之后要重新归档</summary>
    private async Task<List<string>> ArchiveInstalledExtensionsAsync(CancellationToken cancellationToken)
    {
        var details = new List<string>();

        if (_shadeHost is null || _cacheRoot.Length == 0)
        {
            return details;
        }

        try
        {
            var store = new AddonVersionStore(_cacheRoot);
            InstalledExtensionLedger ledger = await new InstalledExtensionStore(_shadeHost).LoadAsync(cancellationToken);

            int archived = 0;
            foreach (InstalledExtension record in ledger.Extensions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                AddonArchiveResult result = store.Archive(_shadeHost, record);
                if (result.Archived > 0 || result.Skipped > 0)
                {
                    archived++;
                }
            }

            if (archived > 0)
            {
                details.Add($"已归档 {archived} 个插件的当前版本");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // 同上：归档失败不影响安装
        }

        return details;
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
