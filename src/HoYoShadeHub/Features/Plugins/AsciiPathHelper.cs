using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 第 7 条「HoYoShade 当前目录是否带中文」的修复：把目录重命名成纯 ASCII 名字。
///
/// <para>
/// 为什么值得单独做个工具：ReShade / <c>inject.exe</c> / 一部分 addon 是
/// <c>fopen</c> + 本地 ANSI 代码页那套，路径里有中文时**不一定报错，但插件就是静默不加载** 
/// 用户看到的现象是「注入成功、插件列表空」。这类问题只能靠路径改名绕开。
/// </para>
///
/// <para>
/// 做法：把 HoYoShade 目录重命名成 <c>HoYoShade</c>（默认）或 <c>hysx-shade-&lt;n&gt;</c>，
/// 并把「原名  新名」记进 <c>&lt;用户数据目录&gt;\.hysx\ascii-paths.json</c>，
/// 这样显示时还能说「你说的是那个中文名字的目录」。
/// </para>
///
/// <para>
///  这个操作**不**改各游戏 ReShade.ini 里的绝对路径  那是
/// <see cref="HoYoShadeHub.Extensions.ReShade.ShadePathAligner"/> 的活，改名之后要再点一次
/// 「指回当前 HoYoShade」（用专属插件目录的游戏不用 —— 那本来是正常状态）。修完会把这件事写进返回文案里。
/// </para>
/// </summary>
internal class AsciiPathHelper
{
    private static readonly ILogger Logger = AppConfig.GetLogger<AsciiPathHelper>();

    private const string MappingFileName = "ascii-paths.json";

    /// <summary>路径里有没有非 ASCII 字符</summary>
    public static bool HasNonAscii(string? path) => path is not null && path.Any(c => c > 127);

    private static string? MappingPath => string.IsNullOrWhiteSpace(AppConfig.UserDataFolder)
        ? null
        : Path.Combine(AppConfig.UserDataFolder, ".hysx", MappingFileName);

    /// <summary>
    /// 把 <paramref name="shadeRoot"/> 重命名成纯 ASCII 目录名。
    /// </summary>
    /// <returns>给人看的一句话（成功 / 失败原因 / 需要用户接着做什么）</returns>
    public static string? TryMakeShadeRootAscii(string? shadeRoot)
    {
        if (string.IsNullOrWhiteSpace(shadeRoot) || !Directory.Exists(shadeRoot))
        {
            return "目录不存在，没法改名。";
        }

        string full = Path.GetFullPath(shadeRoot).TrimEnd('\\', '/');
        string parent = Path.GetDirectoryName(full) ?? string.Empty;
        string name = Path.GetFileName(full);

        if (!HasNonAscii(name))
        {
            // 目录名本身是 ASCII，中文在上层（例如 C:\Users\张三\...）。
            // 这种没法靠改名解决  上层目录是用户/系统建的。
            return "这个目录自己的名字已经是纯 ASCII 了，中文在**上层路径**（" +
                   parent + "）。这层改不了，只能把整个便携包挪到一个纯英文目录（例如 D:\\APPS\\HoYoShadeHub）再重装。";
        }

        if (parent.Length == 0)
        {
            return "解析不出上级目录，不敢动。";
        }

        try
        {
            string target = PickAsciiTarget(parent, name);

            // Directory.Move 在同一卷内是原子改名，很快；跨卷会失败（那就让用户自己搬）
            Directory.Move(full, target);
            SaveMapping(name, Path.GetFileName(target));

            Logger.LogInformation("HoYoShade 目录改名：{Old} -> {New}", full, target);

            return $"已经把 HoYoShade 目录改名成 {target}。\n" +
                   "接下来要做两件事：\n" +
                   "1) 到「全局插件  指定目录」重新指到新目录（如果 Hub 没自动认出来）；\n" +
                   "2) 到每个游戏的插件页点一次「指回当前 HoYoShade」，把各游戏 ReShade.ini 里的绝对路径改过来" +
                   "（用专属插件目录的「每游戏插件包」自动跟着当前 HoYoShade 走，不用管）。";
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "HoYoShade 目录改名失败：{Path}", full);

            return "改名失败：" + ex.Message + "\n" +
                   "常见原因：Hub / 游戏 / 启动器正在用这个目录（先全关掉），或者目录在另一个盘（跨卷改名要先手动复制）。\n" +
                   $"也可以自己手动把 {full} 改名成纯英文，再到「指定目录」指过来。";
        }
    }

    /// <summary>挑一个不冲突的 ASCII 目录名</summary>
    private static string PickAsciiTarget(string parent, string currentName)
    {
        // 首选还是叫 HoYoShade（Hub 默认名，最不容易让别的组件找不到）
        string preferred = Path.Combine(parent, HoYoShadeHub.Extensions.Services.ShadeHostLocator.HoYoShadeFolderName);

        if (!Directory.Exists(preferred) && !File.Exists(preferred))
        {
            return preferred;
        }

        for (int i = 1; i < 100; i++)
        {
            string candidate = Path.Combine(parent, $"hysx-shade-{i}");

            if (!Directory.Exists(candidate) && !File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("同级目录里找不到可用的 ASCII 名字（hysx-shade-1..99 都被占了）。");
    }

    private static void SaveMapping(string oldName, string newName)
    {
        try
        {
            string? path = MappingPath;

            if (path is null)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var map = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(path))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(path));

                    if (existing is not null)
                    {
                        map = new System.Collections.Generic.Dictionary<string, string>(existing, StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch
                {
                    // 坏文件就重写
                }
            }

            map[newName] = oldName;
            File.WriteAllText(path, JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "写 ascii-paths 映射失败");
        }
    }

    /// <summary>新名字对应的原名（显示用；没有映射就返回 null）</summary>
    public static string? GetOriginalName(string asciiName)
    {
        try
        {
            string? path = MappingPath;

            if (path is null || !File.Exists(path))
            {
                return null;
            }

            var map = JsonSerializer.Deserialize<System.Collections.Generic.Dictionary<string, string>>(File.ReadAllText(path));

            return map is not null && map.TryGetValue(asciiName, out string? original) ? original : null;
        }
        catch
        {
            return null;
        }
    }
}
