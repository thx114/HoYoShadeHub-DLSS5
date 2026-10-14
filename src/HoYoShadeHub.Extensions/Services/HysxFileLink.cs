using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace HoYoShadeHub.Extensions.Services;

/// <summary>
/// 「同盘硬链接、跨盘复制」的文件搬运。
///
/// <para>
/// 版本归档（<see cref="AddonVersionStore"/>）和「每游戏插件包」（<c>GameAddonPack</c>）都靠它。
/// 硬链接几乎不占额外空间，而且删掉其中一份不会影响另一份的内容 —— 对「缓存一份旧版本」
/// 这个场景正合适。
/// </para>
/// </summary>
public static class HysxFileLink
{
    [DllImport("Kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);

    /// <summary>两个路径是不是落在同一个卷上（硬链接的前提）</summary>
    public static bool SameVolume(string a, string b)
    {
        try
        {
            string? rootA = Path.GetPathRoot(Path.GetFullPath(a));
            string? rootB = Path.GetPathRoot(Path.GetFullPath(b));
            return !string.IsNullOrEmpty(rootA)
                   && string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把 <paramref name="source"/> 链接/复制到 <paramref name="target"/>。
    /// 先准备临时链接/副本，再替换目标（调用方负责判断内容一样就跳过）。
    /// </summary>
    /// <returns>true = 硬链接，false = 复制</returns>
    public static bool LinkOrCopy(string source, string target)
    {
        string targetFull = Path.GetFullPath(target);
        Directory.CreateDirectory(Path.GetDirectoryName(targetFull)!);

        if (string.Equals(Path.GetFullPath(source), targetFull, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string staged = targetFull + ".hysx-" + Guid.NewGuid().ToString("N");
        try
        {
            bool linked = SameVolume(source, staged) && CreateHardLinkW(staged, Path.GetFullPath(source), IntPtr.Zero);
            if (!linked) File.Copy(source, staged, overwrite: false);
            File.Move(staged, targetFull, overwrite: true);
            return linked;
        }
        finally
        {
            if (File.Exists(staged)) File.Delete(staged);
        }
    }

    /// <summary>
    /// 目标是不是已经是最新的：大小一样、且源不比目标新。
    /// 和 <see cref="SizeEquals"/> 的区别：源被 tmp+Move 替换过之后 mtime 会更新，
    /// 只看大小会把「同名同大小但内容换了」的硬链接留下来。
    /// </summary>
    public static bool IsUpToDate(string source, string target)
    {
        try
        {
            if (!File.Exists(source) || !File.Exists(target))
            {
                return false;
            }

            var sourceInfo = new FileInfo(source);
            var targetInfo = new FileInfo(target);

            if (sourceInfo.Length != targetInfo.Length)
            {
                return false;
            }

            string ext = Path.GetExtension(source);
            if (ext.Equals(".addon64", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".addon64x", StringComparison.OrdinalIgnoreCase)
                || IsSmallConfig(source))
            {
                using var a = File.OpenRead(source);
                using var b = File.OpenRead(target);
                return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
            }
            return sourceInfo.LastWriteTimeUtc <= targetInfo.LastWriteTimeUtc.AddSeconds(1);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 「小配置」按内容比，不按 mtime 比：大小一样但内容换了（同长度）必须算过期。
    ///
    /// <para>
    /// 覆盖包的插件配置是一族文件：基名 <c>FsrBridgeDepthAddon.ini</c>、游戏专属
    /// <c>FsrBridgeDepthAddon.hk4e.ini</c> / <c>FsrBridgeDepthAddon-nap.ini</c>。
    /// 插件在原神读的是 hk4e 那份（`resolve_controller_ini`），只给基名开内容比较会让
    /// 同族文件规则不一致 —— 所以整族都按内容比。
    /// </para>
    /// </summary>
    private static bool IsSmallConfig(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("FsrBridgeDepthAddon", StringComparison.OrdinalIgnoreCase)
               && name.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>两个文件都存在且大小一样（判断「不用重链」的廉价近似）</summary>
    public static bool SizeEquals(string a, string b)
    {
        try
        {
            return File.Exists(a) && File.Exists(b) && new FileInfo(a).Length == new FileInfo(b).Length;
        }
        catch
        {
            return false;
        }
    }
}
