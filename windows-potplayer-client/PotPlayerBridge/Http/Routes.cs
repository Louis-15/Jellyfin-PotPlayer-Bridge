using System.Text.Json;
using PotPlayerBridge.Core;

namespace PotPlayerBridge.Http;

/// <summary>
/// 本机代理的路由与安全策略。
/// <para>
/// 安全模型（PLAN 8）：
/// <list type="bullet">
/// <item>只监听 127.0.0.1；</item>
/// <item>有 Origin 时必须命中配置的 Jellyfin 源或本机设置页；</item>
/// <item>/health 与 /api/* 必须带 X-PPB-Client: 1 —— 浏览器跨域想加自定义头必须先预检，
///       恶意网页无法绕过 CORS 静默调用；</item>
/// <item>不读取、不记录、不落盘任何 Token。</item>
/// </list>
/// </para>
/// </summary>
public sealed class Routes
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<string> SettingsHtml = new(LoadSettingsHtml);

    private readonly ConfigStore _config;
    private readonly PotPlayerLocator _locator;
    private readonly AgentRuntime _runtime;
    private readonly PlaybackService _playback;
    private readonly Logger _log;

    /// <summary>Initializes a new instance of the <see cref="Routes"/> class.</summary>
    /// <param name="config">配置。</param>
    /// <param name="locator">PotPlayer 路径发现。</param>
    /// <param name="runtime">运行期状态。</param>
    /// <param name="playback">播放服务。</param>
    /// <param name="log">日志。</param>
    public Routes(
        ConfigStore config,
        PotPlayerLocator locator,
        AgentRuntime runtime,
        PlaybackService playback,
        Logger log)
    {
        _config = config;
        _locator = locator;
        _runtime = runtime;
        _playback = playback;
        _log = log;
    }

    /// <summary>处理一个请求。</summary>
    /// <param name="request">请求。</param>
    /// <returns>响应。</returns>
    public async Task<HttpResponse> HandleAsync(HttpRequest request)
    {
        var origin = request.Header("Origin");

        // 来源校验放在最前面：即使后续路由写错，也不会把数据暴露给任意网页。
        if (!string.IsNullOrWhiteSpace(origin) && !IsAllowedOrigin(origin))
        {
            return Deny(403, "origin-not-allowed", "请求来源不在允许范围内。", null);
        }

        if (request.Method == "OPTIONS")
        {
            // 预检请求：只回 CORS 头，不执行业务逻辑。
            return WithCors(HttpResponse.Empty(204), origin);
        }

        var needsClientHeader = request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase)
                                || request.Path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase);
        if (needsClientHeader
            && !string.Equals(request.Header("X-PPB-Client"), "1", StringComparison.Ordinal))
        {
            return Deny(403, "missing-client-header", "缺少 X-PPB-Client 请求头。", origin);
        }

        try
        {
            if (request.Method == "GET" && IsPath(request, "/settings"))
            {
                return WithCors(SettingsPage(), origin);
            }

            if (request.Method == "GET" && IsPath(request, "/health"))
            {
                return WithCors(Health(), origin);
            }

            if (request.Method == "GET" && IsPath(request, "/api/settings"))
            {
                return WithCors(HttpResponse.Json(BuildSettings()), origin);
            }

            if (request.Method == "POST" && IsPath(request, "/api/settings"))
            {
                return WithCors(SaveSettings(request), origin);
            }

            if (request.Method == "GET" && IsPath(request, "/api/detect"))
            {
                return WithCors(Detect(), origin);
            }

            if (request.Method == "POST" && IsPath(request, "/api/open-settings"))
            {
                _runtime.RequestOpenSettings();
                return WithCors(HttpResponse.Json(new { ok = true }), origin);
            }

            if (request.Method == "POST" && IsPath(request, "/api/play"))
            {
                return WithCors(await PlayAsync(request).ConfigureAwait(false), origin);
            }

            return WithCors(
                HttpResponse.Json(new { code = "not-found", message = "接口不存在。" }, 404),
                origin);
        }
        catch (Exception ex)
        {
            _log.Error("处理请求异常：" + request.Method + " " + request.Path, ex);
            return WithCors(
                HttpResponse.Json(new { code = "internal-error", message = "本地代理内部错误：" + ex.Message }, 500),
                origin);
        }
    }

    private HttpResponse Health()
    {
        var config = _config.GetSnapshot();
        var detection = _locator.DetectFast(config.PotPlayerPath);
        var uptime = (int)(DateTimeOffset.UtcNow - _runtime.StartedAt).TotalSeconds;

        return HttpResponse.Json(new
        {
            ok = true,
            version = _runtime.Version,
            port = _runtime.Port,
            uptimeSeconds = uptime,
            potPlayerPath = detection.Path,
            potPlayerSource = detection.Source
        });
    }

    private HttpResponse Detect()
    {
        var config = _config.GetSnapshot();
        var result = _locator.DetectFull(config.PotPlayerPath);

        if (result.Path is null)
        {
            return HttpResponse.Json(
                new
                {
                    code = "potplayer-not-found",
                    message = "没有找到 PotPlayer。请先安装 PotPlayer，或在设置页手动指定 PotPlayerMini64.exe 的完整路径。",
                    settingsUrl = $"http://127.0.0.1:{_runtime.Port}/settings"
                },
                409);
        }

        if (!string.Equals(config.PotPlayerPath, result.Path, StringComparison.OrdinalIgnoreCase))
        {
            config.PotPlayerPath = result.Path;
            _config.Save(config);
            _log.Info("已更新 PotPlayer 路径：" + result.Path + "（来源：" + result.Source + "）");
        }

        return HttpResponse.Json(new
        {
            found = true,
            path = result.Path,
            source = result.Source,
            candidates = result.Candidates.Take(10).ToList()
        });
    }

    private async Task<HttpResponse> PlayAsync(HttpRequest request)
    {
        if (request.Body.Length == 0)
        {
            return HttpResponse.Json(new { code = "empty-body", message = "请求体为空。" }, 400);
        }

        PlayRequest? play;
        try
        {
            play = JsonSerializer.Deserialize<PlayRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return HttpResponse.Json(new { code = "bad-json", message = "请求体不是合法的 JSON。" }, 400);
        }

        if (play is null)
        {
            return HttpResponse.Json(new { code = "empty-body", message = "请求体为空。" }, 400);
        }

        try
        {
            var result = await _playback.PlayAsync(play, CancellationToken.None).ConfigureAwait(false);

            // 返回足够的诊断信息：列表名/起播项/DPL 路径/进程号。
            // 注意：这里绝不回传 Token。
            return HttpResponse.Json(new
            {
                ok = true,
                playlistName = result.Plan.PlaylistName,
                startIndex = result.Plan.StartIndex,
                playlistCount = result.Plan.Playlist.Count,
                itemId = result.Plan.ItemId,
                itemType = result.Plan.ItemType,
                dplPath = result.Playlist.DplPath,
                playlistDir = result.Playlist.PlaylistDirectory,
                usedFallback = result.Playlist.UsedFallback,
                processId = result.Launch.ProcessId,
                windowHandle = result.Launch.WindowHandle.ToInt64()
            });
        }
        catch (PlaybackException ex)
        {
            _log.Warn("播放失败（" + ex.Code + "）：" + ex.Message);
            return HttpResponse.Json(new { code = ex.Code, message = ex.Message }, ex.StatusCode);
        }
    }

    private HttpResponse SaveSettings(HttpRequest request)
    {
        if (request.Body.Length == 0)
        {
            return HttpResponse.Json(new { code = "empty-body", message = "请求体为空。" }, 400);
        }

        SettingsUpdate? update;
        try
        {
            update = JsonSerializer.Deserialize<SettingsUpdate>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return HttpResponse.Json(new { code = "bad-json", message = "请求体不是合法的 JSON。" }, 400);
        }

        if (update is null)
        {
            return HttpResponse.Json(new { code = "empty-body", message = "请求体为空。" }, 400);
        }

        var config = _config.GetSnapshot();

        if (update.ServerUrl is not null)
        {
            var url = update.ServerUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return HttpResponse.Json(
                    new { code = "bad-server-url", message = "服务器地址必须是 http:// 或 https:// 开头的完整地址。" },
                    400);
            }

            config.ServerUrl = url;
        }

        if (update.PotPlayerPath is not null)
        {
            var path = update.PotPlayerPath.Trim();
            if (path.Length == 0)
            {
                config.PotPlayerPath = null;
            }
            else if (!PotPlayerLocator.IsValidPotPlayer(path))
            {
                return HttpResponse.Json(
                    new { code = "bad-potplayer-path", message = "路径不存在，或文件名里不含 PotPlayer。" },
                    400);
            }
            else
            {
                config.PotPlayerPath = path;
            }
        }

        if (update.Port is int port && port != _runtime.Port)
        {
            // 端口写死在注入脚本的 AgentBaseUrl 里（默认 18927），改端口要两边一起改，
            // 属于 M7 安装文档才会涉及的操作；这里明确拒绝，避免用户以为改完就生效。
            return HttpResponse.Json(
                new
                {
                    code = "port-fixed",
                    message = $"端口当前固定为 {_runtime.Port}。如需更换，请同时修改 Jellyfin 插件配置并重启两边。"
                },
                400);
        }

        _config.Save(config);
        _log.Info("设置已保存：server=" + config.ServerUrl + "，potPlayer=" + (config.PotPlayerPath ?? "（未设置）"));

        return HttpResponse.Json(new
        {
            saved = true,
            settings = BuildSettings()
        });
    }

    private object BuildSettings()
    {
        var config = _config.GetSnapshot();
        var detection = _locator.DetectFast(config.PotPlayerPath);

        return new
        {
            serverUrl = config.ServerUrl,
            potPlayerPath = config.PotPlayerPath,
            detectedPath = detection.Path,
            detectedSource = detection.Source,
            port = _runtime.Port,
            version = _runtime.Version,
            configFile = _config.FilePath,
            logFile = AppPaths.LogFile
        };
    }

    private static HttpResponse SettingsPage()
    {
        var response = HttpResponse.Text(SettingsHtml.Value, "text/html; charset=utf-8");
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; connect-src 'self'";
        return response;
    }

    private bool IsAllowedOrigin(string origin)
    {
        var config = _config.GetSnapshot();
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Uri.TryCreate(config.ServerUrl, UriKind.Absolute, out var server))
        {
            allowed.Add(server.GetLeftPart(UriPartial.Authority));
        }

        foreach (var extra in config.ExtraAllowedOrigins)
        {
            if (Uri.TryCreate(extra, UriKind.Absolute, out var uri))
            {
                allowed.Add(uri.GetLeftPart(UriPartial.Authority));
            }
        }

        // 设置页自身的同源 POST 也会带 Origin，必须放行本机地址。
        allowed.Add($"http://127.0.0.1:{_runtime.Port}");
        allowed.Add($"http://localhost:{_runtime.Port}");

        return allowed.Contains(origin.TrimEnd('/'));
    }

    private static HttpResponse WithCors(HttpResponse response, string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return response;
        }

        response.Headers["Access-Control-Allow-Origin"] = origin;
        response.Headers["Vary"] = "Origin";
        response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
        response.Headers["Access-Control-Allow-Headers"] = "Content-Type, X-PPB-Client";
        response.Headers["Access-Control-Allow-Private-Network"] = "true";
        response.Headers["Access-Control-Max-Age"] = "600";
        return response;
    }

    private static HttpResponse Deny(int status, string code, string message, string? origin)
    {
        return WithCors(HttpResponse.Json(new { code, message }, status), origin);
    }

    private static bool IsPath(HttpRequest request, string path)
    {
        return request.Path.Equals(path, StringComparison.OrdinalIgnoreCase);
    }

    private static string LoadSettingsHtml()
    {
        var assembly = typeof(Routes).Assembly;
        const string Suffix = ".settings.html";

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return reader.ReadToEnd();
        }

        return "<!doctype html><meta charset=\"utf-8\"><h1>设置页资源缺失</h1><p>请重新安装 PotPlayer Bridge。</p>";
    }

    /// <summary>POST /api/settings 的请求体。</summary>
    private sealed class SettingsUpdate
    {
        public string? ServerUrl { get; set; }

        public string? PotPlayerPath { get; set; }

        public int? Port { get; set; }
    }
}
