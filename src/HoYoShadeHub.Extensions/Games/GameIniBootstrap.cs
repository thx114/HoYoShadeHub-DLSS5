using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>
/// 启动游戏前把这个游戏自己的 ReShade.ini 收拾到可注入状态（插件页「复制模板 / 指回当前 HoYoShade」
/// 那两个按钮的同款逻辑，下沉到这里让**启动路径**也能用）：
///
/// <list type="number">
/// <item>ini 缺失 → 从当前 HoYoShade 宿主复制模板一份；</item>
/// <item>ini 指向<strong>别的</strong> HoYoShade（不是专属包、也不是宿主根内的子目录）→ 自动对齐回当前宿主；</item>
/// <item>主 ini 的 <c>[OVERLAY] TutorialProgress</c> 确保为 4（已完成）——ReShade 6 对**每个 swapchain**
/// 各建一个 runtime，新 ini 的教程进度是 0，启动时每个 runtime 都弹「按 Home 开始教程」欢迎窗口；</item>
/// <item><c>ReShade2.ini</c>（第二个 runtime 的配置）缺失时按主 ini 预复制一份 —— 这是 ReShade 自己的
/// 自然行为（观测到它就是这么生成的），预复制只是保证教程标记一并带上。</item>
/// </list>
///
/// <para>每步失败都吞掉（只反映在返回值里），不挡启动。</para>
/// </summary>
public static class GameIniBootstrap
{
    public const string SecondaryIniFileName = "ReShade2.ini";
    public const string TutorialDoneValue = "4";

    public static GameIniBootstrapResult Ensure(GameEntry entry, ShadeHost? host)
    {
        var result = new GameIniBootstrapResult();

        if (entry.ReShadeIniPath is not { } ini)
        {
            return result;
        }

        try
        {
            if (!File.Exists(ini))
            {
                if (host is null || !File.Exists(host.ReShadeIniPath))
                {
                    result.MissingTemplate = true;
                    return result;
                }

                File.Copy(host.ReShadeIniPath, ini);
                result.CreatedFromTemplate = true;
            }

            ReShadeProfile profile = ReShadeProfile.Load(ini);

            // 欢迎窗口按 runtime 各弹一次；不管原来是什么都标成已完成（用户想重看教程可在游戏内重置，
            // 下一次启动这里再写回去 —— 换来的是默认干净）
            if (!string.Equals(profile.GetValue("OVERLAY", "TutorialProgress")?.Trim(), TutorialDoneValue, StringComparison.Ordinal))
            {
                profile.SetValue("OVERLAY", "TutorialProgress", TutorialDoneValue);
                profile.Save();
                result.TutorialMarkedDone = true;
            }

            AlignIfPointingElsewhere(ini, host, result);

            // 第二个 runtime 的 ini 由 ReShade 在它出现时自动创建（内容复制自主 ini）；
            // 预复制一份，让上面的教程标记一定在场
            string secondary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ini))!, SecondaryIniFileName);
            if (!File.Exists(secondary))
            {
                File.Copy(ini, secondary);
                result.CreatedSecondary = true;
            }
        }
        catch
        {
            result.Failed = true;
        }

        return result;
    }

    /// <summary>ini 指向别的 HoYoShade → 拉回当前宿主。专属插件包 / 宿主根内子目录是正常状态，跳过。</summary>
    private static void AlignIfPointingElsewhere(string ini, ShadeHost? host, GameIniBootstrapResult result)
    {
        if (host is null)
        {
            return;
        }

        string? addonDir = ReShadeProfile.Load(ini).ResolveAddonDirectory();
        if (string.IsNullOrWhiteSpace(addonDir) || GameAddonPack.IsPackDirectory(addonDir) || IsInsideHostRoot(addonDir, host.RootPath))
        {
            return;
        }

        string? hostAddons = host.AddonsPath;
        if (string.IsNullOrWhiteSpace(hostAddons))
        {
            return;
        }

        bool differs = !string.Equals(
            addonDir.TrimEnd('\\', '/'),
            hostAddons.TrimEnd('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
        if (!differs)
        {
            return;
        }

        ShadePathAlignResult align = ShadePathAligner.Align(ini, host);
        if (align.Changed)
        {
            result.Aligned = true;
            result.AlignKeys = align.ChangedKeys;
        }
    }

    private static bool IsInsideHostRoot(string? path, string? hostRoot)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(hostRoot))
        {
            return false;
        }

        try
        {
            string full = path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                               .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            string root = hostRoot.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                  .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            return Path.IsPathFullyQualified(full)
                   && full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary><see cref="GameIniBootstrap.Ensure"/> 这轮做了什么（调用方拿去记日志 / 刷新 UI）</summary>
public sealed class GameIniBootstrapResult
{
    /// <summary>主 ini 原本不存在，已从宿主模板复制</summary>
    public bool CreatedFromTemplate { get; internal set; }

    /// <summary>主 ini 缺失且宿主也没有模板可复制</summary>
    public bool MissingTemplate { get; internal set; }

    /// <summary>把主 ini 的教程进度标成了已完成</summary>
    public bool TutorialMarkedDone { get; internal set; }

    /// <summary>预复制了第二个 runtime 的 ReShade2.ini</summary>
    public bool CreatedSecondary { get; internal set; }

    /// <summary>把指向别的 HoYoShade 的路径对齐回了当前宿主</summary>
    public bool Aligned { get; internal set; }

    public IReadOnlyList<string> AlignKeys { get; internal set; } = [];

    /// <summary>中途异常（已吞，不挡启动）</summary>
    public bool Failed { get; internal set; }

    public bool ChangedAnything => CreatedFromTemplate || TutorialMarkedDone || CreatedSecondary || Aligned;
}
