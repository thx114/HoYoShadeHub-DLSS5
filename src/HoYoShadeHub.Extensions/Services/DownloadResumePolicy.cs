using System.Net.Http.Headers;

namespace HoYoShadeHub.Extensions.Services;

public sealed record DownloadResumeMetadata(string Url, string? ETag);

/// <summary>Conservative byte-range checks, independent of transport and file IO.</summary>
public static class DownloadResumePolicy
{
    public static bool CanResume(DownloadResumeMetadata? metadata, string url) =>
        metadata is not null && string.Equals(metadata.Url, url, StringComparison.Ordinal)
        && IsStrongETag(metadata.ETag);

    public static bool IsStrongETag(string? etag) =>
        EntityTagHeaderValue.TryParse(etag, out var parsed) && !parsed.IsWeak && parsed.Tag != "*";

    public static bool IsCompleteRangeRejection(long received, ContentRangeHeaderValue? range,
        string knownETag, string? responseETag) =>
        range is { Unit: "bytes", HasRange: false, Length: > 0 }
        && received == range.Length.Value && IsStrongETag(responseETag)
        && string.Equals(knownETag, responseETag, StringComparison.Ordinal);

    public static bool IsValidPartialRange(long received, ContentRangeHeaderValue? range,
        long? contentLength) =>
        range is { Unit: "bytes", HasRange: true, HasLength: true }
        && range.From == received && range.To >= received && range.To < range.Length
        && (contentLength is null || contentLength == range.To - range.From + 1);
}
