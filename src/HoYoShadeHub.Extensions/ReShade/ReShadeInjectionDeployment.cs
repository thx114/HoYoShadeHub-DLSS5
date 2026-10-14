using System.Security.Cryptography;
using System.Text;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>Per-game external ReShade DLL and opt-in state. Never creates a game-directory proxy.</summary>
public static class ReShadeInjectionDeployment
{
    private const string LegacyMarker = ".hysx-vanilla-reshade.json";
    private const string EnabledMarker = "enabled";

    public static string DeploymentDirectory(string root, string gameDirectory)
    {
        string identity = Path.GetFullPath(gameDirectory).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(root, "injected", key);
    }

    public static string DllPath(string root, string gameDirectory)
        => Path.Combine(DeploymentDirectory(root, gameDirectory), "ReShade64.dll");

    public static bool IsInstalled(string root, string gameDirectory)
    {
        if (File.Exists(DllPath(root, gameDirectory))) return true;
        // A persisted disabled state with no DLL means explicitly removed, not pending legacy migration.
        if (File.Exists(Path.Combine(DeploymentDirectory(root, gameDirectory), EnabledMarker))) return false;
        return OwnedLegacyDll(gameDirectory) is not null;
    }

    public static bool IsEnabled(string root, string gameDirectory)
    {
        string state = Path.Combine(DeploymentDirectory(root, gameDirectory), EnabledMarker);
        if (File.Exists(state)) return File.ReadAllText(state).Trim() == "1";
        return OwnedLegacyDll(gameDirectory) == Path.Combine(gameDirectory, "dxgi.dll");
    }

    public static void Prepare(string root, string gameDirectory, string? sourceDll = null)
    {
        string proxy = Path.Combine(gameDirectory, "dxgi.dll");
        string? legacy = OwnedLegacyDll(gameDirectory);
        if (File.Exists(proxy) && legacy != proxy)
            throw new InvalidOperationException("游戏目录有无法确认归属的 dxgi.dll，未改动；请先处理该代理文件，避免和 ReShade 注入重复加载。");

        string dll = DllPath(root, gameDirectory);
        if (!File.Exists(dll))
        {
            string? source = legacy ?? sourceDll;
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                throw new FileNotFoundException("没有可注入的 ReShade64.dll，请先安装 ReShade。");
            Directory.CreateDirectory(Path.GetDirectoryName(dll)!);
            File.Copy(source, dll, overwrite: false);
        }

        // Only retire the proxy whose hash matches this launcher's installation marker.
        if (legacy == proxy)
        {
            string backup = proxy + ".hysx-off";
            if (File.Exists(backup)) backup += "." + Guid.NewGuid().ToString("N");
            File.Move(proxy, backup);
        }
    }

    public static void SetEnabled(string root, string gameDirectory, bool enabled, string? sourceDll = null)
    {
        if (enabled) Prepare(root, gameDirectory, sourceDll);
        else
        {
            string proxy = Path.Combine(gameDirectory, "dxgi.dll");
            if (OwnedLegacyDll(gameDirectory) == proxy) Prepare(root, gameDirectory, sourceDll);
        }
        string folder = DeploymentDirectory(root, gameDirectory);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, EnabledMarker), enabled ? "1" : "0");
    }

    public static void Remove(string root, string gameDirectory)
    {
        SetEnabled(root, gameDirectory, false);
        string dll = DllPath(root, gameDirectory);
        if (File.Exists(dll)) File.Delete(dll);
        // Keep the disabled state and any legacy backups; never delete a game-directory DLL.
    }

    private static string? OwnedLegacyDll(string gameDirectory)
    {
        string marker = Path.Combine(gameDirectory, LegacyMarker);
        if (!File.Exists(marker)) return null;
        string? hash = File.ReadLines(marker)
            .Select(line => line.Split('=', 2))
            .Where(pair => pair.Length == 2 && pair[0].Trim().Equals("sha256", StringComparison.OrdinalIgnoreCase))
            .Select(pair => pair[1].Trim()).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(hash)) return null;
        foreach (string name in new[] { "dxgi.dll", "dxgi.dll.hysx-off" })
        {
            string path = Path.Combine(gameDirectory, name);
            if (!File.Exists(path)) continue;
            using var input = File.OpenRead(path);
            if (Convert.ToHexString(SHA256.HashData(input)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                return path;
        }
        return null;
    }
}
