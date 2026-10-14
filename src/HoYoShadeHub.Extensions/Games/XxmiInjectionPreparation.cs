using HoYoShadeHub.Extensions.Services;
using Step = HoYoShadeHub.Extensions.Services.OptiScalerRuntime.FsrBridgeChainStep;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>Candidate timing fix for Genshin XXMI Manual only. No injection, profile, JSON or module-selection writes.</summary>
public static class XxmiInjectionPreparation
{
    public static bool Applies(string? gameBiz, bool xxmiEnabled, bool manual, bool orderedChain) =>
        gameBiz?.StartsWith("hk4e_", StringComparison.OrdinalIgnoreCase) == true && xxmiEnabled && manual && orderedChain;

    public static List<Step> PrepareManualMainStack(string? gameBiz, bool xxmiEnabled, bool manual, bool orderedChain,
        bool optiEnabled, bool shadeEnabled, string bridgeDll, string? optiDll, string? shadeDll,
        string? ffx12Dll, IReadOnlyList<Step> source)
    {
        // Never change ordinary EXE, Rocket, Official, StarRail or ZZZ routes.
        if (!Applies(gameBiz, xxmiEnabled, manual, orderedChain)) return source.ToList();
        string bridge = AbsoluteDll(bridgeDll, true);
        string? opti = SelectedDll(optiEnabled, optiDll), shade = SelectedDll(shadeEnabled, shadeDll);
        string? ffx = ffx12Dll is null ? null : AbsoluteDll(ffx12Dll, true);
        var steps = source.Select(s => s.IsPath ? new Step(s.Verb, AbsoluteDll(s.Argument, false)) : s).ToList();
        if (steps.Count == 0 || steps[0] != Step.Wait("dxgi.dll")
            || steps.Any(s => s.Verb is not ("wait" or "load" or "migoto") || s.Argument.IndexOfAny(['\r', '\n']) >= 0))
            throw new InvalidDataException("XXMI Manual chain must start with wait dxgi.dll and use valid single-line steps.");
        var models = steps.Where(s => s.Verb == "migoto").ToArray();
        if (models.Length != 1) throw new InvalidDataException("XXMI Manual requires exactly one GIMI/migoto owner.");
        AbsoluteDll(models[0].Argument, true);
        int modelIndex = steps.IndexOf(models[0]);
        if (steps.Take(modelIndex).Any(s => s.Verb == "load")
            || steps.Any(s => s.Verb == "load" && (Same(s.Argument, bridge) || Same(s.Argument, models[0].Argument))))
            throw new InvalidDataException("GIMI must precede graphics loads; Bridge/GIMI must not be loaded twice.");
        RequireSelectedLoad(steps, optiEnabled, optiDll, "OptiScaler");
        RequireSelectedLoad(steps, shadeEnabled, shadeDll, "HoYoShade/ReShade");
        RejectOtherOwner(steps, "OptiScaler.dll", optiEnabled, optiDll);
        RejectOtherOwner(steps, "ReShade64.dll", shadeEnabled, shadeDll);
        RejectOtherOwner(steps, "amd_fidelityfx_upscaler_dx12.dll", ffx is not null, ffx12Dll);
        if (ffx is not null && !steps.Any(s => s.Verb == "load" && Same(s.Argument, ffx)))
            throw new InvalidDataException("Selected FFX12 load missing from final XXMI override.");
        if (opti is not null && (Same(opti, bridge) || Same(opti, models[0].Argument) || Same(opti, shade) || Same(opti, ffx))
            || shade is not null && (Same(shade, bridge) || Same(shade, models[0].Argument) || Same(shade, ffx)))
            throw new InvalidDataException("XXMI main components must have distinct owners/paths.");

        var main = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in new[] { ffx, opti, shade }) if (path is not null) main.Add(path);
        var waits = new HashSet<string>(main.Select(p => Path.GetFileName(p)!), StringComparer.OrdinalIgnoreCase) { "swapchain" };
        // Preserve existing selected module loads, but do not resolve/enable any new module. Rebuild only
        // the main stack. The old pre-swapchain gate becomes trailing observation after all DLL hooks exist.
        steps.RemoveAll(s => s.Verb == "load" && main.Contains(s.Argument)
            || s.Verb == "wait" && waits.Contains(s.Argument));
        if (ffx is not null) steps.Add(Step.Load(ffx));
        if (opti is not null) { steps.Add(Step.Load(opti)); steps.Add(Step.Wait(Path.GetFileName(opti))); }
        if (shade is not null) steps.Add(Step.Load(shade));
        steps.Add(Step.Wait("swapchain"));
        return steps;
    }

    public static bool ReadbackMatches(IReadOnlyList<Step> expected, IReadOnlyList<Step>? actual) =>
        actual is not null && expected.Select(Canonical).SequenceEqual(actual.Select(Canonical));

    /// <summary>Called only by the Manual chain branch. Preserve an existing changed chain before writing; identical plans are not rewritten.</summary>
    public static string[]? WriteManualChain(string directory, IReadOnlyList<Step> prepared, out string? backupPath)
    {
        backupPath = null;
        string path = Path.Combine(directory, OptiScalerRuntime.FsrBridgeChainName);
        if (File.Exists(path))
        {
            byte[] original = File.ReadAllBytes(path);
            if (ReadbackMatches(prepared, OptiScalerRuntime.ParseFsrBridgeChain(File.ReadAllText(path))))
                return prepared.Select(s => s.Verb + " " + s.Argument).ToArray();
            backupPath = path + ".bak-before-xxmi-mainstack-" + Guid.NewGuid().ToString("N");
            File.Copy(path, backupPath, overwrite: false);
            if (!original.SequenceEqual(File.ReadAllBytes(backupPath)))
                throw new IOException("XXMI chain backup did not match the original; no write performed.");
        }
        return OptiScalerRuntime.WriteFsrBridgeChain(directory, prepared);
    }

    private static Step Canonical(Step s) => s.IsPath ? new(s.Verb, Path.GetFullPath(s.Argument)) : s;
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static string? SelectedDll(bool enabled, string? path) => enabled ? AbsoluteDll(path, true) : null;
    private static void RejectOtherOwner(List<Step> steps, string knownName, bool enabled, string? selectedPath)
    {
        string? selected = selectedPath is null ? null : AbsoluteDll(selectedPath, enabled);
        foreach (Step step in steps.Where(s => s.Verb == "load"))
        {
            string name = Path.GetFileName(step.Argument);
            if ((Same(name, knownName) || selected is not null && Same(name, Path.GetFileName(selected)))
                && (!enabled || !Same(step.Argument, selected)))
                throw new InvalidDataException("XXMI main component disabled or has another path/owner: " + step.Argument);
        }
    }
    private static void RequireSelectedLoad(List<Step> steps, bool enabled, string? path, string label)
    {
        string? full = path is null ? null : AbsoluteDll(path, enabled);
        bool present = full is not null && steps.Any(s => s.Verb == "load" && Same(s.Argument, full));
        if (enabled != present) throw new InvalidDataException(label + (enabled ? " selected but absent from final XXMI chain." : " disabled but still loaded by override."));
    }
    private static string AbsoluteDll(string? path, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(['\r', '\n', '"']) >= 0
            || !Path.IsPathFullyQualified(path) || !Same(Path.GetExtension(path), ".dll") || mustExist && !File.Exists(path))
            throw new InvalidDataException("XXMI component needs an absolute DLL path" + (mustExist ? " to an existing selected file: " : ": ") + (path ?? "<missing>"));
        return Path.GetFullPath(path);
    }
}
