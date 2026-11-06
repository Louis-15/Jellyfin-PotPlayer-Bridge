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

        using var handler = CreateHandler(baseUri);
        using var client = new HttpClient(handler)
        {
            // 整季列表 + 每集取地址，服务器侧要查库，给足 30 秒。
            Timeout = TimeSpan.FromSeconds(30)
        };

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
