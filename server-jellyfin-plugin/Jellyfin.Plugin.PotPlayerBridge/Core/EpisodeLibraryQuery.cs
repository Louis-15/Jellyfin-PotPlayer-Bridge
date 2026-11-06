using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.PotPlayerBridge.Core;

/// <summary>
/// 剧集层级查询的薄封装。
/// 单独成类的原因：
/// <list type="bullet">
/// <item>12.2.0 的 InternalItemsQuery 行为有坑（只给 ParentId 查不到季下的集），集中在一处便于记录和修正；</item>
/// <item>让 <see cref="PlaybackResolver"/> 只关心「选哪一集、怎么组计划」，不被查询细节淹没。</item>
/// </list>
/// </summary>
public sealed class EpisodeLibraryQuery
{
    private readonly ILibraryManager _library;

    /// <summary>Initializes a new instance of the <see cref="EpisodeLibraryQuery"/> class.</summary>
    /// <param name="library">媒体库。</param>
    public EpisodeLibraryQuery(ILibraryManager library)
    {
        _library = library;
    }

    /// <summary>取一部剧的全部集，按季号、集号升序。</summary>
    /// <param name="series">剧。</param>
    /// <returns>全部集。</returns>
    public List<Episode> GetSeriesEpisodes(Series series)
    {
        var items = _library.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            AncestorIds = new[] { series.Id },
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsMissing = false,
            OrderBy = new[]
            {
                (ItemSortBy.ParentIndexNumber, SortOrder.Ascending),
                (ItemSortBy.IndexNumber, SortOrder.Ascending)
            }
        });

        return items
            .OfType<Episode>()
            .OrderBy(e => e.ParentIndexNumber ?? int.MaxValue)
            .ThenBy(e => e.IndexNumber ?? int.MaxValue)
            .ToList();
    }

    /// <summary>取一季的全部集，按集号升序。</summary>
    /// <param name="season">季。</param>
    /// <returns>该季全部集。</returns>
    public List<Episode> GetSeasonEpisodes(Season season)
    {
        // 实测 12.2.0：只给 ParentId 的查询返回空（查询构建器对非递归查询另有语义），
        // 必须用 AncestorIds + Recursive 才能稳定拿到整季全集。
        var items = _library.GetItemList(new InternalItemsQuery
        {
            Recursive = true,
            AncestorIds = new[] { season.Id },
            IncludeItemTypes = new[] { BaseItemKind.Episode },
            IsMissing = false,
            OrderBy = new[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
        });

        return items
            .OfType<Episode>()
            .OrderBy(e => e.IndexNumber ?? int.MaxValue)
            .ToList();
    }

    /// <summary>按 ID 取季。</summary>
    /// <param name="seasonId">季 ID。</param>
    /// <returns>季或 null。</returns>
    public Season? GetSeason(Guid seasonId)
    {
        return seasonId == Guid.Empty ? null : _library.GetItemById(seasonId) as Season;
    }

    /// <summary>按 ID 取剧。</summary>
    /// <param name="seriesId">剧 ID。</param>
    /// <returns>剧或 null。</returns>
    public Series? GetSeries(Guid seriesId)
    {
        return seriesId == Guid.Empty ? null : _library.GetItemById(seriesId) as Series;
    }
}
