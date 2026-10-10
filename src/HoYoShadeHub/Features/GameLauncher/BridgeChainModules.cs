using HoYoShadeHub.Core.HoYoPlay;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>
/// 把用户在这个游戏勾的**其他模块**也算进所要的注入清单：原神是桥（Dx11FsrBridge）的
/// <c>Dx11FsrBridge.chain.txt</c>，其他游戏的火箭模式是 Rocket 的「&lt;游戏名&gt;插件DLL列表」。
///
/// <para>
/// 为什么需要它：这两条路线里 Hub 都**故意不开外部注入器**（原神那条是「多个注入者抢时间」的
/// 旧路线，见 GameLauncherPage.StartGame 的 orderedInjectChain 分支；火箭那条是火箭独占进程创建
/// 与注入），而两侧的清单以前只写图形层（GIMI/OptiScaler/ReShade）—— 结果用户在「模块」页勾了
/// DLSS-NR、DLSS-Enabler 这类东西，启动时**没有任何人注入它**，静默失效（勾了等于没勾）。
/// </para>
///
/// <para>
/// 这里把「这个游戏勾选的模块」减去清单里已经各就各位的那几层（桥自己、OptiScaler、ReShade、
/// GIMI 的 <c>d3d11.dll</c>、FFX 预载），剩下的原样交给清单。原神那条由桥在游戏进程内
/// <c>LoadLibraryW</c>：进程内加载不走 <c>VirtualAllocEx</c>/<c>CreateRemoteThread</c>，
/// 绕开 mhyprot 对**外部**注入的拦截 —— 这正是原神必须走链的原因。
/// </para>
///
/// <para>
/// 注意：每个模块自己的「注入延迟」在链/清单模式下不生效（链只有 <c>wait</c>/<c>load</c>/<c>migoto</c>
/// 三种动词，没有 sleep）。原神要临时改顺序，用数据目录根部的 <c>bridge-chain.override.txt</c>
/// （见 <c>OptiScalerRuntime.FsrBridgeChainOverrideName</c>）。
/// </para>
/// </summary>
internal static class BridgeChainModules
{
    /// <param name="gameId">要取哪个游戏勾的模块</param>
    /// <param name="handled">清单里已经自己处理的 DLL 路径（桥、OptiScaler、ReShade、GIMI、FFX…），这些要排掉</param>
    public static List<string> Resolve(GameId gameId, params string?[] handled)
    {
        var result = new List<string>();
        if (!AppConfig.GetUseModulesLaunchOption(gameId))
        {
            return result;
        }

        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? path in handled)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                skip.Add(Path.GetFullPath(path));
            }
            catch
            {
                // 路径非法就不参与排除；下面的 File.Exists 还会再挡一次
            }
        }

        foreach ((string _, string _, string path) in Features.Modules.ModuleRegistry.ResolveInjectionDlls(gameId))
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(path);
            }
            catch
            {
                continue;
            }

            if (skip.Contains(full))
            {
                continue;
            }

            if (result.Any(existing => string.Equals(existing, full, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            result.Add(full);
        }

        return result;
    }
}
