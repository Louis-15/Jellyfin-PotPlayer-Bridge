using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PotPlayerBridge.Core;

/// <summary>
/// 记录「播放器实测到的时长」，给卡片角标兜底（PLAN M5.5）。
/// <para>
/// 为什么需要：Jellyfin 的 <c>RunTimeTicks</c> 是扫描媒体库时探测出来的。
/// STRM 只是文本指针，扫描器不会为了读时长去拉远端视频，所以这些条目的 <c>RunTimeTicks</c> 是 0，
/// 网页角标就算有播放进度也显示不出「16:08 / 30:22」。
/// </para>
/// <para>
/// 但播放时 PotPlayer 能读到真实时长，代理一直在 /Progress 里报 <c>DurationTicks</c>。
/// 这里把它按 itemId 记下来，<see cref="Api.ItemProgressController"/> 在
/// <c>RunTimeTicks</c> 为 0 时用它兜底，于是「播过一次之后角标就有数字了」。
/// </para>
/// <para>
/// 落盘而不是只放内存：Jellyfin 重启后不该又变回没数字。
/// 写入失败只记日志，不影响进度上报主链路。
/// </para>
/// </summary>
public sealed class RuntimeDurationStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>
    /// 小于这个值的时长一律不记。
    /// 播放器还没加载完时会报 0，开场广告/片头偶尔会报出几秒的怪值，
    /// 记下来会让角标显示「0:02 / 0:05」这种明显错误的信息。
    /// </summary>
    private static readonly long MinimumTicks = TimeSpan.FromSeconds(60).Ticks;

    private readonly ILogger<RuntimeDurationStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<Guid, long>? _cache;

    /// <summary>Initializes a new instance of the <see cref="RuntimeDurationStore"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public RuntimeDurationStore(ILogger<RuntimeDurationStore> logger)
    {
        _logger = logger;
    }

    private string FilePath
    {
        get
        {
            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrWhiteSpace(folder))
            {
                // 插件实例还没就绪（极早期请求）时给临时目录：读不到就当没有记录，
                // 真正写入时实例一定已就绪，不会把数据写到临时目录。
                folder = Path.Combine(Path.GetTempPath(), "potplayer-bridge");
            }

            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "runtime-durations.json");
        }
    }

    /// <summary>
    /// 判断一个上报的时长该不该替换已有记录。
    /// 抽成纯函数是为了能单测：这条规则错了会让角标显示成「0:02 / 0:05」这种明显错误的信息。
    /// </summary>
    /// <param name="existingTicks">已记录的时长（0 表示还没记录）。</param>
    /// <param name="incomingTicks">本次上报的时长。</param>
    /// <returns>需要更新返回 true。</returns>
    public static bool ShouldRecord(long existingTicks, long incomingTicks)
    {
        // 太小的一律不记：播放器还没加载完会报 0，片头/广告会报出几秒的怪值。
        if (incomingTicks < MinimumTicks)
        {
            return false;
        }

        // 只增不减：同一条目后来报到更小的值（播放器异常、只播了片段）不覆盖已有记录。
        return incomingTicks > existingTicks;
    }

    /// <summary>取某个条目实测到的时长。</summary>
    /// <param name="itemId">条目 ID。</param>
    /// <returns>时长（ticks）；没有记录返回 0。</returns>
    public long Get(Guid itemId)
    {
        var cache = EnsureLoaded();
        return cache.TryGetValue(itemId, out var value) ? value : 0;
    }

    /// <summary>
    /// 记录一次实测时长。只增不减：同一条目后来报到更小的值（播放器异常、广告片段）不覆盖已有记录。
    /// </summary>
    /// <param name="itemId">条目 ID。</param>
    /// <param name="durationTicks">时长（ticks）。</param>
    /// <returns>异步任务。</returns>
    public async Task RecordAsync(Guid itemId, long durationTicks)
    {
        if (itemId == Guid.Empty)
        {
            return;
        }

        var cache = EnsureLoaded();
        bool changed;

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            cache.TryGetValue(itemId, out var existing);
            changed = ShouldRecord(existing, durationTicks);
            if (changed)
            {
                cache[itemId] = durationTicks;
            }
        }
        finally
        {
            _gate.Release();
        }

        if (changed)
        {
            await SaveAsync().ConfigureAwait(false);
        }
    }

    private Dictionary<Guid, long> EnsureLoaded()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        _gate.Wait();
        try
        {
            if (_cache is not null)
            {
                return _cache;
            }

            try
            {
                var path = FilePath;
                if (File.Exists(path))
                {
                    var json = File.ReadAllText(path);
                    var loaded = JsonSerializer.Deserialize<Dictionary<Guid, long>>(json, JsonOptions);
                    _cache = loaded ?? new Dictionary<Guid, long>();
                }
                else
                {
                    _cache = new Dictionary<Guid, long>();
                }
            }
            catch (Exception ex)
            {
                // 文件坏了不能拖垮整个插件：当成空记录，下次写入会覆盖掉。
                _logger.LogWarning(ex, "读取实测时长记录失败，按空记录处理。");
                _cache = new Dictionary<Guid, long>();
            }

            return _cache;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var json = JsonSerializer.Serialize(_cache ?? new Dictionary<Guid, long>(), JsonOptions);
            await File.WriteAllTextAsync(FilePath, json).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 记不下来只是少了角标的兜底数字，绝不能影响播放进度本身。
            _logger.LogWarning(ex, "保存实测时长记录失败。");
        }
        finally
        {
            _gate.Release();
        }
    }
}
