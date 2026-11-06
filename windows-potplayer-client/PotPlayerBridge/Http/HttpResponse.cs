using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PotPlayerBridge.Http;

/// <summary>要写回浏览器的响应。</summary>
public sealed class HttpResponse
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // 中文原样输出，设置页/curl 调试时可读；响应是 JSON，不存在内联 HTML 的 XSS 面。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Gets 状态码。</summary>
    public int StatusCode { get; init; } = 200;

    /// <summary>Gets Content-Type。</summary>
    public string ContentType { get; init; } = "application/json; charset=utf-8";

    /// <summary>Gets 响应体。</summary>
    public byte[] Body { get; init; } = Array.Empty<byte>();

    /// <summary>Gets 额外响应头。</summary>
    public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>构造 JSON 响应。</summary>
    /// <param name="payload">任意可序列化对象。</param>
    /// <param name="statusCode">状态码。</param>
    /// <returns>响应。</returns>
    public static HttpResponse Json(object payload, int statusCode = 200)
    {
        return new HttpResponse
        {
            StatusCode = statusCode,
            ContentType = "application/json; charset=utf-8",
            Body = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOptions))
        };
    }

    /// <summary>构造文本响应。</summary>
    /// <param name="text">文本。</param>
    /// <param name="contentType">Content-Type。</param>
    /// <param name="statusCode">状态码。</param>
    /// <returns>响应。</returns>
    public static HttpResponse Text(string text, string contentType, int statusCode = 200)
    {
        return new HttpResponse
        {
            StatusCode = statusCode,
            ContentType = contentType,
            Body = Encoding.UTF8.GetBytes(text)
        };
    }

    /// <summary>构造空响应（用于 OPTIONS 预检）。</summary>
    /// <param name="statusCode">状态码。</param>
    /// <returns>响应。</returns>
    public static HttpResponse Empty(int statusCode)
    {
        return new HttpResponse
        {
            StatusCode = statusCode,
            ContentType = "text/plain; charset=utf-8",
            Body = Array.Empty<byte>()
        };
    }
}
