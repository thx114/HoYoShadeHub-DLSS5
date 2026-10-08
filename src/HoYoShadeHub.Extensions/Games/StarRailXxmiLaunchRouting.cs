using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Text;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>Opt-in SRMI/Opt route without modules or Bridge; inspect exports without loading any native DLL.</summary>
public static class StarRailXxmiLaunchRouting
{
    public const string PrivateDx12Export = "XXMIPrivateDx12PassthroughVersion";
    public const string PreFlipExport = "XXMIPreFlipCaptureVersion";

    public static bool CanUsePairedGraphicsLaunch(bool xxmiEnabled, bool injectMode, bool starward,
        bool optiScalerEnabled, bool modulesEnabled, bool nativeDx12, string? gameBiz, string? gameInstallPath,
        string? optiDllPath, string? srmiDllPath) =>
        xxmiEnabled && !injectMode && !starward && optiScalerEnabled && !modulesEnabled && !nativeDx12
        && gameBiz?.StartsWith("hkrpg_", StringComparison.OrdinalIgnoreCase) == true
        && !string.IsNullOrWhiteSpace(gameInstallPath)
        && File.Exists(optiDllPath) && HasPairedLoaderExports(srmiDllPath);

    // Only selected graphics libraries; importer-configured extras must not put
    // another ReShade before Opt in this dedicated route.
    public static IReadOnlyList<string> GraphicsLibraries(string optiDllPath, string? shadeDllPath) =>
        string.IsNullOrWhiteSpace(shadeDllPath) ? [optiDllPath] : [optiDllPath, shadeDllPath];

    public static bool HasPairedLoaderExports(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            using FileStream stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (pe.PEHeaders.CoffHeader.Machine != Machine.Amd64) return false;
            var directory = pe.PEHeaders.PEHeader?.ExportTableDirectory;
            if (directory is null || directory.Value.RelativeVirtualAddress == 0 || directory.Value.Size < 40) return false;
            ReadOnlySpan<byte> table = pe.GetSectionData(directory.Value.RelativeVirtualAddress).GetContent().AsSpan();
            if (table.Length < 40) return false;
            int functionsCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(table[20..]));
            int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(table[24..]));
            if (count <= 0 || count > 65536 || functionsCount <= 0 || functionsCount > 65536) return false;
            int functionsRva = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(table[28..]));
            int namesRva = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(table[32..]));
            int ordinalsRva = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(table[36..]));
            if (functionsRva == 0 || namesRva == 0 || ordinalsRva == 0) return false;
            ReadOnlySpan<byte> functions = pe.GetSectionData(functionsRva).GetContent().AsSpan();
            ReadOnlySpan<byte> names = pe.GetSectionData(namesRva).GetContent().AsSpan();
            ReadOnlySpan<byte> ordinals = pe.GetSectionData(ordinalsRva).GetContent().AsSpan();
            if (names.Length < count * 4 || ordinals.Length < count * 2 || functions.Length < functionsCount * 4) return false;
            bool dx12 = false, preFlip = false;
            for (int i = 0; i < count; i++)
            {
                int ordinal = BinaryPrimitives.ReadUInt16LittleEndian(ordinals[(i * 2)..]);
                if (ordinal >= functionsCount || BinaryPrimitives.ReadUInt32LittleEndian(functions[(ordinal * 4)..]) == 0) continue;
                int nameRva = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(names[(i * 4)..]));
                if (nameRva == 0) continue;
                ReadOnlySpan<byte> bytes = pe.GetSectionData(nameRva).GetContent().AsSpan();
                int end = bytes.IndexOf((byte)0);
                if (end <= 0 || end > 128) continue;
                string name = Encoding.ASCII.GetString(bytes[..end]);
                dx12 |= name == PrivateDx12Export;
                preFlip |= name == PreFlipExport;
            }
            return dx12 && preFlip;
        }
        catch
        {
            return false;
        }
    }
}
