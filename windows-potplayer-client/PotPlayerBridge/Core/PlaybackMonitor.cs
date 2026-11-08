namespace PotPlayerBridge.Core;

/// <summary>
/// 播放进度监控：每秒采样 PotPlayer、喂给 <see cref="PlaybackProgressState"/>、按需交给
/// <see cref="ProgressSender"/> 上报。上报本身在另一半（ProgressSender）里，互不影响。
/// <para>
/// 三条硬性约束（来自 PLAN 5.7 / 6.9）：
/// <list type="number">
/// <item>上报走独立队列，网络再慢也不能拖住采样，更不能影响播放；</item>
/// <item>媒体就绪后先做「续播跳转」，把位置设到服务器算好的起播点，
///       否则会变成「显示 20:00 却从头播」的假续播；</item>
/// <item>播放器退出/代理退出时必须把最后位置报成 stopped，否则用户这次看的进度就丢了。</item>
/// </list>
/// </para>
/// </summary>
public sealed class PlaybackMonitor : IAsyncDisposable
{
    private const int SampleIntervalMs = 1000;
    private const int PeriodicReportMs = 5000;
    private const int SeekRetryLimit = 6;
    private const int NearEndMs = 10_000;

    private readonly PlaybackPlan _plan;
    private readonly int _processId;
    private readonly Logger _log;

    private readonly PlaybackProgressState _state;
    private readonly ProgressSender _sender;
    private readonly CancellationTokenSource _sampleCts = new();

    private IntPtr _window;
    private Task? _loop;
    private bool _seekFinished;
    private int _seekAttempts;
    private long _lastReportAt;
    private PlaybackSignal _lastSignal;

    /// <summary>片尾跳转是否已经发过：同一集只跳一次，否则会在阈值附近反复触发。</summary>
    private bool _outroSent;

    /// <summary>
    /// 换集后是否还没等到「新集真的开始」。
    /// 换集瞬间采样可能仍读到上一集的片尾位置（几千秒），若直接跳片头会把上一集拽回去，
    /// 所以要先等位置落到片头之前，才允许跳。
    /// </summary>
    private bool _seekPendingForEpisode;

    /// <summary>
    /// 是否因为「片尾跳转」而主动结束了当前集。
    /// 主动跳过片尾时播放器不会播到结尾，位置距结尾还有几十秒到两分钟；
    /// 若不上报「已看完」，服务器上这一集永远停在 duration-outro，
    /// 会一直挂在「继续观看」里（实测会攒出同一部剧的好几集）。
    /// </summary>
    private bool _outroSkipped;

    /// <summary>当前使用的窗口句柄（片尾跳转要用）。</summary>
    private IntPtr _handle;

    /// <summary>最后一次「位置 > 0」的快照。换集/停止时用它兜底，避免把 0 写成上一集的进度。</summary>
    private PlaybackSignal _lastPositiveSignal;

    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="PlaybackMonitor"/> class.</summary>
    /// <param name="plan">播放计划。</param>
    /// <param name="serverUrl">服务器地址。</param>
    /// <param name="accessToken">访问令牌（只在内存中使用）。</param>
    /// <param name="processId">PotPlayer 进程 ID。</param>
    /// <param name="jellyfin">服务器调用。</param>
    /// <param name="log">日志。</param>
    /// <param name="singleItemMode">
    /// 是否「单集直启」模式（电影/单片视频直接播视频地址 + /sub）。
    /// 该模式下窗口标题是文件名，不能按 Jellyfin 标题识别集号。
    /// </param>
    public PlaybackMonitor(
        PlaybackPlan plan,
        string serverUrl,
        string accessToken,
        int processId,
        JellyfinClient jellyfin,
        Logger log,
        bool singleItemMode = false)
    {
        _plan = plan;
        _processId = processId;
        _log = log;

        _state = new PlaybackProgressState(plan, singleItemMode);
        _sender = new ProgressSender(plan, serverUrl, accessToken, jellyfin, log);
        _lastSignal = new PlaybackSignal(
            _state.CurrentItemId,
            _state.Index,
            0,
            0,
            Armed: false,
            IsPaused: false,
            Stopped: false,
            Seeked: false,
            Ended: false);
    }

    /// <summary>Gets 服务器算好的起播位置（毫秒），诊断用。</summary>
    public long StartPositionMs => _state.StartPositionMs;

    /// <summary>启动监控（幂等）。</summary>
    public void Start()
    {
        if (_loop is not null)
        {
            return;
        }

        _log.Info(
            "开始监控播放进度：" + _plan.PlaylistName +
            "，起播第 " + _state.StartIndex + " 项" +
            (StartPositionMs > 0 ? "，续播位置 " + FormatMs(StartPositionMs) : string.Empty));

        _sender.Start();
        _loop = Task.Run(() => SampleLoopAsync(_sampleCts.Token));
    }

    /// <summary>
    /// 停止监控并上报最后位置。代理退出、换集、播放器关闭都会走到这里。
    /// 上报本身有超时，不会卡住退出流程。
    /// </summary>
    /// <returns>异步任务。</returns>
    public async Task StopAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _sampleCts.Cancel();

        try
        {
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止路径。
        }

        await FlushStoppedAsync().ConfigureAwait(false);
        await _sender.DisposeAsync().ConfigureAwait(false);

        _sampleCts.Dispose();
        _log.Info("已停止监控播放进度。");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private async Task SampleLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(SampleIntervalMs, cancellationToken).ConfigureAwait(false);

                var handle = ResolveWindow();
                if (handle == IntPtr.Zero)
                {
                    _log.Info("PotPlayer 已关闭，结束进度监控。");

                    // 播放器被用户关掉时，采样循环是「自己发现、自己退出」的，
                    // 不会有人再调 StopAsync；不在这里补一次 stopped，
                    // 服务器上停的就是最后一次 5 秒定期上报（暂停时差得更久）。
                    await FlushStoppedAsync().ConfigureAwait(false);
                    return;
                }

                var sample = new PlaybackSample(
                    PotPlayerWindow.GetTitle(handle),
                    PotPlayerWindow.GetCurrentTimeMs(handle),
                    PotPlayerWindow.GetTotalTimeMs(handle),
                    PotPlayerWindow.GetPlayStatus(handle));

                _handle = handle;

                var previous = _lastSignal;
                var change = _state.Feed(sample, out var signal);
                _lastSignal = signal;

                if (signal.PositionMs > 0)
                {
                    _lastPositiveSignal = signal;
                }

                if (change == PlaybackChange.EpisodeChanged)
                {
                    // 换集后重新允许跳片头：跳转状态必须清掉，否则新的一集不会再跳。
                    _seekFinished = false;
                    _seekAttempts = 0;
                    _seekPendingForEpisode = true;
                }

                ApplyStartSeek(handle, sample);
                ApplyOutro(sample);

                // 这一集刚确认真的在播（armed 由 false 变 true）：立刻报一条并标记 EpisodeStart，
                // 让服务器用会广播的保存理由，网页马上就能看到新一集的进度。
                // 不做这一步，自动连播过来的新集要等下一次暂停/停止才在网页上出现。
                var episodeStart = signal.Armed && !_state.EpisodeStartReported;
                if (episodeStart)
                {
                    signal = signal with { EpisodeStart = true };
                    _state.NotifyEpisodeStartReported();
                    _lastSignal = signal;
                }

                if (change == PlaybackChange.EpisodeChanged)
                {
                    // 片尾标记必须在 ApplyOutro 之后清：换集这一刻的采样还是上一集的
                    // 片尾位置，若提前清掉，ApplyOutro 会用「上一集的位置 + 新一集的序号」
                    // 立刻再跳一次，把刚切过来的集又切走。
                    _outroSent = false;
                    ReportEpisodeChange(previous, signal);
                }
                else if (change != PlaybackChange.None || episodeStart)
                {
                    // 暂停/恢复/拖动/播完/新集开始：立刻报一次，用户很快就能在别的设备上看到。
                    _sender.Enqueue(signal);
                    _lastReportAt = Environment.TickCount64;
                }
                else if (ShouldReportPeriodically(signal))
                {
                    _sender.Enqueue(signal);
                    _lastReportAt = Environment.TickCount64;
                }

                if (change == PlaybackChange.Ended && _state.Index >= _state.Count)
                {
                    _log.Info("整季已播完，结束进度监控。");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            // 监控线程绝不能把代理带崩：记日志后安静退出，播放不受影响。
            _log.Warn("进度监控异常退出：" + ex.Message);
        }
    }

    /// <summary>
    /// 起播跳转：媒体就绪后把位置设到该集的期望起点。
    /// <list type="bullet">
    /// <item>起播集：服务器算好的位置（续播点或片头）；</item>
    /// <item>之后连播/手动切来的集：该季配置的片头秒数。</item>
    /// </list>
    /// <para>
    /// STRM 首次加载可能几秒才出画面，所以按「位置还停在开头」重试几次而不是一次了事。
    /// </para>
    /// </summary>
    private void ApplyStartSeek(IntPtr handle, PlaybackSample sample)
    {
        var targetMs = _state.ExpectedStartMs(_state.Index);
        if (_seekFinished || targetMs <= 0 || !sample.IsPlaying || sample.PositionMs <= 0)
        {
            return;
        }

        var delta = Math.Abs(sample.PositionMs - targetMs);
        if (delta <= PlaybackProgressState.ArmedPositionToleranceMs)
        {
            _seekFinished = true;
            _seekPendingForEpisode = false;
            return;
        }

        if (_seekPendingForEpisode)
        {
            // 换集刚发生，位置还可能停在上一集的片尾（几千秒）。
            // 这时跳片头会把上一集拽回去，所以等到位置落到片头之前再动手。
            if (sample.PositionMs > targetMs + PlaybackProgressState.ArmedPositionToleranceMs)
            {
                return;
            }

            _seekPendingForEpisode = false;
        }

        if (_seekAttempts >= SeekRetryLimit)
        {
            // 跳不过去就放弃：状态机不会 armed，这条集不会写坏服务器进度（A4）。
            _seekFinished = true;
            _log.Warn(
                "跳转多次未生效（期望 " + FormatMs(targetMs) +
                "，实际 " + FormatMs(sample.PositionMs) + "），本次不写进度。");
            return;
        }

        // 用户可能在跳转生效前就自己拖了位置：已经接近就别再强行拉回去。
        if (_seekAttempts > 0 && sample.PositionMs > targetMs + PlaybackProgressState.ArmedPositionToleranceMs)
        {
            _seekFinished = true;
            return;
        }

        _seekAttempts++;
        _log.Info(
            (_state.Index == _state.StartIndex ? "续播跳转到 " : "片头跳转到 ") + FormatMs(targetMs) +
            "（第 " + _seekAttempts + " 次，当前 " + FormatMs(sample.PositionMs) + "）");
        PotPlayerWindow.SetCurrentTimeMs(handle, targetMs);

        // 同步状态机里的「上一位置」，否则下一采样会把这个跳变当成用户拖动。
        _state.NotifySeekApplied(targetMs);
    }

    private void ReportEpisodeChange(PlaybackSignal previous, PlaybackSignal signal)
    {
        // 上一集的位置可能已经读成 0（播完瞬间/切集瞬间），这时用最后一次有效位置，
        // 否则会把「看完的上一集」写成「进度 0 且未看完」。
        if (previous.PositionMs <= 0 &&
            _lastPositiveSignal.PositionMs > 0 &&
            !string.Equals(_lastPositiveSignal.ItemId, signal.ItemId, StringComparison.OrdinalIgnoreCase))
        {
            previous = _lastPositiveSignal;
        }

        // 上一集：主动跳过片尾或接近结尾就标成已看完，否则记成「停下来时看到哪」。
        if (!string.IsNullOrEmpty(previous.ItemId) &&
            !string.Equals(previous.ItemId, signal.ItemId, StringComparison.OrdinalIgnoreCase))
        {
            // _outroSkipped：片尾跳转过的集必须留在「已看完」，
            // 否则下面这句会按位置把它改回「未看完」，用户就会在「继续观看」里
            // 看到同一部剧的好几集堆在一起。
            var ended = _outroSkipped ||
                        previous.Ended ||
                        (previous.DurationMs > 0 && previous.PositionMs >= previous.DurationMs - NearEndMs);
            _sender.Enqueue(previous with { Stopped = true, IsPaused = false, Ended = ended });
        }

        _outroSkipped = false;
        _sender.Enqueue(signal);
        _lastReportAt = Environment.TickCount64;
    }

    private bool ShouldReportPeriodically(PlaybackSignal signal)
    {
        // 没 armed 就不报：这正是「假续播时宁可什么都不写」的落点。
        if (!signal.Armed || signal.PositionMs <= 0)
        {
            return false;
        }

        return Environment.TickCount64 - _lastReportAt >= PeriodicReportMs;
    }

    private async Task FlushStoppedAsync()
    {
        // 用状态机重新生成一次快照，而不是复用 _lastSignal：
        // 关闭过程中最后几次采样的位置往往是 0，但 Snapshot(stopped:true) 会回退到
        // 「最后一次读到的正数位置」，这才是用户真正看到哪儿。
        var finalSignal = _state.Snapshot(new PlaybackSample(string.Empty, 0, 0, -1), stopped: true);
        await _sender.FlushAsync(finalSignal).ConfigureAwait(false);
    }

    /// <summary>
    /// 片尾自动下一集（PLAN 6.7 / D4）。
    /// 条件是「靠近结尾、还有下一集、这一集还没跳过一次」。
    /// 最后一集不跳也不循环 —— PotPlayer 播完最后一项会自然停止，播完标记由状态机的 Ended 负责。
    /// </summary>
    private void ApplyOutro(PlaybackSample sample)
    {
        // 电影不跳片尾（需求明确排除：很多电影片尾有彩蛋/信息）。
        if (_plan.ItemType is not "Episode")
        {
            return;
        }

        // 换集刚发生、位置还停在上一集时不做判断：
        // 否则会用「上一集的位置 + 新一集的序号」立刻再跳一次，把刚切过来的集又切走。
        if (_seekPendingForEpisode)
        {
            return;
        }

        // 未配置片尾的季什么都不做（D5）。
        if (_plan.OutroSeconds <= 0 || _outroSent)
        {
            return;
        }

        if (_state.Index >= _state.Count)
        {
            // 最后一集：不跳。让 PotPlayer 自己播完，状态机会把它标记为已看完。
            return;
        }

        var durationMs = sample.DurationMs;
        if (durationMs <= 0)
        {
            return;
        }

        var threshold = durationMs - (_plan.OutroSeconds * 1000L);
        if (sample.PositionMs < threshold)
        {
            return;
        }

        _outroSent = true;
        _outroSkipped = true;
        _log.Info(
            "到达片尾（" + FormatMs(sample.PositionMs) + " / " + FormatMs(durationMs) +
            "，片尾 " + _plan.OutroSeconds + " 秒），标记已看完并跳到下一集。");

        // 立刻按「已看完」上报，而不是等换集时按位置推断：
        // 片尾阈值有一两分钟，位置离结尾还远，按位置推断必然判成「未看完」。
        _sender.Enqueue(_state.Snapshot(sample, stopped: false, ended: true));
        _lastReportAt = Environment.TickCount64;

        PotPlayerWindow.SendNext(_handle);
    }

    private IntPtr ResolveWindow()
    {
        if (PotPlayerWindow.Exists(_window))
        {
            return _window;
        }

        _window = PotPlayerWindow.FindMainWindow(_processId);
        return _window;
    }

    private static string FormatMs(long ms)
    {
        var span = TimeSpan.FromMilliseconds(ms);
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}
