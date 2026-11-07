using System.Collections.Concurrent;

namespace Jellyfin.Plugin.PotPlayerBridge.Core;

/// <summary>
/// 进度写入守卫（PLAN 5.7）。这是整个项目最容易被「看起来能用」掩盖的失败点：
/// 一次错误的上报会把用户攒了很久的观看记录冲掉，而用户往往几天后才发现。
/// <para>
/// 两道防线：
/// <list type="number">
/// <item>代理侧 armed 状态机（见代理的 PlaybackProgressState）负责「不确定就不报」；</item>
/// <item>服务器侧本类负责「即使收到可疑数据也不写」，两层互不依赖。</item>
/// </list>
/// </para>
/// <para>
/// 会话表按 playSessionId 保存「哪一集的会话、是否曾经 armed」。一旦某个会话上报过 armed=true，
/// 服务器就永久记住（armed 只累加不回退），这样代理偶尔的误判也不会让进度倒退。
/// </para>
/// </summary>
public sealed class ProgressGuard
{
    /// <summary>判定「到结尾」与「位置倒退」的容差：5 秒（ticks）。</summary>
    public const long ToleranceTicks = 5 * TimeSpan.TicksPerSecond;

    /// <summary>会话多久没更新就视为过期，过期后按「没有历史」处理。</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, SessionState> _sessions = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    /// <summary>Initializes a new instance of the <see cref="ProgressGuard"/> class.</summary>
    public ProgressGuard()
        : this(TimeProvider.System)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="ProgressGuard"/> class（测试可注入时钟）。</summary>
    /// <param name="time">时钟。</param>
    public ProgressGuard(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>Gets 当前登记的会话数（排查用）。</summary>
    public int SessionCount => _sessions.Count;

    /// <summary>
    /// 纯判定：给定「上报值」与「服务器现有值」，返回该写什么。
    /// 不含任何状态，便于把 PLAN 5.7 的规则逐条写成单元测试。
    /// </summary>
    /// <param name="positionTicks">上报的位置。</param>
    /// <param name="durationTicks">上报的时长（0 表示未知）。</param>
    /// <param name="existingPositionTicks">服务器现有位置。</param>
    /// <param name="armed">是否已 armed（代理确认真的在播这一集）。</param>
    /// <param name="ended">代理是否明确报告「这一集播完了」。</param>
    /// <returns>判定结果。</returns>
    public static GuardDecision Evaluate(
        long positionTicks,
        long durationTicks,
        long existingPositionTicks,
        bool armed,
        bool ended = false)
    {
        // 规则 1：负数一律拒绝。位置为负说明代理读到了坏数据，绝不能落盘。
        if (positionTicks < 0)
        {
            return new GuardDecision(ProgressOutcome.RejectNegative, existingPositionTicks, false);
        }

        if (!armed)
        {
            // 规则 3：未 armed 就要把非零进度清成 0 —— 典型的「假续播后从头播了几秒就关」，
            // 写进去等于抹掉用户的观看记录。
            if (positionTicks == 0 && existingPositionTicks > 0)
            {
                return new GuardDecision(ProgressOutcome.RejectZero, existingPositionTicks, false);
            }

            // 规则 2：未 armed 且比现有进度更小 —— 同理，只允许往上抬。
            if (positionTicks < existingPositionTicks - ToleranceTicks)
            {
                return new GuardDecision(ProgressOutcome.RejectRegression, existingPositionTicks, false);
            }
        }

        // 规则 5：播完就标记已看完、位置归零。
        // 两种来源：代理明确说「播完了」（ended），或位置已经到达结尾。
        // 「位置到达结尾」必须要求 durationTicks > 0：播放器还没读出时长时它是 0，
        // 那时任何非负位置都会「大于等于 duration - 5s」，会把刚开始播的集误标成已看完。
        if (ended || (durationTicks > 0 && positionTicks >= durationTicks - ToleranceTicks))
        {
            return new GuardDecision(ProgressOutcome.Complete, 0, true);
        }

        // 规则 4 / 6：正常写入。
        return new GuardDecision(ProgressOutcome.Accept, positionTicks, false);
    }

    /// <summary>
    /// 带会话校验的判定：确认这个 playSessionId 确实是这个用户、这个 item 的会话，
    /// 并把 armed 记录下来（只累加，不回退）。
    /// </summary>
    /// <param name="request">上报内容。</param>
    /// <param name="userId">当前登录用户。</param>
    /// <param name="existingPositionTicks">服务器现有位置。</param>
    /// <returns>判定结果。</returns>
    public GuardDecision Submit(ProgressRequest request, Guid userId, long existingPositionTicks)
    {
        var sessionId = request.PlaySessionId;
        var itemId = request.ItemId;

        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(itemId))
        {
            return new GuardDecision(ProgressOutcome.RejectSession, existingPositionTicks, false);
        }

        var now = _time.GetUtcNow();
        SweepExpired(now);

        var state = _sessions.GetOrAdd(sessionId, _ => new SessionState(userId, now));

        // 只校验会话归属：一个会话会跨集（PotPlayer 自动连播），itemId 本来就会变，
        // 早先按 itemId 绑定会把换集后的每一次上报都判成 session-mismatch 而丢弃。
        if (state.UserId != userId)
        {
            return new GuardDecision(ProgressOutcome.RejectSession, existingPositionTicks, false);
        }

        // armed 按「集」累计：换集后新的一集必须重新建立信任，
        // 不能因为上一集确认过就把新集的任意位置都写进去。
        var armed = state.IsArmed(itemId) || request.Armed;
        var decision = Evaluate(
            request.PositionTicks,
            request.DurationTicks,
            existingPositionTicks,
            armed,
            request.Ended);

        if (armed || decision.Accepted)
        {
            state.MarkArmed(itemId);
        }

        state.UpdatedAt = now;
        state.LastAcceptedTicks = decision.Accepted ? decision.PositionTicks : state.LastAcceptedTicks;

        return decision;
    }

    /// <summary>清空会话表（插件重启/测试用）。</summary>
    public void Clear() => _sessions.Clear();

    private void SweepExpired(DateTimeOffset now)
    {
        // 不必每次上报都扫全表：一分钟扫一次足够，避免高频上报时的无谓遍历。
        if (now - _lastSweep < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastSweep = now;
        foreach (var pair in _sessions)
        {
            if (now - pair.Value.UpdatedAt > SessionLifetime)
            {
                _sessions.TryRemove(pair.Key, out _);
            }
        }
    }

    private sealed class SessionState
    {
        private readonly HashSet<string> _armedItems = new(StringComparer.OrdinalIgnoreCase);

        public SessionState(Guid userId, DateTimeOffset now)
        {
            UserId = userId;
            UpdatedAt = now;
        }

        public Guid UserId { get; }

        public long LastAcceptedTicks { get; set; }

        public DateTimeOffset UpdatedAt { get; set; }

        public bool IsArmed(string itemId) => _armedItems.Contains(itemId);

        public void MarkArmed(string itemId) => _armedItems.Add(itemId);
    }
}
