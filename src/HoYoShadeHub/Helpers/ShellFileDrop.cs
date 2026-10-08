using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vanara.PInvoke;

namespace HoYoShadeHub.Helpers;

/// <summary>
/// 能接住资源管理器拖进来的文件的页面实现它，并在加载 / 卸载时向
/// <see cref="ShellFileDrop"/> 注册（窗口收到 <c>WM_DROPFILES</c> 后按窗口找它）。
/// </summary>
public interface IShellFileDropTarget
{
    /// <summary>文件落在窗口上了（绝对路径，至少一个；在 UI 线程上调用）。</summary>
    Task OnShellFilesDroppedAsync(IReadOnlyList<string> paths);
}



/// <summary>
/// 提权窗口收资源管理器拖放文件的兜底通道：shell 的 <c>WM_DROPFILES</c>。
///
/// <para>
/// 为什么不用 WinUI 那套（<c>AllowDrop</c> + <c>DragOver</c> / <c>Drop</c>）：Hub 是全程提权的
/// （见 <c>Program.Main</c> 的 runas 自我重启），而 WinUI 的拖放是 OLE —— 资源管理器（中完整性）
/// 的 <c>IDropTarget</c> 调用进提权窗口会被 UIPI 拦掉，所以管理员模式下那些事件永远不触发
/// （游戏列表的拖动重排就是这么废掉的，见 GameSelector 里的「左移 / 右移」）。
/// </para>
///
/// <para>
/// <c>WM_DROPFILES</c> 是另一条路：<c>ChangeWindowMessageFilterEx</c> 给窗口放行
/// <c>WM_DROPFILES</c> / <c>WM_COPYGLOBALDATA</c>(0x0049) / <c>WM_COPYDATA</c>
/// （高完整性进程默认拒收低完整性进程发来的这几个消息），再 <c>DragAcceptFiles</c> 登记成拖放目标。
/// </para>
///
/// <para>
/// <b>关键坑</b>：窗口上只要存在 OLE 拖放目标（WinUI 见到任何 <c>AllowDrop="True"</c> 的元素就会
/// 给窗口 <c>RegisterDragDrop</c>），shell 就只走 OLE、<b>不再回落</b> <c>WM_DROPFILES</c>；
/// 而 OLE 在提权下又必然被 UIPI 拦掉 —— 表现就是"拖上去毫无反应"。所以这里在挂通道之后
/// 还要把那个 OLE 目标撤掉（<c>RevokeDragDrop</c>），并且在页面加载 / 窗口激活时补刀，
/// 因为 WinUI 是 XAML 加载时才注册的，比构造函数里的这次撤销晚。
/// </para>
///
/// <para>
/// 代价（相对 OLE 拖放）：只能拿到路径，拿不到 StorageItems；**没有拖动过程中的 DragOver**，
/// 所以悬浮高亮、自定义光标提示（「松开以本地安装」那种）都得省掉，只能在松手后处理。
/// </para>
/// </summary>
public static partial class ShellFileDrop
{
    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COPYGLOBALDATA = 0x0049;
    private const uint WM_ACTIVATE = 0x0006;
    private const uint MsgFilterAllow = 1;

    /// <summary>子窗口树的展开上限（WinUI 那棵很浅，给点余量）</summary>
    private const int MaxDepth = 4;
    private const int MaxWindows = 64;

    private static readonly ILogger Logger = AppConfig.GetLogger<Log>();

    private static readonly ComCtl32.SUBCLASSPROC SubclassDelegate = SubclassProc;

    private static readonly nuint SubclassId = 0x5D10;

    /// <summary>已经登记过拖放目标的窗口（幂等）。</summary>
    private static readonly HashSet<IntPtr> Enabled = [];

    /// <summary>已经子类化过的窗口（子类化保活，别重复挂）。</summary>
    private static readonly HashSet<IntPtr> Subclassed = [];

    /// <summary>窗口 → 接拖放的页面（只认顶层窗口句柄）。</summary>
    private static readonly Dictionary<IntPtr, IShellFileDropTarget> Targets = [];

    /// <summary>只为拿 ILogger 用（静态类不能当泛型参数）。</summary>
    private sealed class Log { }



    /// <summary>给窗口和它下面所有子窗口挂上拖放目标（幂等，可以重复调）。</summary>
    public static void EnableTree(IntPtr topLevel)
    {
        if (topLevel == IntPtr.Zero)
        {
            return;
        }

        int count = 0;
        Enable(topLevel);
        count++;
        WalkChildren(topLevel, 1, ref count);

        Logger.LogInformation("Shell 拖放入口就绪：顶层 0x{Top:X} 一共挂了 {Count} 个窗口", topLevel.ToInt64(), count);
    }



    /// <summary>让这一个窗口能收 shell 拖放（幂等）。</summary>
    public static void Enable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Enabled.Add(hwnd))
        {
            return;
        }

        foreach (uint message in new[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
        {
            if (!ChangeWindowMessageFilterEx(hwnd, message, MsgFilterAllow, IntPtr.Zero))
            {
                Logger.LogWarning("放行 {Message} 失败（hwnd=0x{Hwnd:X}，err={Error}）",
                    $"0x{message:X4}", hwnd.ToInt64(), Marshal.GetLastWin32Error());
            }
        }

        DragAcceptFiles(hwnd, true);

        if (Subclassed.Add(hwnd))
        {
            ComCtl32.SetWindowSubclass(hwnd, SubclassDelegate, SubclassId, IntPtr.Zero);
        }

        Logger.LogInformation("Shell 拖放目标已挂上（hwnd=0x{Hwnd:X} 类名={Class}）", hwnd.ToInt64(), ClassNameOf(hwnd));
        RevokeOleDropTarget(hwnd);
    }



    /// <summary>页面加载时登记：这个窗口的拖入交给它。</summary>
    public static void Register(IntPtr topLevelHwnd, IShellFileDropTarget target)
    {
        if (topLevelHwnd == IntPtr.Zero)
        {
            return;
        }

        Targets[topLevelHwnd] = target;
        Logger.LogInformation("拖入目标 = {Target}（hwnd=0x{Hwnd:X}）", target.GetType().Name, topLevelHwnd.ToInt64());

        // 页面刚加载完，WinUI 很可能就是这时候注册 OLE 目标的 —— 补一刀
        EnableTree(topLevelHwnd);
        RevokeOleDropTarget(topLevelHwnd);
    }



    /// <summary>页面卸载时注销（只在这条登记还是自己时才摘，免得把新页面的顶掉）。</summary>
    public static void Unregister(IntPtr topLevelHwnd, IShellFileDropTarget target)
    {
        if (topLevelHwnd != IntPtr.Zero && Targets.TryGetValue(topLevelHwnd, out IShellFileDropTarget? current)
            && ReferenceEquals(current, target))
        {
            Targets.Remove(topLevelHwnd);
            Logger.LogInformation("拖入目标已注销（hwnd=0x{Hwnd:X}）", topLevelHwnd.ToInt64());
        }
    }



    /// <summary>是 <c>WM_DROPFILES</c> 就取路径转给当前页面（窗口过程里调）。</summary>
    public static bool TryHandle(IntPtr hwnd, uint msg, IntPtr wParam)
    {
        if (msg != WM_DROPFILES)
        {
            return false;
        }

        // HDROP 只在处理这条消息期间有效：路径必须在这里同步读完
        List<string> paths = ReadPaths(wParam);

        IShellFileDropTarget? target = Resolve(hwnd);
        if (target is null)
        {
            // 排查「拖了没反应」的关键一条：说明 WM_DROPFILES 到了，只是当前页面不吃
            Logger.LogInformation("收到拖入（{Count} 项，首个 {First}），但当前页面不接拖放",
                paths.Count, paths.FirstOrDefault() ?? "(空)");
            return true;
        }

        Logger.LogInformation("收到拖入：{Count} 项，首个 {First} → {Target}",
            paths.Count, paths.FirstOrDefault() ?? "(空)", target.GetType().Name);

        if (paths.Count == 0)
        {
            return true;
        }

        // 窗口过程里不能干重活（会卡住整个窗口消息），丢给 UI 队列
        DispatcherQueue? queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            _ = target.OnShellFilesDroppedAsync(paths);
        }
        else
        {
            queue.TryEnqueue(() => _ = target.OnShellFilesDroppedAsync(paths));
        }

        return true;
    }



    /// <summary>所有挂上的窗口共用的子类化过程。</summary>
    private static IntPtr SubclassProc(HWND hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, nuint uIdSubclass, IntPtr dwRefData)
    {
        try
        {
            if (uMsg == WM_ACTIVATE && wParam is 0x1 or 0x2)
            {
                // 窗口被激活时补一刀：WinUI 换页 / 新建 AllowDrop 元素时可能又把 OLE 目标注册回来
                RevokeOleDropTarget(hWnd.DangerousGetHandle());
            }

            if (TryHandle(hWnd.DangerousGetHandle(), uMsg, wParam))
            {
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Shell 拖放窗口过程出错（hwnd=0x{Hwnd:X} msg=0x{Msg:X}）",
                hWnd.DangerousGetHandle().ToInt64(), uMsg);
        }

        return ComCtl32.DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }



    /// <summary>
    /// 撤掉窗口上的 OLE 拖放目标：有它在，shell 就不会把 <c>WM_DROPFILES</c> 发过来。
    /// 提权下 OLE 那条路本来也不通，所以这个交换是稳赚的。
    /// </summary>
    private static void RevokeOleDropTarget(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || GetProp(hwnd, OleDropTargetProperty) == IntPtr.Zero)
        {
            return;
        }

        int hr = RevokeDragDrop(hwnd);
        Logger.LogInformation("撤掉 OLE 拖放目标（hwnd=0x{Hwnd:X}）→ 0x{Hr:X8}{Tip}",
            hwnd.ToInt64(), hr, hr == 0 ? string.Empty : "（没成功，shell 可能还是不回落 WM_DROPFILES）");
    }



    /// <summary>从收到消息的窗口往上找：自己或某个父窗口登记过就用它。</summary>
    private static IShellFileDropTarget? Resolve(IntPtr hwnd)
    {
        IntPtr current = hwnd;
        for (int depth = 0; current != IntPtr.Zero && depth < 8; depth++)
        {
            if (Targets.TryGetValue(current, out IShellFileDropTarget? target))
            {
                return target;
            }

            current = GetParent(current);
        }

        return null;
    }



    /// <summary>把子窗口树里的窗口都纳进来并打日志（万一 shell 投给了更深的子窗口）。</summary>
    private static void WalkChildren(IntPtr parent, int depth, ref int count)
    {
        if (depth > MaxDepth || count >= MaxWindows)
        {
            return;
        }

        for (IntPtr child = FindWindowEx(parent, IntPtr.Zero, null, null); child != IntPtr.Zero; child = FindWindowEx(parent, child, null, null))
        {
            if (count >= MaxWindows)
            {
                return;
            }

            Enable(child);
            count++;
            Logger.LogInformation("子窗口 {Depth} 层：0x{Hwnd:X} 类名={Class}", depth, child.ToInt64(), ClassNameOf(child));
            WalkChildren(child, depth + 1, ref count);
        }
    }



    private static unsafe string ClassNameOf(IntPtr hwnd)
    {
        char[] buffer = new char[256];
        fixed (char* p = buffer)
        {
            int length = GetClassName(hwnd, p, buffer.Length);
            return length > 0 ? new string(buffer, 0, length) : "(取不到)";
        }
    }



    /// <summary>读 HDROP 里的路径并释放它。</summary>
    private static unsafe List<string> ReadPaths(IntPtr hDrop)
    {
        List<string> paths = [];

        try
        {
            uint count = DragQueryFile(hDrop, uint.MaxValue, null, 0);
            for (uint i = 0; i < count; i++)
            {
                uint length = DragQueryFile(hDrop, i, null, 0);
                if (length == 0)
                {
                    continue;
                }

                char[] buffer = new char[length + 1];
                fixed (char* p = buffer)
                {
                    uint copied = DragQueryFile(hDrop, i, p, (uint)buffer.Length);
                    if (copied > 0)
                    {
                        paths.Add(new string(buffer, 0, (int)copied));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "读拖入路径失败");
        }
        finally
        {
            DragFinish(hDrop);
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }



    #region Win32

    private const string OleDropTargetProperty = "OleDropTargetInterface";

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint message, uint action, IntPtr changeFilterStruct);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetParent(IntPtr hWnd);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(IntPtr hWnd, char* className, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetPropW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr GetProp(IntPtr hWnd, string property);

    [LibraryImport("shell32.dll")]
    private static partial void DragAcceptFiles(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool accept);

    [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")]
    private static unsafe partial uint DragQueryFile(IntPtr hDrop, uint fileIndex, char* fileName, uint fileNameSize);

    [LibraryImport("shell32.dll")]
    private static partial void DragFinish(IntPtr hDrop);

    [LibraryImport("ole32.dll")]
    private static partial int RevokeDragDrop(IntPtr hwnd);

    #endregion
}
