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

    public ExtensionSource ToExtensionSource() => new()
    {
        Type = ExtensionSourceType.GithubRelease,
        Repository = Repository,
        TagPattern = TagPattern,
        IncludePrerelease = true,
    };
}

/// <summary>内置的 OptiScaler 来源表（用户 2026-09 给的三个仓库）。</summary>
public static class OptiScalerCatalog
{
    public static IReadOnlyList<OptiScalerSource> Builtin { get; } =
    [
        new OptiScalerSource
        {
            Id = "wilsjo2",
            Name = "OptiScaler DLSSNR PreSR Multipass (wilsjo2)",
            Repository = "wilsjo2/OptiScaler-DLSSNR-PreSR-Multipass",
            Description = "DLSS 神经渲染 + pre-SR 摆放，支持 1~3 遍处理。带 -rtx40-mfg 的是 40 系多帧生成特化包。",
            TagPattern = @"^v?\d",
        },
        new OptiScalerSource
        {
            Id = "neurotic",
            Name = "NeuRotic (MagicalPrincessUnicorn)",
            Repository = "MagicalPrincessUnicorn/NeuRotic-an-OptiScaler-DLSSNR-fork",
            Description = "NeuRotic 分支（alpha 系列）。名字里带 Patch 的包是在上一版上打的补丁，单独装是残缺的。",
            TagPattern = @"^alpha-\d",
        },
        new OptiScalerSource
        {
            Id = "dlssnr-amd",
            Name = "DLSS NR on AMD (danielblnc)",
            Repository = "danielblnc/DLSS-NR-on-AMD",
            Description = "A 卡用的 DLSS-NR 分支。它只有一个自己的安装程序：下载后会先弹窗提醒，然后运行它 ——" +
                          "装到默认目录（就是构建目录）即可；装完那里会有 version.dll，勾「启动 OptiScaler」就能注入。",
            TagPattern = @"^v?\d",
        },
    ];

    public static OptiScalerSource? Find(string id)
        => Builtin.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 内置来源 + 远端目录的覆盖（同 id 用远端那条）。
    /// 远端目录就是仓库里的 <c>catalog/optiscaler.json</c>：改来源 / 加新分支不用重新发版。
    /// </summary>
    public static List<OptiScalerSource> MergeWithBuiltin(IEnumerable<OptiScalerSource>? overlay)
    {
        var result = new List<OptiScalerSource>(Builtin);

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
