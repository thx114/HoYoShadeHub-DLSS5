using HoYoShadeHub.Extensions.Services;
using HoYoShadeHub.Features.OptiScaler;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;

namespace HoYoShadeHub.Features.GameLauncher;

/// <summary>
/// 启动前把 OptiScaler 构建体检写进启动日志（两条启动路径共用一份，别各写一遍）。
///
/// <para>
/// 只报告，不擅自改构建 —— 补齐统一走「全局插件 → OptiScaler」，或者由启动流程里已有的
/// <c>EnsureDlssgForUnlock</c> 这类补齐动作做。问题是：以前只有补齐、没有校验，
/// 构建里缺 <c>nvngx_dlss.dll</c> / <c>dlssg_sm86.dll</c> 时启动日志里一个字都没有，
/// 游戏菜单里只是少了对应选项，排查全靠猜。这里把"按这份配置该有的文件"逐条摊开。
/// </para>
/// </summary>
internal static class OptiScalerBuildAuditLog
{
    /// <summary>写一次体检：没发现问题记一行 Info，有问题逐个 Warning（Warning 会进本局日志摘要）。</summary>
    public static void Write(ILogger logger, string? buildDirectory)
    {
        if (string.IsNullOrWhiteSpace(buildDirectory))
        {
            return;
        }

        // 显卡代次只用来判「开错解锁」（40/50 系 vs 20/30 系）；认不出就是 null，那就不评判
        string? gpuClass = OptiScalerPresetCatalog.GpuClass;
        IReadOnlyList<OptiScalerRuntime.OptiScalerBuildIssue> issues =
            OptiScalerRuntime.AuditBuild(buildDirectory, gpuClass);

        if (issues.Count == 0)
        {
            logger.LogInformation("OptiScaler 启动体检：构建文件齐全（{Directory}；显卡代次 {GpuClass}）",
                buildDirectory, gpuClass ?? "未知");
            return;
        }

        foreach (OptiScalerRuntime.OptiScalerBuildIssue issue in issues)
        {
            logger.LogWarning("OptiScaler 启动体检：{Issue}", issue);
        }
    }
}
