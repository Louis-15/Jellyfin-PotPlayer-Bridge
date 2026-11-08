namespace Jellyfin.Plugin.PotPlayerBridge.Core;

/// <summary>
/// POST /PotPlayerBridge/Progress 的请求体（由 Windows 代理发来）。
/// 字段命名与代理、PLAN 5.4 保持一致，两边不再维护映射表。
/// </summary>
public sealed class ProgressRequest
{
    /// <summary>Gets or sets 播放会话 ID（Resolve 时生成，代理原样带回）。</summary>
    public string? PlaySessionId { get; set; }

    /// <summary>Gets or sets 正在播放的项 ID。</summary>
    public string? ItemId { get; set; }

    /// <summary>Gets or sets 当前位置（ticks）。</summary>
    public long PositionTicks { get; set; }

    /// <summary>Gets or sets 总时长（ticks）。0 表示播放器还没读出来。</summary>
    public long DurationTicks { get; set; }

    /// <summary>Gets or sets a value indicating whether 当前处于暂停。</summary>
    public bool IsPaused { get; set; }

    /// <summary>Gets or sets a value indicating whether 这是「停止播放/关闭播放器」的最后一次上报。</summary>
    public bool Stopped { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether 代理已确认「播放器真的在播这一集」。
    /// 未 armed 的上报只能抬高进度，不能倒退或清零（PLAN 5.7）。
    /// </summary>
    public bool Armed { get; set; }

    /// <summary>Gets or sets a value indicating whether 本次上报来自用户主动拖动进度条。</summary>
    public bool Seeked { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether 这一集已经播完。
    /// 必须有这个显式标志：PotPlayer 播完那一刻位置会读成 0、时长也可能读成 0，
    /// 只靠「位置接近时长」判断不出来，会被当成「用户把进度拖回开头」而写入 0。
    /// </summary>
    public bool Ended { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether 这是「这一集刚开始播」的第一条上报。
    /// 自动连播到下一集、或从网页点开一集时，代理会把它置为 true。
    /// 服务器据此改用会广播 UserDataChanged 的保存理由：
    /// 否则网页上的进度条与卡片角标会一直停在上一集，直到用户暂停或关闭播放器才更新。
    /// </summary>
    public bool EpisodeStart { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether 要求服务器把这次保存广播出去。
    /// 代理每分钟置一次，让网页上的进度条/角标自己走起来；
    /// 中间每 5 秒的上报不带这个标记，仍按 PlaybackProgress 静默写入，避免刷屏。
    /// </summary>
    public bool Publish { get; set; }
}

/// <summary>进度写入的判定结果。<see cref="ProgressOutcome"/> 决定「写什么」，reason 只用于回给代理排查。</summary>
public enum ProgressOutcome
{
    /// <summary>按上报的位置正常写入。</summary>
    Accept,

    /// <summary>已到结尾：标记已看完并把位置归零（Jellyfin 的惯例）。</summary>
    Complete,

    /// <summary>位置为负数，属于非法输入。</summary>
    RejectNegative,

    /// <summary>未 armed 且位置比服务器现有进度更小：拒绝，避免假续播把记录冲掉。</summary>
    RejectRegression,

    /// <summary>未 armed 且要把非零进度清成 0：拒绝。</summary>
    RejectZero,

    /// <summary>会话与 item/user 不匹配，或会话已过期。</summary>
    RejectSession
}

/// <summary>
/// 一次进度上报的最终决定：包含判定结果与真正要写入的值。
/// 把「判定」和「写入」分开，是为了让判定逻辑可以脱离 Jellyfin 服务单测（见 ProgressGuardTests）。
/// </summary>
/// <param name="Outcome">判定结果。</param>
/// <param name="PositionTicks">要写入的位置（Reject* 时等于服务器现有值，表示不变）。</param>
/// <param name="Played">是否标记为已看完。</param>
public sealed record GuardDecision(ProgressOutcome Outcome, long PositionTicks, bool Played)
{
    /// <summary>Gets a value indicating whether 这次上报应当落盘。</summary>
    public bool Accepted => Outcome is ProgressOutcome.Accept or ProgressOutcome.Complete;

    /// <summary>Gets 回给代理的 reason（机器可读）。</summary>
    public string Reason => Outcome switch
    {
        ProgressOutcome.Accept => "ok",
        ProgressOutcome.Complete => "completed",
        ProgressOutcome.RejectNegative => "negative-position",
        ProgressOutcome.RejectRegression => "not-armed-and-position-regressed",
        ProgressOutcome.RejectZero => "not-armed-and-zero-position",
        _ => "session-mismatch"
    };
}
