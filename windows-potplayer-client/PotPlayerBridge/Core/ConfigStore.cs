using System.Text;
using System.Text.Json;

namespace PotPlayerBridge.Core;

/// <summary>
/// config.json 的读写。
/// 写入采用「同目录临时文件 + 原子改名」，避免程序在写一半时被结束导致配置文件损坏。
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _gate = new();
    private readonly Logger _log;
    private AgentConfig _config;

    /// <summary>Initializes a new instance of the <see cref="ConfigStore"/> class.</summary>
    /// <param name="log">日志。</param>
    public ConfigStore(Logger log)
    {
        _log = log;
        _config = Load();
    }

    /// <summary>Gets 配置文件路径。</summary>
    public string FilePath => AppPaths.ConfigFile;

    /// <summary>拿到当前配置的副本。</summary>
    /// <returns>配置副本。</returns>
    public AgentConfig GetSnapshot()
    {
        lock (_gate)
        {
            return _config.Clone();
        }
    }

    /// <summary>保存配置（会先规范化）。</summary>
    /// <param name="config">新配置。</param>
    public void Save(AgentConfig config)
    {
        Normalize(config);
        lock (_gate)
        {
            _config = config.Clone();
            Persist(_config);
        }
    }

    /// <summary>规范化用户输入，避免奇怪的空白/端口把运行逻辑弄坏。</summary>
    /// <param name="config">配置。</param>
    public static void Normalize(AgentConfig config)
    {
        config.ServerUrl = (config.ServerUrl ?? string.Empty).Trim().TrimEnd('/');
        if (config.Port is < 1 or > 65535)
        {
            config.Port = 18927;
        }

        config.PotPlayerPath = string.IsNullOrWhiteSpace(config.PotPlayerPath)
            ? null
            : config.PotPlayerPath.Trim();

        config.ExtraAllowedOrigins ??= new List<string>();
        config.ExtraAllowedOrigins = config.ExtraAllowedOrigins
            .Where(o => !string.IsNullOrWhiteSpace(o))
            .Select(o => o.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private AgentConfig Load()
    {
        try
        {
            AppPaths.EnsureDataDirectory();
            if (!File.Exists(FilePath))
            {
                return new AgentConfig();
            }

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            var config = JsonSerializer.Deserialize<AgentConfig>(json, JsonOptions) ?? new AgentConfig();
            Normalize(config);
            return config;
        }
        catch (Exception ex)
        {
            // 配置读坏时不要直接覆盖：先备份，再以默认配置运行，用户还能手工找回。
            _log.Warn("读取配置失败，使用默认配置：" + ex.Message);
            TryBackupBrokenConfig();
            return new AgentConfig();
        }
    }

    private void TryBackupBrokenConfig()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            var backup = FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            File.Copy(FilePath, backup, overwrite: true);
            _log.Warn("已备份损坏的配置：" + backup);
        }
        catch (Exception ex)
        {
            _log.Warn("备份损坏配置失败：" + ex.Message);
        }
    }

    private void Persist(AgentConfig config)
    {
        AppPaths.EnsureDataDirectory();
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var temp = FilePath + ".tmp";

        // 显式 UTF-8（无 BOM）：用户用记事本改配置时不会看到多余字符。
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, FilePath, overwrite: true);
    }
}
