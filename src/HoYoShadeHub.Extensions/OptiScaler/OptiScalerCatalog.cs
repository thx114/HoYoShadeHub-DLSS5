using HoYoShadeHub.Extensions.Models;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// 一个 OptiScaler 来源（GitHub 仓库）。
///
/// <para>
/// 这些是社区维护的、带 DLSS 神经渲染（NR）的 OptiScaler 分支 —— 跟 ReShade 插件不是一回事：
/// 它是个独立的 DLL（用户勾了「启动 OptiScaler」时由 Hub 注入游戏进程），
/// 所以不能塞进扩展包目录（<c>reshade-shaders\Addons</c>），得单独放一份自己的库。
/// </para>
/// </summary>
public sealed class OptiScalerSource
{
    // 注意：这些模型会被 WinUI 的 XamlTypeInfo 生成器 new 出来（XamlTypeInfo.g.cs），
    // 所以不能用 C# 的 required —— 生成的 new T() 会报 CS9035。改用带默认值的 init 属性。
    /// <summary>短 id，用作本地目录名（<c>&lt;库&gt;\&lt;id&gt;\&lt;版本&gt;\</c>）</summary>
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    /// <summary>owner/repo</summary>
    public string Repository { get; init; } = string.Empty;

    public string Description { get; init; } = string.Empty;

    /// <summary>tag 正则过滤（比如跳过跟 OptiScaler 无关的 display-filter release）</summary>
    public string? TagPattern { get; init; }

    /// <summary>
    /// 资产名 glob（* 与 ?）过滤；为空时不过滤。
    /// 一个 release 同时发 setup exe 和 standalone zip 时用它收窄（例如只要 standalone zip）。
    /// </summary>
    public string? AssetPattern { get; init; }

    /// <summary>卡片上显示的标签（跟插件那边的 tags 一套）</summary>
    public string[]? Tags { get; init; }

    public string? Homepage { get; init; }

    public ExtensionSource ToExtensionSource() => new()
    {
        Type = ExtensionSourceType.GithubRelease,
        Repository = Repository,
        TagPattern = TagPattern,
        AssetPattern = AssetPattern,
        IncludePrerelease = true,
    };
}

/// <summary>
/// OptiScaler 来源目录（GitHub 仓库列表）。
///
/// <para>
/// 2026-09 起**没有内置表了**：条目全部来自远端目录 <c>catalog/optiscaler.json</c>
/// （由 <c>RemoteCatalogService</c> 每天最多拉一次、缓存在 &lt;用户数据目录&gt;\.hysx\catalog\），
/// 加新来源 / 改来源信息只改仓库里的 json，不重新发版。首跑离线时启动器随包带一份
/// catalog 副本做种子（见 RemoteCatalogService.SeedFromBundle）。
/// </para>
/// </summary>
public static class OptiScalerCatalog
{
    /// <summary>把远端目录规范化成可用列表：按 id 去重（后者覆盖前者）、跳过缺 id / 仓库的条目。</summary>
    public static List<OptiScalerSource> Normalize(IEnumerable<OptiScalerSource>? overlay)
    {
        var result = new List<OptiScalerSource>();

        if (overlay is null)
        {
            return result;
        }

        foreach (OptiScalerSource source in overlay)
        {
            if (string.IsNullOrWhiteSpace(source.Id) || string.IsNullOrWhiteSpace(source.Repository))
            {
                continue;
            }

            int index = result.FindIndex(s => string.Equals(s.Id, source.Id, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                result[index] = source;
            }
            else
            {
                result.Add(source);
            }
        }

        return result;
    }

    /// <summary>按 id 在一份目录里找来源；找不到返回 null。</summary>
    public static OptiScalerSource? Find(IEnumerable<OptiScalerSource>? sources, string id)
        => sources?.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>读一个远端目录文件（读不到 / 坏了就当没有）。</summary>
    public static List<OptiScalerSource>? LoadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            OptiScalerCatalogDocument? document = JsonSerializer.Deserialize<OptiScalerCatalogDocument>(
                File.ReadAllText(path),
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                });

            return document?.Sources;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}

/// <summary>远端 <c>catalog/optiscaler.json</c> 的文档形状。</summary>
public sealed class OptiScalerCatalogDocument
{
    [JsonPropertyName("sources")]
    public List<OptiScalerSource> Sources { get; set; } = [];
}
