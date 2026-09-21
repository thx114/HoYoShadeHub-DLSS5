using System.Diagnostics;
using System.Text;
using HoYoShadeHub.Extensions.Models;

namespace HoYoShadeHub.Extensions.ReShade;

/// <summary>一次 INIBuild 的结果</summary>
public sealed record IniBuildResult(bool Ran, int ExitCode, string? Output, string? Error, string? FailureReason)
{
    /// <summary>跑了而且没出错</summary>
    public bool Ok => Ran && FailureReason is null;

    public static IniBuildResult Skipped(string reason) => new(false, 0, null, null, reason);
}

/// <summary>
/// 跑 HoYoShade 自带的 <c>LauncherResource\INIBuild.exe</c> —— 生成/修补 <c>ReShade.ini</c>。
///
/// <para>
/// 启动器 bat 里的「额外步骤」一共两个：提权 + 跑一次 INIBuild。我们**不提权**（见 GAMES-AND-INJECT.md §4.3），
/// 所以这里只做第二件事。
/// </para>
///
/// <para>
/// **实测结论**（在隔离副本上跑真 exe 验证过）：
/// </para>
/// <list type="number">
/// <item>它会写**自己所在目录的上一级**（= HoYoShade 根目录）的 <c>ReShade.ini</c>，
/// <b>和工作目录无关</b> —— 把 exe 放在 A 的 LauncherResource 里、cwd 设成 B，ini 仍然落在 A；</item>
/// <item>写出来的是**模板**：里面全是绝对路径（EffectSearchPaths / PresetPath / AddonPath…）；
/// </item>
/// <item><c>DisabledAddons</c> 会把 AddonPath 目录里**所有** addon 都列进去（<c>Slug@文件名</c>），
/// 除了 <c>LauncherResource\AddonWhitelist.txt</c> 里点名的那些 —— 也就是说新装的 addon 默认是关的；</item>
/// <item>它不动 <c>[STYLE]</c> / <c>[OVERLAY]</c> 之类，那些键本来就是我们自己的 <see cref="IniDocument"/> 负责保住的。</item>
/// </list>
///
/// <para>
/// 游戏目录里的那份 ini 是 <c>inject.exe</c> 从模板复制过去的（bat 的原话：「注入器会自动检测并复制
/// 配置文件（ReShade.ini）到游戏进程根目录」）。所以我们只需要在**游戏 ini 不存在**时补一次模板。
/// </para>
/// </summary>
public static class ReShadeIniBuilder
{
    public const string LauncherResourceFolderName = "LauncherResource";
    public const string ExeName = "INIBuild.exe";

    /// <summary>INIBuild.exe 全路径</summary>
    public static string GetExePath(string shadeRoot) =>
        Path.Combine(shadeRoot, LauncherResourceFolderName, ExeName);

    public static string GetExePath(ShadeHost host) => GetExePath(host.RootPath);

    public static bool IsAvailable(ShadeHost host) => File.Exists(GetExePath(host));

    /// <summary>
    /// 游戏目录里没 ini 就跑一次 INIBuild 补模板。
    /// </summary>
    /// <returns>跑过 / 失败的结果；<b>null 表示目标 ini 已经在了，什么都没做</b></returns>
    public static async Task<IniBuildResult?> EnsureAsync(
        ShadeHost host,
        string? gameIniPath,
        int timeoutMs = 120_000,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(gameIniPath) && File.Exists(gameIniPath))
        {
            return null;
        }

        return await RunAsync(host, timeoutMs, cancellationToken);
    }

    /// <summary>
    /// 跑一次 INIBuild 并等它退出（对应 bat 里的 <c>:ini_Reset</c>，界面上也可以手动触发）。
    /// </summary>
    public static async Task<IniBuildResult> RunAsync(
        ShadeHost host,
        int timeoutMs = 120_000,
        CancellationToken cancellationToken = default)
    {
        string exePath = GetExePath(host);
        if (!File.Exists(exePath))
        {
            return IniBuildResult.Skipped($"没有找到 {LauncherResourceFolderName}\\{ExeName}：{exePath}");
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = host.RootPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            using Process? process = Process.Start(startInfo);
            if (process is null)
            {
                return new IniBuildResult(true, -1, null, null, "INIBuild.exe 起不来。");
            }

            Task<string> stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            Task<string> stderr = process.StandardError.ReadToEndAsync(cancellationToken);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(timeoutMs);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignore
                }

                return new IniBuildResult(true, -1, null, null, $"INIBuild.exe 超过 {timeoutMs / 1000} 秒没退出，已结束它。");
            }

            string output = await SafeResultAsync(stdout);
            string error = await SafeResultAsync(stderr);

            if (process.ExitCode != 0)
            {
                return new IniBuildResult(true, process.ExitCode, output, error, $"INIBuild.exe 退出码 {process.ExitCode}。");
            }

            if (!File.Exists(host.ReShadeIniPath))
            {
                return new IniBuildResult(true, 0, output, error, "INIBuild.exe 正常退出，但没有生成 ReShade.ini。");
            }

            return new IniBuildResult(true, 0, output, error, null);
        }
        catch (Exception ex)
        {
            return new IniBuildResult(true, -1, null, null, "跑 INIBuild.exe 出错：" + ex.Message);
        }
    }

    private static async Task<string> SafeResultAsync(Task<string> task)
    {
        try
        {
            return await task;
        }
        catch
        {
            return string.Empty;
        }
    }
}
