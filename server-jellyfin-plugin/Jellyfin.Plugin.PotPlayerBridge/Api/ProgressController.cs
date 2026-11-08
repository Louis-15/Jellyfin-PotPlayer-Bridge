using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.PotPlayerBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PotPlayerBridge.Api;

/// <summary>
/// 进度回写接口。用户 ID 一律从 JWT 取，绝不接受请求体里的 userId（否则任何人都能改别人的进度）。
/// </summary>
[ApiController]
[Route("PotPlayerBridge")]
public sealed class ProgressController : BridgeControllerBase
{
    private readonly ProgressGuard _guard;
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly RuntimeDurationStore _durations;

    /// <summary>Initializes a new instance of the <see cref="ProgressController"/> class.</summary>
    /// <param name="guard">进度守卫。</param>
    /// <param name="library">媒体库。</param>
    /// <param name="users">用户管理。</param>
    /// <param name="userData">用户数据。</param>
    /// <param name="durations">实测时长记录（给卡片角标兜底）。</param>
    public ProgressController(
        ProgressGuard guard,
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        RuntimeDurationStore durations)
    {
        _guard = guard;
        _library = library;
        _users = users;
        _userData = userData;
        _durations = durations;
    }

    /// <summary>
    /// 接收一次播放进度上报。被守卫拒绝时仍返回 200 + <c>accepted:false</c>：
    /// 这对代理来说是「正常但没写入」，不该触发重试风暴。
    /// </summary>
    /// <param name="request">上报内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果。</returns>
    [Authorize]
    [HttpPost("Progress")]
    public async Task<IActionResult> Progress([FromBody] ProgressRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || !Guid.TryParse(request.ItemId, out var itemId))
        {
            return ErrorResult(400, "请求里缺少合法的 itemId。", "bad-item-id");
        }

        if (string.IsNullOrWhiteSpace(request.PlaySessionId))
        {
            return ErrorResult(400, "请求里缺少 playSessionId。", "bad-session");
        }

        var userId = CurrentUserId;
        if (userId is null)
        {
            return ErrorResult(401, "登录状态已失效，请刷新页面后重新登录。", "no-user");
        }

        var item = _library.GetItemById(itemId);
        if (item is null)
        {
            return ErrorResult(404, "找不到该项目。", "item-not-found");
        }

        var user = _users.GetUserById(userId.Value);
        if (user is null)
        {
            return ErrorResult(401, "找不到当前用户，请重新登录。", "no-user");
        }

        var data = _userData.GetUserData(user, item);
        var existingTicks = data?.PlaybackPositionTicks ?? 0;

        var decision = _guard.Submit(request, userId.Value, existingTicks);
        if (!decision.Accepted)
        {
            // 明确返回 200：代理据此知道「服务器收到但没采纳」，不需要重试。
            return JsonPayload(new { accepted = false, reason = decision.Reason });
        }

        try
        {
            Apply(user, item, decision, request, cancellationToken);
        }
        catch (Exception ex)
        {
            return ErrorResult(500, "写入播放进度失败：" + ex.Message, "save-failed");
        }

        // 顺手记下「播放器实测到的时长」：STRM 条目在 Jellyfin 里没有 RunTimeTicks，
        // 卡片角标需要这个数字才能显示「16:08 / 30:22」。
        // 失败不影响进度本身（store 内部已吞掉异常），所以这里不等结果也不做错误处理。
        await _durations.RecordAsync(itemId, request.DurationTicks).ConfigureAwait(false);

        return JsonPayload(new { accepted = true, reason = decision.Reason });
    }

    private void Apply(
        User user,
        BaseItem item,
        GuardDecision decision,
        ProgressRequest request,
        CancellationToken cancellationToken)
    {
        var data = _userData.GetUserData(user, item);
        if (data is null)
        {
            return;
        }

        data.PlaybackPositionTicks = decision.PositionTicks;
        data.LastPlayedDate = DateTime.UtcNow;

        // PLAN 5.7 规则 6：已看完由规则 5 单独处理，其余情况一律回到「未看完」。
        // 这与 Jellyfin 自带播放器一致：重新开始播一集时，该集会从「已看」变回「在看的进度」。
        data.Played = decision.Played;

        if (request.DurationTicks > 0 && data.PlaybackPositionTicks > 0)
        {
            // 位置一定不能超过时长本身，否则「继续观看」的进度条会算成 >100%。
            data.PlaybackPositionTicks = Math.Min(data.PlaybackPositionTicks, request.DurationTicks);
        }

        // 为什么不用 PlaybackProgress 处理所有情况：
        // Jellyfin 的 UserDataChangeNotifier 会跳过 SaveReason == PlaybackProgress 的保存，
        // 不广播 UserDataChanged。实测（2026-11-07）用 PlaybackProgress 保存后，网页 WebSocket
        // 一个事件都收不到，进度条要等半分钟才变；而手机端是自己主动拉取，所以看起来「手机快、网页慢」。
        // 处理办法：常规进度仍然用 PlaybackProgress（避免每 5 秒把网页刷一次），
        // 但「用户能感知的时刻」（暂停、拖动、播完、停止、新一集开始）改用会广播的 reason。
        //
        // EpisodeStart 为什么必须广播：自动连播到下一集时，新集的第一条进度如果走
        // PlaybackProgress，网页会一直显示上一集，要等暂停/停止才刷新（实测就是这个现象）。
        //
        // Publish 是代理每分钟发一次的常规刷新：数据仍每 5 秒写入，但只有这一条广播。
        // 这样网页上的进度条与角标会自己走（一分钟一格），又不会每 5 秒把所有网页重绘一遍。
        var reason = request.Stopped
            ? UserDataSaveReason.PlaybackFinished
            : request.IsPaused || request.Seeked || request.Ended || request.EpisodeStart || request.Publish
                ? UserDataSaveReason.UpdateUserData
                : UserDataSaveReason.PlaybackProgress;

        // 用 Jellyfin 自己的保存通道，其他客户端（手机/VidHub）与「继续观看」才能立刻看到。
        // 注意：12.2.0 的 SaveUserData 是同步 void，不要 await。
        _userData.SaveUserData(user, item, data, reason, cancellationToken);
    }
}
