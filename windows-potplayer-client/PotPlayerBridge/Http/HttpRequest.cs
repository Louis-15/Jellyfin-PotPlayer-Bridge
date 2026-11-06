namespace PotPlayerBridge.Http;

/// <summary>解析后的 HTTP 请求。只保留本机代理需要的字段，不做通用 Web 服务器。</summary>
public sealed class HttpRequest
{
    /// <summary>Gets 方法名（大写）：GET / POST / OPTIONS。</summary>
    public string Method { get; init; } = string.Empty;

    /// <summary>Gets 路径（不含查询字符串）。</summary>
    public string Path { get; init; } = "/";

    /// <summary>Gets 原始请求目标（含查询字符串），仅用于日志/排查。</summary>
    public string RawTarget { get; init; } = string.Empty;

    /// <summary>Gets 查询参数。</summary>
    public IReadOnlyDictionary<string, string> Query { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets 请求头（大小写不敏感）。</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets 请求体。</summary>
    public byte[] Body { get; init; } = Array.Empty<byte>();

    /// <summary>读取一个请求头。</summary>
    /// <param name="name">头名。</param>
    /// <returns>值或 null。</returns>
    public string? Header(string name)
    {
        return Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>读取一个查询参数。</summary>
    /// <param name="name">参数名。</param>
    /// <returns>值或 null。</returns>
    public string? QueryValue(string name)
    {
        return Query.TryGetValue(name, out var value) ? value : null;
    }
}
