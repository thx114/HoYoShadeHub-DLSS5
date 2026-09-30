using HoYoShadeHub.Extensions.ReShade;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 覆盖包 auto.json 动作的用户同意状态。
///
/// <para>
/// auto.json 内容变了（SHA-256 变）→ 视为「新包 / 包更新了」，需要重新弹窗确认。
/// 同意时记下 hash + 用户**没勾选**的动作名（默认全选 = 全装）；启动前自动执行（runOnLaunch）
/// 和插件页的动作按钮都只跑仍然启用的动作。用户点「全部不要」= 同意 hash 但全部禁用
/// （不再反复弹窗，直到包内容再变）。
/// </para>
/// </summary>
public static class PackActionConsent
{
    private static string HashKey(string gameBiz) => $"pack_auto_consent_{gameBiz}";
    private static string DisabledKey(string gameBiz) => $"pack_auto_disabled_{gameBiz}";

    /// <summary>auto.json 的内容 hash；没有文件返回空串</summary>
    public static string ComputeHash(string? packRoot)
    {
        if (packRoot is null)
        {
            return string.Empty;
        }

        try
        {
            string path = Path.Combine(packRoot, GameAddonPackUserContent.AutoActionFileName);
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        }
        catch
        {
            return string.Empty;
        }
    }

    public static bool IsConsented(string gameBiz, string hash)
        => hash.Length > 0
           && string.Equals(AppConfig.GetValue<string>(null, HashKey(gameBiz)), hash, StringComparison.Ordinal);

    /// <summary>这个 hash 下被用户禁用的动作名（hash 对不上 = 还没问过 → 空集）</summary>
    public static HashSet<string> DisabledActions(string gameBiz, string hash)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        string? raw = AppConfig.GetValue<string>(null, DisabledKey(gameBiz));
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }

        int split = raw.IndexOf(':');
        if (split <= 0
            || !string.Equals(raw[..split], hash, StringComparison.Ordinal))
        {
            return result;
        }

        foreach (string name in raw[(split + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            result.Add(name);
        }

        return result;
    }

    /// <summary>记住这次同意：hash + 没勾选的动作名（disabled 为空 = 全装）</summary>
    public static void Save(string gameBiz, string hash, IEnumerable<string> disabledNames)
    {
        AppConfig.SetValue<string>(hash, HashKey(gameBiz));
        AppConfig.SetValue<string>(hash + ":" + string.Join('|', disabledNames), DisabledKey(gameBiz));
    }
}

/// <summary>
/// 把 auto.json 的动作 / 步骤翻译成给人看的清单（同意弹窗用）。
/// 「高危」= 会动游戏 / 启动器目录、注册表、删 ini 那类：覆盖文件、注册表加游戏、清 ini、删滤镜。
/// </summary>
public static class PackActionDescriber
{
    public static bool IsHighRisk(PackActionStep step) => step.Action.Trim().ToLowerInvariant() switch
    {
        "override_files" or "add_game_from_registry" or "clear_game_ini" or "delete_shader" => true,
        _ => false,
    };

    /// <summary>一行人话描述（接口名 + 关键参数）</summary>
    public static string Describe(PackActionStep step, string? packRoot)
    {
        string action = step.Action.Trim().ToLowerInvariant();
        string target = (step.GetString("target") ?? string.Empty).Trim().ToLowerInvariant();
        string? name = step.GetString("name") ?? step.GetString("file") ?? step.GetString("key");
        bool? enabled = step.GetBool("enabled");

        return action switch
        {
            "set_addon" or "set_addons" => enabled is null
                ? "开关插件（缺 enabled）"
                : $"{(enabled.Value ? "启用" : "禁用")}插件"
                  + (step.GetStringList("files") is { Count: > 0 } files ? $"：{string.Join("、", files)}" : "（全部）"),
            "import_preset" => (step.GetString("shareCode") ?? step.GetString("code")) is not null
                ? "导入分享码预设"
                : $"导入预设 {name}",
            "apply_preset" => $"应用预设 {name}",
            "clear_game_ini" => "删除游戏目录的 ReShade.ini / ReShade2.ini",
            "set_dx12" => enabled is null ? "开关 DX12（缺 enabled）" : (enabled.Value ? "开启" : "关闭") + " DX12 启动",
            "set_smooth_motion" or "set_ai_frame" or "set_ai_interpolation" => enabled is null
                ? "开关 AI 插帧（缺 enabled）"
                : (enabled.Value ? "开启" : "关闭") + " AI 插帧（Smooth Motion）",
            "set_inject_mode" => enabled is null ? "开关注入模式（缺 enabled）" : (enabled.Value ? "开启" : "关闭") + "注入模式",
            "set_cmd_launch" => (enabled is true ? "开启" : "关闭") + " CMD 启动游戏（全局）",
            "set_cmd_args" => $"修改游戏命令行（{(step.GetString("mode") ?? "replace")}）",
            "set_launch_option" => $"启动选项 {step.GetString("key")} = {(enabled is true ? "开" : "关")}",
            "set_opt" or "set_optiscaler" => (enabled is true ? "启用" : "关闭") + " OptiScaler",
            "set_module" => string.IsNullOrWhiteSpace(step.GetString("id"))
                ? (enabled is true ? "启用" : "关闭") + "模块注入"
                : $"模块 {step.GetString("id")} = {(enabled is true ? "开" : "关")}",
            "import_opt_config" => $"导入 OptiScaler 配置 {name}",
            "update_opt_config" => $"回抓 OptiScaler 配置 {name}",
            "set_opt_dll" => $"OptiScaler 注入 dll 名 → {name}",
            "import_xxmi" => $"导入 XXMI 模型 {name}",
            "switch_dll" => $"切换运行时 dll：{step.GetString("family")} {(step.GetString("version") ?? "推荐版")}",
            "set_inject_delay" => $"注入时机（{step.GetString("target")}）→ {(step.GetNumber("seconds") is { } s ? s + " 秒" : "跟随全局")}",
            "set_game_setting" => $"游戏设置 {step.GetString("key")}",
            "override_files" => target switch
            {
                "launcher" => "覆盖启动器目录文件",
                _ => "覆盖游戏目录文件",
            } + $"（{OverlayFileCount(packRoot, target)} 个文件）",
            "import_shader" => $"导入 ReShade 滤镜 {name}",
            "set_shader" => $"{(enabled is true ? "开启" : "关闭")} technique {name}",
            "delete_shader" => $"删除 ReShade 滤镜 {name}",
            "run_compat_check" => "执行 DLSS5 兼容性检测",
            "add_game_from_registry" => $"从注册表寻找游戏目录并添加：{step.GetString("key")}",
            "launch_game" or "launch" or "start_game" => "启动游戏",
            _ => $"未知道具 {step.Action}",
        };
    }

    /// <summary>高危动作的详细清单（覆盖文件时列全部文件）</summary>
    public static List<string> HighRiskDetails(PackActionStep step, string? packRoot)
    {
        var details = new List<string>();
        string action = step.Action.Trim().ToLowerInvariant();

        switch (action)
        {
            case "override_files":
            {
                string target = (step.GetString("target") ?? "game").Trim().ToLowerInvariant();
                string folder = step.GetString("folder") ?? (target == "launcher"
                    ? GameAddonPackUserContent.LauncherFilesFolderName
                    : GameAddonPackUserContent.GameFilesFolderName);
                string source = packRoot is null ? string.Empty : Path.Combine(packRoot, folder);
                string targetName = target == "launcher" ? "启动器目录" : "游戏目录";

                if (source.Length > 0 && Directory.Exists(source))
                {
                    foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        details.Add(Path.GetRelativePath(source, file) + " → " + targetName);
                    }
                }

                if (details.Count == 0)
                {
                    details.Add("（包里没有 " + folder + "\\ 文件）");
                }

                break;
            }
            case "add_game_from_registry":
                details.Add("注册表键：" + (step.GetString("key") ?? "?"));
                details.Add("值：" + (step.GetString("valueName") ?? "GameInstallPath"));
                break;
            case "clear_game_ini":
                details.Add("删除后下次启动按模板 + 覆盖包 ini_config.json 重建");
                break;
            case "delete_shader":
                details.Add("滤镜文件 + 当前预设里的 technique 都会删");
                break;
        }

        return details;
    }

    private static int OverlayFileCount(string? packRoot, string target)
    {
        string folder = target == "launcher"
            ? GameAddonPackUserContent.LauncherFilesFolderName
            : GameAddonPackUserContent.GameFilesFolderName;
        string source = packRoot is null ? string.Empty : Path.Combine(packRoot, folder);
        if (source.Length == 0 || !Directory.Exists(source))
        {
            return 0;
        }

        try
        {
            return Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Count();
        }
        catch
        {
            return 0;
        }
    }
}
