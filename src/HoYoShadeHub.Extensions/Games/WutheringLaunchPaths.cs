namespace HoYoShadeHub.Extensions.Games;

/// <summary>
/// 解析鸣潮真正要注入的目录（<c>Client-Win64-Shipping.exe</c> 所在目录）。
///
/// <para>
/// 用户注册的可能是根目录的启动器壳（<c>Wuthering Waves.exe</c>）、也可能是本体 exe 本身，
/// 而各渠道的目录层级并不一致：
/// <list type="bullet">
/// <item><c>&lt;安装&gt;\Wuthering Waves Game\Client\Binaries\Win64</c> —— 官方 PC 版（中间多一层 <c>Client</c>）</item>
/// <item><c>&lt;安装&gt;\Wuthering Waves Game\Binaries\Win64</c> —— 旧版 / 部分渠道</item>
/// <item><c>&lt;安装&gt;\Client\Binaries\Win64</c></item>
/// <item><c>&lt;安装&gt;\Binaries\Win64</c></item>
/// </list>
/// </para>
///
/// <para>
/// 先按已知层级逐个试，再在注册目录下做<strong>有深度上限</strong>的递归兜底；都认不出就返回
/// <c>null</c>，绝不瞎猜一个目录出来 —— 猜错会把 ReShade 注进壳进程、或把 ini 写到不该写的地方。
/// 只认 <c>Client-Win64-Shipping.exe</c> 这个名字：同为 <c>*-Win64-Shipping.exe</c> 的别的游戏
/// （如卡拉彼丘的 <c>Calabiyau-Win64-Shipping.exe</c>）不会被误判成鸣潮。
/// </para>
/// </summary>
public static class WutheringLaunchPaths
{
    public const string ClientProcessName = "Client-Win64-Shipping";

    /// <summary>递归兜底在注册目录之下的最大层数（官方布局最深是 4 层：Wuthering Waves Game\Client\Binaries\Win64）</summary>
    private const int MaxSearchDepth = 4;

    public static string? ResolveClientDirectory(string? registeredDirectory)
    {
        if (string.IsNullOrWhiteSpace(registeredDirectory)) return null;

        foreach (string candidate in KnownLayouts(registeredDirectory))
        {
            if (File.Exists(Path.Combine(candidate, ClientProcessName + ".exe")))
                return Path.GetFullPath(candidate);
        }

        return SearchDepthLimited(registeredDirectory);
    }

    /// <summary>已知布局（相对注册目录），顺序即优先级。用于诊断日志，也可以给界面展示。</summary>
    public static IEnumerable<string> KnownLayouts(string registeredDirectory)
    {
        if (string.IsNullOrWhiteSpace(registeredDirectory)) yield break;

        yield return registeredDirectory;
        yield return Path.Combine(registeredDirectory, "Binaries", "Win64");
        yield return Path.Combine(registeredDirectory, "Client", "Binaries", "Win64");
        yield return Path.Combine(registeredDirectory, "Wuthering Waves Game", "Binaries", "Win64");
        yield return Path.Combine(registeredDirectory, "Wuthering Waves Game", "Client", "Binaries", "Win64");
    }

    /// <summary>
    /// 注册目录本身不吃已知布局时的兜底：广度优先往下找 <c>Client-Win64-Shipping.exe</c>，
    /// 只走到 <see cref="MaxSearchDepth"/> 层。权限不足 / 目录消失都当没找到（不抛）。
    /// </summary>
    private static string? SearchDepthLimited(string registeredDirectory)
    {
        try
        {
            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((registeredDirectory, 0));
            while (queue.Count > 0)
            {
                (string path, int depth) = queue.Dequeue();
                if (File.Exists(Path.Combine(path, ClientProcessName + ".exe")))
                    return Path.GetFullPath(path);

                if (depth >= MaxSearchDepth) continue;

                foreach (string child in Directory.EnumerateDirectories(path))
                {
                    try
                    {
                        // 跳过符号链接 / 目录联接，别绕进环里
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                    }
                    catch
                    {
                        continue;
                    }

                    queue.Enqueue((child, depth + 1));
                }
            }
        }
        catch
        {
            // 没权限 / 目录没了：当没找到
        }

        return null;
    }
}
