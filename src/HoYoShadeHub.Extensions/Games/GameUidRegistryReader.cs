using HoYoShadeHub.Core;
using Microsoft.Win32;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>Reads existing last-login UID only. It never writes registry values or reads login credentials.</summary>
public static class GameUidRegistryReader
{
    /// <summary>Verified last-login mappings only; unsupported games never borrow another game's UID field.</summary>
    public static GameUidRegistrySource? GetSource(GameBiz biz)
    {
        string? name = biz.ToGame() == GameBiz.hk4e ? GameRegistry.__LastUid___h2153286551
            : biz.ToGame() == GameBiz.hkrpg ? GameRegistry.App_LastUserID_h2841727341 : null;
        // Starward uses GENERAL_DATA_V2_LastLoginUserId for Honkai Impact 3, not ZZZ.
        string key = biz.GetGameRegistryKey();
        if (name is null || key == "HKEY_CURRENT_USER") return null;
        return new(key, name);
    }

    [SupportedOSPlatform("windows")]
    public static string? ReadLastLogin(GameBiz biz)
    {
        var source = GetSource(biz);
        if (source is null) return null;
        try { return DecodeLastLogin(Registry.GetValue(source.RegistryKey, source.ValueName, null)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException) { return null; }
    }

    public static string? DecodeLastLogin(object? value)
    {
        string? text;
        try
        {
            text = value switch
            {
                // DWORDs are signed by the Registry API, whereas UID digits are unsigned.
                int number => unchecked((uint)number).ToString(CultureInfo.InvariantCulture),
                long number when number > 0 => number.ToString(CultureInfo.InvariantCulture),
                string s when s.Length <= 64 => s.TrimEnd('\0'),
                byte[] bytes when bytes.Length <= 64 => new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0'),
                _ => null,
            };
        }
        catch (DecoderFallbackException) { return null; }
        return UidReportProtocol.IsUid(text) ? text : null;
    }
}

public sealed record GameUidRegistrySource(string RegistryKey, string ValueName);
