using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.ReShade;
using System.Security.Cryptography;

namespace HoYoShadeHub.Extensions.Tests;

internal static class WutheringReShadeInjectionTests
{
    public static int Run()
    {
        int passed = 0;
        void Check(bool ok, string name)
        {
            if (!ok) throw new Exception(name);
            Console.WriteLine("PASS " + name); passed++;
        }
        string root = Path.Combine(Path.GetTempPath(), "hysx-wuthering-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string game = Path.Combine(root, "Game", "Binaries", "Win64");
            string cache = Path.Combine(root, "Hub");
            Directory.CreateDirectory(game);
            File.WriteAllBytes(Path.Combine(game, "Client-Win64-Shipping.exe"), [0]);
            Check(WutheringLaunchPaths.ResolveClientDirectory(Path.Combine(root, "Game")) == game, "shell directory resolves actual client");
            Check(WutheringLaunchPaths.ResolveClientDirectory(game) == game, "registered client directory is preserved");
            string outer = Path.Combine(root, "Outer");
            string outerClient = Path.Combine(outer, "Wuthering Waves Game", "Binaries", "Win64");
            Directory.CreateDirectory(outerClient);
            File.WriteAllBytes(Path.Combine(outerClient, "Client-Win64-Shipping.exe"), [0]);
            Check(WutheringLaunchPaths.ResolveClientDirectory(outer) == outerClient, "launcher install root resolves nested client");

            // 各渠道真实布局。官方 PC 版中间还有一层 Client（<安装>\Wuthering Waves Game\Client\Binaries\Win64），
            // 旧代码只试到 Binaries\Win64 —— 实测就是这个漏网导致「找不到 Client-Win64-Shipping.exe 所在目录」。
            string official = Path.Combine(root, "Official");
            string officialClient = Path.Combine(official, "Wuthering Waves Game", "Client", "Binaries", "Win64");
            Directory.CreateDirectory(officialClient);
            File.WriteAllBytes(Path.Combine(officialClient, "Client-Win64-Shipping.exe"), [0]);
            Check(WutheringLaunchPaths.ResolveClientDirectory(official) == officialClient,
                "official layout (Wuthering Waves Game\\Client\\Binaries\\Win64) resolves");

            string channel = Path.Combine(root, "Channel");
            string channelClient = Path.Combine(channel, "Client", "Binaries", "Win64");
            Directory.CreateDirectory(channelClient);
            File.WriteAllBytes(Path.Combine(channelClient, "Client-Win64-Shipping.exe"), [0]);
            Check(WutheringLaunchPaths.ResolveClientDirectory(channel) == channelClient,
                "channel layout (Client\\Binaries\\Win64) resolves");

            string unknown = Path.Combine(root, "Unknown");
            string unknownClient = Path.Combine(unknown, "a", "b", "Binaries", "Win64");
            Directory.CreateDirectory(unknownClient);
            File.WriteAllBytes(Path.Combine(unknownClient, "Client-Win64-Shipping.exe"), [0]);
            Check(WutheringLaunchPaths.ResolveClientDirectory(unknown) == unknownClient,
                "unknown layout found by depth-limited fallback search");

            Check(WutheringLaunchPaths.ResolveClientDirectory(Path.Combine(root, "NotInstalled")) is null,
                "unresolvable directory returns null instead of guessing");

            // 同为 *-Win64-Shipping.exe 的别的游戏（卡拉彼丘 Calabiyau）绝不能被当成鸣潮
            string other = Path.Combine(root, "Calabiyau", "PM", "Binaries", "Win64");
            Directory.CreateDirectory(other);
            File.WriteAllBytes(Path.Combine(other, "Calabiyau-Win64-Shipping.exe"), [0]);
            Check(WutheringLaunchPaths.ResolveClientDirectory(Path.Combine(root, "Calabiyau")) is null,
                "another *-Win64-Shipping.exe is never mistaken for Wuthering Waves");
            string source = Path.Combine(root, "source.dll");
            byte[] bytes = [1, 2, 3];
            File.WriteAllBytes(source, bytes);
            ReShadeInjectionDeployment.SetEnabled(cache, game, true, source);
            Check(File.Exists(ReShadeInjectionDeployment.DllPath(cache, game)), "DLL deployed outside game");
            Check(!File.Exists(Path.Combine(game, "dxgi.dll")), "no proxy created");
            Check(ReShadeInjectionDeployment.IsEnabled(cache, game), "enable state persists");
            ReShadeInjectionDeployment.SetEnabled(cache, game, false);
            Check(!ReShadeInjectionDeployment.IsEnabled(cache, game), "disabled without unloading a running game");
            Check(File.Exists(ReShadeInjectionDeployment.DllPath(cache, game)), "disable preserves DLL");
            string proxy = Path.Combine(game, "dxgi.dll");
            File.WriteAllBytes(proxy, bytes);
            File.WriteAllText(Path.Combine(game, ".hysx-vanilla-reshade.json"),
                "sha256=" + Convert.ToHexString(SHA256.HashData(bytes)));
            ReShadeInjectionDeployment.SetEnabled(cache, game, true);
            Check(!File.Exists(proxy) && File.Exists(proxy + ".hysx-off"), "owned legacy proxy retired and backed up");
            File.WriteAllBytes(proxy, [9]);
            bool rejected = false;
            try { ReShadeInjectionDeployment.Prepare(cache, game); }
            catch (InvalidOperationException) { rejected = true; }
            Check(rejected && File.ReadAllBytes(proxy)[0] == 9, "unknown proxy blocks mixed loading and stays unchanged");
            ReShadeInjectionDeployment.SetEnabled(cache, game, false);
            ReShadeInjectionDeployment.Remove(cache, game);
            Check(File.Exists(proxy), "remove never deletes foreign proxy");
            Check(!File.Exists(ReShadeInjectionDeployment.DllPath(cache, game)), "remove only deletes external injection DLL");
            Check(!ReShadeInjectionDeployment.IsInstalled(cache, game), "removed deployment does not reappear from retained backups");
            Check(ReShadeInjectionDeployment.DeploymentDirectory(cache, game)
                != ReShadeInjectionDeployment.DeploymentDirectory(cache, root), "deployment isolated per game");
            Console.WriteLine($"{passed} focused offline tests passed; no game or injection executed.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }
}
