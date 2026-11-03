using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using NEXLauncher.Services;

namespace NEXLauncher;

/// <summary>
/// 新版界面（WebView2 混合层）：
/// 在原 WPF 界面之上叠加一层 WebView2，负责「开始页面 / 皮肤站 / 百宝箱 / 安全防护」四个新版页面。
/// 其余页面仍由原有 WPF 代码渲染，功能一行未改。
/// 设置 NEX_LEGACY_UI=1 可完全关闭本层，回到经典界面。
/// </summary>
public partial class MainWindow
{
    private bool _webShellReady;          // WebView2 已就绪并接管首页
    private bool _webShellFailed;         // 初始化失败（无运行时 / 缺文件），永久回退经典界面
    private bool _suppressShellSwitch;    // 内部重建页面时不要切换显示层
    private bool _webNavRequested;        // 用户在新版界面里点了跳原生页的入口（本次 ShowPage 允许切层）

    private static string WebShellDir =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "webshell");

    private static string WebViewProfileDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEXLauncher", "WebView2");

    // ---------------------------------------------------------------- 窗口圆角（DWM 原生）

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// WebView2 不支持分层窗口（AllowsTransparency），圆角改由 DWM 提供（Win11 22000+）。
    /// Win10 上静默忽略，窗口保持直角。
    /// </summary>
    private void ApplyRoundedCorners()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int pref = 2;   // DWMWA_WINDOW_CORNER_PREFERENCE(33) → DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref pref, sizeof(int));
            int border = 0x5A3B15; // DWMWA_BORDER_COLOR(34) COLORREF=0x00BBGGRR → #153B5A 深蓝描边
            DwmSetWindowAttribute(hwnd, 34, ref border, sizeof(int));
        }
        catch { /* DWM 不可用时忽略 */ }
    }

    // ---------------------------------------------------------------- 就绪淡入（避免旧界面闪现）

    /// <summary>新版界面首帧渲染完成（或已回退经典界面）时触发，Splash 收到后淡入主窗口。</summary>
    public event Action? WebShellReady;

    private bool _readyFired;

    private void FireWebShellReady()
    {
        if (_readyFired) return;
        _readyFired = true;
        WebShellReady?.Invoke();
    }

    /// <summary>淡入显示主窗口（由 SplashWindow 在就绪信号/超时时调用）。</summary>
    public void RevealShell()
    {
        try
        {
            BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(
                0d, 1d, TimeSpan.FromMilliseconds(220)));
        }
        catch { Opacity = 1d; }
        ShowActivated = true;
        try { Activate(); } catch { }
    }

    // ---------------------------------------------------------------- 初始化

    private async void InitWebShell()
    {
        ApplyRoundedCorners();
        if (_webShellReady || _webShellFailed) return;

        if (Environment.GetEnvironmentVariable("NEX_LEGACY_UI") == "1")
        {
            _webShellFailed = true;
            FireWebShellReady();
            return;
        }

        try
        {
            var index = Path.Combine(WebShellDir, "index.html");
            if (!File.Exists(index))
            {
                _webShellFailed = true;
                FireWebShellReady();
                return;
            }

            Directory.CreateDirectory(WebViewProfileDir);
            var env = await CoreWebView2Environment.CreateAsync(null, WebViewProfileDir);
            await WebHost.EnsureCoreWebView2Async(env);

            var core = WebHost.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.WebMessageReceived += OnWebMessage;

            // 以虚拟域名加载本地文件，行为等价于 https 页面（localStorage 可用、无 file:// 限制）
            core.SetVirtualHostNameToFolderMapping(
                "nex.local", WebShellDir, CoreWebView2HostResourceAccessKind.Allow);
            core.Navigate("https://nex.local/index.html?app=1");

            _webShellReady = true;
            ShowWebShell();
        }
        catch (Exception ex)
        {
            // 系统未安装 WebView2 运行时等情况：安静回退经典界面，不影响任何功能
            _webShellFailed = true;
            ShowNativeShell();
            FireWebShellReady();
            LogWebShellError(ex);
        }
    }

    private static void LogWebShellError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXLauncher");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 新版界面(WebView2)初始化失败，已回退经典界面:\n{ex}\n\n");
        }
        catch { }
    }

    // ---------------------------------------------------------------- 显示层切换

    private void WebTitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMaximize(); return; }
        try { DragMove(); } catch { }
    }

    private void WebMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void WebMax_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void WebClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() => WindowState =
        WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void ShowWebShell()
    {
        if (!_webShellReady) return;
        RootBorder.Visibility = Visibility.Collapsed;
        WebShellLayer.Visibility = Visibility.Visible;
        try { WebHost.Focus(); } catch { }
    }

    private void ShowNativeShell()
    {
        WebShellLayer.Visibility = Visibility.Collapsed;
        RootBorder.Visibility = Visibility.Visible;
    }

    // ---------------------------------------------------------------- 消息（JS → C#）

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string json;
        try { json = e.WebMessageAsJson; }
        catch { return; }
        if (string.IsNullOrWhiteSpace(json)) return;

        string cmd;
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json);
            root = doc.RootElement.Clone();
            cmd = root.TryGetProperty("cmd", out var c) ? (c.GetString() ?? "") : "";
        }
        catch { return; }

        switch (cmd)
        {
            case "ready":
                PushState();
                // 新版界面首帧已真正渲染完成 —— 此刻才让主窗口可见
                FireWebShellReady();
                break;

            case "drag":
                // DragMove 会接管消息循环，必须等本次回调返回后再执行
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { if (WindowState == WindowState.Maximized) return; DragMove(); }
                    catch { }
                }), DispatcherPriority.Input);
                break;

            case "min":
                WindowState = WindowState.Minimized;
                break;

            case "max":
                WindowState = WindowState == WindowState.Maximized
                    ? WindowState.Normal : WindowState.Maximized;
                break;

            case "close":
                Close();
                break;

            case "refresh":
                PushState();
                break;

            case "nav":
                NavigateFromWeb(root);
                break;

            case "launch":
                _ = LaunchFromWebAsync(root);
                break;

            case "theme":
                ApplyThemeFromWeb(root);
                break;

            case "open-url":
                OpenExternal(root);
                break;
        }
    }

    /// <summary>新版界面请求用系统默认浏览器打开链接（QQ 频道、Modrinth 页面等）。</summary>
    private void OpenExternal(JsonElement root)
    {
        try
        {
            var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) return;
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* 打开失败静默 */ }
    }

    /// <summary>新版界面里的导航 → 对应原生页面（功能完整）。</summary>
    private void NavigateFromWeb(JsonElement root)
    {
        var id = root.TryGetProperty("page", out var p) ? p.GetString() : null;
        var native = id switch
        {
            "versions" => "游戏版本",
            "mods" => "模组管理",
            "res" => "资源下载",
            "account" => "账号",
            "settings" => "设置",
            "skins" => "皮肤",
            "tools" => "百宝箱",
            "security" => "安全防护",
            _ => null
        };
        if (native == null) return;

        // 用户在新版界面主动请求原生页：标记一次合法切层，随后由 ShowPage 消费
        _webNavRequested = true;
        ShowPage(native, true);
        HighlightNav(native);
    }

    private async Task LaunchFromWebAsync(JsonElement root)
    {
        var wanted = root.TryGetProperty("version", out var v) ? v.GetString() : null;
        var installed = GetInstalledVersionIds();

        var version = !string.IsNullOrWhiteSpace(wanted) && installed.Contains(wanted)
            ? wanted
            : installed.FirstOrDefault();

        if (version == null)
        {
            PostJson(new { type = "toast", msg = "没有已安装的游戏版本，请先到「游戏版本」下载一个" });
            return;
        }

        // 与侧栏选择保持一致，保证原生界面状态不错位
        try
        {
            if (SideVersionBox.Items.Contains(version)) SideVersionBox.SelectedItem = version;
        }
        catch { }

        _settings.LastQuickVersion = version;
        _settings.Save();
        _session ??= MinecraftService.CreateOfflineSession(_settings.QuickOfflineName);

        PostJson(new { type = "launch-begin", version });

        try
        {
            await _minecraftService.StartGameAsync(version, _session, _settings);
            PostJson(new { type = "launch-ok", version });
        }
        catch (Exception ex)
        {
            PostJson(new { type = "launch-fail", msg = ex.Message });
        }
    }

    private void ApplyThemeFromWeb(JsonElement root)
    {
        var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name)) return;
        if (UiPalette.Presets.All(p => p.Name != name)) return;

        _settings.ThemePreset = name;
        _settings.AccentColor = "";          // 跟随预设，不再覆盖主色
        _settings.Save();
        UiPalette.ApplyPreset(name);

        // 原生界面按新配色重建（重建时不要切回原生层）
        _suppressShellSwitch = true;
        try { ShowPage(CurrentPage, true); }
        finally { _suppressShellSwitch = false; }

        PostJson(new { type = "theme-applied", name });
        PushState();
    }

    // ---------------------------------------------------------------- 状态推送（C# → JS）

    private void PostJson(object payload)
    {
        if (!_webShellReady) return;
        try { WebHost.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(payload)); }
        catch { }
    }

    private void PushState()
    {
        if (!_webShellReady) return;

        var installed = GetInstalledVersionIds();
        var versions = installed.Select(id => new
        {
            id,
            loader = DetectLoaderType(id)
        }).ToList();

        var selected = SideVersionBox.SelectedItem as string
                       ?? (installed.Contains(_settings.LastQuickVersion) ? _settings.LastQuickVersion : installed.FirstOrDefault());

        var accountName = _session?.Username;
        var isMicrosoft = string.Equals(_settings.LastAccountType, "microsoft", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(accountName))
            accountName = string.IsNullOrWhiteSpace(_settings.LastOfflineName) ? "未登录" : _settings.LastOfflineName;

        PostJson(new
        {
            type = "state",
            account = new
            {
                name = accountName,
                kind = isMicrosoft ? "微软正版" : "离线模式",
                loggedIn = _session != null,
                uuid = _session?.UUID ?? _settings.MicrosoftSessionUuid
            },
            versions,
            selected,
            ram = new { min = _settings.MinRamMb, max = _settings.MaxRamMb },
            theme = new
            {
                name = _settings.ThemePreset,
                presets = UiPalette.Presets.Select(p => p.Name).ToArray(),
                vars = new Dictionary<string, string>
                {
                    ["bg"] = UiPalette.Bg,
                    ["header"] = UiPalette.Header,
                    ["surface"] = UiPalette.Surface,
                    ["surface-alt"] = UiPalette.SurfaceAlt,
                    ["border"] = UiPalette.Border,
                    ["accent"] = UiPalette.Accent,
                    ["text"] = UiPalette.Text,
                    ["text-dim"] = UiPalette.TextDim,
                    ["muted"] = UiPalette.Muted,
                    ["muted-light"] = UiPalette.MutedLight
                }
            },
            security = BuildSecurityState(),
            gameDir = _gameDirectory
        });
    }

    private object BuildSecurityState()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXLauncher");

        int? pid = null;
        string started = "";
        string uptime = "";
        string beat = "";
        try
        {
            var hb = Path.Combine(dir, "guard-heartbeat.json");
            if (File.Exists(hb))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(hb));
                var r = doc.RootElement;
                if (r.TryGetProperty("Pid", out var p) && p.TryGetInt32(out var pidVal)) pid = pidVal;
                if (r.TryGetProperty("Started", out var s) && s.GetString() is { } sv && DateTimeOffset.TryParse(sv, out var st))
                {
                    started = st.ToLocalTime().ToString("HH:mm");
                    var span = DateTimeOffset.Now - st;
                    uptime = span.TotalHours >= 1
                        ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
                        : $"{Math.Max(span.Minutes, 0)} 分钟";
                }
                if (r.TryGetProperty("LastBeat", out var b) && b.GetString() is { } bv)
                    beat = bv;
            }
        }
        catch { }

        var events = new List<object>();
        try
        {
            var evPath = Path.Combine(dir, "security-events.jsonl");
            if (File.Exists(evPath))
            {
                var lines = File.ReadAllLines(evPath).Where(l => !string.IsNullOrWhiteSpace(l)).TakeLast(6).ToList();
                foreach (var line in lines)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var r = doc.RootElement;
                        var detail = r.TryGetProperty("Detail", out var d) ? (d.GetString() ?? "") : "";
                        detail = detail.Replace("\r", "").Replace("\n", " ");
                        if (detail.Length > 150) detail = detail[..150] + "…";
                        events.Add(new
                        {
                            time = r.TryGetProperty("Time", out var t) ? t.GetString() : "",
                            level = r.TryGetProperty("Level", out var lv) ? lv.GetString() : "提示",
                            layer = r.TryGetProperty("Layer", out var la) ? la.GetString() : "",
                            title = r.TryGetProperty("Title", out var ti) ? ti.GetString() : "",
                            detail
                        });
                    }
                    catch { }
                }
                events.Reverse();
            }
        }
        catch { }

        var canaries = new List<string>();
        try
        {
            var cn = Path.Combine(dir, "canaries.json");
            if (File.Exists(cn))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cn));
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    var raw = prop.Value.GetString() ?? "";
                    var path = raw.Split('|')[0];
                    if (!string.IsNullOrWhiteSpace(path)) canaries.Add(path);
                }
            }
        }
        catch { }

        return new
        {
            guardRunning = pid != null,
            pid = pid ?? 0,
            started,
            uptime,
            beat,
            shields = new[]
            {
                new { name = "下载防护", on = _settings.SecDownloadShield },
                new { name = "勒索蜜罐", on = _settings.SecRansomGuard },
                new { name = "行为监控", on = _settings.SecBehaviorMonitor },
                new { name = "系统守护", on = _settings.SecSystemGuard },
                new { name = "游戏防护", on = _settings.SecGameGuard }
            },
            events,
            canaries
        };
    }
}
