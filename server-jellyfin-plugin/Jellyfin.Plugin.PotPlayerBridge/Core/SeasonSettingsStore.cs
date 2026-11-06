using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.PotPlayerBridge.Core;

/// <summary>
/// 季级片头片尾配置的持久化存储。
/// <para>
/// 存储位置：插件数据目录下的 <c>season-settings.json</c>。
/// 设计取舍：
/// <list type="bullet">
/// <item>整体读入内存 + 一把异步锁，配置量很小（几十季），没必要上数据库；</item>
/// <item>写入先落到同目录临时文件再原子改名，断电/重启不会留下半个 JSON；</item>
/// <item>原文件损坏时先备份再写新文件，绝不静默丢弃用户已经调好的秒数。</item>
/// </list>
/// </para>
/// </summary>
public sealed class SeasonSettingsStore
{
    /// <summary>允许的最大秒数（2 小时）。超过基本可以确定是手误，直接拒绝。</summary>
    public const int MaxSeconds = 7200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly ILogger<SeasonSettingsStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<Guid, SeasonSettings>? _cache;
    private bool _loadFailed;

    /// <summary>Initializes a new instance of the <see cref="SeasonSettingsStore"/> class.</summary>
    /// <param name="logger">Logger.</param>
    public SeasonSettingsStore(ILogger<SeasonSettingsStore> logger)
    {
        _logger = logger;
    }

    private string FilePath
    {
        get
        {
            var folder = Plugin.Instance?.DataFolderPath;
            if (string.IsNullOrWhiteSpace(folder))
            {
                // 插件实例还没就绪（极早期请求）时给一个临时目录，保证读取返回空配置；
                // 真正写入时实例一定已经就绪，不会把配置写到临时目录。
                folder = Path.Combine(Path.GetTempPath(), "potplayer-bridge");
            }

            Directory.CreateDirectory(folder);
            return Path.Combine(folder, "season-settings.json");
        }
    }

    /// <summary>读取某一季的配置；没有配置时返回 null（表示「不跳过」）。</summary>
    /// <param name="seasonId">季 ID。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>配置或 null。</returns>
    public async Task<SeasonSettings?> GetAsync(Guid seasonId, CancellationToken cancellationToken)
    {
        if (seasonId == Guid.Empty)
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            return cache.TryGetValue(seasonId, out var settings) ? settings : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 保存一季的配置。调用方负责把「单集」换算成它所属的季。
    /// </summary>
    /// <param name="seasonId">季 ID。</param>
    /// <param name="introSeconds">片头秒数（0 表示不跳过）。</param>
    /// <param name="outroSeconds">片尾秒数（0 表示不跳过）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>保存后的配置。</returns>
    public async Task<SeasonSettings> SaveAsync(Guid seasonId, int introSeconds, int outroSeconds, CancellationToken cancellationToken)
    {
        if (seasonId == Guid.Empty)
        {
            throw new BridgeException(400, "缺少季 ID，无法保存片头片尾。", "no-season");
        }

        ValidateSeconds(introSeconds, "片头");
        ValidateSeconds(outroSeconds, "片尾");

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var cache = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            var settings = new SeasonSettings
            {
                SeasonId = seasonId,
                IntroSeconds = introSeconds,
                OutroSeconds = outroSeconds,
                UpdatedAt = DateTimeOffset.UtcNow
            };

            cache[seasonId] = settings;
            await PersistCoreAsync(cache, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "已保存季配置：season={SeasonId} intro={Intro}s outro={Outro}s",
                seasonId,
                introSeconds,
                outroSeconds);
            return settings;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void ValidateSeconds(int value, string label)
    {
        if (value < 0 || value > MaxSeconds)
        {
            throw new BridgeException(400, $"{label}秒数必须在 0～{MaxSeconds} 之间。", "bad-seconds");
        }
    }

    private async Task<Dictionary<Guid, SeasonSettings>> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var cache = new Dictionary<Guid, SeasonSettings>();
        var path = FilePath;

        if (File.Exists(path))
        {
            try
            {
                using var stream = File.OpenRead(path);
                var file = await JsonSerializer
                    .DeserializeAsync<SeasonSettingsFile>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);

                foreach (var item in file?.Items ?? new List<SeasonSettings>())
                {
                    if (item.SeasonId != Guid.Empty)
                    {
                        cache[item.SeasonId] = item;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is JsonException or IOException or NotSupportedException)
            {
                // 读坏了不代表配置可以丢：标记一下，下次保存前先把原文件改名备份。
                _loadFailed = true;
                _logger.LogWarning(ex, "季配置读取失败，本次按空配置运行；原文件会在下次保存时备份。");
            }
        }

        _cache = cache;
        return cache;
    }

    private async Task PersistCoreAsync(Dictionary<Guid, SeasonSettings> cache, CancellationToken cancellationToken)
    {
        var path = FilePath;

        if (_loadFailed && File.Exists(path))
        {
            var backup = path + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            try
            {
                File.Move(path, backup, overwrite: true);
                _logger.LogWarning("已备份损坏的季配置文件：{Backup}", backup);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "备份损坏的季配置文件失败，继续写入新文件。");
            }

            _loadFailed = false;
        }

        var payload = new SeasonSettingsFile
        {
            Items = cache.Values.OrderBy(s => s.SeasonId).ToList()
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var temp = path + ".tmp";

        // 先写临时文件再原子改名：即使进程在写入过程中被杀，原文件也仍然完整。
        await File.WriteAllTextAsync(temp, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true);
    }

    private sealed class SeasonSettingsFile
    {
        public List<SeasonSettings> Items { get; set; } = new();
    }
}
