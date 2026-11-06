using System.Text;

namespace PotPlayerBridge;

/// <summary>
/// 极简文件日志。托盘程序没有控制台，出问题时用户可以把 agent.log 发过来。
/// 约束：只记录路径、状态、错误消息；Token、密码等敏感内容一律不写。
/// </summary>
public sealed class Logger
{
    private const long MaxBytes = 2 * 1024 * 1024;
    private readonly object _gate = new();

    /// <summary>Initializes a new instance of the <see cref="Logger"/> class.</summary>
    public Logger()
    {
        AppPaths.EnsureDataDirectory();
    }

    /// <summary>记录普通信息。</summary>
    /// <param name="message">消息。</param>
    public void Info(string message) => Write("INF", message);

    /// <summary>记录警告。</summary>
    /// <param name="message">消息。</param>
    public void Warn(string message) => Write("WRN", message);

    /// <summary>记录错误。</summary>
    /// <param name="message">消息。</param>
    /// <param name="exception">异常，可为空。</param>
    public void Error(string message, Exception? exception = null)
    {
        Write("ERR", exception is null ? message : message + " | " + exception.GetType().Name + ": " + exception.Message);
    }

    private void Write(string level, string message)
    {
        try
        {
            lock (_gate)
            {
                RotateIfNeeded();
                var line = string.Format(
                    "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}",
                    DateTimeOffset.Now,
                    level,
                    message,
                    Environment.NewLine);
                // 显式 UTF-8（无 BOM）：中文日志用记事本/VS Code 都能正常打开。
                File.AppendAllText(AppPaths.LogFile, line, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 日志写不进去不能影响主流程（例如磁盘满、文件被占用）。
        }
    }

    private static void RotateIfNeeded()
    {
        var file = new FileInfo(AppPaths.LogFile);
        if (!file.Exists || file.Length < MaxBytes)
        {
            return;
        }

        var backup = AppPaths.LogFile + ".old";
        try
        {
            File.Move(AppPaths.LogFile, backup, overwrite: true);
        }
        catch
        {
            // 轮转失败就继续往原文件追加，最多是文件变大。
        }
    }
}
