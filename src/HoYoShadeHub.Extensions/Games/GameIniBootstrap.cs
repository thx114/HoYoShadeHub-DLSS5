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
/// <item><c>ReShade2.ini</c>（第二个 runtime 的配置）**每次启动都按主 ini 镜像同步** —— 这是
/// ReShade 自己的自然行为（观测到它就是这么生成的）。不同步的话第二 runtime 永远停在它第一次出现
/// 时那份副本上：旧宿主路径、旧插件开关全留在里面，旧插件永远启用、启动器改的配置到不了它
/// （用户报过：崩铁两个窗口两个 ReShade 实例，旧的关不掉、新的启不了）。</item>
/// </list>
///
/// <para>每步失败都吞掉（只反映在返回值里），不挡启动。</para>
/// </summary>
public static class GameIniBootstrap
{
    public const string SecondaryIniFileName = "ReShade2.ini";
    public const string TutorialDoneValue = "4";

    /// <summary>镜像同步时留给第二 runtime 自己的节：热键、覆盖层窗口布局属于各 runtime 的使用状态</summary>
    private static readonly string[] SecondaryOwnedSections = ["INPUT", "OVERLAY"];

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

            // 覆盖包（每游戏插件包）用户内容：ini_config.json 强制写入 + presets\ 导入。
            // 放在镜像 ReShade2.ini **之前**，第二 runtime 的配置自然带上。
            ApplyPackUserContent(ini, result);

            // 第二个 runtime 的 ini：缺失时预复制一份（教程标记一定在场）；
            // 已存在时也按主 ini 镜像同步 —— 不然它永远停在第一次出现时的那份旧副本上
            string secondary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ini))!, SecondaryIniFileName);
            bool secondaryExisted = File.Exists(secondary);
            if (SyncSecondaryIni(ini, secondary))
            {
                if (!secondaryExisted)
                {
                    result.CreatedSecondary = true;
                }
                else
                {
                    result.SyncedSecondary = true;
                }
            }
        }
        catch
        {
            result.Failed = true;
        }

        return result;
    }

    /// <summary>
    /// 把主 ini 的「管理态」镜像进第二 runtime 的 ini：两边都有/主有的键以主为准，
    /// 主没有的键从副本里摘掉（死键，比如插件卸载后的残留）。
    /// <c>[INPUT]</c> / <c>[OVERLAY]</c> 是各 runtime 自己的使用状态，不动（只把教程标记写过去）。
    /// </summary>
    /// <returns>有没有实际写入</returns>
    private static bool SyncSecondaryIni(string mainIniPath, string secondaryPath)
    {
        IniDocument main = IniDocument.Load(mainIniPath);
        IniDocument secondary = File.Exists(secondaryPath)
            ? IniDocument.Load(secondaryPath)
            : IniDocument.CreateEmpty();

        bool changed = false;
        var owned = new HashSet<string>(SecondaryOwnedSections, StringComparer.OrdinalIgnoreCase);

        foreach (string section in main.GetSections())
        {
            if (owned.Contains(section))
            {
                continue;
            }

            foreach (string key in main.GetSectionKeys(section))
            {
                string? value = main.GetValue(section, key);
                if (!string.Equals(secondary.GetValue(section, key), value, StringComparison.Ordinal))
                {
                    secondary.SetValue(section, key, value ?? string.Empty);
                    changed = true;
                }
            }

            // 主 ini 里没有的键（死键）摘掉
            foreach (string key in secondary.GetSectionKeys(section).ToList())
            {
                if (!main.ContainsKey(section, key))
                {
                    secondary.RemoveKey(section, key);
                    changed = true;
                }
            }
        }

        // 副本里有、主 ini 已经没有的节（比如整节废弃的 [RENODX-XXX]）：键也一并摘掉
        foreach (string section in secondary.GetSections())
        {
            if (owned.Contains(section)
                || main.GetSections().Contains(section, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string key in secondary.GetSectionKeys(section).ToList())
            {
                secondary.RemoveKey(section, key);
                changed = true;
            }
        }

        if (!string.Equals(secondary.GetValue("OVERLAY", "TutorialProgress")?.Trim(), TutorialDoneValue, StringComparison.Ordinal))
        {
            secondary.SetValue("OVERLAY", "TutorialProgress", TutorialDoneValue);
            changed = true;
        }

        // 第二 runtime（FG swapchain 那条链）不加载任何插件：AddonPath 恒为空。
        // 不然 dlss5 这类 present 路径插件会挂到第二 runtime 上，把帧生成 swapchain
        // 的 present 再处理一遍（NR 输出回流 = 反复 NR）。主 ini 的 AddonPath 只服务第一 runtime。
        if (!string.IsNullOrEmpty(secondary.GetValue("ADDON", "AddonPath")))
        {
            secondary.SetValue("ADDON", "AddonPath", string.Empty);
            changed = true;
        }

        if (changed || !File.Exists(secondaryPath))
        {
            secondary.Save(secondaryPath);
            return true;
        }

        return false;
    }

    /// <summary>
    /// 覆盖包用户内容（ini_config.json / presets\）：游戏 ini 的 AddonPath 指向每游戏插件包时生效。
    /// 失败全吞 —— 用户内容只是增强，不挡启动。
    /// </summary>
    private static void ApplyPackUserContent(string ini, GameIniBootstrapResult result)
    {
        try
        {
            string? addonDir = ReShadeProfile.Load(ini).ResolveAddonDirectory();
            string? packRoot = GameAddonPackUserContent.PackRootOfAddonDirectory(addonDir);
            if (packRoot is null || addonDir is null)
            {
                return;
            }

            if (GamePackIniConfig.Load(packRoot) is { } config && config.Apply(ini))
            {
                result.AppliedPackIniConfig = true;
            }

            int imported = GameAddonPackUserContent.SyncPresets(packRoot, addonDir);
            if (imported > 0)
            {
                result.ImportedPackPresets = imported;
            }
        }
        catch
        {
            // ignore
        }
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

    /// <summary>已存在的 ReShade2.ini 按主 ini 同步过（第二 runtime 的旧状态被刷新）</summary>
    public bool SyncedSecondary { get; internal set; }

    /// <summary>把指向别的 HoYoShade 的路径对齐回了当前宿主</summary>
    public bool Aligned { get; internal set; }

    /// <summary>覆盖包 ini_config.json 这轮写进了游戏 ini</summary>
    public bool AppliedPackIniConfig { get; internal set; }

    /// <summary>从覆盖包 presets\ 新导入的预设数</summary>
    public int ImportedPackPresets { get; internal set; }

    public IReadOnlyList<string> AlignKeys { get; internal set; } = [];

    /// <summary>中途异常（已吞，不挡启动）</summary>
    public bool Failed { get; internal set; }

    public bool ChangedAnything => CreatedFromTemplate || TutorialMarkedDone || CreatedSecondary || SyncedSecondary || Aligned;
}
