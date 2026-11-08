using System;
using Jellyfin.Plugin.PotPlayerBridge.Core;
using Xunit;

namespace Jellyfin.Plugin.PotPlayerBridge.Tests;

/// <summary>
/// 「播放器实测时长」的合并规则测试（PLAN M5.5）。
/// 这条规则决定卡片角标显示的数字对不对：记错了会出现「0:02 / 0:05」这种明显错误的信息，
/// 比不显示数字更糟，所以单独锁死。
/// </summary>
public class RuntimeDurationStoreTests
{
    private static readonly long OneMinute = TimeSpan.FromMinutes(1).Ticks;
    private static readonly long OneHour = TimeSpan.FromHours(1).Ticks;

    [Fact]
    public void Rejects_Zero_And_Too_Small_Durations()
    {
        // 播放器还没加载完会报 0；片头/广告会报出几秒的怪值。
        Assert.False(RuntimeDurationStore.ShouldRecord(0, 0));
        Assert.False(RuntimeDurationStore.ShouldRecord(0, TimeSpan.FromSeconds(5).Ticks));
        Assert.False(RuntimeDurationStore.ShouldRecord(0, TimeSpan.FromSeconds(59).Ticks));
    }

    [Fact]
    public void Accepts_First_Real_Duration()
    {
        Assert.True(RuntimeDurationStore.ShouldRecord(0, OneMinute));
        Assert.True(RuntimeDurationStore.ShouldRecord(0, OneHour));
    }

    [Fact]
    public void Only_Grows()
    {
        // 同一集后来报到更小的值（播放器异常、只播了片段）不能覆盖已有记录，
        // 否则角标的分母会突然变小，进度条会跳。
        Assert.False(RuntimeDurationStore.ShouldRecord(OneHour, OneMinute));
        Assert.False(RuntimeDurationStore.ShouldRecord(OneHour, OneHour));
        Assert.True(RuntimeDurationStore.ShouldRecord(OneHour, OneHour + TimeSpan.FromSeconds(1).Ticks));
    }

    [Fact]
    public void Grows_Even_If_Previous_Record_Is_Too_Small()
    {
        // 极端情况：早期错误地记了一个很小的值（例如规则收紧之前写进去的），
        // 后续的真实时长必须能覆盖它，否则那条记录永远坏着。
        Assert.True(RuntimeDurationStore.ShouldRecord(TimeSpan.FromSeconds(3).Ticks, OneHour));
    }
}
