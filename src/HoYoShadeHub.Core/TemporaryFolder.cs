using System;
using System.IO;

namespace HoYoShadeHub.Core;

/// <summary>
/// 临时目录的统一入口。
///
/// <para>
/// 默认就是系统 <c>%TEMP%</c>（和以前直接写 <c>Path.GetTempPath()</c> 一模一样）。
/// 「真便携」模式下（便携根目录里有 <c>.portable</c> 标记）由 AppConfig 把
/// <see cref="Override"/> 设成 <c>&lt;便携根&gt;\.cache\temp</c>：
/// 下载中的 zip、解压中间文件、汉化 / 更新 / dll 备份就都不再落到 C: 盘上，
/// 也不会**读到** C: 盘上残留的同名中间文件（比如 <c>%TEMP%\hysx-catalog-names.json</c>）。
/// </para>
///
/// <para>
/// 放在 Core 里是因为 Extensions / RPC / 主程序三层都要用它，而三层都引用 Core。
/// </para>
/// </summary>
public static class TemporaryFolder
{
    private static string? _override;

    private static bool _prepared;

    /// <summary>
    /// 非空时覆盖系统 <c>%TEMP%</c>。只由 AppConfig 在「真便携」模式下设置；
    /// 传 null / 空白就回到系统临时目录。
    /// </summary>
    public static string? Override
    {
        get => _override;
        set
        {
            _override = string.IsNullOrWhiteSpace(value) ? null : value;
            _prepared = false;
        }
    }

    /// <summary>
    /// 当前可用的临时目录，**一定以目录分隔符结尾**（和 <see cref="System.IO.Path.GetTempPath"/> 行为一致，
    /// 调用方照旧写 <c>Path.Combine(TemporaryFolder.Path, "x")</c>）。
    /// </summary>
    public static string Path
    {
        get
        {
            string? folder = _override;
            if (string.IsNullOrWhiteSpace(folder))
            {
                return System.IO.Path.GetTempPath();
            }

            if (!_prepared)
            {
                try
                {
                    Directory.CreateDirectory(folder);
                    _prepared = true;
                }
                catch
                {
                    // 便携目录写不进去（只读盘 / 没权限）就退回系统临时目录 —— 总比抛异常强
                    _override = null;
                    return System.IO.Path.GetTempPath();
                }
            }

            return folder.EndsWith(System.IO.Path.DirectorySeparatorChar)
                ? folder
                : folder + System.IO.Path.DirectorySeparatorChar;
        }
    }
}
