using HoYoShadeHub.Extensions.Networking;
using System.Net.Http;

namespace HoYoShadeHub.Extensions.Services;

public sealed record DownloadProgress(long BytesReceived, long? TotalBytes)
{
    public double? Percent => TotalBytes is > 0 ? (double)BytesReceived / TotalBytes.Value * 100d : null;
}

/// <summary>
/// 带进度、带 sha256 校验的下载器。
/// </summary>
public sealed class DownloadService
{
    private readonly HttpClient _httpClient;

    public DownloadService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? HysxHttp.CreateClient();
    }

    public async Task<long> DownloadToFileAsync(
        string url,
        string targetFile,
        string? expectedSha256 = null,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string? directory = Path.GetDirectoryName(targetFile);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using HttpResponseMessage response = await _httpClient.GetAsync(
            HysxHttp.Apply(url), HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;
        long received = 0;
        byte[] buffer = new byte[81920];

        await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var destination = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress?.Report(new DownloadProgress(received, total));
            }
        }

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            string actual = await HysxUtil.ComputeSha256Async(targetFile, cancellationToken);
            if (!HysxUtil.IsHashEqual(actual, expectedSha256))
            {
                File.Delete(targetFile);
                throw new InvalidDataException($"sha256 校验失败：期望 {expectedSha256}，实际 {actual}");
            }
        }

        return received;
    }
}
