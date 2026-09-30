using System;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 插件服务的应用层工厂。
///
/// <para>
/// 页面只提供当前游戏和宿主目录，插件缓存、候选名称和标签解析器由这里统一注入。
/// 这样 UI 不需要了解插件基础设施，后续也可以在这里切换到依赖注入或测试替身。
/// </para>
/// </summary>
internal static class GamePluginServiceFactory
{
    public static GamePluginService Create(GameEntry game, ShadeHost? host)
    {
        ArgumentNullException.ThrowIfNull(game);

        return GamePluginService.Create(
            game,
            host,
            new GamePluginServiceOptions
            {
                NameCachePath = PluginHostLocator.AddonNameCachePath,
                CandidateNames = [.. GameCatalog.AddonCandidateNames()],
                TagsOfAddon = GameCatalog.TagsOfAddonFile,
            });
    }
}