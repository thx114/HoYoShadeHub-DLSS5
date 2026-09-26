using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.Background;
using HoYoShadeHub.Features.GameLauncher;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 游戏条目在 Hub 这边的胶水：把 <see cref="GameDiscoveryService"/> 和 Hub 自己的
/// 游戏检测（<see cref="GameLauncherService.GetGameInstallPath(GameBiz)"/>）接起来。
/// </summary>
internal static class GameCatalog
{
    /// <summary>游戏表落盘位置：&lt;用户数据目录&gt;\.hysx\games.json</summary>
    public static string? StorePath =>
        string.IsNullOrWhiteSpace(AppConfig.UserDataFolder)
            ? null
            : GameEntryStore.GetDefaultPath(AppConfig.UserDataFolder);

    /// <summary>新读一份 store + 发现服务（每次调用都是最新的盘上状态）</summary>
    public static GameDiscoveryService CreateService()
    {
        string? path = StorePath;
        GameEntryStore store = path is null ? new GameEntryStore() : GameEntryStore.Load(path);
        return new GameDiscoveryService(store) { StorePath = path ?? string.Empty };
    }

    /// <summary>
    /// Hub 已知的游戏 → 发现候选。
    /// 用 Hub 自己的安装路径检测（**不扫盘**，见 GAMES-AND-INJECT.md §5）。
    /// </summary>
    public static List<KnownGameCandidate> KnownCandidates()
    {
        var candidates = new List<KnownGameCandidate>();

        foreach (GameBiz biz in GameBiz.AllGameBizs)
        {
            string? installPath = GameLauncherService.GetGameInstallPath(biz);
            if (string.IsNullOrWhiteSpace(installPath))
            {
                continue;
            }

            candidates.Add(new KnownGameCandidate(
                biz,
                DisplayNameOf(biz),
                installPath,
                GameLauncherService.GetGameExeName(biz)));
        }

        return candidates;
    }

    /// <summary>发现全量条目（已知 + 自定义）</summary>
    public static List<GameEntry> DiscoverAll(GameDiscoveryService service) =>
        service.DiscoverAll(KnownCandidates());

    /// <summary>
    /// 拿某个客户端对应的条目；Hub 没探到（游戏装在别处 / 用了别的客户端）就用
    /// 当前客户端的安装路径现造一条，保证启动器页的「注入模式」永远有地方存。
    /// </summary>
    public static GameEntry? GetOrCreate(GameDiscoveryService service, GameId? gameId)
    {
        if (gameId is null || string.IsNullOrWhiteSpace(gameId.GameBiz.Value))
        {
            return null;
        }

        List<GameEntry> all = DiscoverAll(service);

        GameEntry? entry = GameDiscoveryService.FindByBiz(all, gameId.GameBiz);
        if (entry is not null)
        {
            return entry;
        }

        // 自定义游戏：顶部游戏列表用的是**合成 biz**，反查一下
        GameEntry? custom = FindByCustomBiz(all, gameId.GameBiz);
        if (custom is not null)
        {
            RegisterCustomGame(custom);
            return custom;
        }

        string? installPath = GameLauncherService.GetGameInstallPath(gameId);
        string? exeName = GameLauncherService.GetGameExeName(gameId.GameBiz);

        entry = new GameEntry(GameEntry.MakeBizId(gameId.GameBiz), DisplayNameOf(gameId.GameBiz))
        {
            ExePath = GameDiscoveryService.PickMainExe(installPath, exeName),
            Biz = gameId.GameBiz,
        };

        service.Store.ApplyTo(entry);
        return entry;
    }

    /// <summary>是不是自定义游戏的合成 biz</summary>
    public static bool IsCustomBiz(GameBiz biz) =>
        biz.Value.StartsWith("custom_", StringComparison.OrdinalIgnoreCase);

    /// <summary>合成 biz（<c>custom_xxxxxxxxxxxx</c>）反查自定义游戏</summary>
    public static GameEntry? FindByCustomBiz(IEnumerable<GameEntry> entries, GameBiz biz) =>
        biz.Value.StartsWith("custom_", StringComparison.OrdinalIgnoreCase)
            ? entries.FirstOrDefault(e => e.IsCustom
                                          && string.Equals(e.CustomBizValue, biz.Value, StringComparison.OrdinalIgnoreCase))
            : null;

    /// <summary>自定义游戏条目（顶部游戏列表要把它们摆进去）</summary>
    public static List<GameEntry> CustomEntries()
    {
        var entries = new List<GameEntry>();
        try
        {
            CreateService().AppendCustom(entries);
        }
        catch
        {
            // 存坏了就当没有
        }

        return entries;
    }

    /// <summary>
    /// 把自定义游戏的安装路径登记进 AppConfig。
    /// 启动器页、DX12 开关、启动选项这些地方都是 <c>AppConfig.GetGameInstallPath(biz)</c> 认路的，
    /// 登记过之后自定义游戏就能像已知游戏一样被这些代码处理。
    /// </summary>
    public static void RegisterCustomGame(GameEntry entry)
    {
        if (!entry.IsCustom)
        {
            return;
        }

        // 背景不依赖游戏目录，先给上（用户：加蓝色星原就带这个背景）
        ApplyBuiltinBackground(entry);

        if (entry.GameDirectory is not { } directory)
        {
            return;
        }

        try
        {
            AppConfig.SetGameInstallPath(entry.CustomBiz, directory);
        }
        catch
        {
            // ignore
        }
    }

    #region 内置背景（随包的宣传动图）

    /// <summary>随包的默认背景都放这儿（<c>Assets\Video\</c>）</summary>
    public const string BuiltinBackgroundFolder = "Video";

    /// <summary>蓝色星原：旅谣的宣传动图（照搬官方活动页那段 6 秒循环，见 GAMES-AND-INJECT.md §8.9）</summary>
    public const string AzurPromiliaVideo = "azurpromilia.mp4";

    /// <summary>同一张画面的静帧 —— 选择界面那张卡片只认图片，动图给它会是一片空白</summary>
    public const string AzurPromiliaPoster = "azurpromilia.jpg";

    /// <summary>鸣潮的循环视频背景（1080p / 无音轨，随包）</summary>
    public const string WutheringWavesVideo = "wutheringwaves.mp4";

    /// <summary>鸣潮视频的首帧 —— 视频不在时兜底，选择界面那张卡片也只认图片</summary>
    public const string WutheringWavesPoster = "wutheringwaves.jpg";

    /// <summary>明日方舟：终末地的 KV 背景图（1920x1920 方图裁成 16:9 再压过）</summary>
    public const string EndfieldBackground = "endfield.jpg";

    /// <summary>随包资源在盘上的全路径</summary>
    public static string BuiltinBackgroundPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", BuiltinBackgroundFolder, fileName);

    /// <summary>这条自定义游戏是不是蓝色星原（看 exe 名 / 显示名，认不出就 false）</summary>
    public static bool IsAzurPromilia(GameEntry entry)
    {
        if (!entry.IsCustom)
        {
            return false;
        }

        if (entry.ExePath is { } exe &&
            string.Equals(Path.GetFileName(exe), "AzurPromilia.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string label = entry.DisplayName ?? string.Empty;
        return label.Contains("蓝色星原", StringComparison.Ordinal)
               || label.Contains("旅谣", StringComparison.Ordinal)
               || label.Contains("Azur Promilia", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>这条自定义游戏是不是鸣潮（看 exe 名 / 显示名，认不出就 false）</summary>
    public static bool IsWutheringWaves(GameEntry entry)
    {
        if (!entry.IsCustom)
        {
            return false;
        }

        if (entry.ExePath is { } exe &&
            string.Equals(Path.GetFileName(exe), "Wuthering Waves.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string name = Path.GetFileNameWithoutExtension(entry.ExePath ?? string.Empty);
        string label = entry.DisplayName ?? string.Empty;
        return name.Contains("Wuthering", StringComparison.OrdinalIgnoreCase)
               || label.Contains("Wuthering", StringComparison.OrdinalIgnoreCase)
               || label.Contains("鸣潮", StringComparison.Ordinal);
    }

    /// <summary>这条自定义游戏是不是明日方舟：终末地（看 exe 名 / 显示名，认不出就 false）</summary>
    public static bool IsEndfield(GameEntry entry)
    {
        if (!entry.IsCustom)
        {
            return false;
        }

        if (entry.ExePath is { } exe &&
            string.Equals(Path.GetFileName(exe), "Endfield.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string name = Path.GetFileNameWithoutExtension(entry.ExePath ?? string.Empty);
        string label = entry.DisplayName ?? string.Empty;
        return name.Contains("Endfield", StringComparison.OrdinalIgnoreCase)
               || label.Contains("Endfield", StringComparison.OrdinalIgnoreCase)
               || label.Contains("终末地", StringComparison.Ordinal);
    }

    /// <summary>
    /// 这条自定义游戏随包背景的文件名；不是内置支持的游戏 / 资源不在就返回 null。
    /// 鸣潮同蓝色星原：随包动图直接播，视频不在才退回首帧图；终末地只有图片；
    /// 蓝色星原沿用原来的动图（那一套一直无视这个开关，见 GAMES-AND-INJECT.md §8.9）。
    /// </summary>
    public static string? BuiltinBackgroundAssetName(GameEntry entry)
    {
        if (IsAzurPromilia(entry))
        {
            return File.Exists(BuiltinBackgroundPath(AzurPromiliaVideo)) ? AzurPromiliaVideo : null;
        }

        if (IsWutheringWaves(entry))
        {
            // 跟蓝色星原一样：这是随包带的（用户点名要的）动图背景，直接播视频；
            // 视频文件不在才退回首帧图。
            if (File.Exists(BuiltinBackgroundPath(WutheringWavesVideo)))
            {
                return WutheringWavesVideo;
            }

            return File.Exists(BuiltinBackgroundPath(WutheringWavesPoster)) ? WutheringWavesPoster : null;
        }

        if (IsEndfield(entry))
        {
            return File.Exists(BuiltinBackgroundPath(EndfieldBackground)) ? EndfieldBackground : null;
        }

        return null;
    }

    /// <summary>
    /// 这个文件名是不是**我们自己铺的**内置背景（用来跟用户自己设的背景区分）：
    /// 是的话允许重新铺一遍，不是的话绝不覆盖用户的选择。
    /// </summary>
    public static bool IsBuiltinBackgroundAsset(string? fileName) =>
        string.Equals(fileName, WutheringWavesVideo, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, WutheringWavesPoster, StringComparison.OrdinalIgnoreCase)
        || string.Equals(fileName, EndfieldBackground, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 用户加这几款游戏（蓝色星原 / 鸣潮 / 明日方舟：终末地）就自动带上随包背景：
    /// 把资源复制到 <c>&lt;用户数据目录&gt;\bg\</c>，再写成这个游戏的 <c>custom_bg_</c> 设置。
    /// 用户自己设过背景就不动他的；当前用的正是我们铺的内置资源时允许重铺（随包资源换过也能跟上）。
    /// </summary>
    public static void ApplyBuiltinBackground(GameEntry entry)
    {
        try
        {
            if (BuiltinBackgroundAssetName(entry) is not { } assetName
                || string.IsNullOrWhiteSpace(AppConfig.UserDataFolder))
            {
                return;
            }

            GameBiz biz = entry.CustomBiz;
            string? current = AppConfig.GetCustomBg(biz);
            if (AppConfig.GetEnableCustomBg(biz) && !string.IsNullOrWhiteSpace(current)
                && !IsBuiltinBackgroundAsset(current))
            {
                return;
            }

            string source = BuiltinBackgroundPath(assetName);
            if (!File.Exists(source))
            {
                return;
            }

            string directory = Path.Combine(AppConfig.UserDataFolder, "bg");
            Directory.CreateDirectory(directory);

            string target = Path.Combine(directory, assetName);
            if (!File.Exists(target) || new FileInfo(target).Length != new FileInfo(source).Length)
            {
                File.Copy(source, target, overwrite: true);
            }

            AppConfig.SetCustomBg(biz, assetName);
            AppConfig.SetEnableCustomBg(biz, true);
        }
        catch
        {
            // 背景是锦上添花，坏了不该影响加游戏
        }
    }

    /// <summary>
    /// 卡片要用的背景：设的自定义背景是**视频**时换成随包的静帧
    /// （<c>CachedImage</c> 播不了视频）；没认出来就返回 null，让调用方用兜底图。
    /// </summary>
    public static string? CardBackgroundFor(GameEntry entry, string? customBgPath)
    {
        if (customBgPath is null || !BackgroundService.FileIsSupportedVideo(customBgPath))
        {
            return null;
        }

        // 有视频背景的游戏各配一张静帧；终末地本来就是图片，走不到这里
        string? posterName = IsAzurPromilia(entry) ? AzurPromiliaPoster
            : IsWutheringWaves(entry) ? WutheringWavesPoster
            : null;
        if (posterName is null)
        {
            return null;
        }

        string poster = BuiltinBackgroundPath(posterName);
        return File.Exists(poster) ? poster : null;
    }

    #endregion

    /// <summary>写回注入模式开关（只影响这一个游戏）</summary>
    public static void SetInjectMode(GameDiscoveryService service, GameEntry entry, bool value)
    {
        entry.UseInjectMode = value;
        service.Store.SetUseInjectMode(entry.Id, value);
        service.Store.Save(service.StorePath);
    }

    /// <summary>
    /// addon 文件名 → 认领它的扩展条目 id（靠目录里的 <c>addonPatterns</c>）。
    /// 每游戏插件页的版本下拉靠它把盘上的 addon 文件对到扩展的版本归档。
    /// 认不出来返回 null。
    /// </summary>
    public static string? ExtensionIdOfAddonFile(string addonFileName)
    {
        try
        {
            ExtensionManifest[] manifests = ExtensionCatalogService.LoadBuiltin().Extensions;
            return ExtensionAddonMatcher.MatchExtensionId(manifests, addonFileName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// addon 文件名 → 它属于哪个扩展条目的 tags（靠目录里的 <c>addonPatterns</c> 认领）。
    /// 用来判断「这个插件是不是 DLSS5 类，要不要那套 dll」。
    /// </summary>
    public static IReadOnlyList<string>? TagsOfAddonFile(string addonFileName)
    {
        try
        {
            ExtensionManifest[] manifests = ExtensionCatalogService.LoadBuiltin().Extensions;
            AddonFileInfo? file = AddonFileInfo.Parse(addonFileName);
            if (file is null)
            {
                return null;
            }

            Dictionary<string, List<AddonFileInfo>> matched = ExtensionAddonMatcher.Match(manifests, [file]);
            foreach (string id in matched.Keys)
            {
                if (manifests.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase)) is { } manifest)
                {
                    return manifest.Tags;
                }
            }
        }
        catch
        {
            // 认不出来就当普通插件
        }

        return null;
    }

    /// <summary>
    /// 切游戏时同步「DLSS5 Feed」在 ReShade 预设里的开关。
    ///
    /// <para>
    /// 效果开关只存在于 <c>[GENERAL] PresetPath</c> 指的那个预设文件里，而它常常是
    /// 多个游戏共用的（本机都指向 <c>Presets/Mod OFF.ini</c>）—— 所以每次切游戏都按
    /// 「当前这个游戏有没有开 dlss5-feed」重新写一次：开着就补上
    /// Lumenite_Kernel + DLSS5_Feed，没开就去掉。幂等，别的内容不动。
    /// </para>
    /// </summary>
    public static void SyncDlss5FeedPreset(GameId? gameId)
    {
        try
        {
            ShadeHost? host = PluginHostLocator.Resolve(out _);
            if (host is null)
            {
                return;
            }

            GameEntry? entry = GetOrCreate(CreateService(), gameId);
            if (entry is null)
            {
                return;
            }

            var service = new GamePluginService(
                entry,
                host,
                PluginHostLocator.AddonNameCachePath,
                AddonCandidateNames(),
                TagsOfAddonFile);

            service.SyncDlss5FeedPreset(out _);
        }
        catch
        {
            // 预设同步失败不该影响切游戏
        }
    }

    /// <summary>装了哪些扩展、叫什么名字 —— 给 addon 内部名的二进制匹配当候选</summary>
    public static IEnumerable<string> AddonCandidateNames()
    {
        try
        {
            return ExtensionCatalogService.LoadBuiltin().Extensions
                .SelectMany(e => new[] { e.Name, e.Source.AssetName ?? string.Empty })
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public static string DisplayNameOf(GameBiz biz)
    {
        string game = biz.ToGameName();
        if (string.IsNullOrWhiteSpace(game))
        {
            return biz.Value;
        }

        string server = biz.ToGameServerName();
        return string.IsNullOrWhiteSpace(server) ? game : $"{game}（{server}）";
    }

    /// <summary>这条条目的 ini / exe 状态，界面上直接显示</summary>
    public static string Describe(GameEntry entry)
    {
        var parts = new List<string>();
        parts.Add(entry.ProcessName ?? "未确定主程序");
        parts.Add(entry.HasReShadeIni ? "✅ ReShade.ini" : "⚠ 没有 ReShade.ini");
        if (entry.IsCustom)
        {
            parts.Add("自定义");
        }

        return string.Join(" · ", parts);
    }
}
