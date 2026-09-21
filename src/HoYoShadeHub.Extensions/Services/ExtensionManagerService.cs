using HoYoShadeHub.Extensions.Models;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>一个扩展在「目录 + 本地状态」合并后的视图</summary>
public sealed class ExtensionStatus
{
    public required ExtensionManifest Manifest { get; init; }

    public InstalledExtension? Installed { get; init; }

    public bool IsInstalled => Installed is not null;

    /// <summary>本地已装版本与目录里声明的版本不一致</summary>
    public bool IsUpdateAvailable =>
        Installed is not null
        && !string.IsNullOrWhiteSpace(Manifest.Version)
        && !string.Equals(Installed.Version, Manifest.Version, StringComparison.OrdinalIgnoreCase);

    /// <summary>这个扩展声明的适用区服（空 = 不限）</summary>
    public string[] Games => Manifest.GameBiz ?? [];
}

/// <summary>
/// 对某个 HoYoShade 宿主做扩展管理的统一入口。
/// UI 只需要跟它打交道。
/// </summary>
public sealed class ExtensionManagerService
{
    public ExtensionManagerService(ShadeHost host)
    {
        Host = host;
        Store = new InstalledExtensionStore(host);
        Installer = new ExtensionInstaller(Store);
        Fetcher = new ExtensionPackageFetcher();
        Catalog = new ExtensionCatalogService();
    }

    public ShadeHost Host { get; }

    public InstalledExtensionStore Store { get; }

    public ExtensionInstaller Installer { get; }

    public ExtensionPackageFetcher Fetcher { get; }

    public ExtensionCatalogService Catalog { get; }

    /// <summary>只读本地状态，不发网络请求</summary>
    public async Task<List<ExtensionStatus>> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await Catalog.LoadAsync(cancellationToken);
        var ledger = await Store.LoadAsync(cancellationToken);

        var byId = ledger.Extensions.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var statuses = new List<ExtensionStatus>();

        foreach (ExtensionManifest manifest in catalog.Extensions)
        {
            byId.TryGetValue(manifest.Id, out InstalledExtension? installed);
            statuses.Add(new ExtensionStatus { Manifest = manifest, Installed = installed });
        }

        // 装了但目录里已经没有的（本地包、或者目录条目被删了）也要列出来，否则用户没法卸载
        foreach (InstalledExtension installed in ledger.Extensions)
        {
            if (!catalog.Extensions.Any(m => string.Equals(m.Id, installed.Id, StringComparison.OrdinalIgnoreCase)))
            {
                statuses.Add(new ExtensionStatus
                {
                    Manifest = new ExtensionManifest
                    {
                        Id = installed.Id,
                        Name = installed.Name ?? installed.Id,
                        Version = installed.Version,
                        Source = installed.Source ?? new ExtensionSource { Type = ExtensionSourceType.Local },
                        Rules = [new ExtensionFileRule()],
                        Description = "本地记录的扩展，当前目录里没有对应条目。",
                    },
                    Installed = installed,
                });
            }
        }

        return statuses;
    }

    public async Task<List<InstalledExtension>> GetInstalledAsync(CancellationToken cancellationToken = default)
    {
        var ledger = await Store.LoadAsync(cancellationToken);
        return ledger.Extensions;
    }

    /// <summary>
    /// 安装（或重装 / 更新）一个扩展。
    /// 下载 → 解压 → 按规则落位 → 记帐 → 必要时补 ReShade.ini。
    /// </summary>
    /// <param name="tagOverride">装指定版本（GitHub tag）；null = 最新版</param>
    public async Task<ExtensionInstallResult> InstallAsync(
        ExtensionManifest manifest,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? tagOverride = null)
    {
        if (!manifest.IsValid)
        {
            throw new InvalidOperationException($"扩展 {manifest.Id} 的清单不完整，无法安装。");
        }

        string hostName = Host.Name;
        string[] allowed = manifest.Hosts is { Length: > 0 } ? manifest.Hosts : ["HoYoShade"];
        if (!allowed.Any(h => string.Equals(h, hostName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"扩展 {manifest.Id} 不支持装到 {hostName}。");
        }

        using ResolvedExtensionPayload payload = await Fetcher.FetchAsync(manifest, progress, cancellationToken, tagOverride);
        return await Installer.InstallAsync(Host, manifest, payload, cancellationToken);
    }

    /// <param name="removeSiblings">连同一个插件的其它版本文件一起删（用户要求「删插件要能删干净」）</param>
    public Task<ExtensionUninstallResult> UninstallAsync(
        string extensionId,
        bool removeSiblings = true,
        CancellationToken cancellationToken = default)
        => Installer.UninstallAsync(Host, extensionId, removeSiblings, cancellationToken);

    /// <summary>
    /// 查某个扩展在 GitHub 上有没有新版本。只对 github-release 来源有效。
    /// </summary>
    public async Task<string?> CheckUpdateAsync(ExtensionManifest manifest, CancellationToken cancellationToken = default)
    {
        if (manifest.Source.Type != ExtensionSourceType.GithubRelease)
        {
            return null;
        }

        var installed = await Store.FindAsync(manifest.Id, cancellationToken);
        var resolver = new GithubReleaseResolver();
        GithubArtifact? artifact = await resolver.ResolveAsync(manifest.Source, null, cancellationToken);

        if (artifact is null)
        {
            return null;
        }

        if (installed is not null && string.Equals(installed.ResolvedTag, artifact.Tag, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return artifact.Tag;
    }

    /// <summary>
    /// 这个扩展能装哪些版本（新 → 旧）。
    /// 用户要求：「最好是可以直接下拉选择插件版本」。只对 github-release 来源有效，全程不碰 API 限额。
    /// </summary>
    public async Task<List<ExtensionVersion>> ListVersionsAsync(
        ExtensionManifest manifest,
        int max = 30,
        CancellationToken cancellationToken = default)
    {
        if (manifest.Source.Type != ExtensionSourceType.GithubRelease)
        {
            return [];
        }

        var resolver = new GithubReleaseResolver();
        return await resolver.ListVersionsAsync(manifest.Source, max, cancellationToken);
    }



    /// <summary>简单体检：宿主是否可用、ReShade.ini 的 addon 路径是否配好</summary>
    public ShadeHostDiagnostics Diagnose()
    {
        var diagnostics = new ShadeHostDiagnostics
        {
            RootPath = Host.RootPath,
            ShadeDllExists = Host.Exists,
            ReShadeIniExists = File.Exists(Host.ReShadeIniPath),
            AddonPathConfigured = ReShadeIniService.HasUsableAddonPath(Host),
        };

        string shaders = Host.ResolveRelative(ShadeHost.ShadersFolder);
        string addons = Host.ResolveRelative(ShadeHost.AddonsFolder);
        diagnostics.ShadersFolderExists = Directory.Exists(shaders);
        diagnostics.AddonsFolderExists = Directory.Exists(addons);

        return diagnostics;
    }
}

public sealed class ShadeHostDiagnostics
{
    public required string RootPath { get; init; }
    public bool ShadeDllExists { get; init; }
    public bool ReShadeIniExists { get; init; }
    public bool AddonPathConfigured { get; init; }
    public bool ShadersFolderExists { get; set; }
    public bool AddonsFolderExists { get; set; }

    public bool IsHealthy => ShadeDllExists && AddonPathConfigured;
}
