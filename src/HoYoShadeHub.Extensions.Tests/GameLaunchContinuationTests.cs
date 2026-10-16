using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Tests;

internal static class GameLaunchContinuationTests
{
    public static async Task<int> RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "hys-launch-continue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int checks = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception(name);
            checks++;
        }
        try
        {
            var actions = PackAutoActionFile.Parse("""
                {"name":"setup","runOnLaunch":true,"steps":[
                    {"action":"set_addons"},{"action":"set_ini_keys"},{"action":"set_launch_option"}]}
                """);
            var action = actions[0];
            var keys = PackActionOncePolicy.PlanKeys(actions).ToArray();
            using var ledger = await PackOnceLedger.OpenAsync(root);
            ledger.RegisterPlan(keys);
            ledger.Reserve(keys[0], "old-addon");
            ledger.Complete(keys[0]);
            ledger.Reserve(keys[1], "old-ini");
            string before = File.ReadAllText(Path.Combine(root, PackOnceLedger.FileName));
            int dispatches = 0, starts = 0;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var result = await PackActionOncePolicy.RunStepsAsync(action, true, ledger,
                    _ => { dispatches++; return Task.FromResult("unexpected"); }, () => true);
                Check(result.Blocked && result.Reason == PackActionOncePolicy.BlockReason.Pending,
                    "Old pending keeps the package batch stopped");
                if (GameLaunchContinuation.AfterPackActions(result, contextCurrent: true)) starts++;
            }
            Check(dispatches == 0 && starts == 2, "Repeated normal launch proceeds without replaying package actions");
            Check(File.ReadAllText(Path.Combine(root, PackOnceLedger.FileName)) == before,
                "Launch never resets or completes the old pending ledger");
            foreach (var reason in Enum.GetValues<PackActionOncePolicy.BlockReason>())
            {
                var result = PackActionOncePolicy.Block("fixture", reason);
                Check(GameLaunchContinuation.AfterPackActions(result, true)
                    == (reason != PackActionOncePolicy.BlockReason.ContextChanged), "Launch status: " + reason);
                Check(!GameLaunchContinuation.AfterPackActions(result, false), "Cancelled context: " + reason);
            }
            Console.WriteLine($"PASS game launch continuation ({checks} checks); no game started.");
            return 0;
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
