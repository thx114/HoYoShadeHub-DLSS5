using HoYoShadeHub.Features.Xxmi;

namespace HoYoShadeHub.Extensions.Tests;

internal static class ModPreviewTests
{
    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "mod-preview-" + Guid.NewGuid().ToString("N"));
        int passed = 0;
        void Check(bool result, string name)
        {
            if (!result) throw new InvalidOperationException(name);
            Console.WriteLine("PASS " + name);
            passed++;
        }
        string Put(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(root, relative));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
            return path;
        }
        try
        {
            Check(XxmiModManager.LocalPreview(null) is null, "missing mod path");
            Put("VodyanitsaHairADiffuse.png");
            Check(XxmiModManager.LocalPreview(root) is null, "material textures never become preview");
            string nested = Put("Summer Bikini/preview.png");
            Check(string.Equals(XxmiModManager.LocalPreview(root), nested, StringComparison.OrdinalIgnoreCase), "Rocket wrapper directory preview");
            string direct = Put("Preview.JPG");
            Check(string.Equals(XxmiModManager.LocalPreview(root), direct, StringComparison.OrdinalIgnoreCase), "root preview takes priority over nested PNG");
            File.Delete(direct);
            File.Delete(nested);
            string deeper = Put("Wrapper/Variant/Sub/preview.webp");
            Check(string.Equals(XxmiModManager.LocalPreview(root), deeper, StringComparison.OrdinalIgnoreCase), "three nested levels supported");
            string shallow = Put("Wrapper/preview.bmp");
            Check(string.Equals(XxmiModManager.LocalPreview(root), shallow, StringComparison.OrdinalIgnoreCase), "nearest preview wins");
            File.Delete(shallow);
            File.Delete(deeper);
            Put("Wrapper/Variant/Sub/TooDeep/preview.png");
            Check(XxmiModManager.LocalPreview(root) is null, "search depth bounded");
            Console.WriteLine($"Mod preview tests: PASS {passed}, FAIL 0");
            return 0;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
