using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.PotPlayerBridge.Core;
using Xunit;

namespace Jellyfin.Plugin.PotPlayerBridge.Tests;

/// <summary>
/// PlaybackPlanner 的规则测试。
/// 这些规则直接对应验收项（B1/B2/B3/B4/B7/B8/D3/D7），改动前请先看 PLAN 第 4、5 节。
/// </summary>
public class PlaybackPlannerTests
{
    private static EpisodeSnapshot Episode(
        int id,
        int? season,
        int? number,
        long positionTicks = 0,
        bool played = false,
        DateTime? lastPlayed = null)
    {
        return new EpisodeSnapshot(
            new Guid(id, 0, 0, new byte[8]),
            season,
            number,
            positionTicks,
            played,
            lastPlayed,
            $"S{season:00}E{number:00}",
            27_000_000_000);
    }

    [Fact]
    public void SortPlaylist_OrdersBySeasonThenEpisode_AndPutsUnnumberedLast()
    {
        var playlist = PlaybackPlanner.SortPlaylist(new[]
        {
            Episode(5, 1, null),
            Episode(4, 2, 1),
            Episode(3, 1, 3),
            Episode(2, 1, 1),
            Episode(1, null, null)
        });

        Assert.Equal(
            new (int?, int?)[] { (1, 1), (1, 3), (1, null), (2, 1), (null, null) },
            playlist.Select(e => (e.SeasonNumber, e.EpisodeNumber)).ToArray());
    }

    [Fact]
    public void SortPlaylist_PutsEpisodesInAscendingOrder()
    {
        var playlist = PlaybackPlanner.SortPlaylist(new[]
        {
            Episode(1, 1, 3),
            Episode(2, 1, 1),
            Episode(3, 1, 2)
        });

        Assert.Equal(new[] { 1, 2, 3 }, playlist.Select(e => e.EpisodeNumber!.Value).ToArray());
    }

    [Fact]
    public void PickMostRecentlyPlayed_UsesLastPlayedDate_NotEpisodeNumber()
    {
        // 第 4 集比第 2 集更近看过，必须选第 4 集（B1 的「以时间排序」要求）。
        var chosen = PlaybackPlanner.PickMostRecentlyPlayed(new[]
        {
            Episode(2, 1, 2, 10_000_000, lastPlayed: new DateTime(2026, 11, 1)),
            Episode(4, 1, 4, 14_630_000_000, lastPlayed: new DateTime(2026, 11, 5)),
            Episode(3, 1, 3, 5_000_000, lastPlayed: new DateTime(2026, 10, 30))
        });

        Assert.NotNull(chosen);
        Assert.Equal(4, chosen!.EpisodeNumber);
    }

    [Fact]
    public void PickMostRecentlyPlayed_IgnoresFinishedAndZeroProgress()
    {
        var chosen = PlaybackPlanner.PickMostRecentlyPlayed(new[]
        {
            Episode(1, 1, 1, 0, lastPlayed: new DateTime(2026, 11, 5)),
            Episode(2, 1, 2, 20_000_000, played: true, lastPlayed: new DateTime(2026, 11, 5)),
            Episode(3, 1, 3, 5_000_000, lastPlayed: new DateTime(2026, 11, 2))
        });

        Assert.NotNull(chosen);
        Assert.Equal(3, chosen!.EpisodeNumber);
    }

    [Fact]
    public void PickMostRecentlyPlayed_ReturnsNullWhenNothingInProgress()
    {
        var chosen = PlaybackPlanner.PickMostRecentlyPlayed(new[]
        {
            Episode(1, 1, 1),
            Episode(2, 1, 2, 20_000_000, played: true)
        });

        Assert.Null(chosen);
    }

    [Fact]
    public void PickMostRecentlyPlayed_FallsBackToEpisodeOrderWhenLastPlayedIsNull()
    {
        var chosen = PlaybackPlanner.PickMostRecentlyPlayed(new[]
        {
            Episode(3, 1, 3, 5_000_000),
            Episode(1, 1, 1, 5_000_000)
        });

        // 全都没有 LastPlayedDate 时，按季/集号兜底，取更靠前的一集。
        Assert.NotNull(chosen);
        Assert.Equal(1, chosen!.EpisodeNumber);
    }

    [Fact]
    public void PickFirstEpisode_PrefersRegularSeasonOverSpecials()
    {
        var first = PlaybackPlanner.PickFirstEpisode(new[]
        {
            Episode(1, 0, 1),
            Episode(2, 1, 1),
            Episode(3, 1, 2)
        });

        Assert.NotNull(first);
        Assert.Equal(1, first!.SeasonNumber);
        Assert.Equal(1, first.EpisodeNumber);
    }

    [Fact]
    public void PickFirstEpisode_FallsBackToSpecialsWhenOnlySpecialsExist()
    {
        var first = PlaybackPlanner.PickFirstEpisode(new[]
        {
            Episode(1, 0, 1),
            Episode(2, 0, 2)
        });

        Assert.NotNull(first);
        Assert.Equal(0, first!.SeasonNumber);
        Assert.Equal(1, first.EpisodeNumber);
    }

    [Fact]
    public void PickFirstEpisode_ReturnsNullForEmptyList()
    {
        Assert.Null(PlaybackPlanner.PickFirstEpisode(Array.Empty<EpisodeSnapshot>()));
    }

    [Fact]
    public void ComputeStartPosition_SkipsIntro_WhenStartingFromZero()
    {
        var ticks = PlaybackPlanner.ComputeStartPositionTicks(0, 90);

        Assert.Equal(90L * TimeSpan.TicksPerSecond, ticks);
    }

    [Fact]
    public void ComputeStartPosition_JumpsToIntroEnd_WhenResumeIsInsideIntro()
    {
        var ticks = PlaybackPlanner.ComputeStartPositionTicks(30L * TimeSpan.TicksPerSecond, 90);

        Assert.Equal(90L * TimeSpan.TicksPerSecond, ticks);
    }

    [Fact]
    public void ComputeStartPosition_KeepsResume_WhenResumeIsAfterIntro()
    {
        // D7：续播位置已经在片头之后时，不能往回跳。
        var ticks = PlaybackPlanner.ComputeStartPositionTicks(1463L * TimeSpan.TicksPerSecond, 90);

        Assert.Equal(1463L * TimeSpan.TicksPerSecond, ticks);
    }

    [Fact]
    public void ComputeStartPosition_ReturnsResume_WhenNoIntroConfigured()
    {
        var ticks = PlaybackPlanner.ComputeStartPositionTicks(1463L * TimeSpan.TicksPerSecond, 0);

        Assert.Equal(1463L * TimeSpan.TicksPerSecond, ticks);
    }

    [Fact]
    public void ComputeStartPosition_ReturnsZero_WhenNoIntroAndNoResume()
    {
        Assert.Equal(0, PlaybackPlanner.ComputeStartPositionTicks(0, 0));
    }

    [Fact]
    public void FindStartIndex_ReturnsOneBasedIndex()
    {
        var playlist = new List<EpisodeSnapshot>
        {
            Episode(1, 1, 1),
            Episode(2, 1, 2),
            Episode(3, 1, 3)
        };

        Assert.Equal(3, PlaybackPlanner.FindStartIndex(playlist, playlist[2].ItemId));
    }

    [Fact]
    public void FindStartIndex_ReturnsOne_WhenTargetIsMissing()
    {
        var playlist = new List<EpisodeSnapshot> { Episode(1, 1, 1) };

        Assert.Equal(1, PlaybackPlanner.FindStartIndex(playlist, Guid.NewGuid()));
    }
}
