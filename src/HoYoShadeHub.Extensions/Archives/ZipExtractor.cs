using System.Diagnostics;
using System.IO.Compression;

namespace HoYoShadeHub.Extensions.Archives;

/// <summary>
/// 解压 zip 的统一入口，带「别人压的包」兜底。
///
/// <para>
/// .NET 的 <see cref="ZipFile.ExtractToDirectory(string, string, bool)"/> 只认 Store / Deflate。
/// 用户拿 7-Zip、WinRAR 默认参数压出来的包（LZMA / Deflate64 / BZip2 / Zstd）会在真正解压那一刻抛
/// <see cref="InvalidDataException"/>：
/// 「The archive entry was compressed using an unsupported compression method.」
///  「本地安装」插件 / 模块 / OptiScaler 构建就是这么挂的。
/// </para>
///
/// <para>
/// 所以先走 .NET（快、无额外进程），只在抛 <see cref="InvalidDataException"/> 时退到 Windows 自带的
/// <c>tar.exe</c>（libarchive，从 Win10 1803 起随系统分发，能解 Deflate / Deflate64 / BZip2 / LZMA / Zstd）。
/// 其它异常（路径非法、磁盘满）原样抛出，不用兜底把真问题盖掉。
/// </para>
///
/// <para>
/// 这里**故意不引第三方库**：SharpCompress 0.49 起把同步 <c>Open</c> 全部换成了 async 版，
/// 而这条路径是同步调用链，用 <c>tar.exe</c> 既不用赌 API，也不给 Extensions 层加依赖。
/// </para>
/// </summary>
public static class ZipExtractor
{
    private static readonly string TarPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "tar.exe");

    /// <summary>
    /// 解压 <paramref name="archivePath"/> 到 <paramref name="destinationDirectory"/>（覆盖同名文件）。
    /// </summary>
    /// <param name="usedFallback">true = .NET 解不开、走了 tar.exe 兜底</param>
    public static void ExtractToDirectory(string archivePath, string destinationDirectory, out bool usedFallback)
    {
        usedFallback = false;
        Directory.CreateDirectory(destinationDirectory);

        try
        {
            ZipFile.ExtractToDirectory(archivePath, destinationDirectory, overwriteFiles: true);
            return;
        }
        catch (InvalidDataException)
        {
            // 压缩方法不认识（LZMA / Deflate64 / BZip2 / Zstd）：交给 tar.exe 再试一次
        }

        ExtractWithTar(archivePath, destinationDirectory);
        usedFallback = true;
    }

    /// <summary>解压（不关心有没有走兜底）</summary>
    public static void ExtractToDirectory(string archivePath, string destinationDirectory)
        => ExtractToDirectory(archivePath, destinationDirectory, out _);

    private static void ExtractWithTar(string archivePath, string destinationDirectory)
    {
        if (!File.Exists(TarPath))
        {
            throw new FileNotFoundException(
                "这个包用了 .NET 不支持的压缩方法（多半是 7-Zip / WinRAR 的默认压缩），" +
                "而系统里又找不到 tar.exe 来兜底。请把包重新压成标准 zip（Deflate）再装。",
                TarPath);
        }

        var startInfo = new ProcessStartInfo(TarPath)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = destinationDirectory,
        };
        startInfo.ArgumentList.Add("-x");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(Path.GetFullPath(archivePath));
        startInfo.ArgumentList.Add("-C");
        startInfo.ArgumentList.Add(Path.GetFullPath(destinationDirectory));

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("起不了 tar.exe。");
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"tar.exe 也解不开这个包（exit {process.ExitCode}）：{error.Trim()}");
        }
    }
}
