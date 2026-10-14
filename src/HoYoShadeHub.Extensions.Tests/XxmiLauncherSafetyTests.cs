using HoYoShadeHub.Extensions.Games;

namespace HoYoShadeHub.Extensions.Tests;

internal static class XxmiLauncherSafetyTests
{
    public static (int Passed, int Failed) Run()
    {
        int passed = 0, failed = 0;
        void Check(bool ok, string name)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            if (ok) passed++; else failed++;
        }
        var start = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc);
        var identity = new XxmiOwnedLauncher(42, start, Path.GetFullPath("XXMI Launcher.exe"));
        Check(XxmiLauncherSafety.Matches(identity, 42, start, identity.ExecutablePath), "matching owned launcher accepted");
        Check(!XxmiLauncherSafety.Matches(identity, 43, start, identity.ExecutablePath), "foreign PID rejected");
        Check(!XxmiLauncherSafety.Matches(identity, 42, start.AddSeconds(1), identity.ExecutablePath), "reused PID rejected");
        Check(!XxmiLauncherSafety.Matches(identity, 42, start, Path.GetFullPath("Other/XXMI Launcher.exe")), "other installation rejected");
        int calls = 0;
        int result = XxmiLauncherSafety.PollAsync(seconds => { calls++; return calls == 2 ? 0 : 1; },
            TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
        Check(result == 0 && calls == 2, "native timeout retried until success");
        using var cancel = new CancellationTokenSource();
        bool nativeFinished = false, cancelled = false;
        try
        {
            XxmiLauncherSafety.PollAsync(seconds =>
            {
                cancel.Cancel();
                Thread.Sleep(20);
                nativeFinished = true;
                return 1;
            }, TimeSpan.FromSeconds(10), cancel.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && nativeFinished, "cancellation waits for native return before cleanup");
        result = XxmiLauncherSafety.PollAsync(_ => 200, TimeSpan.FromSeconds(10), CancellationToken.None).GetAwaiter().GetResult();
        Check(result == 200, "unexpected native errors are not retried");
        return (passed, failed);
    }
}
