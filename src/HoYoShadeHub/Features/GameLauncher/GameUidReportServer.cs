using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>Game-local pull broker. No whitelist, credentials, authorization decisions or persistent UID.</summary>
[SupportedOSPlatform("windows")]
internal static class GameUidReportServer
{
    private static readonly ConcurrentDictionary<string, byte> Active = new();

    public static void Begin(GameId? id, int pid)
    {
        string biz = id?.GameBiz.Value ?? "";
        string? game = biz.StartsWith("hk4e_", StringComparison.Ordinal) ? "genshin"
            : biz.StartsWith("hkrpg_", StringComparison.Ordinal) ? "starrail"
            : biz.StartsWith("nap_", StringComparison.Ordinal) ? "zzz" : null;
        if (game is null || pid <= 0) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited || !UidReportProtocol.MatchesGameProcess(game, process.ProcessName)) return;
            ulong start = (ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc();
            string pipe = UidReportProtocol.PipeName(pid, start);
            if (Active.TryAdd(pipe, 0)) _ = Task.Run(async () =>
            {
                // Production reports verified registry mappings as remembered only. OCR remains an experiment.
                try { await ServeAsync(pipe, game, biz, pid, start, CancellationToken.None,
                    (_, _) => Task.FromResult<string?>(null), GameUidRegistryReader.ReadLastLogin).ConfigureAwait(false); }
                finally { Active.TryRemove(pipe, out _); }
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static bool IsCurrent(int pid, ulong start)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && (ulong)process.StartTime.ToUniversalTime().ToFileTimeUtc() == start;
        }
        catch { return false; }
    }

    // The injected readers also allow protocol/pipe tests without starting or photographing a game.
    internal static async Task ServeAsync(string name, string game, string biz, int pid, ulong start,
        CancellationToken cancellation, Func<int, CancellationToken, Task<string?>> observe,
        Func<HoYoShadeHub.Core.GameBiz, string?> readLastLogin)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        Task watchdog = WatchProcessAsync(pid, start, lifetime);
        try
        {
            // Hold the first instance for the entire game session; a rival broker cannot replace it between requests.
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 4096, 4096);
            long sequence = 0;
            string session = Guid.NewGuid().ToString("N");
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                    if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint client) || client != pid || !IsCurrent(pid, start)) continue;
                    using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    requestTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    string text = await ReadMessageAsync(pipe, requestTimeout.Token).ConfigureAwait(false);
                    var request = JsonSerializer.Deserialize<UidReportRequest>(text, UidReportProtocol.Json);
                    if (request is null || !UidReportProtocol.Validate(request, game, pid, start)) continue;

                    string? uid = null;
                    using (var captureTimeout = CancellationTokenSource.CreateLinkedTokenSource(requestTimeout.Token))
                    {
                        captureTimeout.CancelAfter(TimeSpan.FromSeconds(4));
                        try { uid = await observe(pid, captureTimeout.Token).ConfigureAwait(false); }
                        catch (Exception ex) when (ex is OperationCanceledException or ArgumentException
                            or InvalidOperationException or COMException or NotSupportedException) { }
                    }
                    string? remembered = uid is null ? readLastLogin(new HoYoShadeHub.Core.GameBiz(biz)) : null;
                    if (!IsCurrent(pid, start)) { uid = null; remembered = null; }
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var response = UidReportProtocol.CreateResponse(request, biz, ++sequence, uid, remembered, now, session);
                    byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response, UidReportProtocol.Json) + "\n");
                    await pipe.WriteAsync(bytes, requestTimeout.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(requestTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException
                    or ArgumentException or InvalidOperationException or COMException or UnauthorizedAccessException) { /* Never log raw UID/OCR or request data. */ }
                finally
                {
                    try { if (pipe.IsConnected) pipe.Disconnect(); }
                    catch (IOException) { }
                }
                await Task.Delay(500, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException
            or InvalidOperationException) { }
        finally { lifetime.Cancel(); await watchdog.ConfigureAwait(false); }
    }

    private static async Task WatchProcessAsync(int pid, ulong start, CancellationTokenSource lifetime)
    {
        try
        {
            while (true)
            {
                await Task.Delay(1000, lifetime.Token).ConfigureAwait(false);
                if (!IsCurrent(pid, start)) { lifetime.Cancel(); return; }
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task<string> ReadMessageAsync(Stream stream, CancellationToken token)
    {
        byte[] buffer = new byte[UidReportProtocol.MaxMessageBytes];
        int used = 0;
        while (used < buffer.Length)
        {
            int count = await stream.ReadAsync(buffer.AsMemory(used, buffer.Length - used), token).ConfigureAwait(false);
            if (count == 0) throw new IOException("Incomplete UID request");
            int newline = Array.IndexOf(buffer, (byte)'\n', used, count);
            if (newline >= 0)
            {
                if (newline != used + count - 1) throw new IOException("Only one UID request per connection");
                return new UTF8Encoding(false, true).GetString(buffer, 0, newline);
            }
            used += count;
        }
        throw new IOException("UID request too large");
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
}
