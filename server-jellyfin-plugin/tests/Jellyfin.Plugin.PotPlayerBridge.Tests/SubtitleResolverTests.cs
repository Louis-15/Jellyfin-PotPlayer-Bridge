using System;
using System.Collections.Generic;
using Jellyfin.Plugin.PotPlayerBridge.Core;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.PotPlayerBridge.Tests;

/// <summary>
/// 字幕选择规则测试（PLAN 6.8 / D-07 / E1 / E2）。
/// 选错字幕用户会直接看到乱码或外文，比"没有字幕"更糟，所以这段逻辑单独锁死。
/// </summary>
public class SubtitleResolverTests
{
    private static MediaStream Sub(int index, string? language, string? title = null, bool external = true)
    {
        return new MediaStream
        {
            Index = index,
            Type = MediaStreamType.Subtitle,
            Language = language,
            Title = title,
            IsExternal = external,
            Codec = "subrip"
        };
    }

    [Fact]
    public void Prefers_Simplified_Chinese()
    {
        var candidates = new List<MediaStream>
        {
            Sub(0, "eng"),
            Sub(1, "cht", "繁体"),
            Sub(2, "chs", "简体")
        };

        var picked = SubtitleResolver.PickBest(candidates);

        Assert.NotNull(picked);
        Assert.Equal(2, picked!.Index);
    }

    [Fact]
    public void Falls_Back_To_Traditional_When_No_Simplified()
    {
        var candidates = new List<MediaStream>
        {
            Sub(0, "eng"),
            Sub(1, "cht", "繁体")
        };

        var picked = SubtitleResolver.PickBest(candidates);

        Assert.Equal(1, picked!.Index);
    }

    [Fact]
    public void Recognizes_Chinese_By_Title_When_Language_Is_Missing()
    {
        // 很多库的外挂字幕没有语言标记，只能靠标题里的「中」判断（实测常见）。
        var candidates = new List<MediaStream>
        {
            Sub(0, null, "English"),
            Sub(1, null, "中文字幕")
        };

        var picked = SubtitleResolver.PickBest(candidates);

        Assert.Equal(1, picked!.Index);
    }

    [Fact]
    public void Recognizes_Zh_Language_Prefix()
    {
        var candidates = new List<MediaStream>
        {
            Sub(0, "en"),
            Sub(1, "zh-Hans", "简体中文")
        };

        var picked = SubtitleResolver.PickBest(candidates);

        Assert.Equal(1, picked!.Index);
    }

    [Fact]
    public void Falls_Back_To_First_External_When_No_Chinese()
    {
        var candidates = new List<MediaStream>
        {
            Sub(0, "eng", "English"),
            Sub(1, "jpn", "日本語")
        };

        var picked = SubtitleResolver.PickBest(candidates);

        Assert.Equal(0, picked!.Index);
    }

    [Fact]
    public void Returns_Null_When_No_Candidates()
    {
        Assert.Null(SubtitleResolver.PickBest(new List<MediaStream>()));
    }
}
