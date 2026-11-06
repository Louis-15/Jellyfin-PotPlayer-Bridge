namespace Jellyfin.Plugin.PotPlayerBridge.Core;

/// <summary>播放意图。注入脚本只负责判断「用户点了哪种按钮」，具体播哪一集由服务器决定。</summary>
public enum PlaybackIntent
{
    /// <summary>普通播放按钮：Series/Season 找最近看的那一集，Episode/Movie 播自己。</summary>
    Item,

    /// <summary>「继续观看」按钮：播卡片上标的那一项，从用户进度续播。</summary>
    Resume,

    /// <summary>「接下来」/ 重播按钮：播目标项，但从 0 开始（片头仍然按季配置跳过）。</summary>
    Restart
}

/// <summary>POST /PotPlayerBridge/Resolve 的请求体。</summary>
public sealed class ResolveRequest
{
    /// <summary>Gets or sets 用户点的那一项的 ID（卡片或详情页上的 item）。</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets 意图字符串：item / resume / restart。</summary>
    public string? Intent { get; set; }
}

/// <summary>播放列表里的一项（对应 DPL 的一行）。</summary>
public sealed class PlaylistEntry
{
    /// <summary>Gets or sets Jellyfin 条目 ID（32 位十六进制，不带连字符）。</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets 集号（电影为空）。</summary>
    public int? IndexNumber { get; set; }

    /// <summary>Gets or sets 季号（电影为空）。</summary>
    public int? ParentIndexNumber { get; set; }

    /// <summary>Gets or sets 展示标题，例如 "S01E04 突现情报真假难辨"。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets 可以直接交给播放器的地址：STRM 直链或 Jellyfin 回退路径。</summary>
    public string StreamUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets 时长（ticks，1 秒 = 10,000,000）。</summary>
    public long RuntimeTicks { get; set; }

    /// <summary>Gets or sets 外挂字幕列表（M6 实现，现在固定为空数组）。</summary>
    public List<SubtitleEntry> Subtitles { get; set; } = new();
}

/// <summary>外挂字幕信息。字段与 Jellyfin 的媒体流一一对应，代理按 Url 带 Token 下载。</summary>
public sealed class SubtitleEntry
{
    /// <summary>Gets or sets 字幕流索引。</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets 字幕编码，例如 subrip / ass。</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Gets or sets 语言代码，例如 zho / chi。</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets 字幕标题。</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets a value indicating whether 是否是外挂字幕（内嵌字幕不下载，交给播放器自己切）。</summary>
    public bool IsExternal { get; set; }

    /// <summary>Gets or sets 相对下载地址（代理需要补 Token）。</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>POST /PotPlayerBridge/Resolve 的成功响应：一个完整的播放计划。</summary>
public sealed class PlaybackPlan
{
    /// <summary>Gets or sets 本次播放会话 ID，进度回写用它做幂等与防倒退。</summary>
    public string PlaySessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets 用户 ID。</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets 实际要播的那一项（对 Series/Season 来说是解析出来的那一集）。</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets 实际播放项的类型：Episode / Movie。</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets or sets 展示标题，与列表名一致。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets 播放列表名称（DPL 文件名），例如 "誓言无声 第1季"。</summary>
    public string PlaylistName { get; set; } = string.Empty;

    /// <summary>Gets or sets 起播项在播放列表里的序号（1 开始，对应 DPL 的 topindex）。</summary>
    public int StartIndex { get; set; }

    /// <summary>Gets or sets 起播位置（ticks），已经过「续播 + 片头跳过」计算。</summary>
    public long StartPositionTicks { get; set; }

    /// <summary>Gets or sets 片头秒数（0 表示不跳过）。</summary>
    public int IntroSeconds { get; set; }

    /// <summary>Gets or sets 片尾秒数（0 表示不跳过，M6 使用）。</summary>
    public int OutroSeconds { get; set; }

    /// <summary>Gets or sets 所属剧 ID（电影为空）。</summary>
    public string? SeriesId { get; set; }

    /// <summary>Gets or sets 所属季 ID（电影为空）。</summary>
    public string? SeasonId { get; set; }

    /// <summary>Gets or sets 整季全集（电影只有一项）。</summary>
    public List<PlaylistEntry> Playlist { get; set; } = new();
}

/// <summary>某一季的片头片尾配置（按季存储，所有用户共享）。</summary>
public sealed class SeasonSettings
{
    /// <summary>Gets or sets 季 ID。</summary>
    public Guid SeasonId { get; set; }

    /// <summary>Gets or sets 片头秒数，0 表示不跳过。</summary>
    public int IntroSeconds { get; set; }

    /// <summary>Gets or sets 片尾秒数，0 表示不跳过。</summary>
    public int OutroSeconds { get; set; }

    /// <summary>Gets or sets 最近一次修改时间（UTC）。</summary>
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>POST /PotPlayerBridge/SeasonSettings 的请求体。</summary>
public sealed class SeasonSettingsRequest
{
    /// <summary>Gets or sets 当前页面上的项（季或单集，单集会换算成所属季）。</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets 片头秒数。</summary>
    public int IntroSeconds { get; set; }

    /// <summary>Gets or sets 片尾秒数。</summary>
    public int OutroSeconds { get; set; }
}

/// <summary>GET /PotPlayerBridge/ItemInfo 的响应，供注入脚本决定是否显示季配置面板。</summary>
public sealed class ItemInfoResponse
{
    /// <summary>Gets or sets 请求的项 ID。</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets 项类型：Series / Season / Episode / Movie 等。</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets or sets 项名称。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets 所属剧 ID。</summary>
    public string? SeriesId { get; set; }

    /// <summary>Gets or sets 所属剧名称。</summary>
    public string? SeriesName { get; set; }

    /// <summary>Gets or sets 所属季 ID。</summary>
    public string? SeasonId { get; set; }

    /// <summary>Gets or sets 所属季名称。</summary>
    public string? SeasonName { get; set; }

    /// <summary>Gets or sets 季号。</summary>
    public int? SeasonNumber { get; set; }

    /// <summary>Gets or sets 集号。</summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>Gets or sets 该季当前生效的片头秒数。</summary>
    public int IntroSeconds { get; set; }

    /// <summary>Gets or sets 该季当前生效的片尾秒数。</summary>
    public int OutroSeconds { get; set; }

    /// <summary>Gets or sets a value indicating whether 当前项是否允许配置片头片尾（只有季和单集允许）。</summary>
    public bool CanConfigure { get; set; }
}
