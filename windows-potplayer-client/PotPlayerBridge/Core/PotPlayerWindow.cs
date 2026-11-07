using System.Runtime.InteropServices;
using System.Text;

namespace PotPlayerBridge.Core;

/// <summary>
/// PotPlayer 窗口操作（窗口消息是本项目与播放器交互的主通道）。
/// 控制接口来自 PotPlayer 的 Remote Control SDK：<c>SendMessage(hwnd, WM_USER, 命令号, 参数)</c>。
/// </summary>
internal static class PotPlayerWindow
{
    /// <summary>WM_COMMAND。</summary>
    public const uint WmCommand = 0x0111;

    /// <summary>WM_CLOSE。</summary>
    public const uint WmClose = 0x0010;

    /// <summary>WM_USER：PotPlayer 控制命令的消息号。</summary>
    public const uint WmUser = 0x0400;

    /// <summary>取总时长（毫秒）。</summary>
    public const int CmdGetTotalTime = 0x5002;

    /// <summary>取当前播放位置（毫秒）。</summary>
    public const int CmdGetCurrentTime = 0x5004;

    /// <summary>设置当前播放位置（毫秒），M6 片头跳跃用。</summary>
    public const int CmdSetCurrentTime = 0x5005;

    /// <summary>取播放状态：-1 已停止/未加载，0 空闲/播完，1 暂停，2 正在播放。</summary>
    public const int CmdGetPlayStatus = 0x5006;

    /// <summary>PotPlayer 的「下一集」菜单命令（M6 片尾跳转用）。</summary>
    public const int NextCommand = 10124;

    /// <summary>PotPlayer 的「停止」命令 ID（实测有效，仅停止播放，不退出进程）。</summary>
    public const int StopCommand = 20002;

    /// <summary>PotPlayer 主窗口类名（Mini 版同样是这个类名）。</summary>
    public const string WindowClassName = "PotPlayer64";

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// 按 PID 找 PotPlayer 主窗口。
    /// MainWindowHandle 有时是 0（窗口还没创建完/被其它窗口抢走），所以必须用 EnumWindows + 类名兜底。
    /// </summary>
    /// <param name="processId">进程 ID。</param>
    /// <returns>窗口句柄；找不到返回 IntPtr.Zero。</returns>
    public static IntPtr FindMainWindow(int processId)
    {
        var found = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != (uint)processId || !IsWindowVisible(hwnd))
            {
                return true;
            }

            if (string.Equals(GetClass(hwnd), WindowClassName, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }

            // 极端情况下类名取不到，但标题不为空的可见窗口也可以先用。
            if (found == IntPtr.Zero && !string.IsNullOrWhiteSpace(GetTitle(hwnd)))
            {
                found = hwnd;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>窗口是否仍然存在。</summary>
    /// <param name="hwnd">句柄。</param>
    /// <returns>存在返回 true。</returns>
    public static bool Exists(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    /// <summary>取窗口标题（用于辅助判断当前播放的文件）。</summary>
    /// <param name="hwnd">句柄。</param>
    /// <returns>标题。</returns>
    public static string GetTitle(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    /// <summary>发送「停止播放」命令。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    public static void SendStop(IntPtr hwnd)
    {
        SendMessage(hwnd, WmCommand, (IntPtr)StopCommand, IntPtr.Zero);
    }

    /// <summary>发送「下一集」命令（M6 片尾跳转用）。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    public static void SendNext(IntPtr hwnd)
    {
        SendMessage(hwnd, WmCommand, (IntPtr)NextCommand, IntPtr.Zero);
    }

    /// <summary>取总时长（毫秒）。读不到时返回 0。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <returns>毫秒。</returns>
    public static long GetTotalTimeMs(IntPtr hwnd)
    {
        return Math.Max(0, Query(hwnd, CmdGetTotalTime));
    }

    /// <summary>取当前播放位置（毫秒）。未加载媒体时可能返回 0。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <returns>毫秒。</returns>
    public static long GetCurrentTimeMs(IntPtr hwnd)
    {
        return Math.Max(0, Query(hwnd, CmdGetCurrentTime));
    }

    /// <summary>取播放状态：-1 停止/未加载，0 空闲/播完，1 暂停，2 播放中。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <returns>状态码。</returns>
    public static int GetPlayStatus(IntPtr hwnd)
    {
        return (int)Query(hwnd, CmdGetPlayStatus);
    }

    /// <summary>把播放位置跳到指定毫秒（M6 片头跳过用）。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="positionMs">目标位置。</param>
    public static void SetCurrentTimeMs(IntPtr hwnd, long positionMs)
    {
        SendMessage(hwnd, WmUser, (IntPtr)CmdSetCurrentTime, (IntPtr)positionMs);
    }

    private static long Query(IntPtr hwnd, int command)
    {
        if (hwnd == IntPtr.Zero)
        {
            return 0;
        }

        try
        {
            return SendMessage(hwnd, WmUser, (IntPtr)command, IntPtr.Zero).ToInt64();
        }
        catch
        {
            // 窗口刚好被销毁时 SendMessage 可能抛异常，按「读不到」处理。
            return 0;
        }
    }

    /// <summary>请求关闭窗口（优雅退出）。</summary>
    /// <param name="hwnd">窗口句柄。</param>
    public static void PostClose(IntPtr hwnd)
    {
        PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
    }

    private static string GetClass(IntPtr hwnd)
    {
        var buffer = new StringBuilder(256);
        GetClassName(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }
}
