using HoYoShadeHub.Extensions.Dlls;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Services;
using System;
using System.IO;

namespace HoYoShadeHub.Features.Plugins;

internal static class GameNrRuntimeService
{
    // 显式准备/启动入口调用；导航和只读查询不得创建加载目录。
    public static void PrepareOpti(GameId game)
    {
        string? nr = AppConfig.GetGameNrVersion(game, "opti");
        var library = new OptiScalerLibrary(AppConfig.OptiScalerRootPath);
        string? id = AppConfig.GetSelectedOptiScalerId(game);
        var build = library.List(includeDirectorySize: false).Find(b => b.Id == id) ?? library.GetSelected(includeDirectorySize: false);
        if (nr is null && (build is null || !Directory.Exists(NrRuntimeLibrary.OptiDirectory(AppConfig.CacheRoot, game.GameBiz.Value, build.Id)))) return;
        if (build?.DllPath is not { } dll) throw new IOException("已选择独立 NR，但 OptiScaler 构建缺失。");
        NrRuntimeLibrary.PrepareOpti(AppConfig.CacheRoot, game.GameBiz.Value, build.Id, dll, nr);
    }
}
