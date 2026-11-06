namespace PotPlayerBridge;

/// <summary>
/// 本机代理的固定路径集合。
/// 全部放在当前用户的 LocalAppData 下，不需要管理员权限，也不会污染安装目录。
/// </summary>
public static class AppPaths
{
    /// <summary>数据目录：配置、日志、以后生成的播放列表/字幕缓存都放这里。</summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PotPlayerBridge");

    /// <summary>配置文件路径。</summary>
    public static string ConfigFile { get; } = Path.Combine(DataDirectory, "config.json");

    /// <summary>日志文件路径。日志里绝不写 Token。</summary>
    public static string LogFile { get; } = Path.Combine(DataDirectory, "agent.log");

    /// <summary>确保数据目录存在。</summary>
    public static void EnsureDataDirectory()
    {
        Directory.CreateDirectory(DataDirectory);
    }
}
