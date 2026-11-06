using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PotPlayerBridge.Http;

/// <summary>
/// 手写的极简 HTTP/1.1 服务，只绑定 127.0.0.1。
/// <para>
/// 为什么不用 HttpListener：它走 HTTP.sys，非管理员运行时通常需要 URL ACL（netsh http add urlacl），
/// 而本项目要求「无需管理员权限即可运行」。TcpListener 直接开套接字，没有这个限制。
/// </para>
/// <para>
/// 支持范围刻意很小：GET/POST/OPTIONS、Content-Length、Connection: close。
/// 不支持分块传输/长连接/keep-alive，这些本机代理都用不到。
/// </para>
/// </summary>
public sealed class LocalHttpServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 1024 * 1024;
    private const int ReadTimeoutMs = 15000;

    private readonly int _port;
    private readonly Func<HttpRequest, Task<HttpResponse>> _handler;
    private readonly Logger _log;
    private readonly TcpListener _listener;

    private CancellationTokenSource? _cts;
    private Task? _acceptTask;

    /// <summary>Initializes a new instance of the <see cref="LocalHttpServer"/> class.</summary>
    /// <param name="port">端口。</param>
    /// <param name="handler">路由处理函数（异步：播放请求要等服务器解析与播放器启动）。</param>
    /// <param name="log">日志。</param>
    public LocalHttpServer(int port, Func<HttpRequest, Task<HttpResponse>> handler, Logger log)
    {
        _port = port;
        _handler = handler;
        _log = log;

        // 只监听回环地址：局域网/公网都连不上。
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    /// <summary>Gets 监听端口。</summary>
    public int Port => _port;

    /// <summary>启动监听。端口被占用时会抛 <see cref="SocketException"/>。</summary>
    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listener.Start();
        _acceptTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
        _log.Info($"HTTP 服务已启动：http://127.0.0.1:{_port}/");
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            _cts?.Cancel();
            _listener.Stop();
        }
        catch
        {
            // 关闭阶段的异常无需处理。
        }

        try
        {
            _acceptTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 忽略等待超时。
        }

        _cts?.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                _log.Warn("接受连接失败：" + ex.Message);
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = ReadTimeoutMs;
                client.SendTimeout = ReadTimeoutMs;

                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, cancellationToken).ConfigureAwait(false);
                if (request is null)
                {
                    await WriteResponseAsync(stream, BadRequest(), cancellationToken).ConfigureAwait(false);
                    return;
                }

                HttpResponse response;
                try
                {
                    response = await _handler(request).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.Error("处理请求失败：" + request.Method + " " + request.Path, ex);
                    response = HttpResponse.Json(
                        new { code = "internal-error", message = "本地代理内部错误：" + ex.Message },
                        500);
                }

                await WriteResponseAsync(stream, response, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // 单个连接出错不能影响监听循环；浏览器/脚本会看到连接被断开并重试。
                _log.Warn("连接处理异常：" + ex.Message);
            }
        }
    }

    private static HttpResponse BadRequest()
    {
        return HttpResponse.Json(new { code = "bad-request", message = "请求格式不正确。" }, 400);
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxHeaderBytes)
            {
                return null;
            }

            headerEnd = IndexOfHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
        }

        var all = buffer.GetBuffer();
        var headerText = Encoding.Latin1.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0)
        {
            return null;
        }

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3)
        {
            return null;
        }

        var method = requestLine[0].ToUpperInvariant();
        if (method is not ("GET" or "POST" or "OPTIONS"))
        {
            return null;
        }

        var rawTarget = requestLine[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                continue;
            }

            var colon = lines[i].IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim();
            headers[name] = value;
        }

        // 明确不接受分块传输：本机代理的调用方（注入脚本/设置页/本机程序）都发固定长度。
        if (headers.ContainsKey("Transfer-Encoding"))
        {
            return null;
        }

        var contentLength = 0;
        if (headers.TryGetValue("Content-Length", out var lengthText)
            && (!int.TryParse(lengthText, out contentLength) || contentLength < 0))
        {
            return null;
        }

        if (contentLength > MaxBodyBytes)
        {
            return null;
        }

        var headerLength = headerEnd + 4;
        var alreadyRead = (int)buffer.Length - headerLength;
        var body = new byte[contentLength];
        var copied = Math.Min(alreadyRead, contentLength);
        if (copied > 0)
        {
            Array.Copy(all, headerLength, body, 0, copied);
        }

        var remaining = contentLength - copied;
        var offset = copied;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(body.AsMemory(offset, remaining), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            offset += read;
            remaining -= read;
        }

        var (path, query) = ParseTarget(rawTarget);

        return new HttpRequest
        {
            Method = method,
            Path = path,
            RawTarget = rawTarget,
            Query = query,
            Headers = headers,
            Body = body
        };
    }

    private static (string Path, Dictionary<string, string> Query) ParseTarget(string rawTarget)
    {
        var queryIndex = rawTarget.IndexOf('?');
        var path = queryIndex >= 0 ? rawTarget[..queryIndex] : rawTarget;
        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (queryIndex >= 0 && queryIndex < rawTarget.Length - 1)
        {
            foreach (var pair in rawTarget[(queryIndex + 1)..].Split('&'))
            {
                if (string.IsNullOrEmpty(pair))
                {
                    continue;
                }

                var eq = pair.IndexOf('=');
                var name = eq >= 0 ? pair[..eq] : pair;
                var value = eq >= 0 ? pair[(eq + 1)..] : string.Empty;
                query[Uri.UnescapeDataString(name)] = Uri.UnescapeDataString(value);
            }
        }

        return (path, query);
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, HttpResponse response, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.Append("HTTP/1.1 ").Append(response.StatusCode).Append(' ').Append(Reason(response.StatusCode)).Append("\r\n");
        builder.Append("Server: PotPlayerBridge\r\n");
        builder.Append("Date: ").Append(DateTime.UtcNow.ToString("R")).Append("\r\n");
        builder.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        builder.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");

        // 本机代理没有长连接需求：响应完立即关闭，实现简单且不会留下半开连接。
        builder.Append("Connection: close\r\n");

        foreach (var header in response.Headers)
        {
            builder.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
        }

        builder.Append("\r\n");

        var head = Encoding.ASCII.GetBytes(builder.ToString());
        await stream.WriteAsync(head, cancellationToken).ConfigureAwait(false);
        if (response.Body.Length > 0)
        {
            await stream.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
        }

        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Reason(int statusCode)
    {
        return statusCode switch
        {
            200 => "OK",
            204 => "No Content",
            400 => "Bad Request",
            401 => "Unauthorized",
            403 => "Forbidden",
            404 => "Not Found",
            409 => "Conflict",
            413 => "Payload Too Large",
            500 => "Internal Server Error",
            501 => "Not Implemented",
            _ => "OK"
        };
    }
}
