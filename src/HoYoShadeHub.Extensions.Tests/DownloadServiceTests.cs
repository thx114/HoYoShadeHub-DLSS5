using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HoYoShadeHub.Extensions.Networking;
using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

internal static class DownloadServiceTests
{
    public static async Task<int> RunAsync()
    {
        int failed = 0;
        string root = Path.Combine(Path.GetTempPath(), "download-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        const string url = "https://download.invalid/archive";
        const string etag = "\"version-one\"";
        void Check(bool ok, string name)
        {
            Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}");
            if (!ok) failed++;
        }
        async Task Scenario(string name, string? metadata, string partial,
            Func<HttpRequestMessage, int, HttpResponseMessage> respond, string expected, int expectedRequests,
            bool shouldFail = false, string? hash = null)
        {
            string target = Path.Combine(root, name);
            await File.WriteAllTextAsync(target, "old-target");
            await File.WriteAllTextAsync(target + ".part", partial);
            if (metadata is not null) await File.WriteAllTextAsync(target + ".part.json", metadata);
            using var handler = new FakeHandler(respond);
            using var client = new HttpClient(handler);
            bool threw = false;
            try { await new DownloadService(client).DownloadToFileAsync(url, target, hash); }
            catch (Exception ex) when (ex is InvalidDataException or HttpRequestException) { threw = true; }
            Check(threw == shouldFail && await File.ReadAllTextAsync(target) == expected
                && handler.Requests == expectedRequests, name);
        }
        string Meta(string source, string tag = etag) =>
            JsonSerializer.Serialize(new DownloadResumeMetadata(HysxHttp.Apply(source), tag));
        HttpResponseMessage Full(string text = "fresh") => Response(HttpStatusCode.OK, text);
        HttpResponseMessage Partial(long from, long to, long total, string body = "def")
        {
            var result = Response(HttpStatusCode.PartialContent, body);
            result.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, total);
            result.Headers.ETag = new EntityTagHeaderValue(etag);
            return result;
        }
        HttpResponseMessage Rejection(long? total, bool matchingTag = true)
        {
            var result = Response(HttpStatusCode.RequestedRangeNotSatisfiable, "");
            if (total is not null) result.Content.Headers.ContentRange = new ContentRangeHeaderValue(total.Value);
            if (matchingTag) result.Headers.ETag = new EntityTagHeaderValue(etag);
            return result;
        }
        try
        {
            await Scenario("changed-url", Meta(url + "/other"), "abc", (r, _) =>
            { Check(r.Headers.Range is null, "changed URL sends no Range"); return Full(); }, "fresh", 1);
            await Scenario("legacy-sidecar", "{\"etag\":\"\\\"version-one\\\"\"}", "abc",
                (r, _) => { Check(r.Headers.Range is null, "legacy metadata restarts"); return Full(); }, "fresh", 1);
            await Scenario("weak-tag", Meta(url, "W/" + etag), "abc",
                (r, _) => { Check(r.Headers.Range is null, "weak ETag does not resume"); return Full(); }, "fresh", 1);
            await Scenario("valid-resume", Meta(url), "abc", (r, _) =>
            { Check(r.Headers.Range?.Ranges.Single().From == 3, "resume offset is exact"); return Partial(3, 5, 6); }, "abcdef", 1);
            await Scenario("segmented-resume", Meta(url), "abc", (r, n) => n == 1
                ? Partial(3, 3, 6, "d") : Partial(4, 5, 6, "ef"), "abcdef", 2);
            await Scenario("wrong-offset", Meta(url), "abc", (_, n) => n == 1 ? Partial(0, 2, 6) : Full(), "fresh", 2);
            await Scenario("changed-etag", Meta(url), "abc", (_, n) =>
            {
                if (n > 1) return Full();
                var result = Partial(3, 5, 6);
                result.Headers.ETag = new EntityTagHeaderValue("\"version-two\"");
                return result;
            }, "fresh", 2);
            await Scenario("missing-206-range", Meta(url), "abc", (_, n) => n == 1
                ? Response(HttpStatusCode.PartialContent, "def") : Full(), "fresh", 2);
            await Scenario("missing-416-length", Meta(url), "abc", (_, n) => n == 1 ? Rejection(null) : Full(), "fresh", 2);
            await Scenario("overlong-416", Meta(url), "abcd", (_, n) => n == 1 ? Rejection(3) : Full(), "fresh", 2);
            await Scenario("missing-416-etag", Meta(url), "abc", (_, n) => n == 1 ? Rejection(3, false) : Full(), "fresh", 2);
            await Scenario("exact-416", Meta(url), "abc", (_, _) => Rejection(3), "abc", 1);
            await Scenario("range-body-truncated", Meta(url), "abc", (_, n) =>
            {
                if (n > 1) return Full();
                var result = Partial(3, 5, 6, "d");
                result.Content.Headers.ContentLength = 3;
                return result;
            }, "fresh", 2);
            await Scenario("fresh-truncated", null, "", (_, _) =>
            { var result = Full("x"); result.Content.Headers.ContentLength = 9; return result; }, "old-target", 1, true);
            await Scenario("fresh-unrequested-partial", null, "", (_, _) => Partial(0, 2, 6, "abc"), "old-target", 1, true);
            await Scenario("error-response", null, "", (_, _) => Response(HttpStatusCode.BadGateway, "bad"), "old-target", 1, true);
            await Scenario("hash-mismatch", null, "", (_, _) => Full(), "old-target", 1, true, new string('0', 64));
            string cancelledTarget = Path.Combine(root, "cancelled");
            await File.WriteAllTextAsync(cancelledTarget, "old-target");
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            using var cancelledHandler = new FakeHandler((_, _) => Full());
            using var cancelledClient = new HttpClient(cancelledHandler);
            bool cancelled = false;
            try { await new DownloadService(cancelledClient).DownloadToFileAsync(url, cancelledTarget, cancellationToken: cts.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && await File.ReadAllTextAsync(cancelledTarget) == "old-target", "cancellation preserves old target");
        }
        finally { Directory.Delete(root, recursive: true); }
        return failed == 0 ? 0 : 1;
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) =>
        new(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };

    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(respond(request, ++Requests));
        }
    }
}
