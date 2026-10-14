using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class UiRuntimeTests
{
    public static async Task<(int Passed, int Failed)> RunAsync()
    {
        int passed = 0, failed = 0;
        void Check(bool condition, string name)
        {
            if (condition) { passed++; Console.WriteLine("[PASS] UI " + name); }
            else { failed++; Console.WriteLine("[FAIL] UI " + name); }
        }

        var lifetime = new UiLifetime();
        Check(!lifetime.IsActive && lifetime.Token.IsCancellationRequested, "unattached work is canceled");
        Check(!lifetime.Exit(), "duplicate unload is harmless");
        Check(lifetime.Enter(), "initial load enters");
        long first = lifetime.Generation;
        CancellationToken firstToken = lifetime.Token;
        Check(!lifetime.Enter() && lifetime.Generation == first, "duplicate load does not replace ownership");
        Check(lifetime.IsCurrent(first), "live attachment owns callbacks");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(firstToken);
        Check(lifetime.Exit() && firstToken.IsCancellationRequested && linked.IsCancellationRequested,
            "unload cancels linked queries");
        Check(!lifetime.IsCurrent(first), "unloaded callbacks are rejected");
        Check(lifetime.Enter() && !lifetime.Token.IsCancellationRequested, "reload has a fresh cancellation scope");
        Check(!lifetime.IsCurrent(first), "reload rejects callbacks from its prior attachment");
        lifetime.Exit();

        var operations = new LatestAsyncOperation();
        using var old = operations.Begin();
        int cancellations = 0;
        using var registration = old.Token.Register(() => cancellations++);
        using var current = operations.Begin();
        Check(old.Token.IsCancellationRequested && cancellations == 1, "replacement cancels previous query once");
        Check(!old.IsCurrent && current.IsCurrent, "only latest query may publish");
        old.Dispose();
        Check(current.IsCurrent && !current.Token.IsCancellationRequested, "old disposal preserves new ownership");

        bool busy = true;
        int displayed = 0;
        var oldCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newCompletion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Complete(LatestAsyncOperation.Lease lease, Task<int> value)
        {
            try
            {
                int result = await value;
                if (lease.IsCurrent) displayed = result;
            }
            finally { if (lease.IsCurrent) busy = false; }
        }
        Task oldTask = Complete(old, oldCompletion.Task);
        Task newTask = Complete(current, newCompletion.Task);
        oldCompletion.SetResult(1);
        await oldTask;
        Check(displayed == 0 && busy, "stale completion preserves latest result and busy indicator");
        newCompletion.SetResult(2);
        await newTask;
        Check(displayed == 2 && !busy, "latest completion publishes and clears busy indicator");
        operations.Cancel();
        Check(!current.IsCurrent && current.Token.IsCancellationRequested, "owner cancellation stops active query");
        using var parent = new CancellationTokenSource();
        using var child = operations.Begin(parent.Token);
        parent.Cancel();
        Check(child.Token.IsCancellationRequested && !child.IsCurrent, "page unload reaches nested background work");
        using var alreadyCanceled = operations.Begin(new CancellationToken(canceled: true));
        Check(!alreadyCanceled.IsCurrent, "canceled attachment cannot claim query ownership");
        operations.Cancel();
        operations.Cancel();
        Check(true, "repeated cancellation is harmless");

        var slotA = new object();
        var slotB = new object();
        var presentation = new PresentationState<object>(slotA, slotB);
        var animationA = presentation.Begin();
        Check(ReferenceEquals(animationA.Outgoing, slotA) && ReferenceEquals(animationA.Incoming, slotB),
            "initial animation uses distinct presentation slots");
        Check(ReferenceEquals(presentation.CommitPending(), animationA) && ReferenceEquals(presentation.Current, slotB),
            "interrupted animation promotes the requested page");
        var animationB = presentation.Begin();
        Check(!presentation.Complete(animationA) && ReferenceEquals(presentation.Current, slotB),
            "late old animation cannot swap or clear the new page");
        Check(presentation.Complete(animationB) && ReferenceEquals(presentation.Current, slotA),
            "current animation completes once");
        Check(!presentation.Complete(animationB), "duplicate animation completion is ignored");
        Check(presentation.CommitPending() is null, "immediate navigation has no leftover transition");
        var animationC = presentation.Begin();
        var copied = new PresentationState<object>.Transition(animationC.Outgoing, animationC.Incoming);
        Check(!presentation.Complete(copied), "equal-valued callback does not acquire transition ownership");
        presentation.CommitPending();
        bool ordered = true;
        for (int i = 0; i < 200; i++)
        {
            var interrupted = presentation.Begin();
            presentation.CommitPending();
            var latest = presentation.Begin();
            ordered &= !presentation.Complete(interrupted) && presentation.Complete(latest);
        }
        Check(ordered && ReferenceEquals(presentation.Current, slotB), "200 rapid replacement cycles preserve final ownership");
        try { _ = new PresentationState<object>(slotA, slotA); Check(false, "duplicate slots rejected"); }
        catch (ArgumentException) { Check(true, "duplicate slots rejected"); }
        lifetime.Enter();
        using var pageQuery = operations.Begin(lifetime.Token);
        lifetime.Exit();
        Check(pageQuery.Token.IsCancellationRequested && !pageQuery.IsCurrent, "combined lifecycle and query owner cancel on unload");
        Check(lifetime.Enter() && !lifetime.Token.IsCancellationRequested, "combined scopes can reattach after cancellation");
        lifetime.Exit();

        Console.WriteLine($"UI runtime tests: PASS {passed} / FAIL {failed}; no real UI or game started.");
        return (passed, failed);
    }
}
