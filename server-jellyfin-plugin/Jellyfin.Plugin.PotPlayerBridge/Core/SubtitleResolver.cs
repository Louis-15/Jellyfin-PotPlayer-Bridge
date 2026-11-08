using System;
using System.Collections.Generic;
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
/// 挑选每一集的外挂字幕（PLAN 6.8 / D-07）。
/// <para>
/// 只处理**外挂**字幕：内嵌字幕 PotPlayer 自己能切，插件插手反而会把用户的选择改掉。
/// 选择优先级：简体 &gt; 繁体 &gt; 其它中文 &gt; 第一条外挂；没有外挂就不返回，DPL 里也不写 sub 行。
/// </para>
/// </summary>
public sealed class SubtitleResolver
{
    private static readonly string[] ChineseLanguages = { "zh", "chi", "zho", "chs", "cht" };
    private static readonly string[] SimplifiedHints = { "简", "chs", "sc", "simplified" };
    private static readonly string[] TraditionalHints = { "繁", "cht", "tc", "traditional" };
    private static readonly string[] ChineseHints = { "中", "chinese", "zh", "chi", "zho", "双语", "雙語" };

    private readonly IMediaSourceManager _mediaSources;
    private readonly ILogger<SubtitleResolver> _logger;

    /// <summary>Initializes a new instance of the <see cref="SubtitleResolver"/> class.</summary>
    /// <param name="mediaSources">媒体源服务。</param>
    /// <param name="logger">Logger.</param>
    public SubtitleResolver(IMediaSourceManager mediaSources, ILogger<SubtitleResolver> logger)
    {
        _mediaSources = mediaSources;
        _logger = logger;
    }

    /// <summary>
    /// 选出该条目要用的外挂字幕。返回的相对 URL 由代理带 Token 下载。
    /// </summary>
    /// <param name="item">条目。</param>
    /// <param name="user">用户（字幕列表与用户权限相关）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>最多一条字幕；没有外挂字幕返回空列表。</returns>
    public async Task<List<SubtitleEntry>> ResolveAsync(BaseItem item, User user, CancellationToken cancellationToken)
    {
        try
        {
            var sources = await _mediaSources
                .GetPlaybackMediaSources(item, user, false, false, cancellationToken)
                .ConfigureAwait(false);

            var source = sources.FirstOrDefault();
            if (source is null || source.MediaStreams is null)
            {
                return new List<SubtitleEntry>();
            }

            var external = source.MediaStreams
                .Where(s => s.IsExternal && s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle)
                .ToList();

            if (external.Count == 0)
            {
                return new List<SubtitleEntry>();
            }

            var chosen = PickBest(external);
            if (chosen is null)
            {
                return new List<SubtitleEntry>();
            }

            var codec = string.IsNullOrWhiteSpace(chosen.Codec) ? "srt" : chosen.Codec;

            return new List<SubtitleEntry>
            {
                new()
                {
                    Index = chosen.Index,
                    Codec = codec,
                    Language = chosen.Language,
                    Title = chosen.DisplayTitle ?? chosen.Title,
                    IsExternal = true,
                    Url = string.Format(
                        CultureInfo.InvariantCulture,
                        "/Videos/{0}/{1}/Subtitles/{2}/Stream.{3}",
                        item.Id.ToString("N"),
                        source.Id ?? item.Id.ToString("N"),
                        chosen.Index,
                        codec)
                }
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 拿不到字幕不该影响播放：记一条日志，当作「这集没有字幕」。
            _logger.LogWarning(ex, "取外挂字幕失败，按无字幕处理：{ItemId}", item.Id);
            return new List<SubtitleEntry>();
        }
    }

    /// <summary>
    /// 按「简体 &gt; 繁体 &gt; 其它中文 &gt; 第一条外挂」挑一条。
    /// 抽成静态方法便于单测（字幕选择错了用户会直接看到乱码或外文）。
    /// </summary>
    /// <param name="candidates">外挂字幕流。</param>
    /// <returns>选中的字幕流。</returns>
    public static MediaBrowser.Model.Entities.MediaStream? PickBest(
        IReadOnlyList<MediaBrowser.Model.Entities.MediaStream> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates.FirstOrDefault(IsSimplified)
               ?? candidates.FirstOrDefault(IsTraditional)
               ?? candidates.FirstOrDefault(IsChinese)
               ?? candidates[0];
    }

    private static bool IsSimplified(MediaBrowser.Model.Entities.MediaStream stream)
    {
        return IsChinese(stream) && ContainsAny(Hint(stream), SimplifiedHints);
    }

    private static bool IsTraditional(MediaBrowser.Model.Entities.MediaStream stream)
    {
        return IsChinese(stream) && ContainsAny(Hint(stream), TraditionalHints);
    }

    private static bool IsChinese(MediaBrowser.Model.Entities.MediaStream stream)
    {
        var language = (stream.Language ?? string.Empty).Trim();
        if (ChineseLanguages.Any(l => string.Equals(l, language, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 有些库把语言写成 zh-CN / zh-Hans，用前缀兜住。
        if (language.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsAny(Hint(stream), ChineseHints);
    }

    private static string Hint(MediaBrowser.Model.Entities.MediaStream stream)
    {
        return ((stream.Title ?? string.Empty) + " " + (stream.DisplayTitle ?? string.Empty)).ToLowerInvariant();
    }

    private static bool ContainsAny(string text, IEnumerable<string> keywords)
    {
        return keywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
