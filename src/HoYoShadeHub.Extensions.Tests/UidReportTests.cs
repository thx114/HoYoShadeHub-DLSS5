using HoYoShadeHub.Core;
using HoYoShadeHub.Extensions.Games;
using System.Text;
using System.Text.Json;

namespace HoYoShadeHub.Extensions.Tests;

internal static class UidReportTests
{
    public static int Run(string[] args)
    {
        int passed = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException(name);
            passed++;
        }
        string? Parse(params string[] lines) => UidReportProtocol.ParseVisibleUid(lines);
        foreach (string text in new[] { "UID: 123456789", "UID：123456789", "uid 1 2 3 4 5 6 7 8 9", "UID # 123456789", "UID:123456789 Player",
            "UID.• 123456789", "UID · 123456789" })
            Check(Parse(text) == "123456789", "unique labeled UID");
        foreach (string text in new[] { "123456789", "xUID 123456789", "UID 000123456", "UID 12345",
            "UID 1234567890123", "UID 1 2 3 4 5 6 7 8 9 0 1 2 3", "UID １２３４５６７８９",
            "UID 123456a", "UID 123456７", "UID\n123456789", new string('x', 4097) })
            Check(Parse(text) is null, "reject unlabeled, truncated or invalid UID");
        Check(Parse("UID 123456789", "UID 123456789") == "123456789", "duplicate same label is unambiguous");
        Check(Parse("UID 123456789", "UID 234567890") is null, "multiple distinct UIDs rejected");
        Check(UidReportProtocol.Confirm("123456789", "123456789") == "123456789", "consistent frames");
        Check(UidReportProtocol.Confirm("123456789", "234567890") is null, "changed account");
        Check(UidReportProtocol.Confirm("123456789", null) is null, "unreadable frame invalidates");
        Check(UidReportProtocol.Confirm(null, null) is null, "no cached value");
        var request = new UidReportRequest(1, "observe", "genshin", 123, 133713371337133713, new string('a', 64));
        Check(UidReportProtocol.Validate(request, "genshin", 123, request.ProcessStartFileTime), "valid process-bound request");
        foreach (var invalid in new[] { request with { Version = 2 }, request with { Operation = "allow" },
            request with { GameId = "zzz" }, request with { Pid = 124 }, request with { ProcessStartFileTime = 1 },
            request with { Nonce = new string('A', 64) }, request with { Nonce = "short" }, request with { Nonce = null! } })
            Check(!UidReportProtocol.Validate(invalid, "genshin", 123, request.ProcessStartFileTime), "reject wrong binding or nonce");
        Check(UidReportProtocol.MatchesGameProcess("genshin", "YuanShen"), "CN Genshin process");
        Check(UidReportProtocol.MatchesGameProcess("genshin", "GenshinImpact"), "global Genshin process");
        Check(UidReportProtocol.MatchesGameProcess("starrail", "StarRail"), "StarRail process");
        Check(UidReportProtocol.MatchesGameProcess("zzz", "ZenlessZoneZero"), "ZZZ process");
        Check(!UidReportProtocol.MatchesGameProcess("starrail", "YuanShen"), "cross-game process rejected");
        string json = JsonSerializer.Serialize(request, UidReportProtocol.Json);
        Check(json.Contains("\"processStartFileTime\":\"133713371337133713\""), "FILETIME preserves integer precision in JSON");
        Check(JsonSerializer.Deserialize<UidReportRequest>(json, UidReportProtocol.Json) == request, "request roundtrip");
        foreach (string invalid in new[] { "{}", json.Replace("\"version\":1", "\"version\":1,\"unexpected\":1"),
            json.Replace("\"version\":1", "\"version\":\"1\""), json.Replace("\"version\":1", "\"version\":1,\"version\":1") })
        {
            bool rejected = false;
            try { JsonSerializer.Deserialize<UidReportRequest>(invalid, UidReportProtocol.Json); }
            catch (JsonException) { rejected = true; }
            Check(rejected, "strict schema rejects malformed request");
        }
        foreach (object value in new object[] { 123456789, 123456789L, "123456789\0", Encoding.UTF8.GetBytes("123456789\0"), unchecked((int)3123456789u) })
            Check(GameUidRegistryReader.DecodeLastLogin(value) is not null, "decode last-login UID formats");
        foreach (object? value in new object?[] { null, 0, -1L, "0", "001234567", "123456\0x", " uid:123456789 ", new byte[] { 0xff, 0xfe }, new byte[65], "account-token" })
            Check(GameUidRegistryReader.DecodeLastLogin(value) is null, "reject invalid registry values");
        Check(GameUidRegistryReader.DecodeLastLogin(unchecked((int)3123456789u)) == "3123456789", "unsigned DWORD UID");
        foreach (var item in new[]
        {
            ("hk4e_cn", GameRegistry.GamePath_hk4e_cn, GameRegistry.__LastUid___h2153286551),
            ("hk4e_bilibili", GameRegistry.GamePath_hk4e_cn, GameRegistry.__LastUid___h2153286551),
            ("hk4e_global", GameRegistry.GamePath_hk4e_global, GameRegistry.__LastUid___h2153286551),
            ("hk4e_cn_beta", GameRegistry.GamePath_hk4e_cn_beta, GameRegistry.__LastUid___h2153286551),
            ("hk4e_os_beta", GameRegistry.GamePath_hk4e_os_beta, GameRegistry.__LastUid___h2153286551),
            ("hkrpg_cn", GameRegistry.GamePath_hkrpg_cn, GameRegistry.App_LastUserID_h2841727341),
            ("hkrpg_bilibili", GameRegistry.GamePath_hkrpg_cn, GameRegistry.App_LastUserID_h2841727341),
            ("hkrpg_global", GameRegistry.GamePath_hkrpg_global, GameRegistry.App_LastUserID_h2841727341),
            ("hkrpg_beta", GameRegistry.GamePath_hkrpg_beta, GameRegistry.App_LastUserID_h2841727341),
        })
            Check(GameUidRegistryReader.GetSource(new GameBiz(item.Item1)) == new GameUidRegistrySource(item.Item2, item.Item3),
                "verified source mapped to exact selected game/region");
        foreach (string unsupported in new[] { "nap_cn", "nap_global", "nap_bilibili", "bh3_cn", "hk4e", "hkrpg", "unknown_cn" })
            Check(GameUidRegistryReader.GetSource(new GameBiz(unsupported)) is null, "unsupported game/region has no borrowed UID field");
        var observed = UidReportProtocol.CreateResponse(request, "hk4e_cn", 1, "123456789", "234567890", 10000, "session");
        Check(observed.Status == "observed" && observed.Source == "visible-window-ocr" && observed.ObservedAtUnixMs == 10000
            && observed.Uid == "123456789" && observed.ExpiresAtUnixMs == 15000, "fresh visual observation wins over remembered account");
        var remembered = UidReportProtocol.CreateResponse(request, "hk4e_cn", 2, null, "234567890", 10000, "session");
        Check(remembered.Status == "remembered" && remembered.ObservedAtUnixMs is null
            && remembered.Source == "game-registry-last-login", "last login cannot become current observation");
        var unknown = UidReportProtocol.CreateResponse(request, "hk4e_cn", 3, null, null, 10000, "session");
        Check(unknown.Status == "unknown" && unknown.Uid is null && unknown.ExpiresAtUnixMs == unknown.ReportedAtUnixMs,
            "unknown contains no stale UID");
        if (args.Contains("--local-registry") && OperatingSystem.IsWindows())
        {
            foreach (string biz in new[] { "hk4e_cn", "hkrpg_cn", "hkrpg_global", "nap_cn" })
            {
                string? uid = GameUidRegistryReader.ReadLastLogin(new GameBiz(biz));
                Console.WriteLine($"Local registry {biz}: {(uid is null ? "unavailable" : "valid last-login format")}; value withheld");
            }
        }
        Console.WriteLine($"PASS {passed} UID protocol and source checks");
        return 0;
    }
}
