using System.Globalization;
using System.Text;

namespace PotPlayerBridge.Core;

/// <summary>
/// 把服务器返回的外挂字幕下载到本地，供 PotPlayer 命令行 <c>/sub=</c> 引用（PLAN 6.8）。
/// <para>
/// 为什么必须落盘：<c>/sub=</c> 只接受本地文件路径，不接受 URL。
/// </para>
/// <para>
/// 为什么只给「单集直启」用：PotPlayer 的 DPL 播放列表格式没有字幕字段，
/// 整季连播时无法把字幕带进去；只有直接播放视频地址时 <c>/sub=</c> 才生效。
/// </para>
/// <para>
/// 目录按 playSessionId 隔离，播放期间不能被清理；7 天后由下次启动时顺手回收（N-04）。
/// </para>
/// </summary>
public sealed class SubtitleDownloader
{
    private const int RetentionDays = 7;

    private readonly JellyfinClient _jellyfin;
    private readonly Logger _log;

    /// <summary>Initializes a new instance of the <see cref="SubtitleDownloader"/> class.</summary>
    /// <param name="jellyfin">服务器调用（复用它的 Token 头与证书策略）。</param>
    /// <param name="log">日志。</param>
    public SubtitleDownloader(JellyfinClient jellyfin, Logger log)
    {
        _jellyfin = jellyfin;
        _log = log;
    }

    private static string RootDirectory => Path.Combine(AppPaths.DataDirectory, "subtitles");

    /// <summary>
    /// 下载某一集/某一部电影的外挂字幕。
    /// 失败只影响字幕本身（返回 null，播放继续），绝不让整次播放入口失败。
    /// </summary>
    /// <param name="entry">要播放的那一项。</param>
    /// <param name="playSessionId">播放会话 ID（用作目录名，隔离不同会话）。</param>
    /// <param name="serverUrl">服务器地址。</param>
    /// <param name="accessToken">访问令牌（只在内存中使用）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>本地字幕绝对路径；该集没有外挂字幕或下载失败时为 null。</returns>
    public async Task<string?> DownloadOneAsync(
        PlaylistEntry entry,
        string playSessionId,
        string serverUrl,
        string accessToken,
        CancellationToken cancellationToken)
    {
        // 服务器已按「简体 > 繁体 > 其他中文 > 第一条」挑好，这里只取第一条。
        if (entry.Subtitles.Count == 0)
        {
            return null;
        }

        var subtitle = entry.Subtitles[0];
        if (string.IsNullOrWhiteSpace(subtitle.Url))
        {
            return null;
        }

        CleanupOldSessions();

        var directory = Path.Combine(RootDirectory, Sanitize(playSessionId));

        try
        {
            Directory.CreateDirectory(directory);

            var bytes = await _jellyfin
                .DownloadAsync(serverUrl, accessToken, subtitle.Url, cancellationToken)
                .ConfigureAwait(false);

            if (bytes is null || bytes.Length == 0)
            {
                _log.Warn("字幕下载为空，跳过：" + entry.Title);
                return null;
            }

            var path = Path.Combine(directory, BuildFileName(entry, subtitle));

            // 字幕编码五花八门（UTF-8 / GBK / UTF-16），PotPlayer 主要靠 BOM 判断。
            // 没有 BOM 的 UTF-8 中文在部分机器上会显示成乱码，这里按 UTF-8 BOM 统一一次。
            var normalized = EnsureUtf8Bom(bytes);
            await File.WriteAllBytesAsync(path, normalized, cancellationToken).ConfigureAwait(false);

            _log.Info("已下载字幕：" + entry.Title + " → " + Path.GetFileName(path) + "（" + bytes.Length + " 字节）");
            return path;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 字幕是增强功能：失败就当这次没有字幕，播放继续。
            _log.Warn("下载字幕失败（" + entry.Title + "）：" + ex.Message);
            return null;
        }
    }

    /// <summary>清理 7 天前的字幕目录（N-04）。</summary>
    public void CleanupOldSessions()
    {
        try
        {
            if (!Directory.Exists(RootDirectory))
            {
                return;
            }

            var deadline = DateTime.UtcNow - TimeSpan.FromDays(RetentionDays);

            foreach (var directory in Directory.GetDirectories(RootDirectory))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < deadline)
                    {
                        Directory.Delete(directory, recursive: true);
                        _log.Info("已清理过期字幕目录：" + Path.GetFileName(directory));
                    }
                }
                catch
                {
                    // 单个目录被占用（正在播放）就跳过，下次再清。
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("清理字幕目录失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 用「S01E02.chi.srt」这种可读名字，出问题时一眼能看出是第几集。
    /// 没有集号（电影）时退化成 itemId 前缀。
    /// </summary>
    private static string BuildFileName(PlaylistEntry entry, SubtitleEntry subtitle)
    {
        var baseName = entry.ParentIndexNumber is > 0 && entry.IndexNumber is > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                "S{0:00}E{1:00}",
                entry.ParentIndexNumber.Value,
                entry.IndexNumber.Value)
            : entry.ItemId[..Math.Min(8, entry.ItemId.Length)];

        var language = string.IsNullOrWhiteSpace(subtitle.Language) ? "und" : subtitle.Language;

        // 防目录穿越：语言来自服务器，仍按最保守的方式过滤一遍。
        return Sanitize(baseName) + "." + Sanitize(language) + "." + ToExtension(subtitle.Codec);
    }

    /// <summary>
    /// Jellyfin 的 codec 名（subrip / ass / webvtt / pgssub …）不是文件名扩展名。
    /// 必须映射成标准扩展名，否则 PotPlayer 的字幕过滤器不认，
    /// 会表现为「命令没错、文件也下载了，就是没有字幕」（实测 .subrip 就是这样被忽略的）。
    /// </summary>
    private static string ToExtension(string? codec)
    {
        return (codec ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "subrip" or "srt" or "text" or "mov_text" => "srt",
            "ass" => "ass",
            "ssa" => "ssa",
            "webvtt" or "vtt" => "vtt",
            "subviewer" or "subviewer1" => "sub",
            "microdvd" => "sub",
            "sami" or "smi" => "smi",
            "ttml" or "dfxp" => "ttml",
            "pgssub" or "hdmv_pgs_subtitle" or "pgs" => "sup",
            "dvdsub" or "dvd_subtitle" => "sub",
            _ => "srt"
        };
    }

    private static string Sanitize(string value)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var ch in value)
        {
            builder.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '_');
        }

        return builder.Length == 0 ? "und" : builder.ToString();
    }

    /// <summary>
    /// 统一成「UTF-8 带 BOM」。
    /// 已经有 UTF-8 BOM 或 UTF-16 BOM 的原样返回（UTF-16 的 BOM 也够 PotPlayer 判断编码）。
    /// </summary>
    private static byte[] EnsureUtf8Bom(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return bytes;
        }

        if (bytes.Length >= 2
            && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            return bytes;
        }

        var bom = Encoding.UTF8.GetPreamble();
        var combined = new byte[bom.Length + bytes.Length];
        Buffer.BlockCopy(bom, 0, combined, 0, bom.Length);
        Buffer.BlockCopy(bytes, 0, combined, bom.Length, bytes.Length);
        return combined;
    }
}
