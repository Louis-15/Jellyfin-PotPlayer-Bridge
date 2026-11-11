using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.PotPlayerBridge;

/// <summary>
/// Jellyfin → PotPlayer 桥接插件（服务器侧）。
/// 服务器只负责「解析播放意图、生成整季播放列表、回写播放进度」，不启动任何本机程序。
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
    /// <remarks>
    /// 这段文字直接显示在 Jellyfin 插件详情页，写成人话而不是内部术语。
    /// 分句而不是列表：插件页按纯文本渲染，换行和连字符不会变成排版。
    /// </remarks>
    public override string Description =>
        "在 Jellyfin 网页上点播放，直接在本机 PotPlayer 打开，不用下载、不用复制地址。"
        + "点某一集会生成整季播放列表并从这一集开始，播完自动跳下一集；"
        + "播放进度实时回写，手机上看到哪、电脑上就从哪接着播，自动续播；"
        + "可按季设置片头片尾长度，自动跳过片头、到片尾自动跳下一集；"
        + "播放器异常读到 0 时不会把观看记录冲掉；"
        + "电影和单片视频会自动挂载 Jellyfin 里的外挂中文字幕；"
        + "网页卡片左上角显示已看与总时长角标，并自动补全 STRM 缺失的时长。"
        + "本插件是服务器侧，真正启动 PotPlayer 的是同一仓库的 Windows 本地代理，两个都装才能正常使用。";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <summary>
    /// Gets 当前生效的配置。注入过滤器可能在插件实例就绪前被调用，
    /// 此时返回一份默认配置（等价于「开启注入」），不能因为读不到实例就让网页注入失效。
    /// </summary>
    public static PluginConfiguration Current => Instance?.Configuration ?? new PluginConfiguration();
}
