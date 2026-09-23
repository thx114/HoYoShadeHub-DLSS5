using HoYoShadeHub.Core;
using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>DLSS5 神经渲染的承载方式 —— 决定检测里哪些项该查、哪些项查不了也不该报错</summary>
public enum Dlss5Delivery
{
    /// <summary>还不知道（没选游戏 / 读不到配置）</summary>
    Unknown = 0,

    /// <summary>两条路都没有：既没 HoYoShade 也没给游戏选 OptiScaler</summary>
    None,

    /// <summary>只有 HoYoShade（走 ReShade addon 那条）：插件页那几条要查</summary>
    HoYoShadeOnly,

    /// <summary>只有 OptiScaler（<b>不需要 HoYoShade</b>）：插件页那几条查不了，但不该报错</summary>
    OptiScalerOnly,

    /// <summary>两条都有</summary>
    Both,
}

/// <summary>
/// 跑一次 DLSS5 兼容性检测需要的全部上下文。
///
/// <para>
/// 检测分三块：显卡/驱动（全局）、启动器（当前 HoYoShade）、游戏目录（当前游戏）。
/// 所以它既要「当前 HoYoShade 宿主」，也要「当前游戏」的 <see cref="GameEntry"/> + 读好的 profile。
/// </para>
/// </summary>
public sealed class Dlss5CompatContext
{
    public ShadeHost? ShadeHost { get; init; }

    public GameEntry? Game { get; init; }

    /// <summary>当前游戏的 GameId（拿 AppConfig 里按游戏记的开关用）</summary>
    public GameId? GameId { get; init; }

    /// <summary>游戏目录（exe 所在目录），可能为空</summary>
    public string? GameDirectory => Game?.GameDirectory;

    public string? GameReShadeIniPath => Game?.ReShadeIniPath;

    public string? GameExePath => Game?.ExePath;

    public string? GameBiz => Game?.Biz?.Value;

    public string? GameDisplayName => Game?.DisplayName;

    /// <summary>这个游戏的 ReShade.ini（读不了是 null，原因见 <see cref="ProfileError"/>）</summary>
    public ReShadeProfile? Profile { get; init; }

    public string? ProfileError { get; init; }

    /// <summary>这个游戏的插件状态（读不出来是 null）</summary>
    public IReadOnlyList<GameAddonState>? AddonStates { get; init; }

    /// <summary>当前 hook 点（读不出来给 0）</summary>
    public int HookPoint { get; init; }

    /// <summary>「用哪个游戏的服务读插件/ini」的操作对象  修复要写盘，所以留着 service</summary>
    public GamePluginService? PluginService { get; init; }

    /// <summary>
    /// DLSS5 的承载方式。
    ///
    /// <para>
    /// 现在有两条路：
    /// <list type="bullet">
    /// <item><b>HoYoShade</b>：ReShade + addon（renodx-dlss5 / dlss5-bridge）那条，需要装 HoYoShade；</item>
    /// <item><b>OptiScaler</b>：用 OptiScaler 的 DLSS-NR（神经渲染）实现，<b>不需要 HoYoShade</b>，
    /// 只要给游戏选了 OptiScaler 构建。</item>
    /// </list>
    /// 检测项要根据它决定「哪些项该查、哪些项查不了但也不该报错」。
    /// </para>
    /// </summary>
    public Dlss5Delivery Delivery { get; init; } = Dlss5Delivery.Unknown;

    /// <summary>这个游戏选的 OptiScaler 构建 dll 全路径（走 OptiScaler 那条路时才有）</summary>
    public string? OptiScalerDllPath { get; init; }

    /// <summary>有没有 HoYoShade 宿主</summary>
    public bool HasShadeHost => ShadeHost is not null;

    /// <summary>是不是「只装了 OptiScaler、没装 HoYoShade」</summary>
    public bool IsOptiScalerOnly => Delivery == Dlss5Delivery.OptiScalerOnly;

    /// <summary>
    /// 这个游戏当前实际在用的 DLSS5 承载方式（按配置算）。
    /// 检测里判断「要不要因为缺 HoYoShade 报错」全靠它。
    /// </summary>
    public static Dlss5Delivery ResolveDelivery(GameId? gameId, ShadeHost? host)
    {
        bool hasOpti = !string.IsNullOrWhiteSpace(AppConfig.GetSelectedOptiScalerDll(gameId));

        if (hasOpti)
        {
            // OptiScaler 和 HoYoShade 不互斥，但「只装 Opti」是用户明确要支持的一条路
            return host is null ? Dlss5Delivery.OptiScalerOnly : Dlss5Delivery.Both;
        }

        return host is null ? Dlss5Delivery.None : Dlss5Delivery.HoYoShadeOnly;
    }

    /// <summary>
    /// 第 2 条驱动检查的补充说明：用户要求「如果此游戏配置有项目使用全局，则查一下全局的」。
    /// 现在驱动的判断口径和每个游戏无关（都是同一台机器同一份驱动），
    /// 所以这里只在「这个游戏确实勾了使用全局配置」时给一句来源说明。
    /// </summary>
    public bool ForThisGameUsedGlobalDriverCheck(out string? globalBiz)
    {
        globalBiz = null;

        try
        {
            // 每个游戏可以「使用全局」的项：OptiScaler 构建 / 模块选择都是按游戏记的，
            // 没选过（null）= 跟着全局走  那驱动的判断口径也就是全局那份。
            if (GameId is not { } id)
            {
                return false;
            }

            bool usesGlobalModules = AppConfig.GetUsedModuleKeysOrNull(id) is null;
            bool usesGlobalOpti = string.IsNullOrWhiteSpace(AppConfig.GetSelectedOptiScalerId(id));

            if (usesGlobalModules || usesGlobalOpti)
            {
                globalBiz = GameDisplayName ?? "当前全局配置";
                return true;
            }
        }
        catch
        {
            // ignore：这只是一句说明，失败了不该让检测报错
        }

        return false;
    }

    #region 修复用的写盘操作

    /// <summary>补一份模板 ReShade.ini 到游戏目录</summary>
    public string? CopyTemplateIni()
    {
        try
        {
            if (ShadeHost is null || Game?.GameDirectory is not { } gameDir || !Directory.Exists(gameDir))
            {
                return "没有 HoYoShade 或游戏目录，补不了。";
            }

            string target = Path.Combine(gameDir, "ReShade.ini");
            if (File.Exists(target))
            {
                return "游戏目录里已经有 ReShade.ini 了。";
            }

            string template = ShadeHost.ReShadeIniPath;
            if (File.Exists(template))
            {
                File.Copy(template, target);
            }
            else
            {
                // 模板都没有就自己造一份最小可用的
                ReShadeProfile.CreateNew(target).Save();
            }

            // 顺手把绝对路径对到当前 HoYoShade（模板可能是别的启动器留下的）
            ShadePathAligner.Align(target, ShadeHost);
            return "已复制一份 ReShade.ini 到游戏目录，并把路径指到当前 HoYoShade。";
        }
        catch (Exception ex)
        {
            return "补 ReShade.ini 失败：" + ex.Message;
        }
    }

    /// <summary>把这份 ini 里的绝对路径指回当前 HoYoShade</summary>
    public string? AlignIniPaths()
    {
        try
        {
            if (ShadeHost is null || GameReShadeIniPath is not { } ini || !File.Exists(ini))
            {
                return "没有可对齐的 ReShade.ini。";
            }

            ShadePathAlignResult result = ShadePathAligner.Align(ini, ShadeHost);
            return result.Changed
                ? "已把 " + string.Join("、", result.ChangedKeys) + " 指回当前 HoYoShade。"
                : "没有需要改的键（路径本来就对得上）。";
        }
        catch (Exception ex)
        {
            return "对齐失败：" + ex.Message;
        }
    }

    /// <summary>改 hook 点（走 service，保证「两个键都写」那套逻辑）</summary>
    public string? SetHookPoint(int value)
    {
        try
        {
            if (PluginService is null)
            {
                return "不知道当前游戏，改不了 hook 点。";
            }

            return PluginService.SetHookPoint(value)
                ? $"Hook 模式已改成 {value}。"
                : "Hook 点写不进去  没有 ReShade.ini，或者没有满足前置条件的插件（RenoDX DLSS / super-anus）。";
        }
        catch (Exception ex)
        {
            return "改 Hook 模式失败：" + ex.Message;
        }
    }

    /// <summary>改 DirectNeuralRenderingPassCount</summary>
    public string? SetPassCount(int value)
    {
        try
        {
            if (Profile is null || GameReShadeIniPath is not { } ini)
            {
                return "没有 ReShade.ini，改不了。";
            }

            Profile.SetDirectNeuralRenderingPassCount(value);
            Profile.Save();
            return $"DirectNeuralRenderingPassCount 已改成 {value}（{ini}）。";
        }
        catch (Exception ex)
        {
            return "改 PassCount 失败：" + ex.Message;
        }
    }

    #endregion
}
