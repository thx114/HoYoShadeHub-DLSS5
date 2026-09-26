using HoYoShadeHub.Extensions.Networking;
using System.Net;
using System.Net.Http;
using System.Text.Json;

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
            // 没有 ETag 对账就没法保证续上的字节还属于同一个文件（资源变了就是坏包）—— 宁可重下。
            // 以前 knownETag 为空也硬续，If-Range 都不带，服务器回 200 / 206 都分不清，纯粹碰运气。
            if (string.IsNullOrEmpty(knownETag))
            {
                DeleteQuietly(partPath);
                DeleteQuietly(metaPath);
                received = 0;
                completed = false;
            }
            else
            {
                // TryResumeAsync：-1 = 没法续（服务器不认 Range / 资源变了），推倒重来；
                // >=0 = 已经拿全（416 收口，或者追加到 ContentRange 标的全长）。
                received = await TryResumeAsync(appliedUrl, partPath, existing, knownETag, progress, cancellationToken, pauseToken);
                completed = received >= 0;
                if (!completed)
                {
                    // Server refused the range (or the file changed) -- start over.
                    DeleteQuietly(partPath);
                    DeleteQuietly(metaPath);
                    received = 0;
                }
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
    /// 断点续传：从 <paramref name="existing"/> 开始循环发 Range 请求，直到拿全。
    /// 返回值：-1 = 没法续（服务器不认 Range / 资源变了 / 行为异常），调用方删掉 part 从零重下；
    /// &gt;= 0 = 已经拿全（416 收口，或追加到 ContentRange 标的全长）。
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
        long received = existing;

        // 以前只发一次 Range：206 追加一段就返回「追加后的总数」，调用方拿它跟 existing 比永远不等，
        // 刚续下来的字节整个被删掉重新下 —— 断点续传等于从来没生效过。现在一轮一轮续到收口。
        for (int attempt = 0; attempt < 64; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, appliedUrl);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(received, null);
            if (!string.IsNullOrEmpty(knownETag))
            {
                request.Headers.TryAddWithoutValidation("If-Range", knownETag);
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                // 416：带 If-Range 才敢下「已经拿全」的结论 —— 服务器明确说 bytes=received- 要不到，
                // 即已有的字节覆盖到（或超过）全长。全长反而更大的话是服务器行为不对劲，别把半截当完整。
                long? fullLength = response.Content.Headers.ContentRange?.Length;
                if (fullLength is null or <= 0 || received >= fullLength)
                {
                    return received;
                }

                return -1;
            }

            if (response.StatusCode != HttpStatusCode.PartialContent)
            {
                // 服务器不认 Range（或 If-Range 没对上回了 200）—— 没法续，推倒重来。
                return -1;
            }

            // If-Range failed: the resource changed under us, restart.
            string? currentETag = response.Headers.ETag?.ToString();
            if (!string.IsNullOrEmpty(knownETag) && !string.IsNullOrEmpty(currentETag)
                && !string.Equals(knownETag, currentETag, StringComparison.Ordinal))
            {
                return -1;
            }

            long? total = response.Content.Headers.ContentRange?.Length;

            await using (Stream source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var destination = new FileStream(partPath, FileMode.Append, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
            {
                received = await PumpAsync(source, destination, received, total, progress, cancellationToken, pauseToken);
            }

            // 一段 206 不够（服务器截短了）就接着要下一段；total 拿不到就靠下一轮的 416 收口。
            if (total is > 0 && received >= total.Value)
            {
                return received;
            }
        }

        // 64 轮还没收口：防御上限，按「没法续」处理。
        return -1;
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

            // sidecar 是 WriteSidecar 写的标准 JSON，必须用 JSON 解析。以前手工找引号，
            // 而 ETag 本来就带引号（GitHub / S3 形如 "abc"），写进 JSON 变成 \"abc\" ——
            // 手工解析取到的是一截反斜杠，If-Range 发的是垃圾，服务器回 200，续传报废。
            using var document = JsonDocument.Parse(File.ReadAllText(metaPath));
            if (!document.RootElement.TryGetProperty("etag", out JsonElement value)
                || value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string etag = value.GetString() ?? string.Empty;
            return etag.Length == 0 ? null : etag;
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