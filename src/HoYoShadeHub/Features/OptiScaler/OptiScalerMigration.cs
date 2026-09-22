using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Services;
using System;
using System.Linq;

namespace HoYoShadeHub.Features.OptiScaler;

/// <summary>
/// OptiScaler 的老配置迁移。
///
/// <para>
/// 老版本：全局只有「一个被启用的构建」（state.json 里的 selected），启动器里每个游戏再勾一个
/// 「启动 OptiScaler」。新版本：左侧 OptiScaler 页一个**总开关** + 每个游戏**各选一个构建**；
/// DLSS-NR on AMD 还从 OptiScaler 挪去了「模块」页。
/// 用户在这个游戏上第一次打开启动器/相关页面时跑一次，把老的选择接上。
/// </para>
/// </summary>
internal static class OptiScalerMigration
{
    public static void Run(GameId gameId)
    {
        try
        {
            if (!AppConfig.GetUseOptiScalerLaunchOption(gameId))
            {
                return;
            }

            // 老的那个「启动 OptiScaler」勾 = 现在启动选项里的「启用OptiScaler」，所以留着不清；
            // 总开关和「哪个构建」补上就行。
            string root = AppConfig.OptiScalerRootPath;
            if (root.Length == 0)
            {
                return;
            }

            OptiScalerBuild? build = new OptiScalerLibrary(root).GetSelected();
            if (build is null)
            {
                return;
            }

            // DLSS-NR on AMD 已经不是 OptiScaler 了：跟着挪到「模块」页，顺带把这个游戏的模块开关打开
            if (string.Equals(build.SourceId, "dlssnr-amd", StringComparison.OrdinalIgnoreCase))
            {
                // 它已经不是 OptiScaler 了 —— 挪到「模块」，OptiScaler 那个勾也就不用留了
                AppConfig.SetUseOptiScalerLaunchOption(gameId, false);
                AppConfig.SetModuleEnabled("dlssnr-amd", true);
                AppConfig.SetUseModulesLaunchOption(gameId, true);
                return;
            }

            AppConfig.OptiScalerEnabled = true;
            AppConfig.SetSelectedOptiScalerId(gameId, build.Id);
        }
        catch
        {
            // 迁移失败就当没迁移：用户手动选一下就行
        }
    }
}
