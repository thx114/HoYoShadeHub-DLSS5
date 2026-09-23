using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Features.GameLauncher;
using HoYoShadeHub.Features.PlayTime;
using HoYoShadeHub.Features.Xxmi;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Web;
using Vanara.PInvoke;

namespace HoYoShadeHub.Features.UrlProtocol;

internal class UrlProtocolService
{
    // 协议进程没有主窗口，默认弹窗会被挡在后面且无任务栏按钮。置顶并抢前台。
    private const User32.MB_FLAGS ProtocolBoxFlags =
        User32.MB_FLAGS.MB_OK | User32.MB_FLAGS.MB_TOPMOST | User32.MB_FLAGS.MB_SETFOREGROUND;




    public static void RegisterProtocol()
    {
        UnregisterProtocol();
        string exe;
        if (AppConfig.IsPortable)
        {
            exe = AppConfig.HoYoShadeHubLauncherExecutePath ?? Path.Join(Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd('\\', '/')), "HoYoShadeHub.exe");
        }
        else
        {
            exe = AppConfig.HoYoShadeHubExecutePath;
        }
        string command = $"""
            "{exe}" "%1"
            """;
        Registry.SetValue(@"HKEY_CURRENT_USER\Software\Classes\HoYoShadeHub", "", "URL:HoYoShadeHub Protocol");
        Registry.SetValue(@"HKEY_CURRENT_USER\Software\Classes\HoYoShadeHub", "URL Protocol", "");
        Registry.SetValue(@"HKEY_CURRENT_USER\Software\Classes\HoYoShadeHub\DefaultIcon", "", "HoYoShadeHub.exe,1");
        Registry.SetValue(@"HKEY_CURRENT_USER\Software\Classes\HoYoShadeHub\Shell\Open\Command", "", command);
    }



    public static void UnregisterProtocol()
    {
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Classes\HoYoShadeHub", false);
    }



    public static async Task<bool> HandleUrlProtocolAsync(string url)
    {
        var log = AppConfig.GetLogger<UrlProtocolService>();
        try
        {
            if (Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out Uri? uri))
            {
                if (uri.Host is "test")
                {
                    return false;
                }
                if (string.IsNullOrWhiteSpace(AppConfig.UserDataFolder))
                {
                    log.LogWarning("UserDataFolder is null");
                    return false;
                }
                if (uri.Host is "startgame")
                {
                    if (GameBiz.TryParse(uri.AbsolutePath.Trim('/'), out GameBiz biz) && GameId.FromGameBiz(biz) is GameId gameId)
                    {
                        var kvs = HttpUtility.ParseQueryString(uri.Query);
                        string? installPath = kvs["install_path"];
                        await AppConfig.GetService<GameLauncherService>().StartGameAsync(gameId, installPath);
                    }
                    else
                    {
                        throw new ArgumentException($"Cannot parse the game_biz \"{uri.AbsolutePath.Trim('/')}\".");
                    }
                    return true;
                }
                if (uri.Host is "playtime")
                {
                    if (GameBiz.TryParse(uri.AbsolutePath.Trim('/'), out GameBiz biz) && GameId.FromGameBiz(biz) is GameId gameId)
                    {
                        var kvs = HttpUtility.ParseQueryString(uri.Query);
                        if (int.TryParse(kvs["pid"], out int pid))
                        {
                            await AppConfig.GetService<PlayTimeService>().StartProcessToLogAsync(gameId, pid);
                        }
                        else
                        {
                            await AppConfig.GetService<PlayTimeService>().StartProcessToLogAsync(gameId);
                        }
                    }
                    else
                    {
                        throw new ArgumentException($"Cannot parse the game_biz \"{uri.AbsolutePath.Trim('/')}\".");
                    }
                    return true;
                }
                if (uri.Host is "mod" && uri.AbsolutePath.Trim('/') is "install")
                {
                    await HandleModInstallAsync(uri);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Handle url protocol");
            User32.MessageBox(HWND.NULL, ex.Message, "HoYoShadeHub", ProtocolBoxFlags | User32.MB_FLAGS.MB_ICONERROR);
            return true;
        }
        return false;
    }

    private static async Task HandleModInstallAsync(Uri uri)
    {
        var kvs = HttpUtility.ParseQueryString(uri.Query);

        if (!int.TryParse(kvs["game_id"], out int gameId)
            || !int.TryParse(kvs["mod_id"], out int modId)
            || !long.TryParse(kvs["file_id"], out long fileId))
        {
            throw new ArgumentException("mod/install 缺少必要参数 game_id / mod_id / file_id。");
        }

        var request = new GameBananaInstallRequest(
            gameId,
            modId,
            fileId,
            kvs["file_name"],
            kvs["mod_name"],
            kvs["download_url"],
            kvs["version"],
            kvs["replace"] is "1" or "true");

        await AppConfig.GetService<GameBananaModInstaller>().InstallAsync(request);

        User32.MessageBox(
            HWND.NULL,
            request.Replace
                ? $"模组「{request.ModName}」已更新，旧版本已备份到 Mods\\_backup。"
                : $"模组「{request.ModName}」已安装到对应游戏的 Mods 目录。",
            "HoYoShadeHub",
            ProtocolBoxFlags | User32.MB_FLAGS.MB_ICONINFORMATION);
    }
}
