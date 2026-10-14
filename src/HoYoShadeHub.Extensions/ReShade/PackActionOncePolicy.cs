using System.Security.Cryptography;
using System.Text;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>纯策略：包动作按逻辑步骤永久 once；内置动作不因 PackRoot 存在而变成包动作。</summary>
public static class PackActionOncePolicy
{
    public enum BlockReason { None, ContextChanged, Pending, NeedsMigration, Failed, LedgerUnavailable }
    public sealed record ExecutionResult(string Summary, bool Blocked, BlockReason Reason);
    public static ExecutionResult Block(string summary, BlockReason reason) => new(summary, true, reason);

    public static IEnumerable<string> PlanKeys(IEnumerable<PackAutoAction> actions) =>
        actions.SelectMany(action => action.Steps.Select((step, index) => StableKey(action.Name, index, step)));

    /// <summary>只绑定已捕获快照，绝不读取当前 auto 来重新授权旧 action。</summary>
    public static bool TryBindSnapshot(PackAutoAction action, bool? declaredPackAction, string? packRoot,
        PackAutoActionSnapshot? capturedSnapshot, string? expectedHash,
        out bool fromPack, out PackAutoActionSnapshot? snapshot, out PackAutoAction boundAction)
    {
        snapshot = capturedSnapshot ?? action.OriginSnapshot;
        // 只有显式 false 且无 Load origin 才是手动 builtin。默认 CLI 来源未知也 fail-closed，
        // 不因 PackRoot 为空/当前 raw 不匹配而降级成可重复无同意动作。
        fromPack = action.OriginSnapshot is not null || declaredPackAction != false;
        boundAction = action;
        if (!fromPack) return true; // 显式手动 builtin 不因 PackRoot 非空受到影响。
        if (declaredPackAction == false || snapshot is null || !snapshot.MatchesRoot(packRoot)
            || (expectedHash is not null && expectedHash != snapshot.Hash)
            || (action.OriginSnapshot is { } origin && (origin.Hash != snapshot.Hash || !origin.MatchesRoot(packRoot))))
            return false;
        var canonical = snapshot.CaptureAction(action);
        if (canonical is null) return false;
        boundAction = canonical; // await 期间外部对按钮 action 的修改不能改变实际执行内容。
        return true;
    }

    /// <summary>生产与回归共用的包门：锁前后检查同一授权快照，随后只登记该快照的完整 plan。</summary>
    public static async Task<ExecutionResult> RunSnapshotAsync(PackAutoAction action, PackAutoActionSnapshot snapshot,
        string packRoot, Func<bool> isCurrent, Func<PackActionStep, Task<string>> dispatch, Action<string>? report = null,
        Func<string, byte[]>? readBytes = null)
    {
        bool Current() => isCurrent() && snapshot.IsCurrent(packRoot, readBytes);
        try
        {
            if (!snapshot.MatchesRoot(packRoot) || snapshot.CaptureAction(action) is not { } captured || !Current())
                return Block("✗ 包授权快照/上下文已变化；未登记 plan、未执行。", BlockReason.ContextChanged);
            using var ledger = await PackOnceLedger.OpenAsync(packRoot);
            if (!Current()) return Block("✗ 锁等待中包授权快照/上下文已变化；未登记 plan、未执行。", BlockReason.ContextChanged);
            var plan = ledger.RegisterPlan(PlanKeys(snapshot.LoadActions()));
            if (plan == PackOnceLedger.PlanRegistration.NeedsMigration && ledger.HasPlan)
                return Block("✗ 包出现未获权逻辑身份；普通 hash 同意不扩 plan，需显式迁移，批次已阻断。", BlockReason.NeedsMigration);
            return await RunStepsAsync(captured, true, ledger, dispatch, Current, report);
        }
        catch (Exception ex)
        {
            return Block($"✗ once 账本不可用；动作批次已阻断：{ex.Message}", BlockReason.LedgerUnavailable);
        }
    }

    /// <summary>生产与回归共用的步骤链：success skip 可继续；一旦 blocked，后续副作用为零。</summary>
    public static async Task<ExecutionResult> RunStepsAsync(PackAutoAction action, bool fromPack,
        PackOnceLedger.Session? ledger, Func<PackActionStep, Task<string>> dispatch,
        Func<bool> isCurrent, Action<string>? report = null)
    {
        var outcomes = new List<string>();
        ExecutionResult Stop(string note, BlockReason reason)
        {
            outcomes.Add(note);
            report?.Invoke($"[{action.Name}] {note}");
            return Block(string.Join("；", outcomes), reason);
        }
        for (int index = 0; index < action.Steps.Count; index++)
        {
            if (!isCurrent()) return Stop("✗ 上下文已变化；动作和后续批次已阻断。", BlockReason.ContextChanged);
            PackActionStep step = action.Steps[index];
            try
            {
                string outcome;
                if (RequiresOnce(fromPack, step))
                {
                    if (ledger is null) return Stop("✗ once 账本缺失；动作已阻断。", BlockReason.LedgerUnavailable);
                    var result = await ledger.ExecuteAsync(StableKey(action.Name, index, step), LegacyKey(step), async () =>
                    {
                        if (!isCurrent()) return "✗ 上下文已变化；未执行。";
                        string note = await dispatch(step);
                        return isCurrent() ? note : "✗ 执行期间上下文已变化，可能部分完成；保留 pending。";
                    });
                    if (result.Reservation == PackOnceLedger.Reservation.Pending)
                        return Stop($"✗ {step.Action}：pending/可能部分执行；需显式核验迁移，当前动作及批次已阻断。", BlockReason.Pending);
                    if (result.Reservation == PackOnceLedger.Reservation.NeedsMigration)
                        return Stop($"✗ {step.Action}：未知逻辑身份/旧 raw 无法精确迁移；不能推断已完成，当前动作及批次已阻断。", BlockReason.NeedsMigration);
                    outcome = result.Reservation == PackOnceLedger.Reservation.Success
                        ? $"{step.Action}（once）：已完成，跳过" : result.Outcome!;
                }
                else outcome = await dispatch(step);
                if (!isCurrent()) return Stop("✗ 上下文已变化；后续动作已阻断。", BlockReason.ContextChanged);
                if (!IsSuccessfulOutcome(outcome)) return Stop(outcome, BlockReason.Failed);
                outcomes.Add(outcome);
                report?.Invoke($"[{action.Name}] {outcome}");
            }
            catch (Exception ex)
            {
                return Stop($"✗ {step.Action}：{ex.Message}；once pending 不自动重试，当前动作及批次已阻断。", BlockReason.Failed);
            }
        }
        return new ExecutionResult(string.Join("；", outcomes), false, BlockReason.None);
    }

    public static async Task<List<ExecutionResult>> RunBatchAsync(IEnumerable<PackAutoAction> actions,
        Func<PackAutoAction, Task<ExecutionResult>> execute)
    {
        var results = new List<ExecutionResult>();
        foreach (PackAutoAction action in actions)
        {
            ExecutionResult result = await execute(action);
            results.Add(result);
            if (result.Blocked) break;
        }
        return results;
    }

    // 已捕获的包动作即使与新的 auto.json 不匹配，也绝不能降级为可重复内置动作。
    public static bool TryResolveOrigin(bool? declaredPackAction, bool matchesCurrentPack, out bool fromPack)
    {
        fromPack = declaredPackAction != false;
        return !fromPack || matchesCurrentPack;
    }

    public static bool IsPackAction(PackAutoAction action, IEnumerable<PackAutoAction> packActions) =>
        packActions.Any(candidate => candidate.Name == action.Name
            && candidate.RunOnLaunch == action.RunOnLaunch
            && candidate.Steps.Count == action.Steps.Count
            && candidate.Steps.Zip(action.Steps).All(pair =>
                pair.First.Action == pair.Second.Action
                && pair.First.Raw.GetRawText() == pair.Second.Raw.GetRawText()));

    public static bool RequiresOnce(bool isPackAction, PackActionStep step) =>
        isPackAction || step.GetBool("once") == true;

    public static bool IsSuccessfulOutcome(string? outcome) =>
        PackActionOutcome.ShouldMarkOnce(outcome)
        && !outcome!.Contains('✗') && !outcome.Contains("失败", StringComparison.Ordinal)
        && !outcome.Contains("部分", StringComparison.Ordinal)
        && !outcome.Contains("partial", StringComparison.OrdinalIgnoreCase);

    public static string StableKey(string actionName, int stepIndex, PackActionStep step)
    {
        string type = step.Action.Trim().ToLowerInvariant();
        string identity = !string.IsNullOrWhiteSpace(step.GetString("onceId"))
            ? "id:" + step.GetString("onceId")
            : "index:" + stepIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"logical:{actionName.Length}:{actionName}:{identity.Length}:{identity}:{type}";
    }

    public static string LegacyKey(PackActionStep step) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            step.Action.Trim().ToLowerInvariant() + '\n' + step.Raw.GetRawText())))
            .ToLowerInvariant()[..16];

    public static bool ConsentContextMatches(bool alive, long capturedEpoch, long currentEpoch,
        string capturedGame, string currentGame, string? capturedRoot, string? currentRoot,
        string capturedHash, string currentHash) =>
        alive && capturedEpoch == currentEpoch && capturedGame.Length > 0
        && capturedGame == currentGame && !string.IsNullOrWhiteSpace(capturedRoot)
        && string.Equals(capturedRoot, currentRoot, StringComparison.OrdinalIgnoreCase)
        && capturedHash.Length > 0 && capturedHash == currentHash;
}
