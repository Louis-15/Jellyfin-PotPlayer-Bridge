using PotPlayerBridge.Core;

namespace PotPlayerBridge;

/// <summary>
/// 运行期状态：版本、端口、启动时间，以及「请打开设置窗口」的跨线程请求标志。
/// 托盘程序的所有 UI 操作必须在 UI 线程执行，这里只用标志位 + WinForms Timer 轮询，
/// 避免从 HTTP 线程直接碰控件。
/// </summary>
public sealed class AgentRuntime
{
    private readonly object _playbackGate = new();
    private volatile bool _openSettingsRequested;

    /// <summary>Gets 程序版本（四段式）。</summary>
    public string Version { get; } = typeof(AgentRuntime).Assembly.GetName().Version?.ToString(4) ?? "1.0.0.0";

    /// <summary>Gets or sets 实际监听端口。</summary>
    public int Port { get; set; }

    /// <summary>Gets 启动时间（UTC），用于 /health 的 uptimeSeconds。</summary>
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Gets 当前播放计划（M5 进度上报使用）。</summary>
    public PlaybackPlan? CurrentPlan { get; private set; }

    /// <summary>Gets 当前由本代理拉起的 PotPlayer 进程 ID。</summary>
    public int? CurrentProcessId { get; private set; }

    /// <summary>Gets 当前 PotPlayer 主窗口句柄。</summary>
    public IntPtr CurrentWindow { get; private set; }

    /// <summary>记录一次成功启动的播放，供后续进度读取/上报使用。</summary>
    /// <param name="plan">播放计划。</param>
    /// <param name="processId">进程 ID。</param>
    /// <param name="window">主窗口句柄。</param>
    public void SetCurrentPlayback(PlaybackPlan plan, int processId, IntPtr window)
    {
        lock (_playbackGate)
        {
            CurrentPlan = plan;
            CurrentProcessId = processId;
            CurrentWindow = window;
        }
    }

    /// <summary>清空当前播放状态（播放器已关闭/播放失败时使用）。</summary>
    public void ClearCurrentPlayback()
    {
        lock (_playbackGate)
        {
            CurrentPlan = null;
            CurrentProcessId = null;
            CurrentWindow = IntPtr.Zero;
        }
    }

    /// <summary>由 HTTP 线程调用：请求 UI 线程打开设置窗口。</summary>
    public void RequestOpenSettings()
    {
        _openSettingsRequested = true;
    }

    /// <summary>由 UI 线程轮询：取走一次「打开设置」请求。</summary>
    /// <returns>有一次请求返回 true。</returns>
    public bool ConsumeOpenSettingsRequest()
    {
        if (!_openSettingsRequested)
        {
            return false;
        }

        _openSettingsRequested = false;
        return true;
    }
}
