using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PotPlayerBridge.Core;

/// <summary>
/// 计算每一项的播放地址。
/// STRM 项目的 MediaSourceInfo.Path 就是网盘直链（实测），直接交给 PotPlayer；
/// 其它情况回退到 Jellyfin 的 static 直链，由本机代理补 Token。
/// </summary>
public sealed class StreamUrlResolver
{
    private readonly IMediaSourceManager _mediaSources;
    private readonly ILogger<StreamUrlResolver> _logger;

    /// <summary>Initializes a new instance of the <see cref="StreamUrlResolver"/> class.</summary>
    /// <param name="mediaSources">播放地址服务。</param>
    /// <param name="logger">Logger.</param>
    public StreamUrlResolver(IMediaSourceManager mediaSources, ILogger<StreamUrlResolver> logger)
    {
        _mediaSources = mediaSources;
        _logger = logger;
    }

    /// <summary>解析一项的播放地址。</summary>
    /// <param name="item">目标项。</param>
    /// <param name="user">用户（影响权限与字幕选择，地址本身通常与用户无关）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>绝对 HTTP 地址或 Jellyfin 相对路径。</returns>
    public async Task<string> ResolveAsync(BaseItem item, User user, CancellationToken cancellationToken)
    {
        try
        {
            var sources = await _mediaSources
                .GetPlaybackMediaSources(item, user, false, false, cancellationToken)
                .ConfigureAwait(false);

            var source = sources.FirstOrDefault();
            if (source is null)
            {
                return BuildRelative(item, null);
            }

            if (!string.IsNullOrWhiteSpace(source.Path)
                && (source.Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                    || source.Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                return source.Path;
            }

            return BuildRelative(item, source.Id);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 单集取地址失败不应该让整次播放失败：回退到 Jellyfin 直链，代理会补 Token。
            _logger.LogWarning(ex, "取播放地址失败，回退到 Jellyfin 直链：{ItemId}", item.Id);
            return BuildRelative(item, null);
        }
    }

    private static string BuildRelative(BaseItem item, string? mediaSourceId)
    {
        var url = string.Format(CultureInfo.InvariantCulture, "/Videos/{0}/stream?static=true", item.Id.ToString("N"));
        if (!string.IsNullOrWhiteSpace(mediaSourceId))
        {
            url += "&MediaSourceId=" + Uri.EscapeDataString(mediaSourceId);
        }

        return url;
    }
}
