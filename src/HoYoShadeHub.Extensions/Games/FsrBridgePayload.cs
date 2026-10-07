using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>
/// 原神 FSR 桥模块「落库后的样子」：正身 DLL 叫 <c>Dx11FsrBridge.dll</c>，配置叫 <c>Dx11FsrBridge.ini</c>。
///
/// <para>
/// 1.4.3.1 及更早的下载器把**每一个** Release 模块都按 OptiScaler 落库
/// （<c>OptiScalerLibrary.NormalizePrimaryDll</c>：目录里唯一的 DLL 一律改名成 <c>OptiScaler.dll</c>），
/// 于是桥的 <c>Dx11FsrBridge.dll</c> 变成了 <c>OptiScaler.dll</c>。载荷一个字节没变
/// （SHA256 与 release 自带的 SHA256SUMS.txt 一致），但所有「按文件名找桥」的地方都认不出来了。
/// 认名 / 归位集中在这里，别再各自比字符串。
/// </para>
/// </summary>
public static class FsrBridgePayload
{
    /// <summary>桥的 DLL 正身名（模块 dllHint 与「按 DLL 所在目录找 ini」都按它来）。</summary>
    public const string DllName = "Dx11FsrBridge.dll";

    /// <summary>桥的配置文件名（和 DLL 同目录）。</summary>
    public const string IniName = "Dx11FsrBridge.ini";

    /// <summary>旧下载器把桥改名成的名字（= OptiScaler 归一化的目标名）。</summary>
    public const string LegacyAliasDllName = "OptiScaler.dll";

    private static readonly string[] _manifestNames = ["build.json", "SHA256SUMS.txt"];

    /// <summary>
    /// 目录看起来是不是「桥的载荷」：桥的 ini、桥的正身 DLL、或点名 genshin-fsr-bridge 的清单
    /// （build.json / SHA256SUMS.txt）任一场。
    ///
    /// <para>
    /// 判据刻意**不认光秃秃的 OptiScaler.dll**：真正的 OptiScaler 构建目录也有这个名字，
    /// 只有 ini / 清单才能证明这是桥。所以旧下载器改名之后，只要 ini 还在（release 的 zip 一直带），
    /// 这里就还认得出来。
    /// </para>
    /// </summary>
    public static bool LooksLikeBridgeDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return false;
        }

        if (File.Exists(Path.Combine(directory, IniName)) || File.Exists(Path.Combine(directory, DllName)))
        {
            return true;
        }

        if (FindByFileName(directory, DllName) is not null)
        {
            return true;
        }

        foreach (string manifest in _manifestNames)
        {
            try
            {
                string path = Path.Combine(directory, manifest);
                if (File.Exists(path)
                    && File.ReadAllText(path).Contains("genshin-fsr-bridge", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch
            {
                // 清单读不动就当没有，别把整条解析拖下水
            }
        }

        return false;
    }

    /// <summary>
    /// 在目录里找桥要注入的那份 DLL：正身名 → 历史别名 → 目录里唯一的 DLL。
    /// 每个候选都要过 <paramref name="accept"/>（启动器拿它套 2.3.x 版本门）；找不到返回 null。
    /// </summary>
    public static string? FindDll(string? directory, Func<string, bool>? accept = null)
    {
        List<string> dlls = EnumerateDlls(directory);
        if (dlls.Count == 0)
        {
            return null;
        }

        accept ??= static _ => true;

        foreach (string name in new[] { DllName, LegacyAliasDllName })
        {
            string? hit = dlls.FirstOrDefault(f => Matches(Path.GetFileName(f), name));
            if (hit is not null && accept(hit))
            {
                return hit;
            }
        }

        // 只剩「目录里就这一份」的兜底：多份 DLL 时不猜，猜错就是往游戏里注错东西。
        string? single = dlls.Count == 1 ? dlls[0] : null;
        return single is not null && accept(single) ? single : null;
    }

    /// <summary>
    /// 把旧下载器改过名的桥 DLL 归位成正身名 <c>Dx11FsrBridge.dll</c>。
    /// 幂等：正身名已存在时**什么都不做**（绝不覆盖）；目录不像桥载荷时也不动
    /// （免得把真正的 OptiScaler 改名成桥）。改成功返回正身名的完整路径，
    /// 没得改 / 文件被占用（游戏正跑）改不动就返回 null，调用方继续用原名那份。
    /// </summary>
    public static string? NormalizeDll(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        string canonical = Path.Combine(directory, DllName);
        if (File.Exists(canonical))
        {
            return canonical;
        }

        if (!LooksLikeBridgeDirectory(directory))
        {
            return null;
        }

        string? alias = FindByFileName(directory, LegacyAliasDllName);
        if (alias is null)
        {
            return null;
        }

        try
        {
            File.Move(alias, Path.Combine(Path.GetDirectoryName(alias) ?? directory, DllName));
            return Path.Combine(Path.GetDirectoryName(alias) ?? directory, DllName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>目录里所有 DLL 的文件名（去重，诊断用）。</summary>
    public static IReadOnlyList<string> ListDllNames(string? directory)
        => [.. EnumerateDlls(directory)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)];

    private static string? FindByFileName(string? directory, string fileName)
        => EnumerateDlls(directory).FirstOrDefault(f => Matches(Path.GetFileName(f), fileName));

    private static List<string> EnumerateDlls(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return [];
        }

        try
        {
            return [.. Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories)];
        }
        catch
        {
            return [];
        }
    }

    private static bool Matches(string? name, string expected)
        => string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
}
