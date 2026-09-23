using HoYoShadeHub.Extensions.Networking;
using System.Net;
using System.Net.Http;

namespace HoYoShadeHub.Extensions.Services;

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value * 100d : null;
}

/// <summary>
/// Pause signal. Pausing does NOT throw: the loop in <see cref="DownloadService"/> parks
/// until the token is released, so a paused download keeps its partial .part file and its
/// HTTP connection state (if any). Cancelling still aborts for real.
/// </summary>
public sealed class DownloadPauseToken
{
    private volatile TaskCompletionSource _gate = CreateOpenGate();

    public bool IsPaused { get; private set; }

    private static TaskCompletionSource CreateOpenGate()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    public void Pause()
    {
        if (IsPaused)
        {
            return;
        }

        IsPaused = true;
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void Resume()
    {
        if (!IsPaused)
        {
            return;
        }

        IsPaused = false;
        _gate.TrySetResult();
    }

    /// <summary>Blocks while paused. Returns immediately when running.</summary>
    public Task WaitAsync(CancellationToken cancellationToken) =>
        IsPaused ? _gate.Task.WaitAsync(cancellationToken) : Task.CompletedTask;
}

/// <summary>
/// Downloader with progress, sha256 verification and resume support.
///
/// Resume: the partial file is kept as "&lt;target&gt;.part" plus a small "&lt;target&gt;.part.json"
/// sidecar describing where it came from (url + ETag). A later run with the same url continues
/// from the existing byte count via a Range request. The sidecar matters because resuming the
/// wrong bytes silently produces a corrupt file -- ETag mismatch always restarts from zero.
/// </summary>
public sealed class DownloadService
{
    private const int BufferSize = 81920;

    private readonly HttpClient _httpClient;

    public DownloadService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? HysxHttp.CreateClient();
    }

    /// <summary>Partial file next to the target, plus sidecar metadata.</summary>
    public static string PartPathOf(string targetFile) => targetFile + ".part";

    private static string PartMetaPathOf(string targetFile) => targetFile + ".part.json";

    public async Task<long> DownloadToFileAsync(
        string url,
        string targetFile,
        string? expectedSha256 = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        DownloadPauseToken? pauseToken = null,
        bool allowResume = true)
    {
        string? directory = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string appliedUrl = HysxHttp.Apply(url);
        string partPath = PartPathOf(targetFile);
        string metaPath = PartMetaPathOf(targetFile);

        if (!allowResume)
        {
            DeleteQuietly(partPath);
            DeleteQuietly(metaPath);
        }

        long existing = allowResume && File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        string? knownETag = allowResume ? ReadSidecarETag(metaPath) : null;

        long received;
        bool completed;

        if (existing > 0)
        {
            received = await TryResumeAsync(appliedUrl, partPath, existing, knownETag, progress, cancellationToken, pauseToken);
            completed = received == existing;
            if (!completed)
            {
                // Server refused the range (or the file changed) -- start over.
                DeleteQuietly(partPath);
                DeleteQuietly(metaPath);
                received = 0;
            }
        }
        else
        {
            received = 0;
            completed = false;
        }

        if (!completed)
        {
            received = await DownloadFromScratchAsync(
                appliedUrl, partPath, metaPath, progress, cancellationToken, pauseToken);
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string actual = await HysxUtil.ComputeSha256Async(partPath, cancellationToken);
            if (!HysxUtil.IsHashEqual(actual, expectedSha256))
            {
                DeleteQuietly(partPath);
                DeleteQuietly(metaPath);
                throw new InvalidDataException($"sha256 mismatch: expected {expectedSha256}, got {actual}");
            }
        }

        // Only publish the file once it is complete and verified.
        File.Move(partPath, targetFile, overwrite: true);
        DeleteQuietly(metaPath);

        return new FileInfo(targetFile).Length;
    }

    /// <summary>
    /// Attempts a Range request. Returns the resulting byte count; equals <paramref name="existing"/>
    /// when the server answered 416 (already have everything) and the caller should treat it as done.
    /// </summary>
    private async Task<long> TryResumeAsync(
        string appliedUrl,
        string partPath,
        long existing,
        string? knownETag,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        DownloadPauseToken? pauseToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, appliedUrl);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existing, null);
        if (!string.IsNullOrEmpty(knownETag))
        {
            request.Headers.TryAddWithoutValidation("If-Range", knownETag);
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // We already hold the whole thing.
            long? fullLength = response.Content.Headers.ContentRange?.Length;
            if (fullLength is null or <= 0 || existing >= fullLength)
            {
                return existing;
            }

            return -1;
        }

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            // Server ignored the range header -- cannot resume, caller restarts.
            return -1;
        }

        // If-Range failed: the resource changed under us, restart.
        string? currentETag = response.Headers.ETag?.ToString();
        if (!string.IsNullOrEmpty(knownETag) && !string.IsNullOrEmpty(currentETag)
            && !string.Equals(knownETag, currentETag, StringComparison.Ordinal))
        {
            return -1;
        }

        long? contentLength = response.Content.Headers.ContentRange?.Length;
        long received = existing;

        await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            received = await PumpAsync(source, destination, received, contentLength, progress, cancellationToken, pauseToken);
        }

        return received;
    }

    private async Task<long> DownloadFromScratchAsync(
        string appliedUrl,
        string partPath,
        string metaPath,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        DownloadPauseToken? pauseToken)
    {
        using HttpResponseMessage response = await _httpClient.GetAsync(
            appliedUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        WriteSidecar(metaPath, response.Headers.ETag?.ToString());

        long received = 0;
        await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
        {
            received = await PumpAsync(source, destination, 0, total, progress, cancellationToken, pauseToken);
        }

        return received;
    }

    private static async Task<long> PumpAsync(
        Stream source,
        Stream destination,
        long received,
        long? total,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken,
        DownloadPauseToken? pauseToken)
    {
        byte[] buffer = new byte[BufferSize];
        int read;

        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (pauseToken is not null)
            {
                await pauseToken.WaitAsync(cancellationToken);
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;
            progress?.Report(new DownloadProgress(received, total));
        }

        await destination.FlushAsync(cancellationToken);
        return received;
    }

    private static void WriteSidecar(string metaPath, string? etag)
    {
        try
        {
            File.WriteAllText(metaPath, $"{{\"etag\":{(etag is null ? "null" : "\"" + etag.Replace("\"", "\\\"") + "\"")}}}");
        }
        catch
        {
            // Sidecar is an optimisation only.
        }
    }

    private static string? ReadSidecarETag(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
            {
                return null;
            }

            string text = File.ReadAllText(metaPath);
            int start = text.IndexOf("\"etag\"", StringComparison.Ordinal);
            if (start < 0)
            {
                return null;
            }

            int colon = text.IndexOf(':', start);
            int firstQuote = text.IndexOf('"', colon + 1);
            if (colon < 0 || firstQuote < 0)
            {
                return null;
            }

            int secondQuote = text.IndexOf('"', firstQuote + 1);
            if (secondQuote < 0)
            {
                return null;
            }

            string value = text[(firstQuote + 1)..secondQuote];
            return value.Length == 0 ? null : value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Drops any partial download for this target. Called when the user cancels.</summary>
    public static void DiscardPartial(string targetFile)
    {
        DeleteQuietly(PartPathOf(targetFile));
        DeleteQuietly(PartMetaPathOf(targetFile));
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }
}