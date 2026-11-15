using System.Runtime.InteropServices;
using System.Text;

namespace PotPlayerBridge.Core;

/// <summary>
/// 把外挂字幕挂到 PotPlayer **当前正在播放的播放列表项**上。
/// <para>
/// 背景：PotPlayer 的 DPL 播放列表没有字幕字段（<c>N*sub*</c> 会被静默忽略），
/// 而命令行 <c>/sub=</c> 只要和播放列表一起用就会被丢弃。所以整季列表播放时，
/// 只能改用 PotPlayer 自己的「加载字幕文件」对话框来挂字幕。
/// </para>
/// <para>
/// 实测可行的做法（本机 PotPlayer + 真实 Jellyfin《绝命毒师》第 1 季验证）：
/// <list type="number">
/// <item>向主窗口发送 <c>WM_COMMAND 10146</c>，打开「加载字幕文件」对话框；</item>
/// <item>对话框是标准 Windows 文件对话框（窗口类 <c>#32770</c>），
///       文件名输入框是 Edit 控件 ID <c>1148</c>，「打开」按钮 ID <c>1</c>；</item>
/// <item>用 <c>WM_SETTEXT</c> 把字幕绝对路径写进输入框，再向对话框发送
///       <c>WM_COMMAND IDOK</c>（或点击按钮），字幕立即挂载。</item>
/// </list>
/// </para>
/// <para>
/// 为什么不用模拟键盘/UI Automation：这是纯窗口消息操作，不抢焦点、不依赖输入法，
/// 也不需要把 PotPlayer 窗口切到前台。代价是对话框会短暂出现（约 0.3～0.5 秒）。
/// </para>
/// <para>
/// 这是 PotPlayer 的非公开接口：如果未来版本改了命令号或控件结构，
/// 这里会返回 <c>false</c> 并记日志，绝不影响正常播放。
/// </para>
/// </summary>
internal static class SubtitleInjector
{
    /// <summary>WM_COMMAND。</summary>
    private const uint WmCommand = 0x0111;

    /// <summary>WM_SETTEXT：给标准 Edit 控件设置文本。</summary>
    private const uint WmSetText = 0x000C;

    /// <summary>
    /// PotPlayer 的「加载字幕文件」菜单命令。
    /// 这是实测值，和 <see cref="PotPlayerWindow.StopCommand"/> 一样属于非公开接口。
    /// </summary>
    private const int LoadSubtitleCommand = 10146;

    /// <summary>标准打开文件对话框里的文件名输入框 ID。</summary>
    private const int FileNameEditId = 1148;

    /// <summary>标准打开文件对话框里的「打开」按钮 ID（IDOK）。</summary>
    private const int OpenButtonId = 1;

    /// <summary>标准 Windows 文件对话框的窗口类名。</summary>
    private const string DialogClassName = "#32770";

    /// <summary>等待对话框出现的上限。PotPlayer 忙时对话框可能晚 1 秒左右。</summary>
    private const int DialogWaitMs = 1600;

    /// <summary>
    /// 尝试给当前播放项加载一条字幕。
    /// 失败只影响字幕本身，播放继续（字幕是增强功能，绝不能把播放流程带崩）。
    /// </summary>
    /// <param name="mainWindow">PotPlayer 主窗口句柄。</param>
    /// <param name="subtitlePath">本地字幕绝对路径。</param>
    /// <param name="log">日志。</param>
    /// <returns>成功把路径提交给对话框返回 true；否则 false。</returns>
    public static bool TryInject(IntPtr mainWindow, string subtitlePath, Logger log)
    {
        if (mainWindow == IntPtr.Zero || !PotPlayerWindow.Exists(mainWindow))
        {
            log.Warn("PotPlayer 主窗口已不存在，跳过字幕加载：" + subtitlePath);
            return false;
        }

        if (string.IsNullOrWhiteSpace(subtitlePath) || !File.Exists(subtitlePath))
        {
            log.Warn("字幕文件不存在，跳过加载：" + subtitlePath);
            return false;
        }

        GetWindowThreadProcessId(mainWindow, out var processId);
        if (processId == 0)
        {
            log.Warn("取不到 PotPlayer 进程 ID，跳过字幕加载。");
            return false;
        }

        // 打开「加载字幕文件」对话框。PostMessage 不等待、不抢焦点。
        PostMessage(mainWindow, WmCommand, (IntPtr)LoadSubtitleCommand, IntPtr.Zero);

        var deadline = Environment.TickCount64 + DialogWaitMs;
        while (Environment.TickCount64 < deadline)
        {
            var dialog = FindDialog((int)processId);
            if (dialog != IntPtr.Zero)
            {
                // 这一版的时序是真实 Jellyfin《绝命毒师》验证成功的：
                // 对话框刚创建时文件名组合框还没初始化完，必须等它稳定后再写。
                // 之前尝试缩短等待/动态轮询，会在控件未就绪时反复写，反而把输入框状态打乱。
                Thread.Sleep(1500);

                var combo = GetDlgItem(dialog, FileNameEditId);
                var button = GetDlgItem(dialog, OpenButtonId);

                if (combo == IntPtr.Zero || button == IntPtr.Zero)
                {
                    // 例如未来 PotPlayer 换成了 DirectUI 对话框：这里只记日志，不尝试更脆弱的键鼠模拟。
                    log.Warn(
                        "PotPlayer 字幕对话框结构不符合预期（combo=" + combo + "，button=" + button + "），" +
                        "本次字幕未加载：" + subtitlePath);
                    return false;
                }

                log.Info("找到 PotPlayer 字幕对话框：" + ReadText(dialog));

                // SetWindowText + WM_SETTEXT 两步都要：
                // 只做一步时实测会出现「对话框关了但文件名是空的」，字幕根本没加载。
                SetWindowText(combo, subtitlePath);
                Thread.Sleep(150);
                SendMessageText(combo, WmSetText, IntPtr.Zero, subtitlePath);
                Thread.Sleep(500);

                // 校验只用于日志；即使跨进程 GetWindowText 读不到，也不再重复写。
                var actual = ReadText(combo);
                if (string.Equals(actual, subtitlePath, StringComparison.Ordinal))
                {
                    log.Info("字幕路径已写入对话框：" + subtitlePath);
                }
                else
                {
                    log.Warn("字幕路径校验不一致（当前='" + actual + "'），仍尝试点击打开：" + subtitlePath);
                }

                PostMessage(dialog, WmCommand, (IntPtr)OpenButtonId, button);

                log.Info("已向 PotPlayer 提交字幕：" + subtitlePath);
                return true;
            }

            Thread.Sleep(80);
        }

        log.Warn("等待 PotPlayer 字幕对话框超时，本次字幕未加载：" + subtitlePath);
        return false;
    }

    /// <summary>
    /// 找属于指定进程的标准文件对话框。
    /// 只按「进程 ID + 窗口类」匹配，不碰用户自己打开的其他窗口。
    /// </summary>
    /// <param name="processId">PotPlayer 进程 ID。</param>
    /// <returns>对话框句柄；没找到返回 <see cref="IntPtr.Zero"/>。</returns>
    private static IntPtr FindDialog(int processId)
    {
        var found = IntPtr.Zero;

        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid != (uint)processId)
            {
                return true;
            }

            var className = new StringBuilder(64);
            GetClassName(hwnd, className, className.Capacity);
            if (string.Equals(className.ToString(), DialogClassName, StringComparison.OrdinalIgnoreCase))
            {
                found = hwnd;
                return false;
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    /// <summary>
    /// 读取窗口/控件文本（用于日志和写入校验）。
    /// </summary>
    private static string ReadText(IntPtr hwnd)
    {
        var buffer = new StringBuilder(512);
        GetWindowText(hwnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // 方法名不能直接用 SendMessage：这里已经有 PostMessage/其它重载，容易看混。
    // 关键是 EntryPoint 必须写 SendMessageW，否则运行时会去找不存在的 SendMessageText 导出函数。
    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageText(IntPtr hWnd, uint msg, IntPtr wParam, string lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowTextW", CharSet = CharSet.Unicode)]
    private static extern bool SetWindowText(IntPtr hWnd, string text);
}
