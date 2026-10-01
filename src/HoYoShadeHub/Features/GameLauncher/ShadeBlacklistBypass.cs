using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Features.Plugins;
using System;
using System.Collections.Generic;
using System.IO;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>
/// inject.exe 硬编码黑名单（退出码 1002）的 launcher 侧绕行。
///
/// <para>
/// 黑名单本体是 inject.exe 二进制里的宽字符串表（2026-10 从字符串提取：系统进程 +
/// 各游戏名 + <c>Client-Win64-Shipping.exe</c>），没有任何配置入口，C# 侧改不掉。
/// 对黑名单进程不走 inject.exe，改由 Hub 自己的 <see cref="DllInjector"/> 直接注
/// <c>ReShade64.dll</c>（CreateRemoteThread 那套，和 OptiScaler 注入同一机制，没有黑名单）。
/// </para>
///
/// <para>上游 HoYoShade 更新 inject.exe 时要重新核对黑名单表。</para>
/// </summary>
internal static class ShadeBlacklistBypass
{
    /// <summary>
    /// inject.exe 拒注的游戏进程。黑名单里 explorer/cmd/svchost 等系统进程不可能成为游戏
    /// 注入目标，不列；只列真会被当成游戏注的。
    /// </summary>
    private static readonly HashSet<string> HardcodedProcessBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "Client-Win64-Shipping", // 鸣潮
    };

    /// <summary>这个进程名是否在 inject.exe 的硬编码黑名单里（喂给 inject.exe 会退 1002）</summary>
    public static bool IsBlacklisted(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        string name = Path.GetFileNameWithoutExtension(processName.Trim());
        return HardcodedProcessBlacklist.Contains(name);
    }

    /// <summary>
    /// 解析「真正要注的进程名」：鸣潮注册 exe 是根目录启动器壳 <c>Wuthering Waves.exe</c>
    /// （起来做客户端文件检查、拉起真身后自己退出），要注/等的是它拉起的 <c>Client-Win64-Shipping</c>。
    /// 非鸣潮（或已经是对的进程名）原样返回。
    /// </summary>
    public static string RemapInjectProcessName(GameEntry? entry, string? processName)
    {
        if (entry is not null && GameCatalog.IsWutheringWaves(entry))
        {
            return "Client-Win64-Shipping";
        }

        return processName ?? string.Empty;
    }

    /// <summary>
    /// 鸣潮真身游戏目录。ReShade64.dll 从**进程 exe 旁边**找 ReShade.ini，而注册 exe 是根目录的壳，
    /// 所以要注的 ini 得放在真身目录（&lt;安装&gt;\Wuthering Waves Game\Binaries\Win64）。
    /// 认不出 / 目录不存在返回 null。
    /// </summary>
    public static string? RealGameDirectory(GameEntry? entry)
    {
        if (entry?.GameDirectory is not { } root)
        {
            return null;
        }

        try
        {
            string candidate = Path.Combine(root, "Wuthering Waves Game", "Binaries", "Win64");
            return File.Exists(Path.Combine(candidate, "Client-Win64-Shipping.exe")) ? candidate : null;
        }
        catch
        {
            return null;
        }
    }
}
