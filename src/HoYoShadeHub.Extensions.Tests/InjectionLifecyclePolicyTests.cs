using HoYoShadeHub.Extensions.Games;

namespace HoYoShadeHub.Extensions.Tests;

/// <summary>
/// 注入生命周期的**失败路径**判定（挂起进程恢复 / 远端线程超时后的参数释放）。
/// 这些判定原来散在 P/Invoke 调用点里、只判 <c>0xFFFFFFFF</c>，于是出现过
/// 「界面报已启动、实际进程仍挂起」和「超时后释放远端参数」两类静默失败。
/// </summary>
internal static class InjectionLifecyclePolicyTests
{
    public static (int Passed, int Failed) Run()
    {
        int passed = 0, failed = 0;
        void Check(bool ok, string name)
        {
            Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}");
            if (ok) passed++; else failed++;
        }

        // ResumeThread 返回的是"之前的挂起计数"，不是成败标记
        Check(InjectionLifecyclePolicy.ClassifyResume(0) == ResumeThreadOutcome.Running,
            "ResumeThread 返回 0（本来就没挂起）视为进程在跑");
        Check(InjectionLifecyclePolicy.ClassifyResume(1) == ResumeThreadOutcome.Running,
            "ResumeThread 返回 1（正常的挂起恢复）视为进程在跑");
        Check(InjectionLifecyclePolicy.ClassifyResume(2) == ResumeThreadOutcome.StillSuspended,
            "ResumeThread 返回 2（还剩挂起计数）必须判为仍挂起，不能当成功");
        Check(InjectionLifecyclePolicy.ClassifyResume(5) == ResumeThreadOutcome.StillSuspended,
            "任意 >1 的挂起计数都判为仍挂起");
        Check(InjectionLifecyclePolicy.ClassifyResume(uint.MaxValue) == ResumeThreadOutcome.Failed,
            "ResumeThread 返回 0xFFFFFFFF 判为调用失败");

        // 远端线程没结束就不许释放/覆盖 LoadLibraryW 的参数缓冲区
        Check(InjectionLifecyclePolicy.CanReleaseRemoteParameter(InjectionLifecyclePolicy.WaitObject0),
            "远端线程已结束才允许释放/清空参数缓冲区");
        Check(!InjectionLifecyclePolicy.CanReleaseRemoteParameter(InjectionLifecyclePolicy.WaitTimeout),
            "远端线程超时不得释放参数缓冲区（否则目标进程 use-after-free）");
        Check(!InjectionLifecyclePolicy.CanReleaseRemoteParameter(InjectionLifecyclePolicy.WaitFailed),
            "等待调用失败同样不得释放参数缓冲区");

        // 终止必须确认退出，日志/提示不许谎报
        Check(InjectionLifecyclePolicy.TerminateConfirmed(true, InjectionLifecyclePolicy.WaitObject0),
            "终止成功且确认退出才算清理完成");
        Check(!InjectionLifecyclePolicy.TerminateConfirmed(true, InjectionLifecyclePolicy.WaitTimeout),
            "只发终止、2 秒内没确认退出不算清理完成");
        Check(!InjectionLifecyclePolicy.TerminateConfirmed(false, InjectionLifecyclePolicy.WaitObject0),
            "终止调用失败不算清理完成");
        Check(InjectionLifecyclePolicy.DescribeTerminate(false, InjectionLifecyclePolicy.WaitObject0).Contains("失败"),
            "终止失败时说明文本必须承认失败");
        Check(InjectionLifecyclePolicy.DescribeTerminate(true, InjectionLifecyclePolicy.WaitTimeout).Contains("未确认"),
            "未确认退出时说明文本必须承认未确认");
        Check(InjectionLifecyclePolicy.DescribeTerminate(true, InjectionLifecyclePolicy.WaitObject0).Contains("确认退出"),
            "已确认退出时说明文本才可以说已退出");

        return (passed, failed);
    }
}
