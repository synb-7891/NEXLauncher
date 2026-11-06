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

    /// <summary>
    /// 把嵌入 exe 的界面资源释放到 Assets/webshell。
    /// 这样单文件版（PublishSingleFile）单独拷到任何地方都能跑，不依赖旁边的文件夹。
    /// 磁盘文件与内嵌内容不一致（版本更新）时自动覆盖，保证用户始终跑上新界面。
    /// </summary>
    private static void EnsureWebShellFiles()
    {
        try
        {
            Directory.CreateDirectory(WebShellDir);
            var asm = typeof(MainWindow).Assembly;
            const string prefix = "nex.webshell.";
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var fileName = name[prefix.Length..];
                var target = Path.Combine(WebShellDir, fileName);
                bool needWrite = !File.Exists(target);
                if (!needWrite)
                {
                    using var s = asm.GetManifestResourceStream(name);
                    if (s == null) continue;
                    using var fs = File.OpenRead(target);
                    needWrite = fs.Length != s.Length;   // 简单长度比对足够区分版本
                }
                if (needWrite)
                {
                    using var s = asm.GetManifestResourceStream(name);
                    if (s == null) continue;
                    using var fs = File.Create(target);
                    s.CopyTo(fs);
                }
            }
        }
        catch { /* 释放失败时 InitWebShell 会检测到 index.html 缺失并回退经典界面 */ }
    }

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

    private static Task<CoreWebView2Environment>? _environmentTask;

    /// <summary>本次进程使用的 WebView2 用户数据目录（优先取上次绑定成功的目录）。</summary>
    private static string? _environmentDir;

    /// <summary>记录上次绑定成功的用户数据目录，下次启动直接用它（避免每次都踩一遍坏目录再恢复）。</summary>
    private static string LastGoodMarkerPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEXLauncher", "webshell.good");

    private static string PickEnvironmentDir()
    {
        if (_environmentDir != null) return _environmentDir;
        _environmentDir = ReadLastGoodDir() ?? WebViewProfileDir;
        return _environmentDir;
    }

    private static string? ReadLastGoodDir()
    {
        try
        {
            if (!File.Exists(LastGoodMarkerPath)) return null;
            var d = File.ReadAllText(LastGoodMarkerPath).Trim();
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            // 只允许 NEXLauncher 自己的目录，防篡改
            if (d.Length > 0 && d.StartsWith(Path.Combine(root, "NEXLauncher"), StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(d))
                return d;
        }
        catch { }
        return null;
    }

    private static void SaveLastGoodDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LastGoodMarkerPath)!);
            File.WriteAllText(LastGoodMarkerPath, dir);
            // 清理历史遗留的恢复目录，避免磁盘膨胀
            var parent = Path.GetDirectoryName(dir);
            if (parent != null)
                foreach (var old in Directory.GetDirectories(parent, "WebView2-rec-*"))
                    if (!string.Equals(old, dir, StringComparison.OrdinalIgnoreCase))
                    { try { Directory.Delete(old, true); } catch { } }
        }
        catch { }
    }

    /// <summary>绑定前先把自己能挤的内存挤出来：这台机器常年只剩 0.5GB 可用，
    /// WebView2 拉起浏览器进程池时内存不足会拖到几十秒甚至失败。</summary>
    private static void TrimOwnMemory()
    {
        try
        {
            GC.Collect(2, GCCollectionMode.Forced, true);
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Forced, true);
            SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, (IntPtr)(-1), (IntPtr)(-1));
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProc, IntPtr min, IntPtr max);

    /// <summary>创建（并缓存）WebView2 环境：在构造函数最开始就发起，与耗时的构造工作并行。</summary>
    private static Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        _environmentTask ??= CreateEnvironmentSafeAsync(PickEnvironmentDir());
        return _environmentTask;
    }

    /// <summary>
    /// 创建 WebView2 环境。两个开关是老机器上 0x800705B4 的直接解药：
    ///   --disable-gpu                  G5400 / UHD610 这类老核显，硬件加速会让
    ///                                  CreateCoreWebView2ControllerAsync 直接超时
    ///   CalculateNativeWinOcclusion    启动阶段窗口在屏幕外做首帧，Chromium 会判定
    ///                                  "被遮挡"并暂停渲染，导致首帧永远等不到
    /// 之前只在注释里写了这个坑、参数却没加，所以时好时坏。
    /// </summary>
    private static Task<CoreWebView2Environment> CreateEnvironmentSafeAsync(string? profileDir = null)
    {
        var opts = new CoreWebView2EnvironmentOptions(
            "--disable-gpu " +
            "--disable-gpu-compositing " +
            "--disable-features=CalculateNativeWinOcclusion " +
            "--disable-backgrounding-occluded-windows " +
            "--disable-renderer-backgrounding");
        return CoreWebView2Environment.CreateAsync(null, profileDir ?? WebViewProfileDir, opts);
    }

    private static readonly System.Diagnostics.Stopwatch BootClock = System.Diagnostics.Stopwatch.StartNew();

    private static void BootLog(string tag)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXLauncher");
            Directory.CreateDirectory(dir);
            // 用 QPC 计时（Stopwatch），不受系统时钟漂移影响；同时带上进程内毫秒数
            File.AppendAllText(Path.Combine(dir, "boot.log"),
                $"[{DateTime.Now:HH:mm:ss} +{BootClock.ElapsedMilliseconds,6}ms] {tag}\n");
        }
        catch { }
    }

    private void FireWebShellReady()
    {
        if (_readyFired) return;
        _readyFired = true;
        BootLog("WebShellReady 信号（新版界面就绪 或 已回退旧版界面）");
        try
        {
            var ms = (DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalMilliseconds;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXLauncher");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "boot.log"),
                $"[{DateTime.Now:HH:mm:ss}] 新版界面首帧就绪，启动耗时 {ms:F0} ms\n");
        }
        catch { }
        WebShellReady?.Invoke();
    }

    /// <summary>
    /// 淡入显示主窗口（由 SplashWindow 在就绪信号/超时时调用）。
    /// 启动期间窗口被放在屏幕外（Left=-32000）完成 WebView2 首帧渲染，
    /// 此处先移回工作区中央再淡入，避免 WebView2 子窗口的黑底面被看到。
    /// </summary>
    public void RevealShell()
    {
        try
        {
            if (Left < -10000)
            {
                var wa = SystemParameters.WorkArea;
                var w = (ActualWidth > 0 ? ActualWidth : Width);
                var h = (ActualHeight > 0 ? ActualHeight : Height);
                Left = Math.Max(wa.Left, wa.Left + (wa.Width - w) / 2);
                Top = Math.Max(wa.Top, wa.Top + (wa.Height - h) / 2);
            }
            // 注意：不能做 Opacity 淡入动画 —— 动画期间窗口处于分层（layered）状态，
            // WebView2 子窗口区域会被 DWM 渲染成整块黑色。就绪后直接显示即可。
            Opacity = 1d;
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
            ShowPage("首页", true);   // 重建旧版首页用于回退
            ShowNativeShell();
            FireWebShellReady();
            return;
        }

        try
        {
            BootLog("InitWebShell 开始");
            EnsureWebShellFiles();   // 嵌入资源先落盘（单文件版依赖这一步）
            var index = Path.Combine(WebShellDir, "index.html");
            if (!File.Exists(index))
            {
                _webShellFailed = true;
                ShowPage("首页", true);   // 重建旧版首页用于回退
                ShowNativeShell();
                FireWebShellReady();
                return;
            }

            Directory.CreateDirectory(WebViewProfileDir);
            // 启动阶段主窗口在屏幕外完成首帧渲染：必须禁用 Chromium 的遮挡/后台降频，
            // 否则离屏窗口会被判定为"被遮挡"，渲染被暂停，首帧永远等不到（表现为一直不显示）。
            // 环境在构造函数最开始就并行创建（静态缓存），这里通常直接取到现成的
            BootLog("取 WebView2 环境");
            TrimOwnMemory();
            var env = await GetEnvironmentAsync();
            BootLog("环境已就绪，绑定控件");
            // WPF 的 HwndHost 是惰性建 HWND 的：控件可见 ≠ 窗口句柄已存在。
            // 没有句柄就调 EnsureCoreWebView2Async，控制器创建会一直等到超时。
            for (int i = 0; i < 4000 && PresentationSource.FromVisual(WebHost) == null; i++)
                await Task.Delay(50);
            var boundDir = _environmentDir ?? WebViewProfileDir;

            // ★ 本机时钟存在"狂奔"故障（QPC 与系统时间同步变快，实测 25 秒真实时间
            //   被记成 2 小时），狂奔期 Chromium 内部定时器全部瞬间到期，控制器创建
            //   必然返回 0x800705B4。所以：① 绝不按时间设超时；② 狂奔期每次失败在
            //   真实时间里不到 1 秒，成本极低 —— 连续换全新目录重试，直到时钟恢复
            //   正常窗口的某一次尝试成功为止（上午时钟正常时同样代码一次成功）。
            Exception? lastBindError = null;
            var bindOk = false;
            for (int attempt = 1; attempt <= 10 && !bindOk; attempt++)
            {
                string dir;
                if (attempt == 1) dir = boundDir;
                else if (attempt == 2 && boundDir != WebViewProfileDir) dir = WebViewProfileDir;
                else dir = WebViewProfileDir + "-rec-" + Guid.NewGuid().ToString("N")[..8];

                try
                {
                    var e = attempt == 1 ? env : await CreateEnvironmentSafeAsync(dir);
                    if (attempt > 1) BootLog($"第 {attempt} 次尝试绑定（目录 {Path.GetFileName(dir)}）");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    await WebHost.EnsureCoreWebView2Async(e);
                    _environmentDir = dir;
                    SaveLastGoodDir(dir);
                    bindOk = true;
                    BootLog($"控件已绑定（第 {attempt} 次尝试成功，内部计时 {sw.ElapsedMilliseconds}ms）");
                }
                catch (Exception ex)
                {
                    lastBindError = ex;
                    BootLog($"第 {attempt} 次绑定失败：{ex.Message}");
                    await Task.Delay(200);
                    TrimOwnMemory();
                }
            }
            if (!bindOk)
                throw new InvalidOperationException("WebView2 控制器创建连续 10 次失败（本机时钟故障或运行时损坏）", lastBindError);
            // 首帧渲染前 WebView2 底面强制云白（双保险，XAML 里也已设置）
            try { WebHost.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0xF3, 0xF5, 0xF9); } catch { }

            var core = WebHost.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsSwipeNavigationEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPinchZoomEnabled = false;
            core.WebMessageReceived += OnWebMessage;

            // 以虚拟域名加载本地文件，行为等价于 https 页面（localStorage 可用、无 file:// 限制）
            core.SetVirtualHostNameToFolderMapping(
                "nex.local", WebShellDir, CoreWebView2HostResourceAccessKind.Allow);

            // WebView2 底色已强制云白（XAML + 代码双保险），首帧前是云白而不是黑，
            // 无需折叠控件 —— 折叠会让 Chromium 降频，反而拖慢首帧。
            // ?v= 用 index.html 的最后写入时间做版本号：页面文件更新后强制绕过 WebView2 HTTP 缓存
            BootLog("开始导航");
            string pageVer;
            try { pageVer = File.GetLastWriteTimeUtc(index).Ticks.ToString("x"); }
            catch { pageVer = "0"; }
            core.Navigate($"https://nex.local/index.html?app=1&v={pageVer}");

            core.NavigationCompleted += async (_, e) =>
            {
                BootLog("NavigationCompleted");
                if (!e.IsSuccess) return;
                // 兜底：网页脚本异常没发 ready 时也要放行
                await Task.Delay(600);
                if (!_readyFired) FireWebShellReady();
            };

            _webShellReady = true;
            ShowWebShell();
        }
        catch (Exception ex)
        {
            // 系统未安装 WebView2 运行时等情况：安静回退经典界面，不影响任何功能
            BootLog("★ 两次绑定均失败，回退旧版界面：" + ex.Message);
            _webShellFailed = true;
            ShowPage("首页", true);   // 重建旧版首页用于回退
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
        try { WebLoading.Visibility = Visibility.Collapsed; } catch { }
        RootBorder.Visibility = Visibility.Collapsed;
        WebShellLayer.Visibility = Visibility.Visible;
        try { WebHost.Focus(); } catch { }
    }

    private void ShowNativeShell()
    {
        try { WebLoading.Visibility = Visibility.Collapsed; } catch { }
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
                try { WebHost.Visibility = Visibility.Visible; } catch { }
                PushState();
                AttachCornerServices();   // 右下角悬浮：下载中心快照 + 游戏进程状态（幂等）
                PushDlTasks();
                PostJson(new { type = "game-running", running = _minecraftService.ProcessCount > 0 });
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

            case "kill-game":
                // PCL2 式右下角「强制结束」按钮：结束本启动器启动的全部游戏进程树
                _ = KillGameFromWebAsync();
                break;

            case "del-ver":
                // 游戏版本页：删除已安装版本（两步确认后触发，走 DeleteLocalVersion 的
                // 路径保护 / 链接导入只删链接逻辑）
                _ = DeleteVersionFromWeb(root);
                break;

            case "dl-clear":
                Services.DownloadManagerService.Instance.ClearFinished();
                break;

            case "theme":
                ApplyThemeFromWeb(root);
                break;

            case "ram":
                // 新版设置页的内存滑条 → 写回真实设置（与经典设置页同一份配置）
                if (root.TryGetProperty("mb", out var mbEl) && mbEl.TryGetInt32(out var mb))
                {
                    mb = Math.Clamp(mb, 512, 65536);
                    _settings.MaxRamMb = mb;
                    if (_settings.MinRamMb > mb) _settings.MinRamMb = mb;
                    _settings.Save();
                }
                break;

            case "catalog":
                // 新版「游戏版本」页请求官方版本目录（可下载列表）
                _ = SendCatalogAsync();
                break;

            case "loader-info":
                // 新版「下载版本」页弹出加载器选择窗时预取 Forge/Fabric/Quilt 版本（PCL 行为）
                _ = SendLoaderInfoAsync(
                    root.TryGetProperty("id", out var liId) ? liId.GetString() ?? "" : "");
                break;

            case "tool":
                // 新版界面上所有真实功能入口（Java/存档/模组/皮肤/下载/开服/隔离区…）
                HandleWebTool(root);
                break;

            case "action":
                HandleWebAction(root);
                break;

            case "open-url":
                OpenExternal(root);
                break;
        }
    }

    /// <summary>新版界面里的按钮 → 真实原生功能（只弹独立对话框，绝不切换界面层）。</summary>
    private void HandleWebAction(JsonElement root)
    {
        var action = root.TryGetProperty("action", out var a) ? a.GetString() : null;
        switch (action)
        {
            case "select-version":
                {
                    var id = root.TryGetProperty("id", out var i) ? i.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) return;
                    _settings.LastQuickVersion = id;
                    _settings.Save();
                    _suppressShellSwitch = true;
                    try { SideVersionBox.SelectedItem = id; }
                    catch { }
                    finally { _suppressShellSwitch = false; }
                    PushState();
                    break;
                }
            case "import-version":
                ImportLocalVersions_Click();
                PushState();
                break;
            case "login-microsoft":
                Login_Click(this, new RoutedEventArgs());
                _ = RefreshStateSoonAsync();
                break;
            case "login-offline":
                Offline_Click(this, new RoutedEventArgs());
                _ = RefreshStateSoonAsync();
                break;
            case "login-authlib":
                {
                    var dlg = new AuthlibLoginDialog(_settings) { Owner = this };
                    dlg.ShowDialog();
                    if (dlg.ResultAccount != null)
                    {
                        UpsertSavedAccount(dlg.ResultAccount);
                        ApplyAccountItem(dlg.ResultAccount);
                    }
                    PushState();
                    break;
                }
            case "install-version":
                {
                    var id = root.TryGetProperty("id", out var iv) ? iv.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) break;
                    var loader = root.TryGetProperty("loader", out var ld) ? ld.GetString() : null;
                    var loaderVer = root.TryGetProperty("loaderVersion", out var lv) ? lv.GetString() : null;
                    _ = InstallVersionFromWebAsync(id, loader, loaderVer);
                    break;
                }
            case "install-modpack":
                {
                    // 新版资源页「下载安装」整合包：下载最新 .mrpack → 打开原生安装向导
                    var id = root.TryGetProperty("id", out var mi) ? mi.GetString() : null;
                    var title = root.TryGetProperty("title", out var mt) ? mt.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) break;
                    _ = DownloadAndInstallModpackAsync(id, title ?? "整合包");
                    break;
                }
            case "download-content":
                {
                    // 新版资源页「下载到游戏目录」：模组→mods、资源包→resourcepacks、光影→shaderpacks、数据包→datapacks
                    var id = root.TryGetProperty("id", out var ci) ? ci.GetString() : null;
                    var title = root.TryGetProperty("title", out var ct) ? ct.GetString() : null;
                    var type = root.TryGetProperty("type", out var ty) ? ty.GetString() : null;
                    var withDeps = root.TryGetProperty("withDeps", out var wd) && wd.ValueKind == JsonValueKind.True;
                    var versionId = root.TryGetProperty("versionId", out var vid) ? vid.GetString() : null;
                    if (string.IsNullOrWhiteSpace(id)) break;
                    _ = DownloadContentAsync(id, title ?? "资源", type ?? "mod", versionId, withDeps);
                    break;
                }
            case "logout":
                Logout_Click(this, new RoutedEventArgs());
                _ = RefreshStateSoonAsync();
                break;
        }
    }

    // ---------------------------------------------------------------- 官方版本目录（下载游戏版本）

    private List<Services.MinecraftVersionItem>? _catalogCache;
    private bool _catalogLoading;
    private string? _installingVersion;

    /// <summary>把 Mojang 官方版本目录推给新版界面（带缓存，只拉一次）。</summary>
    private async Task SendCatalogAsync()
    {
        try
        {
            if (_catalogCache == null)
            {
                if (_catalogLoading) return;
                _catalogLoading = true;
                try { _catalogCache = (await _minecraftService.GetVersionsAsync()).ToList(); }
                finally { _catalogLoading = false; }
            }
            PostJson(new
            {
                type = "catalog",
                installing = _installingVersion,
                items = _catalogCache.Select(v => new
                {
                    id = v.Id,
                    type = v.Type,
                    category = v.Category,
                    date = v.ReleaseDate,
                    installed = v.IsInstalled
                })
            });
        }
        catch (Exception ex)
        {
            PostJson(new { type = "catalog-fail", msg = ex.Message });
        }
    }

    /// <summary>新版「游戏版本」页点击下载 → 走原生 CmlLib 安装（进度回推到网页）。
    /// loader/loaderVersion 非空时先装模组加载器（PCL 行为：Forge 用官方安装器，Fabric/Quilt 写 profile）。</summary>
    private async Task InstallVersionFromWebAsync(string versionId, string? loader = null, string? loaderVersion = null)
    {
        if (_installingVersion != null)
        {
            PostJson(new { type = "install-busy", id = versionId, current = _installingVersion });
            return;
        }
        _installingVersion = versionId;
        PostJson(new { type = "install-start", id = versionId });

        var dlItem = Services.DownloadManagerService.Instance.Add($"Minecraft {versionId}", Services.DownloadKind.Game);
        dlItem.Status = Services.DownloadStatus.Downloading;
        _activeGameDownload = dlItem;   // 复用原生进度事件 → 右下角下载面板

        try
        {
            // 轮询进度并回推网页（原生事件已在更新 dlItem.Progress / Detail）
            var done = Task.Run(async () =>
            {
                // 先装加载器（此时还没装游戏本体，加载器 profile/安装器只写 json 或自行处理）
                string installId = versionId;
                if (!string.IsNullOrWhiteSpace(loader) && loader != "vanilla")
                {
                    var ml = new Services.ModloaderService();
                    if (loader == "forge")
                    {
                        var forgeVer = loaderVersion;
                        if (string.IsNullOrWhiteSpace(forgeVer))
                        {
                            var info = await ml.GetForgeLoaderInfoAsync(versionId);
                            forgeVer = info.Recommended ?? info.Latest ?? info.Versions.FirstOrDefault();
                        }
                        if (!string.IsNullOrWhiteSpace(forgeVer))
                            installId = await ml.InstallForgeAsync(_gameDirectory, versionId, forgeVer);
                    }
                    else if (loader is "fabric" or "quilt")
                    {
                        var lver = loaderVersion;
                        if (string.IsNullOrWhiteSpace(lver))
                        {
                            var list = loader == "fabric"
                                ? await ml.GetFabricLoaderVersionsAsync(versionId)
                                : await ml.GetQuiltLoaderVersionsAsync(versionId);
                            lver = list.FirstOrDefault();
                        }
                        if (!string.IsNullOrWhiteSpace(lver))
                            installId = await ml.InstallLoaderProfileAsync(_gameDirectory, versionId, loader, lver);
                    }
                }
                await _minecraftService.InstallAsync(installId);
            });
            while (!done.IsCompleted)
            {
                await Task.Delay(700);
                PostJson(new
                {
                    type = "install-progress",
                    id = versionId,
                    pct = Math.Clamp(dlItem.Progress, 0, 1),
                    detail = dlItem.Detail
                });
            }
            await done;   // 若抛异常在这里冒出

            Services.DownloadManagerService.Instance.Complete(dlItem);
            // 更新原生缓存里的安装标记
            var idx = _allVersions.FindIndex(x => x.Id == versionId);
            if (idx >= 0) _allVersions[idx] = _allVersions[idx] with { IsInstalled = true };
            if (_catalogCache != null)
            {
                var ci = _catalogCache.FindIndex(x => x.Id == versionId);
                if (ci >= 0) _catalogCache[ci] = _catalogCache[ci] with { IsInstalled = true };
            }
            PostJson(new { type = "install-done", id = versionId });
            toastFromWeb($"版本 {versionId} 安装完成");
            PushState();
        }
        catch (Exception ex)
        {
            dlItem.Status = Services.DownloadStatus.Error;
            PostJson(new { type = "install-fail", id = versionId, msg = ex.Message });
            toastFromWeb($"版本 {versionId} 安装失败：{ex.Message}");
        }
        finally
        {
            _activeGameDownload = null;
            _installingVersion = null;
        }
    }

    private void toastFromWeb(string msg) => PostJson(new { type = "toast", msg });

    /// <summary>并行获取某游戏版本可用的模组加载器信息（Forge/Fabric/Quilt），推给网页弹窗（PCL 行为）。</summary>
    private async Task SendLoaderInfoAsync(string gameVersion)
    {
        if (string.IsNullOrWhiteSpace(gameVersion)) return;
        try
        {
            var ml = new Services.ModloaderService();
            var forgeTask = Task.Run(async () =>
            {
                try
                {
                    var info = await ml.GetForgeLoaderInfoAsync(gameVersion);
                    return new { versions = info.Versions.Take(30).ToList(), recommended = info.Recommended };
                }
                catch { return null; }
            });
            var fabricTask = Task.Run(async () =>
            {
                try { return new { versions = (await ml.GetFabricLoaderVersionsAsync(gameVersion)).Take(30).ToList(), recommended = (string?)null }; }
                catch (Exception ex) { BootLog("fabric 获取失败: " + (ex.InnerException?.InnerException?.Message ?? ex.InnerException?.Message ?? ex.Message)); return null; }
            });
            var quiltTask = Task.Run(async () =>
            {
                try { return new { versions = (await ml.GetQuiltLoaderVersionsAsync(gameVersion)).Take(30).ToList(), recommended = (string?)null }; }
                catch (Exception ex) { BootLog("quilt 获取失败: " + (ex.InnerException?.InnerException?.Message ?? ex.InnerException?.Message ?? ex.Message)); return null; }
            });
            await Task.WhenAll(forgeTask, fabricTask, quiltTask);

            PostJson(new
            {
                type = "loader-info",
                id = gameVersion,
                forge = forgeTask.Result,
                fabric = fabricTask.Result,
                quilt = quiltTask.Result,
                fail = forgeTask.Result == null && fabricTask.Result == null && quiltTask.Result == null,
            });
        }
        catch (Exception ex)
        {
            PostJson(new { type = "loader-info", id = gameVersion, fail = true, msg = ex.Message });
        }
    }

    /// <summary>资源页整合包「下载安装」：下载最新 .mrpack → 打开原生安装向导（复用经典页安装流程）。</summary>
    private async Task DownloadAndInstallModpackAsync(string projectId, string title)
    {
        var dlItem = Services.DownloadManagerService.Instance.Add($"整合包 {title}", Services.DownloadKind.Mod);
        dlItem.Status = Services.DownloadStatus.Downloading;
        try
        {
            toastFromWeb($"正在获取「{title}」的整合包文件…");
            var svc = new Services.ModrinthService();
            var latest = await svc.GetLatestDownloadAsync(projectId)
                ?? throw new Exception("该项目没有可下载的文件");
            var dir = Path.Combine(Path.GetTempPath(), "NEXLauncher-modpacks");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, latest.FileName);

            var last = DateTime.MinValue;
            await svc.DownloadFileAsync(latest.Url, dest, new Progress<(long Received, long Total)>(p =>
            {
                if (p.Total > 0) dlItem.Progress = (double)p.Received / p.Total;
                if ((DateTime.Now - last).TotalMilliseconds > 600)
                {
                    last = DateTime.Now;
                    var mb = p.Total > 0 ? $" {p.Received / 1048576.0:0.0}/{p.Total / 1048576.0:0.0} MB" : $" {p.Received / 1048576.0:0.0} MB";
                    toastFromWeb($"正在下载整合包{mb}");
                }
            }));
            dlItem.Progress = 1;
            Services.DownloadManagerService.Instance.Complete(dlItem);
            toastFromWeb("下载完成，正在打开安装向导…");

            var path = dest;
            await Dispatcher.InvokeAsync(async () => await InstallModpackAsync(path));
        }
        catch (Exception ex)
        {
            Services.DownloadManagerService.Instance.Fail(dlItem, ex.Message);
            PostJson(new { type = "download-fail", msg = $"整合包下载失败：{ex.Message}" });
        }
    }

    /// <summary>资源页模组/资源包/光影/数据包「下载到游戏目录」：下载最新文件到对应文件夹。</summary>
    private async Task DownloadContentAsync(string projectId, string title, string type,
        string? versionId = null, bool withDeps = false)
    {
        var dlItem = Services.DownloadManagerService.Instance.Add($"{title}", Services.DownloadKind.Mod);
        dlItem.Status = Services.DownloadStatus.Downloading;
        try
        {
            var svc = new Services.ModrinthService();
            // PCL2 式版本选择：前端弹窗选中的具体文件版本优先，没传就用最新版
            var picked = await svc.GetVersionAsync(projectId, versionId)
                ?? throw new Exception("该项目没有可下载的文件");
            var file = picked.PrimaryFile ?? throw new Exception("该版本没有可下载的文件");
            var targetGame = picked.GameVersions.FirstOrDefault();
            var targetLoader = picked.Loaders.FirstOrDefault(l => !l.Equals("minecraft", StringComparison.OrdinalIgnoreCase));
            var sub = type switch
            {
                "resourcepack" => "resourcepacks",
                "shader" => "shaderpacks",
                "datapack" => "datapacks",
                _ => "mods",
            };
            // 模组必须存到「模组页实际扫描的目录」：开了版本隔离时在 versions/<id>/mods，
            // 否则用户下载完在模组页永远看不到（资源包/光影是全局目录，仍放根目录）
            var dir = sub == "mods"
                ? ModsDirFor(CurrentVersionId())
                : Path.Combine(_gameDirectory, sub);
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, file.FileName);
            if (File.Exists(dest)) { try { File.Delete(dest); } catch { /* 被占用时交给下载器覆盖 */ } }

            await svc.DownloadFileAsync(file.Url, dest, new Progress<(long Received, long Total)>(p =>
            {
                if (p.Total > 0) dlItem.Progress = (double)p.Received / p.Total;
            }));
            Services.DownloadManagerService.Instance.Complete(dlItem);
            PostJson(new { type = "download-ok", msg = $"「{title}」{picked.VersionNumber} 已下载到 {sub}\\" + file.FileName });

            // 用户在弹窗里选择了「下载前置」：把必需前置一并下载到同一目录（按目标游戏版本挑前置）
            if (withDeps)
            {
                try
                {
                    var deps = await svc.GetRequiredDependenciesAsync(projectId, picked.Id, targetGame, targetLoader);
                    foreach (var dep in deps)
                    {
                        var depItem = Services.DownloadManagerService.Instance.Add(
                            $"前置 · {dep.Title}", Services.DownloadKind.Mod);
                        depItem.Status = Services.DownloadStatus.Downloading;
                        try
                        {
                            var depDest = Path.Combine(dir, dep.FileName);
                            await svc.DownloadFileAsync(dep.Url, depDest, new Progress<(long Received, long Total)>(p =>
                            {
                                if (p.Total > 0) depItem.Progress = (double)p.Received / p.Total;
                            }));
                            Services.DownloadManagerService.Instance.Complete(depItem);
                        }
                        catch (Exception dex)
                        {
                            Services.DownloadManagerService.Instance.Fail(depItem, dex.Message);
                        }
                    }
                    if (deps.Count > 0)
                        PostJson(new { type = "download-ok", msg = $"已下载 {deps.Count} 个前置依赖" });
                }
                catch (Exception ex)
                {
                    PostJson(new { type = "toast", msg = $"前置下载失败：{ex.Message}" });
                }
            }
        }
        catch (Exception ex)
        {
            Services.DownloadManagerService.Instance.Fail(dlItem, ex.Message);
            PostJson(new { type = "download-fail", msg = $"「{title}」下载失败：{ex.Message}" });
        }
    }

    /// <summary>登录/退出是异步完成的，短时间内多次回推状态，让新版界面自动刷新。</summary>
    private async Task RefreshStateSoonAsync()
    {
        for (var i = 0; i < 6; i++)
        {
            await Task.Delay(700);
            PushState();
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
        string json;
        try { json = JsonSerializer.Serialize(payload); }
        catch { return; }
        try
        {
            // ★ WebView2 有线程亲和性：PostWebMessageAsJson 必须在创建它的 UI 线程上调用。
            //   之前工具回复（mods-list / skins-page 等）都在 Task.Run 的后台线程里直接发，
            //   调用抛异常被下面的 catch 静默吞掉 —— C# 日志记了"回复成功"，
            //   页面却永远等不到消息，表现为"正在读取…/正在拉取…"永久卡死。
            Dispatcher.Invoke(() => WebHost.CoreWebView2?.PostWebMessageAsJson(json));
        }
        catch { }
    }

    private bool _cornerAttached;
    private string _lastDlSig = "";

    /// <summary>
    /// 挂接右下角悬浮（PCL2 风格）所需数据源：下载中心快照推送 + 游戏进程状态事件。幂等。
    /// </summary>
    private void AttachCornerServices()
    {
        if (_cornerAttached) return;
        _cornerAttached = true;

        Services.DownloadManagerService.Instance.TasksChanged += (_, _) => PushDlTasks();
        _minecraftService.GameStarted += (_, _) => PostJson(new { type = "game-running", running = true });
        _minecraftService.GameExited += (_, _) => PostJson(new { type = "game-running", running = false });

        // 下载进度变化不触发 TasksChanged，用 1 秒定时器推送快照（本地桥，开销可忽略；
        // PushDlTasks 内部按签名去重，无变化不发包）
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) => PushDlTasks();
        timer.Start();
    }

    /// <summary>推送下载中心快照（游戏版本 / 资源·模组 两组）给右下角下载面板。</summary>
    private void PushDlTasks()
    {
        if (!_webShellReady) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(PushDlTasks); return; }
        var dm = Services.DownloadManagerService.Instance;
        var g = dm.GameDownloads.Take(12).Select(x => new
        {
            name = x.Name,
            pct = x.ProgressPercent,
            status = x.Status.ToString().ToLowerInvariant(),
            detail = x.Detail
        }).ToList();
        var m = dm.ModDownloads.Take(12).Select(x => new
        {
            name = x.Name,
            pct = x.ProgressPercent,
            status = x.Status.ToString().ToLowerInvariant(),
            detail = x.Detail
        }).ToList();
        var sig = string.Concat(g.Select(t => $"{t.name}|{t.pct}|{t.status};"))
                + "#" + string.Concat(m.Select(t => $"{t.name}|{t.pct}|{t.status};"));
        if (sig == _lastDlSig) return;   // 无变化不发包
        _lastDlSig = sig;
        PostJson(new { type = "dl-tasks", game = g, mod = m });
    }

    private async Task KillGameFromWebAsync()
    {
        var killed = await Task.Run(() => _minecraftService.KillAllGames());
        PostJson(new { type = "toast", msg = killed > 0 ? $"已强制结束 {killed} 个游戏进程" : "没有正在运行的游戏进程" });
        PostJson(new { type = "game-running", running = _minecraftService.ProcessCount > 0 });
    }

    /// <summary>删除已安装版本（游戏版本页「删除」按钮，两步确认后调用）。</summary>
    private async Task DeleteVersionFromWeb(JsonElement root)
    {
        var id = root.TryGetProperty("id", out var el) ? (el.GetString() ?? "") : "";
        if (string.IsNullOrWhiteSpace(id))
        {
            PostJson(new { type = "toast", msg = "删除失败：未指定版本" });
            return;
        }
        string? deleted;
        try { deleted = await Task.Run(() => _minecraftService.DeleteLocalVersion(id)); }
        catch (Exception ex)
        {
            // 典型原因：游戏正在运行、文件被占用
            PostJson(new { type = "toast", msg = $"删除失败：{ex.Message}" });
            return;
        }
        if (deleted == null)
        {
            PostJson(new { type = "toast", msg = $"删除失败：找不到版本 {id}" });
            return;
        }
        // 被删的是上次快速启动版本时清掉引用，避免残留失效选择
        if (string.Equals(_settings.LastQuickVersion, id, StringComparison.OrdinalIgnoreCase))
        {
            _settings.LastQuickVersion = "";
            _settings.Save();
        }
        PostJson(new { type = "toast", msg = $"已删除版本 {id}" });
        PushState();
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

        // 未登录时不把上次的离线名字当成当前账号展示（用户反馈：退出后还显示旧名字）；
        // 名字只通过 offlineName 字段给说明文字引用
        var accountName = _session?.Username ?? "";
        var isMicrosoft = string.Equals(_settings.LastAccountType, "microsoft", StringComparison.OrdinalIgnoreCase);

        PostJson(new
        {
            type = "state",
            account = new
            {
                name = accountName,
                kind = isMicrosoft ? "微软正版" : "离线模式",
                loggedIn = _session != null,
                offlineName = string.IsNullOrWhiteSpace(_settings.LastOfflineName)
                    ? (_settings.QuickOfflineName ?? "Player")
                    : _settings.LastOfflineName,
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
