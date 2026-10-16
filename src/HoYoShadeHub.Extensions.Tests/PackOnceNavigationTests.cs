using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Tests;

/// <summary>All mutations and fake dispatches are confined to an owned temporary directory.</summary>
internal static class PackOnceNavigationTests
{
    private static readonly string TempPrefix = Path.Combine(Path.GetTempPath(), "hys-pack-once-tests-");
    public static async Task<int> RunAsync(string[] args)
    {
        if (args[0] == "pack-once-worker") return await WorkerAsync(args);
        string root = TempPrefix + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; }
        string Fixture(string name, string? json = null)
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            if (json is not null) File.WriteAllText(Path.Combine(dir, PackOnceLedger.FileName), json);
            return dir;
        }
        async Task Throws(Func<Task> call, string name)
        {
            bool threw = false;
            try { await call(); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { threw = true; }
            Check(threw, name);
        }
        try
        {
            PackActionStep Step(string raw) => PackAutoActionFile.Parse("{\"name\":\"setup\",\"steps\":[" + raw + "]}")[0].Steps[0];
            PackActionStep missing = Step("{\"action\":\"set_addons\",\"enabled\":false}");
            PackActionStep disabledOnce = Step("{\"action\":\"set_addons\",\"once\":false}");
            Check(PackActionOncePolicy.RequiresOnce(true, missing), "Pack missing once is forced");
            Check(PackActionOncePolicy.RequiresOnce(true, disabledOnce), "Pack false once is forced");
            Check(!PackActionOncePolicy.RequiresOnce(false, missing), "Manual builtin remains repeatable despite PackRoot");
            Check(!PackActionOncePolicy.RequiresOnce(false, disabledOnce), "Non-pack false once remains repeatable");
            Check(PackActionOncePolicy.RequiresOnce(false, Step("{\"action\":\"set_ini\",\"once\":true}")), "Explicit non-pack once supported");
            PackActionStep oldPath = Step("{\"action\":\"set_opt_build\",\"build\":\"D:\\\\build-old\"}");
            PackActionStep newPath = Step("{\"action\":\"SET_OPT_BUILD\",\"build\":\"E:\\\\build-new\",\"once\":false}");
            string key = PackActionOncePolicy.StableKey("setup", 0, oldPath);
            Check(key == PackActionOncePolicy.StableKey("setup", 0, newPath), "Stable logical key ignores raw/build paths/case");
            Check(key != PackActionOncePolicy.StableKey("setup", 1, oldPath), "Index separates steps");
            Check(key != PackActionOncePolicy.StableKey("other", 0, oldPath), "Action name separates steps");
            Check(key != PackActionOncePolicy.StableKey("setup", 0, missing), "Type separates steps");
            var id = Step("{\"action\":\"set_ini\",\"onceId\":\"durable\"}");
            Check(PackActionOncePolicy.StableKey("setup", 0, id) == PackActionOncePolicy.StableKey("setup", 7, id), "Explicit onceId survives index movement");
            var actions = PackAutoActionFile.Parse("{\"actions\":[{\"name\":\"pack\",\"steps\":[{\"action\":\"set_addons\",\"once\":false}]}]}");
            Check(PackActionOncePolicy.IsPackAction(actions[0], actions), "auto.json provenance detected by action, not root");
            var builtin = PackAutoActionFile.Parse("{\"name\":\"manual\",\"steps\":[{\"action\":\"set_addons\"}]}")[0];
            Check(!PackActionOncePolicy.IsPackAction(builtin, actions), "Builtin is not pack action");
            var changedActions = PackAutoActionFile.Parse("{\"actions\":[{\"name\":\"pack\",\"steps\":[{\"action\":\"set_ini\"}]}]}");
            bool staleMatch = PackActionOncePolicy.IsPackAction(actions[0], changedActions);
            Check(!staleMatch && !PackActionOncePolicy.TryResolveOrigin(true, staleMatch, out bool staleFromPack)
                && staleFromPack, "Stale loaded pack/auto drift cannot downgrade to builtin even with once=false");
            Check(PackActionOncePolicy.TryResolveOrigin(false, false, out bool builtinFromPack) && !builtinFromPack,
                "Explicit manual builtin remains non-pack");
            int manualCalls = 0;
            for (int i = 0; i < 3; i++) if (!PackActionOncePolicy.RequiresOnce(false, missing)) manualCalls++;
            Check(manualCalls == 3, "Manual builtin can run repeatedly");

            bool Context(bool alive = true, long epoch = 1, string game = "g", string path = "p", string hash = "h") =>
                PackActionOncePolicy.ConsentContextMatches(alive, 1, epoch, "g", game, "p", path, "h", hash);
            Check(Context(), "Captured context accepted");
            Check(!Context(alive: false), "Unloaded page rejected");
            Check(!Context(epoch: 2), "Navigation/reentry rejected");
            Check(!Context(game: "other"), "Different game rejected");
            Check(!Context(path: "other"), "Different pack rejected");
            Check(!Context(hash: "new"), "New auto hash never implicitly consented");

            int calls = 0;
            string fresh = Fixture("fresh");
            Check(!File.Exists(Path.Combine(fresh, PackOnceLedger.FileName)), "New pack has no ledger");
            using (var session = await PackOnceLedger.OpenAsync(fresh))
            {
                Check(session.RegisterPlan(new[] { key }) == PackOnceLedger.PlanRegistration.Registered, "Fresh full plan persisted first");
                var execution = await session.ExecuteAsync(key, PackActionOncePolicy.LegacyKey(oldPath), () =>
                {
                    Check(File.ReadAllText(Path.Combine(fresh, PackOnceLedger.FileName)).Contains("pending"), "Pending durable before dispatch");
                    calls++; return Task.FromResult("已完成");
                });
                Check(execution.Reservation == PackOnceLedger.Reservation.Reserved && calls == 1, "Fresh pack runs once");
            }
            PackOnceLedger.ApplyFence(fresh, "different-identity|different-auto");
            using (var session = await PackOnceLedger.OpenAsync(fresh))
            {
                var execution = await session.ExecuteAsync(PackActionOncePolicy.StableKey("setup", 0, newPath), "changed-raw", () =>
                { calls++; return Task.FromResult("unexpected"); });
                Check(execution.Reservation == PackOnceLedger.Reservation.Success && calls == 1, "Changed fence/auto/raw/path never reruns completed logical key");
            }
            string legacy = PackActionOncePolicy.LegacyKey(missing);
            foreach (bool flat in new[] { true, false })
            {
                string json = JsonSerializer.Serialize(new Dictionary<string, bool> { [legacy] = true, ["old-false"] = false });
                if (!flat) json = "{\"schema\":2,\"fence\":\"old\",\"entries\":" + json + "}";
                string dir = Fixture(flat ? "flat" : "schema2", json);
                Check(PackOnceLedger.ApplyFence(dir, "new"), "Fence changes without deleting old " + (flat ? "flat" : "schema2"));
                Check(PackOnceLedger.Has(dir, legacy), "Legacy true retained");
                using var session = await PackOnceLedger.OpenAsync(dir);
                var migrated = await session.ExecuteAsync("logical-existing", legacy, () => throw new Exception("Must not dispatch legacy"));
                Check(migrated.Reservation == PackOnceLedger.Reservation.Success, "Exact raw legacy migration skips dispatch");
                Check(session.Reserve("logical-unknown", "changed-raw") == PackOnceLedger.Reservation.NeedsMigration, "Unmatched legacy conservatively blocked");
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, PackOnceLedger.FileName)));
                Check(doc.RootElement.GetProperty("entries").GetProperty(legacy).GetBoolean(), "Old raw entry preserved after migration");
                Check(!doc.RootElement.GetProperty("entries").GetProperty("old-false").GetBoolean(), "Old false entry preserved");
            }
            string stableMap = Fixture("stable-map", "{\"schema\":2,\"fence\":\"\",\"entries\":{\"logical-known\":true}}");
            using (var session = await PackOnceLedger.OpenAsync(stableMap))
                Check(session.Reserve("logical-known", "unmatched") == PackOnceLedger.Reservation.Success, "Existing stable key wins without guessing raw");

            foreach (string bad in new[] { "{", "[]", "null", "{\"x\":\"true\"}", "{\"schema\":99,\"fence\":\"\",\"entries\":{}}",
                "{\"schema\":2,\"fence\":\"\",\"entries\":{\"x\":true,\"x\":false}}", "{\"schema\":3,\"fence\":\"\",\"entries\":{},\"states\":{\"x\":\"unknown\"}}" })
            {
                string dir = Fixture("bad-" + passed, bad);
                await Throws(async () => { using var session = await PackOnceLedger.OpenAsync(dir); }, "Malformed ledger fails closed");
                Check(File.ReadAllText(Path.Combine(dir, PackOnceLedger.FileName)) == bad, "Malformed ledger not overwritten");
            }
            string blocked = Fixture("unreadable");
            Directory.CreateDirectory(Path.Combine(blocked, PackOnceLedger.FileName));
            await Throws(async () => { using var session = await PackOnceLedger.OpenAsync(blocked); }, "Unreadable ledger is not missing ledger");
            string unwritable = Fixture("unwritable");
            using (var session = await PackOnceLedger.OpenAsync(unwritable))
            {
                session.RegisterPlan(new[] { "write-fail" });
                using var hold = new FileStream(Path.Combine(unwritable, PackOnceLedger.FileName), FileMode.Open, FileAccess.Read, FileShare.None);
                await Throws(async () => { await session.ExecuteAsync("write-fail", "raw", () => { calls++; return Task.FromResult("已完成"); }); }, "Reservation write failure blocks dispatch");
                Check(calls == 1, "No side effect when reservation cannot persist");
            }
            foreach (string outcome in new[] { "✗ 失败", "已复制一项；✗ 另一项失败", "可能部分完成", "partial fail" })
            {
                string dir = Fixture("partial-" + passed);
                using (var session = await PackOnceLedger.OpenAsync(dir))
                {
                    session.RegisterPlan(new[] { "partial" });
                    await session.ExecuteAsync("partial", "raw", () => Task.FromResult(outcome));
                }
                using (var session = await PackOnceLedger.OpenAsync(dir))
                    Check(session.Reserve("partial", "raw") == PackOnceLedger.Reservation.Pending, "Partial outcome remains pending: " + outcome);
            }
            string exception = Fixture("exception");
            using (var session = await PackOnceLedger.OpenAsync(exception))
            {
                session.RegisterPlan(new[] { "exception" });
                await Throws(async () => { await session.ExecuteAsync("exception", "raw", () => throw new IOException("Fake partial exception")); }, "Dispatch exception propagated");
            }
            using (var session = await PackOnceLedger.OpenAsync(exception))
                Check(session.Reserve("exception", "raw") == PackOnceLedger.Reservation.Pending, "Exception cannot auto retry");
            string completeFail = Fixture("complete-fail");
            using (var session = await PackOnceLedger.OpenAsync(completeFail))
            {
                session.RegisterPlan(new[] { "complete" });
                session.Reserve("complete", "raw");
                using var hold = new FileStream(Path.Combine(completeFail, PackOnceLedger.FileName), FileMode.Open, FileAccess.Read, FileShare.None);
                await Throws(() => { session.Complete("complete"); return Task.CompletedTask; }, "Success persistence failure propagated");
            }
            using (var session = await PackOnceLedger.OpenAsync(completeFail))
                Check(session.Reserve("complete", "raw") == PackOnceLedger.Reservation.Pending, "Failed success write leaves disk pending");

            string concurrent = Fixture("concurrent"); int parallelCalls = 0;
            await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
            {
                using var session = await PackOnceLedger.OpenAsync(concurrent);
                session.RegisterPlan(new[] { "same" });
                await session.ExecuteAsync("same", "raw", async () => { Interlocked.Increment(ref parallelCalls); await Task.Delay(30); return "已完成"; });
            }));
            Check(parallelCalls == 1, "Eight concurrent callers dispatch only once");
            using (var session = await PackOnceLedger.OpenAsync(concurrent))
                await Throws(async () => { using var other = await PackOnceLedger.OpenAsync(concurrent, TimeSpan.FromMilliseconds(50)); }, "Lock timeout fails closed");
            string processes = Fixture("processes");
            using (Process first = StartWorker(processes, "normal"))
            using (Process second = StartWorker(processes, "normal"))
            {
                await Task.WhenAll(first.WaitForExitAsync(), second.WaitForExitAsync());
                Check(first.ExitCode == 0 && second.ExitCode == 0, "Two offline worker processes exit successfully");
            }
            Check(File.ReadAllLines(Path.Combine(processes, "fake-dispatch.txt")).Length == 1, "Cross-process dispatch exactly once");
            string crash = Fixture("crash");
            using (Process worker = StartWorker(crash, "crash"))
            { await worker.WaitForExitAsync(); Check(worker.ExitCode == 23, "Offline worker crash simulated after pending + partial dispatch"); }
            using (Process worker = StartWorker(crash, "normal"))
            { await worker.WaitForExitAsync(); Check(worker.ExitCode == 0, "Crash lock released by OS"); }
            Check(File.ReadAllLines(Path.Combine(crash, "fake-dispatch.txt")).Length == 1, "Pending prevents dispatch by later process");

            await ReviewSafetyAsync(root, Check);
            await SnapshotBindingAsync(root, Check);
            if (args.Length >= 2) await SnapshotAsync(Path.GetFullPath(args[1]), root, Check);
            if (args.Length >= 3) SourceContracts(Path.GetFullPath(args[2]), Check);
            Console.WriteLine($"Pack once/navigation tests: PASS {passed} / FAIL 0; offline temporary files only; no game/UI started.");
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("Pack once/navigation tests FAILED: " + ex); return 1; }
        finally
        {
            if (Path.GetFullPath(root).StartsWith(TempPrefix, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task SnapshotBindingAsync(string tempRoot, Action<bool, string> check)
    {
        const string a = """
            {"actions":[{"name":"pack","runOnLaunch":true,"steps":[
              {"action":"set_addons","enabled":true,"once":false},
              {"action":"set_opt_build","build":"A"}]},
              {"name":"second","steps":[{"action":"set_ini","value":"A"}]}]}
            """;
        string b = a.Replace("\"enabled\":true", "\"enabled\":false").Replace("\"build\":\"A\"", "\"build\":\"B\"");
        string bNewPlan = b.Replace("\"set_opt_build\"", "\"set_window_mode\"");
        byte[] bytesA = Encoding.UTF8.GetBytes(a), bytesB = Encoding.UTF8.GetBytes(b);
        string hashA = Convert.ToHexString(SHA256.HashData(bytesA));
        string Dir(string name)
        {
            string root = Path.Combine(tempRoot, "snapshot-binding-" + name);
            Directory.CreateDirectory(root);
            return root;
        }
        string Auto(string root) => Path.Combine(root, GameAddonPackUserContent.AutoActionFileName);
        bool NoHistory(string root) => !File.Exists(Path.Combine(root, PackOnceLedger.FileName));
        string single = Dir("single-read");
        int reads = 0;
        var snapshot = PackAutoActionSnapshot.TryLoad(single, _ => { reads++; return reads == 1 ? bytesA : bytesB; })!;
        check(reads == 1 && snapshot.Hash == hashA && snapshot.LoadActions()[0].Steps[0].GetBool("enabled") == true,
            "One byte read derives actions and exact old hash, never A-actions/B-hash");
        var changedCopy = snapshot.LoadActions();
        changedCopy[0].Steps.Clear();
        changedCopy[1].Name = "mutated";
        check(snapshot.LoadActions()[0].Steps.Count == 2 && snapshot.LoadActions()[1].Name == "second",
            "Snapshot actions/whole plan immutable through returned models");
        check(!snapshot.IsCurrent(single, _ => bytesB), "Current compares against captured expected A, not freshly authorized B");
        foreach (byte[] bytes in new[] { bytesA, Encoding.UTF8.GetPreamble().Concat(bytesA).ToArray(),
            Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(a)).ToArray(), Encoding.UTF8.GetBytes(a + "\r\n ") })
        {
            var compatible = PackAutoActionSnapshot.TryLoad(single, _ => bytes)!;
            check(compatible.Hash == Convert.ToHexString(SHA256.HashData(bytes)), "Hash compatibility including BOM/encoding/whitespace");
            var step = compatible.LoadActions()[0].Steps[0];
            string oldRaw = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                step.Action.Trim().ToLowerInvariant() + '\n' + step.Raw.GetRawText()))).ToLowerInvariant()[..16];
            check(PackActionOncePolicy.LegacyKey(step) == oldRaw, "Legacy RawGetText hash unchanged");
        }
        foreach (string malformed in new[] { "", "{", "[]", "{}", "{\"actions\":[]}", "{\"actions\":[{}]}",
            "{\"steps\":[]}", "{\"steps\":[{\"action\":\"set_ini\"},{}]}", "{\"actions\":[{\"steps\":[{\"action\":\"x\"}]},null]}" })
            check(PackAutoActionSnapshot.TryLoad(single, _ => Encoding.UTF8.GetBytes(malformed)) is null,
                "Malformed/empty/partial parse has no authorizable snapshot");
        check(PackAutoActionSnapshot.TryLoad(single, _ => throw new IOException("fixture")) is null, "Snapshot IO failure fail closed");
        check(PackAutoActionSnapshot.TryLoad(single, _ => throw new UnauthorizedAccessException("fixture")) is null, "Snapshot unreadable fail closed");
        check(PackAutoActionSnapshot.TryLoad(single) is null, "Missing auto has no snapshot");
        var actionA = snapshot.LoadActions()[0];
        var snapshotB = PackAutoActionSnapshot.TryLoad(single, _ => bytesB)!;
        var actionB = snapshotB.LoadActions()[0];
        bool Bind(PackAutoAction action, bool? declared, PackAutoActionSnapshot? captured, string? expected) =>
            PackActionOncePolicy.TryBindSnapshot(action, declared, single, captured, expected, out _, out _, out _);
        check(Bind(actionA, true, snapshot, hashA), "Explicit pack binds same immutable snapshot");
        check(Bind(actionA, null, null, null), "CLI default preserves loaded origin even without context snapshot");
        check(!Bind(actionB, null, snapshot, hashA), "CLI stale loaded B never downgrades to builtin or authorizes with A");
        check(!Bind(actionA, false, snapshot, hashA), "Cannot relabel loaded pack action as manual builtin");
        check(!Bind(actionB, true, snapshot, hashA), "A consent plus separately loaded B action rejected even when file returns A");
        check(!Bind(actionA, true, snapshot, snapshotB.Hash), "Captured expectedHash mismatch rejected without live read");
        var manual = PackAutoActionFile.Parse("{\"steps\":[{\"action\":\"set_addons\"}]}")[0];
        check(Bind(manual, false, null, null), "Explicit manual builtin with packRoot remains independent");
        check(!Bind(manual, null, null, null), "Unknown CLI origin with packRoot fails closed, no matching-live fallback");
        check(!PackActionOncePolicy.TryBindSnapshot(manual, null, null, null, null, out _, out _, out _),
            "Unknown CLI origin without packRoot also fails closed; builtin must be explicitly declared");
        check(!PackActionOncePolicy.TryResolveOrigin(null, false, out bool unknownFromPack) && unknownFromPack,
            "Legacy origin helper no longer downgrades unknown default to builtin");
        check(!PackActionOncePolicy.TryBindSnapshot(actionA, null, null, null, null, out _, out _, out _), "Loaded origin cannot drop required root");

        // Same shared production gate; consent always belongs only to A, never B.
        foreach (bool addNewIdentity in new[] { false, true })
        {
            string root = Dir(addNewIdentity ? "ABA-new-plan" : "ABA-same-key");
            byte[][] sequence = [bytesA, Encoding.UTF8.GetBytes(addNewIdentity ? bNewPlan : b), bytesA];
            int next = 0, dispatch = 0;
            byte[] Reader(string _) => sequence[Math.Min(next++, sequence.Length - 1)];
            var captured = PackAutoActionSnapshot.TryLoad(root, Reader)!; // A: one read, both actions/hash.
            var result = await PackActionOncePolicy.RunSnapshotAsync(captured.LoadActions()[0], captured, root,
                () => captured.Hash == hashA, _ => { dispatch++; return Task.FromResult("unexpected"); }, readBytes: Reader); // B
            check(result.Blocked && dispatch == 0 && NoHistory(root), "Controlled A→B→A has plan/reservation/dispatch=0 for B");
            check(captured.IsCurrent(root, Reader), "ABA restoration checks original A, does not authorize captured B"); // A
        }
        foreach (bool addNewIdentity in new[] { false, true })
        {
            string root = Dir(addNewIdentity ? "BA-new-plan" : "BA-same-key");
            int next = 0, dispatch = 0;
            byte[] Reader(string _) => next++ == 0 ? Encoding.UTF8.GetBytes(addNewIdentity ? bNewPlan : b) : bytesA;
            var captured = PackAutoActionSnapshot.TryLoad(root, Reader)!; // B, later file=A.
            var result = await PackActionOncePolicy.RunSnapshotAsync(captured.LoadActions()[0], captured, root,
                () => captured.Hash == hashA, _ => { dispatch++; return Task.FromResult("unexpected"); }, readBytes: Reader);
            check(result.Blocked && dispatch == 0 && NoHistory(root), "Controlled B→A cannot replace captured B hash with consented A");
            check(!captured.IsCurrent(root, Reader), "B→A current fails captured expected hash");
        }
        string good = Dir("normal");
        File.WriteAllBytes(Auto(good), bytesA);
        var full = PackAutoActionSnapshot.TryLoad(good)!;
        int normalCalls = 0;
        var batch = await PackActionOncePolicy.RunBatchAsync(full.LoadActions(), action =>
            PackActionOncePolicy.RunSnapshotAsync(action, full, good, () => full.Hash == hashA, step =>
            {
                using var ledger = JsonDocument.Parse(File.ReadAllText(Path.Combine(good, PackOnceLedger.FileName)));
                check(ledger.RootElement.GetProperty("plan").GetArrayLength() == 3, "Initial plan includes all snapshot actions before first reservation");
                normalCalls++;
                return Task.FromResult("已完成");
            }));
        check(batch.All(result => !result.Blocked) && normalCalls == 3, "Consented snapshot executes all initial steps/actions");
        var again = await PackActionOncePolicy.RunSnapshotAsync(full.LoadActions()[0], full, good, () => true,
            _ => { normalCalls++; return Task.FromResult("unexpected"); });
        check(!again.Blocked && normalCalls == 3, "Same snapshot completed steps skip");

        string cliRoot = Dir("CLI-loaded-drift");
        File.WriteAllBytes(Auto(cliRoot), bytesA);
        var cliLoaded = PackAutoActionFile.Load(cliRoot)[0];
        check(cliLoaded.OriginSnapshot?.Hash == hashA, "File.Load retains immutable origin for default CLI runner");
        File.WriteAllBytes(Auto(cliRoot), bytesB);
        check(PackActionOncePolicy.TryBindSnapshot(cliLoaded, null, cliRoot, null, null,
            out bool cliFromPack, out var cliSnapshot, out var cliCaptured) && cliFromPack,
            "Default null origin stays pack when disk changes, never becomes builtin");
        int cliDispatch = 0;
        var cliResult = await PackActionOncePolicy.RunSnapshotAsync(cliCaptured, cliSnapshot!, cliRoot,
            () => cliSnapshot!.Hash == hashA, _ => { cliDispatch++; return Task.FromResult("unexpected"); });
        check(cliResult.Blocked && cliDispatch == 0 && NoHistory(cliRoot), "Default loaded pack drift blocks plan/reservation/dispatch");
        cliLoaded.Steps[0].Raw = snapshotB.LoadActions()[0].Steps[0].Raw;
        check(!PackActionOncePolicy.TryBindSnapshot(cliLoaded, null, cliRoot, null, null, out _, out _, out _),
            "Mutable loaded action parameters cannot forge captured origin authorization");

        string waiting = Dir("lock-drift");
        File.WriteAllBytes(Auto(waiting), bytesA);
        var beforeLock = PackAutoActionSnapshot.TryLoad(waiting)!;
        var holder = await PackOnceLedger.OpenAsync(waiting);
        int lockDispatch = 0, currentChecks = 0;
        Task<PackActionOncePolicy.ExecutionResult> blocked;
        try
        {
            blocked = PackActionOncePolicy.RunSnapshotAsync(beforeLock.LoadActions()[0], beforeLock, waiting,
                () => { currentChecks++; return true; }, _ => { lockDispatch++; return Task.FromResult("unexpected"); });
            check(!blocked.IsCompleted && currentChecks == 1, "Gate passed A check and waits on held cross-process ledger lock");
            File.WriteAllBytes(Auto(waiting), bytesB);
        }
        finally { holder.Dispose(); }
        var afterLock = await blocked;
        check(afterLock.Blocked && afterLock.Reason == PackActionOncePolicy.BlockReason.ContextChanged
            && currentChecks == 2 && lockDispatch == 0 && NoHistory(waiting), "Lock-wait drift checked before plan/reservation/dispatch, all zero");
        File.WriteAllBytes(Auto(waiting), bytesA);
        var restored = await PackActionOncePolicy.RunSnapshotAsync(beforeLock.LoadActions()[0], beforeLock, waiting,
            () => true, _ => { lockDispatch++; return Task.FromResult("已完成"); });
        check(!restored.Blocked && lockDispatch == 2, "Restored A can execute only original consented snapshot, not B");
        string throwing = Dir("context-exception");
        File.WriteAllBytes(Auto(throwing), bytesA);
        var throwingSnapshot = PackAutoActionSnapshot.TryLoad(throwing)!;
        var throwingResult = await PackActionOncePolicy.RunSnapshotAsync(throwingSnapshot.LoadActions()[0], throwingSnapshot, throwing,
            () => throw new IOException("fixture context read failed"), _ => throw new Exception("must not dispatch"));
        check(throwingResult.Blocked && NoHistory(throwing), "Context check failure is structured blocked without plan/reservation");
        Console.WriteLine("SNAPSHOT-BINDING: controlled ABA/BA/lock drift blocked; unconsented B plan=0 reservation=0 dispatch=0; hash+legacy compatible.");
    }

    private static async Task ReviewSafetyAsync(string tempRoot, Action<bool, string> check)
    {
        List<PackAutoAction> pack = PackAutoActionFile.Parse("""
            {"actions":[
              {"name":"setup","steps":[{"action":"set_ini","once":false},{"action":"launch_game"}]},
              {"name":"next","steps":[{"action":"set_dx12"}]}
            ]}
            """);
        string[] keys = PackActionOncePolicy.PlanKeys(pack).ToArray();
        string Fixture(string name, string? json = null)
        {
            string path = Path.Combine(tempRoot, "review-" + name); Directory.CreateDirectory(path);
            if (json is not null) File.WriteAllText(Path.Combine(path, PackOnceLedger.FileName), json);
            return path;
        }
        foreach (string outcome in new[] { "✗ 失败", "已复制一项；✗ 部分失败", "possible partial failure" })
        {
            string dir = Fixture("chain-" + Guid.NewGuid().ToString("N"));
            int dispatches = 0;
            using var session = await PackOnceLedger.OpenAsync(dir);
            session.RegisterPlan(keys);
            var batch = await PackActionOncePolicy.RunBatchAsync(pack, action =>
                PackActionOncePolicy.RunStepsAsync(action, true, session,
                    step => { dispatches++; return Task.FromResult(outcome); }, () => true));
            check(batch.Count == 1 && batch[0].Blocked && batch[0].Reason == PackActionOncePolicy.BlockReason.Failed
                && dispatches == 1, "Failed/partial prefix stops following launch step and following action: " + outcome);
            var retry = await PackActionOncePolicy.RunStepsAsync(pack[0], true, session,
                _ => { dispatches++; return Task.FromResult("已完成"); }, () => true);
            check(retry.Blocked && retry.Reason == PackActionOncePolicy.BlockReason.Pending && dispatches == 1,
                "Pending retry stops entire action without reaching following dispatch");
        }
        string good = Fixture("new-full-plan");
        int normalCalls = 0;
        using (var session = await PackOnceLedger.OpenAsync(good))
        {
            check(session.RegisterPlan(keys) == PackOnceLedger.PlanRegistration.Registered, "New ledger registers whole pack once");
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(good, PackOnceLedger.FileName))))
                check(doc.RootElement.GetProperty("plan").GetArrayLength() == 3
                    && !doc.RootElement.GetProperty("states").EnumerateObject().Any(), "Whole pack plan durable before any reservation");
            var batch = await PackActionOncePolicy.RunBatchAsync(pack, action =>
                PackActionOncePolicy.RunStepsAsync(action, true, session,
                    _ => { normalCalls++; return Task.FromResult("已完成"); }, () => true));
            check(batch.Count == 2 && batch.All(result => !result.Blocked) && normalCalls == 3,
                "All steps in initial whole pack execute; second step/action not blocked by history count");
        }
        using (var session = await PackOnceLedger.OpenAsync(good))
        {
            var batch = await PackActionOncePolicy.RunBatchAsync(pack, action =>
                PackActionOncePolicy.RunStepsAsync(action, true, session,
                    _ => { normalCalls++; return Task.FromResult("unexpected"); }, () => true));
            check(batch.Count == 2 && batch.All(result => !result.Blocked) && normalCalls == 3, "Success skip can continue chain/batch, never dispatch again");
        }
        string skipThenRun = Fixture("skip-then-run");
        using (var session = await PackOnceLedger.OpenAsync(skipThenRun))
        {
            session.RegisterPlan(keys); session.Reserve(keys[0], "raw"); session.Complete(keys[0]);
            int calls = 0;
            var batch = await PackActionOncePolicy.RunBatchAsync(pack, action =>
                PackActionOncePolicy.RunStepsAsync(action, true, session,
                    _ => { calls++; return Task.FromResult("已完成"); }, () => true));
            check(batch.All(result => !result.Blocked) && calls == 2, "Initial plan allows still-unexecuted siblings after success skip");
        }
        string unknownLegacy = Fixture("unknown-legacy", "{\"deadbeef\":true}");
        using (var session = await PackOnceLedger.OpenAsync(unknownLegacy))
        {
            check(session.RegisterPlan(keys) == PackOnceLedger.PlanRegistration.NeedsMigration && !session.HasPlan,
                "Legacy history never implicitly registers new plan");
            int calls = 0;
            var batch = await PackActionOncePolicy.RunBatchAsync(pack, action =>
                PackActionOncePolicy.RunStepsAsync(action, true, session,
                    _ => { calls++; return Task.FromResult("unexpected"); }, () => true));
            check(batch.Count == 1 && batch[0].Blocked && batch[0].Reason == PackActionOncePolicy.BlockReason.NeedsMigration
                && calls == 0, "Unknown legacy prefix blocks following action/batch with zero dispatch");
        }
        var variants = new[]
        {
            "{\"name\":\"setup\",\"steps\":[{\"action\":\"launch_game\"},{\"action\":\"set_ini\"}]}",
            "{\"name\":\"setup\",\"steps\":[{\"action\":\"set_ini\",\"onceId\":\"new-id\"},{\"action\":\"launch_game\"}]}",
            "{\"name\":\"setup\",\"steps\":[{\"action\":\"write_ini\"},{\"action\":\"launch_game\"}]}",
            "{\"name\":\"renamed\",\"steps\":[{\"action\":\"set_ini\"},{\"action\":\"launch_game\"}]}"
        };
        foreach (bool statesOnly in new[] { false, true })
            foreach (string variant in variants)
            {
                string dir = Fixture("identity-" + Guid.NewGuid().ToString("N"));
                if (statesOnly)
                    File.WriteAllText(Path.Combine(dir, PackOnceLedger.FileName), JsonSerializer.Serialize(new
                    { schema = 3, fence = "old", entries = new Dictionary<string, bool>(), states = keys.ToDictionary(key => key, _ => "success") }));
                else File.Copy(Path.Combine(good, PackOnceLedger.FileName), Path.Combine(dir, PackOnceLedger.FileName));
                var changed = PackAutoActionFile.Parse(variant);
                using var session = await PackOnceLedger.OpenAsync(dir);
                check(session.RegisterPlan(PackActionOncePolicy.PlanKeys(changed)) == PackOnceLedger.PlanRegistration.NeedsMigration,
                    "History unknown identity never implicitly authorized (statesOnly=" + statesOnly + ")");
                int calls = 0;
                var result = await PackActionOncePolicy.RunStepsAsync(changed[0], true, session,
                    _ => { calls++; return Task.FromResult("unexpected"); }, () => true);
                check(result.Blocked && result.Reason == PackActionOncePolicy.BlockReason.NeedsMigration && calls == 0,
                    "Reorder/new onceId/alias/name drift is blocked, no guessed history or dispatch");
            }
        foreach (string plan in new[] { "null", "{}", "[\"x\",\"x\"]", "[true]" })
        {
            string dir = Fixture("bad-plan-" + Guid.NewGuid().ToString("N"), "{\"schema\":3,\"fence\":\"\",\"entries\":{},\"states\":{},\"plan\":" + plan + "}");
            bool refused = false;
            try { using var session = await PackOnceLedger.OpenAsync(dir); } catch (InvalidDataException) { refused = true; }
            check(refused, "Malformed plan fails closed");
        }
        string lockWait = Fixture("lock-context");
        bool current = true; int waitCalls = 0;
        Task<PackActionOncePolicy.ExecutionResult> waiting;
        using (var held = await PackOnceLedger.OpenAsync(lockWait))
        {
            held.RegisterPlan(keys);
            waiting = Task.Run(async () =>
            {
                using var session = await PackOnceLedger.OpenAsync(lockWait);
                return await PackActionOncePolicy.RunStepsAsync(pack[0], true, session,
                    _ => { waitCalls++; return Task.FromResult("unexpected"); }, () => current);
            });
            await Task.Delay(50); current = false;
        }
        var waitResult = await waiting;
        check(waitResult.Blocked && waitResult.Reason == PackActionOncePolicy.BlockReason.ContextChanged && waitCalls == 0,
            "Game/page changes during lock wait: zero dispatch after lock acquired");
        string asyncPrefix = Fixture("async-context");
        using (var session = await PackOnceLedger.OpenAsync(asyncPrefix))
        {
            session.RegisterPlan(keys);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string activeGame = "captured"; int calls = 0;
            Task<PackActionOncePolicy.ExecutionResult> running = PackActionOncePolicy.RunStepsAsync(pack[0], true, session, async _ =>
            { calls++; entered.SetResult(); await release.Task; return "已完成"; }, () => activeGame == "captured");
            await entered.Task; activeGame = "other"; release.SetResult();
            var result = await running;
            check(result.Blocked && result.Reason == PackActionOncePolicy.BlockReason.ContextChanged && calls == 1,
                "Async prefix + game change stops later step without targeting new game");
            check(session.Reserve(keys[0], "raw") == PackOnceLedger.Reservation.Pending,
                "Context change during prefix conservatively leaves pending, not success");
        }
        int manualCalls = 0;
        for (int i = 0; i < 2; i++)
        {
            var manual = PackAutoActionFile.Parse("{\"name\":\"manual\",\"steps\":[{\"action\":\"set_addons\"}]}")[0];
            var result = await PackActionOncePolicy.RunStepsAsync(manual, false, null,
                _ => { manualCalls++; return Task.FromResult("已完成"); }, () => true);
            check(!result.Blocked, "Manual builtin is independent of package batch block");
        }
        check(manualCalls == 2, "Manual builtin remains independently repeatable");
    }

    private static Process StartWorker(string root, string mode)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach (string arg in new[] { "pack-once-worker", root, mode }) start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    private static async Task<int> WorkerAsync(string[] args)
    {
        if (args.Length != 3 || !Path.GetFullPath(args[1]).StartsWith(TempPrefix, StringComparison.OrdinalIgnoreCase)) return 90;
        using var session = await PackOnceLedger.OpenAsync(args[1]);
        session.RegisterPlan(new[] { "process-key" });
        await session.ExecuteAsync("process-key", "raw", async () =>
        {
            File.AppendAllText(Path.Combine(args[1], "fake-dispatch.txt"), "fake\n");
            if (args[2] == "crash") Environment.Exit(23);
            await Task.Delay(100);
            return "已完成";
        });
        return 0;
    }

    private static async Task SnapshotAsync(string snapshot, string tempRoot, Action<bool, string> check)
    {
        foreach ((string game, int expectedSkip, int expectedBlock) in new[] { ("hk4e_cn", 12, 0), ("hkrpg_bilibili", 10, 2), ("nap_cn", 11, 0) })
        {
            string dir = Path.Combine(tempRoot, "snapshot-" + game); Directory.CreateDirectory(dir);
            File.Copy(Path.Combine(snapshot, game + "-.hysx_once.json"), Path.Combine(dir, PackOnceLedger.FileName));
            List<PackAutoAction> actions = PackAutoActionFile.Parse(File.ReadAllText(Path.Combine(snapshot, game + "-auto.json")));
            int skip = 0, block = 0, dispatch = 0;
            var blocked = new List<string>();
            using (var session = await PackOnceLedger.OpenAsync(dir))
                foreach (PackAutoAction action in actions)
                    for (int index = 0; index < action.Steps.Count; index++)
                    {
                        PackActionStep step = action.Steps[index];
                        var result = await session.ExecuteAsync(PackActionOncePolicy.StableKey(action.Name, index, step),
                            PackActionOncePolicy.LegacyKey(step), () => { dispatch++; throw new Exception("Snapshot MUST NOT dispatch"); });
                        if (result.Reservation == PackOnceLedger.Reservation.Success) skip++;
                        else if (result.Reservation == PackOnceLedger.Reservation.NeedsMigration) { block++; blocked.Add($"{index}/{step.Action}"); }
                    }
            check(skip == expectedSkip && block == expectedBlock && dispatch == 0, game + " offline legacy skip/block coverage");
            if (game == "hkrpg_bilibili") check(blocked.SequenceEqual(new[] { "1/set_window_mode", "6/set_opt_build" }), "Only two known StarRail steps blocked, never guessed");
            Console.WriteLine($"SNAPSHOT {game}: total={skip + block}, skip={skip}, blocked={block}, dispatch={dispatch}" + (block > 0 ? "; " + string.Join(", ", blocked) : ""));
        }
    }

    private static void SourceContracts(string sourceRoot, Action<bool, string> check)
    {
        string dir = Path.Combine(sourceRoot, "src", "HoYoShadeHub", "Features", "Plugins");
        string page = File.ReadAllText(Path.Combine(dir, "GamePluginPage.xaml.cs"));
        string refresh = page[page.IndexOf("    private void RefreshActions()", StringComparison.Ordinal)..page.IndexOf("    private static PackAutoAction BuiltinAction", StringComparison.Ordinal)];
        check(!refresh.Contains("ShowAsync") && !refresh.Contains("PromptPackActionConsentAsync") && !refresh.Contains(".Save(") && !refresh.Contains(".RunAsync("), "Refresh source is read-only: no show/run/consent write");
        check(refresh.Contains("new ConsentButton") && refresh.Contains("点击确认覆盖包动作"), "Unconsented page offers explicit button");
        check(refresh.Contains("new ActionButton(BuiltinAction") && refresh.Contains("false, snapshot"), "Builtin buttons carry explicit non-pack provenance");
        check(page.Contains("_packActionBusy") && page.Contains("if (disabled is null || !IsActionContextCurrent(snapshot, true)) return;"), "Consent reentry and post-await context fenced");
        string runner = File.ReadAllText(Path.Combine(dir, "LauncherActionRunner.cs"));
        check(!runner.Contains("OnceMarker") && !runner.Contains("ApplyFence("), "Runner never resets pack ledger on navigation/fence");
        check(runner.Contains("PackActionOncePolicy.TryBindSnapshot") && runner.Contains("PackActionOncePolicy.RunStepsAsync")
            && runner.Contains("PackActionOncePolicy.RunSnapshotAsync") && runner.Contains("ledger.RegisterPlan"), "Runner uses tested provenance/reservation path");
        check(!runner.Contains("PackAutoActionFile.Load(") && !runner.Contains("PackActionConsent.ComputeHash(")
            && runner.Contains("fromPack ? snapshot!.Hash") && runner.Contains("context.ExpectedPackHash"),
            "Runner cannot stitch live pack actions/hash or authorize old actions with a new hash");
        check(refresh.Contains("snapshot.Pack?.LoadActions()") && page.Contains("PackAutoActionSnapshot.TryLoad(_packRoot)")
            && page.Contains("PackSnapshot = fromPack ? snapshot.Pack") && !page.Contains("PackActionConsent.ComputeHash("),
            "Plugin refresh/buttons/consent share one captured action/hash snapshot");
        string prompt = File.ReadAllText(Path.Combine(dir, "PackActionPrompt.cs"));
        check(prompt.Contains("Interlocked.CompareExchange") && prompt.Contains("isPackAction: true") && prompt.Contains("host, gamePackRoot") && prompt.Contains("PackActionOncePolicy.RunBatchAsync"), "Explicit import preserved with dialog guard and per-game ledger root");
        check(prompt.Contains("PackAutoActionSnapshot.TryLoad(packRoot)") && prompt.Contains("group.Games.Add((id, entry, snapshot))")
            && prompt.Contains("PackSnapshot = snapshot") && prompt.Contains("RunAsync(snapshot.LoadActions()")
            && !prompt.Contains("PackAutoActionFile.Load(") && !prompt.Contains("PackActionConsent.ComputeHash("),
            "Import groups consent by same-read hash and retains each game's own immutable action snapshot");
        string consent = File.ReadAllText(Path.Combine(dir, "PackActionConsent.cs"));
        check(consent.Contains("PackAutoActionSnapshot.ComputeHash(File.ReadAllBytes(path))"), "Actual consent class shares original byte SHA256 algorithm");
        string policy = File.ReadAllText(Path.Combine(sourceRoot, "src", "HoYoShadeHub.Extensions", "ReShade", "PackActionOncePolicy.cs"));
        string gate = policy[policy.IndexOf("public static async Task<ExecutionResult> RunSnapshotAsync", StringComparison.Ordinal)..policy.IndexOf("/// <summary>生产与回归共用的步骤链", StringComparison.Ordinal)];
        check(gate.Contains("snapshot.IsCurrent(packRoot, readBytes)") && gate.Contains("PlanKeys(snapshot.LoadActions())")
            && gate.IndexOf("if (!Current())", StringComparison.Ordinal) > gate.IndexOf("await PackOnceLedger.OpenAsync", StringComparison.Ordinal)
            && gate.IndexOf("if (!Current())", StringComparison.Ordinal) < gate.IndexOf("ledger.RegisterPlan", StringComparison.Ordinal),
            "Shared production gate checks captured expected hash after lock wait and registers only snapshot plan");
        string navigation = page[page.IndexOf("    private async Task RefreshAsync()", StringComparison.Ordinal)..page.IndexOf("    private void UpdateDriverWarning()", StringComparison.Ordinal)];
        check(!navigation.Contains("GameAddonPackService.Sync(") && !navigation.Contains("GameIniBootstrap.Ensure(")
            && !navigation.Contains("SyncDlss5FeedPreset(") && !navigation.Contains("GamePluginServiceFactory.Create("),
            "Full navigation/LoadGame chain has no pack sync, INI ensure, preset sync or name cache write");
        check(page.Contains("Button_CopyIni_Click") && page.Contains("Button_AlignIni_Click"), "Explicit user INI controls retained");
        string start = File.ReadAllText(Path.Combine(sourceRoot, "src", "HoYoShadeHub", "Features", "GameLauncher", "GameLauncherPage.StartGame.cs"));
        string launch = start[start.IndexOf("private async Task<bool> RunPackAutoActionsOnLaunchAsync()", StringComparison.Ordinal)..start.IndexOf("    /// <summary>Observe optional", StringComparison.Ordinal)];
        check(launch.Contains("LauncherPage = null") && launch.Contains("GameId = gameId") && launch.Contains("Entry = capturedEntry")
            && launch.Contains("IsContextCurrent = () => !unloaded && IsLoaded") && launch.Contains("Unloaded += onUnloaded")
            && launch.Contains("Unloaded -= onUnloaded"), "StartGame captured identity + lifecycle callback; no mutable LauncherPage");
        check(start.Contains("if (!await RunPackAutoActionsOnLaunchAsync())"), "Page still runs baseline package preparation with a context guard");
        string pipeline = File.ReadAllText(Path.Combine(sourceRoot, "src", "HoYoShadeHub", "Features", "GameLauncher", "GameLaunchPipeline.cs"));
        check(pipeline.Contains("if (!await RunPackAutoActionsOnLaunchAsync(gameId, entry, report, ct))"), "Headless launch still runs baseline package preparation with a context guard");
        foreach (string file in new[] { "GameLaunchPipeline.cs", "GameLauncherPage.StartGame.cs" })
            check(File.ReadAllText(Path.Combine(sourceRoot, "src", "HoYoShadeHub", "Features", "GameLauncher", file))
                .Contains("LauncherActionRunner.RunResultAsync(action, context, isPackAction: true)"), "Launch caller cannot lose captured pack origin: " + file);
        foreach (string file in new[] { "GameLaunchPipeline.cs", "GameLauncherPage.StartGame.cs" })
            check(File.ReadAllText(Path.Combine(sourceRoot, "src", "HoYoShadeHub", "Features", "GameLauncher", file))
                .Contains("return GameLaunchContinuation.AfterPackActions(result,"), "Stopped package batch permits independent launch: " + file);
        foreach (string file in new[] { "GameLaunchPipeline.cs", "GameLauncherPage.StartGame.cs" })
        {
            string source = File.ReadAllText(Path.Combine(sourceRoot, "src", "HoYoShadeHub", "Features", "GameLauncher", file));
            int begin = source.IndexOf("private " + (file == "GameLaunchPipeline.cs" ? "static " : "") + "async Task<bool> RunPackAutoActionsOnLaunchAsync", StringComparison.Ordinal);
            int end = source.IndexOf(file == "GameLaunchPipeline.cs" ? "    // ==================== ②" : "    /// <summary>Observe optional", begin, StringComparison.Ordinal);
            string entry = source[begin..end];
            check(entry.Contains("PackAutoActionSnapshot.TryLoad(packRoot)") && entry.Contains("string consentHash = snapshot.Hash;")
                && entry.Contains("snapshot.LoadActions()") && entry.Contains("PackSnapshot = snapshot")
                && entry.Contains("ExpectedPackHash = consentHash") && !entry.Contains("PackActionConsent.ComputeHash(")
                && !entry.Contains("PackAutoActionFile.Load("), "Launch consent/action/plan same-read binding: " + file);
        }
    }
}
