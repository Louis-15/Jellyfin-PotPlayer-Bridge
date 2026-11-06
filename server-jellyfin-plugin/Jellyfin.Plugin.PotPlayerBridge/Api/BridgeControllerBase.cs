using System;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.PotPlayerBridge.Api;

/// <summary>
/// 插件 API 的共用底座：统一 JSON 输出、统一错误结构、统一从 JWT 里取用户 ID。
/// 拆成基类是为了让三个控制器（静态资源 / 播放解析 / 季配置）用同一套响应格式，
/// 前端只需要处理一种错误形状。
/// </summary>
public abstract class BridgeControllerBase : ControllerBase
{
    /// <summary>插件 API 统一使用 camelCase（与注入脚本、Windows 代理的约定一致）。</summary>
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // 中文按原样输出：响应是 JSON API / 独立 JS 文件，不存在内联进 HTML 的 XSS 面，
        // 保留中文可以让 curl、Jellyfin 日志和排错时的可读性大幅提升。
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>当前登录用户 ID。Jellyfin 12.2.0 的 JWT 用 Jellyfin-UserId claim。</summary>
    protected Guid? CurrentUserId
    {
        get
        {
            var value = User.FindFirst("Jellyfin-UserId")?.Value
                        ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
                        ?? User.FindFirst("sub")?.Value;

            return Guid.TryParse(value, out var id) ? id : null;
        }
    }

    /// <summary>把匿名对象序列化成 UTF-8 JSON。</summary>
    /// <param name="payload">响应体。</param>
    /// <returns>JSON 结果。</returns>
    protected IActionResult JsonPayload(object payload)
    {
        return Content(JsonSerializer.Serialize(payload, JsonOptions), "application/json; charset=utf-8");
    }

    /// <summary>
    /// 返回带中文消息的错误。前端/代理统一读 <c>message</c>，
    /// <c>code</c> 只用于需要分支处理的少数场景（例如「需要重新登录」）。
    /// </summary>
    /// <param name="statusCode">HTTP 状态码。</param>
    /// <param name="message">面向用户的中文消息。</param>
    /// <param name="code">可选的机器可读错误码。</param>
    /// <returns>JSON 错误结果。</returns>
    protected IActionResult ErrorResult(int statusCode, string message, string? code = null)
    {
        return new ContentResult
        {
            StatusCode = statusCode,
            ContentType = "application/json; charset=utf-8",
            Content = JsonSerializer.Serialize(new { message, code }, JsonOptions)
        };
    }
}
