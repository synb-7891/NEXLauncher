using System.Text.Json;
using Photino.NET;

namespace NEXLauncher.Cross;

/// <summary>
/// NEX Launcher 跨平台外壳（Mac / Linux / Windows）。
/// 界面层与 Windows 版完全同一份（wwwroot/index.html），消息协议也保持一致（{cmd:...}）。
/// 当前阶段：外壳 + 桥接骨架，界面以浏览器模式自渲染（皮肤站/百宝箱/资源下载/个性化全部可用）；
/// 下一步把 Services 里 17 个纯 C# 服务接进来，即可获得与 Windows 版对等的真实功能。
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var win = new PhotinoWindow()
            .SetTitle("NEX Launcher")
            .SetSize(1180, 760)
            .Center()
            .SetResizable(true)
            .RegisterWebMessageReceivedHandler(OnMessage)
            .Load("wwwroot/index.html");

        win.WaitForClose();
    }

    /// <summary>与 Windows 版 MainWindow.WebShell 同一套消息协议。</summary>
    private static void OnMessage(object? sender, string message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement.Clone();
            var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : null;

            switch (cmd)
            {
                case "open-url":
                {
                    var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url) &&
                        (url.StartsWith("http://") || url.StartsWith("https://")))
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
                            { UseShellExecute = true });
                    }
                    break;
                }

                case "ready":
                case "refresh":
                    // TODO 接入服务层后推送真实状态（账号/版本/内存/主题/守护进程）：
                    //   var state = BuildState();  sender.SendWebMessage(state);
                    // 17 个纯 C# 服务可直接复用；仅 4 个（UiPalette/GuardDaemon/
                    // DownloadManagerService/UpdateService）需要替换 WPF 依赖。
                    break;

                case "launch":
                    // TODO MinecraftService.LaunchAsync(...)（CmlLib 本身跨平台）
                    break;

                case "theme":
                    // TODO 持久化主题到 ~/.config/nexlauncher/settings.json
                    break;
            }
        }
        catch
        {
            // 消息解析失败静默，保持与 Windows 版一致的容错策略
        }
    }
}
