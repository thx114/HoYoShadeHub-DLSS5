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
/// <item>普通路线维持第二 runtime 的管理态同步；显式原神最终 DX12 路线独立保存两份 runtime 状态。</item>
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

    public static bool IsGenshinFinalDx12Route(string? gameBiz, bool nativeDx12, bool modulesEnabled, bool bridgeScheduled) =>
        gameBiz?.StartsWith("hk4e_", StringComparison.OrdinalIgnoreCase) == true
        && !nativeDx12 && modulesEnabled && bridgeScheduled;

    public const string DisabledDx11PresetFileName = "HoYoShade DX11 Disabled.ini";
    private const string SharedPresetKey = "HoYoShadeSharedPresetPath";
    private static readonly string[] ManagedGeneralKeys = ["EffectSearchPaths", "TextureSearchPaths", "IntermediateCachePath"];

    /// <summary>深度方向定义：两个 runtime 各留各的，任何"回灌 / 恢复"都不能覆盖它</summary>
    private const string PreprocessorDefinitionsKey = "PreprocessorDefinitions";
    private static readonly string[] PrimaryHotkeys = ["KeyOverlay", "KeyEffects", "KeyReload", "KeyNextPreset", "KeyPreviousPreset", "KeyScreenshot", "KeyFPS", "KeyFrameTime", "KeyPerformanceMode"];
    private static readonly string[] PrimaryHudKeys = ["ShowClock", "ShowFPS", "ShowFrameTime", "ShowPresetName", "ShowScreenshotMessage", "ShowPresetTransitionMessage", "ShowForceLoadEffectsButton"];

    // 路线由调用方根据实际启动选项/Bridge 注入列表明确传入，绝不靠 ReShade2.ini 的存在推断。
    public static GameIniBootstrapResult Ensure(GameEntry entry, ShadeHost? host) => Ensure(entry, host, genshinFinalDx12: false);

    public static GameIniBootstrapResult Ensure(GameEntry entry, ShadeHost? host, bool genshinFinalDx12, bool starRailFinalDx12 = false)
    {
        var result = new GameIniBootstrapResult();
        string? gameBiz = entry.Biz?.Value ?? (entry.Id.StartsWith(GameEntry.BizIdPrefix, StringComparison.OrdinalIgnoreCase) ? entry.Id[GameEntry.BizIdPrefix.Length..] : null);
        bool isGenshin = gameBiz?.StartsWith("hk4e_", StringComparison.OrdinalIgnoreCase) == true;
        genshinFinalDx12 &= isGenshin;
        bool isStarRail = gameBiz?.StartsWith("hkrpg_", StringComparison.OrdinalIgnoreCase) == true;
        starRailFinalDx12 &= isStarRail;

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

            IniDocument initial = IniDocument.Load(ini);
            bool independentFinalDx12 = genshinFinalDx12
                || (starRailFinalDx12 && IsSingleEffectsMode(LoadCoexistConfig(initial, ini)));

            // 切回普通路线时清理单套控制状态；恢复空预设只适用于原神原有策略。
            if ((isGenshin || isStarRail) && !independentFinalDx12)
            {
                string secondaryPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ini))!, SecondaryIniFileName);
                IniDocument primary = IniDocument.Load(ini);
                IniDocument? final = File.Exists(secondaryPath) ? IniDocument.Load(secondaryPath) : null;
                bool clearedSingleMode = ClearSingleEffectsMode(primary, final);
                if (clearedSingleMode)
                {
                    primary.Save(ini);
                    result.PrimaryRuntimeConfigured = true;
                }
                if (final is not null && (ClearSingleEffectsKeys(final) | clearedSingleMode))
                {
                    final.Save(secondaryPath);
                    result.SyncedSecondary = true;
                }
                if (isGenshin) RestorePrimaryRuntime(ini, result);
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

            // Align stale host paths first. Preserve that corrected selection across
            // pack defaults, rather than restoring the stale pre-alignment path.
            AlignIfPointingElsewhere(ini, host, result);
            IniDocument beforePack = IniDocument.Load(ini);
            bool preserveSharedPreset = independentFinalDx12 && IsSingleEffectsMode(LoadCoexistConfig(beforePack, ini));
            ApplyPackUserContent(ini, result);
            IniDocument afterManaged = IniDocument.Load(ini);
            bool managedChanged = false;
            // Pack defaults must not discard explicit per-game plugin choices.
            // Preserve the whole list, including an explicit empty (all-on) list.
            foreach (string key in new[] { "DisabledAddons", "LoadFromDllMain" })
                if (beforePack.ContainsKey("ADDON", key))
                    managedChanged |= Set(afterManaged, "ADDON", key, beforePack.GetValue("ADDON", key));
            managedChanged |= Set(afterManaged, "GENERAL", "HoYoShade_BypassEffectCheck", "1");
            managedChanged |= Set(afterManaged, "GENERAL", "HoYoShade_BypassDepthCheck", "1");
            if (preserveSharedPreset)
            {
                managedChanged |= Set(afterManaged, "GENERAL", "PresetPath", beforePack.GetValue("GENERAL", "PresetPath"));
                managedChanged |= Set(afterManaged, "GENERAL", SharedPresetKey, beforePack.GetValue("GENERAL", SharedPresetKey));
            }
            if (managedChanged)
            {
                afterManaged.Save(ini);
                result.PrimaryRuntimeConfigured = true;
            }

            // Pack overrides can reintroduce template-relative shader paths.
            // Normalize those after applying pack content, before syncing final paths.
            AlignIfPointingElsewhere(ini, host, result);

            // Plugin enablement is user-owned. Never rename .addon64x or
            // re-enable a dependency merely because its policy INI exists.

            // 普通路线保留既有管理态镜像；最终 DX12 路线只同步管理路径/插件状态，
            // 最终 runtime 的预设、INPUT 和效果配置独立持久化。
            string secondary = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(ini))!, SecondaryIniFileName);
            bool secondaryExisted = File.Exists(secondary);
            if (independentFinalDx12 ? EnsureFinalDx12(ini, secondary, host, result) : SyncSecondaryIni(ini, secondary))
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

    private static bool IsDisabledPreset(string? path)
    {
        string name = Path.GetFileName((path ?? string.Empty).Replace('\\', '/'));
        return string.Equals(name, DisabledDx11PresetFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "DX11 Disabled.ini", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Set(IniDocument ini, string section, string key, string? value)
    {
        if (value is null)
        {
            if (!ini.ContainsKey(section, key)) return false;
            ini.RemoveKey(section, key);
            return true;
        }
        if (string.Equals(ini.GetValue(section, key), value, StringComparison.Ordinal)) return false;
        ini.SetValue(section, key, value);
        return true;
    }

    // 只用于初始化或被禁用主 ini 污染的迁移；正常的最终 runtime 不读模板状态。
    private static IniDocument FinalDefaults(IniDocument main, ShadeHost? host)
    {
        if (!IsDisabledPreset(main.GetValue("GENERAL", "PresetPath"))) return main;
        IniDocument seed = host is not null && File.Exists(host.ReShadeIniPath)
            ? IniDocument.Load(host.ReShadeIniPath) : IniDocument.CreateEmpty();
        if (IsDisabledPreset(seed.GetValue("GENERAL", "PresetPath"))
            || string.IsNullOrWhiteSpace(seed.GetValue("GENERAL", "PresetPath")))
            seed.SetValue("GENERAL", "PresetPath", ".\\ReShadePreset.ini");
        seed.SetValue("GENERAL", "NoReloadOnInit", "0");
        seed.SetValue("GENERAL", "SkipLoadingDisabledEffects", "0");
        if (string.IsNullOrWhiteSpace(seed.GetValue("INPUT", "KeyOverlay"))
            || seed.GetValue("INPUT", "KeyOverlay")!.Split(',')[0].Trim() == "0")
            seed.SetValue("INPUT", "KeyOverlay", "36,0,0,0");
        return seed;
    }

    private static bool EnsureFinalDx12(string primaryPath, string secondaryPath, ShadeHost? host, GameIniBootstrapResult result)
    {
        IniDocument main = IniDocument.Load(primaryPath);
        bool existed = File.Exists(secondaryPath);
        IniDocument final = existed ? IniDocument.Load(secondaryPath) : FinalDefaults(main, host);
        // 首次必须先写正常最终配置，再禁用 primary，两个文档不能共享实例。
        if (!existed)
        {
            final.Save(secondaryPath);
            final = IniDocument.Load(secondaryPath);
        }
        bool changed = !existed;
        if (existed && IsDisabledPreset(final.GetValue("GENERAL", "PresetPath")))
        {
            IniDocument seed = FinalDefaults(main, host);
            foreach (string key in new[] { "PresetPath", "NoReloadOnInit", "SkipLoadingDisabledEffects" })
                changed |= Set(final, "GENERAL", key, seed.GetValue("GENERAL", key));
            foreach (string key in final.GetSectionKeys("INPUT").ToList())
                changed |= Set(final, "INPUT", key, null);
            foreach (string key in seed.GetSectionKeys("INPUT"))
                changed |= Set(final, "INPUT", key, seed.GetValue("INPUT", key));
            foreach (string key in PrimaryHudKeys)
                changed |= Set(final, "OVERLAY", key, seed.GetValue("OVERLAY", key));
        }
        foreach (string key in ManagedGeneralKeys)
            changed |= Set(final, "GENERAL", key, main.GetValue("GENERAL", key));
        // 插件管理态同步，但最终 runtime 不重复加载 Present/NR 插件。
        // 主 runtime 的 addon 全局事件已经为最终 DX12 runtime 提供深度。
        foreach (string key in final.GetSectionKeys("ADDON").Union(main.GetSectionKeys("ADDON"), StringComparer.OrdinalIgnoreCase).ToList())
            changed |= Set(final, "ADDON", key, string.Equals(key, "AddonPath", StringComparison.OrdinalIgnoreCase) ? string.Empty : main.GetValue("ADDON", key));
        if (!string.IsNullOrEmpty(final.GetValue("ADDON", "AddonPath")))
            changed |= Set(final, "ADDON", "AddonPath", string.Empty);
        changed |= Set(final, "OVERLAY", "TutorialProgress", TutorialDoneValue);
        // EffectsOwner selects the single-executor policy. Without it the
        // existing FinalRuntimeOnly opt-in keeps independent runtime presets.
        IniDocument? addon = LoadCoexistConfig(main, primaryPath);
        bool allowDx11 = addon?.GetValue("General", "FinalRuntimeOnly")?.Trim() == "0";
        bool singleEffects = IsSingleEffectsMode(addon);
        if (singleEffects)
        {
            bool sourceChanged = ConfigureSingleEffects(main, final, primaryPath, ref changed);
            if (changed) final.Save(secondaryPath);
            if (sourceChanged) main.Save(primaryPath);
            result.PrimaryRuntimeConfigured |= sourceChanged;
            return changed;
        }
        bool modeCleared = ClearSingleEffectsMode(main, final);
        changed |= modeCleared;
        changed |= ClearSingleEffectsKeys(final);
        if (changed) final.Save(secondaryPath);
        if (modeCleared)
        {
            main.Save(primaryPath);
            result.PrimaryRuntimeConfigured = true;
        }
        if (allowDx11)
        {
            bool coexistChanged = false;
            if (IsDisabledPreset(main.GetValue("GENERAL", "PresetPath")))
            {
                string preNrPreset = Path.Combine(Path.GetDirectoryName(primaryPath)!, "HoYoShade DX11 Before NR.ini");
                if (!File.Exists(preNrPreset)) File.WriteAllText(preNrPreset, "Techniques=\nTechniqueSorting=\n");
                coexistChanged |= Set(main, "GENERAL", "PresetPath", preNrPreset);
                coexistChanged |= Set(main, "INPUT", "KeyOverlay", "121,0,0,0"); // F10
                coexistChanged |= Set(main, "INPUT", "KeyEffects", "120,0,0,0"); // F9
                coexistChanged |= Set(main, "INPUT", "InputProcessing", "2");
            }
            coexistChanged |= Set(main, "GENERAL", "NoReloadOnInit", "0");
            coexistChanged |= Set(main, "GENERAL", "SkipLoadingDisabledEffects", "0");
            if (coexistChanged) main.Save(primaryPath);
            result.PrimaryRuntimeConfigured |= coexistChanged;
            return changed;
        }

        string emptyPreset = Path.Combine(Path.GetDirectoryName(primaryPath)!, DisabledDx11PresetFileName);
        const string emptyContent = "Techniques=\nTechniqueSorting=\n";
        if (!File.Exists(emptyPreset) || File.ReadAllText(emptyPreset).Replace("\r\n", "\n") != emptyContent)
        {
            File.WriteAllText(emptyPreset, emptyContent);
            result.PrimaryRuntimeConfigured = true;
        }
        bool primaryChanged = Set(main, "GENERAL", "PresetPath", emptyPreset);
        primaryChanged |= Set(main, "GENERAL", "NoReloadOnInit", "1");
        primaryChanged |= Set(main, "GENERAL", "SkipLoadingDisabledEffects", "1");
        foreach (string key in main.GetSectionKeys("INPUT").Where(k => k.StartsWith("Key", StringComparison.OrdinalIgnoreCase)).Union(PrimaryHotkeys).ToList())
            primaryChanged |= Set(main, "INPUT", key, "0,0,0,0");
        foreach (string key in PrimaryHudKeys)
            primaryChanged |= Set(main, "OVERLAY", key, "0");
        if (primaryChanged) main.Save(primaryPath);
        result.PrimaryRuntimeConfigured |= primaryChanged;
        return changed;
    }

    private static IniDocument? LoadCoexistConfig(IniDocument source, string primaryPath)
    {
        string? addonPath = source.GetValue("ADDON", "AddonPath");
        if (string.IsNullOrWhiteSpace(addonPath)) return null;
        string directory = Path.GetFullPath(addonPath.Trim(), Path.GetDirectoryName(Path.GetFullPath(primaryPath))!);
        string config = Path.Combine(directory, "FsrBridgeDepthAddon.ini");
        return File.Exists(config) ? IniDocument.Load(config) : null;
    }

    private static bool IsSingleEffectsMode(IniDocument? addon) =>
        addon?.GetValue("General", "FinalRuntimeOnly")?.Trim() == "0"
        && string.Equals(addon.GetValue("General", "EffectsOwner")?.Trim(), "DX11PreNR", StringComparison.OrdinalIgnoreCase);

    // The last shared path is also the one-time migration marker. It lets either
    // runtime change the selection; source wins if both were changed between launches.
    private static bool ConfigureSingleEffects(IniDocument source, IniDocument final, string primaryPath, ref bool finalChanged)
    {
        string? last = source.GetValue("GENERAL", SharedPresetKey);
        string? sourcePreset = source.GetValue("GENERAL", "PresetPath");
        string? finalPreset = final.GetValue("GENERAL", "PresetPath");
        bool IsExistingUserPreset(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || IsDisabledPreset(path)) return false;
            string resolved = Path.GetFullPath(path, Path.GetDirectoryName(Path.GetFullPath(primaryPath))!);
            if (!File.Exists(resolved)) return false;
            IniDocument preset = IniDocument.Load(resolved);
            // Turning every effect off does not make a user preset an empty
            // placeholder: ordering and shader parameters remain user state.
            return !string.IsNullOrWhiteSpace(preset.GetValue(IniDocument.RootSection, "Techniques"))
                || !string.IsNullOrWhiteSpace(preset.GetValue(IniDocument.RootSection, "TechniqueSorting"))
                || preset.GetSections().Any(section =>
                    (section.EndsWith(".fx", StringComparison.OrdinalIgnoreCase)
                        || section.EndsWith(".addonfx", StringComparison.OrdinalIgnoreCase))
                    && preset.GetSectionKeys(section).Any());
        }
        // 迁移标记还在、但预设文件已经被删/改名：这种失效路径只在**两端都没有真实可用的预设**
        // 时才继续沿用；否则（source 先判）它会每次启动都把另一头新选的有效预设覆盖回失效路径，
        // 用户在 final 里怎么改都换不回来。
        bool RetainsMissingPreset(string? path) =>
            !string.IsNullOrWhiteSpace(last)
            && !string.IsNullOrWhiteSpace(path)
            && !IsDisabledPreset(path);
        string? selected = IsExistingUserPreset(sourcePreset) ? sourcePreset
            : IsExistingUserPreset(finalPreset) ? finalPreset
            : RetainsMissingPreset(sourcePreset) ? sourcePreset
            : RetainsMissingPreset(finalPreset) ? finalPreset
            : null;
        if (!string.IsNullOrWhiteSpace(last) && string.Equals(sourcePreset, last, StringComparison.Ordinal)
            && IsExistingUserPreset(finalPreset)) selected = finalPreset;
        bool changed = false;
        // Do not remove the controller (or NR) from DisabledAddons. The
        // user can intentionally turn off those functions for a latency test.
        if (selected is not null)
        {
            changed |= Set(source, "GENERAL", "PresetPath", selected);
            changed |= Set(source, "GENERAL", SharedPresetKey, selected);
            finalChanged |= Set(final, "GENERAL", "PresetPath", selected);
        }
        changed |= Set(source, "GENERAL", "HoYoShadeEffectsStage", "1");
        changed |= Set(source, "GENERAL", "NoReloadOnInit", "0");
        changed |= Set(source, "GENERAL", "SkipLoadingDisabledEffects", "0");
        changed |= Set(source, "INPUT", "KeyOverlay", "0,0,0,0");
        foreach (string key in PrimaryHudKeys)
            changed |= Set(source, "OVERLAY", key, "0");
        changed |= Set(source, "OVERLAY", "HoYoShadeHomeOverlay", null);
        finalChanged |= Set(final, "GENERAL", "HoYoShadeEffectsStage", "2");
        finalChanged |= Set(final, "GENERAL", "NoReloadOnInit", "1");
        finalChanged |= Set(final, "GENERAL", SharedPresetKey, null);
        finalChanged |= Set(final, "OVERLAY", "HoYoShadeHomeOverlay", "NR Input Effects");
        return changed;
    }

    private static bool ClearSingleEffectsKeys(IniDocument ini)
    {
        bool changed = Set(ini, "GENERAL", "HoYoShadeEffectsStage", null);
        changed |= Set(ini, "GENERAL", SharedPresetKey, null);
        changed |= Set(ini, "OVERLAY", "HoYoShadeHomeOverlay", null);
        return changed;
    }

    private static bool ClearSingleEffectsMode(IniDocument source, IniDocument? final)
    {
        bool wasSingle = source.ContainsKey("GENERAL", "HoYoShadeEffectsStage")
            || source.ContainsKey("GENERAL", SharedPresetKey);
        bool changed = ClearSingleEffectsKeys(source);
        if (wasSingle && final is not null)
        {
            final.SetValue("GENERAL", "NoReloadOnInit", "0");
            foreach (string key in final.GetSectionKeys("INPUT").Union(source.GetSectionKeys("INPUT"), StringComparer.OrdinalIgnoreCase).ToList())
                changed |= Set(source, "INPUT", key, final.GetValue("INPUT", key));
            foreach (string key in PrimaryHudKeys)
                changed |= Set(source, "OVERLAY", key, final.GetValue("OVERLAY", key));
        }
        return changed;
    }

    private static void RestorePrimaryRuntime(string primaryPath, GameIniBootstrapResult result)
    {
        IniDocument primary = IniDocument.Load(primaryPath);
        string? preset = primary.GetValue("GENERAL", "PresetPath");
        if (string.IsNullOrWhiteSpace(preset)) return;
        string primaryDirectory = Path.GetDirectoryName(Path.GetFullPath(primaryPath))!;
        string ownedPreset = Path.Combine(primaryDirectory, DisabledDx11PresetFileName);
        string resolvedPreset = Path.GetFullPath(preset, primaryDirectory);
        if (!string.Equals(resolvedPreset, ownedPreset, StringComparison.OrdinalIgnoreCase)) return;
        string secondary = Path.Combine(Path.GetDirectoryName(primaryPath)!, SecondaryIniFileName);
        if (!File.Exists(secondary)) return;
        IniDocument final = IniDocument.Load(secondary);
        if (IsDisabledPreset(final.GetValue("GENERAL", "PresetPath"))) return;
        // 各 runtime 的深度方向定义是它自己的使用状态（UpsideDown 之类）：从 final 回灌会把
        // source 的深度上下翻过来，所以这里也排除掉，别只看 ManagedGeneralKeys。
        foreach (string key in primary.GetSectionKeys("GENERAL").Union(final.GetSectionKeys("GENERAL"), StringComparer.OrdinalIgnoreCase).ToList())
            if (!ManagedGeneralKeys.Contains(key, StringComparer.OrdinalIgnoreCase)
                && !string.Equals(key, PreprocessorDefinitionsKey, StringComparison.OrdinalIgnoreCase))
                Set(primary, "GENERAL", key, final.GetValue("GENERAL", key));
        foreach (string section in SecondaryOwnedSections)
            foreach (string key in primary.GetSectionKeys(section).Union(final.GetSectionKeys(section), StringComparer.OrdinalIgnoreCase).ToList())
                Set(primary, section, key, final.GetValue(section, key));
        primary.Save(primaryPath);
        result.PrimaryRuntimeConfigured = true;
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
                // This runtime has no addons; don't first copy a path that would
                // be cleared below on every launch and defeat idempotence.
                if (string.Equals(section, "ADDON", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(key, "AddonPath", StringComparison.OrdinalIgnoreCase))
                    continue;
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
        foreach (string section in secondary.GetSections().ToList())
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

    /// <summary>Check every managed path independently. A valid AddonPath does
    /// not imply the shader/texture paths are valid; the aligner preserves pack directories.</summary>
    private static void AlignIfPointingElsewhere(string ini, ShadeHost? host, GameIniBootstrapResult result)
    {
        if (host is null) return;
        ShadePathAlignResult align = ShadePathAligner.Align(ini, host);
        if (align.Changed)
        {
            result.Aligned = true;
            result.AlignKeys = result.AlignKeys.Concat(align.ChangedKeys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

    public bool PrimaryRuntimeConfigured { get; internal set; }

    public bool ChangedAnything => CreatedFromTemplate || TutorialMarkedDone || CreatedSecondary || SyncedSecondary || Aligned || PrimaryRuntimeConfigured;
}
