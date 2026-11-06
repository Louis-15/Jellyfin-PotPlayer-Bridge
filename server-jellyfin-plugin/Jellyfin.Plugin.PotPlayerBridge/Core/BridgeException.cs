namespace Jellyfin.Plugin.PotPlayerBridge.Core;

/// <summary>
/// 可以安全返回给前端/本机代理的异常：带 HTTP 状态码和面向用户的中文消息。
/// 其它异常继续按 500 处理，避免把内部细节（路径、堆栈）泄露到网页。
/// </summary>
public sealed class BridgeException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="BridgeException"/> class.</summary>
    /// <param name="statusCode">要返回的 HTTP 状态码。</param>
    /// <param name="message">面向用户的中文消息。</param>
    /// <param name="code">可选的机器可读错误码。</param>
    public BridgeException(int statusCode, string message, string? code = null)
        : base(message)
    {
        StatusCode = statusCode;
        Code = code;
    }

    /// <summary>Gets 要返回的 HTTP 状态码。</summary>
    public int StatusCode { get; }

    /// <summary>Gets 可选的机器可读错误码（前端用于分支处理）。</summary>
    public string? Code { get; }
}
