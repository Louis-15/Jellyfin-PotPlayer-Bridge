using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.PotPlayerBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PotPlayerBridge.Api;

/// <summary>
/// 季级片头片尾配置接口。
/// 存储粒度是「季」：从单集页进入时，读写的是该集所属季的配置（PLAN 5.6）。
/// 按需求 D-05，任何已登录用户都可以改，不做按用户隔离。
/// </summary>
[ApiController]
[Route("PotPlayerBridge")]
public sealed class SeasonSettingsController : BridgeControllerBase
{
    private readonly ILibraryManager _library;
    private readonly SeasonSettingsStore _store;

    /// <summary>Initializes a new instance of the <see cref="SeasonSettingsController"/> class.</summary>
    /// <param name="library">媒体库。</param>
    /// <param name="store">季配置存储。</param>
    public SeasonSettingsController(ILibraryManager library, SeasonSettingsStore store)
    {
        _library = library;
        _store = store;
    }

    /// <summary>读取某一季（或某一集所属季）当前生效的配置。</summary>
    /// <param name="itemId">季 ID 或单集 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>配置 JSON。</returns>
    [Authorize]
    [HttpGet("SeasonSettings")]
    public async Task<IActionResult> Get([FromQuery] string? itemId, CancellationToken cancellationToken)
    {
        try
        {
            var target = ResolveSeason(itemId);
            var settings = await _store.GetAsync(target.Season.Id, cancellationToken).ConfigureAwait(false);
            return JsonPayload(BuildResponse(target, settings));
        }
        catch (BridgeException ex)
        {
            return ErrorResult(ex.StatusCode, ex.Message, ex.Code);
        }
    }

    /// <summary>保存某一季（或某一集所属季）的片头片尾秒数。</summary>
    /// <param name="request">请求体。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>保存后的配置 JSON。</returns>
    [Authorize]
    [HttpPost("SeasonSettings")]
    public async Task<IActionResult> Save([FromBody] SeasonSettingsRequest? request, CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return ErrorResult(400, "请求体为空。", "empty-body");
        }

        try
        {
            var target = ResolveSeason(request.ItemId);
            var settings = await _store
                .SaveAsync(target.Season.Id, request.IntroSeconds, request.OutroSeconds, cancellationToken)
                .ConfigureAwait(false);

            return JsonPayload(BuildResponse(target, settings));
        }
        catch (BridgeException ex)
        {
            return ErrorResult(ex.StatusCode, ex.Message, ex.Code);
        }
    }

    private static object BuildResponse(SeasonTarget target, SeasonSettings? settings)
    {
        return new
        {
            itemId = target.RequestedId.ToString("N"),
            seasonId = target.Season.Id.ToString("N"),
            seasonName = target.Season.Name,
            seasonNumber = target.Season.IndexNumber,
            seriesId = target.Season.SeriesId == Guid.Empty ? null : target.Season.SeriesId.ToString("N"),
            seriesName = target.Season.SeriesName,
            episodeId = target.Episode?.Id.ToString("N"),
            introSeconds = settings?.IntroSeconds ?? 0,
            outroSeconds = settings?.OutroSeconds ?? 0,
            updatedAt = settings?.UpdatedAt,
            canConfigure = true
        };
    }

    /// <summary>
    /// 把请求里的 itemId 换算成「要编辑的季」。
    /// 单集、季都允许；其它类型明确拒绝，避免用户误以为可以给整部剧设置片头。
    /// </summary>
    private SeasonTarget ResolveSeason(string? itemId)
    {
        if (!Guid.TryParse(itemId, out var id))
        {
            throw new BridgeException(400, "itemId 不是合法的 GUID。", "bad-item-id");
        }

        var item = _library.GetItemById(id)
                   ?? throw new BridgeException(404, "找不到该项目。", "item-not-found");

        switch (item)
        {
            case Season season:
                return new SeasonTarget(id, season, null);

            case Episode episode:
            {
                if (episode.SeasonId == Guid.Empty)
                {
                    throw new BridgeException(400, "这一集没有所属季，无法配置片头片尾。", "no-season");
                }

                var season = _library.GetItemById(episode.SeasonId) as Season
                             ?? throw new BridgeException(404, "这一集所属的季已被删除。", "season-not-found");

                return new SeasonTarget(id, season, episode);
            }

            default:
                throw new BridgeException(400, "只有季和单集可以配置片头片尾。", "unsupported-type");
        }
    }

    private sealed record SeasonTarget(Guid RequestedId, Season Season, Episode? Episode);
}
