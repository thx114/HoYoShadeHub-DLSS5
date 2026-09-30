using HoYoShadeHub.Extensions.Models;
using System.IO;
using System.Text.Json;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// 扩展目录（catalog）的加载：远程 → 用户本地，后者按 id 覆盖前者。
///
/// <para>
/// 2026-09-30 起**没有嵌入式内置目录了**：条目全部来自远端 catalog/plugins.json
/// （由启动器的 RemoteCatalogService 每天最多拉一次、缓存在 &lt;用户数据目录&gt;\.hysx\catalog\，
/// 经 <see cref="ExtraCatalogFiles"/> 进来），加新插件 / 改插件信息只改仓库里的 json，
/// 不重新发版。首跑离线时启动器随包的 catalog 副本会做种子（见 RemoteCatalogService.SeedFromBundle）。
/// </para>
/// </summary>
public sealed class ExtensionCatalogService
{
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
        // 没有内置表了：从空文档开始，远程目录 + 本地文件按 id 覆盖/追加。
        var document = new ExtensionCatalogDocument();

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
        => new();

    /// <summary>同步读一个目录文件（本地缓存用；读不到 / 坏了返回 null）。允许单包清单当目录用。</summary>
    public static ExtensionCatalogDocument? LoadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        // 先按目录文档解析；单包清单（.hysx）没有 "extensions" 键会解析出空表，再退到单条清单。
        // 顺序不能反：整份目录文档也能「解析成」一条空清单（未知键被忽略），那样只剩 1 个假条目。
        ExtensionCatalogDocument? document = TryParseCatalogDocument(path);
        if (document is { Extensions.Length: > 0 })
        {
            return document;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var manifest = JsonSerializer.Deserialize<ExtensionManifest>(stream, _jsonOptions);
            return manifest is null ? null : new ExtensionCatalogDocument { Extensions = [manifest] };
        }
        catch
        {
            return null;
        }
    }

    private static ExtensionCatalogDocument? TryParseCatalogDocument(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<ExtensionCatalogDocument>(stream, _jsonOptions);
        }
        catch (JsonException)
        {
            return null;
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

        // 先按目录文档解析；单包清单（.hysx）没有 "extensions" 键会解析出空表，再退到单条清单。
        // 顺序不能反：整份目录文档也能「解析成」一条空清单（未知键被忽略），那样只剩 1 个假条目。
        try
        {
            await using var stream = File.OpenRead(path);
            var document = await JsonSerializer.DeserializeAsync<ExtensionCatalogDocument>(stream, _jsonOptions, cancellationToken);
            if (document is { Extensions.Length: > 0 })
            {
                return document;
            }
        }
        catch (JsonException)
        {
            // 落到下面按单包清单再试一次
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
            // 两个形状都不是，按没有处理
        }

        return null;
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

            // 墓碑：远端把这条删了
            if (manifest.Removed)
            {
                map.Remove(manifest.Id);
                order.RemoveAll(id => string.Equals(id, manifest.Id, StringComparison.OrdinalIgnoreCase));
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
