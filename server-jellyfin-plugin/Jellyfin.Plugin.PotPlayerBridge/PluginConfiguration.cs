using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PotPlayerBridge;

/// <summary>
/// 插件配置。M1 只包含「注入开关 + 本机代理对接参数」，
/// 片头片尾等季级配置不放在这里（按季单独存 season-settings.json，见 M2）。
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets a value indicating whether 是否向 jellyfin-web 注入客户端脚本。
    /// 关掉后网页完全恢复 Jellyfin 原生行为，便于排查「插件是否应该负责」的问题。
    /// </summary>
    public bool EnableClientInjection { get; set; } = true;

    /// <summary>
    /// Gets or sets 本机 Windows 代理的地址。
    /// 注入脚本只允许访问本机回环地址；端口与代理默认端口 18927 保持一致。
    /// </summary>
    public string AgentBaseUrl { get; set; } = "http://127.0.0.1:18927";

    /// <summary>
    /// Gets or sets 注入脚本做代理健康检查的超时时间（毫秒）。
    /// 默认 800ms：本机回环正常时远小于此值，超时可以快速判定「代理没在运行」。
    /// </summary>
    public int AgentHealthTimeoutMs { get; set; } = 800;

    /// <summary>
    /// Gets or sets 等待本机代理被拉起的总时长（毫秒）。
    /// STRM 直链首播慢、Windows 冷启动也要时间，默认给 15 秒。
    /// </summary>
    public int AgentStartTimeoutMs { get; set; } = 15000;
}
