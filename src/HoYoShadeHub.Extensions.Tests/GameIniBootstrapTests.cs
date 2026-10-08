using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class GameIniBootstrapTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "Hoyo-bootstrap-" + Guid.NewGuid().ToString("N"));
        int failed = 0;
        int count = 0;
        void Check(bool ok, string name)
        {
            count++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok) failed++;
        }
        Directory.CreateDirectory(root);
        try
        {
            string hostRoot = Path.Combine(root, "host");
            Directory.CreateDirectory(hostRoot);
            Directory.CreateDirectory(Path.Combine(hostRoot, "reshade-shaders", "Addons"));
            string template = Path.Combine(hostRoot, "ReShade.ini");
            File.WriteAllText(template, "[GENERAL]\nPresetPath=Default.ini\nNoReloadOnInit=0\nSkipLoadingDisabledEffects=0\nEffectSearchPaths=Shaders\nTextureSearchPaths=Textures\n[INPUT]\nKeyOverlay=36,0,0,0\nKeyEffects=145,0,0,0\n[OVERLAY]\nShowFPS=1\nTutorialProgress=4\n[ADDON]\nAddonPath=" + Path.Combine(hostRoot, "reshade-shaders", "Addons") + "\n");
            ShadeHost host = new(hostRoot, ShadeHostKind.HoYoShade);
            GameEntry Entry(string name)
            {
                string dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                return new GameEntry("biz:hk4e_cn", name) { ExePath = Path.Combine(dir, "YuanShen.exe") };
            }
            IniDocument Load(GameEntry entry) => IniDocument.Load(entry.ReShadeIniPath!);
            string Secondary(GameEntry entry) => Path.Combine(entry.GameDirectory!, GameIniBootstrap.SecondaryIniFileName);
            Check(GameIniBootstrap.IsGenshinFinalDx12Route("hk4e_cn", false, true, true), "原神 DX11 + 实际 Bridge 启用最终路线");
            Check(!GameIniBootstrap.IsGenshinFinalDx12Route("hkrpg_cn", false, true, true), "其他游戏不启用最终路线");
            Check(!GameIniBootstrap.IsGenshinFinalDx12Route("hk4e_cn", true, true, true), "原神本体 DX12 不启用最终路线");
            Check(!GameIniBootstrap.IsGenshinFinalDx12Route("hk4e_cn", false, false, true), "模块关闭不启用最终路线");
            Check(!GameIniBootstrap.IsGenshinFinalDx12Route("hk4e_cn", false, true, false), "未调度 Bridge 不启用最终路线");

            GameEntry protectedIni = Entry("vendor-config-protection");
            GameIniBootstrap.Ensure(protectedIni, host);
            Check(Load(protectedIni).GetValue("GENERAL", "HoYoShade_BypassEffectCheck") == "1"
                && Load(protectedIni).GetValue("GENERAL", "HoYoShade_BypassDepthCheck") == "1",
                "原版注入器保留 Hub 管理的插件/预设路径和每游戏深度宏");
            Check(!GameIniBootstrap.Ensure(protectedIni, host).ChangedAnything, "原版注入器配置保护重复启动幂等");

            // Rocket reproduction: correct AddonPath but copied relative shader paths.
            GameEntry relativeShaders = Entry("relative-shaders-valid-addon");
            GameIniBootstrap.Ensure(relativeShaders, host);
            var relativeShaderSource = Load(relativeShaders);
            relativeShaderSource.SetValue("GENERAL", "EffectSearchPaths", @".\reshade-shaders\Shaders\**");
            relativeShaderSource.SetValue("GENERAL", "TextureSearchPaths", @".\reshade-shaders\Textures\**");
            relativeShaderSource.SetValue("GENERAL", "PresetPath", "UserKeep.ini");
            relativeShaderSource.SetValue("GENERAL", "PreprocessorDefinitions", "SOURCE_CUSTOM=1");
            relativeShaderSource.Save(relativeShaders.ReShadeIniPath!);
            string expectedShaders=Path.Combine(hostRoot,"reshade-shaders","Shaders")+@"\**";
            string expectedTextures=Path.Combine(hostRoot,"reshade-shaders","Textures")+@"\**";
            var relativeShaderFix=GameIniBootstrap.Ensure(relativeShaders, host);
            Check(!relativeShaderFix.Failed && relativeShaderFix.Aligned
                && Load(relativeShaders).GetValue("GENERAL", "EffectSearchPaths")==expectedShaders,
                "正确 AddonPath 不再阻止校正失效的相对滤镜目录");
            Check(Load(relativeShaders).GetValue("GENERAL", "TextureSearchPaths")==expectedTextures,
                "相对纹理目录指回所选 HoYoShade 宿主");
            Check(IniDocument.Load(Secondary(relativeShaders)).GetValue("GENERAL", "EffectSearchPaths")==expectedShaders
                && IniDocument.Load(Secondary(relativeShaders)).GetValue("GENERAL", "TextureSearchPaths")==expectedTextures,
                "最终 runtime 同步正确滤镜和纹理搜索路径");
            Check(Load(relativeShaders).GetValue("GENERAL", "PresetPath")=="UserKeep.ini"
                && Load(relativeShaders).GetValue("GENERAL", "PreprocessorDefinitions")=="SOURCE_CUSTOM=1",
                "路径修复不更换用户预设或深度宏");
            Check(!GameIniBootstrap.Ensure(relativeShaders, host).ChangedAnything,
                "相对滤镜目录修复重复启动幂等");
            string relativePackRoot=Path.Combine(root,"relative-path-pack");
            string relativePackAddons=Path.Combine(relativePackRoot,"Addons");Directory.CreateDirectory(relativePackAddons);
            File.WriteAllText(Path.Combine(relativePackAddons,GameAddonPack.MarkerFileName),"{}");
            File.WriteAllText(Path.Combine(relativePackRoot,"ini_config.json"), """
                { "set": { "GENERAL": { "EffectSearchPaths": ".\\reshade-shaders\\Shaders\\**", "TextureSearchPaths": ".\\reshade-shaders\\Textures\\**" } } }
                """);
            relativeShaderSource=Load(relativeShaders);relativeShaderSource.SetValue("ADDON","AddonPath",relativePackAddons);
            relativeShaderSource.Save(relativeShaders.ReShadeIniPath!);
            var packRelativeFix=GameIniBootstrap.Ensure(relativeShaders, host);
            Check(packRelativeFix.AppliedPackIniConfig && !packRelativeFix.Failed
                && Load(relativeShaders).GetValue("GENERAL","EffectSearchPaths")==expectedShaders,
                "插件包重新写入相对路径后仍最终归一化滤镜路径");
            Check(Load(relativeShaders).GetValue("ADDON","AddonPath")==relativePackAddons,
                "修复滤镜目录保留每游戏插件包路径");
            Check(IniDocument.Load(Secondary(relativeShaders)).GetValue("GENERAL","TextureSearchPaths")==expectedTextures,
                "插件包覆写后最终 runtime 的纹理路径也正确");
            var customShaderSource=Load(relativeShaders);
            customShaderSource.SetValue("GENERAL","EffectSearchPaths",Path.Combine(root,"CustomShaders"));
            customShaderSource.Save(relativeShaders.ReShadeIniPath!);
            // Remove the test pack override: no longer applies managed defaults.
            File.Delete(Path.Combine(relativePackRoot,"ini_config.json"));
            GameIniBootstrap.Ensure(relativeShaders, host);
            Check(Load(relativeShaders).GetValue("GENERAL","EffectSearchPaths")==Path.Combine(root,"CustomShaders"),
                "不带宿主特征的用户自定义滤镜目录不被擅自替换");

            GameEntry fresh = Entry("fresh");
            var first = GameIniBootstrap.Ensure(fresh, host, true);
            IniDocument primary = Load(fresh);
            IniDocument final = IniDocument.Load(Secondary(fresh));
            Check(!first.Failed && first.CreatedFromTemplate && first.CreatedSecondary, "首次创建两份配置");
            Check(final.GetValue("GENERAL", "PresetPath") == "Default.ini" && final.GetValue("INPUT", "KeyOverlay") == "36,0,0,0", "首次最终 runtime 保留正常预设和菜单默认值");
            Check(final.GetValue("GENERAL", "NoReloadOnInit") == "0" && final.GetValue("OVERLAY", "ShowFPS") == "1", "首次最终 runtime 保留加载和 HUD 默认值");
            Check(primary.GetValue("GENERAL", "NoReloadOnInit") == "1" && primary.GetValue("GENERAL", "SkipLoadingDisabledEffects") == "1", "主 DX11 禁止初始加载及禁用效果加载");
            Check(primary.GetValue("INPUT", "KeyOverlay") == "0,0,0,0" && primary.GetValue("INPUT", "KeyEffects") == "0,0,0,0" && primary.GetValue("OVERLAY", "ShowFPS") == "0", "主 DX11 关闭菜单、效果快捷键和 HUD");
            Check(File.ReadAllText(primary.GetValue("GENERAL", "PresetPath")!).Contains("Techniques=\n"), "主 DX11 使用独立空预设");
            Check(string.IsNullOrEmpty(final.GetValue("ADDON", "AddonPath")), "最终 runtime AddonPath 保持为空，避免重复 NR 加载");

            final.SetValue("GENERAL", "PresetPath", "UserFinal.ini");
            final.SetValue("GENERAL", "PreprocessorDefinitions", "USER=1");
            final.SetValue("GENERAL", "NoReloadOnInit", "0");
            final.SetValue("INPUT", "KeyOverlay", "113,0,0,0");
            final.SetValue("INPUT", "KeyEffects", "0,0,0,0");
            final.SetValue("OVERLAY", "ShowFPS", "2");
            final.SetValue("UserEffect.fx", "Strength", "0.75");
            final.SetValue("ADDON", "Obsolete", "old");
            final.Save(Secondary(fresh));
            primary.SetValue("GENERAL", "EffectSearchPaths", "NewShaders");
            primary.SetValue("ADDON", "DisabledAddons", "Removed@removed.addon64");
            primary.Save(fresh.ReShadeIniPath!);
            var sync = GameIniBootstrap.Ensure(fresh, host, true);
            final = IniDocument.Load(Secondary(fresh));
            Check(!sync.Failed && sync.SyncedSecondary, "管理态变更同步最终配置");
            Check(final.GetValue("GENERAL", "PresetPath") == "UserFinal.ini" && final.GetValue("GENERAL", "PreprocessorDefinitions") == "USER=1" && final.GetValue("GENERAL", "NoReloadOnInit") == "0", "最终预设和效果加载配置不被禁用主 ini 覆盖");
            Check(final.GetValue("INPUT", "KeyOverlay") == "113,0,0,0" && final.GetValue("INPUT", "KeyEffects") == "0,0,0,0" && final.GetValue("OVERLAY", "ShowFPS") == "2", "最终用户 INPUT 和 HUD 保留，包括主动禁用键");
            Check(final.GetValue("UserEffect.fx", "Strength") == "0.75", "最终效果节不被主 ini 删除");
            Check(final.GetValue("GENERAL", "EffectSearchPaths") == "NewShaders" && final.GetValue("ADDON", "DisabledAddons") == "Removed@removed.addon64" && !final.ContainsKey("ADDON", "Obsolete"), "资源路径和插件卸载管理态同步");
            byte[] beforeMain = File.ReadAllBytes(fresh.ReShadeIniPath!);
            byte[] beforeFinal = File.ReadAllBytes(Secondary(fresh));
            var again = GameIniBootstrap.Ensure(fresh, host, true);
            Check(!again.Failed && !again.ChangedAnything && beforeMain.SequenceEqual(File.ReadAllBytes(fresh.ReShadeIniPath!)) && beforeFinal.SequenceEqual(File.ReadAllBytes(Secondary(fresh))), "重复启动不改写两份 ini");
            ShadePathAligner.Align(fresh.ReShadeIniPath!, host);
            GameIniBootstrap.Ensure(fresh, host, true);
            Check(Load(fresh).GetValue("GENERAL", "PresetPath") == Path.Combine(fresh.GameDirectory!, GameIniBootstrap.DisabledDx11PresetFileName) && IniDocument.Load(Secondary(fresh)).GetValue("GENERAL", "PresetPath") == "UserFinal.ini", "后续路径对齐及策略收尾仍保留两份预设");

            var churn = Load(fresh);
            churn.SetValue("GENERAL", "PresetPath", "PackDefault.ini");
            churn.SetValue("GENERAL", "NoReloadOnInit", "0");
            churn.SetValue("INPUT", "KeyOverlay", "36,0,0,0");
            churn.SetValue("ADDON", "AddonPath", Path.Combine(root, "NewPack", "Addons"));
            churn.Save(fresh.ReShadeIniPath!);
            GameIniBootstrap.Ensure(fresh, null, true);
            Check(Load(fresh).GetValue("GENERAL", "NoReloadOnInit") == "1"
                && IniDocument.Load(Secondary(fresh)).GetValue("GENERAL", "PresetPath") == "UserFinal.ini"
                && string.IsNullOrEmpty(IniDocument.Load(Secondary(fresh)).GetValue("ADDON", "AddonPath")), "包同步/配置覆写后的最终收尾恢复主禁用态且不覆盖最终用户状态");
            Check(Load(fresh).GetValue("INPUT", "KeyPerformanceMode") == "0,0,0,0", "缺省性能模式快捷键也被禁用");
            // 保持当前宿主路径，随后普通路线恢复只验证 runtime 状态。
            churn = Load(fresh);
            churn.SetValue("ADDON", "AddonPath", host.AddonsPath);
            churn.Save(fresh.ReShadeIniPath!);

            GameEntry migration = Entry("migration");
            File.Copy(template, migration.ReShadeIniPath!);
            var disabled = Load(migration);
            disabled.SetValue("GENERAL", "PresetPath", ".\\DX11 Disabled.ini");
            disabled.SetValue("GENERAL", "NoReloadOnInit", "1");
            disabled.SetValue("GENERAL", "SkipLoadingDisabledEffects", "1");
            disabled.SetValue("INPUT", "KeyOverlay", "0");
            disabled.Save(migration.ReShadeIniPath!);
            Check(!GameIniBootstrap.Ensure(migration, host, true).Failed, "已手工禁用主 ini 可迁移");
            var migrated = IniDocument.Load(Secondary(migration));
            Check(migrated.GetValue("GENERAL", "PresetPath") == "Default.ini" && migrated.GetValue("GENERAL", "NoReloadOnInit") == "0" && migrated.GetValue("INPUT", "KeyOverlay") == "36,0,0,0", "迁移缺失最终 ini 时使用正常宿主默认值");
            disabled.Save(Secondary(migration));
            GameIniBootstrap.Ensure(migration, host, true);
            Check(IniDocument.Load(Secondary(migration)).GetValue("GENERAL", "PresetPath") == "Default.ini", "恢复已经镜像成禁用预设的最终 ini");
            migrated.SetValue("GENERAL", "PresetPath", "ExistingFinal.ini");
            migrated.SetValue("INPUT", "KeyOverlay", "114,0,0,0");
            migrated.Save(Secondary(migration));
            disabled.Save(migration.ReShadeIniPath!);
            GameIniBootstrap.Ensure(migration, host, true);
            Check(IniDocument.Load(Secondary(migration)).GetValue("GENERAL", "PresetPath") == "ExistingFinal.ini" && IniDocument.Load(Secondary(migration)).GetValue("INPUT", "KeyOverlay") == "114,0,0,0", "迁移时已有有效最终用户配置完整保留");
            File.Delete(Secondary(migration));
            Check(!GameIniBootstrap.Ensure(migration, null, true).Failed && IniDocument.Load(Secondary(migration)).GetValue("INPUT", "KeyOverlay") == "36,0,0,0", "无模板时迁移仍生成能打开菜单的正常最终默认值");

            GameEntry coexist = Entry("coexist");
            GameIniBootstrap.Ensure(coexist, host, true);
            string coexistAddons = Path.Combine(root, "coexist-addons");
            Directory.CreateDirectory(coexistAddons);
            string coexistIni = Path.Combine(coexistAddons, "FsrBridgeDepthAddon.ini");
            File.WriteAllText(coexistIni, "[General]\nFinalRuntimeOnly=0\n");
            IniDocument coexistMain = Load(coexist);
            coexistMain.SetValue("ADDON", "AddonPath", coexistAddons);
            coexistMain.Save(coexist.ReShadeIniPath!);
            IniDocument coexistFinal = IniDocument.Load(Secondary(coexist));
            coexistFinal.SetValue("GENERAL", "PresetPath", "FinalNR.ini");
            coexistFinal.Save(Secondary(coexist));
            Check(!GameIniBootstrap.Ensure(coexist, host, true).Failed, "显式共存配置应用成功");
            coexistMain = Load(coexist);
            Check(coexistMain.GetValue("INPUT", "KeyOverlay") == "121,0,0,0" && coexistMain.GetValue("GENERAL", "NoReloadOnInit") == "0", "共存允许 DX11 F10 菜单及效果加载");
            Check(coexistMain.GetValue("GENERAL", "PresetPath") != "FinalNR.ini" && IniDocument.Load(Secondary(coexist)).GetValue("GENERAL", "PresetPath") == "FinalNR.ini", "共存保留独立预设和最终 NR 配置");
            Check(!GameIniBootstrap.Ensure(coexist, host, true).ChangedAnything, "共存重复启动幂等");
            File.WriteAllText(coexistIni, "[General]\nFinalRuntimeOnly=1\n");
            GameIniBootstrap.Ensure(coexist, host, true);
            Check(Load(coexist).GetValue("INPUT", "KeyOverlay") == "0,0,0,0", "关闭共存恢复 DX11 抑制");

            // A real preset is required for initial migration; all files stay in this fixture.
            const string singleConfig = "[General]\nEffectsOwner=DX11PreNR\nFinalRuntimeOnly=0\n";
            void Preset(GameEntry game, string name, string techniques = "User@User.fx") =>
                File.WriteAllText(Path.Combine(game.GameDirectory!, name), "Techniques=" + techniques + "\nTechniqueSorting=" + techniques + "\n");
            GameEntry single = Entry("single-effects");
            GameIniBootstrap.Ensure(single, host, true);
            IniDocument singleSource = Load(single);
            singleSource.SetValue("ADDON", "AddonPath", coexistAddons);
            singleSource.SetValue("ADDON", "DisabledAddons", "FsrBridgeDepthAddon@FsrBridgeDepthAddon.addon64,Other@Other.addon64");
            singleSource.Save(single.ReShadeIniPath!);
            Preset(single, "FinalUser.ini");
            IniDocument singleFinal = IniDocument.Load(Secondary(single));
            singleFinal.SetValue("GENERAL", "PresetPath", ".\\FinalUser.ini");
            singleFinal.SetValue("INPUT", "KeyOverlay", "113,0,0,0");
            singleFinal.SetValue("OVERLAY", "ShowFPS", "2");
            singleFinal.Save(Secondary(single));
            File.WriteAllText(coexistIni, singleConfig);
            var singleFirst = GameIniBootstrap.Ensure(single, host, true);
            singleSource = Load(single);
            singleFinal = IniDocument.Load(Secondary(single));
            Check(!singleFirst.Failed && singleSource.GetValue("GENERAL", "PresetPath") == ".\\FinalUser.ini"
                && singleFinal.GetValue("GENERAL", "PresetPath") == ".\\FinalUser.ini", "单套方案首次把最终用户预设迁移给被禁用 source");
            Check(singleSource.GetValue("GENERAL", "HoYoShadeEffectsStage") == "1" && singleFinal.GetValue("GENERAL", "HoYoShadeEffectsStage") == "2"
                && singleFinal.GetValue("OVERLAY", "HoYoShadeHomeOverlay") == "NR Input Effects", "两 runtime stage 和自定义首页正确");
            Check(singleSource.GetValue("INPUT", "KeyOverlay") == "0,0,0,0" && singleSource.GetValue("GENERAL", "NoReloadOnInit") == "0"
                && singleSource.GetValue("GENERAL", "SkipLoadingDisabledEffects") == "0"
                && singleFinal.GetValue("GENERAL", "NoReloadOnInit") == "1"
                && singleFinal.GetValue("INPUT", "KeyOverlay") == "113,0,0,0", "source 隐藏菜单并加载 fx，final 保留用户菜单键");
            Check(new[] { "ShowClock", "ShowFPS", "ShowFrameTime", "ShowPresetName", "ShowScreenshotMessage", "ShowPresetTransitionMessage", "ShowForceLoadEffectsButton" }
                .All(key => singleSource.GetValue("OVERLAY", key) == "0") && singleFinal.GetValue("OVERLAY", "ShowFPS") == "2", "source 关闭重复 HUD，final HUD 保留");
            Check(singleSource.GetValue("ADDON", "DisabledAddons") == "FsrBridgeDepthAddon@FsrBridgeDepthAddon.addon64,Other@Other.addon64"
                && singleFinal.GetValue("ADDON", "DisabledAddons") == singleSource.GetValue("ADDON", "DisabledAddons"),
                "单套准备保留用户禁用的控制插件和其他插件，最终列表一致");
            Check(!GameIniBootstrap.Ensure(single, host, true).ChangedAnything, "单套方案重复启动幂等");
            Preset(single, "ChangedSource.ini");
            singleSource.SetValue("GENERAL", "PresetPath", "ChangedSource.ini");
            singleSource.SetValue("GENERAL", "NoReloadOnInit", "1");
            singleSource.SetValue("GENERAL", "HoYoShadeEffectsStage", "0");
            singleSource.SetValue("INPUT", "KeyOverlay", "121,0,0,0");
            singleSource.SetValue("OVERLAY", "ShowFPS", "1");
            singleSource.Save(single.ReShadeIniPath!);
            GameIniBootstrap.Ensure(single, host, true);
            Check(Load(single).GetValue("GENERAL", "PresetPath") == "ChangedSource.ini"
                && IniDocument.Load(Secondary(single)).GetValue("GENERAL", "PresetPath") == "ChangedSource.ini", "后续启动保留 source 用户改选并同步 final，不重新迁移");
            Check(Load(single).GetValue("GENERAL", "NoReloadOnInit") == "0" && Load(single).GetValue("GENERAL", "HoYoShadeEffectsStage") == "1"
                && Load(single).GetValue("INPUT", "KeyOverlay") == "0,0,0,0" && Load(single).GetValue("OVERLAY", "ShowFPS") == "0", "每次启动重新确保 source 执行策略");
            Preset(single, "ChangedFinal.ini");
            singleFinal = IniDocument.Load(Secondary(single));
            singleFinal.SetValue("GENERAL", "PresetPath", "ChangedFinal.ini");
            singleFinal.SetValue("INPUT", "KeyOverlay", "36,0,0,0");
            singleFinal.SetValue("GENERAL", "HoYoShadeEffectsStage", "0");
            singleFinal.SetValue("OVERLAY", "HoYoShadeHomeOverlay", "Home");
            singleFinal.Save(Secondary(single));
            GameIniBootstrap.Ensure(single, host, true);
            Check(Load(single).GetValue("GENERAL", "PresetPath") == "ChangedFinal.ini"
                && IniDocument.Load(Secondary(single)).GetValue("GENERAL", "PresetPath") == "ChangedFinal.ini", "自定义首页改选 final 预设后同步 source");
            singleFinal = IniDocument.Load(Secondary(single));
            Check(singleFinal.GetValue("GENERAL", "HoYoShadeEffectsStage") == "2" && singleFinal.GetValue("OVERLAY", "HoYoShadeHomeOverlay") == "NR Input Effects"
                && singleFinal.GetValue("INPUT", "KeyOverlay") == "36,0,0,0", "每次启动恢复 final 菜单策略，保留新的 Home 键");
            Check(!GameIniBootstrap.Ensure(single, host, true).ChangedAnything, "用户改选后的单套启动幂等");

            GameEntry validSource = Entry("valid-source");
            GameIniBootstrap.Ensure(validSource, host, true);
            Preset(validSource, "SourceUser.ini");
            Preset(validSource, "FinalUser.ini");
            var validPrimary = Load(validSource);
            validPrimary.SetValue("ADDON", "AddonPath", coexistAddons);
            validPrimary.SetValue("GENERAL", "PresetPath", "SourceUser.ini");
            validPrimary.Save(validSource.ReShadeIniPath!);
            var validFinal = IniDocument.Load(Secondary(validSource));
            validFinal.SetValue("GENERAL", "PresetPath", "FinalUser.ini");
            validFinal.Save(Secondary(validSource));
            GameIniBootstrap.Ensure(validSource, host, true);
            Check(Load(validSource).GetValue("GENERAL", "PresetPath") == "SourceUser.ini"
                && IniDocument.Load(Secondary(validSource)).GetValue("GENERAL", "PresetPath") == "SourceUser.ini", "首次切换不覆盖已有有效 source 用户预设");

            // Disabled effects are valid user choices. Neither migration nor
            // repeated startup may turn them on or cross-copy depth definitions.
            foreach ((string name, string content) in new[]
            {
                ("sorting-only", "Techniques=\nTechniqueSorting=DisplayDepth@DisplayDepth.fx,MXAO@qUINT_mxao.fx\n"),
                ("parameters-only", "Techniques=\nTechniqueSorting=\n[DisplayDepth.fx]\nbUIShowOffset=0\n[qUINT_mxao.fx]\nMXAO_SAMPLE_RADIUS=2.5\n"),
                ("addon-parameters-only", "Techniques=\nTechniqueSorting=\n[User.addonfx]\nStrength=0.75\n")
            })
            {
                GameEntry allOff = Entry(name);
                GameIniBootstrap.Ensure(allOff, host, true);
                string sourceSelection = "HoYoShade DX11 Before NR.ini";
                string sourceFile = Path.Combine(allOff.GameDirectory!, sourceSelection);
                File.WriteAllText(sourceFile, content);
                Preset(allOff, "Mod OFF.ini", "");
                var offSource = Load(allOff);
                offSource.SetValue("ADDON", "AddonPath", coexistAddons);
                offSource.SetValue("GENERAL", "PresetPath", sourceSelection);
                offSource.SetValue("GENERAL", "PreprocessorDefinitions", "RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=0,SOURCE_ONLY=1");
                offSource.Save(allOff.ReShadeIniPath!);
                var offFinal = IniDocument.Load(Secondary(allOff));
                offFinal.SetValue("GENERAL", "PresetPath", "Mod OFF.ini");
                offFinal.SetValue("GENERAL", "PreprocessorDefinitions", "RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=1,FINAL_ONLY=1");
                offFinal.Save(Secondary(allOff));
                var allOffFirst = GameIniBootstrap.Ensure(allOff, host, true);
                Check(!allOffFirst.Failed && Load(allOff).GetValue("GENERAL", "PresetPath") == sourceSelection
                    && IniDocument.Load(Secondary(allOff)).GetValue("GENERAL", "PresetPath") == sourceSelection,
                    name + ": 已取消全部效果的 source 用户预设优先并共享路径");
                Check(File.ReadAllText(sourceFile) == content
                    && Load(allOff).GetValue("GENERAL", "PreprocessorDefinitions") == "RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=0,SOURCE_ONLY=1"
                    && IniDocument.Load(Secondary(allOff)).GetValue("GENERAL", "PreprocessorDefinitions") == "RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=1,FINAL_ONLY=1",
                    name + ": 不启用滤镜、不改预设文件且分别保留两端深度定义");
                Check(!GameIniBootstrap.Ensure(allOff, host, true).ChangedAnything && File.ReadAllText(sourceFile) == content,
                    name + ": 全关闭效果的共享预设重复启动幂等");
            }
            // Preserve the aligned current-host preset, never restore a stale host path.
            string currentPresetDir = Path.Combine(hostRoot, "Presets");
            Directory.CreateDirectory(currentPresetDir);
            string alignedPreset = Path.Combine(currentPresetDir, "AlignedUser.ini");
            File.WriteAllText(alignedPreset, "Techniques=AO@AO.fx\nTechniqueSorting=AO@AO.fx\n");
            GameEntry staleHostGame = Entry("single-stale-host");
            GameIniBootstrap.Ensure(staleHostGame, host, true);
            var staleHostSource = Load(staleHostGame);
            staleHostSource.SetValue("ADDON", "AddonPath", coexistAddons);
            staleHostSource.SetValue("GENERAL", "PresetPath", Path.Combine(root, "OldHost", "Presets", "AlignedUser.ini"));
            staleHostSource.Save(staleHostGame.ReShadeIniPath!);
            GameIniBootstrap.Ensure(staleHostGame, host, true);
            Check(Load(staleHostGame).GetValue("GENERAL", "PresetPath") == alignedPreset
                && IniDocument.Load(Secondary(staleHostGame)).GetValue("GENERAL", "PresetPath") == alignedPreset,
                "单套预设保留路径对齐结果，不回写旧宿主路径");

            string srDir = Path.Combine(root, "starrail-single");
            Directory.CreateDirectory(srDir);
            GameEntry sr = new("biz:hkrpg_bilibili", "Star Rail") { ExePath = Path.Combine(srDir, "StarRail.exe") };
            GameIniBootstrap.Ensure(sr, host);
            Preset(sr, "SrAO.ini", "AO@AO.fx");
            var srSource = Load(sr);
            srSource.SetValue("ADDON", "AddonPath", coexistAddons);
            srSource.SetValue("GENERAL", "PresetPath", "SrAO.ini");
            srSource.SetValue("GENERAL", "PreprocessorDefinitions", "RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=0,SR_SOURCE=1");
            srSource.Save(sr.ReShadeIniPath!);
            var srFinal = IniDocument.Load(Secondary(sr));
            srFinal.SetValue("GENERAL", "PreprocessorDefinitions", "RESHADE_DEPTH_INPUT_IS_UPSIDE_DOWN=1,SR_FINAL=1");
            srFinal.SetValue("INPUT", "KeyOverlay", "36,0,0,0");
            srFinal.Save(Secondary(sr));
            var srFirst = GameIniBootstrap.Ensure(sr, host, false, starRailFinalDx12: true);
            srSource = Load(sr); srFinal = IniDocument.Load(Secondary(sr));
            Check(!srFirst.Failed && srSource.GetValue("GENERAL", "HoYoShadeEffectsStage") == "1"
                && srFinal.GetValue("GENERAL", "HoYoShadeEffectsStage") == "2", "崩铁 Opt DX11 来源进入单套效果控制");
            Check(srSource.GetValue("GENERAL", "PresetPath") == "SrAO.ini" && srFinal.GetValue("GENERAL", "PresetPath") == "SrAO.ini",
                "崩铁 source/final 共享真实用户预设");
            Check(srSource.GetValue("GENERAL", "NoReloadOnInit") == "0" && srFinal.GetValue("GENERAL", "NoReloadOnInit") == "1",
                "崩铁只在 source 初始化效果，final 不重复编译");
            Check(srSource.GetValue("INPUT", "KeyOverlay") == "0,0,0,0" && srFinal.GetValue("INPUT", "KeyOverlay") == "36,0,0,0",
                "崩铁 source 隐藏菜单，final 保留 Home");
            Check(srSource.GetValue("GENERAL", "PreprocessorDefinitions")!.Contains("SR_SOURCE=1")
                && srFinal.GetValue("GENERAL", "PreprocessorDefinitions")!.Contains("SR_FINAL=1"),
                "崩铁单套配置分别保留两端深度宏");
            Check(string.IsNullOrEmpty(srFinal.GetValue("ADDON", "AddonPath"))
                && srSource.GetValue("ADDON", "AddonPath") == coexistAddons, "崩铁 final 不重复加载 NR 插件，source 保留包路径");
            Check(!GameIniBootstrap.Ensure(sr, host, false, true).ChangedAnything, "崩铁单套启动幂等");
            GameIniBootstrap.Ensure(sr, host, false, false);
            Check(!Load(sr).ContainsKey("GENERAL", "HoYoShadeEffectsStage")
                && Load(sr).GetValue("INPUT", "KeyOverlay") == "36,0,0,0"
                && IniDocument.Load(Secondary(sr)).GetValue("GENERAL", "NoReloadOnInit") == "0", "退出崩铁最终路线恢复普通加载和菜单");

            // A physically disabled addon is user state, even in explicit single mode.
            string controllerName = "FsrBridgeDepthAddon.addon64";
            Directory.CreateDirectory(host.AddonsPath);
            byte[] controllerBytes = [1, 2, 3, 4];
            string sharedDisabled = Path.Combine(host.AddonsPath, controllerName + "x");
            string sharedEnabled = Path.Combine(host.AddonsPath, controllerName);
            string packedDisabled = Path.Combine(coexistAddons, controllerName + "x");
            string packedEnabled = Path.Combine(coexistAddons, controllerName);
            File.WriteAllBytes(sharedDisabled, controllerBytes);
            File.WriteAllBytes(packedDisabled, controllerBytes);
            GameIniBootstrap.Ensure(single, host, true);
            Check(File.Exists(sharedDisabled) && File.Exists(packedDisabled)
                && !File.Exists(sharedEnabled) && !File.Exists(packedEnabled),
                "单套模式不把全局或每游戏 addon64x 强制改回启用");
            Check(File.ReadAllBytes(sharedDisabled).SequenceEqual(controllerBytes)
                && File.ReadAllBytes(packedDisabled).SequenceEqual(controllerBytes),
                "启动准备不修改禁用插件二进制内容");
            Check(!GameIniBootstrap.Ensure(single, host, true).ChangedAnything, "保留插件关闭状态重复启动幂等");
            File.Delete(packedDisabled);
            File.Move(sharedDisabled, sharedEnabled);
            GameIniBootstrap.Ensure(single, host, true);
            Check(!File.Exists(packedEnabled) && !File.Exists(packedDisabled),
                "每游戏插件缺失时不自动复制共享启用副本");
            string otherDisabled = Path.Combine(coexistAddons, "Other.addon64x");
            File.WriteAllBytes(otherDisabled, [5]);
            GameIniBootstrap.Ensure(single, host, true);
            Check(File.Exists(otherDisabled) && !File.Exists(Path.Combine(coexistAddons, "Other.addon64")),
                "准备单套模式不启用其他插件");
            File.Move(sharedEnabled, sharedDisabled);
            GameIniBootstrap.Ensure(protectedIni, host);
            Check(File.Exists(sharedDisabled), "普通路线同样保留插件文件名禁用态");

            string statePack=Path.Combine(root,"addon-state-pack");
            string stateAddons=Path.Combine(statePack,"Addons");Directory.CreateDirectory(stateAddons);
            File.WriteAllText(Path.Combine(stateAddons,GameAddonPack.MarkerFileName),"{}");
            File.WriteAllText(Path.Combine(stateAddons,"FsrBridgeDepthAddon.ini"),singleConfig);
            File.WriteAllText(Path.Combine(statePack,"ini_config.json"),"""
                { "set": { "ADDON": { "DisabledAddons": "", "LoadFromDllMain": "renodx-dlss5.addon64,FsrBridgeDepthAddon.addon64" } } }
                """);
            GameEntry disabledPack=Entry("disabled-addons-pack");
            GameIniBootstrap.Ensure(disabledPack,host,true);
            var statePrimary=Load(disabledPack);
            const string userDisabled="FSR Bridge Depth Provider@FsrBridgeDepthAddon.addon64,DLSS 5 Neural Rendering@renodx-dlss5.addon64,Other@Other.addon64";
            statePrimary.SetValue("ADDON","AddonPath",stateAddons);
            statePrimary.SetValue("ADDON","DisabledAddons",userDisabled);
            statePrimary.SetValue("ADDON","LoadFromDllMain",",,OtherAllowed.addon64");
            statePrimary.Save(disabledPack.ReShadeIniPath!);
            var stateResult=GameIniBootstrap.Ensure(disabledPack,host,true);
            Check(!stateResult.Failed&&Load(disabledPack).GetValue("ADDON","DisabledAddons")==userDisabled,
                "插件包默认清空禁用列表时仍保留用户关闭的 NR 和控制插件");
            Check(Load(disabledPack).GetValue("ADDON","LoadFromDllMain")==",,OtherAllowed.addon64",
                "包同步保留用户 DllMain 加载选项和空槽位");
            Check(IniDocument.Load(Secondary(disabledPack)).GetValue("ADDON","DisabledAddons")==userDisabled,
                "最终 runtime 不使用过期的已启用列表");
            GameIniBootstrap.Ensure(disabledPack,host,true);
            Check(Load(disabledPack).GetValue("ADDON","DisabledAddons")==userDisabled,
                "多次 Rocket 风格准备不会重启关闭的插件");
            File.WriteAllText(Path.Combine(statePack,"ini_config.json"),"""
                { "remove": { "ADDON": ["DisabledAddons","LoadFromDllMain"] } }
                """);
            GameIniBootstrap.Ensure(disabledPack,host,true);
            Check(Load(disabledPack).GetValue("ADDON","DisabledAddons")==userDisabled,
                "插件包删除禁用键也不能抹掉用户选择");
            Check(Load(disabledPack).GetValue("ADDON","LoadFromDllMain")==",,OtherAllowed.addon64",
                "插件包删除 DllMain 键不能抹掉用户选择");
            statePrimary=Load(disabledPack);statePrimary.SetValue("ADDON","DisabledAddons","");statePrimary.Save(disabledPack.ReShadeIniPath!);
            File.WriteAllText(Path.Combine(statePack,"ini_config.json"),"""
                { "set": { "ADDON": { "DisabledAddons": "PackDefault@PackDefault.addon64" } } }
                """);
            GameIniBootstrap.Ensure(disabledPack,host,true);
            Check(Load(disabledPack).GetValue("ADDON","DisabledAddons")=="",
                "用户显式全部启用的空列表同样优先于包默认");
            statePrimary=Load(disabledPack);statePrimary.SetValue("ADDON","DisabledAddons",userDisabled);statePrimary.Save(disabledPack.ReShadeIniPath!);
            GameIniBootstrap.Ensure(disabledPack,host,false);
            Check(Load(disabledPack).GetValue("ADDON","DisabledAddons")==userDisabled,
                "切回普通 DX11 路线也不丢失关闭状态");

            GameEntry offFinalMigration = Entry("all-off-final-migration");
            GameIniBootstrap.Ensure(offFinalMigration, host, true);
            var offMigrationSource = Load(offFinalMigration);
            offMigrationSource.SetValue("ADDON", "AddonPath", coexistAddons);
            offMigrationSource.Save(offFinalMigration.ReShadeIniPath!);
            string finalOffFile = Path.Combine(offFinalMigration.GameDirectory!, "Mod OFF.ini");
            const string finalOffContent = "Techniques=\nTechniqueSorting=\n[MXAO.fx]\nMXAO_SAMPLE_RADIUS=2.5\n";
            File.WriteAllText(finalOffFile, finalOffContent);
            var offMigrationFinal = IniDocument.Load(Secondary(offFinalMigration));
            offMigrationFinal.SetValue("GENERAL", "PresetPath", "Mod OFF.ini");
            offMigrationFinal.Save(Secondary(offFinalMigration));
            GameIniBootstrap.Ensure(offFinalMigration, host, true);
            Check(Load(offFinalMigration).GetValue("GENERAL", "PresetPath") == "Mod OFF.ini"
                && File.ReadAllText(finalOffFile) == finalOffContent, "被抑制 source 可迁移效果全关闭但含参数的合法 final 用户预设");

            GameEntry emptySource = Entry("empty-source");
            GameIniBootstrap.Ensure(emptySource, host, true);
            Preset(emptySource, "HoYoShade DX11 Before NR.ini", "");
            Preset(emptySource, "FinalUser.ini");
            var emptyPrimary = Load(emptySource);
            emptyPrimary.SetValue("ADDON", "AddonPath", coexistAddons);
            emptyPrimary.SetValue("GENERAL", "PresetPath", "HoYoShade DX11 Before NR.ini");
            emptyPrimary.Save(emptySource.ReShadeIniPath!);
            var emptyFinal = IniDocument.Load(Secondary(emptySource));
            emptyFinal.SetValue("GENERAL", "PresetPath", "FinalUser.ini");
            emptyFinal.Save(Secondary(emptySource));
            GameIniBootstrap.Ensure(emptySource, host, true);
            Check(Load(emptySource).GetValue("GENERAL", "PresetPath") == "FinalUser.ini", "旧共存创建的空 source 预设不会阻止迁移真实 final 用户预设");
            GameEntry emptyFinalGame = Entry("empty-final");
            GameIniBootstrap.Ensure(emptyFinalGame, host, true);
            var emptyFinalSource = Load(emptyFinalGame);
            string? suppressedSelection = emptyFinalSource.GetValue("GENERAL", "PresetPath");
            emptyFinalSource.SetValue("ADDON", "AddonPath", coexistAddons);
            emptyFinalSource.Save(emptyFinalGame.ReShadeIniPath!);
            Preset(emptyFinalGame, "EmptyFinal.ini", "");
            var emptyFinalDoc = IniDocument.Load(Secondary(emptyFinalGame));
            emptyFinalDoc.SetValue("GENERAL", "PresetPath", "EmptyFinal.ini");
            emptyFinalDoc.Save(Secondary(emptyFinalGame));
            GameIniBootstrap.Ensure(emptyFinalGame, host, true);
            Check(Load(emptyFinalGame).GetValue("GENERAL", "PresetPath") == suppressedSelection
                && !Load(emptyFinalGame).ContainsKey("GENERAL", "HoYoShadeSharedPresetPath"), "没有有效用户预设时不迁移空 final，也不提前标记迁移完成");
            Preset(emptyFinalGame, "RecoveredUser.ini");
            emptyFinalDoc = IniDocument.Load(Secondary(emptyFinalGame));
            emptyFinalDoc.SetValue("GENERAL", "PresetPath", "RecoveredUser.ini");
            emptyFinalDoc.Save(Secondary(emptyFinalGame));
            GameIniBootstrap.Ensure(emptyFinalGame, host, true);
            Check(Load(emptyFinalGame).GetValue("GENERAL", "PresetPath") == "RecoveredUser.ini", "随后选到有效 final 用户预设仍可完成首次迁移");

            string singlePackRoot = Path.Combine(root, "single-pack");
            string singlePackAddons = Path.Combine(singlePackRoot, "Addons");
            Directory.CreateDirectory(singlePackAddons);
            File.WriteAllText(Path.Combine(singlePackAddons, GameAddonPack.MarkerFileName), "{}");
            File.WriteAllText(Path.Combine(singlePackAddons, "FsrBridgeDepthAddon.ini"), singleConfig);
            File.WriteAllText(Path.Combine(singlePackRoot, "ini_config.json"), """
                { "set": { "GENERAL": { "PresetPath": "PackDefault.ini", "NoReloadOnInit": "1", "HoYoShadeEffectsStage": "0" },
                  "INPUT": { "KeyOverlay": "121,0,0,0" }, "OVERLAY": { "ShowFPS": "1" } },
                  "remove": { "GENERAL": ["HoYoShadeSharedPresetPath"] } }
                """);
            singleSource = Load(single);
            singleSource.SetValue("ADDON", "AddonPath", singlePackAddons);
            singleSource.Save(single.ReShadeIniPath!);
            Preset(single, "PackDefault.ini", "Pack@Pack.fx");
            var enforced = GameIniBootstrap.Ensure(single, host, true);
            Check(!enforced.Failed && enforced.AppliedPackIniConfig && Load(single).GetValue("GENERAL", "PresetPath") == "ChangedFinal.ini"
                && IniDocument.Load(Secondary(single)).GetValue("GENERAL", "PresetPath") == "ChangedFinal.ini", "真实 ini_config 覆写及移除迁移标志不丢失用户共享预设");
            Check(!GameIniBootstrap.Ensure(single, host, true).Failed && Load(single).GetValue("GENERAL", "PresetPath") == "ChangedFinal.ini"
                && Load(single).GetValue("GENERAL", "HoYoShadeEffectsStage") == "1" && Load(single).GetValue("GENERAL", "NoReloadOnInit") == "0",
                "带包强制配置的反复启动仍保留用户选择并确保 stage 和加载");
            singleSource = Load(single);
            singleSource.SetValue("ADDON", "AddonPath", coexistAddons);
            singleSource.Save(single.ReShadeIniPath!);

            bool NoSingleKeys(GameEntry game) => new[] { Load(game), IniDocument.Load(Secondary(game)) }.All(doc =>
                !doc.ContainsKey("GENERAL", "HoYoShadeEffectsStage") && !doc.ContainsKey("GENERAL", "HoYoShadeSharedPresetPath")
                && !doc.ContainsKey("OVERLAY", "HoYoShadeHomeOverlay"));
            File.WriteAllText(coexistIni, "[General]\nFinalRuntimeOnly=0\n");
            Check(!GameIniBootstrap.Ensure(single, host, true).Failed && NoSingleKeys(single)
                && Load(single).GetValue("INPUT", "KeyOverlay") == "36,0,0,0", "移除 EffectsOwner 清理专属键并恢复旧共存菜单");
            Check(!GameIniBootstrap.Ensure(single, host, true).ChangedAnything, "退出到旧共存模式后幂等");
            File.WriteAllText(coexistIni, singleConfig);
            GameIniBootstrap.Ensure(single, host, true);
            File.WriteAllText(coexistIni, "[General]\nEffectsOwner=DX11PreNR\nFinalRuntimeOnly=1\n");
            GameIniBootstrap.Ensure(single, host, true);
            Check(NoSingleKeys(single) && Load(single).GetValue("GENERAL", "NoReloadOnInit") == "1"
                && Path.GetFileName(Load(single).GetValue("GENERAL", "PresetPath")) == GameIniBootstrap.DisabledDx11PresetFileName,
                "FinalRuntimeOnly=1 退出单套模式并恢复原 DX11 抑制");
            File.WriteAllText(coexistIni, singleConfig);
            GameIniBootstrap.Ensure(single, host, true);
            GameIniBootstrap.Ensure(single, host);
            Check(NoSingleKeys(single) && Load(single).GetValue("GENERAL", "PresetPath") == "ChangedFinal.ini"
                && Load(single).GetValue("INPUT", "KeyOverlay") == "36,0,0,0" && Load(single).GetValue("OVERLAY", "ShowFPS") == "2",
                "关闭 Bridge 后清理单套键，普通 DX11 保留共享用户预设并恢复菜单 HUD");
            Check(!GameIniBootstrap.Ensure(single, host).ChangedAnything, "单套切回普通路线后幂等");

            string relativeAddons = Path.Combine(validSource.GameDirectory!, "local-addons");
            Directory.CreateDirectory(relativeAddons);
            File.WriteAllText(Path.Combine(relativeAddons, "FsrBridgeDepthAddon.ini"), singleConfig);
            validPrimary = Load(validSource);
            validPrimary.SetValue("ADDON", "AddonPath", ".\\local-addons");
            validPrimary.Save(validSource.ReShadeIniPath!);
            Check(!GameIniBootstrap.Ensure(validSource, null, true).Failed && !NoSingleKeys(validSource), "addon 相对路径按游戏 ini 目录解析并检测单套模式");
            File.Delete(Path.Combine(relativeAddons, "FsrBridgeDepthAddon.ini"));
            Check(!GameIniBootstrap.Ensure(validSource, null, true).Failed && NoSingleKeys(validSource)
                && Load(validSource).GetValue("GENERAL", "NoReloadOnInit") == "1", "删除 addon 模式配置后清理专属键并回退旧策略");

            GameEntry ordinary = Entry("ordinary");
            GameIniBootstrap.Ensure(ordinary, host);
            Check(Load(ordinary).GetValue("GENERAL", "PresetPath") == "Default.ini" && string.IsNullOrEmpty(IniDocument.Load(Secondary(ordinary)).GetValue("ADDON", "AddonPath")), "普通路线维持原行为，不因 ReShade2 存在而禁用 primary");
            string foreignDir = Path.Combine(root, "foreign");
            Directory.CreateDirectory(foreignDir);
            var foreign = new GameEntry("biz:hkrpg_cn", "foreign") { ExePath = Path.Combine(foreignDir, "StarRail.exe") };
            GameIniBootstrap.Ensure(foreign, host, true);
            Check(Load(foreign).GetValue("GENERAL", "PresetPath") == "Default.ini", "即使调用方误传 true，非原神仍不进入最终策略");
            var foreignPrimary = Load(foreign);
            foreignPrimary.SetValue("GENERAL", "PresetPath", Path.Combine(foreignDir, GameIniBootstrap.DisabledDx11PresetFileName));
            foreignPrimary.Save(foreign.ReShadeIniPath!);
            GameIniBootstrap.Ensure(foreign, host);
            Check(Load(foreign).GetValue("GENERAL", "PresetPath") == Path.Combine(foreignDir, GameIniBootstrap.DisabledDx11PresetFileName), "非原神不会触发本策略恢复");
            var restore = GameIniBootstrap.Ensure(fresh, host);
            Check(!restore.Failed && Load(fresh).GetValue("GENERAL", "PresetPath") == "UserFinal.ini" && Load(fresh).GetValue("INPUT", "KeyOverlay") == "113,0,0,0" && Load(fresh).GetValue("GENERAL", "NoReloadOnInit") == "0", "关闭 Bridge 后普通 DX11 恢复可用 runtime 状态");
            Check(!GameIniBootstrap.Ensure(fresh, host).ChangedAnything, "切回普通路线后仍幂等");
            GameIniBootstrap.Ensure(fresh, host, true);
            IniDocument relativePrimary = Load(fresh);
            relativePrimary.SetValue("GENERAL", "PresetPath", ".\\" + GameIniBootstrap.DisabledDx11PresetFileName);
            relativePrimary.Save(fresh.ReShadeIniPath!);
            var relativeRestore = GameIniBootstrap.Ensure(fresh, host);
            Check(!relativeRestore.Failed && Load(fresh).GetValue("GENERAL", "PresetPath") == "UserFinal.ini" && Load(fresh).GetValue("INPUT", "KeyOverlay") == "113,0,0,0", "运行时将空预设改成相对路径后仍可恢复普通 DX11");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine($"Bootstrap regression: {count - failed}/{count} passed");
        return failed == 0 ? 0 : 1;
    }
}
