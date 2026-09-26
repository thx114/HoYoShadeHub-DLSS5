using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 读 NV App（NVIDIA app）的本地状态文件  RTX 动态亮丽 / RTX HDR / 平滑运动 这些
/// **滤镜类**开关。
///
/// <para>
///  事实核查（别再花时间找「那个设置文件」）：NV App 的滤镜开关（动态亮丽 / RTX HDR /
/// 平滑运动 / DLSS 覆盖）**没有**落成我们读得到的「开关值」。实测这台机器：
/// <list type="bullet">
/// <item><c>NvBackend\SHIM.json</c> 只是<b>硬件清单</b>（CPU / 主板 / 显卡 / 显示器 / 驱动版本）；</item>
/// <item><c>NvBackend\ApplicationStorage.json</c> 里每个游戏只有
/// <c>Disable_FG_Override</c> / <c>Disable_SR_Override</c> / <c>Disable_RR_Override</c>
/// 这类「DLSS 覆盖」开关（这个我们用得上，见 <see cref="TryReadDlssOverrideState"/>）；</item>
/// <item>动态亮丽 / RTX HDR 的真身在 DRS 数据库（<c>nvdrswr.lk</c> 独占锁）和 NV App 自己的内部存储里，
/// 驱动不提供只读 API；读到的 <c>nvdrswr.lk</c> / DRS 二进制也没有稳定的字段布局。</item>
/// </list>
/// </para>
///
/// <para>
/// 所以这里的策略是：**能读到的就读，读不到就明说读不到**，绝不猜一个值糊弄用户。
/// 唯一的例外是系统级 HDR 开关（<see cref="TryReadSystemHdr"/>），那个有明确的注册表键。
/// </para>
/// </summary>
internal class NvidiaAppSettings
{
    private static readonly ILogger Logger = AppConfig.GetLogger<NvidiaAppSettings>();

    /// <summary>NV App 的 Backend 目录（没有就返回 null）</summary>
    public static string? BackendDirectory
    {
        get
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NVIDIA Corporation", "NVIDIA app", "NvBackend");

                return Directory.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>NV App 装了没有（有 Backend 目录或 NVApp 进程）</summary>
    public static bool IsNvidiaAppInstalled()
    {
        if (BackendDirectory is not null)
        {
            return true;
        }

        try
        {
            return Process.GetProcessesByName("NVIDIA app").Length > 0
                   || Process.GetProcessesByName("NVIDIA App").Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 「播放流式 HDR 视频」这个开关（注册表 VideoSettings 下带 HDR 字样的值），读得到 true/false，读不到 null。
    ///
    /// <para>
    /// ⚠ <b>它不是桌面 HDR / 高级颜色</b>。显示器的 Windows HDR 开没开请看
    /// <c>DisplayHdrState</c>（走 DisplayConfig），RTX HDR 有没有效果就是按那个判的 ——
    /// 以前这里被当成「系统 HDR」用，是错的：用户显示器 HDR 没开的时候这个键也可能是 1。
    /// </para>
    /// </summary>
    public static bool? TryReadSystemHdr()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\VideoSettings");

            if (key is null)
            {
                return null;
            }

            bool? any = null;

            foreach (string name in key.GetValueNames())
            {
                if (name.Contains("HDR", StringComparison.OrdinalIgnoreCase) && key.GetValue(name) is int value)
                {
                    any = (any ?? false) || value != 0;
                }
            }

            return any;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "读系统 HDR 开关失败");
            return null;
        }
    }

    /// <summary>
    /// 「RTX HDR / 动态亮丽 到底开没开」的诚实答案：读得到就给 read 结果，
    /// 读不到返回 null（调用方照实说「读不到」）。
    /// </summary>
    public static string? TryReadFeatureHint(params string[] keys)
    {
        // NV App 没有落盘这些开关（见类型注释）。唯一能顺带确认的是「NV App 装没装」
        // 装了才谈得上开没开这些滤镜。
        return null;
    }

    /// <summary>NV App 装没装 + 版本（explain 里那行用）</summary>
    public static string DescribeNvidiaApp()
    {
        try
        {
            string? backend = BackendDirectory;
            if (backend is null)
            {
                return "没装 NVIDIA app（或读不到它的目录）";
            }

            string config = Path.Combine(backend, "config.xml");
            if (File.Exists(config))
            {
                string text = File.ReadAllText(config);
                int start = text.IndexOf("CurrentNvAppVersion", StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                {
                    int valueStart = text.IndexOf("value='", start, StringComparison.Ordinal);
                    if (valueStart > 0)
                    {
                        valueStart += "value='".Length;
                        int valueEnd = text.IndexOf('\'', valueStart);
                        if (valueEnd > valueStart)
                        {
                            return "NVIDIA app " + text[valueStart..valueEnd];
                        }
                    }
                }
            }

            return "已装 NVIDIA app";
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "读 NV App 版本失败");
            return "已装 NVIDIA app";
        }
    }

    /// <summary>
    /// 从 <c>ApplicationStorage.json</c> 里读这个游戏的「DLSS 覆盖 / 帧生成覆盖」开关。
    /// 这是 NV App <b>真正落盘</b>、我们读得到的部分  第 5 条（AI 插帧）用得上。
    /// </summary>
    /// <returns>形如「帧生成覆盖已关（说明你没让 NV App 接管这游戏的 FG）」的一句话；读不到 null</returns>
    public static string? TryReadDlssOverrideState(string? gameExePath)
    {
        if (string.IsNullOrWhiteSpace(gameExePath))
        {
            return null;
        }

        try
        {
            string? backend = BackendDirectory;
            if (backend is null)
            {
                return null;
            }

            string storage = Path.Combine(backend, "ApplicationStorage.json");
            if (!File.Exists(storage))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(storage));

            if (!document.RootElement.TryGetProperty("Applications", out JsonElement apps)
                || apps.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string exeName = Path.GetFileName(gameExePath);

            foreach (JsonElement entry in apps.EnumerateArray())
            {
                if (!entry.TryGetProperty("Application", out JsonElement app))
                {
                    continue;
                }

                string? display = app.TryGetProperty("DisplayName", out JsonElement d) ? d.GetString() : null;
                string? detected = null;

                if (app.TryGetProperty("DetectedFiles", out JsonElement files) && files.ValueKind == JsonValueKind.Array)
                {
                    detected = files.EnumerateArray()
                        .Select(f => f.GetString())
                        .FirstOrDefault(f => f is not null && Path.GetFileName(f).Equals(exeName, StringComparison.OrdinalIgnoreCase));
                }

                bool matchesExe = detected is not null;
                bool matchesName = display is not null
                                   && (display.Contains(Path.GetFileNameWithoutExtension(exeName), StringComparison.OrdinalIgnoreCase)
                                       || exeName.Contains(display, StringComparison.OrdinalIgnoreCase));

                if (!matchesExe && !matchesName)
                {
                    continue;
                }

                var parts = new List<string>();

                if (app.TryGetProperty("Disable_FG_Override", out JsonElement fg) && fg.ValueKind == JsonValueKind.True)
                {
                    parts.Add("NV App 没接管帧生成（Disable_FG_Override）");
                }

                if (app.TryGetProperty("DLSS_OTA_OptOut_PinnedSLVersion", out JsonElement pinned)
                    && pinned.GetString() is { Length: > 0 } version)
                {
                    parts.Add($"DLSS 覆盖被钉在 {version}");
                }

                if (app.TryGetProperty("DLSS_Override_No_OPS", out JsonElement noOps) && noOps.ValueKind == JsonValueKind.True)
                {
                    parts.Add("DLSS 覆盖走 No_OPS 通道");
                }

                return parts.Count > 0
                    ? $"NV App 里这个游戏：{string.Join("、", parts)}"
                    : $"NV App 里有这个游戏的条目（{display ?? exeName}）";
            }

            return null;
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "读 NV App DLSS 覆盖状态失败");
            return null;
        }
    }
}
