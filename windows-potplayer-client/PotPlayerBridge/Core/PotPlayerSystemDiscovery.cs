using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace PotPlayerBridge.Core;

/// <summary>
/// Windows 系统层面的 PotPlayer 发现：注册表、potplayer:// 协议、App Paths、开始菜单快捷方式。
/// 从 <see cref="PotPlayerLocator"/> 拆出来的原因：
/// <list type="bullet">
/// <item>这些步骤全部依赖 Windows API，和「常见目录 / 磁盘扫描」是两类不同风险；</item>
/// <item>单文件接近 500 行不利于维护，拆开后每个类只做一件事。</item>
/// </list>
/// </summary>
public sealed class PotPlayerSystemDiscovery
{
    private static readonly string[] DaumSubKeys = { "PotPlayerMini64", "PotPlayer64" };

    private static readonly string[] PreferredNames =
    {
        "PotPlayerMini64.exe",
        "PotPlayerMini.exe",
        "PotPlayer64.exe",
        "PotPlayer.exe"
    };

    private readonly Logger _log;

    /// <summary>Initializes a new instance of the <see cref="PotPlayerSystemDiscovery"/> class.</summary>
    /// <param name="log">日志。</param>
    public PotPlayerSystemDiscovery(Logger log)
    {
        _log = log;
    }

    /// <summary>
    /// 读 HKCU / HKLM（含 32 位视图）下 DAUM 的 PotPlayer 键。
    /// </summary>
    /// <param name="source">命中来源标识。</param>
    /// <returns>路径或 null。</returns>
    public string? TryRegistry(out string source)
    {
        source = "registry";

        var attempts = new (RegistryHive Hive, RegistryView View, string Label)[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default, "registry-hkcu"),
            (RegistryHive.LocalMachine, RegistryView.Registry64, "registry-hklm"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "registry-hklm32")
        };

        foreach (var attempt in attempts)
        {
            try
            {
                var hit = ReadDaumKey(attempt.Hive, attempt.View);
                if (hit is not null)
                {
                    source = attempt.Label;
                    return hit;
                }
            }
            catch (Exception ex)
            {
                // 精简系统/权限受限环境读注册表可能抛异常；继续尝试下一步。
                _log.Warn("读取 DAUM 注册表失败（" + attempt.Label + "）：" + ex.Message);
            }
        }

        return null;
    }

    /// <summary>
    /// 读 potplayer:// 协议命令，解析出 exe 路径。
    /// </summary>
    /// <param name="source">命中来源标识。</param>
    /// <returns>路径或 null。</returns>
    public string? TryProtocolCommand(out string source)
    {
        var attempts = new (RegistryHive Hive, RegistryView View, string Label)[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64, "protocol-hklm"),
            (RegistryHive.LocalMachine, RegistryView.Registry32, "protocol-hklm32"),
            (RegistryHive.CurrentUser, RegistryView.Default, "protocol-hkcu")
        };

        foreach (var attempt in attempts)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(attempt.Hive, attempt.View);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Classes\potplayer\shell\open\command");
                var commandLine = key?.GetValue(null) as string;
                var exe = ExtractExecutable(commandLine);
                if (PotPlayerLocator.IsValidPotPlayer(exe))
                {
                    source = attempt.Label;
                    return exe;
                }
            }
            catch (Exception ex)
            {
                _log.Warn("读取 potplayer 协议失败（" + attempt.Label + "）：" + ex.Message);
            }
        }

        source = "protocol";
        return null;
    }

    /// <summary>
    /// 读 App Paths 注册项。
    /// </summary>
    /// <param name="source">命中来源标识。</param>
    /// <returns>路径或 null。</returns>
    public string? TryAppPaths(out string source)
    {
        source = "app-paths";

        foreach (var name in PreferredNames)
        {
            foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            {
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                        using var key = baseKey.OpenSubKey(
                            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + name);
                        var path = key?.GetValue(null) as string;
                        if (PotPlayerLocator.IsValidPotPlayer(path))
                        {
                            return path;
                        }
                    }
                    catch
                    {
                        // 忽略单个视图的失败，继续尝试。
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 扫描开始菜单快捷方式并解析 .lnk 目标。
    /// </summary>
    /// <returns>路径或 null。</returns>
    public string? TryStartMenu()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs)
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            var hit = SearchShortcuts(root, 0);
            if (hit is not null)
            {
                return hit;
            }
        }

        return null;
    }

    private static string? ReadDaumKey(RegistryHive hive, RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, view);

        foreach (var subKey in DaumSubKeys)
        {
            using var key = baseKey.OpenSubKey(@"SOFTWARE\DAUM\" + subKey);
            if (key is null)
            {
                continue;
            }

            var programPath = key.GetValue("ProgramPath") as string;
            if (PotPlayerLocator.IsValidPotPlayer(programPath))
            {
                return programPath;
            }

            var folder = key.GetValue("ProgramFolder") as string;
            var fromFolder = PotPlayerLocator.PickFromDirectory(folder);
            if (fromFolder is not null)
            {
                return fromFolder;
            }
        }

        return null;
    }

    private string? SearchShortcuts(string directory, int depth)
    {
        if (depth > 3)
        {
            return null;
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.lnk"))
            {
                if (!Path.GetFileName(file).Contains("PotPlayer", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var target = ResolveShortcut(file);
                if (PotPlayerLocator.IsValidPotPlayer(target))
                {
                    return target;
                }
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var hit = SearchShortcuts(child, depth + 1);
                if (hit is not null)
                {
                    return hit;
                }
            }
        }
        catch (Exception ex)
        {
            _log.Warn("扫描开始菜单失败：" + ex.Message);
        }

        return null;
    }

    private static string? ResolveShortcut(string lnkPath)
    {
        try
        {
            // 用 WScript.Shell 解析 .lnk；COM 不可用时返回 null，不影响其它发现步骤。
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            string target = shortcut.TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractExecutable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return null;
        }

        var text = commandLine.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end > 1)
            {
                return text.Substring(1, end - 1);
            }
        }

        // 兼容 `C:\Path\PotPlayerMini64.exe "%1"` 这种没有引号包裹路径的写法。
        var match = Regex.Match(text, "^([^\\s\"]+?\\.exe)\\b", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : null;
    }
}
