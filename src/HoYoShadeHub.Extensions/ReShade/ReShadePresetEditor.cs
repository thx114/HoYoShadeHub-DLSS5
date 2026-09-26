using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>
/// 往某个游戏**当前生效的 ReShade 预设**里加 / 去掉「DLSS5 Feed」这套效果。
///
/// <para>
/// 事实来源：DLSS5-Feeder 的手册（README 的 Install for a 64-bit game）——
/// 手动装要：下载 <c>DLSS5_Feed.fx</c> 进 <c>reshade-shaders\Shaders\</c>，
/// 装 <b>LumeniteFX</b> 当动作矢量来源，然后在 ReShade 叠加层里把
/// <c>LUMENITE: Kernel 2.0</c> 勾上、<c>DLSS 5 Feed</c> 排在它下面，
/// 并给 DLSS5_Feed.fx 设预处理器定义 <c>DLSS5_MV_PROVIDER=3</c>。
/// </para>
///
/// <para>
/// 效果开关只存在于**预设文件**里（<c>[GENERAL] PresetPath</c> 指的那份），不在 ReShade.ini。
/// 预设有可能是多个游戏共用的（本机都指向 <c>Presets\Mod OFF.ini</c>），所以这个类只做
/// <b>幂等</b>的增删：只碰我们那两个 technique 和那一条预处理器定义，别的一律不动。
/// 切游戏时由调用方按当前游戏重新同步一次即可。
/// </para>
/// </summary>
public static class ReShadePresetEditor
{
    /// <summary>DLSS5 Feed 那个 addon 的 slug 前缀</summary>
    public const string FeedAddonSlug = "dlss5-feed";

    /// <summary>动作矢量来源的预处理器定义名</summary>
    public const string MotionVectorProviderName = "DLSS5_MV_PROVIDER";

    /// <summary>3 = LumeniteFX Kernel（手册推荐的来源）</summary>
    public const string MotionVectorProviderValue = "3";

    /// <summary>要打开的两个 technique —— **顺序有意义**：provider 必须排在 Feed 前面</summary>
    public static readonly string[] FeedTechniques =
    [
        "Lumenite_Kernel@lumenite_Kernel.fx",
        "DLSS5_Feed@DLSS5_Feed.fx",
    ];

    /// <summary>这两个 technique 所在的 .fx 文件名（删除时按文件认，不认 technique 名）</summary>
    public static readonly string[] FeedEffectFiles =
    [
        "lumenite_Kernel.fx",
        "DLSS5_Feed.fx",
    ];

    /// <summary>这个 addon 文件名是不是 DLSS5 Feed（<c>dlss5-feed.addon64*</c>）</summary>
    public static bool IsFeedAddon(string? addonFileName)
    {
        if (string.IsNullOrWhiteSpace(addonFileName))
        {
            return false;
        }

        string slug = AddonFileInfo.Parse(addonFileName)?.Slug
                      ?? Path.GetFileNameWithoutExtension(addonFileName);

        return slug is not null && slug.StartsWith(FeedAddonSlug, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 按这个游戏当前生效的预设开 / 关那两个效果。
    /// </summary>
    /// <param name="error">失败原因（成功时为 null）</param>
    /// <returns>成功写入（或本来就已经是目标状态、不需要写）返回 true</returns>
    public static bool TrySetEnabled(ReShadeProfile profile, bool enabled, out string? error)
    {
        error = null;

        try
        {
            string? presetPath = profile.ResolvePresetPath();
            if (string.IsNullOrWhiteSpace(presetPath))
            {
                error = "这个游戏的 ReShade.ini 里没写 PresetPath —— 先在 ReShade 叠加层里存一个预设。";
                return false;
            }

            if (!File.Exists(presetPath))
            {
                error = "预设文件不存在：" + presetPath;
                return false;
            }

            IniDocument preset = IniDocument.Load(presetPath);
            bool changed = enabled ? Enable(preset) : Disable(preset);

            if (changed)
            {
                preset.Save(presetPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>这个游戏的预设里现在有没有我们那两个效果（给界面显示用）</summary>
    public static bool IsEnabled(ReShadeProfile profile)
    {
        try
        {
            string? presetPath = profile.ResolvePresetPath();
            if (string.IsNullOrWhiteSpace(presetPath) || !File.Exists(presetPath))
            {
                return false;
            }

            List<string> techniques = SplitList(IniDocument.Load(presetPath).GetValue(IniDocument.RootSection, "Techniques"));
            return FeedTechniques.All(t => techniques.Any(e => string.Equals(e.Trim(), t, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    private static bool Enable(IniDocument preset)
    {
        bool changed = false;

        List<string> techniques = SplitList(preset.GetValue(IniDocument.RootSection, "Techniques"));
        foreach (string technique in FeedTechniques)
        {
            if (!techniques.Any(e => string.Equals(e.Trim(), technique, StringComparison.OrdinalIgnoreCase)))
            {
                techniques.Add(technique);
                changed = true;
            }
        }

        if (changed)
        {
            preset.SetValue(IniDocument.RootSection, "Techniques", string.Join(',', techniques));
        }

        // TechniqueSorting 是「所有 technique 的排序全集」，没有就补在末尾，
        // 保证 Kernel 排在 Feed 前面（ReShade 先按这张表排，再补表里没有的）。
        List<string> sorting = SplitList(preset.GetValue(IniDocument.RootSection, "TechniqueSorting"));
        bool sortingChanged = false;
        foreach (string technique in FeedTechniques)
        {
            if (!sorting.Any(e => string.Equals(e.Trim(), technique, StringComparison.OrdinalIgnoreCase)))
            {
                sorting.Add(technique);
                sortingChanged = true;
            }
        }

        if (sortingChanged && sorting.Count > 0)
        {
            preset.SetValue(IniDocument.RootSection, "TechniqueSorting", string.Join(',', sorting));
            changed = true;
        }

        return EnsureProviderDefinition(preset) || changed;
    }

    private static bool Disable(IniDocument preset)
    {
        bool changed = false;

        string? rawTechniques = preset.GetValue(IniDocument.RootSection, "Techniques");
        if (rawTechniques is not null)
        {
            List<string> all = SplitList(rawTechniques);
            List<string> kept = [.. all.Where(e => !IsOurTechnique(e))];
            if (kept.Count != all.Count)
            {
                preset.SetValue(IniDocument.RootSection, "Techniques", string.Join(',', kept));
                changed = true;
            }
        }

        // TechniqueSorting 故意不动：它是全集清单，留着不表示这个效果是开着的。
        return RemoveProviderDefinition(preset) || changed;
    }

    private static bool EnsureProviderDefinition(IniDocument preset)
    {
        string? raw = preset.GetValue(IniDocument.RootSection, "PreprocessorDefinitions");

        // 用户自己写过（哪怕不是 3）就尊重他，不覆盖
        if (SplitList(raw).Any(t => string.Equals(KeyOf(t), MotionVectorProviderName, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        string value = string.IsNullOrWhiteSpace(raw)
            ? MotionVectorProviderName + "=" + MotionVectorProviderValue
            : raw.TrimEnd() + "," + MotionVectorProviderName + "=" + MotionVectorProviderValue;

        preset.SetValue(IniDocument.RootSection, "PreprocessorDefinitions", value);
        return true;
    }

    private static bool RemoveProviderDefinition(IniDocument preset)
    {
        string? raw = preset.GetValue(IniDocument.RootSection, "PreprocessorDefinitions");
        if (raw is null)
        {
            return false;
        }

        // 按逗号切开再拼回去：其它 token 一个字符都不会变（含引号里的逗号）
        List<string> all = [.. raw.Split(',')];
        List<string> kept = [.. all.Where(t => !IsOurProviderDefinition(t))];

        if (kept.Count == all.Count)
        {
            return false;
        }

        preset.SetValue(IniDocument.RootSection, "PreprocessorDefinitions", string.Join(',', kept));
        return true;
    }

    private static bool IsOurProviderDefinition(string token)
    {
        string trimmed = token.Trim();
        int separator = trimmed.IndexOf('=');
        if (separator <= 0
            || !string.Equals(trimmed[..separator].Trim(), MotionVectorProviderName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return string.Equals(trimmed[(separator + 1)..].Trim(), MotionVectorProviderValue, StringComparison.Ordinal);
    }

    private static string KeyOf(string token)
    {
        string trimmed = token.Trim();
        int separator = trimmed.IndexOf('=');
        return separator <= 0 ? trimmed : trimmed[..separator].Trim();
    }

    /// <summary>效果项（Tech@Effect.fx）里的效果文件是不是我们要管的那两个</summary>
    private static bool IsOurTechnique(string entry)
    {
        string trimmed = entry.Trim();
        int at = trimmed.LastIndexOf('@');
        string file = at >= 0 ? trimmed[(at + 1)..].Trim() : trimmed;

        return FeedEffectFiles.Any(f => string.Equals(f, file, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>逗号分隔列表；空 / null 给空列表</summary>
    private static List<string> SplitList(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : [.. raw.Split(',', StringSplitOptions.TrimEntries).Where(s => s.Length > 0)];
}
