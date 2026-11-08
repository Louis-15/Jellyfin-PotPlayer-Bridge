using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PotPlayerBridge.Core;

/// <summary>
/// 把「播放器实测到的时长」写回 Jellyfin 的数据库（PLAN M5.5）。
/// <para>
/// 为什么需要：Jellyfin 的 <c>RunTimeTicks</c> 是扫描媒体库时探测出来的。
/// STRM 只是文本指针，扫描器不会为了读时长去拉远端视频，所以这些条目的时长一直是空，
/// 网页卡片角标就算有播放进度也显示不出「16:08 / 30:22」，
/// Jellyfin 自己的排序/筛选/「已看时长统计」也都拿不到这个数字。
/// </para>
/// <para>
/// 为什么写数据库而不是 NFO：<c>BaseItems</c> 表里就有 <c>RunTimeTicks</c> 列
/// （实测确认：该季 E1-E3 有值、我们播过的 E5 为 NULL），
/// 用 Jellyfin 自己的 <see cref="ILibraryManager.UpdateItemAsync"/> 写库即可 ——
/// 不新增文件、不碰用户的媒体目录、不用等元数据刷新，所有用户立刻受益。
/// </para>
/// <para>
/// 只写一次：<c>RunTimeTicks</c> 一旦有值就不该再被我们覆盖（Jellyfin 探测的结果更权威）。
/// 同一个条目在处理失败后允许重试，成功则永久跳过。
/// </para>
/// </summary>
public sealed class RuntimeDurationWriter
{
    /// <summary>
    /// 小于这个值的时长一律不写。
    /// 播放器还没加载完时会报 0，片头/广告偶尔会报出几秒的怪值，
    /// 写进数据库会让卡片角标显示「0:02 / 0:05」这种明显错误的信息。
    /// </summary>
    private static readonly long MinimumTicks = TimeSpan.FromSeconds(60).Ticks;

    /// <summary>已成功写过的条目，避免每次进度上报都去改库。</summary>
    private readonly ConcurrentDictionary<Guid, byte> _succeeded = new();

    private readonly ILibraryManager _library;
    private readonly ILogger<RuntimeDurationWriter> _logger;

    /// <summary>Initializes a new instance of the <see cref="RuntimeDurationWriter"/> class.</summary>
    /// <param name="library">媒体库（提供 UpdateItemAsync）。</param>
    /// <param name="logger">Logger.</param>
    public RuntimeDurationWriter(ILibraryManager library, ILogger<RuntimeDurationWriter> logger)
    {
        _library = library;
        _logger = logger;
    }

    /// <summary>
    /// 判断是否需要写库。抽成静态方法便于单测：这条规则错了会污染用户的媒体库元数据。
    /// </summary>
    /// <param name="existingTicks">条目当前的 RunTimeTicks（null 表示 Jellyfin 没探测到）。</param>
    /// <param name="incomingTicks">本次播放器实测到的时长。</param>
    /// <returns>需要写入返回 true。</returns>
    public static bool ShouldWrite(long? existingTicks, long incomingTicks)
    {
        // Jellyfin 已经有值就绝不覆盖：它可能是探测出来的精确值，也可能是 nfo 里人工维护的。
        if (existingTicks is > 0)
        {
            return false;
        }

        // 太小的一律不写：加载未完成会报 0，片头/广告会报几秒的怪值。
        return incomingTicks >= MinimumTicks;
    }

    /// <summary>
    /// 尝试把实测时长写回数据库。失败只记日志，绝不影响进度上报主链路。
    /// </summary>
    /// <param name="item">要更新的条目。</param>
    /// <param name="durationTicks">播放器实测到的时长（ticks）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>异步任务。</returns>
    public async Task TryWriteAsync(BaseItem item, long durationTicks, CancellationToken cancellationToken)
    {
        if (_succeeded.ContainsKey(item.Id))
        {
            return;
        }

        if (!ShouldWrite(item.RunTimeTicks, durationTicks))
        {
            return;
        }

        try
        {
            item.RunTimeTicks = durationTicks;

            // UpdateItemAsync 是 Jellyfin 官方入口：它会把变更写进 BaseItems 表并触发 ItemUpdated。
            // 用 MetadataEdit 而不是 MetadataImport：后者会触发完整的元数据刷新，
            // 对这种「只补一个数字」的场景太重，还会连带重新下载图片。
            await _library
                .UpdateItemAsync(item, item.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken)
                .ConfigureAwait(false);

            _succeeded[item.Id] = 0;
            _logger.LogInformation(
                "已把实测时长写回数据库：{Name}（{Duration}）",
                item.Name,
                TimeSpan.FromTicks(durationTicks).ToString(@"hh\:mm\:ss"));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 写不进去只是少了角标的数字，不能让进度上报失败；下次上报会再试一次。
            _logger.LogWarning(ex, "写回实测时长失败：{Name}", item.Name);
        }
    }
}
