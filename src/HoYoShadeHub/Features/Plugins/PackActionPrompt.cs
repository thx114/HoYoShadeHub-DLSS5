using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 覆盖包 auto.json 动作的同意弹窗：列出**全部**动作和步骤，
/// 高危动作（覆盖文件 / 注册表加游戏 / 清 ini / 删滤镜）标红并展开全部文件清单。
/// 逐项勾选、默认全装；「全部不要」= 同意但全禁（包内容再变之前不再弹）。
/// 导入覆盖包时（<see cref="OverlayPackActionPrompter"/>）和每游戏插件页兜底共用。
/// </summary>
public static class PackActionConsentDialog
{
    /// <param name="gameNames">这批动作会落到哪些游戏上（导入时可能一次多个服）；null = 当前游戏</param>
    /// <returns>null = 下次再说（不记同意，下次还会问）；否则是用户**没勾选**的动作名（空 = 全装）</returns>
    public static async Task<List<string>?> ShowAsync(XamlRoot xamlRoot, string packRoot, List<PackAutoAction> actions, string? gameNames = null)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "这个覆盖包（auto.json）请求执行以下自定义动作。勾选要启用的（默认全部启用）："
                   + (string.IsNullOrWhiteSpace(gameNames) ? string.Empty : $"\n影响的游戏：{gameNames}"),
            TextWrapping = TextWrapping.Wrap,
        });

        var boxes = new List<CheckBox>();
        foreach (PackAutoAction action in actions)
        {
            var box = new CheckBox { IsChecked = true, Tag = action.Name };
            var content = new StackPanel { Spacing = 2 };
            content.Children.Add(new TextBlock
            {
                Text = action.Name + (action.RunOnLaunch ? "　（每次启动前自动检查）" : string.Empty),
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });

            int index = 1;
            foreach (PackActionStep step in action.Steps)
            {
                bool highRisk = PackActionDescriber.IsHighRisk(step);
                var stepText = new TextBlock
                {
                    Text = $"{index}. {(highRisk ? "⚠ " : string.Empty)}{PackActionDescriber.Describe(step, packRoot)}",
                    TextWrapping = TextWrapping.Wrap,
                };
                if (highRisk)
                {
                    stepText.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
                }

                content.Children.Add(stepText);

                foreach (string detail in PackActionDescriber.HighRiskDetails(step, packRoot))
                {
                    content.Children.Add(new TextBlock
                    {
                        Text = "　　· " + detail,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
                        TextWrapping = TextWrapping.Wrap,
                    });
                }

                index++;
            }

            box.Content = content;
            boxes.Add(box);
            panel.Children.Add(box);
        }

        var scroll = new ScrollViewer { Content = panel, MaxHeight = 420 };
        var dialog = new ContentDialog
        {
            XamlRoot = xamlRoot,
            Title = "覆盖包动作确认",
            Content = scroll,
            PrimaryButtonText = "启用勾选的",
            SecondaryButtonText = "全部不要",
            CloseButtonText = "下次再说",
            DefaultButton = ContentDialogButton.Primary,
            MaxWidth = 560,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result switch
        {
            ContentDialogResult.Primary => boxes.Where(b => b.IsChecked != true).Select(b => (string)b.Tag!).ToList(),
            ContentDialogResult.Secondary => actions.Select(a => a.Name).ToList(),
            _ => null,
        };
    }
}

/// <summary>给 auto.json 动作执行造上下文 + 跑一组动作（导入时和插件页共用）</summary>
public static class PackActionExecutor
{
    /// <summary>
    /// 造一份动作上下文：插件服务能建就建（读不出 ini 就只跑不依赖 ini 的步骤）。
    /// gameId 不能为空 —— 启动选项 / OptiScaler 选择这类步骤都按它记账。
    /// </summary>
    public static LauncherActionContext BuildContext(
        GameId gameId, GameEntry? entry, ShadeHost? host, string? packRoot,
        XamlRoot? xamlRoot, bool interactive, Action<string>? report)
    {
        GamePluginService? plugins = null;
        if (entry is not null)
        {
            try
            {
                plugins = GamePluginServiceFactory.Create(entry, host);
            }
            catch
            {
                // 读不出插件状态就只跑不依赖 ini 的步骤
            }
        }

        return new LauncherActionContext
        {
            GameId = gameId,
            GameBiz = gameId.GameBiz,
            Entry = entry,
            Host = host,
            Plugins = plugins,
            PackRoot = packRoot,
            XamlRoot = xamlRoot,
            Interactive = interactive,
            Report = report,
        };
    }

    /// <summary>逐个执行没被禁用的动作，返回每个动作的一行摘要</summary>
    public static async Task<List<string>> RunAsync(
        IEnumerable<PackAutoAction> actions, IReadOnlySet<string> disabled, LauncherActionContext context)
    {
        var results = new List<string>();
        foreach (PackAutoAction action in actions)
        {
            if (disabled.Contains(action.Name))
            {
                continue;
            }

            string summary = await LauncherActionRunner.RunAsync(action, context);
            results.Add($"「{action.Name}」：{summary}");
        }

        return results;
    }
}

/// <summary>
/// 导入覆盖包后的「动作一并办掉」：扫所有已装游戏的包目录，auto.json 还没同意过的
/// 现在弹窗；确认后**立即**对这些游戏执行（once 步骤记 .hysx_once.json，启动前自动跑会跳过）。
///
/// <para>
/// 游戏已装但 cache\games\ 目录还没生成（全新便携包首导）时，安装器把 GamePack 暂存在
/// .pending-gamepacks\&lt;hint&gt; —— 这里按「已装游戏」反查暂存并先铺进包目录，
/// 保证弹窗在导入当场就弹，不用等插件页 / 启动。游戏真没装的不管：游戏出现后插件页兜底。
/// 没有 auto.json 的旧包自然走不到这里，行为不变。
/// </para>
/// </summary>
public static class OverlayPackActionPrompter
{
    private static readonly ILogger _logger = AppConfig.GetLogger<GamePluginPage>();

    /// <returns>执行结果汇总（没有待办 / 用户都点了「下次再说」= 空串）</returns>
    public static async Task<string> PromptAndRunAsync(XamlRoot xamlRoot, ShadeHost? host, Action<string>? report)
    {
        // 按 auto.json 内容 hash 分组：同一份包内容（多个服共用一份）只弹一次窗
        var groups = new Dictionary<string, (string PackRoot, string Hash, List<PackAutoAction> Actions, List<(GameId Id, GameEntry Entry)> Games)>();

        string cacheRoot = AppConfig.CacheRoot;
        if (string.IsNullOrWhiteSpace(cacheRoot))
        {
            return string.Empty;
        }

        try
        {
            GameDiscoveryService discovery = GameCatalog.CreateService();

            // 候选游戏 = 已知 biz 里装了的 + 自定义游戏条目（目录真的存在才算）
            var candidates = new List<(GameId Id, GameEntry Entry)>();
            foreach (GameBiz biz in GameBiz.AllGameBizs)
            {
                if (GameId.FromGameBiz(biz) is not { } id)
                {
                    continue;
                }

                GameEntry? entry = GameCatalog.GetOrCreate(discovery, id);
                if (entry?.GameDirectory is { } dir && Directory.Exists(dir))
                {
                    candidates.Add((id, entry));
                }
            }

            foreach (GameEntry custom in GameCatalog.CustomEntries())
            {
                if (custom.GameDirectory is { } dir && Directory.Exists(dir))
                {
                    candidates.Add((new GameId { Id = custom.CustomBizValue, GameBiz = custom.CustomBiz }, custom));
                }
            }

            foreach ((GameId id, GameEntry entry) in candidates)
            {
                string gameKey = id.GameBiz.Value;
                string packRoot = GameAddonPack.PackDirectory(cacheRoot, gameKey);

                // 暂存的 GamePack 先铺进包目录（同 GameAddonPackService.Sync 的暂存逻辑）：
                // 全新实例导入时 cache\games\ 还没有这个游戏的目录，不铺的话弹窗永远不会在导入当场弹
                PlacePendingGamePacks(cacheRoot, gameKey, packRoot);

                List<PackAutoAction> actions = PackAutoActionFile.Load(packRoot);
                if (actions.Count == 0)
                {
                    continue;
                }

                string hash = PackActionConsent.ComputeHash(packRoot);
                if (PackActionConsent.IsConsented(gameKey, hash))
                {
                    continue;
                }

                if (!groups.TryGetValue(hash, out var group))
                {
                    group = (packRoot, hash, actions, []);
                    groups[hash] = group;
                }

                group.Games.Add((id, entry));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Scan pack actions on import");
            return string.Empty;
        }

        if (groups.Count == 0)
        {
            return string.Empty;
        }

        var summaries = new List<string>();
        foreach ((string packRoot, string hash, List<PackAutoAction> actions, List<(GameId Id, GameEntry Entry)> games) in groups.Values)
        {
            string gameNames = string.Join("、", games.Select(g => g.Entry.DisplayName));
            List<string>? disabled;
            try
            {
                disabled = await PackActionConsentDialog.ShowAsync(xamlRoot, packRoot, actions, gameNames);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pack action consent prompt on import");
                continue;
            }

            if (disabled is null)
            {
                continue;   // 下次再说：不记同意，插件页进页还会问
            }

            var disabledSet = new HashSet<string>(disabled, StringComparer.Ordinal);
            foreach ((GameId id, GameEntry entry) in games)
            {
                try
                {
                    PackActionConsent.Save(id.GameBiz.Value, hash, disabled);

                    // 先把这个游戏的 ini 定向好、包 Addons 铺好，再跑动作：
                    // set_addons 按 AddonPath 目录扫文件写 [ADDON] DisabledAddons，
                    // 目录还没指向包 / 包还没铺时「禁用全部」会扫到空目录或旧目录，白跑一次
                    // （once 记账一立，状态就定格在「全开」——崩铁 mfgunlock 事故）
                    try
                    {
                        GameIniBootstrap.Ensure(entry, host);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Ensure game ini before pack actions ({Game})", id.GameBiz.Value);
                    }

                    try
                    {
                        GameAddonPackService.Sync(id, entry, host);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Sync addon pack before pack actions ({Game})", id.GameBiz.Value);
                    }

                    LauncherActionContext context = PackActionExecutor.BuildContext(
                        id, entry, host, packRoot, xamlRoot, interactive: true, report);
                    List<string> results = await PackActionExecutor.RunAsync(actions, disabledSet, context);
                    if (results.Count > 0)
                    {
                        summaries.Add($"【{entry.DisplayName}】\n" + string.Join("\n", results));
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Run pack actions on import for {Game}", id.GameBiz.Value);
                    summaries.Add($"【{entry.DisplayName}】执行失败：{ex.Message}");
                }
            }
        }

        return string.Join("\n\n", summaries);
    }

    /// <summary>
    /// 把 .pending-gamepacks\&lt;hint&gt; 里 hint 前缀匹配这个游戏键的暂存 GamePack 铺进包目录
    /// （与 GameAddonPackService.Sync 的暂存铺放同一逻辑，铺完删除暂存）。
    /// </summary>
    private static void PlacePendingGamePacks(string cacheRoot, string gameKey, string packRoot)
    {
        try
        {
            string pendingRoot = Path.Combine(cacheRoot, "games", ".pending-gamepacks");
            if (!Directory.Exists(pendingRoot))
            {
                return;
            }

            foreach (string hintDir in Directory.EnumerateDirectories(pendingRoot))
            {
                string hint = Path.GetFileName(hintDir);
                if (!gameKey.StartsWith(hint, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Directory.CreateDirectory(packRoot);
                foreach (string pendingFile in Directory.EnumerateFiles(hintDir))
                {
                    try
                    {
                        File.Copy(pendingFile, Path.Combine(packRoot, Path.GetFileName(pendingFile)), overwrite: true);
                    }
                    catch
                    {
                        // 占用中下轮再铺
                    }
                }

                try
                {
                    Directory.Delete(hintDir, recursive: true);
                }
                catch
                {
                    // 删不掉就留着，下次再清
                }
            }
        }
        catch
        {
            // 暂存铺放只是增强，不挡主流程
        }
    }
}
