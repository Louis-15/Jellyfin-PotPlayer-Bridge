using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PotPlayerBridge.Core;

/// <summary>
/// 把「用户点了哪一项」解析成「播哪一集、整季列表、从哪里开始」。
/// 这是需求 2 的唯一权威实现：网页脚本只上报 itemId + 意图，不做任何剧集层级推断。
/// <para>
/// 查询职责集中在这里，挑选/计算规则放在 <see cref="PlaybackPlanner"/>，后者可以单元测试。
/// </para>
/// </summary>
public sealed class PlaybackResolver
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly EpisodeLibraryQuery _episodes;
    private readonly StreamUrlResolver _streams;
    private readonly SeasonSettingsStore _seasonSettings;
    private readonly ILogger<PlaybackResolver> _logger;

    /// <summary>Initializes a new instance of the <see cref="PlaybackResolver"/> class.</summary>
    /// <param name="library">媒体库。</param>
    /// <param name="users">用户管理。</param>
    /// <param name="userData">用户进度。</param>
    /// <param name="episodes">剧集查询。</param>
    /// <param name="streams">播放地址。</param>
    /// <param name="seasonSettings">季配置。</param>
    /// <param name="logger">Logger.</param>
    public PlaybackResolver(
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        EpisodeLibraryQuery episodes,
        StreamUrlResolver streams,
        SeasonSettingsStore seasonSettings,
        ILogger<PlaybackResolver> logger)
    {
        _library = library;
        _users = users;
        _userData = userData;
        _episodes = episodes;
        _streams = streams;
        _seasonSettings = seasonSettings;
        _logger = logger;
    }

    /// <summary>解析播放计划。</summary>
    /// <param name="userId">当前用户。</param>
    /// <param name="itemId">用户点击的项。</param>
    /// <param name="intent">播放意图。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>播放计划。</returns>
    public async Task<PlaybackPlan> ResolveAsync(Guid userId, Guid itemId, PlaybackIntent intent, CancellationToken cancellationToken)
    {
        var user = _users.GetUserById(userId)
                   ?? throw new BridgeException(401, "找不到当前登录用户，请刷新页面重新登录。", "no-user");

        var item = _library.GetItemById(itemId)
                   ?? throw new BridgeException(404, "找不到要播放的项目，可能已被删除或移出媒体库。", "item-not-found");

        _logger.LogDebug(
            "解析播放：item={ItemId} type={Type} intent={Intent} user={UserId}",
            itemId,
            item.GetType().Name,
            intent,
            userId);

        return item switch
        {
            // 电影只有一项；按 D-10 不跳片头片尾。
            Movie movie => await BuildPlanAsync(
                new ResolutionContext(movie, new List<BaseItem> { movie }, null, null, intent == PlaybackIntent.Restart),
                user,
                cancellationToken).ConfigureAwait(false),
            Episode episode => await ResolveEpisodeAsync(episode, user, intent, cancellationToken).ConfigureAwait(false),
            Season season => await ResolveSeasonAsync(season, user, intent, cancellationToken).ConfigureAwait(false),
            Series series => await ResolveSeriesAsync(series, user, intent, cancellationToken).ConfigureAwait(false),
            _ => throw new BridgeException(
                400,
                $"「{item.Name}」的类型（{item.GetType().Name}）暂不支持用 PotPlayer 播放。",
                "unsupported-type")
        };
    }

    private async Task<PlaybackPlan> ResolveSeriesAsync(Series series, User user, PlaybackIntent intent, CancellationToken cancellationToken)
    {
        var episodes = _episodes.GetSeriesEpisodes(series);
        if (episodes.Count == 0)
        {
            throw new BridgeException(404, $"《{series.Name}》还没有可播放的剧集。", "no-episodes");
        }

        var snapshots = episodes.Select(e => ToSnapshot(e, user, series.Name)).ToList();
        var first = PlaybackPlanner.PickFirstEpisode(snapshots)
                    ?? throw new BridgeException(404, $"《{series.Name}》还没有可播放的剧集。", "no-episodes");

        // item / resume 都按「最近有进度的一集」；restart 强制第一季第一集。
        var chosenSnapshot = intent == PlaybackIntent.Restart
            ? first
            : PlaybackPlanner.PickMostRecentlyPlayed(snapshots) ?? first;

        var chosen = episodes.First(e => e.Id == chosenSnapshot.ItemId);
        var season = _episodes.GetSeason(chosen.SeasonId);

        // 播放列表 = 被选中那一集的整季全集，与「从哪一集开始」无关。
        var playlist = season is null
            ? new List<BaseItem> { chosen }
            : episodes.Where(e => e.SeasonId == season.Id).Cast<BaseItem>().ToList();

        if (playlist.Count == 0)
        {
            playlist.Add(chosen);
        }

        return await BuildPlanAsync(
            new ResolutionContext(chosen, playlist, season, series, intent == PlaybackIntent.Restart),
            user,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlaybackPlan> ResolveSeasonAsync(Season season, User user, PlaybackIntent intent, CancellationToken cancellationToken)
    {
        var episodes = _episodes.GetSeasonEpisodes(season);
        if (episodes.Count == 0)
        {
            throw new BridgeException(404, $"「{season.Name}」还没有可播放的剧集。", "no-episodes");
        }

        // 这里只是为了「挑哪一集」建快照，标题不参与判断；最终标题在 BuildPlanAsync 里用剧名重生成。
        var snapshots = episodes.Select(e => ToSnapshot(e, user, null)).ToList();
        var first = PlaybackPlanner.PickFirstEpisode(snapshots)
                    ?? throw new BridgeException(404, $"「{season.Name}」还没有可播放的剧集。", "no-episodes");

        var chosenSnapshot = intent == PlaybackIntent.Restart
            ? first
            : PlaybackPlanner.PickMostRecentlyPlayed(snapshots) ?? first;

        var chosen = episodes.First(e => e.Id == chosenSnapshot.ItemId);
        var series = _episodes.GetSeries(season.SeriesId);

        return await BuildPlanAsync(
            new ResolutionContext(
                chosen,
                episodes.Cast<BaseItem>().ToList(),
                season,
                series,
                intent == PlaybackIntent.Restart),
            user,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlaybackPlan> ResolveEpisodeAsync(Episode episode, User user, PlaybackIntent intent, CancellationToken cancellationToken)
    {
        var season = _episodes.GetSeason(episode.SeasonId);
        var series = _episodes.GetSeries(episode.SeriesId)
                     ?? (season is null ? null : _episodes.GetSeries(season.SeriesId));

        var playlist = season is null
            ? new List<BaseItem>()
            : _episodes.GetSeasonEpisodes(season).Cast<BaseItem>().ToList();

        // 即使季查询异常（例如手动删过季目录），也必须保证目标集本身在列表里。
        if (!playlist.Any(i => i.Id == episode.Id))
        {
            playlist.Add(episode);
        }

        return await BuildPlanAsync(
            new ResolutionContext(episode, playlist, season, series, intent == PlaybackIntent.Restart),
            user,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PlaybackPlan> BuildPlanAsync(ResolutionContext context, User user, CancellationToken cancellationToken)
    {
        var snapshots = context.Playlist.Select(i => ToSnapshot(i, user, context.Series?.Name)).ToList();
        var ordered = PlaybackPlanner.SortPlaylist(snapshots);
        var startSnapshot = ordered[PlaybackPlanner.FindStartIndex(ordered, context.Target.Id) - 1];

        var settings = context.Season is null
            ? null
            : await _seasonSettings.GetAsync(context.Season.Id, cancellationToken).ConfigureAwait(false);

        var introSeconds = settings?.IntroSeconds ?? 0;
        var outroSeconds = settings?.OutroSeconds ?? 0;

        // 续播位置与片头跳过在这里合并：代理拿到的是「最终起播位置」，不需要再算一次。
        var resumeTicks = context.Restart ? 0 : startSnapshot.PlaybackPositionTicks;
        var startTicks = PlaybackPlanner.ComputeStartPositionTicks(resumeTicks, introSeconds);

        var entries = new List<PlaylistEntry>(ordered.Count);
        foreach (var snapshot in ordered)
        {
            var entity = _library.GetItemById(snapshot.ItemId);
            if (entity is null)
            {
                continue;
            }

            entries.Add(new PlaylistEntry
            {
                ItemId = snapshot.ItemId.ToString("N"),
                IndexNumber = snapshot.EpisodeNumber,
                ParentIndexNumber = snapshot.SeasonNumber,
                Title = snapshot.Title,
                StreamUrl = await _streams.ResolveAsync(entity, user, cancellationToken).ConfigureAwait(false),
                RuntimeTicks = snapshot.RuntimeTicks
            });
        }

        var targetItemId = context.Target.Id.ToString("N");
        var startIndex = entries.FindIndex(e => e.ItemId == targetItemId);
        if (startIndex < 0)
        {
            throw new BridgeException(404, "播放列表里找不到目标项，已中止播放。", "target-missing");
        }

        var playlistName = BuildPlaylistName(context);

        return new PlaybackPlan
        {
            PlaySessionId = Guid.NewGuid().ToString("N"),
            UserId = user.Id.ToString("N"),
            ItemId = targetItemId,
            ItemType = context.Target is Episode ? "Episode" : "Movie",
            Title = playlistName,
            PlaylistName = playlistName,
            StartIndex = startIndex + 1,
            StartPositionTicks = startTicks,
            IntroSeconds = introSeconds,
            OutroSeconds = outroSeconds,
            SeriesId = context.Series?.Id.ToString("N") ?? (context.Target is Episode episode && episode.SeriesId != Guid.Empty
                ? episode.SeriesId.ToString("N")
                : null),
            SeasonId = context.Season?.Id.ToString("N"),
            Playlist = entries
        };
    }

    private static string BuildPlaylistName(ResolutionContext context)
    {
        if (context.Target is Movie movie)
        {
            return movie.Name;
        }

        var seriesName = context.Series?.Name ?? context.Season?.SeriesName;

        if (context.Season is null)
        {
            return string.IsNullOrWhiteSpace(seriesName) ? context.Target.Name : seriesName;
        }

        // C3：普通季用「剧名 第X季」；特别篇（Season 0）没有正季号，用季名兜底。
        if (context.Season.IndexNumber is > 0)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0} 第{1}季",
                string.IsNullOrWhiteSpace(seriesName) ? context.Season.Name : seriesName,
                context.Season.IndexNumber.Value);
        }

        return string.IsNullOrWhiteSpace(seriesName)
            ? context.Season.Name
            : seriesName + " " + context.Season.Name;
    }

    private EpisodeSnapshot ToSnapshot(BaseItem item, User user, string? seriesName)
    {
        var data = _userData.GetUserData(user, item);
        var episode = item as Episode;

        return new EpisodeSnapshot(
            item.Id,
            episode?.ParentIndexNumber,
            episode?.IndexNumber,
            data?.PlaybackPositionTicks ?? 0,
            data?.Played ?? false,
            data?.LastPlayedDate,
            BuildItemTitle(item, seriesName),
            item.RunTimeTicks ?? 0);
    }

    /// <summary>
    /// 生成播放列表里显示的条目名。
    /// 剧集带上剧名（「誓言无声 S01E04 突现情报真假难辨」），因为这一行会直接成为
    /// PotPlayer 的窗口标题与 OSD 文本——只有「S01E04 集名」时，切集后很难看出在放哪部剧。
    /// 电影保持原名；拿不到剧名时退回原来的「SxxExx 集名」格式，不影响任何已有行为。
    /// </summary>
    /// <param name="item">条目。</param>
    /// <param name="seriesName">所属剧名，可为空。</param>
    /// <returns>展示用标题。</returns>
    private static string BuildItemTitle(BaseItem item, string? seriesName)
    {
        if (item is not Episode episode || episode.ParentIndexNumber is not > 0 || episode.IndexNumber is not > 0)
        {
            return item.Name;
        }

        var header = string.Format(
            CultureInfo.InvariantCulture,
            "S{0:00}E{1:00} {2}",
            episode.ParentIndexNumber.Value,
            episode.IndexNumber.Value,
            episode.Name);

        return string.IsNullOrWhiteSpace(seriesName) ? header : seriesName.Trim() + " " + header;
    }

    /// <summary>解析过程中用到的中间结果（目标、播放列表、季、剧、是否从 0 开始）。</summary>
    private sealed record ResolutionContext(
        BaseItem Target,
        IReadOnlyList<BaseItem> Playlist,
        Season? Season,
        Series? Series,
        bool Restart);
}
