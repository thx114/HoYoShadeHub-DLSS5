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
    /// <summary>原神专用（FSR 桥链）——火箭的 d3d11 主 DLL 是 GIMI，链只对原神成立。</summary>
    public static bool SupportsGame(string? biz) => biz?.StartsWith("hk4e_", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>
    /// 火箭配置里的游戏名（所有键的前缀）。null = 火箭没有这个游戏的后端。
    /// 火箭 <c>backing\Config\</c> 的后端：YS 原神 / SR 星穹铁道 / ZZZ 绝区零 / WW 鸣潮 /
    /// AE 终末地（EFMI）/ NE 异环（XXMI 没有它的导入器，不收）。
    /// 键名和节名用同一套中文名，见 config.ini 的 <c>原神DLL路径</c>、<c>绝区零包名</c>、
    /// <c>崩坏：星穹铁道包名</c>。
    /// </summary>
    public static string? GameNameFor(string? gameBiz, string? gameName)
    {
        string biz = gameBiz?.ToLowerInvariant() ?? string.Empty;
        if (biz.StartsWith("hk4e", StringComparison.Ordinal)) return "原神";
        if (biz.StartsWith("hkrpg", StringComparison.Ordinal)) return "崩坏：星穹铁道";
        if (biz.StartsWith("nap", StringComparison.Ordinal)) return "绝区零";

        // 自定义游戏没有 GameBiz，只有名字（鸣潮 / 终末地 就是这种）
        string name = gameName?.ToLowerInvariant() ?? string.Empty;
        if (name.Contains("鸣潮") || name.Contains("wuthering")) return "鸣潮";
        if (name.Contains("终末地") || name.Contains("endfield")) return "终末地";
        return null;
    }

    public static bool SupportsRocketGame(string? gameBiz, string? gameName) => GameNameFor(gameBiz, gameName) is not null;

    /// <summary>插件 DLL 列表的分隔符（config.ini 里就是竖线，见 <c>原神插件DLL列表 = a|b</c>）。</summary>
    public const char PluginListSeparator = '|';

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

    /// <summary>
    /// 写「<c>&lt;游戏名&gt;插件DLL列表</c>」和「<c>&lt;游戏名&gt;插件启用列表</c>」（都在 <c>[用户设置]</c> 节）：
    /// 火箭按这个列表把多份第三方 DLL 注进游戏进程，列表顺序 = 注入顺序（OptiScaler 在前、ReShade 在后，
    /// 和原神链一致）。键不存在就插到 <c>[用户设置]</c> 节末尾，其余行逐字节保留。
    /// </summary>
    public static byte[] BuildPluginListConfiguration(byte[] original, string gameName, IReadOnlyList<string> dllPaths)
    {
        if (string.IsNullOrWhiteSpace(gameName)) throw new InvalidDataException("缺少火箭配置里的游戏名。");
        if (dllPaths.Count == 0) throw new InvalidDataException("插件 DLL 列表为空。");
        foreach (string path in dllPaths) RequirePluginDll(path);

        bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
        string text = new UTF8Encoding(false, true).GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0));
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string dllKey = gameName + "插件DLL列表";
        string enabledKey = gameName + "插件启用列表";
        string dllValue = string.Join(PluginListSeparator, dllPaths.Select(Path.GetFullPath));
        string enabledValue = string.Join(PluginListSeparator, dllPaths.Select(_ => "1"));

        var lines = new List<(int Start, int Length, string Text)>();
        foreach (Match match in Regex.Matches(text, @"[^\r\n]*(?:\r\n|\n|\r|$)"))
        {
            if (match.Length > 0) lines.Add((match.Index, match.Length, match.Value));
        }

        string section = "";
        bool haveUserSection = false;
        int dllCount = 0, enabledCount = 0;
        int insertAfter = -1;   // [用户设置] 里最后一个非空行之后 = 插新键的位置（不越过尾随空行）
        for (int i = 0; i < lines.Count; i++)
        {
            (int start, int length, string line) = lines[i];
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                if (section == "用户设置") haveUserSection = true;
                continue;
            }
            if (section != "用户设置") continue;
            if (trimmed.Length > 0) insertAfter = start + length;
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            int equal = line.IndexOf('=');
            if (equal < 0) continue;
            string key = line[..equal].Trim();
            string? wanted = null;
            if (key == dllKey) { ++dllCount; wanted = dllValue; }
            else if (key == enabledKey) { ++enabledCount; wanted = enabledValue; }
            if (wanted is null || dllCount > 1 || enabledCount > 1) continue;
            if (string.Equals(line[(equal + 1)..].Trim(), wanted, StringComparison.Ordinal)) continue;
            int valueStart = equal + 1;
            while (valueStart < line.Length && char.IsWhiteSpace(line[valueStart])) ++valueStart;
            lines[i] = (start, length, line[..valueStart] + wanted + NewlineOf(line));
        }

        if (dllCount > 1 || enabledCount > 1)
            throw new InvalidDataException($"Rocket 配置里 {gameName} 的插件列表键重复，拒绝猜哪个生效；未修改配置。");
        if (!haveUserSection)
            throw new InvalidDataException("Rocket 配置缺少 [用户设置] 节，无法写入插件 DLL 列表；未修改配置。");

        string updated = string.Concat(lines.Select(x => x.Text));
        if (dllCount == 0 || enabledCount == 0)
        {
            string added = "";
            if (dllCount == 0) added += dllKey + " = " + dllValue + newline;
            if (enabledCount == 0) added += enabledKey + " = " + enabledValue + newline;
            if (insertAfter < 0)
            {
                updated = updated.TrimEnd('\r', '\n') + newline + added;
            }
            else
            {
                updated = updated[..insertAfter] + added + updated[insertAfter..];
            }
        }

        if (string.Equals(updated, text, StringComparison.Ordinal)) return original;
        byte[] content = Encoding.UTF8.GetBytes(updated);
        return bom ? Encoding.UTF8.GetPreamble().Concat(content).ToArray() : content;
    }

    private static string NewlineOf(string line) =>
        line.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : line.EndsWith('\n') ? "\n" : line.EndsWith('\r') ? "\r" : "";

    private static void RequirePluginDll(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new FileNotFoundException("缺少要注入的 DLL：" + (path ?? "(空)"), path);
        if (!string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("插件列表只收 DLL：" + path);
        if (path.Contains('\r') || path.Contains('\n') || path.Contains(PluginListSeparator))
            throw new InvalidDataException("DLL 路径包含换行符或分隔符：" + path);
    }

    /// <summary>写插件 DLL 列表（带备份、回读验证）。</summary>
    public static PrepareResult PreparePluginList(string? configPath, string gameName, IReadOnlyList<string> dllPaths)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                return new(false, "找不到 Rocket config.ini，请在 Rocket 设置中选择安装目录。", configPath);
            byte[] original = File.ReadAllBytes(configPath);
            byte[] updated = BuildPluginListConfiguration(original, gameName, dllPaths);
            string? backup = WriteBackedUp(configPath, updated, "hysx-rocket", original);
            return new(true, WaitingText, Path.GetFullPath(configPath), backup);
        }
        catch (Exception ex) { return new(false, "准备 Rocket 插件列表失败：" + ex.Message, configPath); }
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
