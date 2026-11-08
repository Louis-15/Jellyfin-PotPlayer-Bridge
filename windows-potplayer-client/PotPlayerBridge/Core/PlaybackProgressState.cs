namespace PotPlayerBridge.Core;

/// <summary>一次播放器采样。</summary>
/// <param name="WindowTitle">PotPlayer 主窗口标题。</param>
/// <param name="PositionMs">当前位置（毫秒）。</param>
/// <param name="DurationMs">总时长（毫秒），0 表示还没读出来。</param>
/// <param name="Status">-1 停止/未加载，0 空闲/播完，1 暂停，2 播放中。</param>
public readonly record struct PlaybackSample(string WindowTitle, long PositionMs, long DurationMs, int Status)
{
    /// <summary>Gets a value indicating whether 播放器正在播放。</summary>
    public bool IsPlaying => Status == 2;

    /// <summary>Gets a value indicating whether 播放器处于暂停。</summary>
    public bool IsPaused => Status == 1;

    /// <summary>Gets a value indicating whether 播放器没有加载媒体/已播完。</summary>
    public bool IsIdle => Status is -1 or 0;
}

/// <summary>状态机识别出的「需要立刻上报」的变化。定期上报由监控循环按时间决定，不在这里。</summary>
public enum PlaybackChange
{
    /// <summary>没有需要立即上报的变化。</summary>
    None,

    /// <summary>换集了。</summary>
    EpisodeChanged,

    /// <summary>用户拖动进度条。</summary>
    Seek,

    /// <summary>刚开始暂停。</summary>
    Pause,

    /// <summary>从暂停恢复播放。</summary>
    Resume,

    /// <summary>这一集播完了。</summary>
    Ended
}

/// <summary>一次上报所需的全部信息。</summary>
/// <param name="ItemId">当前集/电影的 Jellyfin ID。</param>
/// <param name="Index">在播放列表里的序号（1 开始）。</param>
/// <param name="PositionMs">位置（毫秒）。</param>
/// <param name="DurationMs">时长（毫秒）。</param>
/// <param name="Armed">是否已确认「真的在播这一集」。</param>
/// <param name="IsPaused">是否暂停。</param>
/// <param name="Stopped">是否是停止/关闭时的最后一次上报。</param>
/// <param name="Seeked">是否是用户拖动产生的上报。</param>
/// <param name="Ended">是否已播完这一集。</param>
public readonly record struct PlaybackSignal(
    string ItemId,
    int Index,
    long PositionMs,
    long DurationMs,
    bool Armed,
    bool IsPaused,
    bool Stopped,
    bool Seeked,
    bool Ended);

/// <summary>
/// 代理侧的播放进度状态机（PLAN 5.7）。纯逻辑、不碰窗口消息，所以能单测。
/// <para>
/// 核心职责是「宁可少报一次，也不要用错误的值覆盖用户的观看记录」：
/// <list type="bullet">
/// <item>只有确认播放器真的从「服务器算好的起播位置」开始播，才 armed；</item>
/// <item>假续播（期望 20:00，实际从 0 开始）永远不会 armed —— 因为一旦上报，
///       服务器记录的进度就会从 20:00 掉到几分钟，这正是验收项 A4 要防的；</item>
/// <item>换集后重新判定 armed（新的一集确实是从 0 开始）。</item>
/// </list>
/// </para>
/// </summary>
public sealed class PlaybackProgressState
{
    /// <summary>位置与期望起播位置的允许误差（毫秒）。STRM 加载慢时首帧位置会有几百毫秒抖动。</summary>
    public const long ArmedPositionToleranceMs = 5000;

    /// <summary>多久的位置跳变算「用户拖动」（毫秒）。</summary>
    public const long SeekThresholdMs = 10_000;

    /// <summary>「位置连续增长」需要连续几次采样才认可（1 秒一次 → 约 3 秒）。</summary>
    public const int GrowingSamplesRequired = 3;

    /// <summary>判定「已经播到结尾」的容差（毫秒）。</summary>
    public const long NearEndMs = 10_000;

    private readonly PlaybackPlan _plan;
    private readonly int _startIndex;
    private readonly long _startPositionMs;
    private readonly bool _singleItemMode;

    private int _index;
    private bool _armed;
    private long _lastPositionMs;
    private long _lastDurationMs;
    private int _growingCount;
    private bool _wasPlaying;
    private bool _lastPaused;

    /// <summary>
    /// 最后一次「读到过的正数位置」。
    /// 播完和关闭播放器这两种情况，PotPlayer 都会把 0x5004 读成 0；
    /// 如果这时按 0 上报，用户在「继续观看」里的进度就被抹平了。
    /// </summary>
    private long _lastPositivePositionMs;

    /// <summary>Initializes a new instance of the <see cref="PlaybackProgressState"/> class.</summary>
    /// <param name="plan">服务器返回的播放计划。</param>
    /// <param name="singleItemMode">
    /// 是否「单集直启」模式（电影/单片视频直接播放视频地址 + /sub）。
    /// 这种模式下 PotPlayer 的窗口标题是<b>文件名</b>而不是 Jellyfin 标题，
    /// 按标题前缀匹配永远识别不出集号，所以直接固定为起播项。
    /// </param>
    public PlaybackProgressState(PlaybackPlan plan, bool singleItemMode = false)
    {
        if (plan.Playlist.Count == 0)
        {
            throw new ArgumentException("播放计划里没有可播放的项。", nameof(plan));
        }

        _plan = plan;
        _singleItemMode = singleItemMode;
        _startIndex = Math.Clamp(plan.StartIndex, 1, plan.Playlist.Count);
        _index = _startIndex;
        _startPositionMs = Math.Max(0, plan.StartPositionTicks / TimeSpan.TicksPerMillisecond);
    }

    /// <summary>Gets 当前列表序号（1 开始）。</summary>
    public int Index => _index;

    /// <summary>Gets a value indicating whether 已经确认真的在播这一集。</summary>
    public bool Armed => _armed;

    /// <summary>Gets 当前项的 Jellyfin ID。</summary>
    public string CurrentItemId => _plan.Playlist[_index - 1].ItemId;

    /// <summary>Gets 起播项在列表里的序号（1 开始）。</summary>
    public int StartIndex => _startIndex;

    /// <summary>Gets 服务器算好的起播位置（毫秒），续播跳转就跳到这里。</summary>
    public long StartPositionMs => _startPositionMs;

    /// <summary>Gets 列表总数。</summary>
    public int Count => _plan.Playlist.Count;

    /// <summary>
    /// 告知状态机「刚才我们自己把播放位置跳到了这个位置」。
    /// 不这么做的话，续播跳转造成的 0 → 20:00 会被识别成「用户拖动」：
    /// 结果虽然无害（上报的位置是对的），但日志会一直误导排查的人。
    /// </summary>
    /// <param name="positionMs">跳转后的位置。</param>
    public void NotifySeekApplied(long positionMs)
    {
        _lastPositionMs = Math.Max(0, positionMs);
    }

    /// <summary>取某一项的期望起播位置：只有起播集有偏移，其余集都从 0 开始。</summary>
    /// <param name="index">1 开始的序号。</param>
    /// <returns>毫秒。</returns>
    public long ExpectedStartMs(int index) => index == _startIndex ? _startPositionMs : 0;

    /// <summary>喂一个采样，返回需要立即上报的变化，并给出当前完整状态。</summary>
    /// <param name="sample">采样。</param>
    /// <param name="signal">当前状态快照。</param>
    /// <returns>需要立即上报的变化。</returns>
    public PlaybackChange Feed(PlaybackSample sample, out PlaybackSignal signal)
    {
        var matched = MatchTitle(sample.WindowTitle);
        var changed = matched > 0 && matched != _index;

        if (changed)
        {
            // 换集：新的一集从 0 开始，armed 必须重新判定，否则会把上一集的「已确认」带过来。
            // 位置兜底值也要清空，否则新集停止上报时会拿上一集的位置去写新集。
            _index = matched;
            _armed = false;
            _growingCount = 0;
            _lastPositionMs = 0;
            _lastDurationMs = 0;
            _lastPositivePositionMs = 0;
        }

        var expectedMs = ExpectedStartMs(_index);
        var delta = Math.Abs(sample.PositionMs - expectedMs);
        var grows = sample.PositionMs > _lastPositionMs;

        // armed 只认两种证据：
        //  A. 位置落在期望起播位置附近（含换集后从 0 开始）；
        //  B. 期望就是从 0 开始，且位置连续增长了几秒。
        // 刻意不认「大跳 = 用户拖动」：自动连播时标题可能还没刷新，位置先从 2700s 掉到 0，
        // 那种跳变看起来和「用户拖回开头」一模一样，认错就会把进度冲成 0。
        if (!_armed && sample.IsPlaying && sample.PositionMs > 0)
        {
            if (delta <= ArmedPositionToleranceMs)
            {
                _armed = true;
            }
            else if (expectedMs <= ArmedPositionToleranceMs)
            {
                _growingCount = grows ? _growingCount + 1 : 0;
                if (_growingCount >= GrowingSamplesRequired)
                {
                    _armed = true;
                }
            }
            else
            {
                // 期望位置很远，实际却不在附近：典型的假续播，继续等（或永远不 armed）。
                _growingCount = 0;
            }
        }

        // 只有「同一集内、两边都在播、时长没变」的大跳才当作用户拖动。
        var seeked = !changed
                     && _armed
                     && _wasPlaying
                     && sample.IsPlaying
                     && _lastPositionMs > 0
                     && _lastDurationMs > 0
                     && sample.DurationMs > 0
                     && Math.Abs(sample.DurationMs - _lastDurationMs) <= 1000
                     && Math.Abs(sample.PositionMs - _lastPositionMs) > SeekThresholdMs;

        var paused = sample.IsPaused;
        var change = PlaybackChange.None;

        if (changed)
        {
            change = PlaybackChange.EpisodeChanged;
        }
        else if (seeked)
        {
            change = PlaybackChange.Seek;
        }
        else if (paused && !_lastPaused)
        {
            change = PlaybackChange.Pause;
        }
        else if (!paused && _lastPaused && sample.IsPlaying)
        {
            change = PlaybackChange.Resume;
        }
        else if (sample.IsIdle && _wasPlaying && IsNearEnd())
        {
            // 只有「播到结尾附近」才算播完。
            // 用户直接关播放器时 PotPlayer 也会把状态从 2 掉到 0，如果不看位置就判定，
            // 会把「才看了 25 分钟」的一集标成已看完 —— 用户的进度就此消失。
            change = PlaybackChange.Ended;
        }

        // 快照必须在更新 _lastPositionMs 之前生成：播完/停止那一刻位置读成 0，
        // 而我们要报的是「上一秒还在哪儿」。
        signal = Snapshot(sample, stopped: false, seeked: seeked, ended: change == PlaybackChange.Ended);

        _lastPositionMs = sample.PositionMs;
        _lastDurationMs = sample.DurationMs;
        _lastPaused = paused;
        _wasPlaying = sample.IsPlaying;

        if (sample.PositionMs > 0)
        {
            _lastPositivePositionMs = sample.PositionMs;
        }

        return change;
    }

    /// <summary>上一次采样是否已经播到结尾附近。</summary>
    /// <returns>接近结尾返回 true。</returns>
    private bool IsNearEnd()
    {
        return _lastDurationMs > 0 && _lastPositionMs >= _lastDurationMs - NearEndMs;
    }

    /// <summary>
    /// 生成当前状态快照。停止上报时也要带上最后一次已知的位置，
    /// 否则用户关播放器那一刻的进度就丢了（验收项 A1 / H3）。
    /// </summary>
    /// <param name="sample">最后一次采样。</param>
    /// <param name="stopped">是否是停止上报。</param>
    /// <param name="seeked">是否是拖动上报。</param>
    /// <param name="ended">是否已播完。</param>
    /// <returns>信号。</returns>
    public PlaybackSignal Snapshot(PlaybackSample sample, bool stopped, bool seeked = false, bool ended = false)
    {
        // 播完/停止时播放器往往读不到位置（返回 0 或窗口已销毁），这时必须用最后一次有效位置，
        // 否则「看完一集」会被写成「进度 0」，用户会在「继续观看」里看到这一集从头开始。
        var lostPosition = sample.PositionMs <= 0;
        var fallback = Math.Max(_lastPositionMs, _lastPositivePositionMs);
        var position = (stopped || ended) && lostPosition ? fallback : sample.PositionMs;
        var duration = sample.DurationMs > 0 ? sample.DurationMs : _lastDurationMs;

        return new PlaybackSignal(
            CurrentItemId,
            _index,
            Math.Max(0, position),
            Math.Max(0, duration),
            Armed: _armed || ended,
            IsPaused: !stopped && sample.IsPaused,
            Stopped: stopped,
            Seeked: seeked,
            Ended: ended);
    }

    /// <summary>
    /// 按窗口标题识别当前是第几项。
    /// PotPlayer 的标题形如「誓言无声 S01E04 突现情报真假难辨 - PotPlayer」，
    /// 与 DPL 的 <c>N*title*</c> 同源，所以用前缀匹配即可。
    /// 取最长匹配，避免 S01E1 命中 S01E10（虽然本项目标题都带两位集号，仍然防御一下）。
    /// </summary>
    /// <param name="title">窗口标题。</param>
    /// <returns>1 开始的序号；识别不出返回 0。</returns>
    private int MatchTitle(string title)
    {
        if (_singleItemMode)
        {
            // 单集直启：PotPlayer 的标题是视频文件名，和 Jellyfin 标题不同源，匹配必然失败。
            // 这次播放只有一个视频，直接认定就是起播项（返回 0 会让进度完全不上报）。
            return _startIndex;
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return 0;
        }

        var best = 0;
        var bestLength = 0;

        for (var i = 0; i < _plan.Playlist.Count; i++)
        {
            var candidate = _plan.Playlist[i].Title;
            if (string.IsNullOrWhiteSpace(candidate) || candidate.Length <= bestLength)
            {
                continue;
            }

            if (title.StartsWith(candidate, StringComparison.Ordinal))
            {
                best = i + 1;
                bestLength = candidate.Length;
            }
        }

        return best;
    }
}
