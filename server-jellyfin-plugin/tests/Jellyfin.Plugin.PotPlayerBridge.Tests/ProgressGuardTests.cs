using System;
using Jellyfin.Plugin.PotPlayerBridge.Core;
using Xunit;

namespace Jellyfin.Plugin.PotPlayerBridge.Tests;

/// <summary>
/// 进度守卫的规则测试（PLAN 5.7 / 验收项 A3 / A4 / A5 / H1 / H3）。
/// 这些断言是「用户的观看记录不会被写坏」的最后一道保险，
/// 任何一条被改坏都意味着某天会有人发现自己的进度被清零了。
/// </summary>
public class ProgressGuardTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    /// <summary>可以手动拨动的时钟，用来测会话过期。</summary>
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 7, 4, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;

        public void Advance(TimeSpan span) => Now += span;
    }

    private static readonly Guid User = Guid.Parse("11111111111111111111111111111111");

    private static ProgressRequest Request(
        long positionTicks,
        long durationTicks = 27_391_280_000,
        bool armed = true,
        bool stopped = false,
        string session = "s1",
        string item = "970d910847fe30222a747d922c433214")
    {
        return new ProgressRequest
        {
            PlaySessionId = session,
            ItemId = item,
            PositionTicks = positionTicks,
            DurationTicks = durationTicks,
            Armed = armed,
            Stopped = stopped
        };
    }

    [Fact]
    public void Negative_Position_Is_Rejected()
    {
        var decision = ProgressGuard.Evaluate(-1, 100 * Second, 10 * Second, armed: true);

        Assert.Equal(ProgressOutcome.RejectNegative, decision.Outcome);
        Assert.False(decision.Accepted);
        Assert.Equal(10 * Second, decision.PositionTicks);
    }

    [Fact]
    public void Not_Armed_Regression_Is_Rejected()
    {
        // A4 的核心：续播失败后从头播了几分钟，位置远小于服务器记录的 20:00。
        var decision = ProgressGuard.Evaluate(300 * Second, 2700 * Second, 1200 * Second, armed: false);

        Assert.Equal(ProgressOutcome.RejectRegression, decision.Outcome);
        Assert.False(decision.Accepted);
    }

    [Fact]
    public void Not_Armed_Zero_Is_Rejected()
    {
        var decision = ProgressGuard.Evaluate(0, 2700 * Second, 1200 * Second, armed: false);

        Assert.Equal(ProgressOutcome.RejectZero, decision.Outcome);
        Assert.False(decision.Accepted);
    }

    [Fact]
    public void Not_Armed_Small_Forward_Progress_Is_Accepted()
    {
        // 允许往上抬：代理还没 armed，但位置已经超过服务器记录，写进去只会更好。
        var decision = ProgressGuard.Evaluate(1300 * Second, 2700 * Second, 1200 * Second, armed: false);

        Assert.Equal(ProgressOutcome.Accept, decision.Outcome);
        Assert.Equal(1300 * Second, decision.PositionTicks);
    }

    [Fact]
    public void Armed_Backward_Seek_Is_Accepted()
    {
        // A5：用户主动拖回 0，必须记录拖回后的位置。
        var decision = ProgressGuard.Evaluate(0, 2700 * Second, 1200 * Second, armed: true);

        Assert.Equal(ProgressOutcome.Accept, decision.Outcome);
        Assert.Equal(0, decision.PositionTicks);
        Assert.False(decision.Played);
    }

    [Fact]
    public void Reaching_The_End_Marks_Played_And_Resets_Position()
    {
        var duration = 2700 * Second;
        var decision = ProgressGuard.Evaluate(duration - 2 * Second, duration, 1200 * Second, armed: true);

        Assert.Equal(ProgressOutcome.Complete, decision.Outcome);
        Assert.True(decision.Played);
        Assert.Equal(0, decision.PositionTicks);
    }

    [Fact]
    public void Ended_Flag_Marks_Complete_Even_When_Position_Reads_Zero()
    {
        // 实测：PotPlayer 播完最后一刻 0x5004 返回 0、0x5002 也可能返回 0。
        // 没有这个显式标志，这一集会被当成「用户把进度拖回开头」，把进度写成 0。
        var decision = ProgressGuard.Evaluate(0, 0, 1200 * Second, armed: true, ended: true);

        Assert.Equal(ProgressOutcome.Complete, decision.Outcome);
        Assert.True(decision.Played);
        Assert.Equal(0, decision.PositionTicks);
    }

    [Fact]
    public void Unknown_Duration_Never_Marks_Played()
    {
        // 播放器还没读出时长时 durationTicks = 0。若不判 0，任何位置都会「大于等于 -5 秒」，
        // 刚开始播的一集会被立刻标记成已看完 —— 这是最容易踩的坑。
        var decision = ProgressGuard.Evaluate(5 * Second, 0, 0, armed: true);

        Assert.Equal(ProgressOutcome.Accept, decision.Outcome);
        Assert.False(decision.Played);
    }

    [Fact]
    public void Regression_Within_Tolerance_Is_Accepted()
    {
        // 4 秒的抖动属于正常（进度是 5 秒一次报的），不算倒退。
        var decision = ProgressGuard.Evaluate(1196 * Second, 2700 * Second, 1200 * Second, armed: false);

        Assert.Equal(ProgressOutcome.Accept, decision.Outcome);
    }

    [Fact]
    public void Same_Session_Can_Report_Multiple_Episodes()
    {
        // PotPlayer 自动连播时 playSessionId 不变、itemId 会变。
        // 早先按 itemId 绑定会话，换集后的每一次上报都被判成 session-mismatch 丢掉。
        var guard = new ProgressGuard(new FakeClock());

        Assert.True(guard.Submit(Request(1200 * Second, item: "a"), User, 0).Accepted);
        Assert.True(guard.Submit(Request(30 * Second, item: "b"), User, 0).Accepted);
    }

    [Fact]
    public void Arming_Is_Per_Episode()
    {
        // 上一集确认过在播，不代表下一集也可以随便写：
        // 换集后新集仍要重新建立信任，否则「新集从 0 开始」会把它已有的进度冲掉。
        var guard = new ProgressGuard(new FakeClock());

        Assert.True(guard.Submit(Request(1200 * Second, armed: true, item: "a"), User, 0).Accepted);

        var next = guard.Submit(Request(5 * Second, armed: false, item: "b"), User, 600 * Second);
        Assert.Equal(ProgressOutcome.RejectRegression, next.Outcome);
    }

    [Fact]
    public void Same_Session_From_Another_User_Is_Rejected()
    {
        var guard = new ProgressGuard(new FakeClock());
        Assert.True(guard.Submit(Request(100 * Second), User, 0).Accepted);

        var other = guard.Submit(Request(200 * Second), Guid.Parse("22222222222222222222222222222222"), 0);
        Assert.Equal(ProgressOutcome.RejectSession, other.Outcome);
    }

    [Fact]
    public void Missing_Session_Is_Rejected()
    {
        var guard = new ProgressGuard(new FakeClock());
        var decision = guard.Submit(Request(100 * Second, session: "  "), User, 0);

        Assert.Equal(ProgressOutcome.RejectSession, decision.Outcome);
    }

    [Fact]
    public void Armed_Is_Remembered_For_The_Session()
    {
        var guard = new ProgressGuard(new FakeClock());
        var clockRequest = Request(1200 * Second, armed: true);

        Assert.True(guard.Submit(clockRequest, User, 0).Accepted);

        // 之后代理哪怕报 armed=false 并给出更小的位置，也不该被当作「未 armed」拒绝：
        // 会话里已经出现过「确实在播」的证据（H1：连点两集时旧会话不应把进度写乱）。
        var later = guard.Submit(Request(600 * Second, armed: false), User, 1200 * Second);

        Assert.Equal(ProgressOutcome.Accept, later.Outcome);
        Assert.Equal(600 * Second, later.PositionTicks);
    }

    [Fact]
    public void Expired_Session_Loses_Armed_Privilege()
    {
        var clock = new FakeClock();
        var guard = new ProgressGuard(clock);

        Assert.True(guard.Submit(Request(1200 * Second, armed: true), User, 0).Accepted);

        // 30 分钟没有更新：会话被清理。清理后重新收到同一 sessionId 时按「全新会话」处理，
        // 也就是说 armed 特权必须一起作废 —— 否则代理只要复用旧 sessionId 就能永久绕过守卫。
        clock.Advance(ProgressGuard.SessionLifetime + TimeSpan.FromMinutes(1));

        var decision = guard.Submit(Request(600 * Second, armed: false), User, 1200 * Second);
        Assert.Equal(ProgressOutcome.RejectRegression, decision.Outcome);
    }

    [Fact]
    public void Reason_Strings_Are_Stable()
    {
        // 代理与排错文档都按字符串判断，改字面量属于破坏性变更。
        Assert.Equal("ok", ProgressGuard.Evaluate(100 * Second, 2700 * Second, 0, true).Reason);
        Assert.Equal("completed", ProgressGuard.Evaluate(2700 * Second, 2700 * Second, 0, true).Reason);
        Assert.Equal("negative-position", ProgressGuard.Evaluate(-5, 2700 * Second, 0, true).Reason);
        Assert.Equal(
            "not-armed-and-position-regressed",
            ProgressGuard.Evaluate(10 * Second, 2700 * Second, 1200 * Second, false).Reason);
        Assert.Equal(
            "not-armed-and-zero-position",
            ProgressGuard.Evaluate(0, 2700 * Second, 1200 * Second, false).Reason);
    }
}
