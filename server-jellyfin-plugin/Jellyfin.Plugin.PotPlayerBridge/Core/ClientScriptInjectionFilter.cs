using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PotPlayerBridge.Core;

/// <summary>
/// 在 jellyfin-web 的 index.html 里插入客户端脚本。
/// <para>
/// jellyfin-web 是编译好的静态前端，插件无法从服务器侧给页面加按钮，
/// 社区插件通用的做法是：改写页面响应，在 &lt;/body&gt; 前补一个 &lt;script&gt;。
/// 这里全程在内存里改写，不碰磁盘，所以 Docker 只读镜像也能正常工作。
/// 任何一步失败都原样返回，绝不能让插件把 Jellyfin Web 弄坏。
/// </para>
/// </summary>
public sealed class ClientScriptInjectionFilter : IStartupFilter
{
    /// <summary>写进 index.html 的标记，同时用于幂等判断（重复注入只保留一份）。</summary>
    internal const string Marker = "<!--potplayer-bridge-client-->";

    /// <summary>客户端脚本的固定路径。控制器上用同一个字符串，改动请两边同步。</summary>
    internal const string ScriptPath = "/PotPlayerBridge/Client.js";

    /// <summary>
    /// 缓存指纹：取「插件进程启动的时刻」。
    /// 为什么不用程序集文件的写入时间：部署脚本用 <c>unzip -o</c> 解压，会原样恢复 zip 里记录的
    /// 时间戳，所以反复部署时程序集时间几乎不变，指纹不变 → 浏览器一直复用它缓存的旧脚本
    /// （实测：服务器上已是新版，浏览器仍在跑旧代码，改了半天看不到效果）。
    /// 换成进程启动时刻后：同一次运行内指纹稳定（缓存依然有效），重启即换新（强制更新）。
    /// </summary>
    private static readonly string CacheStamp = DateTime.UtcNow.Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture);

    private readonly ILogger<ClientScriptInjectionFilter> _logger;

    // 只在第一次成功注入时写日志，避免每次首页请求都刷一行。
    private int _logged;

    /// <summary>Initializes a new instance of the <see cref="ClientScriptInjectionFilter"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public ClientScriptInjectionFilter(ILogger<ClientScriptInjectionFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // 注册在管道最外层：先剥掉 Accept-Encoding，才能拿到可改写的明文 HTML。
            app.Use(InvokeAsync);
            next(app);
        };
    }

    /// <summary>构造插入 &lt;/body&gt; 前的脚本标签。</summary>
    /// <param name="basePath">服务器挂载前缀，没有则为空字符串。</param>
    /// <returns>要插入的 HTML 片段。</returns>
    internal static string BuildScriptBlock(string basePath)
    {
        return Marker + "<script src=\"" + basePath + ScriptPath + "?v=" + CacheStamp + "\" defer></script>";
    }

    private static string ComputeCacheStamp()
    {
        // 保留给单元测试/诊断用；正式路径直接用进程启动时刻（见 CacheStamp 字段说明）。
        return DateTime.UtcNow.Ticks.ToString("x", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>判断请求是不是 jellyfin-web 的页面外壳。</summary>
    /// <param name="path">请求路径。</param>
    /// <returns>命中 /web、/web/、/web/index.html（可以带前缀）时为 true。</returns>
    internal static bool IsIndexRequest(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
               || path.Equals("/web", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>从请求路径里剥掉 /web 部分，保留反向代理的子路径前缀。</summary>
    /// <param name="path">请求路径。</param>
    /// <returns>形如 <c>""</c> 或 <c>"/jellyfin"</c> 的前缀。</returns>
    internal static string ResolveBasePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var index = path.LastIndexOf("/web", StringComparison.OrdinalIgnoreCase);
        return index > 0 ? path[..index] : string.Empty;
    }

    /// <summary>把脚本标签写进 HTML，或者原样返回。</summary>
    /// <param name="html">原始页面。</param>
    /// <param name="basePath">服务器挂载前缀。</param>
    /// <returns>要返回给浏览器的页面。</returns>
    internal static string Rewrite(string html, string basePath)
    {
        // 已经注入过（例如反向代理缓存了改写后的页面）就不再插第二次。
        if (html.Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            return html;
        }

        var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        if (bodyClose < 0)
        {
            // 页面结构不认识时宁可不动，也不要插到奇怪的位置。
            return html;
        }

        return html[..bodyClose] + BuildScriptBlock(basePath) + "\n" + html[bodyClose..];
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        // 配置里关掉注入后，行为和原生 Jellyfin 完全一致，方便排查问题。
        if (!Plugin.Current.EnableClientInjection
            || !IsIndexRequest(context.Request.Path.Value)
            || !HttpMethods.IsGet(context.Request.Method))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // 要一份完整、未压缩的 200：压缩过或分段（Range）的响应没法安全改写。
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        // index.html 的 ETag 在插件更新后仍可能不变，浏览器会拿 304 继续用旧副本，
        // 导致注入的脚本永远不刷新。丢掉校验头强制拿 200，页面只有几 KB，代价可以忽略。
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next().ConfigureAwait(false);
        }
        catch
        {
            // 下游抛异常时先恢复响应流，避免把管道状态搞坏。
            context.Response.Body = originalBody;
            throw;
        }

        context.Response.Body = originalBody;
        buffer.Seek(0, SeekOrigin.Begin);

        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
                     && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isHtml)
        {
            await buffer.CopyToAsync(originalBody, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        string html;
        using (var reader = new StreamReader(buffer, Encoding.UTF8, true, 1024, leaveOpen: true))
        {
            html = await reader.ReadToEndAsync(context.RequestAborted).ConfigureAwait(false);
        }

        try
        {
            var basePath = context.Request.PathBase.Value + ResolveBasePath(context.Request.Path.Value);
            html = Rewrite(html, (basePath ?? string.Empty).TrimEnd('/'));

            if (Interlocked.Exchange(ref _logged, 1) == 0)
            {
                _logger.LogInformation(
                    "PotPlayer Bridge：已把客户端脚本注入 jellyfin-web（请求时注入，不写磁盘，版本 {Version}）。",
                    Plugin.VersionString);
            }
        }
        catch (Exception ex)
        {
            // 注入失败只影响桥接功能，不能连累 Jellyfin Web 本身。
            _logger.LogWarning(ex, "PotPlayer Bridge：注入客户端脚本失败，返回原始 index.html。");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html;charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");

        // 页面里带着脚本的版本指纹（?v=...），所以页面本身绝不能被缓存：
        // 浏览器如果复用了旧的 index.html，就会一直去加载旧版本的 Client.js，
        // 插件更新后用户完全看不到变化（实测：服务器上已是新版，浏览器仍在跑旧代码）。
        context.Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";

        await originalBody.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
    }
}
