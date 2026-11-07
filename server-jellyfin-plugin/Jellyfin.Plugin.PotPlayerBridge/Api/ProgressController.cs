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

    /// <summary>Initializes a new instance of the <see cref="ProgressController"/> class.</summary>
    /// <param name="guard">进度守卫。</param>
    /// <param name="library">媒体库。</param>
    /// <param name="users">用户管理。</param>
    /// <param name="userData">用户数据。</param>
    public ProgressController(
        ProgressGuard guard,
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData)
    {
        _guard = guard;
        _library = library;
        _users = users;
        _userData = userData;
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

        var reason = request.Stopped
            ? UserDataSaveReason.PlaybackFinished
            : UserDataSaveReason.PlaybackProgress;

        // 用 Jellyfin 自己的保存通道，其他客户端（手机/VidHub）与「继续观看」才能立刻看到。
        // 注意：12.2.0 的 SaveUserData 是同步 void，不要 await。
        _userData.SaveUserData(user, item, data, reason, cancellationToken);
    }
}
