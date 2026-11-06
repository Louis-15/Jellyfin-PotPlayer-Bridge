using System.Text;
using PotPlayerBridge.Core;

namespace PotPlayerBridge;

/// <summary>
/// 程序入口。
/// <list type="bullet">
/// <item>正常启动：托盘常驻 + 本机 HTTP 服务；</item>
/// <item>由 <c>potplayerbridge://settings</c> 拉起：额外打开设置窗口；</item>
/// <item>已有实例在运行：不再启动第二份，只请求第一个实例打开设置（如果是 settings 协议），然后退出。</item>
/// </list>
/// </summary>
internal static class Program
{
    private const string MutexName = @"Local\PotPlayerBridge.SingleInstance";

    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch
        {
            // 老系统不支持 PerMonitorV2 时使用系统默认 DPI 行为。
        }

        var wantsSettings = args.Any(a => a.Contains("settings", StringComparison.OrdinalIgnoreCase));

        // 只允许一份实例：否则两个进程会抢同一个端口，进度/播放状态也会互相打架。
        using var mutex = new Mutex(initiallyOwned: false, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            if (wantsSettings)
            {
                TryAskRunningInstanceToOpenSettings();
            }

            return;
        }

        var log = new Logger();
        log.Info("PotPlayerBridge 启动" + (args.Length > 0 ? "，参数：" + string.Join(' ', args) : string.Empty));

        using var context = new TrayApplicationContext(log, wantsSettings);
        Application.Run(context);
    }

    /// <summary>
    /// 让已经运行的实例打开设置窗口。
    /// 走本机 HTTP 而不是进程间消息，是因为第一个实例一定已经在监听，且这条路径在 M3 就有现成的接口。
    /// </summary>
    private static void TryAskRunningInstanceToOpenSettings()
    {
        try
        {
            var log = new Logger();
            var config = new ConfigStore(log).GetSnapshot();

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"http://127.0.0.1:{config.Port}/api/open-settings");
            request.Headers.Add("X-PPB-Client", "1");
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            client.Send(request);
        }
        catch
        {
            // 第一个实例可能刚启动、还没开始监听。设置窗口仍可从托盘双击打开。
        }
    }
}
