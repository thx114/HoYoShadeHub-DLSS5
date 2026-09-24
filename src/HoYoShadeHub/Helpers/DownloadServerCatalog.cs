using HoYoShadeHub.Models;
using System.Collections.Generic;

namespace HoYoShadeHub.Helpers;

/// <summary>
/// 下载服务器下拉列表的统一构建。
///
/// <para>
/// 以前每个页面（关于 / 文件管理 / 更新窗口 / HoYoShade 下载 / ReShade 下载）
/// 各自手写一份 <c>new DownloadServerItem { ... }</c>，加一个新服务器要改 5 个地方，
/// 漏一个就会出现「这个页面能选、那个页面选不到」。现在统一从这里取。
/// </para>
///
/// <para>
/// 服务器序号约定（与 <see cref="CloudProxyManager.GetProxyUrl"/> 一一对应）：
/// <c>-1</c> 自动选择、<c>0</c> GitHub 直连、<c>1</c> Cloudflare、<c>2</c> 腾讯云、<c>3</c> 阿里云、
/// <c>4</c> gh-proxy.org、<c>5</c> ghfast.top、<c>6</c> gh-proxy.com、<c>7</c> github.akams.cn。
/// </para>
/// </summary>
public static class DownloadServerCatalog
{
    /// <summary>
    /// 公共 GitHub 加速代理（第三方服务）—— 只留实测能拉到 raw.githubusercontent.com 的两个。
    /// <para>
    /// 6 / 7 撤掉的原因（2026-09 实测）：github.akams.cn 只转发 github.com/...，
    /// 对 raw 域名一律 404 —— 而组件清单和远端目录都在 raw 上，留着它等于没代理。
    /// 两个前缀仍保留在 CloudProxyManager 里，老配置写过 6 / 7 的不会炸。
    /// </para>
    /// </summary>
    public static readonly (int Index, string Name)[] GhProxies =
    [
        (4, "gh-proxy.org"),
        (5, "ghfast.top"),
    ];

    /// <summary>
    /// 完整列表（自动选择 + GitHub 直连 + 自家 CDN + 公共 gh 代理）。
    /// </summary>
    /// <param name="localizedNames">
    /// 可选：给自家 CDN 那几个用界面语言的名字（懒得传就用固定中文）。
    /// 键是服务器序号。
    /// </param>
    public static List<DownloadServerItem> Create(IReadOnlyDictionary<int, string>? localizedNames = null)
    {
        string Name(int index, string fallback) =>
            localizedNames is not null && localizedNames.TryGetValue(index, out string? v) && !string.IsNullOrWhiteSpace(v)
                ? v
                : fallback;

        var list = new List<DownloadServerItem>
        {
            new() { Name = Name(-1, "自动选择"), ServerIndex = -1 },
            new() { Name = Name(0, "GitHub 直连"), ServerIndex = 0 },
            new() { Name = Name(1, "Cloudflare"), ServerIndex = 1 },
            new() { Name = Name(2, "腾讯云"), ServerIndex = 2 },
            new() { Name = Name(3, "阿里云"), ServerIndex = 3 },
        };

        foreach ((int index, string name) in GhProxies)
        {
            list.Add(new DownloadServerItem { Name = name, ServerIndex = index });
        }

        return list;
    }

    /// <summary>服务器序号 → 显示名（日志 / 状态文案用）</summary>
    public static string Describe(int serverIndex) => serverIndex switch
    {
        -1 => "自动选择",
        0 => "GitHub 直连",
        1 => "Cloudflare",
        2 => "腾讯云",
        3 => "阿里云",
        4 => "gh-proxy.org",
        5 => "ghfast.top",
        6 => "gh-proxy.com",
        7 => "github.akams.cn",
        _ => "GitHub 直连",
    };
}
