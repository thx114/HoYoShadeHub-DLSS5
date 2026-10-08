namespace HoYoShadeHub.Extensions.Games;

public static class GenshinLaunchRouting
{
    /// <summary>XXMI manual launch uses the ordinary Hub injection/start sequence.</summary>
    public static bool UseEarlyGraphicsLaunch(bool injectMode, bool starward, bool xxmiEnabled) =>
        !injectMode && !starward && !xxmiEnabled;

    /// <summary>
    /// 能不能走「有序注入链」：外部只在 CreateProcess 那一刻注桥，其余各层由桥在游戏进程内按
    /// <c>Dx11FsrBridge.chain.txt</c> 的行序 LoadLibraryW（见
    /// <see cref="Services.OptiScalerRuntime.WriteFsrBridgeChain"/>）。
    ///
    /// <para>
    /// 为什么要它：多个注入者各自抢时间（历史上六轮注入时序实验全部证伪——游戏建 D3D11 设备的时刻
    /// 在 0.55s 和 4.9s 两个极端都出现过，而外部注入窗口受反作弊限制不到 1 秒，三者交集不稳定）。
    /// 桥本来就在进程内拉 OptiScaler，让它按清单多拉两层，顺序就从「抢时间」变成「数据」，
    /// 而且进程内 LoadLibraryW 不受 mhyprot 拦截。
    /// </para>
    ///
    /// <para>
    /// 链里**含 3DMigoto，且排在 OptiScaler/ReShade 之前**：GIMI 的代理 <c>d3d11.dll</c> 必须是先占位的
    /// 那一层（反过来 3DMigoto 装载返回 600）。早先「由桥加载的 GIMI 会被 <c>[Loader] loader</c> 拒载、
    /// 并留下 <c>CreateDXGIFactory1</c> 钩子把游戏打崩（<c>0xC0000005</c>、<c>at=&lt;no-module&gt;</c>）」
    /// 的归因已被证伪：真因是桥克隆 <c>ID3D11DeviceContext</c> 虚表少算 21 个槽（128 vs Context4 的 149），
    /// 桥修好之后链里带 migoto 步实测能正常进游戏（见 <c>docs/XXMI-挂载顺序-调查-20261008.md</c> 第 14 节）。
    /// 走链时仍要把 XXMI 的注入模式设成 SKIP，避免出现两份 <c>d3d11.dll</c> 抢顺序。
    /// </para>
    ///
    /// <para>前提缺一不可：</para>
    /// <list type="bullet">
    /// <item><paramref name="manualXxmi"/>：官方模式下游戏由 XXMI 自己 CreateProcess，Hub 拿不到父句柄就注不进桥。</item>
    /// <item><paramref name="optiScalerEnabled"/> + <paramref name="bridgeDllPath"/>：链是桥 autoload 的扩展，没有桥就没有执行者。</item>
    /// <item><paramref name="isGenshin"/>：这套顺序是原神链路上实测出来的，别的游戏不套用。</item>
    /// </list>
    /// </summary>
    public static bool CanUseOrderedInjectChain(
        bool manualXxmi,
        bool injectMode,
        bool starward,
        bool optiScalerEnabled,
        bool isGenshin,
        string? gameInstallPath,
        string? bridgeDllPath) =>
        manualXxmi
        && !injectMode
        && !starward
        && optiScalerEnabled
        && isGenshin
        && !string.IsNullOrWhiteSpace(gameInstallPath)
        && !string.IsNullOrWhiteSpace(bridgeDllPath);
}
