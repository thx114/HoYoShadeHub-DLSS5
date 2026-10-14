using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class OptiPageLoadingTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            passed++;
            Console.WriteLine("[PASS] Opti page " + message);
        }
        string root = Path.Combine(Path.GetTempPath(), "hoyoshade-opti-page-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string build = Path.Combine(root, "test", "v1");
            Directory.CreateDirectory(build);
            File.WriteAllText(Path.Combine(build, "build.json"), "{\"sourceId\":\"test\",\"version\":\"v1\"}");
            File.WriteAllBytes(Path.Combine(build, "OptiScaler.dll"), new byte[32]);
            File.WriteAllText(Path.Combine(root, "state.json"), "{\"selected\":\"test/v1\"}");
            var library = new OptiScalerLibrary(root);
            var full = library.List();
            var light = library.List(includeDirectorySize: false);
            Check(full.Count == 1 && full[0].SizeBytes > 0, "default listing retains size calculation");
            Check(light.Count == 1 && light[0].SizeBytes == 0 && light[0].DllPath == full[0].DllPath,
                "page listing preserves build identity without recursive size scan");
            Check(library.GetSelected(false)?.Id == "test/v1", "lightweight legacy selection preserves identity");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { library.List(false, canceled.Token); Check(false, "canceled enumeration rejected"); }
            catch (OperationCanceledException) { Check(true, "canceled enumeration rejected"); }
            Check(new OptiScalerLibrary(Path.Combine(root, "missing")).List(false).Count == 0,
                "missing library is an empty page");
            using (File.Open(Path.Combine(build, "build.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                try { library.List(false); Check(false, "locked manifest reports failure"); }
                catch (IOException) { Check(true, "locked manifest reports failure"); }
            }

            // WinUI is not hosted here; these are source contracts protecting the
            // exact initialization/reentrant refresh bug, separate from UI acceptance.
            string? repo = Directory.GetCurrentDirectory();
            while (repo is not null && !File.Exists(Path.Combine(repo, "src", "HoYoShadeHub", "Features", "OptiScaler", "OptiScalerPage.xaml.cs")))
                repo = Directory.GetParent(repo)?.FullName;
            if (repo is null) throw new DirectoryNotFoundException("Run from the repository for page source contracts.");
            string page = File.ReadAllText(Path.Combine(repo, "src", "HoYoShadeHub", "Features", "OptiScaler", "OptiScalerPage.xaml.cs"));
            string delay = page[page.IndexOf("private void UpdateOptiInjectDelayUi()")..page.IndexOf("private void NumberBox_OptiInjectDelay_ValueChanged")];
            string attach = page[page.IndexOf("private void AttachPreset(")..page.IndexOf("private void OnPresetChanged(")];
            string change = page[page.IndexOf("private void OnPresetChanged(")..page.IndexOf("#region 注入用的 DLL 名字")];
            Check(delay.Contains("bool wasLoading = _loading;") && delay.Contains("_loading = wasLoading;"),
                "nested control initialization retains outer event suppression (source contract)");
            Check(attach.Contains("_loading = true;") && attach.Contains("finally { _loading = wasLoading; }"),
                "preset attachment suppresses callbacks and restores prior state (source contract)");
            Check(!change.Contains("Load();") && change.Contains("AttachPreset(item);"),
                "profile conflict cannot recursively load the page (source contract)");
            Check(page.Contains("await Task.Run(") && page.Contains("if (!operation.IsCurrent")
                && page.Contains("_loadOperation.Cancel();"),
                "background loading rejects stale completions and cancels on unload (source contract)");
            Console.WriteLine($"Opti page loading tests: {passed} PASS / 0 FAIL; RealUiTested=false.");
            return 0;
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
