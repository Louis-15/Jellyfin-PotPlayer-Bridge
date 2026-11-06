using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PotPlayerBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PotPlayerBridge.Api;

/// <summary>
/// 播放语义相关的接口：解析播放计划、返回 item 基本信息（供季配置面板判断类型）。
/// 这两个接口都要求登录；用户 ID 一律从 JWT 里取，绝不接受请求体里的 userId。
/// </summary>
[ApiController]
[Route("PotPlayerBridge")]
public sealed class PlaybackController : BridgeControllerBase
{
    private readonly PlaybackResolver _resolver;
    private readonly ILibraryManager _library;
    private readonly SeasonSettingsStore _seasonSettings;

    /// <summary>Initializes a new instance of the <see cref="PlaybackController"/> class.</summary>
    /// <param name="resolver">播放解析器。</param>
    /// <param name="library">媒体库。</param>
    /// <param name="seasonSettings">季配置。</param>
    public PlaybackController(PlaybackResolver resolver, ILibraryManager library, SeasonSettingsStore seasonSettings)
    {
        _resolver = resolver;
        _library = library;
        _seasonSettings = seasonSettings;
    }

    /// <summary>
    /// 解析「点的是谁 → 播哪一集 → 整季列表 → 从哪开始」。
    /// </summary>
    /// <param name="request">请求体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>播放计划或错误。</returns>
    [Authorize]
    [HttpPost("Resolve")]
    public async Task<IActionResult> Resolve([FromBody] ResolveRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || !Guid.TryParse(request.ItemId, out var itemId))
        {
            return ErrorResult(400, "请求里缺少合法的 itemId。", "bad-item-id");
        }

        var intent = ParseIntent(request.Intent);
        if (intent is null)
        {
            return ErrorResult(400, "intent 只能是 item / resume / restart。", "bad-intent");
        }

        var userId = CurrentUserId;
        if (userId is null)
        {
            return ErrorResult(401, "登录状态已失效，请刷新页面后重新登录。", "no-user");
        }

        try
        {
            var plan = await _resolver.ResolveAsync(userId.Value, itemId, intent.Value, cancellationToken).ConfigureAwait(false);
            return JsonPayload(plan);
        }
        catch (BridgeException ex)
        {
            return ErrorResult(ex.StatusCode, ex.Message, ex.Code);
        }
    }

    /// <summary>
    /// 返回 item 的类型与所属剧/季，注入脚本据此决定要不要显示季配置面板。
    /// </summary>
    /// <param name="itemId">item ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>item 信息或错误。</returns>
    [Authorize]
    [HttpGet("ItemInfo")]
    public async Task<IActionResult> ItemInfo([FromQuery] string? itemId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(itemId, out var id))
        {
            return ErrorResult(400, "itemId 不是合法的 GUID。", "bad-item-id");
        }

        var item = _library.GetItemById(id);
        if (item is null)
        {
            return ErrorResult(404, "找不到该项目。", "item-not-found");
        }

        var response = new ItemInfoResponse
        {
            ItemId = id.ToString("N"),
            ItemType = item.GetType().Name,
            Name = item.Name
        };

        switch (item)
        {
            case Episode episode:
            {
                var season = episode.SeasonId == Guid.Empty ? null : _library.GetItemById(episode.SeasonId) as Season;
                var series = episode.SeriesId == Guid.Empty ? null : _library.GetItemById(episode.SeriesId) as Series;

                response.ItemType = "Episode";
                response.SeasonId = season?.Id.ToString("N")
                                    ?? (episode.SeasonId == Guid.Empty ? null : episode.SeasonId.ToString("N"));
                response.SeasonName = season?.Name ?? episode.SeasonName;
                response.SeriesId = series?.Id.ToString("N")
                                    ?? (episode.SeriesId == Guid.Empty ? null : episode.SeriesId.ToString("N"));
                response.SeriesName = series?.Name ?? episode.SeriesName;
                response.SeasonNumber = episode.ParentIndexNumber;
                response.EpisodeNumber = episode.IndexNumber;
                response.CanConfigure = response.SeasonId is not null;

                await FillSeasonSettingsAsync(response, season?.Id ?? episode.SeasonId, cancellationToken).ConfigureAwait(false);
                break;
            }

            case Season season:
            {
                var series = season.SeriesId == Guid.Empty ? null : _library.GetItemById(season.SeriesId) as Series;

                response.ItemType = "Season";
                response.SeasonId = season.Id.ToString("N");
                response.SeasonName = season.Name;
                response.SeriesId = series?.Id.ToString("N")
                                    ?? (season.SeriesId == Guid.Empty ? null : season.SeriesId.ToString("N"));
                response.SeriesName = series?.Name ?? season.SeriesName;
                response.SeasonNumber = season.IndexNumber;
                response.CanConfigure = true;

                await FillSeasonSettingsAsync(response, season.Id, cancellationToken).ConfigureAwait(false);
                break;
            }

            case Series series:
            {
                response.ItemType = "Series";
                response.SeriesId = series.Id.ToString("N");
                response.SeriesName = series.Name;
                break;
            }

            case Movie:
            {
                response.ItemType = "Movie";
                break;
            }
        }

        return JsonPayload(response);
    }

    private async Task FillSeasonSettingsAsync(ItemInfoResponse response, Guid seasonId, CancellationToken cancellationToken)
    {
        if (seasonId == Guid.Empty)
        {
            return;
        }

        var settings = await _seasonSettings.GetAsync(seasonId, cancellationToken).ConfigureAwait(false);
        response.IntroSeconds = settings?.IntroSeconds ?? 0;
        response.OutroSeconds = settings?.OutroSeconds ?? 0;
    }

    private static PlaybackIntent? ParseIntent(string? value)
    {
        return value?.Trim().ToLowerInvariant() switch
        {
            null or "" or "item" => PlaybackIntent.Item,
            "resume" => PlaybackIntent.Resume,
            "restart" => PlaybackIntent.Restart,
            _ => null
        };
    }
}
