using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Plugin.PotPlayerBridge.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PotPlayerBridge.Api;

/// <summary>
/// 卡片进度角标的数据来源（PLAN M5.5）。
/// 网页上每个卡片只有进度条、没有数字时间，这个接口一次把「位置 / 总时长 / 是否已看」批量给前端。
/// </summary>
[ApiController]
[Route("PotPlayerBridge")]
public sealed class ItemProgressController : BridgeControllerBase
{
    /// <summary>单次最多查询多少个 item。卡片列表一屏最多几十个，100 足够，同时防止被当成批量爬接口。</summary>
    private const int MaxItems = 100;

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly RuntimeDurationStore _durations;

    /// <summary>Initializes a new instance of the <see cref="ItemProgressController"/> class.</summary>
    /// <param name="library">媒体库。</param>
    /// <param name="users">用户管理。</param>
    /// <param name="userData">用户数据。</param>
    /// <param name="durations">实测时长记录（RunTimeTicks 缺失时兜底）。</param>
    public ItemProgressController(
        ILibraryManager library,
        IUserManager users,
        IUserDataManager userData,
        RuntimeDurationStore durations)
    {
        _library = library;
        _users = users;
        _userData = userData;
        _durations = durations;
    }

    /// <summary>
    /// 批量返回若干 item 的播放进度。找不到的 item 直接省略（前端保持不显示角标）。
    /// </summary>
    /// <param name="itemIds">逗号分隔的 item ID 列表。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>进度列表。</returns>
    [Authorize]
    [HttpGet("ItemProgress")]
    public IActionResult ItemProgress([FromQuery] string? itemIds, CancellationToken cancellationToken)
    {
        var userId = CurrentUserId;
        if (userId is null)
        {
            return ErrorResult(401, "登录状态已失效，请刷新页面后重新登录。", "no-user");
        }

        var user = _users.GetUserById(userId.Value);
        if (user is null)
        {
            return ErrorResult(401, "找不到当前用户，请重新登录。", "no-user");
        }

        var ids = ParseIds(itemIds);
        if (ids.Count == 0)
        {
            return JsonPayload(new { items = Array.Empty<object>() });
        }

        if (ids.Count > MaxItems)
        {
            return ErrorResult(400, $"一次最多查询 {MaxItems} 个条目。", "too-many-items");
        }

        var result = new List<object>(ids.Count);

        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = _library.GetItemById(id);
            if (item is null)
            {
                continue;
            }

            result.Add(BuildEntry(user, item));
        }

        return JsonPayload(new { items = result });
    }

    private static List<Guid> ParseIds(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new List<Guid>();
        }

        // 去重：同一个卡片会在多个容器里重复出现，前端合并后仍可能带重复 ID。
        return raw
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => Guid.TryParse(value, out var id) ? id : Guid.Empty)
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
    }

    private object BuildEntry(User user, BaseItem item)
    {
        var data = _userData.GetUserData(user, item);

        // RunTimeTicks 是扫描时探测的元数据，STRM 条目基本拿不到（扫描器不会去拉远端视频）。
        // 这时用「播放器实测到的时长」兜底：只要这一集被播过一次，角标就能显示数字。
        var runtimeTicks = item.RunTimeTicks ?? 0;
        if (runtimeTicks <= 0)
        {
            runtimeTicks = _durations.Get(item.Id);
        }

        return new
        {
            itemId = item.Id.ToString("N"),
            // 前端要据此决定「该不该显示角标」：剧/季/合集不显示（每集时长不同，没有意义），
            // 只有具体某集、某部电影才有「看到哪/共多久」。
            itemType = item.GetType().Name,
            positionTicks = data?.PlaybackPositionTicks ?? 0,
            runtimeTicks,
            played = data?.Played ?? false
        };
    }
}
