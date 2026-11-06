using System;
using System.Collections.Generic;
using System.Linq;

namespace Jellyfin.Plugin.PotPlayerBridge.Core;

/// <summary>
/// 播放计划里「候选集」的纯数据快照。把 Jellyfin 的实体查询隔离在
/// <see cref="PlaybackResolver"/> 里，这里只保留可单元测试的排序/挑选规则。
/// </summary>
/// <param name="ItemId">条目 ID。</param>
/// <param name="SeasonNumber">季号（电影/无编号时为 null）。</param>
/// <param name="EpisodeNumber">集号（电影/无编号时为 null）。</param>
/// <param name="PlaybackPositionTicks">用户进度（ticks）。</param>
/// <param name="Played">是否已标记为看完。</param>
/// <param name="LastPlayedDate">最近播放时间（Jellyfin 的 UserData.LastPlayedDate）。</param>
/// <param name="Title">展示标题。</param>
/// <param name="RuntimeTicks">时长（ticks）。</param>
public sealed record EpisodeSnapshot(
    Guid ItemId,
    int? SeasonNumber,
    int? EpisodeNumber,
    long PlaybackPositionTicks,
    bool Played,
    DateTime? LastPlayedDate,
    string Title,
    long RuntimeTicks);

/// <summary>
/// 挑选起播集与计算起播位置的纯逻辑。
/// 这里的所有规则都对应需求里的明确条目（B1-B10 / D3 / D7），改动前请对照 PLAN 第 4、5 节。
/// </summary>
public static class PlaybackPlanner
{
    /// <summary>
    /// 按「季号、集号」升序排列，作为整季播放列表的顺序。
    /// 编号为空的项排在最后，避免把没编号的花絮插到正片中间。
    /// </summary>
    /// <param name="episodes">候选集。</param>
    /// <returns>排序后的列表。</returns>
    public static IReadOnlyList<EpisodeSnapshot> SortPlaylist(IEnumerable<EpisodeSnapshot> episodes)
    {
        return episodes
            .OrderBy(e => e.SeasonNumber ?? int.MaxValue)
            .ThenBy(e => e.EpisodeNumber ?? int.MaxValue)
            .ThenBy(e => e.ItemId)
            .ToList();
    }

    /// <summary>
    /// 「最近看的那一集」：只看有进度且未看完的集，按 LastPlayedDate 倒序。
    /// 不能用集号推断——用户可能跳着看，最近看的可能是第 4 集而不是第 2 集。
    /// </summary>
    /// <param name="episodes">候选集。</param>
    /// <returns>最近有进度的一集；没有则返回 null。</returns>
    public static EpisodeSnapshot? PickMostRecentlyPlayed(IEnumerable<EpisodeSnapshot> episodes)
    {
        var candidates = episodes
            .Where(e => e.PlaybackPositionTicks > 0 && !e.Played)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        // LastPlayedDate 为空的老数据排在最后，再按季/集号兜底，保证结果稳定。
        return candidates
            .OrderByDescending(e => e.LastPlayedDate ?? DateTime.MinValue)
            .ThenBy(e => e.SeasonNumber ?? int.MaxValue)
            .ThenBy(e => e.EpisodeNumber ?? int.MaxValue)
            .First();
    }

    /// <summary>
    /// 没有任何进度时的兜底：第一季第一集（特别篇 Season 0 只有在没有正片时才选）。
    /// </summary>
    /// <param name="episodes">候选集。</param>
    /// <returns>第一集；候选为空时返回 null。</returns>
    public static EpisodeSnapshot? PickFirstEpisode(IEnumerable<EpisodeSnapshot> episodes)
    {
        var ordered = SortPlaylist(episodes);
        if (ordered.Count == 0)
        {
            return null;
        }

        return ordered.FirstOrDefault(e => e.SeasonNumber is > 0) ?? ordered[0];
    }

    /// <summary>
    /// 计算起播位置：续播点落在片头里时跳到片头结束，已经在片头之后时不能往回跳（D7）。
    /// 从 0 开始播放时同样跳片头（D-04：重播不等于不跳片头）。
    /// </summary>
    /// <param name="resumeTicks">用户续播位置（ticks），从头播传 0。</param>
    /// <param name="introSeconds">该季配置的片头秒数，0 表示不跳。</param>
    /// <returns>实际起播位置（ticks）。</returns>
    public static long ComputeStartPositionTicks(long resumeTicks, int introSeconds)
    {
        var resume = Math.Max(0, resumeTicks);
        if (introSeconds <= 0)
        {
            return resume;
        }

        var introTicks = introSeconds * (long)TimeSpan.TicksPerSecond;
        return resume > introTicks ? resume : introTicks;
    }

    /// <summary>
    /// 在播放列表里找目标项的 1 开始序号（对应 DPL 的 topindex）。
    /// 找不到时返回 1：宁可从第一项开始播，也不要让整次播放失败。
    /// </summary>
    /// <param name="playlist">已排序的播放列表。</param>
    /// <param name="targetItemId">目标项 ID。</param>
    /// <returns>1 开始的序号。</returns>
    public static int FindStartIndex(IReadOnlyList<EpisodeSnapshot> playlist, Guid targetItemId)
    {
        for (var i = 0; i < playlist.Count; i++)
        {
            if (playlist[i].ItemId == targetItemId)
            {
                return i + 1;
            }
        }

        return 1;
    }
}
