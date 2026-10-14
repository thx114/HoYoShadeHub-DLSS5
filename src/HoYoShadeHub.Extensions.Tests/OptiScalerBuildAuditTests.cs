using HoYoShadeHub.Extensions.Services;

namespace HoYoShadeHub.Extensions.Tests;

/// <summary>
/// 启动体检（<see cref="OptiScalerRuntime.AuditBuild"/>）：按构建目录里生效的 ini 要什么功能，
/// 报缺哪些运行时文件。以前缺 <c>nvngx_dlss.dll</c> / <c>dlssg_sm86.dll</c> 是完全静默的 ——
/// 30 系那台「OptiScaler 文件夹里没有 DLSS 文件却没人说话」就是这条没覆盖到。
/// </summary>
internal static class OptiScalerBuildAuditTests
{
    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "Hoyo-opti-audit-" + Guid.NewGuid().ToString("N"));
        int failed = 0;
        int count = 0;
        void Check(bool ok, string name)
        {
            count++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}");
            if (!ok) failed++;
        }

        static bool Has(IReadOnlyList<OptiScalerRuntime.OptiScalerBuildIssue> issues, string fragment)
            => issues.Any(i => i.Title.Contains(fragment, StringComparison.OrdinalIgnoreCase)
                               || i.ExpectedPath.Contains(fragment, StringComparison.OrdinalIgnoreCase));

        Directory.CreateDirectory(root);
        try
        {
            string Build(string name, string ini)
            {
                string dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, OptiScalerRuntime.ConfigFileName), ini);
                return dir;
            }

            // ① 配置要 DLSS 超分，但构建里没有 nvngx_dlss.dll
            string dirDlss = Build("dlss-missing", "[Upscalers]\nDx11Upscaler = dlss\nDx12Upscaler = auto\n");
            IReadOnlyList<OptiScalerRuntime.OptiScalerBuildIssue> dlssIssues = OptiScalerRuntime.AuditBuild(dirDlss);
            Check(Has(dlssIssues, "nvngx_dlss.dll"), "缺 DLSS 超分运行时报出来");
            Check(dlssIssues.All(i => i.Severity == OptiScalerRuntime.IssueRequired), "缺文件是 Required 级");
            Check(dlssIssues.Any(i => i.ExpectedPath.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase)),
                "期望路径里给出 OptiScaler\\ 下的位置（OptiScaler 只在自己所在目录找）");

            Directory.CreateDirectory(Path.Combine(dirDlss, "OptiScaler"));
            File.WriteAllText(Path.Combine(dirDlss, "OptiScaler", OptiScalerRuntime.DlssSrFileName), "fake");
            Check(!Has(OptiScalerRuntime.AuditBuild(dirDlss), "nvngx_dlss.dll"),
                "nvngx_dlss.dll 放到 OptiScaler\\ 后不再报");

            // ② 30 系：开了 AmpereMfgUnlock 却没有 dlssg_sm86.dll（那台机器的现场）
            string dirAmpere = Build("ampere-sm86-missing",
                "[Upscalers]\nDx12Upscaler = dlss\n[FrameGen]\nFGOutput = DLSSG\n[DLSSG]\nAmpereMfgUnlock = true\nAdaMfgUnlock = false\n");
            IReadOnlyList<OptiScalerRuntime.OptiScalerBuildIssue> ampereIssues = OptiScalerRuntime.AuditBuild(dirAmpere);
            Check(Has(ampereIssues, "dlssg_sm86.dll"), "开 AmpereMfgUnlock 缺 dlssg_sm86.dll 会报");
            Check(ampereIssues.Any(i => i.ExpectedPath.Contains("dlssg_sm86", StringComparison.OrdinalIgnoreCase)),
                "报的是加载器真正会找的位置");
            Check(OptiScalerRuntime.FindSm86Loader(dirAmpere) is null, "FindSm86Loader：没有就返回 null");

            string sm86Dir = Path.Combine(dirAmpere, "OptiScaler", OptiScalerRuntime.Sm86FolderName);
            Directory.CreateDirectory(sm86Dir);
            File.WriteAllText(Path.Combine(sm86Dir, "dlssg_sm86.dll"), "fake");
            Check(!Has(OptiScalerRuntime.AuditBuild(dirAmpere), "dlssg_sm86.dll"),
                "dlssg_sm86.dll 就位（第一条搜索路径）后不再报");
            Check(OptiScalerRuntime.FindSm86Loader(dirAmpere) is not null, "FindSm86Loader 命中正身");

            string dirProxy = Path.Combine(root, "ampere-sm86-proxy");
            Directory.CreateDirectory(Path.Combine(dirProxy, OptiScalerRuntime.Sm86FolderName));
            File.WriteAllText(Path.Combine(dirProxy, OptiScalerRuntime.Sm86FolderName, "version.dll"), "fake");
            Check(OptiScalerRuntime.FindSm86Loader(dirProxy) is not null, "FindSm86Loader 也认上游代理名 version.dll");

            // ③ FGOutput=DLSSG 时，310.9 的 dlssg 和 streamline 也要报
            Check(Has(ampereIssues, "nvngx_dlssg.dll"), "要 DLSSG 但 310.9 dlssg 不齐会报");
            Check(ampereIssues.Any(i => i.Title.Contains("Streamline", StringComparison.OrdinalIgnoreCase)),
                "要 DLSSG 但 streamline 不全会报");

            // ④ 两个解锁同开 → 提醒，不报缺文件
            string dirBoth = Build("both-unlock",
                "[Upscalers]\nDx12Upscaler = auto\n[DLSSG]\nAmpereMfgUnlock = true\nAdaMfgUnlock = true\n");
            IReadOnlyList<OptiScalerRuntime.OptiScalerBuildIssue> bothIssues = OptiScalerRuntime.AuditBuild(dirBoth);
            Check(bothIssues.Any(i => i.Severity == OptiScalerRuntime.IssueNote), "两个解锁同开给一条提醒（互斥）");
            Check(!bothIssues.Any(i => i.Title.Contains("nvngx_dlss.dll")), "超分是 auto 时不误报 DLSS 超分运行时");

            // ⑤ 显卡代次对不上 —— 就是那台 30 系机器的现场：开的是 40 系的解锁，AmpereMfgUnlock 连键都没有
            string dirAdaGpu = Build("ada-gpu-ampere-key",
                "[Upscalers]\nDx12Upscaler = auto\n[DLSSG]\nAmpereMfgUnlock = true\nAdaMfgUnlock = false\n");
            IReadOnlyList<OptiScalerRuntime.OptiScalerBuildIssue> adaGpuIssues =
                OptiScalerRuntime.AuditBuild(dirAdaGpu, "ada");
            Check(adaGpuIssues.Any(i => i.Severity == OptiScalerRuntime.IssueNote && i.Title.Contains("40/50")),
                "40/50 系开 AmpereMfgUnlock → 提醒（不是缺文件）");
            Check(!adaGpuIssues.Any(i => i.Severity == OptiScalerRuntime.IssueRequired && i.Title.Contains("dlssg_sm86")),
                "40/50 系不报缺 dlssg_sm86.dll（那张卡用不上）");
            Check(OptiScalerRuntime.AuditBuild(dirAdaGpu)
                    .Any(i => i.Severity == OptiScalerRuntime.IssueRequired && i.Title.Contains("dlssg_sm86")),
                "显卡代次认不出时仍然保守报缺文件");

            string dir30 = Build("sm86-gpu-ada-key",
                "[Upscalers]\nDx12Upscaler = auto\n[DLSSG]\nAmpereMfgUnlock = true\nAdaMfgUnlock = true\n");
            IReadOnlyList<OptiScalerRuntime.OptiScalerBuildIssue> issues30 =
                OptiScalerRuntime.AuditBuild(dir30, "ampere");
            Check(issues30.Any(i => i.Severity == OptiScalerRuntime.IssueNote && i.Title.Contains("20/30 系")),
                "20/30 系开 AdaMfgUnlock → 提醒它会被自动关掉（白开）");
            Check(issues30.Any(i => i.Severity == OptiScalerRuntime.IssueRequired && i.Title.Contains("dlssg_sm86")),
                "同一份配置在 20/30 系上照样报缺 dlssg_sm86.dll");

            // ⑥ 边界：目录不存在 / 缺主 ini / 什么都不开
            Check(OptiScalerRuntime.AuditBuild(Path.Combine(root, "nope")).Count == 0, "目录不存在返回空而不是抛异常");
            Check(OptiScalerRuntime.AuditBuild(null).Count == 0, "构建目录为 null 返回空");

            string dirNoIni = Path.Combine(root, "no-ini");
            Directory.CreateDirectory(dirNoIni);
            IReadOnlyList<OptiScalerRuntime.OptiScalerBuildIssue> noIniIssues = OptiScalerRuntime.AuditBuild(dirNoIni);
            Check(noIniIssues.Count == 1 && noIniIssues[0].Title.Contains("OptiScaler.ini"),
                "缺主 ini 只报一条（不连带报一堆缺文件）");

            string dirClean = Build("clean",
                "[Upscalers]\nDx12Upscaler = fsr31\n[FrameGen]\nFGOutput = NoFG\n[DLSSG]\nAmpereMfgUnlock = false\nAdaMfgUnlock = false\n");
            Check(OptiScalerRuntime.AuditBuild(dirClean).Count == 0, "什么都不开时不报（干净构建）");

            Console.WriteLine($"opti-build-audit: {count - failed}/{count} passed");

            // 实测入口：HYSX_OPTI_AUDIT_PATH=<某个 opt 构建目录>（可选 HYSX_OPTI_AUDIT_GPU=ampere/ada/…）
            // 时把体检结果原样打出来（排查现场用，不计入通过数）。
            if (Environment.GetEnvironmentVariable("HYSX_OPTI_AUDIT_PATH") is { Length: > 0 } realPath)
            {
                string? realGpu = Environment.GetEnvironmentVariable("HYSX_OPTI_AUDIT_GPU");
                Console.WriteLine($"--- 实测构建目录：{realPath}（显卡代次 {realGpu ?? "未指定"}）");
                IReadOnlyList<OptiScalerRuntime.OptiScalerBuildIssue> real =
                    OptiScalerRuntime.AuditBuild(realPath, realGpu);
                if (real.Count == 0)
                {
                    Console.WriteLine("    （干净：按这份 ini 该有的文件都在）");
                }
                foreach (OptiScalerRuntime.OptiScalerBuildIssue issue in real)
                {
                    Console.WriteLine($"    {issue}");
                }
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 临时目录清理失败不影响结果 */ }
        }

        return failed;
    }
}
