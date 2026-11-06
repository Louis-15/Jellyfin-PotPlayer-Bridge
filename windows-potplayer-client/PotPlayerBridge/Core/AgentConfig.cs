namespace PotPlayerBridge.Core;

/// <summary>
/// 本机代理的配置（不含任何 Token）。
/// JSON 里保留 camelCase，与 README、设置页字段一致，便于用户直接手改。
/// </summary>
public sealed class AgentConfig
{
    /// <summary>Jellyfin 服务器地址。默认取本项目联调环境，用户可在设置里改。</summary>
    public string ServerUrl { get; set; } = "https://931002.xyz:8443";

    /// <summary>用户确认/自动发现的 PotPlayer 可执行文件路径。</summary>
    public string? PotPlayerPath { get; set; }

    /// <summary>本机 HTTP 端口。</summary>
    public int Port { get; set; } = 18927;

    /// <summary>额外允许的网页来源（一般不需要，调试其它 Jellyfin 地址时用）。</summary>
    public List<string> ExtraAllowedOrigins { get; set; } = new();

    /// <summary>
    /// Gets or sets a value indicating whether 上次是否因为「PotPlayer 未在 3 秒内退出」而被强制结束。
    /// 下次播放前会先对 Playlist 目录做一次快照，再继续（PLAN 6.5）。
    /// </summary>
    public bool AbnormalExit { get; set; }

    /// <summary>复制一份，避免调用方在锁外改到正在使用的对象。</summary>
    /// <returns>副本。</returns>
    public AgentConfig Clone()
    {
        return new AgentConfig
        {
            ServerUrl = ServerUrl,
            PotPlayerPath = PotPlayerPath,
            Port = Port,
            ExtraAllowedOrigins = new List<string>(ExtraAllowedOrigins),
            AbnormalExit = AbnormalExit
        };
    }
}
