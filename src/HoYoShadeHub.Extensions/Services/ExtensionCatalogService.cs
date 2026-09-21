using HoYoShadeHub.Extensions.Models;
using System.Reflection;
using System.Text.Json;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// 扩展目录（catalog）的加载：内置 → 远程 → 用户本地，后者按 id 覆盖前者。
/// 远程目录让我们不重新发版也能加/改条目。
/// </summary>
public sealed class ExtensionCatalogService
{
    public const string BuiltinResourceName = "HoYoShadeHub.Extensions.Resources.catalog.builtin.json";

    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 远程目录地址。默认指向本项目仓库里的 catalog.json，
    /// 为空或拉取失败时静默回退到内置目录。
    /// </summary>
    public string? RemoteCatalogUrl { get; set; }

    /// <summary>用户自己加的本地目录文件（可以放 .hysx 单包清单或 catalog 文档）</summary>
    public List<string> ExtraCatalogFiles { get; } = [];

    public async Task<ExtensionCatalogDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        ExtensionCatalogDocument document = LoadBuiltin();

        if (!string.IsNullOrWhiteSpace(RemoteCatalogUrl))
        {
            ExtensionCatalogDocument? remote = await TryLoadRemoteAsync(RemoteCatalogUrl, cancellationToken);
            if (remote is not null)
            {
                Merge(document, remote);
            }
        }

        foreach (string file in ExtraCatalogFiles)
        {
            ExtensionCatalogDocument? extra = await TryLoadFileAsync(file, cancellationToken);
            if (extra is not null)
            {
                Merge(document, extra);
            }
        }

        document.Extensions = [.. document.Extensions
            .Where(e => e.IsValid)
            .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];

        return document;
    }

    public static ExtensionCatalogDocument LoadBuiltin()
    {
        using Stream? stream = typeof(ExtensionCatalogService).Assembly.GetManifestResourceStream(BuiltinResourceName);
        if (stream is null)
        {
            return new ExtensionCatalogDocument();
        }

        try
        {
            return JsonSerializer.Deserialize<ExtensionCatalogDocument>(stream, _jsonOptions)
                   ?? new ExtensionCatalogDocument();
        }
        catch (JsonException)
        {
            return new ExtensionCatalogDocument();
        }
    }

    private static async Task<ExtensionCatalogDocument?> TryLoadRemoteAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var client = Networking.HysxHttp.CreateClient(timeout: TimeSpan.FromSeconds(20));
            await using Stream stream = await client.GetStreamAsync(Networking.HysxHttp.Apply(url), cancellationToken);
            return await JsonSerializer.DeserializeAsync<ExtensionCatalogDocument>(stream, _jsonOptions, cancellationToken);
        }
        catch
        {
            // 远程目录只是锦上添花，失败不打扰用户
            return null;
        }
    }

    private static async Task<ExtensionCatalogDocument?> TryLoadFileAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<ExtensionManifest>(stream, _jsonOptions, cancellationToken);
            if (manifest is not null)
            {
                // 允许把一个单包清单直接当目录用
                return new ExtensionCatalogDocument { Extensions = [manifest] };
            }
        }
        catch (JsonException)
        {
            // 落到下面按 catalog 文档再试一次
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ExtensionCatalogDocument>(stream, _jsonOptions, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把 overlay 合并进 target，同 id 覆盖</summary>
    public static void Merge(ExtensionCatalogDocument target, ExtensionCatalogDocument overlay)
    {
        var map = target.Extensions.ToDictionary(e => e.Id, StringComparer.OrdinalIgnoreCase);
        var order = target.Extensions.Select(e => e.Id).ToList();

        foreach (ExtensionManifest manifest in overlay.Extensions)
        {
            if (string.IsNullOrWhiteSpace(manifest.Id))
            {
                continue;
            }

            if (map.ContainsKey(manifest.Id))
            {
                map[manifest.Id] = manifest;
            }
            else
            {
                map[manifest.Id] = manifest;
                order.Add(manifest.Id);
            }
        }

        target.Extensions = [.. order.Select(id => map[id])];
        if (overlay.UpdatedAt is not null)
        {
            target.UpdatedAt = overlay.UpdatedAt;
        }
    }
}
