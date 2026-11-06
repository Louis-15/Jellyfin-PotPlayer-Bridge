using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;
using PotPlayerBridge.Core;

namespace PotPlayerBridge;

/// <summary>
/// 设置窗口：服务器地址、PotPlayer 路径、自动检测、打开日志。
/// 端口固定为 18927（注入脚本里写死了本机地址），所以这里只展示、不允许改，
/// 避免用户改了以后以为会生效。
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly ConfigStore _config;
    private readonly PotPlayerLocator _locator;
    private readonly Logger _log;

    private readonly TextBox _serverBox = new() { Dock = DockStyle.Fill };
    private readonly TextBox _pathBox = new() { Dock = DockStyle.Fill };
    private readonly Button _browseButton = new() { Text = "浏览…", AutoSize = true };
    private readonly Button _detectButton = new() { Text = "自动检测", AutoSize = true };
    private readonly Button _saveButton = new() { Text = "保存", AutoSize = true };
    private readonly Button _cancelButton = new() { Text = "取消", AutoSize = true, DialogResult = DialogResult.Cancel };
    private readonly Label _statusLabel = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoSize = false
    };

    private readonly LinkLabel _openLogLink = new() { Text = "打开日志", AutoSize = true };

    /// <summary>Initializes a new instance of the <see cref="SettingsForm"/> class.</summary>
    /// <param name="config">配置。</param>
    /// <param name="locator">路径发现。</param>
    /// <param name="runtime">运行期状态（这里只用端口做提示）。</param>
    /// <param name="log">日志。</param>
    public SettingsForm(ConfigStore config, PotPlayerLocator locator, AgentRuntime runtime, Logger log)
    {
        _config = config;
        _locator = locator;
        _log = log;

        Text = "PotPlayer Bridge 设置";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(700, 340);
        Font = new Font("Microsoft YaHei UI", 9F);
        Icon = LoadIcon();

        BuildLayout(runtime);
        LoadValues();

        AcceptButton = _saveButton;
        CancelButton = _cancelButton;
    }

    private void BuildLayout(AgentRuntime runtime)
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18),
            ColumnCount = 3,
            RowCount = 5
        };

        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 128F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // 第 0 行：服务器地址
        layout.Controls.Add(MakeLabel("Jellyfin 服务器地址"), 0, 0);
        layout.Controls.Add(_serverBox, 1, 0);
        layout.SetColumnSpan(_serverBox, 2);

        // 第 1 行：PotPlayer 路径
        layout.Controls.Add(MakeLabel("PotPlayer 路径"), 0, 1);
        layout.Controls.Add(_pathBox, 1, 1);
        layout.Controls.Add(_browseButton, 2, 1);

        // 第 2 行：自动检测 + 端口说明
        var detectPanel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 8, 0, 0)
        };
        detectPanel.Controls.Add(_detectButton);

        var portHint = new Label
        {
            Text = $"本机端口固定为 {runtime.Port}",
            AutoSize = true,
            ForeColor = Color.Gray,
            Margin = new Padding(12, 12, 0, 0)
        };
        detectPanel.Controls.Add(portHint);

        layout.Controls.Add(detectPanel, 1, 2);
        layout.SetColumnSpan(detectPanel, 2);

        // 第 3 行：状态提示（占满剩余高度）
        layout.Controls.Add(_statusLabel, 0, 3);
        layout.SetColumnSpan(_statusLabel, 3);

        // 第 4 行：左下打开日志 + 右下按钮
        var bottom = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            AutoSize = true,
            Margin = new Padding(0, 10, 0, 0)
        };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _openLogLink.Anchor = AnchorStyles.Left;
        bottom.Controls.Add(_openLogLink, 0, 0);

        var buttons = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Dock = DockStyle.Fill
        };
        buttons.Controls.Add(_saveButton);
        buttons.Controls.Add(_cancelButton);
        bottom.Controls.Add(buttons, 1, 0);

        layout.Controls.Add(bottom, 0, 4);
        layout.SetColumnSpan(bottom, 3);

        Controls.Add(layout);

        _browseButton.Click += (_, _) => BrowseForPotPlayer();
        _detectButton.Click += OnDetectClicked;
        _saveButton.Click += OnSaveClicked;
        _openLogLink.LinkClicked += (_, _) => OpenLog();
    }

    private static Label MakeLabel(string text)
    {
        return new Label
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            TextAlign = ContentAlignment.MiddleLeft,
            Margin = new Padding(0, 6, 8, 6)
        };
    }

    private void LoadValues()
    {
        var config = _config.GetSnapshot();
        _serverBox.Text = config.ServerUrl;
        _pathBox.Text = config.PotPlayerPath ?? string.Empty;
    }

    private void BrowseForPotPlayer()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 PotPlayer 可执行文件",
            Filter = "PotPlayer (PotPlayer*.exe)|PotPlayer*.exe|可执行文件 (*.exe)|*.exe",
            CheckFileExists = true
        };

        var current = _pathBox.Text.Trim();
        if (File.Exists(current))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current);
        }

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            _pathBox.Text = dialog.FileName;
        }
    }

    private async void OnDetectClicked(object? sender, EventArgs e)
    {
        _detectButton.Enabled = false;
        SetStatus("正在检测 PotPlayer…", false);

        try
        {
            var current = _pathBox.Text.Trim();
            var result = await Task.Run(() =>
                _locator.DetectFull(current.Length == 0 ? null : current));

            if (result.Path is { } path)
            {
                _pathBox.Text = path;
                SetStatus("已找到（来源：" + result.Source + "）", false);
            }
            else
            {
                SetStatus("没有找到 PotPlayer，请手动指定 PotPlayerMini64.exe 的路径。", true);
            }
        }
        catch (Exception ex)
        {
            _log.Error("设置窗口自动检测失败", ex);
            SetStatus("检测失败：" + ex.Message, true);
        }
        finally
        {
            _detectButton.Enabled = true;
        }
    }

    private void OnSaveClicked(object? sender, EventArgs e)
    {
        var url = _serverBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SetStatus("服务器地址必须是 http:// 或 https:// 开头的完整地址。", true);
            return;
        }

        var path = _pathBox.Text.Trim();
        if (path.Length > 0 && !PotPlayerLocator.IsValidPotPlayer(path))
        {
            SetStatus("PotPlayer 路径不存在，或文件名里不含 PotPlayer。", true);
            return;
        }

        var config = _config.GetSnapshot();
        config.ServerUrl = url;
        config.PotPlayerPath = path.Length == 0 ? null : path;
        _config.Save(config);

        _log.Info("设置窗口已保存：server=" + config.ServerUrl + "，potPlayer=" + (config.PotPlayerPath ?? "（未设置）"));
        DialogResult = DialogResult.OK;
        Close();
    }

    private void OpenLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(AppPaths.LogFile) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "打开日志失败：" + ex.Message + "\n\n日志位置：" + AppPaths.LogFile,
                "PotPlayer Bridge",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void SetStatus(string text, bool isError)
    {
        _statusLabel.Text = text;
        _statusLabel.ForeColor = isError ? Color.Firebrick : Color.SeaGreen;
    }

    private static Icon LoadIcon()
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
            // 取不到就退回系统图标。
        }

        return SystemIcons.Application;
    }
}
