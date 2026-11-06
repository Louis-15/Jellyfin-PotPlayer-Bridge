using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using PotPlayerBridge.Core;
using PotPlayerBridge.Http;

namespace PotPlayerBridge;

/// <summary>
/// 托盘应用：没有主窗口，托盘图标常驻。
/// 职责边界：
/// <list type="bullet">
/// <item>启动/停止本机 HTTP 服务；</item>
/// <item>提供设置窗口、自动检测、打开配置/日志；</item>
/// <item>把后台线程的「打开设置」「提示气泡」请求安全地转交给 UI 线程。</item>
/// </list>
/// 播放器相关逻辑（DPL、进度）从 M4/M5 开始接入，这里只留扩展点。
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly Logger _log;
    private readonly ConfigStore _config;
    private readonly PotPlayerLocator _locator;
    private readonly AgentRuntime _runtime;
    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _uiTimer;

    private LocalHttpServer? _server;
    private SettingsForm? _settingsForm;
    private bool _openSettingsWhenReady;

    // 后台线程只写这几个字段，UI 线程在定时器里取用；字符串赋值是原子的。
    private volatile bool _pendingBalloon;
    private string _pendingTitle = string.Empty;
    private string _pendingText = string.Empty;
    private ToolTipIcon _pendingIcon = ToolTipIcon.Info;

    /// <summary>Initializes a new instance of the <see cref="TrayApplicationContext"/> class.</summary>
    /// <param name="log">日志。</param>
    /// <param name="openSettingsInitially">是否在启动后立刻打开设置（协议 settings 触发）。</param>
    public TrayApplicationContext(Logger log, bool openSettingsInitially)
    {
        _log = log;
        _config = new ConfigStore(log);
        _locator = new PotPlayerLocator(log);

        var config = _config.GetSnapshot();
        _runtime = new AgentRuntime { Port = config.Port };

        _tray = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "PotPlayer Bridge",
            Visible = true,
            ContextMenuStrip = BuildMenu()
        };
        _tray.DoubleClick += (_, _) => ShowSettings();

        StartServer();

        // 用 UI 定时器轮询跨线程请求：不依赖 SynchronizationContext 的初始化时机，
        // 也避免从 HTTP 线程直接创建/激活 WinForms 控件。
        _openSettingsWhenReady = openSettingsInitially;
        _uiTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _uiTimer.Tick += (_, _) => OnUiTick();
        _uiTimer.Start();

        if (!openSettingsInitially)
        {
            QueueBalloon("PotPlayer Bridge", "本地代理已启动。双击托盘图标可打开设置。", ToolTipIcon.Info);
        }
    }

    /// <inheritdoc />
    protected override void ExitThreadCore()
    {
        _uiTimer.Stop();

        try
        {
            _settingsForm?.Close();
        }
        catch
        {
            // 关闭窗口失败不应阻止退出。
        }

        _server?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();

        _log.Info("已退出。");
        base.ExitThreadCore();
    }

    private void StartServer()
    {
        var config = _config.GetSnapshot();
        _runtime.Port = config.Port;

        var playback = new PlaybackService(
            _config,
            _locator,
            new JellyfinClient(_log),
            new PlaylistStore(_log),
            new PotPlayerLauncher(_log, _config),
            _runtime,
            _log);

        var routes = new Routes(_config, _locator, _runtime, playback, _log);
        _server = new LocalHttpServer(config.Port, routes.HandleAsync, _log);

        try
        {
            _server.Start();
            _tray.Text = $"PotPlayer Bridge（127.0.0.1:{config.Port}）";
        }
        catch (Exception ex)
        {
            _log.Error("启动 HTTP 服务失败（端口 " + config.Port + "）", ex);
            MessageBox.Show(
                "无法监听 127.0.0.1:" + config.Port + "。\n\n" +
                "可能原因：\n" +
                "1. 已经有一个 PotPlayer Bridge 在运行；\n" +
                "2. 端口被其它程序占用。\n\n" +
                "错误信息：" + ex.Message,
                "PotPlayer Bridge",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("设置(&S)", null, (_, _) => ShowSettings());
        menu.Items.Add("自动检测 PotPlayer", null, (_, _) => DetectPotPlayer());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("打开配置目录", null, (_, _) => OpenPath(AppPaths.DataDirectory));
        menu.Items.Add("打开日志", null, (_, _) => OpenPath(AppPaths.LogFile));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出(&X)", null, (_, _) => ExitThread());
        return menu;
    }

    private void OnUiTick()
    {
        if (_runtime.ConsumeOpenSettingsRequest())
        {
            ShowSettings();
        }

        if (_openSettingsWhenReady)
        {
            _openSettingsWhenReady = false;
            ShowSettings();
        }

        if (_pendingBalloon)
        {
            _pendingBalloon = false;
            _tray.ShowBalloonTip(3000, _pendingTitle, _pendingText, _pendingIcon);
        }
    }

    private void ShowSettings()
    {
        try
        {
            if (_settingsForm is { IsDisposed: false })
            {
                if (_settingsForm.WindowState == FormWindowState.Minimized)
                {
                    _settingsForm.WindowState = FormWindowState.Normal;
                }

                _settingsForm.Activate();
                return;
            }

            _settingsForm = new SettingsForm(_config, _locator, _runtime, _log);
            _settingsForm.FormClosed += (_, _) => _settingsForm = null;
            _settingsForm.Show();
            _settingsForm.Activate();
        }
        catch (Exception ex)
        {
            _log.Error("打开设置窗口失败", ex);
            MessageBox.Show(
                "打开设置窗口失败：" + ex.Message,
                "PotPlayer Bridge",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private void DetectPotPlayer()
    {
        QueueBalloon("PotPlayer Bridge", "正在检测 PotPlayer…", ToolTipIcon.Info);

        Task.Run(() =>
        {
            try
            {
                var config = _config.GetSnapshot();
                var result = _locator.DetectFull(config.PotPlayerPath);

                if (result.Path is { } path)
                {
                    config.PotPlayerPath = path;
                    _config.Save(config);
                    _log.Info("托盘菜单检测到 PotPlayer：" + path + "（来源：" + result.Source + "）");
                    QueueBalloon("已找到 PotPlayer", path, ToolTipIcon.Info);
                }
                else
                {
                    QueueBalloon(
                        "没有找到 PotPlayer",
                        "请在设置窗口中手动指定 PotPlayerMini64.exe 的路径。",
                        ToolTipIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                _log.Error("自动检测失败", ex);
                QueueBalloon("检测失败", ex.Message, ToolTipIcon.Error);
            }
        });
    }

    private void QueueBalloon(string title, string text, ToolTipIcon icon)
    {
        _pendingTitle = title;
        _pendingText = text;
        _pendingIcon = icon;
        _pendingBalloon = true;
    }

    private void OpenPath(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                MessageBox.Show("路径不存在：\n" + path, "PotPlayer Bridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _log.Error("打开路径失败：" + path, ex);
            MessageBox.Show("打开失败：" + ex.Message, "PotPlayer Bridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
            // 单文件/裁剪环境下偶尔取不到，使用系统图标兜底。
        }

        return SystemIcons.Application;
    }
}
