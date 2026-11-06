using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PotPlayerBridge;

/// <summary>
/// Jellyfin → PotPlayer 桥接插件（服务器侧）。
/// 服务器只负责「解析语义、生成播放计划、守卫进度回写」，不启动任何本机程序。
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>
{
    /// <summary>插件 ID，必须与 meta.json 中的 guid 完全一致。</summary>
    public static readonly Guid PluginId = Guid.Parse("f70de2ad-8dc4-4efd-9daf-73c9657ae62f");

    /// <summary>
    /// 插件版本（取程序集版本，四段式，例如 1.0.0.0）。
    /// 注入脚本会在浏览器控制台打印它，用来确认「服务器上跑的是哪一版」。
    /// </summary>
    public static readonly string VersionString =
        typeof(Plugin).Assembly.GetName().Version?.ToString(4) ?? "0.0.0.0";

    /// <summary>Initializes a new instance of the <see cref="Plugin"/> class.</summary>
    /// <param name="applicationPaths">Application paths.</param>
    /// <param name="xmlSerializer">Serializer for the configuration file.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>Gets 当前插件实例；早期启动阶段可能为 null。</summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override string Name => "PotPlayer Bridge";

    /// <inheritdoc />
    public override string Description =>
        "把 jellyfin-web 的播放按钮接到本机 PotPlayer：服务器解析播放意图、生成整季播放计划、守卫进度回写。";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>
    /// Gets 当前生效的配置。注入过滤器可能在插件实例就绪前被调用，
    /// 此时返回一份默认配置（等价于「开启注入」），不能因为读不到实例就让网页注入失效。
    /// </summary>
    public static PluginConfiguration Current => Instance?.Configuration ?? new PluginConfiguration();
}
