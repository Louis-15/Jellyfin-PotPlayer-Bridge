namespace PotPlayerBridge.Core;

/// <summary>POST /api/play 的请求体（由注入脚本发来）。</summary>
public sealed class PlayRequest
{
    /// <summary>Gets or sets Jellyfin 服务器地址，例如 https://your-server:8443。</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Gets or sets 当前用户的访问令牌。只在本机内存里用于调用服务器，绝不落盘/写日志。</summary>
    public string? AccessToken { get; set; }

    /// <summary>Gets or sets 当前用户 ID（服务器仍以 Token 为准，这里只做校验）。</summary>
    public string? UserId { get; set; }

    /// <summary>Gets or sets 用户点击的项 ID。</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets 意图：item / resume / restart。</summary>
    public string? Intent { get; set; }
}

/// <summary>
/// 服务器 /PotPlayerBridge/Resolve 的响应里，本机代理真正会用到的部分。
/// 字段名与服务器保持一致（camelCase），避免两边再维护一套映射。
/// </summary>
public sealed class PlaybackPlan
{
    /// <summary>Gets or sets 播放会话 ID（M5 进度回写用）。</summary>
    public string PlaySessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets 实际播放项 ID。</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets 实际播放项类型：Episode / Movie。</summary>
    public string ItemType { get; set; } = string.Empty;

    /// <summary>Gets or sets 展示标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets 播放列表名称（DPL 文件名）。</summary>
    public string PlaylistName { get; set; } = string.Empty;

    /// <summary>Gets or sets 起播项序号（1 开始）。</summary>
    public int StartIndex { get; set; }

    /// <summary>Gets or sets 起播位置（ticks，M5/M6 使用）。</summary>
    public long StartPositionTicks { get; set; }

    /// <summary>Gets or sets 片头秒数（M6 使用）。</summary>
    public int IntroSeconds { get; set; }

    /// <summary>Gets or sets 片尾秒数（M6 使用）。</summary>
    public int OutroSeconds { get; set; }

    /// <summary>Gets or sets 整季全集（电影只有一项）。</summary>
    public List<PlaylistEntry> Playlist { get; set; } = new();
}

/// <summary>播放列表中的一项。</summary>
public sealed class PlaylistEntry
{
    /// <summary>Gets or sets Jellyfin 条目 ID。</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets 集号。</summary>
    public int? IndexNumber { get; set; }

    /// <summary>Gets or sets 季号。</summary>
    public int? ParentIndexNumber { get; set; }

    /// <summary>Gets or sets 展示标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets 播放地址（STRM 直链或 Jellyfin 直链）。</summary>
    public string StreamUrl { get; set; } = string.Empty;

    /// <summary>Gets or sets 时长（ticks）。</summary>
    public long RuntimeTicks { get; set; }

    /// <summary>Gets or sets 外挂字幕（M6 使用）。</summary>
    public List<SubtitleEntry> Subtitles { get; set; } = new();
}

/// <summary>外挂字幕（M6 使用）。</summary>
public sealed class SubtitleEntry
{
    /// <summary>Gets or sets 字幕流索引。</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets 编码。</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Gets or sets 语言。</summary>
    public string? Language { get; set; }

    /// <summary>Gets or sets 标题。</summary>
    public string? Title { get; set; }

    /// <summary>Gets or sets 是否外挂。</summary>
    public bool IsExternal { get; set; }

    /// <summary>Gets or sets 相对下载地址。</summary>
    public string Url { get; set; } = string.Empty;
}

/// <summary>播放失败时抛出的异常：带 HTTP 状态码与机器可读错误码。</summary>
public sealed class PlaybackException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="PlaybackException"/> class.</summary>
    /// <param name="statusCode">返回给网页的状态码。</param>
    /// <param name="message">面向用户的中文消息。</param>
    /// <param name="code">机器可读错误码。</param>
    public PlaybackException(int statusCode, string message, string code)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    /// <summary>Gets 返回给网页的状态码。</summary>
    public int StatusCode { get; }

    /// <summary>Gets 机器可读错误码。</summary>
    public string Code { get; }
}
