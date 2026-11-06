using System.Runtime.InteropServices;
using System.Text;

namespace PotPlayerBridge.Core;

/// <summary>
/// PotPlayer 窗口操作（窗口消息是本项目与播放器交互的主通道）。
/// M4 只用到「找主窗口 / 停止 / 优雅关闭」，进度读取与跳转在 M5/M6 扩展。
/// </summary>
internal static class PotPlayerWindow
{
    /// <summary>WM_COMMAND。</summary>
    public const uint WmCommand = 0x0111;

    /// <summary>WM_CLOSE。</summary>
    public const uint WmClose = 0x0010;

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
