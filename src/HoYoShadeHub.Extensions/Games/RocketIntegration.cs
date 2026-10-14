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
    public sealed record PrepareResult(bool Success, string Message, string? ConfigPath = null, string? BackupPath = null, IReadOnlyList<string>? DllPaths = null);
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
    /// <param name="extraDlls">
    /// 用户在这个游戏勾的其他模块（DLSS-NR / DLSS-Enabler 之类）。链模式下 Hub 不开外部注入器，
    /// 它们只能由桥在游戏进程内 LoadLibraryW，否则就是「勾了等于没勾」。排在 <c>wait dxgi.dll</c>
    /// 之后、OptiScaler 之前：先在位，再让 OptiScaler 接管 Present/交换链。
    /// </param>
    public static IReadOnlyList<string> BuildGraphicsChain(string bridgeDll, string? optiDll, string? shadeDll,
        IReadOnlyList<string>? extraDlls = null)
    {
        RequireDll(bridgeDll, "Dx11FsrBridge.dll");
        List<string> steps = ["wait dxgi.dll"];
        if (extraDlls is not null)
        {
            foreach (string extra in extraDlls)
            {
                if (string.IsNullOrWhiteSpace(extra)) continue;
                RequireDll(extra, Path.GetFileName(extra));
                string full = Path.GetFullPath(extra);
                if (steps.Any(step => string.Equals(step, "load " + full, StringComparison.OrdinalIgnoreCase))) continue;
                steps.Add("load " + full);
            }
        }
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

    public static PrepareResult EnsureGraphicsChain(string bridgeDll, string? optiDll, string? shadeDll,
        IReadOnlyList<string>? extraDlls = null)
    {
        try
        {
            var steps = BuildGraphicsChain(bridgeDll, optiDll, shadeDll, extraDlls);
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

    /// <summary>
    /// 写 <c>[用户设置] 原神DLL路径</c> 和 <c>[注入设置] 原神第三方DLL启用</c>：
    /// 键已存在就改值（空值/火箭占位值同样整体替换）；键整条缺失就补进对应节末尾
    /// （Rocket 从没配置过 DLL 路径时配置项可能不存在）。目标节本身缺失仍拒绝——
    /// 那通常意味着选错了 config.ini。其余行逐字节保留；重复键拒绝歧义。
    /// </summary>
    public static byte[] BuildRocketConfiguration(byte[] original, string bridgeDllPath)
    {
        RequireDll(bridgeDllPath, "Dx11FsrBridge.dll");
        byte[] withPath = RewriteKeyInSection(original, "用户设置", "原神DLL路径", Path.GetFullPath(bridgeDllPath));
        return RewriteKeyInSection(withPath, "注入设置", "原神第三方DLL启用", "true");
    }

    /// <summary>
    /// 写 <c>[注入设置] &lt;游戏名&gt;第三方DLL启用</c>——火箭按游戏名的第三方 DLL 注入总开关，
    /// 插件 DLL 列表只有在它为 <c>true</c> 时才生效。键缺失整条补全、逐字节保留、重复键/缺节拒绝，同上。
    /// </summary>
    public static byte[] BuildThirdPartyEnableConfiguration(byte[] original, string gameName, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(gameName)) throw new InvalidDataException("缺少火箭配置里的游戏名。");
        return RewriteKeyInSection(original, "注入设置", gameName + "第三方DLL启用", enabled ? "true" : "false");
    }

    /// <summary>把 <paramref name="sectionName"/> 节里的 <paramref name="key"/> 改成 <paramref name="wanted"/>：
    /// 键整条缺失就补在节内最后一个内容行之后；节缺失或键重复抛 <see cref="InvalidDataException"/>。
    /// 其余内容逐字节保留（含 BOM 与换行风格）。</summary>
    private static byte[] RewriteKeyInSection(byte[] original, string sectionName, string key, string wanted)
    {
        bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
        string text = new UTF8Encoding(false, true).GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0));
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

        var lines = new List<string>();
        foreach (Match match in Regex.Matches(text, @"[^\r\n]*(?:\r\n|\n|\r|$)"))
        {
            if (match.Length > 0) lines.Add(match.Value);
        }

        string section = "";
        bool haveSection = false;
        int insertAfter = -1, count = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            string line = lines[i], trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                if (section == sectionName) { haveSection = true; insertAfter = i; }
                continue;
            }
            if (section != sectionName) continue;
            if (trimmed.Length > 0) insertAfter = i;
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            int equal = line.IndexOf('='); if (equal < 0) continue;
            if (line[..equal].Trim() != key) continue;
            ++count; if (count > 1) continue;
            if (string.Equals(line[(equal + 1)..].Trim(), wanted, StringComparison.Ordinal)) continue;
            int valueStart = equal + 1;
            // Empty values still contain the physical line ending. Never consume CR/LF.
            while (valueStart < line.Length && line[valueStart] is ' ' or '\t') ++valueStart;
            lines[i] = line[..valueStart] + wanted + NewlineOf(line);
        }

        if (count > 1)
            throw new InvalidDataException("Rocket 配置里 [" + sectionName + "] 的 " + key + " 键重复，拒绝猜哪个生效；未修改配置。");
        if (!haveSection)
            throw new InvalidDataException("Rocket 配置缺少 [" + sectionName + "] 节，无法补全 " + key + "；未修改配置。");
        if (count == 0)
        {
            // 键整条缺失时补在节内最后一个内容行之后（不越过节尾空行）。
            string anchor = lines[insertAfter], added = key + " = " + wanted + newline;
            lines[insertAfter] = anchor + (NewlineOf(anchor).Length == 0 ? newline : "") + added;
        }
        string updated = string.Concat(lines);
        if (string.Equals(updated, text, StringComparison.Ordinal)) return original;
        byte[] content = Encoding.UTF8.GetBytes(updated);
        return bom ? Encoding.UTF8.GetPreamble().Concat(content).ToArray() : content;
    }
    /// <summary>
    /// 写「<c>&lt;游戏名&gt;插件DLL列表</c>」和「<c>&lt;游戏名&gt;插件启用列表</c>」（都在 <c>[用户设置]</c> 节）：
    /// 火箭按这个列表把多份第三方 DLL 注进游戏进程，列表顺序 = 注入顺序（OptiScaler 在前、ReShade 在后，
    /// 和原神链一致）。键不存在就插到 <c>[用户设置]</c> 节末尾，其余行逐字节保留。
    /// 同时把 <c>[注入设置] &lt;游戏名&gt;第三方DLL启用</c> 写成 <c>true</c>——这是火箭的注入总开关，
    /// 缺了它火箭不会注列表里的任何 DLL；键缺失同样整条补全。
    /// </summary>
    public static byte[] BuildPluginListConfiguration(byte[] original, string gameName, IReadOnlyList<string> dllPaths)
    {
        if (string.IsNullOrWhiteSpace(gameName)) throw new InvalidDataException("缺少火箭配置里的游戏名。");
        if (dllPaths.Count == 0) throw new InvalidDataException("插件 DLL 列表为空。");
        foreach (string path in dllPaths) RequirePluginDll(path);
        byte[] listed = BuildPluginListValues(original, gameName,
            string.Join(PluginListSeparator, dllPaths.Select(Path.GetFullPath)),
            string.Join(PluginListSeparator, dllPaths.Select(_ => "1")), insertIfMissing: true);
        byte[] updated = BuildThirdPartyEnableConfiguration(listed, gameName, enabled: true);
        VerifyPluginListConfiguration(updated, gameName, dllPaths);
        return updated;
    }
    /// <summary>
    /// 把插件列表清空（取消勾选火箭模式时的可逆动作）：值留空 = Rocket 没东西可注。
    /// 键本来就不存在时**不插空键**（只返回原样），所以对没用过这个机制的游戏是彻底的空操作。
    /// 不动 <c>[注入设置] &lt;游戏名&gt;第三方DLL启用</c> 总开关：那是 Rocket 界面里的用户设置，
    /// 空列表已经等于没东西可注，翻它只会覆盖用户在 Rocket 里亲手选的开关状态。
    /// </summary>
    public static byte[] BuildClearedPluginListConfiguration(byte[] original, string gameName)
    {
        if (string.IsNullOrWhiteSpace(gameName)) throw new InvalidDataException("缺少火箭配置里的游戏名。");
        return BuildPluginListValues(original, gameName, "", "", insertIfMissing: false);
    }

    private static byte[] BuildPluginListValues(byte[] original, string gameName, string dllValue, string enabledValue,
        bool insertIfMissing)
    {

        bool bom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
        string text = new UTF8Encoding(false, true).GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0));
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        string dllKey = gameName + "插件DLL列表";
        string enabledKey = gameName + "插件启用列表";

        var lines = new List<(int Start, int Length, string Text)>();
        foreach (Match match in Regex.Matches(text, @"[^\r\n]*(?:\r\n|\n|\r|$)"))
        {
            if (match.Length > 0) lines.Add((match.Index, match.Length, match.Value));
        }

        string section = "";
        bool haveUserSection = false;
        int dllCount = 0, enabledCount = 0;
        int insertAfter = -1;   // 行索引，不用修改值之前的字符偏移（不越过尾随空行）
        for (int i = 0; i < lines.Count; i++)
        {
            (int start, int length, string line) = lines[i];
            string trimmed = line.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                if (section == "用户设置") { haveUserSection = true; insertAfter = i; }
                continue;
            }
            if (section != "用户设置") continue;
            if (trimmed.Length > 0) insertAfter = i;
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
            // Empty values still contain the physical line ending. Never consume CR/LF.
            while (valueStart < line.Length && line[valueStart] is ' ' or '\t') ++valueStart;
            lines[i] = (start, length, line[..valueStart] + wanted + NewlineOf(line));
        }

        if (dllCount > 1 || enabledCount > 1)
            throw new InvalidDataException($"Rocket 配置里 {gameName} 的插件列表键重复，拒绝猜哪个生效；未修改配置。");
        if (!haveUserSection)
            throw new InvalidDataException("Rocket 配置缺少 [用户设置] 节，无法写入插件 DLL 列表；未修改配置。");

        string updated = string.Concat(lines.Select(x => x.Text));
        if (insertIfMissing && (dllCount == 0 || enabledCount == 0))
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
                int offset = lines.Take(insertAfter + 1).Sum(x => x.Text.Length);
                string separator = NewlineOf(lines[insertAfter].Text).Length == 0 ? newline : "";
                updated = updated[..offset] + separator + added + updated[offset..];
            }
        }

        if (string.Equals(updated, text, StringComparison.Ordinal)) return original;
        byte[] content = Encoding.UTF8.GetBytes(updated);
        return bom ? Encoding.UTF8.GetPreamble().Concat(content).ToArray() : content;
    }

    private static string NewlineOf(string line) =>
        line.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n" : line.EndsWith('\n') ? "\n" : line.EndsWith('\r') ? "\r" : "";

    // Match Rocket's physical INI key/value lines, not a substring somewhere in the file.
    // Reject historical orphan continuations before writing; do not guess which lines to delete.
    private static void VerifyPluginListConfiguration(byte[] bytes, string gameName, IReadOnlyList<string> dllPaths)
    {
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        string text = new UTF8Encoding(false, true).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0));
        string section = "";
        string? paths = null, enabled = null, thirdParty = null;
        int pathCount = 0, enabledCount = 0, thirdPartyCount = 0;
        foreach (string line in Regex.Split(text, @"\r\n|\n|\r"))
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#')) continue;
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                section = trimmed[1..^1].Trim();
                continue;
            }
            int equal = line.IndexOf('=');
            if (section == "用户设置")
            {
                if (equal < 0)
                    throw new InvalidDataException("Rocket [用户设置] 有孤立值行，请先从备份恢复有效配置；未猜测删除内容。");
                string key = line[..equal].Trim();
                string value = line[(equal + 1)..].Trim(' ', '\t');
                if (key == gameName + "插件DLL列表") { paths = value; ++pathCount; }
                else if (key == gameName + "插件启用列表") { enabled = value; ++enabledCount; }
            }
            else if (section == "注入设置" && equal >= 0)
            {
                string key = line[..equal].Trim();
                if (key == gameName + "第三方DLL启用") { thirdParty = line[(equal + 1)..].Trim(' ', '\t'); ++thirdPartyCount; }
            }
        }
        string expectedPaths = string.Join(PluginListSeparator, dllPaths.Select(Path.GetFullPath));
        string expectedEnabled = string.Join(PluginListSeparator, dllPaths.Select(_ => "1"));
        if (pathCount != 1 || enabledCount != 1 || thirdPartyCount != 1
            || !string.Equals(paths, expectedPaths, StringComparison.Ordinal)
            || !string.Equals(enabled, expectedEnabled, StringComparison.Ordinal)
            || !string.Equals(thirdParty, "true", StringComparison.Ordinal))
            throw new InvalidDataException("Rocket 插件列表语义回读失败：DLL 路径/顺序、启用列表或 [注入设置] 注入总开关与所选模块不一致。");
    }

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
        string? backup = null;
        try
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                return new(false, "找不到 Rocket config.ini，请在 Rocket 设置中选择安装目录。", configPath);
            byte[] original = File.ReadAllBytes(configPath);
            byte[] updated = BuildPluginListConfiguration(original, gameName, dllPaths);
            backup = WriteBackedUp(configPath, updated, "hysx-rocket", original);
            byte[] readback = File.ReadAllBytes(configPath);
            VerifyPluginListConfiguration(readback, gameName, dllPaths);
            if (!readback.SequenceEqual(updated)) throw new IOException("Rocket 配置在写入后改变，请重新准备。");
            return new(true, WaitingText, Path.GetFullPath(configPath), backup);
        }
        catch (Exception ex) { return new(false, "准备 Rocket 插件列表失败：" + ex.Message, configPath, backup); }
    }

    /// <summary>
    /// 清空这个游戏的插件 DLL 列表（撤掉火箭模式的可逆动作）：带备份、幂等，
    /// 键本来就不存在时一个字都不改（对没用过这个机制的游戏是彻底空操作）。
    /// </summary>
    public static PrepareResult ClearPluginList(string? configPath, string gameName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configPath) || !File.Exists(configPath))
                return new(false, "找不到 Rocket config.ini。", configPath);
            byte[] original = File.ReadAllBytes(configPath);
            byte[] updated = BuildClearedPluginListConfiguration(original, gameName);
            string? backup = WriteBackedUp(configPath, updated, "hysx-rocket-clear", original);
            return new(true, "已清空 " + gameName + " 的插件 DLL 列表。", Path.GetFullPath(configPath), backup);
        }
        catch (Exception ex) { return new(false, "清空 Rocket 插件列表失败：" + ex.Message, configPath); }
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
