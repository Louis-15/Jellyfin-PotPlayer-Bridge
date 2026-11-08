using System.Text;

namespace PotPlayerBridge.Core;

/// <summary>
/// 生成 PotPlayer 的 .dpl 播放列表（UTF-8 BOM + CRLF）。
/// 格式来自 M0 实测（PLAN 1.6）：
/// <code>
/// DAUMPLAYLIST
/// playname=&lt;起播项地址&gt;
/// topindex=&lt;起播项序号，1 开始&gt;
/// saveplaypos=0
/// 1*file*&lt;地址&gt;
/// 1*title*&lt;标题&gt;
/// </code>
/// 注意：绝不使用 /title= 参数（实测会让 DPL 完全不播放）。
/// <para>
/// 为什么这里不写字幕：实测 PotPlayer 的 DPL 格式<b>没有</b>字幕字段
/// （<c>N*sub*</c> 会被静默忽略，PotPlayer 自己保存 DPL 时也从不写字幕），
/// 所以剧集的外挂字幕无法通过整季 DPL 带进去，只能靠内嵌字幕。
/// 电影/单片视频改走「单集直启 + /sub」路线，见 <see cref="PotPlayerLauncher.LaunchSingleAsync"/>。
/// </para>
/// </summary>
public static class DplBuilder
{
    /// <summary>生成 DPL 文本。</summary>
    /// <param name="plan">服务器返回的播放计划。</param>
    /// <returns>DPL 文本（不含 BOM，写入文件时由调用方加）。</returns>
    public static string Build(PlaybackPlan plan)
    {
        if (plan.Playlist.Count == 0)
        {
            throw new PlaybackException(502, "播放列表为空，无法生成 DPL。", "empty-plan");
        }

        var startIndex = Math.Clamp(plan.StartIndex, 1, plan.Playlist.Count);
        var startItem = plan.Playlist[startIndex - 1];

        if (string.IsNullOrWhiteSpace(startItem.StreamUrl))
        {
            throw new PlaybackException(502, "起播项的播放地址为空。", "empty-stream-url");
        }

        var builder = new StringBuilder();
        builder.Append("DAUMPLAYLIST\r\n");
        builder.Append("playname=").Append(startItem.StreamUrl).Append("\r\n");
        builder.Append("topindex=").Append(startIndex).Append("\r\n");
        builder.Append("saveplaypos=0\r\n");

        for (var i = 0; i < plan.Playlist.Count; i++)
        {
            var item = plan.Playlist[i];
            var number = i + 1;

            if (string.IsNullOrWhiteSpace(item.StreamUrl))
            {
                // 缺地址的项写进去只会让整个列表播到一半卡住，直接跳过（不改变其它项的序号连续性）。
                continue;
            }

            builder.Append(number).Append("*file*").Append(item.StreamUrl).Append("\r\n");
            builder.Append(number).Append("*title*").Append(ToSingleLine(item.Title)).Append("\r\n");
        }

        return builder.ToString();
    }

    /// <summary>把标题压成单行，避免换行破坏 DPL 行结构。</summary>
    private static string ToSingleLine(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return string.Empty;
        }

        return title
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
    }
}
