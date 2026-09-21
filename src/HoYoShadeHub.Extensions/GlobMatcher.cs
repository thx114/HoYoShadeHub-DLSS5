using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HoYoShadeHub.Extensions;

/// <summary>
/// 轻量 glob 匹配（支持 ** 与 * 与 ?），避免为一个匹配器引入 FileSystemGlobbing 包。
/// 分隔符统一按 / 处理。
/// </summary>
public static partial class GlobMatcher
{
    private static readonly Dictionary<string, Regex> _cache = new(StringComparer.Ordinal);
    private static readonly Lock _lock = new();

    /// <summary>
    /// 把 glob 编译成正则。
    /// <list type="bullet">
    /// <item><c>**/</c> 匹配任意层级（含 0 层）</item>
    /// <item><c>**</c> 匹配任意字符</item>
    /// <item><c>*</c> 匹配单层内任意字符（不含 /）</item>
    /// <item><c>?</c> 匹配单层内单个字符</item>
    /// </list>
    /// </summary>
    public static Regex Compile(string glob)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(glob, out Regex? cached))
            {
                return cached;
            }

            var sb = new StringBuilder("^");
            string pattern = glob.Replace('\\', '/').Trim();
            if (pattern.StartsWith("./", StringComparison.Ordinal))
            {
                pattern = pattern[2..];
            }

            for (int i = 0; i < pattern.Length; i++)
            {
                char c = pattern[i];
                switch (c)
                {
                    case '*':
                        bool isDouble = i + 1 < pattern.Length && pattern[i + 1] == '*';
                        if (isDouble)
                        {
                            i++;
                            bool slashAfter = i + 1 < pattern.Length && pattern[i + 1] == '/';
                            if (slashAfter)
                            {
                                i++;
                                // **/ 可以匹配 0 层目录
                                sb.Append("(?:.*/)?");
                            }
                            else
                            {
                                sb.Append(".*");
                            }
                        }
                        else
                        {
                            sb.Append("[^/]*");
                        }
                        break;

                    case '?':
                        sb.Append("[^/]");
                        break;

                    default:
                        sb.Append(Regex.Escape(c.ToString()));
                        break;
                }
            }

            sb.Append('$');
            var regex = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _cache[glob] = regex;
            return regex;
        }
    }

    public static bool IsMatch(string glob, string relativePath)
    {
        return Compile(glob).IsMatch(relativePath.Replace('\\', '/'));
    }

    /// <summary>按简单的 <c>*</c> / <c>?</c> 通配匹配（不跨层），用于资产名匹配</summary>
    public static bool IsSimpleMatch(string pattern, string text)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return true;
        }
        return IsMatch(pattern, text);
    }
}

/// <summary>
/// 哈希与路径小工具
/// </summary>
public static class HysxUtil
{
    public static async Task<string> ComputeSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    public static bool IsHashEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }
        return string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>安全删除一个空目录（非空则忽略）</summary>
    public static void TryDeleteEmptyDirectory(string directory, string stopAtRoot)
    {
        try
        {
            string root = Path.GetFullPath(stopAtRoot);
            string current = Path.GetFullPath(directory);
            while (current.Length > root.Length + 1
                   && current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(current) || Directory.EnumerateFileSystemEntries(current).Any())
                {
                    return;
                }
                Directory.Delete(current);
                current = Path.GetDirectoryName(current)!;
            }
        }
        catch
        {
            // 清理失败不影响卸载结果
        }
    }

    /// <summary>删除临时目录，忽略一切异常</summary>
    public static void TryDeleteDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // ignore
        }
    }

    public static string? NormalizeRepository(string? repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        string value = repository.Trim();
        if (value.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            value = value["https://github.com/".Length..];
        }
        else if (value.StartsWith("http://github.com/", StringComparison.OrdinalIgnoreCase))
        {
            value = value["http://github.com/".Length..];
        }

        value = value.TrimEnd('/');
        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            value = value[..^4];
        }

        string[] parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 ? $"{parts[0]}/{parts[1]}" : null;
    }
}
