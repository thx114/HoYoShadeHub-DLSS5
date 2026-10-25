using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>Visual observation only. This is neither an authorization token nor account authentication.</summary>
public static class UidReportProtocol
{
    public const int Version = 1;
    public const int MaxMessageBytes = 4096;
    public const int MaxAgeMilliseconds = 5000;
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false,
    };
    private static readonly Regex NoncePattern = new("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex UidPattern = new(
        @"(?<![A-Za-z0-9])UID(?:[ \t]*[:：#.•·]){0,3}[ \t]*([0-9](?:[ \t]?[0-9]){5,11})(?![ \t]*[0-9])(?![A-Za-z\p{Nd}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool MatchesGameProcess(string gameId, string processName) => gameId switch
    {
        "genshin" => processName.Equals("YuanShen", StringComparison.OrdinalIgnoreCase)
            || processName.Equals("GenshinImpact", StringComparison.OrdinalIgnoreCase),
        "starrail" => processName.Equals("StarRail", StringComparison.OrdinalIgnoreCase),
        "zzz" => processName.Equals("ZenlessZoneZero", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    public static string PipeName(int pid, ulong processStartFileTime)
    {
        if (pid <= 0 || processStartFileTime == 0) throw new ArgumentOutOfRangeException(nameof(pid));
        return $"HoYoShadeHub.Uid.v1.{pid}.{processStartFileTime:x16}";
    }

    public static bool Validate(UidReportRequest request, string game, int pid, ulong start) =>
        request.Version == Version && request.Operation == "observe"
        && request.GameId == game && request.Pid == pid && request.ProcessStartFileTime == start
        && request.Nonce is not null && NoncePattern.IsMatch(request.Nonce);

    public static string? ParseVisibleUid(IEnumerable<string> lines)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            if (line.Length > 4096) return null;
            foreach (Match match in UidPattern.Matches(line))
            {
                string value = match.Groups[1].Value.Replace(" ", "").Replace("\t", "");
                if (value[0] != '0') values.Add(value);
            }
        }
        return values.Count == 1 ? values.Single() : null;
    }

    public static string? Confirm(string? first, string? second) => first is not null && first == second ? first : null;

    public static bool IsUid(string? value) => value is { Length: >= 6 and <= 12 }
        && value[0] != '0' && value.All(c => c is >= '0' and <= '9');

    /// <summary>Remembered registry values have no verified observation time or current-account binding.</summary>
    public static UidReportResponse CreateResponse(UidReportRequest request, string biz, long sequence,
        string? observedUid, string? rememberedUid, long now, string serverSessionId)
    {
        bool observed = IsUid(observedUid), remembered = !observed && IsUid(rememberedUid);
        return new(Version, observed ? "observed" : remembered ? "remembered" : "unknown",
            request.GameId, biz, request.Pid, request.ProcessStartFileTime, request.Nonce, serverSessionId, sequence,
            observed ? observedUid : remembered ? rememberedUid : null,
            now, observed ? now : null, observed || remembered ? now + MaxAgeMilliseconds : now,
            observed ? "visible-window-ocr" : remembered ? "game-registry-last-login" : "none",
            observed ? "visual-observation-only" : remembered ? "not-current-session-confirmation" : "uid-unavailable-or-inconsistent");
    }
}

public sealed record UidReportRequest(int Version, string Operation, string GameId, int Pid,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    ulong ProcessStartFileTime, string Nonce);

public sealed record UidReportResponse(int Version, string Status, string GameId, string GameBiz, int Pid,
    [property: JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    ulong ProcessStartFileTime, string Nonce, string ServerSessionId, long Sequence, string? Uid, long ReportedAtUnixMs, long? ObservedAtUnixMs,
    long ExpiresAtUnixMs, string Source, string Reason);
