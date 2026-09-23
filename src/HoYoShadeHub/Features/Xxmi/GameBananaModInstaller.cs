using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.IO.Compression;
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
/// 更新（Replace）时先把旧目录移到 <c>Mods\_backup\</c> 再解压新版本。
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
        string modsDirectory = XxmiLocator.ModsDirectory(instance);
        Directory.CreateDirectory(modsDirectory);

        // 一个模组在 Mods 下固定一个目录：gb_{modId}，更新时才能精确找到旧版本。
        string destination = Path.Combine(modsDirectory, $"gb_{request.ModId}");
        string staging = Path.Combine(modsDirectory, "_cache", $"gb_{request.ModId}_{Path.GetRandomFileName()}");

        try
        {
            string zipPath = await DownloadAsync(request, cancellationToken).ConfigureAwait(false);

            // BackupOldVersion 用 Directory.Move 原子地把旧目录移进 _backup，
            // 移动后 destination 已不存在，无需再删除。
            if (request.Replace && Directory.Exists(destination))
            {
                BackupOldVersion(modsDirectory, destination, request.ModId);
            }

            if (Directory.Exists(destination))
            {
                throw new InvalidOperationException($"目标目录已存在：{destination}（请先在模型替换页删除旧版本）。");
            }

            Directory.CreateDirectory(staging);
            ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
            TryDeleteFile(zipPath);

            // 常见打包习惯：zip 里只有一层同名根目录。剥掉这层，让 Mods\gb_{id}\ 直接是 mod 内容。
            string inner = UnwrapSingleRoot(staging);
            Directory.Move(inner, destination);

            _logger.LogInformation("GameBanana mod {ModId} installed to {Path}", request.ModId, destination);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GameBanana mod {ModId} install failed", request.ModId);
            throw;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                TryDeleteDirectory(staging);
            }
        }
    }

    private string ResolveInstance(string importer)
    {
        // 扩展协议没有 GameBiz 上下文，按期望实例名在已发现的 XXMI 根下直接找。
        string? root = XxmiLocator.FindRoot();
        if (root is not null)
        {
            string dir = Path.Combine(root, importer);
            if (XxmiLocator.IsInstance(dir))
            {
                return dir;
            }
        }

        // 兜底：把所有已知实例扫一遍，找名字匹配的（手动指定过的非标准位置）。
        if (root is not null)
        {
            foreach (string candidate in XxmiLocator.ListInstances(root))
            {
                if (string.Equals(Path.GetFileName(candidate), importer, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        throw new DirectoryNotFoundException(
            $"找不到 XXMI 的 {importer} 实例。请先安装 XXMI Launcher 并用它初始化 {importer}，或在「模型替换」页手动指定实例目录。");
    }

    private async Task<string> DownloadAsync(GameBananaInstallRequest request, CancellationToken cancellationToken)
    {
        string url = request.DownloadUrl ?? $"https://gamebanana.com/dl/{request.FileId}";
        string extension = Path.GetExtension(request.FileName ?? string.Empty);
        if (string.IsNullOrEmpty(extension))
        {
            extension = ".zip";
        }
        string zipPath = Path.Combine(Path.GetTempPath(), $"gamebanana_{request.ModId}_{request.FileId}{extension}");

        _logger.LogInformation("Downloading GameBanana mod {ModId} from {Url}", request.ModId, url);

        using (HttpRequestMessage requestMessage = new HttpRequestMessage(HttpMethod.Get, url))
        using (HttpResponseMessage response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (FileStream target = File.Create(zipPath))
            {
                await source.CopyToAsync(target, 81920, cancellationToken).ConfigureAwait(false);
            }
        }

        return zipPath;
    }

    private static void BackupOldVersion(string modsDirectory, string oldDestination, int modId)
    {
        string backupRoot = Path.Combine(modsDirectory, "_backup");
        Directory.CreateDirectory(backupRoot);
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string backup = Path.Combine(backupRoot, $"gb_{modId}_{stamp}");
        Directory.Move(oldDestination, backup);
    }

    // zip 内若只有一个顶层目录，返回该目录；否则返回解压根。
    private static string UnwrapSingleRoot(string directory)
    {
        string[] dirs = Directory.GetDirectories(directory);
        string[] files = Directory.GetFiles(directory);

        if (dirs.Length == 1 && files.Length == 0)
        {
            return dirs[0];
        }
        return directory;
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }
}
