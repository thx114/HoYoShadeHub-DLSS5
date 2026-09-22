using HoYoShadeHub.Features.GameSetting;
using HoYoShadeHub.Features.Xxmi;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using System;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 第 5 条「这个游戏是否开了 AI 插帧」：读游戏自己的图形设置。
///
/// <para>
/// 米哈游几个游戏把图形设置以 <b>JSON 字节串</b>存在注册表里（键名带哈希后缀），
/// 结构见 <see cref="GraphicsSettings_Model_h2986158309"/> 那批模型 
/// 不同游戏字段名不一样，所以这里不反序列化成模型，直接在 JSON 里找
/// 「名字里带 Frame / FG / DLSSG / 帧生成」的布尔字段。
/// </para>
///
/// <para>
///  别把 <c>FPS</c> / <c>EnableVSync</c> 当成插帧  那是帧率上限和垂直同步。
/// </para>
/// </summary>
internal class GameFrameGenerationSetting
{
    private static readonly ILogger Logger = AppConfig.GetLogger<GameFrameGenerationSetting>();

    /// <summary>帧生成相关字段名的特征（不区分大小写）</summary>
    private static readonly string[] FrameGenKeys =
    [
        "framegeneration", "frame_generation", "dlssg", "dlssfg", "framegen",
        "useframegeneration", "enableframegeneration", "framegenerationenabled",
        "aiframegeneration", "aiframe", "enableaiframe", "真生成",
    ];

    /// <summary>读这个游戏的插帧开关；读不到返回 null</summary>
    public static string? TryRead(string? gameBiz)
    {
        if (string.IsNullOrWhiteSpace(gameBiz))
        {
            return null;
        }

        try
        {
            var biz = new HoYoShadeHub.Core.GameBiz(gameBiz);
            string keyPath = biz.GetGameRegistryKey();

            if (string.IsNullOrWhiteSpace(keyPath))
            {
                return null;
            }

            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(keyPath)
                                     ?? Registry.CurrentUser.OpenSubKey(keyPath.Replace(@"HKEY_CURRENT_USER\", string.Empty));

            if (key is null)
            {
                return null;
            }

            foreach (string name in key.GetValueNames())
            {
                if (key.GetValue(name) is not byte[] data || data.Length == 0)
                {
                    continue;
                }

                string json;

                try
                {
                    json = Encoding.UTF8.GetString(data).TrimEnd('\0');
                }
                catch
                {
                    continue;
                }

                if (!json.StartsWith('{') && !json.StartsWith('['))
                {
                    continue;
                }

                string? found = SearchFrameGeneration(json);

                if (found is not null)
                {
                    return found;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "读游戏插帧设置失败：{Biz}", gameBiz);
        }

        return null;
    }

    /// <summary>在 JSON 文本里找帧生成开关（字段名匹配 + 值为布尔/数字）</summary>
    private static string? SearchFrameGeneration(string json)
    {
        try
        {
            JsonNode? node = JsonNode.Parse(json);

            if (node is null)
            {
                return null;
            }

            return Walk(node, string.Empty);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Walk(JsonNode node, string path)
    {
        if (node is JsonObject obj)
        {
            foreach ((string key, JsonNode? child) in obj)
            {
                if (child is null)
                {
                    continue;
                }

                string lower = key.ToLowerInvariant();

                if (FrameGenKeys.Any(k => lower.Contains(k, StringComparison.Ordinal)))
                {
                    if (child is JsonValue value && value.TryGetValue(out bool flag))
                    {
                        return $"游戏图形设置里 {path}{key} = {(flag ? "开" : "关")}";
                    }

                    if (child is JsonValue numberValue && numberValue.TryGetValue(out int number))
                    {
                        return $"游戏图形设置里 {path}{key} = {number}" + (number > 0 ? "（开）" : "（关）");
                    }
                }

                string? nested = Walk(child, path + key + ".");
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (node is JsonArray array)
        {
            int index = 0;

            foreach (JsonNode? child in array)
            {
                if (child is not null)
                {
                    string? nested = Walk(child, $"{path}[{index++}].");
                    if (nested is not null)
                    {
                        return nested;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>兜底：从 ini 文本里找帧生成键（有些自定义游戏把设置写在自己那份 ini 里）</summary>
    public static string? TryReadFromIniText(string? iniText)
    {
        if (string.IsNullOrWhiteSpace(iniText))
        {
            return null;
        }

        foreach (Match match in Regex.Matches(iniText, @"(?im)^\s*([A-Za-z_0-9]*(?:framegen|dlssg|dlssfg|framegeneration)[A-Za-z_0-9]*)\s*=\s*(\S+)\s*$"))
        {
            string key = match.Groups[1].Value;
            string value = match.Groups[2].Value;
            bool on = value.Equals("true", StringComparison.OrdinalIgnoreCase)
                      || value.Equals("on", StringComparison.OrdinalIgnoreCase)
                      || (int.TryParse(value, out int n) && n > 0);

            return $"ini 里 {key} = {value}（{(on ? "开" : "关")}）";
        }

        return null;
    }
}
