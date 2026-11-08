using System;
using System.Collections.Generic;
using PotPlayerBridge.Core;
using Xunit;

namespace PotPlayerBridge.Tests;

/// <summary>
/// 代理侧进度状态机的测试（PLAN 5.7 / 验收项 A1 / A2 / A4 / A5 / H1 / H3）。
/// 最重要的一条：**假续播时绝不能 armed** —— 一旦 armed 就会把服务器上 20:00 的进度
/// 覆盖成「从头播了几分钟」，这是用户最难察觉、也最心疼的数据损坏。
/// </summary>
public class PlaybackProgressStateTests
{
    private const long Start1200s = 1_200_000;
    private const long DurationMs = 2_739_128;

    private static PlaybackPlan Plan(long startPositionTicks, int startIndex = 2)
    {
        var plan = new PlaybackPlan
        {
            PlaySessionId = "session-1",
            ItemId = "item-1",
            ItemType = "Episode",
            Title = "测试剧 第1季",
            PlaylistName = "测试剧 第1季",
            StartIndex = startIndex,
            StartPositionTicks = startPositionTicks,
            Playlist = new List<PlaylistEntry>
            {
                new() { ItemId = "item-1", Title = "测试剧 S01E01 第1集", IndexNumber = 1 },
                new() { ItemId = "item-2", Title = "测试剧 S01E02 第2集", IndexNumber = 2 },
                new() { ItemId = "item-3", Title = "测试剧 S01E03 第3集", IndexNumber = 3 }
            }
        };

        return plan;
    }

    private static string TitleFor(int episode) => $"测试剧 S01E0{episode} 第{episode}集 - PotPlayer";

    private static PlaybackSample Sample(int episode, long positionMs, int status = 2, long durationMs = DurationMs)
    {
        return new PlaybackSample(TitleFor(episode), positionMs, durationMs, status);
    }

    [Fact]
    public void Fresh_Playback_From_Zero_Arms_Immediately()
    {
        // expectedStart = 0，位置在 5 秒容差内 → 第一次采样就 armed。
        var state = new PlaybackProgressState(Plan(0, startIndex: 1));

        state.Feed(Sample(1, 200), out var signal);

        Assert.True(state.Armed);
        Assert.True(signal.Armed);
        Assert.Equal("item-1", signal.ItemId);
    }

    [Fact]
    public void Resume_At_Expected_Position_Arms()
    {
        // A2：手机播到 20:00，电脑点开 → 续播跳转生效，位置落在期望点附近。
        var state = new PlaybackProgressState(Plan(Start1200s * TimeSpan.TicksPerMillisecond));

        state.Feed(Sample(2, Start1200s + 800), out var signal);

        Assert.True(state.Armed);
        Assert.Equal("item-2", signal.ItemId);
    }

    [Fact]
    public void Fake_Resume_Never_Arms()
    {
        // A4 的核心：服务器说从 20:00 续播，播放器却从头开始。
        // 位置一直在增长也不能 armed —— 否则「播了几分钟就关」会把 20:00 冲成 3 分钟。
        var state = new PlaybackProgressState(Plan(Start1200s * TimeSpan.TicksPerMillisecond));

        for (var second = 1; second <= 300; second++)
        {
            state.Feed(Sample(2, second * 1000L), out _);

            Assert.False(state.Armed);
        }
    }

    [Fact]
    public void Growing_From_Zero_Arms_When_Expected_Is_Zero()
    {
        // 期望位置是 0 但第一秒还没出画面（位置仍是 0）：靠「连续增长」补上。
        var state = new PlaybackProgressState(Plan(0, startIndex: 1));

        state.Feed(Sample(1, 0), out _);
        Assert.False(state.Armed);

        for (var i = 1; i <= PlaybackProgressState.GrowingSamplesRequired; i++)
        {
            state.Feed(Sample(1, i * 500L), out _);
        }

        Assert.True(state.Armed);
    }

    [Fact]
    public void Episode_Change_Is_Detected_And_ReArms_From_Zero()
    {
        // 自动连播到下一集：标题变化 + 位置回到 0。
        var state = new PlaybackProgressState(Plan(0, startIndex: 1));

        state.Feed(Sample(1, 1000), out _);
        Assert.True(state.Armed);

        var change = state.Feed(Sample(2, 500), out var signal);

        Assert.Equal(PlaybackChange.EpisodeChanged, change);
        Assert.Equal(2, state.Index);

        // 新的一集位置在 0 附近 → 立刻重新 armed，不会因为「重置」丢掉后续进度。
        Assert.True(state.Armed);
        Assert.Equal("item-2", signal.ItemId);
    }

    [Fact]
    public void Position_Jump_With_Same_Duration_Is_Reported_As_Seek()
    {
        // A5：用户拖动进度条。位置大跳但时长没变（同一集）。
        var state = new PlaybackProgressState(Plan(0, startIndex: 1));

        // 第一次采样落在期望起播位置（0）的容差内 → 立刻 armed。
        state.Feed(Sample(1, 1000), out _);
        state.Feed(Sample(1, 2000), out _);
        Assert.True(state.Armed);

        var change = state.Feed(Sample(1, 900_000), out var signal);

        Assert.Equal(PlaybackChange.Seek, change);
        Assert.True(signal.Seeked);
        Assert.Equal(900_000, signal.PositionMs);
    }

    [Fact]
    public void Position_Drop_Back_To_Zero_Is_Seek_Not_Episode_Change()
    {
        // 用户拖回开头：标题没变，所以算拖动，记录拖动后的位置（A5）。
        var state = new PlaybackProgressState(Plan(0, startIndex: 1));

        state.Feed(Sample(1, 1000), out _);
        Assert.True(state.Armed);

        // 先看到 10 分钟处，再拖回开头。
        state.Feed(Sample(1, 600_000), out _);

        var change = state.Feed(Sample(1, 0), out var signal);

        Assert.Equal(PlaybackChange.Seek, change);
        Assert.Equal(0, signal.PositionMs);
    }

    [Fact]
    public void Pause_And_Resume_Produce_Their_Own_Changes()
    {
        var state = new PlaybackProgressState(Plan(0, startIndex: 1));

        state.Feed(Sample(1, 1000), out _);

        var paused = state.Feed(Sample(1, 1000, status: 1), out var pausedSignal);
        Assert.Equal(PlaybackChange.Pause, paused);
        Assert.True(pausedSignal.IsPaused);

        var resumed = state.Feed(Sample(1, 1000, status: 2), out var resumedSignal);
        Assert.Equal(PlaybackChange.Resume, resumed);
        Assert.False(resumedSignal.IsPaused);
    }

    [Fact]
    public void Playing_To_Idle_Is_Reported_As_Ended()
    {
        var state = new PlaybackProgressState(Plan(0, startIndex: 1));

        state.Feed(Sample(1, DurationMs - 3000), out _);

        var change = state.Feed(Sample(1, 0, status: 0), out var signal);

        Assert.Equal(PlaybackChange.Ended, change);
        Assert.True(signal.Ended);
        Assert.True(signal.Armed);

        // 播完那一刻位置会读成 0，上报的必须是「上一秒还在哪儿」，否则会被写成进度 0。
        Assert.Equal(DurationMs - 3000, signal.PositionMs);
    }

    [Fact]
    public void Closing_The_Player_In_The_Middle_Is_Not_Ended()
    {
        // 用户看到 25 分钟就关播放器：PotPlayer 的状态同样从 2 掉到 0，
        // 但这一集远没看完，绝不能标记成已看完（否则用户再也找不到进度）。
        var state = new PlaybackProgressState(Plan(0, startIndex: 1));

        state.Feed(Sample(1, 1_500_000), out _);

        var change = state.Feed(Sample(1, 0, status: 0), out var signal);

        Assert.NotEqual(PlaybackChange.Ended, change);
        Assert.False(signal.Ended);
    }

    [Fact]
    public void Position_Reading_Zero_On_Stop_Falls_Back_To_Last_Known_Position()
    {
        // 关闭瞬间 0x5004 读成 0，且这一集已经 armed：如果照实上报 0，
        // 服务器会按「用户拖回开头」接受，进度就此清零。
        var state = new PlaybackProgressState(Plan(0, startIndex: 1));

        // 先正常播到 25 分钟（第一次采样落在起播点容差内 → armed）。
        state.Feed(Sample(1, 1000), out _);
        state.Feed(Sample(1, 1_500_000), out _);
        Assert.True(state.Armed);

        // 关闭过程中读到 0（窗口还在）。
        state.Feed(Sample(1, 0, status: 0), out _);

        // 窗口消失后的停止上报。
        var signal = state.Snapshot(new PlaybackSample("PotPlayer", 0, 0, -1), stopped: true);

        Assert.Equal(1_500_000, signal.PositionMs);
    }

    [Fact]
    public void Stopped_Snapshot_Keeps_Last_Known_Position()
    {
        // 用户直接关播放器：窗口销毁后读不到位置，必须用最后一次采到的值上报（A1 / H3）。
        var state = new PlaybackProgressState(Plan(0, startIndex: 1));

        state.Feed(Sample(1, 754_000), out _);

        var signal = state.Snapshot(new PlaybackSample("PotPlayer", 0, 0, -1), stopped: true);

        Assert.Equal(754_000, signal.PositionMs);
        Assert.Equal(DurationMs, signal.DurationMs);
        Assert.True(signal.Stopped);
        Assert.False(signal.IsPaused);
    }

    [Fact]
    public void Title_Matching_Prefers_The_Longest_Candidate()
    {
        // S01E01 是 S01E010 的前缀：必须取最长匹配，否则会把第 10 集认成第 1 集。
        var plan = Plan(0, startIndex: 1);
        plan.Playlist[0].Title = "S01E01";
        plan.Playlist[1].Title = "S01E010 加长版";

        var state = new PlaybackProgressState(plan);
        state.Feed(new PlaybackSample("S01E010 加长版 - PotPlayer", 1000, DurationMs, 2), out var signal);

        Assert.Equal(2, signal.Index);
        Assert.Equal("item-2", signal.ItemId);
    }

    [Fact]
    public void Unknown_Title_Keeps_Current_Item()
    {
        // PotPlayer 还没开始播放时标题是「xxx.dpl - PotPlayer」，匹配不上任何一集。
        var state = new PlaybackProgressState(Plan(0, startIndex: 2));

        state.Feed(new PlaybackSample("测试剧 第1季.dpl - PotPlayer", 0, 0, -1), out var signal);

        Assert.Equal(2, signal.Index);
        Assert.False(signal.Armed);
    }

    [Fact]
    public void Single_Item_Mode_Matches_Without_Title_And_Still_Arms()
    {
        // 电影单集直启：PotPlayer 的标题是视频文件名（PFES-107.mp4），
        // 与 Jellyfin 标题不同源，按标题匹配必然失败。
        // 这时必须直接认定为起播项，否则整场电影的进度都不会上报。
        var plan = Plan(0, startIndex: 1);
        plan.ItemType = "Movie";
        plan.Playlist.RemoveRange(1, 2);
        plan.Playlist[0].Title = "PFES-107 很长的日文标题";

        var state = new PlaybackProgressState(plan, singleItemMode: true);
        state.Feed(new PlaybackSample("PFES-107.mp4 - PotPlayer", 300, DurationMs, 2), out var signal);

        Assert.Equal(1, signal.Index);
        Assert.Equal("item-1", signal.ItemId);
        Assert.True(signal.Armed);
    }

    [Fact]
    public void Later_Episodes_Start_At_Intro_Seconds()
    {
        // 用户点的第 2 集从续播点 20:00 开始；自动连播到第 3 集时应从片头 120 秒开始，
        // 否则会出现「点网页播放的那一集跳片头、连播的下一集不跳」。
        var plan = Plan(Start1200s * TimeSpan.TicksPerMillisecond);
        plan.IntroSeconds = 120;

        var state = new PlaybackProgressState(plan);

        Assert.Equal(Start1200s, state.ExpectedStartMs(2));
        Assert.Equal(120_000, state.ExpectedStartMs(1));
        Assert.Equal(120_000, state.ExpectedStartMs(3));
    }

    [Fact]
    public void Later_Episodes_Start_At_Zero_When_Intro_Not_Configured()
    {
        // 未配置片头的季：连播的下一集仍从 0 开始，不能凭空跳（D5）。
        var plan = Plan(0, startIndex: 1);
        plan.IntroSeconds = 0;

        var state = new PlaybackProgressState(plan);

        Assert.Equal(0, state.ExpectedStartMs(2));
        Assert.Equal(0, state.ExpectedStartMs(3));
    }

    [Fact]
    public void Episode_Start_Flag_Resets_On_Episode_Change()
    {
        // 「新一集开始」这条广播每条集只能发一次，否则每 5 秒都会刷网页。
        // 换集之后必须重新允许发一次，否则自动连播过来的新集不会广播，
        // 网页上会一直显示上一集的进度（实测问题）。
        var plan = Plan(0, startIndex: 1);
        var state = new PlaybackProgressState(plan);

        state.Feed(Sample(1, 200), out _);
        Assert.False(state.EpisodeStartReported);

        state.NotifyEpisodeStartReported();
        Assert.True(state.EpisodeStartReported);

        state.Feed(Sample(2, 200), out var signal);
        Assert.Equal(2, signal.Index);
        Assert.False(state.EpisodeStartReported);
    }
}
