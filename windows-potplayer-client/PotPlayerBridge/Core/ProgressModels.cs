namespace PotPlayerBridge.Core;

/// <summary>POST /PotPlayerBridge/Progress 的请求体。字段与服务器插件保持一致。</summary>
public sealed class ProgressReport
{
    /// <summary>Gets or sets 播放会话 ID（Resolve 返回，原样带回）。</summary>
    public string PlaySessionId { get; set; } = string.Empty;

    /// <summary>Gets or sets 正在播放的项 ID。</summary>
    public string ItemId { get; set; } = string.Empty;

    /// <summary>Gets or sets 当前位置（ticks）。</summary>
    public long PositionTicks { get; set; }

    /// <summary>Gets or sets 总时长（ticks）。0 表示播放器还没读出来。</summary>
    public long DurationTicks { get; set; }

    /// <summary>Gets or sets a value indicating whether 当前暂停。</summary>
    public bool IsPaused { get; set; }

    /// <summary>Gets or sets a value indicating whether 这是停止/关闭时的最后一次上报。</summary>
    public bool Stopped { get; set; }

    /// <summary>Gets or sets a value indicating whether 已确认真的在播这一集。</summary>
    public bool Armed { get; set; }

    /// <summary>Gets or sets a value indicating whether 本次上报来自用户拖动进度条。</summary>
    public bool Seeked { get; set; }

    /// <summary>Gets or sets a value indicating whether 这一集已经播完。</summary>
    public bool Ended { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether 这是「这一集刚开始播」的第一条上报。
    /// 服务器据此改用会广播 UserDataChanged 的保存理由，让网页立刻显示新一集的进度。
    /// </summary>
    public bool EpisodeStart { get; set; }
}

/// <summary>服务器的进度写入结果。</summary>
/// <param name="Accepted">是否真的写入了。</param>
/// <param name="Reason">服务器给的原因（ok / completed / not-armed-... ）。</param>
public sealed record ProgressResult(bool Accepted, string Reason);
