using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace PotPlayerBridge.Core;

/// <summary>启动结果。</summary>
/// <param name="ProcessId">PotPlayer 进程 ID。</param>
/// <param name="WindowHandle">主窗口句柄（可能为 0，窗口还没创建完）。</param>
public sealed record LaunchResult(int ProcessId, IntPtr WindowHandle);

/// <summary>
/// 启动 PotPlayer 并管理「由本代理拉起」的实例。
/// 约束（PLAN 6.5）：
/// <list type="bullet">
/// <item>用 ArgumentList 传参，中文/空格路径不会被打散；</item>
/// <item>只用 <c>&lt;dpl&gt; /new</c>，绝不使用 /title=（会让 DPL 完全不播放）；</item>
/// <item>新播放先优雅关闭旧实例：WM_COMMAND 20002 → WM_CLOSE → 等 3 秒 → 最后才 Kill；</item>
/// <item>不碰用户自己打开的 PotPlayer，只管理自己记录的 PID。</item>
/// </list>
/// </summary>
public sealed class PotPlayerLauncher
{
    private static readonly JsonSerializerOptions SessionJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    /// <summary>Gets 记录「本次拉起的是哪个 PotPlayer」的文件，供下次启动清理孤儿实例。</summary>
    private static string SessionPath => Path.Combine(AppPaths.DataDirectory, "potplayer-session.json");

    private readonly Logger _log;
    private readonly ConfigStore _config;
    private readonly object _gate = new();

    private Process? _owned;
    private IntPtr _window;

    /// <summary>Initializes a new instance of the <see cref="PotPlayerLauncher"/> class.</summary>
    /// <param name="log">日志。</param>
    /// <param name="config">配置（用于写 abnormalExit 标记）。</param>
    public PotPlayerLauncher(Logger log, ConfigStore config)
    {
        _log = log;
        _config = config;
    }

    /// <summary>Gets 当前由本代理拉起的进程 ID；没有则为 null。</summary>
    public int? OwnedProcessId
    {
        get
        {
            lock (_gate)
            {
                return _owned is { HasExited: false } ? _owned.Id : null;
            }
        }
    }

    /// <summary>Gets 当前实例的主窗口句柄（可能为 0）。</summary>
    public IntPtr OwnedWindow
    {
        get
        {
            lock (_gate)
            {
                return _window;
            }
        }
    }

    /// <summary>
    /// 启动一个新的播放实例。
    /// 注意：这里刻意<b>不</b>关闭旧实例。PotPlayer 退出时会把当前播放项写回 DPL
    /// （topindex / playname 都会变），所以必须由调用方先关闭旧实例、再写 DPL、最后调用本方法，
    /// 否则刚写好的「起播第 N 项」会被旧实例的退出覆盖掉。
    /// </summary>
    /// <param name="exePath">PotPlayer 可执行文件。</param>
    /// <param name="dplPath">DPL 文件路径。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>启动结果。</returns>
    public async Task<LaunchResult> LaunchAsync(string exePath, string dplPath, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? Environment.CurrentDirectory
        };

        startInfo.ArgumentList.Add(dplPath);
        startInfo.ArgumentList.Add("/new");

        Process? process;
        try
        {
            process = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            throw new PlaybackException(500, "启动 PotPlayer 失败：" + ex.Message, "launch-failed");
        }

        if (process is null)
        {
            throw new PlaybackException(500, "启动 PotPlayer 失败：进程没有创建成功。", "launch-failed");
        }

        _log.Info("已启动 PotPlayer：PID=" + process.Id + "，DPL=" + dplPath);

        var handle = await WaitForWindowAsync(process, TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (handle == IntPtr.Zero)
        {
            // 窗口晚出现时后续步骤还能再找；这里只告警，不阻断播放。
            _log.Warn("10 秒内没有找到 PotPlayer 主窗口（PID=" + process.Id + "）。");
        }

        lock (_gate)
        {
            _owned = process;
            _window = handle;
        }

        SaveSession(process.Id, dplPath);

        return new LaunchResult(process.Id, handle);
    }

    /// <summary>
    /// 清理上一次运行遗留的 PotPlayer。
    /// 为什么需要：代理被安装脚本强杀或自身崩溃时，它拉起的播放器不会跟着退出；新代理启动后
    /// 又会拉起一个，于是两个实例同时在播，M5 的进度回写会互相覆盖。
    /// 判定依据是本代理「拉起成功后立刻写下」的 session 文件，只处理该 PID，绝不碰用户自己开的播放器。
    /// </summary>
    public void CleanupOrphan()
    {
        var record = LoadSession();
        if (record is null)
        {
            return;
        }

        try
        {
            using var process = Process.GetProcessById(record.ProcessId);
            if (process.HasExited || !IsPotPlayer(process))
            {
                // PID 已被系统复用给别的程序，或播放器早已退出：只清记录，不动进程。
                DeleteSession();
                return;
            }

            var window = PotPlayerWindow.FindMainWindow(record.ProcessId);
            _log.Warn("发现上次遗留的 PotPlayer（PID=" + record.ProcessId + "，列表=" + record.DplPath + "），先关闭它。");

            if (window != IntPtr.Zero)
            {
                PotPlayerWindow.SendStop(window);
                PotPlayerWindow.PostClose(window);
            }
            else
            {
                process.CloseMainWindow();
            }

            for (var i = 0; i < 30 && !process.HasExited; i++)
            {
                process.WaitForExit(100);
            }

            if (!process.HasExited)
            {
                _log.Warn("遗留的 PotPlayer 未在 3 秒内退出，直接结束：PID=" + record.ProcessId);
                process.Kill();
            }
        }
        catch (ArgumentException)
        {
            // 进程已经不存在（GetProcessById 抛这个），属于最常见的正常情况。
        }
        catch (Exception ex)
        {
            _log.Warn("清理遗留 PotPlayer 失败：" + ex.Message);
        }
        finally
        {
            DeleteSession();
        }
    }

    private static bool IsPotPlayer(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            return path is not null
                   && Path.GetFileNameWithoutExtension(path)
                       .StartsWith("PotPlayer", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // 读不到模块信息（权限/位数不匹配）时宁可不碰，避免误杀其它程序。
            return false;
        }
    }

    private void SaveSession(int processId, string dplPath)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(
                SessionPath,
                JsonSerializer.Serialize(new SessionRecord(processId, dplPath), SessionJson),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            // 写不了只是少了一层保护，不影响本次播放。
            _log.Warn("写入 PotPlayer 会话记录失败：" + ex.Message);
        }
    }

    private void DeleteSession()
    {
        try
        {
            if (File.Exists(SessionPath))
            {
                File.Delete(SessionPath);
            }
        }
        catch (Exception ex)
        {
            _log.Warn("删除 PotPlayer 会话记录失败：" + ex.Message);
        }
    }

    private SessionRecord? LoadSession()
    {
        try
        {
            if (!File.Exists(SessionPath))
            {
                return null;
            }

            var json = File.ReadAllText(SessionPath, Encoding.UTF8);
            var record = JsonSerializer.Deserialize<SessionRecord>(json, SessionJson);
            return record is { ProcessId: > 0 } ? record : null;
        }
        catch (Exception ex)
        {
            _log.Warn("读取 PotPlayer 会话记录失败：" + ex.Message);
            return null;
        }
    }

    private sealed record SessionRecord(int ProcessId, string DplPath);

    /// <summary>
    /// 优雅关闭本代理拉起的实例。
    /// 先停止播放再 WM_CLOSE；实在不退出才 Kill，并写入 abnormalExit 标记供下次启动前备份。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task CloseOwnedAsync(CancellationToken cancellationToken)
    {
        Process? process;
        IntPtr window;

        lock (_gate)
        {
            process = _owned;
            window = _window;
            _owned = null;
            _window = IntPtr.Zero;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (!PotPlayerWindow.Exists(window))
            {
                window = PotPlayerWindow.FindMainWindow(process.Id);
            }

            if (window != IntPtr.Zero)
            {
                _log.Info("正在优雅关闭 PotPlayer：PID=" + process.Id);
                PotPlayerWindow.SendStop(window);
                PotPlayerWindow.PostClose(window);
            }
            else
            {
                // 连窗口都没有（还在启动/已隐藏），用 CloseMainWindow 尽力而为。
                try
                {
                    process.CloseMainWindow();
                }
                catch
                {
                    // 忽略：下面统一等待/兜底。
                }
            }

            for (var i = 0; i < 30 && !process.HasExited; i++)
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }

            if (!process.HasExited)
            {
                _log.Warn("PotPlayer 未在 3 秒内退出，执行强制结束（不推荐路径）：PID=" + process.Id);
                try
                {
                    process.Kill(entireProcessTree: false);
                }
                catch (Exception ex)
                {
                    _log.Warn("强制结束 PotPlayer 失败：" + ex.Message);
                }

                // 下次启动前做一次列表目录快照比对（PLAN 6.5）。
                var config = _config.GetSnapshot();
                config.AbnormalExit = true;
                _config.Save(config);
            }
        }
        finally
        {
            process.Dispose();
            DeleteSession();
        }
    }

    private static async Task<IntPtr> WaitForWindowAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (process.HasExited)
            {
                return IntPtr.Zero;
            }

            try
            {
                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return process.MainWindowHandle;
                }
            }
            catch
            {
                // 进程可能刚好退出。
            }

            var handle = PotPlayerWindow.FindMainWindow(process.Id);
            if (handle != IntPtr.Zero)
            {
                return handle;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return IntPtr.Zero;
    }
}
