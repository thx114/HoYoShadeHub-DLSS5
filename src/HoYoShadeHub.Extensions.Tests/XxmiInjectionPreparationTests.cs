using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Services;
using Step = HoYoShadeHub.Extensions.Services.OptiScalerRuntime.FsrBridgeChainStep;

namespace HoYoShadeHub.Extensions.Tests;

internal static class XxmiInjectionPreparationTests
{
    public static int Run()
    {
        int passed = 0;
        string root = Path.Combine(Path.GetTempPath(), "xxmi manual main stack " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        void Check(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException(name);
            passed++; Console.WriteLine("[PASS] " + name);
        }
        void Throws(Action action, string name)
        {
            bool rejected = false;
            try { action(); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, name);
        }
        string Fixture(string name)
        {
            string p = Path.Combine(root, name); Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, "CPU fixture; never loaded"); return Path.GetFullPath(p);
        }
        try
        {
            string bridge = Fixture("Dx11FsrBridge.dll"), gimi = Fixture("GIMI/d3d11.dll");
            string opti = Fixture("mfg-ada-0.2.2.4/OptiScaler.dll"), shade = Fixture("HoYoShade/ReShade64.dll");
            string ffx = Fixture("AMD/amd_fidelityfx_upscaler_dx12.dll"), other = Fixture("already selected extra.dll");
            List<Step> baseline = [Step.Wait("dxgi.dll"), Step.Migoto(gimi), Step.Wait("swapchain"),
                Step.Load(ffx), Step.Load(opti), Step.Wait("OptiScaler.dll"), Step.Load(shade)];
            List<Step> expected = [Step.Wait("dxgi.dll"), Step.Migoto(gimi), Step.Load(ffx), Step.Load(opti),
                Step.Wait("OptiScaler.dll"), Step.Load(shade), Step.Wait("swapchain")];
            List<Step> Prepare(IReadOnlyList<Step>? steps = null, string? game = "hk4e_cn", bool xxmi = true,
                bool manual = true, bool ordered = true, bool opt = true, bool rs = true, string? optPath = null,
                string? shadePath = null, string? ffxPath = null) => XxmiInjectionPreparation.PrepareManualMainStack(
                    game, xxmi, manual, ordered, opt, rs, bridge, optPath ?? opti, shadePath ?? shade, ffxPath ?? ffx, steps ?? baseline);
            var fixedPlan = Prepare();
            Check(fixedPlan.SequenceEqual(expected), "FFX12 -> Opti -> ReShade now precede trailing swapchain observation");
            Check(fixedPlan.IndexOf(Step.Migoto(gimi)) < fixedPlan.IndexOf(Step.Load(ffx)), "GIMI remains before every graphics DLL");
            Check(fixedPlan.IndexOf(Step.Load(opti)) < fixedPlan.IndexOf(Step.Load(shade)), "Opti remains before ReShade");
            Check(fixedPlan[^1] == Step.Wait("swapchain"), "No pre-main swapchain gate remains");
            Check(baseline[2] == Step.Wait("swapchain") && baseline.Count == 7, "Original plan is not mutated");
            Check(Prepare(fixedPlan).SequenceEqual(fixedPlan), "Timing preparation is idempotent");
            bool isolated = true;
            foreach (string? game in new[] { "hk4e_cn", "hk4e_global", "hkrpg_cn", "hkrpg_bilibili", "nap_cn", null })
            foreach (bool xxmi in new[] { false, true }) foreach (bool manual in new[] { false, true })
            foreach (bool ordered in new[] { false, true })
            {
                bool applies = game?.StartsWith("hk4e_", StringComparison.OrdinalIgnoreCase) == true && xxmi && manual && ordered;
                isolated &= Prepare(game: game, xxmi: xxmi, manual: manual, ordered: ordered).SequenceEqual(applies ? expected : baseline);
            }
            Check(isolated, "48-case scope matrix: ordinary/Rocket/Official/HSR/ZZZ unchanged");
            Check(XxmiInjectionPreparation.PrepareManualMainStack("nap_cn", true, true, true, true, true,
                "invalid", "invalid", "invalid", "invalid", baseline).SequenceEqual(baseline), "Out-of-scope route performs no path validation or file work");
            var late = baseline.ToList(); late.Remove(Step.Load(opti)); late.Add(Step.Load(opti));
            Check(Prepare(late).SequenceEqual(expected), "Override late/misordered main DLL loads are repaired, without changing selection");
            Throws(() => Prepare(baseline.Where(s => s != Step.Load(opti)).ToArray()), "Override missing selected Opti is rejected, not silently skipped");
            Throws(() => Prepare(baseline.Where(s => s != Step.Load(shade)).ToArray()), "Override missing selected HoYoShade is rejected");
            Throws(() => Prepare(baseline.Where(s => s != Step.Load(ffx)).ToArray()), "Override missing selected dedicated FFX12 is rejected");
            var duplicate = baseline.ToList(); duplicate.Add(Step.Load(opti)); duplicate.Add(Step.Load(shade)); duplicate.Add(Step.Load(ffx));
            Check(Prepare(duplicate).SequenceEqual(expected), "Duplicate main stack loads collapse to one responsibility");
            var extra = baseline.ToList(); extra.Insert(2, Step.Load(other));
            var extraPlan = Prepare(extra);
            Check(extraPlan.Count(s => s.Argument == other) == 1 && extraPlan.IndexOf(Step.Load(other)) < extraPlan.IndexOf(Step.Load(opti)),
                "Already selected module preserved; no module resolution or enablement added");
            Throws(() => Prepare(opt: false), "Disabled Opti cannot still be loaded by an override");
            Throws(() => Prepare(rs: false), "Disabled ReShade cannot still be loaded by an override");
            List<Step> shadeOnly = [Step.Wait("dxgi.dll"), Step.Migoto(gimi), Step.Wait("swapchain"), Step.Load(shade)];
            var noOpt = XxmiInjectionPreparation.PrepareManualMainStack("hk4e_cn", true, true, true, false, true,
                bridge, opti, shade, null, shadeOnly);
            Check(!noOpt.Any(s => s.Argument == opti) && noOpt[^2] == Step.Load(shade), "Opti switch off is preserved; no default enablement");
            List<Step> optOnly = [Step.Wait("dxgi.dll"), Step.Migoto(gimi), Step.Wait("swapchain"), Step.Load(opti)];
            var noShade = XxmiInjectionPreparation.PrepareManualMainStack("hk4e_cn", true, true, true, true, false,
                bridge, opti, shade, null, optOnly);
            Check(!noShade.Any(s => s.Argument == shade), "ReShade switch off preserved");
            Throws(() => Prepare(optPath: Path.Combine(root, "missing OptiScaler.dll")), "Missing selected Opti file rejected before launch");
            Throws(() => Prepare(shadePath: Path.Combine(root, "missing ReShade64.dll")), "Missing selected HoYoShade file rejected before launch");
            Throws(() => XxmiInjectionPreparation.PrepareManualMainStack("hk4e_cn", true, true, true, true, true,
                bridge, opti, null, ffx, baseline), "Enabled shade with null resolved path explicitly fails");
            Throws(() => Prepare(optPath: "OptiScaler.dll"), "Relative selected path rejected");
            Throws(() => Prepare(optiDllPathAttack()), "Line-breaking override path rejected");
            List<Step> optiDllPathAttack() { var s = baseline.ToList(); s.Add(Step.Load(opti + "\nload " + shade)); return s; }
            var old = Fixture("mfg-ada-0.2.2.3/OptiScaler.dll"); var wrongBuild = baseline.ToList(); wrongBuild[4] = Step.Load(old);
            Throws(() => Prepare(wrongBuild), "Selected .4 cannot silently fall back to a .3 chain path");
            var bothBuilds = baseline.ToList(); bothBuilds.Insert(2, Step.Load(old));
            Throws(() => Prepare(bothBuilds), "Selected .4 plus same-basename .3 Opti is rejected as dual owner");
            string otherShade = Fixture("another runtime/ReShade64.dll"); var bothShade = baseline.ToList(); bothShade.Insert(2, Step.Load(otherShade));
            Throws(() => Prepare(bothShade), "Selected ReShade plus another directory's ReShade64 is rejected");
            string otherFfx = Fixture("another AMD/amd_fidelityfx_upscaler_dx12.dll"); var bothFfx = baseline.ToList(); bothFfx.Insert(2, Step.Load(otherFfx));
            Throws(() => Prepare(bothFfx), "Dedicated FFX12 plus other-path same-name FFX12 is rejected");
            var disabledAliasOpt = baseline.Where(s => s != Step.Load(opti)).ToList(); disabledAliasOpt.Insert(2, Step.Load(old));
            Throws(() => Prepare(disabledAliasOpt, opt: false), "Disabled Opti with an old-path OptiScaler.dll in override is rejected");
            var disabledAliasShade = baseline.Where(s => s != Step.Load(shade)).ToList(); disabledAliasShade.Insert(2, Step.Load(otherShade));
            Throws(() => Prepare(disabledAliasShade, rs: false), "Disabled ReShade with another ReShade64.dll in override is rejected");
            Throws(() => Prepare(baseline.Where(s => s.Verb != "migoto").ToArray()), "Missing GIMI owner rejected");
            var twoModels = baseline.ToList(); twoModels.Insert(2, Step.Migoto(gimi));
            Throws(() => Prepare(twoModels), "Duplicate migoto owners rejected");
            var bridgeTwice = baseline.ToList(); bridgeTwice.Insert(2, Step.Load(bridge));
            Throws(() => Prepare(bridgeTwice), "External Bridge must not also be loaded by its own chain");
            var earlyFfx = baseline.ToList(); earlyFfx.Remove(Step.Load(ffx)); earlyFfx.Insert(1, Step.Load(ffx));
            Throws(() => Prepare(earlyFfx), "No graphics DLL before GIMI even in overrides");
            var alias = baseline.ToList(); alias.Add(Step.Load(opti.ToUpperInvariant()));
            Check(Prepare(alias).SequenceEqual(expected), "Windows path case aliases do not create another main owner");
            var earlyWait = baseline.ToList(); earlyWait.Insert(2, Step.Wait("ReShade64.dll"));
            Check(Prepare(earlyWait).SequenceEqual(expected), "Main-DLL waits cannot deadlock before their loads");

            string signed = Path.Combine(root, "XXMI Launcher Config.json");
            File.WriteAllText(signed, "{\"extra_libraries_enabled\":false,\"extra_libraries_signature\":\"unchanged\"}");
            byte[] originalJson = File.ReadAllBytes(signed);
            string[]? lines = OptiScalerRuntime.WriteFsrBridgeChain(root, fixedPlan);
            var readback = OptiScalerRuntime.ParseFsrBridgeChain(File.ReadAllText(Path.Combine(root, OptiScalerRuntime.FsrBridgeChainName)));
            Check(lines is not null && XxmiInjectionPreparation.ReadbackMatches(fixedPlan, readback), "Production chain writer/parser roundtrip keeps absolute paths containing spaces");
            string chainPath = Path.Combine(root, OptiScalerRuntime.FsrBridgeChainName);
            OptiScalerRuntime.WriteFsrBridgeChain(root, baseline);
            byte[] oldBytes = File.ReadAllBytes(chainPath);
            string[]? written = XxmiInjectionPreparation.WriteManualChain(root, fixedPlan, out string? backup);
            Check(written is not null && backup is not null && File.ReadAllBytes(backup).SequenceEqual(oldBytes), "Changed existing chain is byte-backed-up before candidate write");
            byte[] candidateBytes = File.ReadAllBytes(chainPath); DateTime candidateTime = File.GetLastWriteTimeUtc(chainPath);
            var repeatedWrite = XxmiInjectionPreparation.WriteManualChain(root, fixedPlan, out string? noBackup);
            Check(repeatedWrite is not null && noBackup is null && File.GetLastWriteTimeUtc(chainPath) == candidateTime
                && File.ReadAllBytes(chainPath).SequenceEqual(candidateBytes), "Identical chain write is byte/timestamp idempotent, without another backup");
            Check(File.ReadAllBytes(signed).SequenceEqual(originalJson), "Signed XXMI JSON remains byte-identical");
            Check(!XxmiInjectionPreparation.ReadbackMatches(fixedPlan, baseline)
                && !XxmiInjectionPreparation.ReadbackMatches(fixedPlan, null), "Stale/missing chain readback cannot report configured");
            string badDirectory = Path.Combine(root, "not a directory"); File.WriteAllText(badDirectory, "fixture");
            Check(XxmiInjectionPreparation.WriteManualChain(badDirectory, fixedPlan, out _) is null, "Production writer reports failure instead of prepared");
            string sourcePath = Path.Combine(Directory.GetCurrentDirectory(), "src", "HoYoShadeHub", "Features", "GameLauncher", "GameLauncherPage.StartGame.cs");
            if (File.Exists(sourcePath))
            {
                string source = File.ReadAllText(sourcePath);
                int call = source.IndexOf("chainSteps = XxmiInjector.PrepareManualMainStack", StringComparison.Ordinal);
                int guardedBranch = source.LastIndexOf("if (orderedInjectChain)", call, StringComparison.Ordinal);
                int failure = source.IndexOf("XXMI Manual 主组件准备失败", call, StringComparison.Ordinal);
                int nextBranch = source.IndexOf("else if (genshinExeInjection)", call, StringComparison.Ordinal);
                Check(guardedBranch >= 0 && call > guardedBranch && failure > call && failure < nextBranch
                    && source[failure..nextBranch].Contains("return;", StringComparison.Ordinal), "Production hookup is ordered-chain-only and fail-closed before EXE creation");
            }
            Console.WriteLine($"XXMI Manual main stack: PASS {passed} / FAIL 0. Candidate only; CPU/temp fixtures, not game runtime/FG/menu validation.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("XXMI Manual tests FAILED: " + ex); return 1; }
        finally { Directory.Delete(root, true); }
    }
}
