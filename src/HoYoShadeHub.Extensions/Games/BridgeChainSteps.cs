using HoYoShadeHub.Extensions.Services;
using System.Collections.Generic;
using System.IO;

namespace HoYoShadeHub.Extensions.Games;

/// <summary>
/// 桥链清单（<c>Dx11FsrBridge.chain.txt</c>）的步骤拼装。纯函数，写盘在
/// <see cref="OptiScalerRuntime.WriteFsrBridgeChain"/>，这样顺序约束能被单测钉住。
/// </summary>
public static class BridgeChainSteps
{
    /// <summary>
    /// 官方 XXMI 模式（游戏由 XXMI 创建、GIMI 由 XXMI 注入，Hub 走批量注入）用的链：
    /// <c>wait dxgi.dll</c> → 用户勾的其他模块 → （ReShade 在场时）<c>wait ReShade64.dll</c> → OptiScaler。
    ///
    /// <para>
    /// **绝不写 migoto**：GIMI 这一层归 XXMI，链里再来一份就是两份 <c>d3d11.dll</c> 抢顺序。
    /// **绝不 load ReShade**：官方模式里 ReShade 仍由 <c>inject.exe</c>／注入规格负责，链里再
    /// load 一次就是同进程两份 ReShade；这里只用 <c>wait</c> 等它就位，同时保住
    /// 「OptiScaler 排在 ReShade 之后」这条 NR 时序约束（等不到就 15 秒超时继续，不卡游戏）。
    /// </para>
    /// </summary>
    public static List<OptiScalerRuntime.FsrBridgeChainStep> OfficialMode(
        string optiDll, bool shadePresent, IReadOnlyList<string> extraDlls)
    {
        var steps = new List<OptiScalerRuntime.FsrBridgeChainStep>
        {
            OptiScalerRuntime.FsrBridgeChainStep.Wait("dxgi.dll"),
        };

        foreach (string extra in extraDlls)
        {
            if (!string.IsNullOrWhiteSpace(extra))
            {
                steps.Add(OptiScalerRuntime.FsrBridgeChainStep.Load(extra));
            }
        }

        if (shadePresent)
        {
            steps.Add(OptiScalerRuntime.FsrBridgeChainStep.Wait("ReShade64.dll"));
        }

        steps.Add(OptiScalerRuntime.FsrBridgeChainStep.Load(optiDll));
        steps.Add(OptiScalerRuntime.FsrBridgeChainStep.Wait(Path.GetFileName(optiDll)));
        return steps;
    }
}
