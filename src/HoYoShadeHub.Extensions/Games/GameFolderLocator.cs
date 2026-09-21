namespace HoYoShadeHub.Extensions.Games;

/// <summary>找游戏目录的结果</summary>
public sealed record GameFolderSearchResult(
    string? Directory,
    int Depth,
    int ScannedDirectories)
{
    public bool Found => Directory is not null;

    /// <summary>是不是"往里找了一层"才找到的（用户选的是上层目录）</summary>
    public bool WasNested => Depth > 0;
}

/// <summary>
/// 用户点「定位游戏」时选错层级（选成启动器根目录）的兜底。
///
/// <para>
/// 典型情况：真正的游戏在 <c>D:\APPS\miHoYo Launcher\games\Genshin Impact Game</c>，
/// 但用户会直接选 <c>D:\APPS\miHoYo Launcher</c> —— 于是"找不到游戏"。
/// </para>
///
/// <para>
/// 这里只**往里面看最多 2 层目录名**，不做全盘遍历：
/// 找到含目标 exe 的目录就停，并且**优先挑名字像游戏的**（包含 <c>game</c> 的那些，
/// 例如 miHoYo 那套 <c>&lt;游戏名&gt; Game</c> 布局），把 <c>AntiCheatExpert</c>、
/// <c>cache</c>、<c>logs</c> 这类噪声目录排到最后。
/// </para>
/// </summary>
public static class GameFolderLocator
{
    /// <summary>最多往下几层</summary>
    public const int MaxDepth = 2;

    /// <summary>最多看多少个目录（防止在大目录上卡住）</summary>
    public const int MaxDirectories = 200;

    /// <summary>明显不是游戏目录的名字（命中的排到最后，不是直接跳过）</summary>
    private static readonly string[] _noise =
    [
        "anticheat", "crash", "cache", "temp", "log", "sdk", "config", "update",
        "redist", "directx", "vcredist", "webview", "screenshot", "backup",
        "download", "persistent", "rail_files", "tcls",
    ];

    /// <summary>
    /// 在 <paramref name="folder"/> 里找主程序 <paramref name="exeName"/> 所在的目录。
    /// 找不到返回 <see cref="GameFolderSearchResult.Directory"/> = null（调用方照旧报错）。
    /// </summary>
    public static GameFolderSearchResult Locate(string? folder, string? exeName, int maxDepth = MaxDepth)
    {
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(exeName) || !Directory.Exists(folder))
        {
            return new GameFolderSearchResult(null, 0, 0);
        }

        string root;
        try
        {
            root = Path.GetFullPath(folder);
        }
        catch
        {
            return new GameFolderSearchResult(null, 0, 0);
        }

        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        var candidates = new List<(string Directory, int Depth, int Noise, int Gameish)>();
        int scanned = 0;

        while (queue.Count > 0 && scanned < MaxDirectories)
        {
            (string directory, int depth) = queue.Dequeue();
            scanned++;

            if (File.Exists(Path.Combine(directory, exeName)))
            {
                string name = Path.GetFileName(directory);
                int noise = IsNoise(name) ? 1 : 0;
                int gameish = name.Contains("game", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                candidates.Add((directory, depth, noise, gameish));

                // 这一支已经找到，不用再往里钻
                continue;
            }

            if (depth >= maxDepth)
            {
                continue;
            }

            foreach (string sub in EnumerateSubDirectories(directory))
            {
                queue.Enqueue((sub, depth + 1));
            }
        }

        if (candidates.Count == 0)
        {
            return new GameFolderSearchResult(null, 0, scanned);
        }

        // 先挑名字像游戏的，再挑层级浅的，最后按名字稳定排序
        var best = candidates
            .OrderBy(c => c.Noise)
            .ThenBy(c => c.Gameish)
            .ThenBy(c => c.Depth)
            .ThenBy(c => c.Directory, StringComparer.OrdinalIgnoreCase)
            .First();

        return new GameFolderSearchResult(best.Directory, best.Depth, scanned);
    }

    private static bool IsNoise(string directoryName) =>
        _noise.Any(n => directoryName.Contains(n, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> EnumerateSubDirectories(string directory)
    {
        string[] subs;

        try
        {
            subs = Directory.GetDirectories(directory);
        }
        catch
        {
            // 没权限 / 路径太长之类
            yield break;
        }

        foreach (string sub in subs)
        {
            // 跳过 junction / 符号链接，免得绕圈
            try
            {
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            yield return sub;
        }
    }
}
