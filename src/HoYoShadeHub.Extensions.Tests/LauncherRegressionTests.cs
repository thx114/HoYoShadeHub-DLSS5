namespace HoYoShadeHub.Extensions.Tests;

internal static class LauncherRegressionTests
{
    public static async Task<int> RunAsync()
    {
        int passed = 0, failed = 0;
        async Task Run(string name, Func<Task> test)
        {
            try
            {
                await test();
                Console.WriteLine($"[PASS] {name}");
                passed++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] {name}: {ex}");
                failed++;
            }
        }

        static Task Check((int Passed, int Failed) result)
        {
            if (result.Failed != 0) throw new InvalidOperationException($"{result.Failed} assertions failed.");
            return Task.CompletedTask;
        }

        await Run("Launch reservation", () => Check(GameLaunchReservationTests.Run()));
        await Run("Page request lifetime", async () => { await LatestRequestGateTests.RunAsync(); });
        await Run("Profile integrity", () => { OptiScalerProfileIntegrityTests.Run(); return Task.CompletedTask; });
        await Run("Preset selection", () => { OptiScalerPresetSelectionTests.Run(); return Task.CompletedTask; });
        await Run("Package installation", PackageInstallSafetyTests.RunAsync);
        await Run("Download protocol", async () =>
        {
            if (await DownloadServiceTests.RunAsync() != 0) throw new InvalidOperationException("Download tests failed.");
        });
        await Run("Mod transaction", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "hys-mod-regression-" + Guid.NewGuid().ToString("N"));
            try
            {
                if (ModInstallTransactionTests.Run(root) != 0) throw new InvalidOperationException("Mod tests failed.");
            }
            finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            return Task.CompletedTask;
        });
        await Run("Consent and pending migration", () => { PackConsentMigrationTests.Run(); return Task.CompletedTask; });
        await Run("Setting persistence", () => Check(SettingPersistenceTests.Run()));
        await Run("XXMI ownership and cancellation", () => Check(XxmiLauncherSafetyTests.Run()));
        await Run("Quick config options", () => { QuickConfigOptionTests.Run(); return Task.CompletedTask; });
        Console.WriteLine($"Launcher regression suites: PASS {passed} / FAIL {failed}");
        return failed == 0 ? 0 : 1;
    }
}
