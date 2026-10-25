using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Features.GameLauncher;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace HoYoShadeHub.Extensions.Tests
{
    internal static class UidReportPipeTests
    {
        public static async Task<int> RunAsync(string[] args)
        {
            if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP Windows pipe PID checks"); return 0; }
            if (args[0] == "uid-lifetime-worker") { await Console.In.ReadLineAsync(); return 0; }
            if (args[0] == "uid-foreign-client")
            {
                string? result = await ExchangeAsync(args[1], Encoding.UTF8.GetBytes(args[2] + "\n"));
                return result is null ? 0 : 1;
            }
            int passed = 0;
            void Check(bool ok, string name)
            {
                if (!ok) throw new InvalidOperationException(name);
                passed++;
            }
            using var self = Process.GetCurrentProcess();
            int pid = self.Id;
            ulong start = (ulong)self.StartTime.ToUniversalTime().ToFileTimeUtc();
            string name = UidReportProtocol.PipeName(pid, start) + ".test." + Guid.NewGuid().ToString("N");
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            int mode = 0, readCount = 0;
            Task<string?> Observe(int _, CancellationToken token)
            {
                Interlocked.Increment(ref readCount);
                return mode switch
                {
                    0 => Task.FromResult<string?>("123456789"),
                    1 => Task.FromResult<string?>(null),
                    2 => Task.FromException<string?>(new InvalidOperationException("fixture OCR unavailable")),
                    3 => WaitForTimeout(token),
                    _ => Task.FromResult<string?>(null),
                };
            }
            string? remembered = null;
            Task server = GameUidReportServer.ServeAsync(name, "genshin", "hk4e_cn", pid, start,
                stop.Token, Observe, _ => remembered);
            var request = new UidReportRequest(1, "observe", "genshin", pid, start, new string('c', 64));
            byte[] Encode(UidReportRequest value) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, UidReportProtocol.Json) + "\n");
            async Task<UidReportResponse> GetAsync()
            {
                string? result = await ExchangeAsync(name, Encode(request));
                return JsonSerializer.Deserialize<UidReportResponse>(result ?? throw new IOException("missing fixture response"), UidReportProtocol.Json)!;
            }
            try
            {
                var response = await GetAsync();
                Check(response.Status == "observed" && response.Uid == "123456789" && response.Nonce == request.Nonce
                    && response.Pid == pid && response.ProcessStartFileTime == start, "real pipe response bound to request/process");
                string session = response.ServerSessionId;
                Check(session.Length == 32 && response.Sequence == 1, "server instance session and initial sequence");
                mode = 1;
                var unknown = await GetAsync();
                Check(unknown.Uid is null && unknown.Status == "unknown" && unknown.Sequence == 2
                    && unknown.ServerSessionId == session, "unknown invalidates previous response");
                remembered = "234567890";
                var cached = await GetAsync();
                Check(cached.Status == "remembered" && cached.Source == "game-registry-last-login"
                    && cached.ObservedAtUnixMs is null, "registry fallback has no current-session claim");
                mode = 2;
                Check((await GetAsync()).Status == "remembered", "OCR failure returns registry hint rather than hanging");
                mode = 3;
                var time = Stopwatch.StartNew();
                Check((await GetAsync()).Status == "remembered" && time.Elapsed < TimeSpan.FromSeconds(8), "OCR timeout has bounded response");
                mode = 0;
                int before = readCount;
                foreach (var invalid in new[] { request with { Pid = pid + 1 }, request with { ProcessStartFileTime = start + 1 },
                    request with { GameId = "starrail" }, request with { Version = 2 }, request with { Nonce = "short" } })
                    Check(await ExchangeAsync(name, Encode(invalid)) is null, "wrong request binding rejected");
                foreach (byte[] invalid in new[] { Encoding.UTF8.GetBytes("{}\n"), new byte[] { 0xff, 0x0a },
                    Encoding.UTF8.GetBytes(new string('x', 4096)), Encoding.UTF8.GetBytes("{}\n{}\n") })
                    Check(await ExchangeAsync(name, invalid) is null, "bad framing/schema/UTF8 rejected");
                using var foreign = StartWorker("uid-foreign-client", name, JsonSerializer.Serialize(request, UidReportProtocol.Json));
                await foreign.WaitForExitAsync(stop.Token);
                Check(foreign.ExitCode == 0 && readCount == before, "different actual client PID rejected before UID read");
                var continued = await GetAsync();
                Check(continued.Status == "observed" && continued.ServerSessionId == session,
                    "broker accepts valid requests after malformed/foreign clients");
            }
            finally { stop.Cancel(); await server.WaitAsync(TimeSpan.FromSeconds(5)); }
            Check(server.IsCompleted, "cancellation releases broker and watchdog");

            using var worker = StartWorker("uid-lifetime-worker");
            using var lifetimeStop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            Task childServer = GameUidReportServer.ServeAsync(name + ".lifetime", "genshin", "hk4e_cn", worker.Id,
                (ulong)worker.StartTime.ToUniversalTime().ToFileTimeUtc(), lifetimeStop.Token, Observe, _ => null);
            try
            {
                Check(!childServer.IsCompleted, "broker waits while target exists");
                await worker.StandardInput.WriteLineAsync();
                await worker.WaitForExitAsync(lifetimeStop.Token);
                await childServer.WaitAsync(TimeSpan.FromSeconds(4));
                Check(childServer.IsCompleted, "process exit stops pending pipe accept");
            }
            finally
            {
                lifetimeStop.Cancel();
                worker.StandardInput.Close();
                await childServer.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Console.WriteLine($"PASS {passed} real Windows named-pipe checks; injected UID fixtures, no game capture");
            return 0;
        }

        private static async Task<string?> WaitForTimeout(CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            return null;
        }

        private static async Task<string?> ExchangeAsync(string name, byte[] data)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(9));
            using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            try
            {
                await client.WriteAsync(data, timeout.Token);
                await client.FlushAsync(timeout.Token);
                using var reader = new StreamReader(client, new UTF8Encoding(false, true), false, 4096, true);
                return await reader.ReadLineAsync(timeout.Token);
            }
            catch (IOException) { return null; }
        }

        private static Process StartWorker(params string[] arguments)
        {
            var info = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            };
            if (Path.GetFileNameWithoutExtension(info.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                info.ArgumentList.Add(typeof(UidReportPipeTests).Assembly.Location);
            foreach (string argument in arguments) info.ArgumentList.Add(argument);
            return Process.Start(info) ?? throw new InvalidOperationException("fixture worker failed to start");
        }
    }
}
