using System.Buffers.Binary;
using System.Text;
using HoYoShadeHub.Extensions.Games;

namespace HoYoShadeHub.Extensions.Tests;

internal static class StarRailXxmiLaunchRoutingTests
{
    public static (int Passed, int Failed) Run()
    {
        int passed = 0, failed = 0;
        void Check(bool ok, string name)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            if (ok) passed++; else failed++;
        }
        string root = Path.Combine(Path.GetTempPath(), "srmi-route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] Image(string first, string second, bool nullFunction = false)
            {
                byte[] image = new byte[1024];
                void U16(int at, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(at), value);
                void U32(int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(at), value);
                void Text(int at, string value) => Encoding.ASCII.GetBytes(value + "\0").CopyTo(image, at);
                image[0] = (byte)'M'; image[1] = (byte)'Z'; U32(0x3c, 0x80);
                Text(0x80, "PE"); U16(0x84, 0x8664); U16(0x86, 1); U16(0x94, 240); U16(0x96, 0x2022);
                U16(0x98, 0x20b); U32(0x98 + 32, 0x1000); U32(0x98 + 36, 0x200);
                U32(0x98 + 56, 0x2000); U32(0x98 + 60, 0x200); U32(0x98 + 108, 16);
                U32(0x98 + 112, 0x1000); U32(0x98 + 116, 0x200);
                Text(0x188, ".edata"); U32(0x188 + 8, 0x200); U32(0x188 + 12, 0x1000);
                U32(0x188 + 16, 0x200); U32(0x188 + 20, 0x200); U32(0x188 + 36, 0x40000040);
                U32(0x200 + 16, 1); U32(0x200 + 20, 2); U32(0x200 + 24, 2);
                U32(0x200 + 28, 0x1040); U32(0x200 + 32, 0x1048); U32(0x200 + 36, 0x1050);
                U32(0x240, 0x1100); U32(0x244, nullFunction ? 0u : 0x1110);
                U32(0x248, 0x1060); U32(0x24c, 0x10a0); U16(0x250, 0); U16(0x252, 1);
                Text(0x260, first); Text(0x2a0, second);
                return image;
            }
            string paired = Path.Combine(root, "d3d11.dll");
            string opti = Path.Combine(root, "OptiScaler.dll");
            File.WriteAllBytes(paired, Image(StarRailXxmiLaunchRouting.PrivateDx12Export, StarRailXxmiLaunchRouting.PreFlipExport));
            File.WriteAllText(opti, "CPU fixture");
            Check(StarRailXxmiLaunchRouting.HasPairedLoaderExports(paired), "SRMI paired exports are read from PE without executing the DLL");
            bool Route(bool xxmi = true, bool inject = false, bool starward = false, bool opt = true, bool modules = false, bool nativeDx12 = false,
                string? biz = "hkrpg_bilibili", string? game = "explicit-game", string? optiPath = null, string? loader = null) =>
                StarRailXxmiLaunchRouting.CanUsePairedGraphicsLaunch(xxmi, inject, starward, opt, modules, nativeDx12, biz, game,
                    optiPath ?? opti, loader ?? paired);
            Check(Route(), "StarRail + paired SRMI + Opt with no modules/Bridge enables XXMI-first launch");
            Check(Route(biz: "hkrpg_cn") && Route(biz: "hkrpg_global"), "Paired route covers all StarRail regions");
            Check(StarRailXxmiLaunchRouting.GraphicsLibraries(opti, "ReShade64.dll").SequenceEqual(new[] { opti, "ReShade64.dll" }),
                "Dedicated graphics extras keep Opt before ReShade without a Bridge");
            Check(StarRailXxmiLaunchRouting.GraphicsLibraries(opti, null).SequenceEqual(new[] { opti }),
                "No ReShade selection loads only Opt after SRMI");
            Check(!Route(xxmi: false), "XXMI off keeps ordinary StarRail route");
            Check(!Route(inject: true), "Injection-only mode does not create a paired-chain process");
            Check(!Route(starward: true), "Starward startup keeps existing route");
            Check(!Route(modules: true), "Modules-enabled route is not replaced by the no-Bridge route");
            Check(!Route(nativeDx12: true), "Native DX12 does not use a D3D11 model loader");
            Check(!Route(opt: false), "Opt off keeps existing route");
            Check(!Route(biz: "hk4e_cn") && !Route(biz: "nap_cn") && !Route(biz: null), "Paired SRMI route does not change GIMI or ZZMI routing");
            Check(!Route(game: ""), "Missing game path rejects early launch");
            Check(!Route(optiPath: Path.Combine(root, "missing-opti.dll")), "Missing selected Opt binary rejects chain");
            Check(!Route(loader: Path.Combine(root, "missing-srmi.dll")), "Missing SRMI rejects chain");
            string unpaired = Path.Combine(root, "unpaired.dll");
            File.WriteAllBytes(unpaired, Image(StarRailXxmiLaunchRouting.PrivateDx12Export, "OtherExport"));
            Check(!Route(loader: unpaired), "SRMI with only one capability keeps ordinary route");
            File.WriteAllBytes(unpaired, Image("OtherExport", StarRailXxmiLaunchRouting.PreFlipExport));
            Check(!StarRailXxmiLaunchRouting.HasPairedLoaderExports(unpaired), "PreFlip alone is insufficient");
            File.WriteAllBytes(unpaired, Image(StarRailXxmiLaunchRouting.PrivateDx12Export, StarRailXxmiLaunchRouting.PreFlipExport, true));
            Check(!StarRailXxmiLaunchRouting.HasPairedLoaderExports(unpaired), "Export name with a null function is not a capability");
            File.WriteAllText(unpaired, StarRailXxmiLaunchRouting.PrivateDx12Export + StarRailXxmiLaunchRouting.PreFlipExport);
            Check(!StarRailXxmiLaunchRouting.HasPairedLoaderExports(unpaired), "Marker-looking strings outside a PE export table are rejected");
            byte[] corrupt = Image(StarRailXxmiLaunchRouting.PrivateDx12Export, StarRailXxmiLaunchRouting.PreFlipExport);
            BinaryPrimitives.WriteUInt32LittleEndian(corrupt.AsSpan(0x200 + 32), uint.MaxValue);
            File.WriteAllBytes(unpaired, corrupt);
            Check(!StarRailXxmiLaunchRouting.HasPairedLoaderExports(unpaired), "Malformed export RVAs fail closed without crashing launcher");
            byte[] wrongArch = Image(StarRailXxmiLaunchRouting.PrivateDx12Export, StarRailXxmiLaunchRouting.PreFlipExport);
            BinaryPrimitives.WriteUInt16LittleEndian(wrongArch.AsSpan(0x84), 0x14c);
            File.WriteAllBytes(unpaired, wrongArch);
            Check(!StarRailXxmiLaunchRouting.HasPairedLoaderExports(unpaired), "Non-x64 model loader does not enable chain");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        return (passed, failed);
    }
}
