namespace HoYoShadeHub.Extensions.Games;

/// <summary>
/// 「挂起创建 → 注入 → 恢复」这条生命周期的**判定**部分（纯函数，扩展测试可覆盖）：
/// 把 Win32 的原始返回值翻成「进程到底在不在跑」和「远端参数能不能释放」。
///
/// <para>
/// 之所以单独拿出来：<c>ResumeThread</c> 的返回值是**之前的挂起计数**，不是成败标记 —— 只判
/// <c>== 0xFFFFFFFF</c> 会把「还有别的挂起计数、进程其实没跑起来」当成成功；而远端线程超时后
/// 仍然释放/覆盖 <c>LoadLibraryW</c> 的参数缓冲区，等于往目标进程里投一个 use-after-free。
/// 两条都属于「界面报成功、实际没成功」的静默失败，所以判定要独立可测。
/// </para>
/// </summary>
public static class InjectionLifecyclePolicy
{
    /// <summary>WaitForSingleObject：对象已信号（这里 = 线程已结束）</summary>
    public const uint WaitObject0 = 0x00000000;

    /// <summary>WaitForSingleObject：超时（线程仍在跑）</summary>
    public const uint WaitTimeout = 0x00000102;

    /// <summary>WaitForSingleObject：调用失败</summary>
    public const uint WaitFailed = 0xFFFFFFFF;

    /// <summary>
    /// <c>ResumeThread</c> 的返回值 → 进程的实际状态。
    /// 返回值语义：0 = 本来就没挂起；1 = 正常从挂起恢复；&gt;1 = **还剩别的挂起计数**；0xFFFFFFFF = 调用失败。
    /// </summary>
    public static ResumeThreadOutcome ClassifyResume(uint previousSuspendCount) => previousSuspendCount switch
    {
        uint.MaxValue => ResumeThreadOutcome.Failed,
        0 or 1 => ResumeThreadOutcome.Running,
        _ => ResumeThreadOutcome.StillSuspended,
    };

    /// <summary>
    /// 远端参数缓冲区（<c>LoadLibraryW</c> 的路径字符串）能不能释放或覆盖：
    /// **只有确认远端线程已经结束**才安全，否则线程可能正在读那块内存。
    /// 超时/等待失败时宁可漏掉那几 KB。
    /// </summary>
    public static bool CanReleaseRemoteParameter(uint threadWaitResult) => threadWaitResult == WaitObject0;

    /// <summary>终止是否已确认（别在日志里写「已终止」而其实没退）</summary>
    public static bool TerminateConfirmed(bool terminated, uint waitResult) =>
        terminated && waitResult == WaitObject0;

    /// <summary>终止结果的说明文本：给日志和用户提示用，不谎报</summary>
    public static string DescribeTerminate(bool terminated, uint waitResult)
    {
        if (!terminated)
        {
            return "终止调用失败，可能留下一个未清理的游戏进程";
        }

        return waitResult switch
        {
            WaitObject0 => "已终止并确认退出",
            WaitTimeout => "已发终止但 2 秒内未确认退出",
            _ => $"已发终止但退出确认失败（WaitForSingleObject=0x{waitResult:X8}）",
        };
    }
}

/// <summary><c>ResumeThread</c> 之后进程的实际状态</summary>
public enum ResumeThreadOutcome
{
    /// <summary>ResumeThread 调用失败（返回 0xFFFFFFFF）</summary>
    Failed,

    /// <summary>进程已在跑：返回值 0 = 本来就没挂起，1 = 正常从挂起恢复</summary>
    Running,

    /// <summary>返回值 &gt;1：还剩别的挂起计数，进程**仍然没跑**</summary>
    StillSuspended,
}
