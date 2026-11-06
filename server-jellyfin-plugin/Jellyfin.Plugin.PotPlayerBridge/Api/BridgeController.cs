using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using Jellyfin.Plugin.PotPlayerBridge;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PotPlayerBridge.Api;

/// <summary>
/// 服务器插件对外暴露的静态资源入口。
/// <list type="bullet">
/// <item>Client.js：注入脚本本体（登录前也要能加载，所以匿名）。</item>
/// <item>SeasonPanel.js / SeasonSettings.css：季配置面板，由 Client.js 在登录后动态加载。</item>
/// <item>Version：给部署自检和本机代理做兼容性判断用。</item>
/// </list>
/// 解析播放计划、季配置、进度回写等数据接口在 PlaybackController / SeasonSettingsController 里，统一要求登录。
/// </summary>
[ApiController]
[Route("PotPlayerBridge")]
public sealed class BridgeController : BridgeControllerBase
{
    // 资源只从内嵌文件读一次；Client.js 每次请求只做配置占位符替换。
    private static readonly Lazy<string> ClientScriptTemplate = new(() => LoadEmbedded(".clientScript.js"));
    private static readonly Lazy<string> SeasonPanelScript = new(() => LoadEmbedded(".seasonPanel.js"));
    private static readonly Lazy<string> PlayClientScript = new(() => LoadEmbedded(".playClient.js"));
    private static readonly Lazy<string> SeasonSettingsStyles = new(() => LoadEmbedded(".seasonSettings.css"));

    private readonly ILogger<BridgeController> _logger;

    // 只记录第一次脚本请求，用来区分「注入没生效」和「浏览器根本没请求脚本」。
    private int _clientServed;

    /// <summary>Initializes a new instance of the <see cref="BridgeController"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public BridgeController(ILogger<BridgeController> logger)
    {
        _logger = logger;
    }

    /// <summary>返回注入到 jellyfin-web 的客户端脚本。</summary>
    /// <returns>JavaScript 文本。</returns>
    [AllowAnonymous]
    [HttpGet("Client.js")]
    public IActionResult Client()
    {
        if (Interlocked.Exchange(ref _clientServed, 1) == 0)
        {
            _logger.LogInformation("PotPlayer Bridge：客户端脚本被浏览器加载（版本 {Version}）。", Plugin.VersionString);
        }

        var config = Plugin.Current;
        var payload = JsonSerializer.Serialize(
            new
            {
                version = Plugin.VersionString,
                agentBaseUrl = config.AgentBaseUrl,
                healthTimeoutMs = config.AgentHealthTimeoutMs,
                startTimeoutMs = config.AgentStartTimeoutMs
            },
            JsonOptions);

        var script = ClientScriptTemplate.Value.Replace("__PPB_CONFIG_JSON__", payload, StringComparison.Ordinal);

        // 脚本内容跟着插件版本/配置变，必须让浏览器每次校验，不能吃缓存。
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return Content(script, "application/javascript; charset=utf-8");
    }

    /// <summary>返回插件版本与目标 ABI，供部署后自检使用。</summary>
    /// <returns>JSON。</returns>
    [AllowAnonymous]
    [HttpGet("Version")]
    public IActionResult Version()
    {
        Response.Headers.CacheControl = "no-cache";
        return JsonPayload(new
        {
            name = "PotPlayer Bridge",
            version = Plugin.VersionString,
            targetAbi = "12.2.0.0"
        });
    }

    /// <summary>
    /// 返回季配置面板脚本。由 Client.js 在 ApiClient 就绪后动态加载，
    /// 这样 index.html 里仍然只有一个注入标签。
    /// </summary>
    /// <returns>JavaScript 文本。</returns>
    [AllowAnonymous]
    [HttpGet("SeasonPanel.js")]
    public IActionResult SeasonPanel()
    {
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return Content(SeasonPanelScript.Value, "application/javascript; charset=utf-8");
    }

    /// <summary>返回播放按钮接管脚本。由 Client.js 在 ApiClient 就绪后动态加载。</summary>
    /// <returns>JavaScript 文本。</returns>
    [AllowAnonymous]
    [HttpGet("PlayClient.js")]
    public IActionResult PlayClient()
    {
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return Content(PlayClientScript.Value, "application/javascript; charset=utf-8");
    }

    /// <summary>返回季配置面板样式。</summary>
    /// <returns>CSS 文本。</returns>
    [AllowAnonymous]
    [HttpGet("SeasonSettings.css")]
    public IActionResult SeasonSettingsCss()
    {
        Response.Headers.CacheControl = "no-cache, no-store, must-revalidate";
        return Content(SeasonSettingsStyles.Value, "text/css; charset=utf-8");
    }

    private static string LoadEmbedded(string suffix)
    {
        var assembly = typeof(BridgeController).Assembly;

        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null)
            {
                continue;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }

        // 资源没打进 dll 属于打包错误，直接抛出让部署自检立刻发现问题。
        throw new InvalidOperationException($"找不到内嵌资源（Web 目录下以 {suffix} 结尾的文件）。");
    }
}
