using System;

namespace PotPlayerBridge.Core;

/// <summary>一次播放请求的完整结果。</summary>
/// <param name="Plan">服务器返回的播放计划。</param>
/// <param name="Playlist">DPL 写入结果。</param>
/// <param name="Launch">播放器启动结果。</param>
public sealed record PlayResponse(PlaybackPlan Plan, PlaylistWriteResult Playlist, LaunchResult Launch);

/// <summary>
/// 把「网页点播放」串起来：解析计划 → 生成 DPL → 拉起 PotPlayer。
/// 整条链路用一把异步锁串行化：连点两集时，第二次会先关掉第一次拉起的实例，
/// 不会出现两个播放器同时上报进度（验收项 H1）。
/// </summary>
public sealed class PlaybackService
{
    private readonly ConfigStore _config;
    private readonly PotPlayerLocator _locator;
    private readonly JellyfinClient _jellyfin;
    private readonly PlaylistStore _playlists;
    private readonly PotPlayerLauncher _launcher;
    private readonly AgentRuntime _runtime;
    private readonly Logger _log;

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="PlaybackService"/> class.</summary>
    /// <param name="config">配置。</param>
    /// <param name="locator">路径发现。</param>
    /// <param name="jellyfin">服务器调用。</param>
    /// <param name="playlists">DPL 管理。</param>
    /// <param name="launcher">播放器启动/关闭。</param>
    /// <param name="runtime">运行期状态。</param>
    /// <param name="log">日志。</param>
    public PlaybackService(
        ConfigStore config,
        PotPlayerLocator locator,
        JellyfinClient jellyfin,
        PlaylistStore playlists,
        PotPlayerLauncher launcher,
        AgentRuntime runtime,
        Logger log)
    {
        _config = config;
        _locator = locator;
        _jellyfin = jellyfin;
        _playlists = playlists;
        _launcher = launcher;
        _runtime = runtime;
        _log = log;
    }

    /// <summary>处理一次播放请求。</summary>
    /// <param name="request">请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>播放结果。</returns>
    public async Task<PlayResponse> PlayAsync(PlayRequest request, CancellationToken cancellationToken)
    {
        Validate(request);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = _config.GetSnapshot();

            if (config.AbnormalExit)
            {
                // 上次是「PotPlayer 没响应 WM_CLOSE 被强杀」：先补一次 Playlist 目录快照（PLAN 6.5）。
                BackupPlaylistForRecovery(config);
                config.AbnormalExit = false;
                _config.Save(config);
                _log.Warn("检测到上次异常退出标记，已对 Playlist 目录做快照。");
            }

            var exePath = ResolvePotPlayer(config);

            var plan = await _jellyfin
                .ResolveAsync(request.ServerUrl!, request.AccessToken!, request.ItemId!, request.Intent!, cancellationToken)
                .ConfigureAwait(false);

            // 顺序很关键：先关旧实例，再写 DPL，最后启动。
            // PotPlayer 退出时会把「当前播放项」写回 DPL（topindex/playname 都会变），
            // 若先写 DPL 再关旧实例，刚算好的起播集会被旧实例的退出状态覆盖（实测会串到下一集）。
            await _launcher.CloseOwnedAsync(cancellationToken).ConfigureAwait(false);

            var write = await _playlists.WriteAsync(plan, exePath, cancellationToken).ConfigureAwait(false);
            var launch = await _launcher.LaunchAsync(exePath, write.DplPath, cancellationToken).ConfigureAwait(false);

            _runtime.SetCurrentPlayback(plan, launch.ProcessId, launch.WindowHandle);

            _log.Info(
                "播放已启动：" + plan.PlaylistName +
                "，起播第 " + plan.StartIndex + " 项" +
                "，列表 " + plan.Playlist.Count + " 项" +
                "，PID=" + launch.ProcessId);

            return new PlayResponse(plan, write, launch);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void Validate(PlayRequest request)
    {
        if (!Uri.TryCreate(request.ServerUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new PlaybackException(400, "serverUrl 必须是 http:// 或 https:// 开头的完整地址。", "bad-server-url");
        }

        if (string.IsNullOrWhiteSpace(request.AccessToken))
        {
            throw new PlaybackException(400, "缺少访问令牌，请刷新网页并重新登录 Jellyfin。", "no-token");
        }

        if (!Guid.TryParse(request.ItemId, out _))
        {
            throw new PlaybackException(400, "itemId 不是合法的 GUID。", "bad-item-id");
        }

        var intent = request.Intent?.Trim().ToLowerInvariant();
        if (intent is not ("item" or "resume" or "restart"))
        {
            throw new PlaybackException(400, "intent 只能是 item / resume / restart。", "bad-intent");
        }

        // 统一成小写，后面拼给服务器的就是这个值。
        request.Intent = intent;
    }

    private string ResolvePotPlayer(AgentConfig config)
    {
        var fast = _locator.DetectFast(config.PotPlayerPath);
        var path = fast.Path;

        if (path is null)
        {
            // 配置失效时由完整发现兜底（F4：自动重查，不直接报错）。
            path = _locator.DetectFull(config.PotPlayerPath).Path;
        }

        if (path is null)
        {
            throw new PlaybackException(
                409,
                "没有找到 PotPlayer。请安装 PotPlayer，或在设置里手动指定 PotPlayerMini64.exe 的路径。",
                "potplayer-not-found");
        }

        if (!string.Equals(path, config.PotPlayerPath, StringComparison.OrdinalIgnoreCase))
        {
            config.PotPlayerPath = path;
            _config.Save(config);
            _log.Info("播放前更新 PotPlayer 路径：" + path);
        }

        return path;
    }

    private void BackupPlaylistForRecovery(AgentConfig config)
    {
        try
        {
            var exe = _locator.DetectFast(config.PotPlayerPath).Path;
            var exeDirectory = exe is null ? null : Path.GetDirectoryName(exe);
            if (string.IsNullOrWhiteSpace(exeDirectory))
            {
                return;
            }

            _playlists.BackupDirectory(Path.Combine(exeDirectory, "Playlist"));
        }
        catch (Exception ex)
        {
            _log.Warn("异常退出后的 Playlist 快照失败：" + ex.Message);
        }
    }
}
