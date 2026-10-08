using System.Text;
using System.Text.RegularExpressions;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>Only edits Rocket's supported extra-DLL keys and the bridge graphics chain.
/// This preparation API deliberately has no process creation or injection operations.</summary>
public static class RocketIntegration
{
    public const string RocketConfigEnvironment = "HYSX_ROCKET_CONFIG";
    public const string DefaultRocketConfigPath = @"D:\APPS\Rocket 管理器最新版\backing\config.ini";
    public const string WaitingText = "等待 Rocket 启动游戏";
    public const string BridgeChainFileName = "Dx11FsrBridge.chain.txt";
    public sealed record PrepareResult(bool Success, string Message, string? ConfigPath = null, string? BackupPath = null);
    public static bool SupportsGame(string? biz) => biz?.StartsWith("hk4e_", StringComparison.OrdinalIgnoreCase) == true;

    public static string? FindConfigPath(string? configuredPath = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return File.Exists(configuredPath) ? Path.GetFullPath(configuredPath) : null;
        string? environment = Environment.GetEnvironmentVariable(RocketConfigEnvironment);
        if (!string.IsNullOrWhiteSpace(environment))
            return File.Exists(environment) ? Path.GetFullPath(environment) : null;
        return File.Exists(DefaultRocketConfigPath) ? DefaultRocketConfigPath : null;
    }

    /// <summary>Construct an absolute graphics-only chain. GIMI remains exclusively Rocket-owned.</summary>
    public static IReadOnlyList<string> BuildGraphicsChain(string bridgeDll, string? optiDll, string? shadeDll)
    {
        RequireDll(bridgeDll, "Dx11FsrBridge.dll");
        List<string> steps = ["wait dxgi.dll"];
        if (!string.IsNullOrWhiteSpace(optiDll))
        {
            RequireDll(optiDll, "OptiScaler.dll");
            steps.Add("load " + Path.GetFullPath(optiDll));
            steps.Add("wait " + Path.GetFileName(optiDll));
        }
        if (!string.IsNullOrWhiteSpace(shadeDll))
        {
            RequireDll(shadeDll, "ReShade64.dll");
            steps.Add("load " + Path.GetFullPath(shadeDll));
        }
        if (steps.Count == 1) throw new InvalidOperationException("Rocket 模式至少需要启用 OptiScaler 或 HoYoShade。");
        return steps;
    }

    public static PrepareResult EnsureGraphicsChain(string bridgeDll, string? optiDll, string? shadeDll)
    {
        try
        {
            var steps = BuildGraphicsChain(bridgeDll, optiDll, shadeDll);
            string chain = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(bridgeDll))!, BridgeChainFileName);
            string text = "# Rocket owns GIMI; Hub prepares graphics only, never starts the game.\r\n"
                + string.Join("\r\n", steps) + "\r\n";
            byte[] wanted = new UTF8Encoding(false).GetBytes(text);
            string? backup = WriteBackedUp(chain, wanted, "rocket-chain");
            if (!File.ReadAllBytes(chain).SequenceEqual(wanted)) throw new IOException("桥加载链回读验证失败。");
            return new(true, WaitingText, chain, backup);
        }
        catch (Exception ex) { return new(false, "准备桥加载链失败：" + ex.Message); }
    }

    /// <summary>Reject unknown formats; preserve all unrelated Rocket options byte-for-byte.</summary>
    public static byte[] BuildRocketConfiguration(byte[] original, string bridgeDllPath)
    {
        RequireDll(bridgeDllPath, "Dx11FsrBridge.dll");
        bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
        string text = new UTF8Encoding(false, true).GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0));
        string section = "";
        int pathCount = 0, enabledCount = 0;
        string updated = Regex.Replace(text, @"[^\r\n]+", match =>
        {
            string line = match.Value, trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) { section = trimmed[1..^1].Trim(); return line; }
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) return line;
            int equal = line.IndexOf('='); if (equal < 0) return line;
            string key = line[..equal].Trim(); string? wanted = null;
            if (section == "用户设置" && key == "原神DLL路径") { ++pathCount; wanted = Path.GetFullPath(bridgeDllPath); }
            if (section == "注入设置" && key == "原神第三方DLL启用") { ++enabledCount; wanted = "true"; }
            if (wanted is null || string.Equals(line[(equal + 1)..].Trim(), wanted, StringComparison.Ordinal)) return line;
            int valueStart = equal + 1; while (valueStart < line.Length && char.IsWhiteSpace(line[valueStart])) ++valueStart;
            return line[..valueStart] + wanted;
        });
        if (pathCount != 1 || enabledCount != 1)
            throw new InvalidDataException("Rocket 配置格式不明确：必须各有一个 [用户设置] 原神DLL路径和 [注入设置] 原神第三方DLL启用；未修改配置。");
        if (text == updated) return original;
        byte[] content = Encoding.UTF8.GetBytes(updated);
        return bom ? Encoding.UTF8.GetPreamble().Concat(content).ToArray() : content;
    }

    public static PrepareResult Prepare(string? configPath, string bridgeDllPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                return new(false, "找不到 Rocket config.ini，请在 Rocket 设置中选择安装目录。", configPath);
            byte[] original = File.ReadAllBytes(configPath);
            byte[] updated = BuildRocketConfiguration(original, bridgeDllPath);
            string? backup = WriteBackedUp(configPath, updated, "hysx-rocket", original);
            return new(true, WaitingText, Path.GetFullPath(configPath), backup);
        }
        catch (Exception ex) { return new(false, "准备 Rocket 联动失败：" + ex.Message, configPath); }
    }

    private static void RequireDll(string path, string expectedName)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) || !string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
            throw new FileNotFoundException("缺少所选组件：" + expectedName, path);
        if (path.Contains('\r') || path.Contains('\n')) throw new InvalidDataException("DLL 路径包含换行符。");
    }

    private static string? WriteBackedUp(string path, byte[] bytes, string tag, byte[]? expected = null)
    {
        bool existed = File.Exists(path);
        byte[] original = existed ? File.ReadAllBytes(path) : [];
        if (expected is not null && !original.SequenceEqual(expected)) throw new IOException("配置被另一程序修改，请重新准备。");
        if (existed && original.SequenceEqual(bytes)) return null;
        string suffix = DateTime.Now.ToString("yyyyMMdd-HHmmssfff") + "-" + Guid.NewGuid().ToString("N")[..8];
        string? backup = existed ? path + ".bak-before-" + tag + "-" + suffix : null;
        if (backup is not null) File.WriteAllBytes(backup, original);
        string temporary = path + ".tmp-" + suffix;
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (existed && !File.ReadAllBytes(path).SequenceEqual(original)) throw new IOException("配置被另一程序修改，请重新准备。");
            if (!existed && File.Exists(path)) throw new IOException("配置文件已由另一程序创建，请重新准备。");
            File.Move(temporary, path, overwrite: existed);
            return backup;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
