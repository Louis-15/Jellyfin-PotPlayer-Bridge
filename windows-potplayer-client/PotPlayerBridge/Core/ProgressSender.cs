using System.Threading.Channels;

namespace PotPlayerBridge.Core;

/// <summary>
/// 进度上报通道：把 <see cref="PlaybackSignal"/> 变成请求，串行发到服务器。
/// <para>
/// 单独成类的原因：上报是「网络慢也不许拖住采样」的那一半逻辑，
/// 和「读播放器、判断状态」完全无关。拆开后两边都能单独看明白。
/// </para>
/// <para>
/// 三条约束：
/// <list type="bullet">
/// <item>有界队列且丢最旧的中间进度：网络长时间不通时不能把内存吃光；</item>
/// <item>停止时的最后一条上报必须发完（有 10 秒上限），这是用户真正在意的那个位置；</item>
/// <item>失败重试交给 <see cref="JellyfinClient"/>，这里只负责顺序和生命周期。</item>
/// </list>
/// </para>
/// </summary>
internal sealed class ProgressSender : IAsyncDisposable
{
    private readonly PlaybackPlan _plan;
    private readonly string _serverUrl;
    private readonly string _accessToken;
    private readonly JellyfinClient _jellyfin;
    private readonly Logger _log;

    private readonly Channel<ProgressReport> _queue;
    private readonly CancellationTokenSource _cts = new();

    private Task? _sender;
    private int _flushed;

    /// <summary>Initializes a new instance of the <see cref="ProgressSender"/> class.</summary>
    /// <param name="plan">播放计划。</param>
    /// <param name="serverUrl">服务器地址。</param>
    /// <param name="accessToken">访问令牌（只在内存中使用）。</param>
    /// <param name="jellyfin">服务器调用。</param>
    /// <param name="log">日志。</param>
    public ProgressSender(
        PlaybackPlan plan,
        string serverUrl,
        string accessToken,
        JellyfinClient jellyfin,
        Logger log)
    {
        _plan = plan;
        _serverUrl = serverUrl;
        _accessToken = accessToken;
        _jellyfin = jellyfin;
        _log = log;

        _queue = Channel.CreateBounded<ProgressReport>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
    }

    /// <summary>启动发送循环（幂等）。</summary>
    public void Start()
    {
        _sender ??= Task.Run(() => SendLoopAsync(_cts.Token));
    }

    /// <summary>把一次状态快照排进队列。</summary>
    /// <param name="signal">状态快照。</param>
    public void Enqueue(PlaybackSignal signal)
    {
        if (string.IsNullOrEmpty(signal.ItemId))
        {
            return;
        }

        var report = new ProgressReport
        {
            PlaySessionId = _plan.PlaySessionId,
            ItemId = signal.ItemId,
            PositionTicks = signal.PositionMs * TimeSpan.TicksPerMillisecond,
            DurationTicks = signal.DurationMs * TimeSpan.TicksPerMillisecond,
            IsPaused = signal.IsPaused,
            Stopped = signal.Stopped,
            Armed = signal.Armed,
            Seeked = signal.Seeked,
            Ended = signal.Ended,
            EpisodeStart = signal.EpisodeStart
        };

        if (!_queue.Writer.TryWrite(report))
        {
            _log.Warn("进度上报队列已满，丢弃一条中间进度。");
        }
    }

    /// <summary>
    /// 发送最后一条上报并等待它完成。幂等：采样循环自己发现播放器关闭、
    /// 以及外面主动停止监控，都会调用这里。
    /// </summary>
    /// <param name="finalSignal">最后状态快照。</param>
    /// <returns>异步任务。</returns>
    public async Task FlushAsync(PlaybackSignal finalSignal)
    {
        if (Interlocked.Exchange(ref _flushed, 1) == 1)
        {
            return;
        }

        if (finalSignal.PositionMs > 0 || finalSignal.Ended)
        {
            Enqueue(finalSignal);
        }

        // 关闭写端，读端会把队列里剩下的读完再结束。
        _queue.Writer.TryComplete();

        if (_sender is not null)
        {
            try
            {
                await Task.WhenAny(_sender, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 忽略：退出流程不能被网络拖住。
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();

        if (_sender is not null)
        {
            try
            {
                await Task.WhenAny(_sender, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 忽略。
            }
        }

        _cts.Dispose();
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var report in _queue.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var result = await _jellyfin
                    .ReportProgressAsync(_serverUrl, _accessToken, report, cancellationToken)
                    .ConfigureAwait(false);

                if (result is null)
                {
                    continue;
                }

                if (!result.Accepted)
                {
                    _log.Info("服务器未采纳本次进度（" + result.Reason + "），属保护性拒绝。");
                    continue;
                }

                // 每 5 秒一条的常规上报纸到日志里会淹没真正有用的信息，只记「有意义的时刻」。
                if (report.Stopped || report.Seeked || result.Reason == "completed")
                {
                    _log.Info(
                        "进度已写入：" + FormatMs(report.PositionTicks / TimeSpan.TicksPerMillisecond) +
                        (report.Stopped ? "（停止）" : string.Empty) +
                        (report.Seeked ? "（拖动）" : string.Empty) +
                        (result.Reason == "completed" ? "（已看完）" : string.Empty));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            _log.Warn("进度上报循环异常退出：" + ex.Message);
        }
    }

    private static string FormatMs(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}
