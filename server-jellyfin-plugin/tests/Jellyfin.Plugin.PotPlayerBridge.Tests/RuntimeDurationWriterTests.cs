using System;
using Jellyfin.Plugin.PotPlayerBridge.Core;
using Xunit;

namespace Jellyfin.Plugin.PotPlayerBridge.Tests;

/// <summary>
/// 「把实测时长写回数据库」的判定规则测试（PLAN M5.5）。
/// 这条规则错了会污染用户的媒体库元数据：
/// 覆盖 Jellyfin 探测到的精确值、或写入广告片段的几秒时长，都是不可接受的。
/// </summary>
public class RuntimeDurationWriterTests
{
    private static readonly long OneMinute = TimeSpan.FromMinutes(1).Ticks;
    private static readonly long OneHour = TimeSpan.FromHours(1).Ticks;

    [Fact]
    public void Writes_When_Jellyfin_Has_No_Duration()
    {
        Assert.True(RuntimeDurationWriter.ShouldWrite(null, OneHour));
        Assert.True(RuntimeDurationWriter.ShouldWrite(0, OneHour));
    }

    [Fact]
    public void Never_Overwrites_Existing_Duration()
    {
        // Jellyfin 已经有值就绝不覆盖：它可能是探测出来的精确值，
        // 也可能是用户在 nfo 里手工维护的。实测时长不该去动它。
        Assert.False(RuntimeDurationWriter.ShouldWrite(OneHour, OneHour));
        Assert.False(RuntimeDurationWriter.ShouldWrite(OneHour, OneHour + OneMinute));
        Assert.False(RuntimeDurationWriter.ShouldWrite(OneHour, OneMinute));
    }

    [Fact]
    public void Rejects_Zero_And_Too_Small_Durations()
    {
        // 播放器还没加载完会报 0；片头/广告会报出几秒的怪值。
        // 写进库会让角标显示「0:02 / 0:05」这种明显错误的信息。
        Assert.False(RuntimeDurationWriter.ShouldWrite(null, 0));
        Assert.False(RuntimeDurationWriter.ShouldWrite(null, TimeSpan.FromSeconds(5).Ticks));
        Assert.False(RuntimeDurationWriter.ShouldWrite(null, TimeSpan.FromSeconds(59).Ticks));
    }

    [Fact]
    public void Accepts_Exactly_One_Minute()
    {
        Assert.True(RuntimeDurationWriter.ShouldWrite(null, OneMinute));
    }
}
