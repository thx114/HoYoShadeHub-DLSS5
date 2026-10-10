using HoYoShadeHub.Core.HoYoPlay;
using HoYoShadeHub.Extensions.Games;
using HoYoShadeHub.Extensions.Models;
using HoYoShadeHub.Extensions.ReShade;
using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.Modules;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>
/// 可逆性：把启动器写进「桥 / Rocket」的东西收回来。
///
/// <para>
/// 会残留的只有两种形态，而且都**与 Hub 是否启动无关**（所以必须主动回收）：
/// <list type="bullet">
/// <item>原神：桥目录下的 <c>Dx11FsrBridge.chain.txt</c> —— 桥只要被注进进程（哪怕游戏是
/// Rocket 或用 pack/CLI 启动的）就会照它 <c>LoadLibraryW</c> 那些行。</item>
/// <item>非原神：Rocket <c>config.ini</c> 里的 <c>&lt;游戏名&gt;插件DLL列表</c> —— 用户自己
/// 开 Rocket 启动游戏时照旧生效。</item>
/// </list>
/// 取消勾选火箭模式、切走游戏时都要收干净，否则表现就是「取消勾选的 DLL 还在注入」。
/// </para>
///
/// <para>
/// 只动**启动器自己写的那份**：原神从来不写 Rocket 的插件列表（走桥的单 DLL 键），
/// 所以不碰它的插件列表键，免得把用户自己填的占位删掉。每次写入都留
/// <c>.bak-before-hysx-rocket*</c> 备份，最坏情况可以按备份逐份回退。
/// </para>
/// </summary>
internal static class RocketArtifacts
{
    /// <summary>回收这个游戏上启动器写过的东西，返回做了什么（空串 = 没这个游戏的事）。</summary>
    public static string Release(GameId? gameId, string? gameDisplayName)
    {
        if (gameId is not { } id)
        {
            return string.Empty;
        }

        var notes = new List<string>();
        bool genshinBridge = RocketIntegration.SupportsGame(id.GameBiz.Value);

        // 只有非原神才用火箭的插件列表；原神用的是桥的单 DLL 键 + 链清单
        if (!genshinBridge)
        {
            string? rocketGame = RocketIntegration.GameNameFor(id.GameBiz.Value, gameDisplayName);
            string? config = RocketIntegration.FindConfigPath(AppConfig.RocketConfigPath);
            if (rocketGame is not null && config is not null)
            {
                RocketIntegration.PrepareResult cleared = RocketIntegration.ClearPluginList(config, rocketGame);
                notes.Add(cleared.Success
                    ? "Rocket「" + rocketGame + "插件DLL列表」已清空"
                      + (string.IsNullOrWhiteSpace(cleared.BackupPath) ? "" : "（备份 " + Path.GetFileName(cleared.BackupPath) + "）")
                    : "清 Rocket 插件列表失败：" + cleared.Message);
            }
        }

        // 原神：桥的链清单是「谁被拉进进程」的唯一依据，撤掉它才真的不再注入那些模块
        if (genshinBridge)
        {
            string? bridge = ModuleRegistry.ResolveInjectionDlls(id)
                .FirstOrDefault(spec => ModuleRegistry.IsGenshinFsrBridgeDll(spec.DllPath)).DllPath;
            string? directory = string.IsNullOrWhiteSpace(bridge) ? null : Path.GetDirectoryName(bridge);
            if (directory is not null && OptiScalerRuntime.RemoveFsrBridgeChain(directory, out string? chainBackup))
            {
                notes.Add("桥的链清单已撤走"
                    + (string.IsNullOrWhiteSpace(chainBackup) ? "" : "（备份 " + Path.GetFileName(chainBackup) + "）"));
            }
        }

        return string.Join("；", notes);
    }
}
