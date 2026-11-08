using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Text;
using System.Text.Json;

namespace PotPlayerBridge.Core;

/// <summary>
/// 调用 Jellyfin 插件的 /PotPlayerBridge/Resolve。
/// 安全约束（PLAN 8）：
/// <list type="bullet">
/// <item>Token 只放在请求头里，不写日志、不落盘；</item>
/// <item>自签名证书只对「配置的 Jellyfin 主机」放宽校验，其它 HTTPS 站点仍走系统默认校验。</item>
/// </list>
/// </summary>
public sealed class JellyfinClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Logger _log;

    /// <summary>
    /// 按「协议+主机+端口」缓存 HttpClient。
    /// 早先每次请求都 new 一个，结果每 5 秒一次的进度上报反复做 TLS 握手，
    /// 实测会间歇性报 "The SSL connection could not be established"；
    /// 复用连接后既稳定又省资源。代理是常驻进程，不需要释放这些实例。
    /// </summary>
    private readonly ConcurrentDictionary<string, HttpClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Initializes a new instance of the <see cref="JellyfinClient"/> class.</summary>
    /// <param name="log">日志。</param>
    public JellyfinClient(Logger log)
    {
        _log = log;
    }

    /// <summary>解析播放计划。</summary>
    /// <param name="serverUrl">服务器地址。</param>
    /// <param name="accessToken">访问令牌（只在内存中使用）。</param>
    /// <param name="itemId">点击的项 ID。</param>
    /// <param name="intent">意图。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>播放计划。</returns>
    public async Task<PlaybackPlan> ResolveAsync(
        string serverUrl,
        string accessToken,
        string itemId,
        string intent,
        CancellationToken cancellationToken)
    {
        var baseUri = new Uri(serverUrl.TrimEnd('/'));
        var endpoint = new Uri(baseUri, "/PotPlayerBridge/Resolve");

        var client = GetClient(baseUri);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            "MediaBrowser Token=\"" + accessToken + "\"");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { itemId, intent }, JsonOptions),
            Encoding.UTF8,
            "application/json");

        _log.Info("请求播放计划：" + endpoint + "，item=" + itemId + "，intent=" + intent);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new PlaybackException(
                502,
                "连接 Jellyfin 服务器失败：" + ex.Message,
                "server-unreachable");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var message = TryReadMessage(text) ?? ("服务器返回 HTTP " + (int)response.StatusCode);
                _log.Warn("解析播放计划失败：" + (int)response.StatusCode + " " + message);
                throw new PlaybackException((int)response.StatusCode, message, "resolve-failed");
            }

            PlaybackPlan? plan;
            try
            {
                plan = JsonSerializer.Deserialize<PlaybackPlan>(text, JsonOptions);
            }
            catch (JsonException ex)
            {
                throw new PlaybackException(502, "服务器返回的播放计划无法解析：" + ex.Message, "bad-plan");
            }

            if (plan is null || plan.Playlist.Count == 0)
            {
                throw new PlaybackException(502, "服务器返回了空的播放列表。", "empty-plan");
            }

            if (plan.StartIndex < 1 || plan.StartIndex > plan.Playlist.Count)
            {
                throw new PlaybackException(502, "服务器返回的起播序号越界。", "bad-start-index");
            }

            return plan;
        }
    }

    /// <summary>
    /// 上报一次播放进度。失败不抛异常（PLAN 6.9：进度上报不能打断播放），
    /// 内部按指数退避重试；全部失败返回 null，由调用方决定记日志。
    /// </summary>
    /// <param name="serverUrl">服务器地址。</param>
    /// <param name="accessToken">访问令牌（只在内存中使用）。</param>
    /// <param name="report">上报内容。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>服务器结果；网络失败或无响应时返回 null。</returns>
    public async Task<ProgressResult?> ReportProgressAsync(
        string serverUrl,
        string accessToken,
        ProgressReport report,
        CancellationToken cancellationToken)
    {
        var baseUri = new Uri(serverUrl.TrimEnd('/'));
        var endpoint = new Uri(baseUri, "/PotPlayerBridge/Progress");
        var body = JsonSerializer.Serialize(report, JsonOptions);

        // 3 次尝试：1s → 2s → 4s。播放本身不受影响，所以这里可以慢一点等网络恢复。
        var delays = new[] { 1000, 2000, 4000 };

        for (var attempt = 0; attempt <= delays.Length; attempt++)
        {
            if (attempt > 0)
            {
                try
                {
                    await Task.Delay(delays[attempt - 1], cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }

            try
            {
                var client = GetClient(baseUri);
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.TryAddWithoutValidation(
                    "Authorization",
                    "MediaBrowser Token=\"" + accessToken + "\"");
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    // 4xx 是请求本身的问题（登录失效、item 不存在），重试没有意义。
                    if ((int)response.StatusCode is >= 400 and < 500)
                    {
                        _log.Warn("进度上报被拒绝：" + (int)response.StatusCode + " " + Truncate(text));
                        return null;
                    }

                    _log.Warn("进度上报失败：" + (int)response.StatusCode + "，将重试。");
                    continue;
                }

                try
                {
                    var result = JsonSerializer.Deserialize<ProgressResult>(text, JsonOptions);
                    return result;
                }
                catch (JsonException)
                {
                    // 服务器返回了非预期内容（例如被反代改成 HTML），当作失败重试。
                    _log.Warn("进度上报返回内容无法解析，将重试：" + Truncate(text));
                    continue;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception ex)
            {
                _log.Warn("进度上报异常（第 " + (attempt + 1) + " 次）：" + ex.Message);
            }
        }

        return null;
    }

    private static string Truncate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "（空响应）";
        }

        return text.Length > 200 ? text[..200] : text;
    }

    /// <summary>
    /// 下载服务器上的二进制内容（字幕）。走和 Resolve 相同的证书策略与 Authorization 头。
    /// </summary>
    /// <param name="serverUrl">服务器地址。</param>
    /// <param name="accessToken">访问令牌（只在内存中使用）。</param>
    /// <param name="relativeUrl">服务器返回的相对地址，例如 /Videos/{id}/{ms}/Subtitles/0/Stream.subrip。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>内容字节；失败返回 null。</returns>
    public async Task<byte[]?> DownloadAsync(
        string serverUrl,
        string accessToken,
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        var baseUri = new Uri(serverUrl.TrimEnd('/'));
        var endpoint = relativeUrl.StartsWith("/", StringComparison.Ordinal)
            ? new Uri(baseUri, relativeUrl)
            : new Uri(new Uri(baseUri + "/"), relativeUrl);

        var client = GetClient(baseUri);

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            "MediaBrowser Token=\"" + accessToken + "\"");

        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            _log.Warn("下载字幕失败：" + (int)response.StatusCode + " " + endpoint.AbsolutePath);
            return null;
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 取（或创建）指向该服务器的 HttpClient。
    /// 注意不要把返回值放进 using：它是共享实例，dispose 掉会让后续请求全部失败。
    /// </summary>
    /// <param name="baseUri">服务器地址。</param>
    /// <returns>共享的 HttpClient。</returns>
    private HttpClient GetClient(Uri baseUri)
    {
        var key = baseUri.GetLeftPart(UriPartial.Authority);

        return _clients.GetOrAdd(key, _ => new HttpClient(CreateHandler(baseUri))
        {
            // 整季 Resolve 要查库、进度上报要过反代，15 秒对两者都够用。
            Timeout = TimeSpan.FromSeconds(15)
        });
    }

    private static HttpClientHandler CreateHandler(Uri serverUri)
    {
        var handler = new HttpClientHandler();

        // 自签名证书：只对配置的 Jellyfin 主机放宽，避免全局关闭证书校验。
        handler.ServerCertificateCustomValidationCallback = (request, certificate, chain, errors) =>
        {
            if (errors == SslPolicyErrors.None)
            {
                return true;
            }

            return request.RequestUri is not null
                   && string.Equals(request.RequestUri.Host, serverUri.Host, StringComparison.OrdinalIgnoreCase);
        };

        return handler;
    }

    private static string? TryReadMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("message", out var message))
            {
                return message.GetString();
            }

            if (document.RootElement.TryGetProperty("error", out var error))
            {
                return error.GetString();
            }
        }
        catch (JsonException)
        {
            // 不是 JSON 就退回截断文本，便于排查。
        }

        return text.Length > 300 ? text[..300] : text;
    }
}
