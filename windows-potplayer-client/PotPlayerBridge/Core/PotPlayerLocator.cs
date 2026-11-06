namespace PotPlayerBridge.Core;

/// <summary>路径发现结果。<see cref="Source"/> 用于设置页告诉用户「从哪里找到的」。</summary>
/// <param name="Path">找到的可执行文件路径；没找到为 null。</param>
/// <param name="Source">来源标识：config / registry / protocol / app-paths / common-dir / disk-scan / start-menu。</param>
/// <param name="Candidates">过程中收集到的其它候选，便于排查。</param>
public sealed record DetectionResult(string? Path, string Source, IReadOnlyList<string> Candidates);

/// <summary>
/// PotPlayer 路径发现（PLAN 6.3 的 8 个步骤）。
/// 设计原则：
/// <list type="bullet">
/// <item>按步骤顺序「命中即停」，避免用户机器上装了多个版本时结果随机；</item>
/// <item>文件必须存在、文件名必须包含 PotPlayer，防止注册表里的旧路径/错误路径被当成有效值；</item>
/// <item>磁盘扫描只在快速步骤都失败后执行，且有目录数/时间上限，不能把 /health 拖死。</item>
/// </list>
/// 注册表 / 协议 / App Paths / 开始菜单这些系统级探测在 <see cref="PotPlayerSystemDiscovery"/>。
/// </summary>
public sealed class PotPlayerLocator
{
    internal static readonly string[] PreferredNames =
    {
        "PotPlayerMini64.exe",
        "PotPlayerMini.exe",
        "PotPlayer64.exe",
        "PotPlayer.exe"
    };

    private static readonly HashSet<string> SkipDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows",
        "$Recycle.Bin",
        "System Volume Information",
        "Recovery",
        "$WinREAgent",
        "Config.Msi",
        "node_modules",
        ".git"
    };

    private readonly Logger _log;
    private readonly PotPlayerSystemDiscovery _system;

    /// <summary>Initializes a new instance of the <see cref="PotPlayerLocator"/> class.</summary>
    /// <param name="log">日志。</param>
    public PotPlayerLocator(Logger log)
    {
        _log = log;
        _system = new PotPlayerSystemDiscovery(log);
    }

    /// <summary>只跑快速步骤（配置、注册表、协议、App Paths、常见目录）。用于 /health，不能卡住。</summary>
    /// <param name="configuredPath">配置里记住的路径。</param>
    /// <returns>发现结果。</returns>
    public DetectionResult DetectFast(string? configuredPath) => Detect(configuredPath, deep: false);

    /// <summary>完整发现，包含磁盘扫描与开始菜单快捷方式。用于 /api/detect 与设置页。</summary>
    /// <param name="configuredPath">配置里记住的路径。</param>
    /// <returns>发现结果。</returns>
    public DetectionResult DetectFull(string? configuredPath) => Detect(configuredPath, deep: true);

    /// <summary>判断一个路径能不能当作 PotPlayer 可执行文件。</summary>
    /// <param name="path">路径。</param>
    /// <returns>有效返回 true。</returns>
    public static bool IsValidPotPlayer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!File.Exists(path))
        {
            return false;
        }

        return Path.GetFileName(path).Contains("PotPlayer", StringComparison.OrdinalIgnoreCase);
    }

    private DetectionResult Detect(string? configuredPath, bool deep)
    {
        var candidates = new List<string>();

        // 1. 配置里记住的路径
        if (IsValidPotPlayer(configuredPath))
        {
            return new DetectionResult(configuredPath, "config", candidates);
        }

        // 2/3. HKCU / HKLM 的 DAUM 键（含 32 位视图）
        var hit = _system.TryRegistry(out var source);
        if (hit is not null)
        {
            return new DetectionResult(hit, source, candidates);
        }

        // 4. potplayer:// 协议命令
        hit = _system.TryProtocolCommand(out source);
        if (hit is not null)
        {
            return new DetectionResult(hit, source, candidates);
        }

        // 5. App Paths
        hit = _system.TryAppPaths(out source);
        if (hit is not null)
        {
            return new DetectionResult(hit, source, candidates);
        }

        // 6. 常见安装目录
        hit = TryCommonDirectories(out source);
        if (hit is not null)
        {
            return new DetectionResult(hit, source, candidates);
        }

        if (deep)
        {
            // 7. 固定磁盘浅层扫描
            hit = TryDiskScan(candidates);
            if (hit is not null)
            {
                return new DetectionResult(hit, "disk-scan", candidates);
            }

            // 8. 开始菜单快捷方式
            hit = _system.TryStartMenu();
            if (hit is not null)
            {
                return new DetectionResult(hit, "start-menu", candidates);
            }
        }

        _log.Warn("没有找到 PotPlayer。");
        return new DetectionResult(null, "not-found", candidates);
    }

    private static string? TryCommonDirectories(out string source)
    {
        source = "common-dir";

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var localPrograms = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs");

        var directories = new List<string>();
        foreach (var root in new[] { programFiles, programFilesX86 })
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                directories.Add(Path.Combine(root, "DAUM", "PotPlayer"));
                directories.Add(Path.Combine(root, "PotPlayer"));
            }
        }

        directories.Add(Path.Combine(localPrograms, "PotPlayer"));
        directories.Add(Path.Combine(localPrograms, "DAUM", "PotPlayer"));

        foreach (var dir in directories)
        {
            var hit = PickFromDirectory(dir);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private string? TryDiskScan(List<string> candidates)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var visited = 0;

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
            {
                continue;
            }

            var queue = new Queue<(string Path, int Depth)>();
            queue.Enqueue((drive.RootDirectory.FullName, 0));

            while (queue.Count > 0)
            {
                if (DateTime.UtcNow > deadline || visited >= 4000)
                {
                    _log.Warn("磁盘扫描达到上限，提前结束。");
                    return null;
                }

                var (dir, depth) = queue.Dequeue();
                visited++;

                var hit = PickFromDirectory(dir);
                if (hit is not null)
                {
                    return hit;
                }

                if (depth >= 2)
                {
                    continue;
                }

                foreach (var child in SafeEnumerateDirectories(dir))
                {
                    var name = Path.GetFileName(child);
                    if (SkipDirectoryNames.Contains(name))
                    {
                        continue;
                    }

                    queue.Enqueue((child, depth + 1));
                }
            }
        }

        return null;
    }

    internal static string? PickFromDirectory(string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return null;
        }

        foreach (var name in PreferredNames)
        {
            var candidate = Path.Combine(directory, name);
            if (IsValidPotPlayer(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToList();
        }
        catch
        {
            // 权限不足/路径失效的目录直接跳过。
            return Array.Empty<string>();
        }
    }
}
