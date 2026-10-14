using HoYoShadeHub.Core;
using HoYoShadeHub.Extensions.Games;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Xxmi;

/// <summary>
/// 浏览器扩展经 <c>hoyoshadehub://mod/install</c> 拉起的安装参数。
/// </summary>
internal sealed record GameBananaInstallRequest(
    int GameId,
    int ModId,
    long FileId,
    string? FileName,
    string? ModName,
    string? DownloadUrl,
    string? Version,
    bool Replace);

/// <summary>
/// GameBanana 模组下载安装：把 zip 下载到临时目录，解压进对应游戏 MI 实例的 <c>Mods\</c>。
/// 更新时先验证新版本，再备份旧目录并提交；提交失败恢复旧版本。
/// </summary>
internal sealed class GameBananaModInstaller
{
    private readonly ILogger<GameBananaModInstaller> _logger;
    private readonly HttpClient _httpClient;

    public GameBananaModInstaller(ILogger<GameBananaModInstaller> logger)
    {
        _logger = logger;
        // GameBanana 必须经系统代理访问：工厂默认客户端的 DOH ConnectCallback 是裸 socket
        // 直连、绕过代理，会挂起。这里用独立处理器，走系统代理 + 自动跟随 /dl 重定向。
        _httpClient = new HttpClient(new SocketsHttpHandler
        {
            UseProxy = true,
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
        });
        _httpClient.Timeout = TimeSpan.FromMinutes(30);
    }

    /// <summary>
    /// GameBanana 游戏 id → MI 实例名。目前插件页主要面向绝区零（19567 → ZZMI）。
    /// 其余 miHoYo 游戏的 id 在这里登记后即可直接支持。
    /// </summary>
    public static string? ImporterForGameBananaId(int gameId)
    {
        return gameId switch
        {
            19567 => "ZZMI",
            _ => null,
        };
    }

    public async Task InstallAsync(GameBananaInstallRequest request, CancellationToken cancellationToken = default)
    {
        string importer = ImporterForGameBananaId(request.GameId)
            ?? throw new InvalidOperationException($"暂不支持 GameBanana 游戏 id {request.GameId}（目前支持：19567 绝区零 → ZZMI）。");

        string instance = ResolveInstance(importer);
        if (request.ModId <= 0 || request.FileId <= 0) throw new ArgumentException("Mod and file ids must be positive.");
        var context = ModDirectoryPolicy.ResolveXxmi("gamebanana:" + request.GameId, instance);
        string modsDirectory = context.ModsDirectory;
        using FileStream installLease = await ModInstallTransaction.AcquireAsync(context, request.ModId, cancellationToken).ConfigureAwait(false);

        // 一个模组在 Mods 下固定一个目录：gb_{modId}，更新时才能精确找到旧版本。
        string destination = Path.Combine(modsDirectory, $"gb_{request.ModId}");
        string staging = Path.Combine(modsDirectory, "_cache", $"gb_{request.ModId}_{Guid.NewGuid():N}");
        string? zipPath = null;

        try
        {
            ModInstallTransaction.CheckPath(destination);
            if (!request.Replace && Directory.Exists(destination)) throw new IOException("Mod already exists: " + destination);
            ModInstallTransaction.CheckPath(staging);
            zipPath = await DownloadAsync(request, cancellationToken).ConfigureAwait(false);
            string inner = ModInstallTransaction.PrepareZip(zipPath, staging);
            cancellationToken.ThrowIfCancellationRequested();

            ModInstallTransaction.Commit(context, request.ModId, inner, request.Replace);

            _logger.LogInformation("GameBanana mod {ModId} installed to {Path}", request.ModId, destination);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GameBanana mod {ModId} install failed", request.ModId);
            throw;
        }
        finally
        {
            if (zipPath is not null) TryDeleteFile(zipPath);
            if (Directory.Exists(staging))
            {
                TryDeleteDirectory(staging);
            }
        }
    }

    private string ResolveInstance(string importer)
    {
        string[] manual = GameBiz.AllGameBizs
            .Where(biz => XxmiLocator.ImporterFor(biz) == importer)
            .Select(biz => AppConfig.GetXxmiInstance(biz))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(path!).TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (manual.Length > 1) throw new InvalidOperationException("Multiple saved MI instances; protocol target is ambiguous.");
        if (manual.Length == 1)
        {
            if (!XxmiLocator.IsInstanceForImporter(manual[0], importer)) throw new InvalidOperationException("Saved MI instance is invalid for this game.");
            return manual[0];
        }
        // The protocol carries no region: conflicting saved or discovered targets fail closed.
        string? root = XxmiLocator.FindRoot();
        if (root is not null)
        {
            string[] found = Directory.GetDirectories(root).Where(d => XxmiLocator.IsInstanceForImporter(d, importer)).ToArray();
            if (found.Length > 1) throw new InvalidOperationException("Multiple MI instances; select a target in the Mod page first.");
            if (found.Length == 1)
            {
                return found[0];
            }
        }

        throw new DirectoryNotFoundException(
            $"找不到 XXMI 的 {importer} 实例。请先安装 XXMI Launcher 并用它初始化 {importer}，或在「模型替换」页手动指定实例目录。");
    }

    private async Task<string> DownloadAsync(GameBananaInstallRequest request, CancellationToken cancellationToken)
    {
        string url = request.DownloadUrl ?? $"https://gamebanana.com/dl/{request.FileId}";
        string zipPath = Path.Combine(TemporaryFolder.Path, $"gamebanana_{request.ModId}_{request.FileId}_{Guid.NewGuid():N}.zip");

        _logger.LogInformation("Downloading GameBanana mod {ModId} from {Url}", request.ModId, url);

        try
        {
            using (HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, url))
            using (HttpResponseMessage response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (FileStream target = new(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    await source.CopyToAsync(target, 81920, cancellationToken).ConfigureAwait(false);
                }
            }
            return zipPath;
        }
        catch
        {
            TryDeleteFile(zipPath);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { ModInstallTransaction.CheckTree(path); Directory.Delete(path, recursive: true); } catch { }
    }
}
