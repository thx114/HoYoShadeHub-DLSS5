using System.Text.Json;

namespace HoYoShadeHub.Extensions.ReShade;

public sealed record PackConsentRecord(string Hash, HashSet<string> Disabled)
{
    private static bool ValidHash(string hash) => hash.Length == 64 && hash.All(Uri.IsHexDigit);

    public static string Serialize(string hash, IEnumerable<string> disabled)
    {
        string[] names = disabled.ToArray();
        if (!ValidHash(hash) || names.Any(string.IsNullOrWhiteSpace)
            || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidDataException("Invalid pack consent record.");
        return JsonSerializer.Serialize(new { schema = 1, hash, disabled = names });
    }

    public static PackConsentRecord? Parse(string? raw)
    {
        if (raw is null) return null;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var fields = root.EnumerateObject().ToArray();
            if (fields.Length != 3 || fields.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 3
                || !root.TryGetProperty("schema", out var schema) || !schema.TryGetInt32(out int version) || version != 1
                || !root.TryGetProperty("hash", out var hash) || hash.ValueKind != JsonValueKind.String
                || !ValidHash(hash.GetString()!) || !root.TryGetProperty("disabled", out var disabled)
                || disabled.ValueKind != JsonValueKind.Array) return null;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in disabled.EnumerateArray())
                if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())
                    || !names.Add(item.GetString()!)) return null;
            return new(hash.GetString()!, names);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    public static PackConsentRecord? ParseLegacy(string? hash, string? raw)
    {
        // Delimited historical records cannot distinguish a literal pipe from multiple names.
        if (hash is null || !ValidHash(hash) || raw is null || !raw.StartsWith(hash + ":", StringComparison.Ordinal)) return null;
        string name = raw[(hash.Length + 1)..];
        if (name.Contains('|') || (name.Length > 0 && string.IsNullOrWhiteSpace(name))) return null;
        return new(hash, name.Length == 0 ? new(StringComparer.Ordinal) : new(new[] { name }, StringComparer.Ordinal));
    }
}
