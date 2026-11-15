using System.Collections.Concurrent;

namespace PotPlayerBridge.Core;

/// <summary>
/// 剧集播放期间的「逐集外挂字幕」协调器。
/// <para>
/// 只用于**整季 DPL 播放**：电影/单片视频已经走「单集直启 + <c>/sub=</c>」，字幕本来就正常，
/// 不需要也不应该再经过这里。
/// </para>
/// <para>
/// 工作方式：
/// <list type="number">
/// <item><see cref="PlaybackMonitor"/> 在每一集真正开始播放（媒体已就绪）时回调 <see cref="Request"/>；</item>
/// <item>这里按播放列表序号找到该集，下载服务器选好的外挂字幕；</item>
/// <item>下载完成后交给 <see cref="SubtitleInjector"/> 挂到 PotPlayer 当前播放项上。</item>
/// </list>
/// </para>
/// <para>
/// 为什么要做「取消旧任务 + 序号校验」：字幕下载可能比用户切集慢。如果第 2 集还在下载时用户
/// 已经切到第 3 集，旧任务必须放弃，否则会把第 2 集的字幕挂到第 3 集上。
/// </para>
/// </summary>
internal sealed class SubtitlePlaybackCoordinator : IAsyncDisposable
{
    private readonly SubtitleDownloader _downloads;
    private readonly Logger _log;
    private readonly object _loadedGate = new();

    /// <summary>已经成功加载过字幕的播放列表序号（1 开始）。失败时会移除，允许重试。</summary>
    private readonly HashSet<int> _loadedIndexes = new();

    /// <summary>
    /// 当前播放上下文。整体替换而不是逐字段改，避免回调线程读到「新计划 + 旧 Token」的半成品状态。
    /// </summary>
    private volatile PlaybackContext? _context;

    /// <summary>当前这轮下载/注入的取消源；新请求会取消旧请求。</summary>
    private CancellationTokenSource? _currentCts;

    /// <summary>请求代号：只有最新一次请求允许真正注入，防止旧字幕串到新一集。</summary>
    private int _latestEpoch;

    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="SubtitlePlaybackCoordinator"/> class.</summary>
    /// <param name="downloads">字幕下载器。</param>
    /// <param name="log">日志。</param>
    public SubtitlePlaybackCoordinator(SubtitleDownloader downloads, Logger log)
    {
        _downloads = downloads;
        _log = log;
    }

    /// <summary>
    /// 绑定一次剧集播放的上下文。每次新播放开始时调用。
    /// </summary>
    /// <param name="plan">服务器返回的播放计划（整季列表 + 每集字幕信息）。</param>
    /// <param name="serverUrl">服务器地址。</param>
    /// <param name="accessToken">访问令牌（只在内存中使用）。</param>
    public void Begin(PlaybackPlan plan, string serverUrl, string accessToken)
    {
        _context = new PlaybackContext(plan, serverUrl, accessToken);

        lock (_loadedGate)
        {
            _loadedIndexes.Clear();
        }

        // 上一轮还没跑完的任务必须取消，避免旧播放的字幕挂到新播放上。
        CancelCurrent();
        Interlocked.Increment(ref _latestEpoch);
    }

    /// <summary>
    /// 为某一集请求加载字幕。同一集只成功加载一次；失败后允许下次重试。
    /// </summary>
    /// <param name="index">该集在播放列表中的序号（1 开始，与 DPL 的项序号一致）。</param>
    /// <param name="window">PotPlayer 主窗口句柄（由进度监控在媒体就绪后给出）。</param>
    public void Request(int index, IntPtr window)
    {
        var context = _context;
        if (context is null || index < 1 || index > context.Plan.Playlist.Count)
        {
            return;
        }

        var entry = context.Plan.Playlist[index - 1];
        if (entry.Subtitles.Count == 0)
        {
            _log.Info("第 " + index + " 项没有外挂字幕，跳过自动加载：" + entry.Title);
            return;
        }

        lock (_loadedGate)
        {
            if (_loadedIndexes.Contains(index))
            {
                return;
            }

            // 先占位，避免同一集的多个回调并发下载同一份字幕；失败时会移除。
            _loadedIndexes.Add(index);
        }

        var epoch = Interlocked.Increment(ref _latestEpoch);
        var cts = new CancellationTokenSource();

        var previous = Interlocked.Exchange(ref _currentCts, cts);
        previous?.Cancel();
        previous?.Dispose();

        _log.Info("准备为第 " + index + " 项加载外挂字幕：" + entry.Title);
        _ = Task.Run(() => LoadAsync(context, index, entry, window, epoch, cts.Token));
    }

    private async Task LoadAsync(
        PlaybackContext context,
        int index,
        PlaylistEntry entry,
        IntPtr window,
        int epoch,
        CancellationToken cancellationToken)
    {
        var injected = false;

        try
        {
            // 进入下载前先看一次：如果用户已经切走，连下载都不必做。
            if (IsStale(epoch, cancellationToken))
            {
                return;
            }

            var subtitlePath = await _downloads
                .DownloadOneAsync(
                    entry,
                    context.Plan.PlaySessionId,
                    context.ServerUrl,
                    context.AccessToken,
                    cancellationToken)
                .ConfigureAwait(false);

            if (subtitlePath is null)
            {
                _log.Warn("第 " + index + " 项字幕下载失败或没有可用字幕：" + entry.Title);
                return;
            }

            // 下载可能花了几秒：再次确认用户还停在目标集，且窗口仍然有效。
            if (IsStale(epoch, cancellationToken))
            {
                return;
            }

            if (!PotPlayerWindow.Exists(window))
            {
                _log.Warn("加载字幕时 PotPlayer 窗口已不存在，跳过：" + entry.Title);
                return;
            }

            injected = SubtitleInjector.TryInject(window, subtitlePath, _log);
            if (injected)
            {
                _log.Info("第 " + index + " 项外挂字幕已加载：" + entry.Title);
            }
        }
        catch (OperationCanceledException)
        {
            // 用户切集/停止播放导致的正常取消，不记错误。
        }
        catch (Exception ex)
        {
            // 字幕是增强功能：任何异常都不能影响播放，也不能让后台任务崩掉。
            _log.Warn("为第 " + index + " 项加载字幕失败：" + ex.Message);
        }
        finally
        {
            if (!injected)
            {
                // 失败/取消时撤销占位，下一次同一集还能重试。
                lock (_loadedGate)
                {
                    _loadedIndexes.Remove(index);
                }
            }
        }
    }

    /// <summary>
    /// 判断这次请求是否已经过期：用户切到别的集、或者协调器已被释放。
    /// </summary>
    private bool IsStale(int epoch, CancellationToken cancellationToken)
    {
        return cancellationToken.IsCancellationRequested
               || Volatile.Read(ref _disposed) != 0
               || epoch != Volatile.Read(ref _latestEpoch);
    }

    private void CancelCurrent()
    {
        var cts = Interlocked.Exchange(ref _currentCts, null);
        if (cts is null)
        {
            return;
        }

        cts.Cancel();
        cts.Dispose();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        CancelCurrent();
        return ValueTask.CompletedTask;
    }

    /// <summary>一次播放的不可变上下文。</summary>
    private sealed record PlaybackContext(PlaybackPlan Plan, string ServerUrl, string AccessToken);
}
