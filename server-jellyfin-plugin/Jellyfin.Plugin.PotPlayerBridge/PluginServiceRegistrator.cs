using Jellyfin.Plugin.PotPlayerBridge.Core;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.PotPlayerBridge;

/// <summary>
/// 注册插件服务。M1 只有「网页注入」这一件事；
/// 解析器 / 进度守卫等单例在对应里程碑里再加到这里。
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        // 季配置是磁盘上的单文件 JSON，进程内只保留一份缓存，必须单例。
        services.AddSingleton<SeasonSettingsStore>();

        // 进度守卫的状态是「进程内的会话表」，必须单例，否则每次请求都拿到新表、armed 记不住。
        services.AddSingleton<ProgressGuard>();

        // 剧集层级查询与播放地址解析从解析器里拆出来，各自只依赖一个 Jellyfin 服务。
        services.AddSingleton<EpisodeLibraryQuery>();
        services.AddSingleton<StreamUrlResolver>();

        // 解析器无状态，注入 Jellyfin 的库/用户/数据服务。
        services.AddSingleton<PlaybackResolver>();

        // IStartupFilter 让我们能在 Jellyfin 自己的中间件之前包一层，
        // 从而改写 index.html 的响应体（请求时注入，不写磁盘）。
        services.AddSingleton<IStartupFilter, ClientScriptInjectionFilter>();
    }
}
