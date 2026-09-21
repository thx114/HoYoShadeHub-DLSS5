using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.Services;
using System;
using System.IO;

namespace HoYoShadeHub.Features.Plugins;

/// <summary>
/// 插件页共用的 HoYoShade 定位。
///
/// HoYoShade 是**全局一份**，装在 &lt;用户数据目录&gt;\HoYoShade，所有游戏共用 ——
/// 所以不要去拼 &lt;游戏目录&gt;\HoYoShade。
/// </summary>
internal static class PluginHostLocator
{
    private const string ManualShadeRootKey = "hysx_manual_shade_root";

    private static string? _manualShadeRoot;
    private static bool _manualShadeRootLoaded;

    /// <summary>
    /// 用户手动指定的 HoYoShade 目录，优先于自动检测（两个页面共用）。
    /// **会记住**：便携版 / 测试沙盒里 &lt;用户数据目录&gt;\HoYoShade 未必是用户实际在用的那一份，
    /// 指定一次之后就不用每次再点「指定目录」。
    /// </summary>
    public static string? ManualShadeRoot
    {
        get
        {
            if (!_manualShadeRootLoaded)
            {
                _manualShadeRootLoaded = true;
                try
                {
                    _manualShadeRoot = AppConfig.GetValue<string>(null, ManualShadeRootKey);
                }
                catch
                {
                    _manualShadeRoot = null;
                }
            }

            return _manualShadeRoot;
        }
        set
        {
            _manualShadeRoot = value;
            _manualShadeRootLoaded = true;

            try
            {
                AppConfig.SetValue(value, ManualShadeRootKey);
            }
            catch
            {
                // ignore
            }
        }
    }

    /// <summary>&lt;用户数据目录&gt;\.hysx\addon-names.json</summary>
    public static string? AddonNameCachePath =>
        string.IsNullOrWhiteSpace(AppConfig.UserDataFolder)
            ? null
            : Path.Combine(AppConfig.UserDataFolder, ".hysx", "addon-names.json");

    public static ShadeHost? Resolve(out string reason)
    {
        reason = string.Empty;

        if (!string.IsNullOrWhiteSpace(ManualShadeRoot))
        {
            ShadeHost? manual = ShadeHostLocator.FromShadeRoot(ManualShadeRoot);
            if (manual is not null)
            {
                return manual;
            }

            reason = $"指定的目录里找不到 ReShade64.dll：{ManualShadeRoot}";
            return null;
        }

        try
        {
            ShadeHost? host = ShadeHostLocator.FromUserDataFolder(AppConfig.UserDataFolder);
            if (host is not null)
            {
                return host;
            }

            string? expected = ShadeHostLocator.GetDefaultRoot(AppConfig.UserDataFolder);
            reason = string.IsNullOrWhiteSpace(expected)
                ? "还没读到用户数据目录，请先完成首次启动向导，或点「指定目录」。"
                : $"还没装 HoYoShade：{expected}　先到「启动器」页装一次，或点「指定目录」。";
        }
        catch (Exception ex)
        {
            reason = "定位 HoYoShade 目录时出错：" + ex.Message;
        }

        return null;
    }

    public static ExtensionManagerService? ResolveManager(out string reason)
    {
        ShadeHost? host = Resolve(out reason);
        if (host is null)
        {
            return null;
        }

        var manager = new ExtensionManagerService(host);

        // 远端目录（每天拉一次，缓存见 RemoteCatalogService）里的插件条目按 id 覆盖内置的 ——
        // 以后改插件来源 / 加新插件不用重新发版。
        string pluginsCache = RemoteCatalogService.PluginsCachePath;
        if (!string.IsNullOrWhiteSpace(pluginsCache) && File.Exists(pluginsCache))
        {
            manager.Catalog.ExtraCatalogFiles.Add(pluginsCache);
        }

        return manager;
    }
}
