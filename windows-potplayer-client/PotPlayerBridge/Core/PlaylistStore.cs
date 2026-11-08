using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PotPlayerBridge.Core;

/// <summary>写 DPL 的结果。</summary>
/// <param name="DplPath">DPL 文件完整路径。</param>
/// <param name="PlaylistDirectory">实际写入的目录。</param>
public sealed record PlaylistWriteResult(string DplPath, string PlaylistDirectory);

/// <summary>
/// DPL 文件管理（PLAN 6.4 / 6.4.1）：
/// <list type="bullet">
/// <item>固定写本程序自己的数据目录（<c>%LocalAppData%\PotPlayerBridge\playlists</c>）；
///       文件名就是 PotPlayer 的列表标签名；</item>
/// <item>写前备份同目录的旧列表（最多 10 份快照）；</item>
/// <item>用 created-playlists.json 记录「本代理创建过哪些文件」，删除时只碰自己的；</item>
/// <item>写完校验非本代理文件数量没有减少，异常时从快照恢复。</item>
/// </list>
/// </summary>
public sealed class PlaylistStore
{
    private const int MaxOwnedPlaylists = 5;
    private const int MaxBackups = 10;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly Logger _log;

    /// <summary>Initializes a new instance of the <see cref="PlaylistStore"/> class.</summary>
    /// <param name="log">日志。</param>
    public PlaylistStore(Logger log)
    {
        _log = log;
    }

    private string ManifestPath => Path.Combine(AppPaths.DataDirectory, "created-playlists.json");

    private string BackupRoot => Path.Combine(AppPaths.DataDirectory, "playlist-backup");

    /// <summary>写 DPL 的目录：固定用本程序的数据目录，绝不写 PotPlayer 安装目录下的 Playlist。</summary>
    private static string PlaylistDirectory => Path.Combine(AppPaths.DataDirectory, "playlists");

    /// <summary>写入一个播放计划对应的 DPL。</summary>
    /// <param name="plan">播放计划。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入结果。</returns>
    public async Task<PlaylistWriteResult> WriteAsync(
        PlaybackPlan plan,
        CancellationToken cancellationToken)
    {
        var directory = PlaylistDirectory;
        Directory.CreateDirectory(directory);

        var manifest = LoadManifest();
        var fileName = SanitizeFileName(plan.PlaylistName) + ".dpl";
        var target = ResolveTargetPath(directory, fileName, manifest);

        // 1. 写前快照 + 记录外部文件数量
        var backup = CreateBackup(directory);
        var foreignBefore = CountForeignFiles(directory, manifest);

        // 2. 写入（UTF-8 BOM + CRLF；DPL 内容由 DplBuilder 生成）
        var content = DplBuilder.Build(plan);
        await File.WriteAllTextAsync(target, content, new UTF8Encoding(true), cancellationToken).ConfigureAwait(false);
        _log.Info(
            "已写入 DPL：" + target +
            "（列表 " + plan.Playlist.Count + " 项，起播 " + plan.StartIndex + "）");

        // 3. 登记 + 清理只属于本代理的旧文件
        manifest = Register(manifest, target, directory);
        SaveManifest(manifest);
        PruneOwned(directory, manifest);

        // 4. 写后校验：外部文件不能变少，否则立即从快照恢复
        var foreignAfter = CountForeignFiles(directory, manifest);
        if (foreignAfter < foreignBefore)
        {
            RestoreBackup(backup, directory);
            _log.Error("检测到列表目录的外部文件数量减少（" + foreignBefore + " -> " + foreignAfter + "），已从快照恢复：" + backup);
            throw new PlaybackException(
                500,
                "写入播放列表后检测到列表目录的其它文件减少，已从备份恢复。请检查 PotPlayer 是否运行正常。",
                "playlist-verify-failed");
        }

        return new PlaylistWriteResult(target, directory);
    }

    /// <summary>在启动播放器前，为指定目录做一次快照（供 abnormalExit 恢复流程使用）。</summary>
    /// <param name="directory">目录。</param>
    /// <returns>快照目录，失败/空目录返回 null。</returns>
    public string? BackupDirectory(string directory)
    {
        return CreateBackup(directory);
    }

    /// <summary>
    /// 清理早期版本留在 PotPlayer 安装目录 <c>Playlist</c> 里的 DPL。
    /// 为什么必须清：PotPlayer 会把该目录里的每个 .dpl 当成常驻「专辑」标签，
    /// 于是同一部剧会同时出现「专辑 誓言无声 第1季」和本次播放的「*誓言无声 第1季」两个同名条目。
    /// 只删清单里登记过、且不在当前目录的项，用户自己的专辑一律不碰。
    /// </summary>
    public void RemoveLegacyFiles()
    {
        var manifest = LoadManifest();
        if (manifest.Items.Count == 0)
        {
            return;
        }

        var current = Path.GetFullPath(PlaylistDirectory);
        var legacy = manifest.Items
            .Where(i => !string.Equals(
                Path.GetFullPath(i.Directory),
                current,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (legacy.Count == 0)
        {
            return;
        }

        foreach (var item in legacy)
        {
            try
            {
                var path = Path.Combine(item.Directory, item.FileName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _log.Info("已清理旧版本留下的 DPL（避免 PotPlayer 多出同名专辑）：" + path);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("清理旧版本 DPL 失败：" + item.FileName + "：" + ex.Message);
            }

            manifest.Items.Remove(item);
        }

        SaveManifest(manifest);
    }

    private static string ResolveTargetPath(string directory, string fileName, PlaylistManifest manifest)
    {
        var target = Path.Combine(directory, fileName);

        if (!File.Exists(target))
        {
            return target;
        }

        // 同名文件是我们自己创建过的：允许覆盖。
        if (manifest.Items.Any(i => IsSameEntry(i, directory, fileName)))
        {
            return target;
        }

        // 用户自己的同名专辑：绝不覆盖，改名保存。
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        for (var index = 2; index < 100; index++)
        {
            var candidateName = baseName + " (" + index.ToString(CultureInfo.InvariantCulture) + ").dpl";
            var candidate = Path.Combine(directory, candidateName);
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        var random = baseName + " (" + Guid.NewGuid().ToString("N")[..6] + ").dpl";
        return Path.Combine(directory, random);
    }

    private static string SanitizeFileName(string? name)
    {
        var value = string.IsNullOrWhiteSpace(name) ? "PotPlayerBridge" : name.Trim();

        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        // Windows 不允许文件名以点或空格结尾。
        value = value.TrimEnd('.', ' ');
        return value.Length == 0 ? "PotPlayerBridge" : value;
    }

    private string? CreateBackup(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return null;
            }

            var files = Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                return null;
            }

            Directory.CreateDirectory(BackupRoot);
            var snapshot = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(snapshot);

            foreach (var file in files)
            {
                var destination = Path.Combine(snapshot, Path.GetFileName(file));
                File.Copy(file, destination, overwrite: true);
            }

            PruneBackups();
            return snapshot;
        }
        catch (Exception ex)
        {
            // 备份失败不应该阻断播放，但必须留下明确日志：后面的校验会因此退化为尽力而为。
            _log.Warn("备份列表目录失败：" + ex.Message);
            return null;
        }
    }

    private void PruneBackups()
    {
        try
        {
            var directories = Directory.GetDirectories(BackupRoot)
                .OrderByDescending(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                .Skip(MaxBackups)
                .ToList();

            foreach (var directory in directories)
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("清理旧备份失败：" + ex.Message);
        }
    }

    private static void RestoreBackup(string? snapshot, string directory)
    {
        if (snapshot is null || !Directory.Exists(snapshot))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(snapshot, "*", SearchOption.TopDirectoryOnly))
        {
            var destination = Path.Combine(directory, Path.GetFileName(file));
            File.Copy(file, destination, overwrite: true);
        }
    }

    private int CountForeignFiles(string directory, PlaylistManifest manifest)
    {
        try
        {
            return Directory.GetFiles(directory, "*.dpl", SearchOption.TopDirectoryOnly)
                .Count(file => !manifest.Items.Any(i => IsSameEntry(i, directory, Path.GetFileName(file))));
        }
        catch
        {
            return 0;
        }
    }

    private PlaylistManifest LoadManifest()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            if (!File.Exists(ManifestPath))
            {
                return new PlaylistManifest();
            }

            var json = File.ReadAllText(ManifestPath, Encoding.UTF8);
            var manifest = JsonSerializer.Deserialize<PlaylistManifest>(json, JsonOptions) ?? new PlaylistManifest();
            manifest.Items ??= new List<CreatedPlaylist>();
            return manifest;
        }
        catch (Exception ex)
        {
            _log.Warn("读取 DPL 清单失败，按空清单处理：" + ex.Message);
            return new PlaylistManifest();
        }
    }

    private void SaveManifest(PlaylistManifest manifest)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        var json = JsonSerializer.Serialize(manifest, JsonOptions);
        var temp = ManifestPath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, ManifestPath, overwrite: true);
    }

    private static PlaylistManifest Register(PlaylistManifest manifest, string path, string directory)
    {
        var fileName = Path.GetFileName(path);
        manifest.Items.RemoveAll(i => IsSameEntry(i, directory, fileName));
        manifest.Items.Add(new CreatedPlaylist
        {
            FileName = fileName,
            Directory = directory,
            CreatedAt = DateTimeOffset.UtcNow
        });
        return manifest;
    }

    private void PruneOwned(string directory, PlaylistManifest manifest)
    {
        var ours = manifest.Items
            .Where(i => string.Equals(i.Directory, directory, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(i => i.CreatedAt)
            .ToList();

        foreach (var old in ours.Skip(MaxOwnedPlaylists))
        {
            try
            {
                var path = Path.Combine(directory, old.FileName);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _log.Info("已清理旧 DPL：" + path);
                }
            }
            catch (Exception ex)
            {
                _log.Warn("清理旧 DPL 失败：" + old.FileName + "：" + ex.Message);
            }

            manifest.Items.Remove(old);
        }
    }

    private static bool IsSameEntry(CreatedPlaylist item, string directory, string fileName)
    {
        return string.Equals(item.FileName, fileName, StringComparison.OrdinalIgnoreCase)
               && string.Equals(item.Directory, directory, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PlaylistManifest
    {
        public List<CreatedPlaylist> Items { get; set; } = new();
    }

    private sealed class CreatedPlaylist
    {
        public string FileName { get; set; } = string.Empty;

        public string Directory { get; set; } = string.Empty;

        public DateTimeOffset CreatedAt { get; set; }
    }
}
