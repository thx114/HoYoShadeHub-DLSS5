using System.Text.Json;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>auto.json 里的一个动作（一串步骤，UI 上是一个按钮）</summary>
public sealed class PackAutoAction
{
    /// <summary>显示名（按钮文本）</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>启动 / 注入这个游戏之前自动执行</summary>
    public bool RunOnLaunch { get; set; }

    public List<PackActionStep> Steps { get; } = [];
}

/// <summary>动作里的一个步骤：<c>action</c> 是接口名，其余字段是参数（按名字取）</summary>
public sealed class PackActionStep
{
    /// <summary>接口名，如 <c>set_addons</c> / <c>apply_preset</c>（大小写不敏感）</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>整个步骤对象（含 action 字段本身），执行器按名字取参数</summary>
    public JsonElement Raw { get; set; }

    public string? GetString(string name)
    {
        if (Raw.ValueKind == JsonValueKind.Object
            && Raw.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return null;
    }

    public bool? GetBool(string name)
    {
        if (Raw.ValueKind == JsonValueKind.Object
            && Raw.TryGetProperty(name, out JsonElement element)
            && (element.ValueKind is JsonValueKind.True or JsonValueKind.False))
        {
            return element.GetBoolean();
        }

        return null;
    }

    public double? GetNumber(string name)
    {
        if (Raw.ValueKind == JsonValueKind.Object
            && Raw.TryGetProperty(name, out JsonElement element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out double value))
        {
            return value;
        }

        return null;
    }

    /// <summary>字符串数组参数（files / args 那种）</summary>
    public List<string> GetStringList(string name)
    {
        var result = new List<string>();
        if (Raw.ValueKind == JsonValueKind.Object
            && Raw.TryGetProperty(name, out JsonElement element))
        {
            if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in element.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    {
                        result.Add(item.GetString()!);
                    }
                }
            }
            else if (element.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(element.GetString()))
            {
                result.Add(element.GetString()!);
            }
        }

        return result;
    }
}

/// <summary>
/// 覆盖包的 <c>auto.json</c>：自定义动作列表。格式：
/// <code>
/// {
///   "actions": [
///     {
///       "name": "一键 NR",
///       "runOnLaunch": true,
///       "steps": [
///         { "action": "set_addons", "enabled": false },
///         { "action": "apply_preset", "name": "NR.ini" }
///       ]
///     }
///   ]
/// }
/// </code>
/// </summary>
public static class PackAutoActionFile
{
    public static List<PackAutoAction> Load(string? packRoot)
    {
        if (packRoot is null)
        {
            return [];
        }

        string path = Path.Combine(packRoot, GameAddonPackUserContent.AutoActionFileName);
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return Parse(File.ReadAllText(path));
        }
        catch
        {
            // 写坏了就当没有动作
            return [];
        }
    }

    /// <summary>
    /// 从 JSON 文本解析动作列表。支持三种形态：
    /// <c>{"actions": [...]}</c>（标准 auto.json）、裸数组 <c>[...]</c>（每个元素一个动作）、
    /// <c>{"steps": [...]}</c>（单个匿名动作，CLI 一键指令用）。
    /// </summary>
    public static List<PackAutoAction> Parse(string json)
    {
        var result = new List<PackAutoAction>();
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in root.EnumerateArray())
                {
                    AddAction(result, item, result.Count + 1);
                }
            }
            else if (root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("actions", out JsonElement actions)
                     && actions.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement item in actions.EnumerateArray())
                {
                    AddAction(result, item, result.Count + 1);
                }
            }
            else
            {
                // 单个对象（带 steps）当成一个匿名动作
                AddAction(result, root, 1);
            }
        }
        catch
        {
            // 写坏了就当没有动作
        }

        return result;
    }

    private static void AddAction(List<PackAutoAction> result, JsonElement actionElement, int fallbackIndex)
    {
        if (actionElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var action = new PackAutoAction
        {
            Name = actionElement.TryGetProperty("name", out JsonElement name)
                   && name.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(name.GetString())
                ? name.GetString()!
                : $"动作{fallbackIndex}",
            RunOnLaunch = actionElement.TryGetProperty("runOnLaunch", out JsonElement run)
                          && run.ValueKind == JsonValueKind.True,
        };

        if (actionElement.TryGetProperty("steps", out JsonElement steps)
            && steps.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement stepElement in steps.EnumerateArray())
            {
                if (stepElement.ValueKind != JsonValueKind.Object
                    || !stepElement.TryGetProperty("action", out JsonElement stepAction)
                    || stepAction.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(stepAction.GetString()))
                {
                    continue;
                }

                // Raw 必须在 document 还活着的时候拷贝出来
                action.Steps.Add(new PackActionStep
                {
                    Action = stepAction.GetString()!.Trim(),
                    Raw = stepElement.Clone(),
                });
            }
        }

        if (action.Steps.Count > 0)
        {
            result.Add(action);
        }
    }
}
