using HoYoShadeHub.Extensions.Games;

namespace HoYoShadeHub.Extensions.Tests;

internal static class GameLaunchReservationTests
{
    public static (int Passed, int Failed) Run()
    {
        int passed = 0, failed = 0;
        void Check(bool ok, string name)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            if (ok) passed++; else failed++;
        }

        string key = "launch-test-" + Guid.NewGuid();
        var owner = GameLaunchReservation.Acquire(key);
        int rejected = 0;
        Parallel.For(0, 16, _ =>
        {
            try { using var duplicate = GameLaunchReservation.Acquire(key.ToUpperInvariant()); }
            catch (InvalidOperationException) { Interlocked.Increment(ref rejected); }
        });
        Check(rejected == 16, "Concurrent entry points cannot acquire the same game");
        using (var other = GameLaunchReservation.Acquire(key + "-other"))
        {
            other.Validate(key + "-other");
            Check(true, "Different games acquire independently");
        }
        bool mismatched = false;
        try { owner.Validate(key + "-other"); }
        catch (InvalidOperationException) { mismatched = true; }
        Check(mismatched, "Reservation cannot create a different game");

        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool prevented = false;
        try { owner.Validate(key, canceled.Token); }
        catch (OperationCanceledException) { prevented = true; }
        Check(prevented, "Cancellation before creation rejects the owner");
        bool canceledAcquire = false;
        try { using var canceledOwner = GameLaunchReservation.Acquire(key + "-canceled", canceled.Token); }
        catch (OperationCanceledException) { canceledAcquire = true; }
        using (var fresh = GameLaunchReservation.Acquire(key + "-canceled"))
            Check(canceledAcquire, "Canceled acquisition does not retain ownership");

        owner.Dispose();
        using var replacement = GameLaunchReservation.Acquire(key);
        owner.Dispose();
        bool staleRejected = false;
        try { owner.Validate(key); }
        catch (InvalidOperationException) { staleRejected = true; }
        replacement.Validate(key);
        Check(staleRejected, "Stale ownership stays invalid after a new launch");
        bool oldDisposeSafe = false;
        try { using var third = GameLaunchReservation.Acquire(key); }
        catch (InvalidOperationException) { oldDisposeSafe = true; }
        Check(oldDisposeSafe, "Repeated old cleanup cannot release the replacement");

        string exceptionKey = key + "-exception";
        try
        {
            using var faulting = GameLaunchReservation.Acquire(exceptionKey);
            throw new IOException("Simulated launch preparation failure");
        }
        catch (IOException) { }
        using (var retry = GameLaunchReservation.Acquire(exceptionKey))
            Check(true, "Preparation exceptions release ownership for retry");
        return (passed, failed);
    }
}
