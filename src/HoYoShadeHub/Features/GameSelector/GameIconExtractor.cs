using HoYoShadeHub.Extensions.Games;
using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace HoYoShadeHub.Features.GameSelector;

/// <summary>
/// 自定义游戏在顶部游戏列表里也要有个图标 —— 直接从 exe 里抠出来，存到
/// <c>&lt;用户数据目录&gt;\.hysx\gameicons\&lt;短id&gt;.png</c>，下次直接用。
///
/// 抠不出来的（拿不到缩略图 / exe 不在了）就用内置的占位图，不至于变成一个空格子。
/// </summary>
internal static class GameIconExtractor
{
    /// <summary>兜底图标：抠不到就用它</summary>
    public const string FallbackIcon = "ms-appx:///Assets/Image/icon_cloudgame.png";

    /// <summary>自定义游戏的背景图：Hub 没有它的官方背景，用应用自带的默认背景</summary>
    public const string CustomBackground = "ms-appx:///Assets/Image/UI_CutScene_1130320101A.png";

    public static string? CachePathOf(GameEntry entry)
    {
        if (string.IsNullOrWhiteSpace(AppConfig.UserDataFolder))
        {
            return null;
        }

        try
        {
            return Path.Combine(AppConfig.UserDataFolder, ".hysx", "gameicons", entry.ShortId + ".png");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>已经抠过就直接返回 file:// URI，否则返回 null</summary>
    public static string? GetCached(GameEntry entry)
    {
        string? path = CachePathOf(entry);
        return path is not null && File.Exists(path) ? new Uri(path).AbsoluteUri : null;
    }

    /// <summary>确保有图标；返回可直接喂给 Image.Source 的 URI</summary>
    public static async Task<string?> EnsureAsync(GameEntry entry)
    {
        try
        {
            if (GetCached(entry) is { } cached)
            {
                return cached;
            }

            if (entry.ExePath is not { } exe || !File.Exists(exe) || CachePathOf(entry) is not { } target)
            {
                return null;
            }

            StorageFile file = await StorageFile.GetFileFromPathAsync(exe);

            // SingleItem 模式的缩略图在 exe 上就是它的图标。
            // 要 256 的：exe 里一般存着 256x256 那一档，取小了拉到 40x40 会糊。
            using StorageItemThumbnail thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, 256, ThumbnailOptions.ResizeThumbnail);
            if (thumbnail is null || thumbnail.Size == 0)
            {
                return null;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using Stream source = thumbnail.AsStreamForRead();
            using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination);

            return GetCached(entry);
        }
        catch
        {
            // 抠不出来就算了，调用方会用兜底图标
            return null;
        }
    }
}
