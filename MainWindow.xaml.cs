using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CmlLib.Core.Auth;
using Microsoft.Win32;
using NEXLauncher.Controls;
using NEXLauncher.Services;

namespace NEXLauncher;

public partial class MainWindow : Window
{
    private readonly string _gameDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
    private readonly LauncherSettings _settings = LauncherSettings.Load();
    private readonly MinecraftService _minecraftService;
    private readonly ModrinthService _modrinthService = new();
    private MSession? _session;

    // 版本页状态
    private List<MinecraftVersionItem> _allVersions = [];
    private string _versionFilter = "全部";
    private StackPanel? _versionListPanel;
    private TextBlock? _versionStatus;
    private int _versionShown = 50;

    // 内容页状态
    private string _catalogProjectType = "mod";
    private StackPanel? _catalogListPanel;
    private TextBox? _catalogSearchBox;
    private ComboBox? _catalogVersionBox;
    private ComboBox? _catalogCategoryBox;
    private TextBlock? _catalogStatus;
    private TextBlock? _catalogHeading;
    private UIElement? _localModsCard;
    private CancellationTokenSource? _catalogCts;

    // 快速启动
    private ComboBox? _quickVersionBox;

    // 联机页：隧道与服务端管理
    private readonly PlayitTunnelService _playitService = new();
    private readonly ServerDirectoryService _serverDirectoryService;
    private bool _playitHooked;
    private TextBlock? _tunnelStatus;
    private TextBlock? _tunnelAddressText;
    private TextBox? _tunnelLog;
    private Button? _tunnelButton;
    private StackPanel? _serverListPanel;
    private TextBlock? _serverStatus;

    // 一键开服
    private readonly OneClickServerService _oneClickService = new();
    private bool _oneClickHooked;
    private ComboBox? _oneClickVersionBox;
    private ComboBox? _oneClickRamBox;
    private CheckBox? _oneClickOfflineBox;
    private Button? _oneClickButton;
    private TextBlock? _oneClickStatus;
    private TextBlock? _oneClickAddressText;
    private Button? _oneClickClaimBtn;
    private Button? _oneClickCopyClaimBtn;
    private TextBox? _oneClickLog;
    private TextBox? _oneClickCmdBox;
    private readonly List<string> _oneClickCmdHistory = new();
    private int _oneClickCmdHistoryIndex = -1;
    private Button? _tunnelClaimBtn;
    private Button? _tunnelCopyClaimBtn;

    // ---------------------------------------------------------------- 系统托盘

    private System.Windows.Forms.NotifyIcon? _trayIcon;

    /// <summary>
    /// 系统托盘图标：出现在右下角「显示隐藏的图标」里；右键菜单可完整退出
    /// （结束全部游戏进程树 + 释放托盘 + 结束启动器后台与所有进程）。
    /// </summary>
    private void SetupTrayIcon()
    {
        try
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Text = "NEX Launcher - Minecraft 启动器",
                Visible = true
            };
            try
            {
                var exePath = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(exePath) && System.IO.File.Exists(exePath))
                    _trayIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
            }
            catch { }

            var menu = new System.Windows.Forms.ContextMenuStrip();
            menu.Items.Add("打开启动器", null, (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    Show();
                    WindowState = WindowState.Normal;
                    Activate();
                });
            });
            menu.Items.Add("退出启动器", null, (_, _) => ExitFully());
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) =>
            {
                Dispatcher.Invoke(() => { Show(); WindowState = WindowState.Normal; Activate(); });
            };
        }
        catch { }
    }

    /// <summary>完整退出：结束全部游戏进程树 → 释放托盘 → 结束启动器（后台与全部进程）。</summary>
    private void ExitFully()
    {
        try
        {
            var killed = _minecraftService?.KillAllGames() ?? 0;
            if (killed > 0) _trayIcon?.ShowBalloonTip(1200, "NEX Launcher", $"已结束 {killed} 个游戏进程，正在退出…", System.Windows.Forms.ToolTipIcon.Info);
        }
        catch { }
        try
        {
            Dispatcher.Invoke(() =>
            {
                _trayIcon?.Dispose();
                _trayIcon = null;
                Application.Current.Shutdown();
            });
        }
        catch { }
        // 兜底：确保任何后台线程/WebView2 子进程都不残留
        Environment.Exit(0);
    }

    // ---------------------------------------------------------------- 系统托盘结束

    public MainWindow()
    {
        InitializeComponent();
        // 第一优先级：立刻开始创建 WebView2 运行时环境（约 1~3 秒），与后续构造工作并行
        WarmUpEnvironment();
        // 后台预取离线皮肤注入器（authlib-injector），让「应用皮肤」一点即生效
        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            try { await Services.LocalSkinServer.Instance.EnsureAgentAsync(); } catch { }
        });
        _minecraftService = new MinecraftService(_gameDirectory);
        _serverDirectoryService = new ServerDirectoryService(_gameDirectory);
        PageHost.RenderTransform = new TranslateTransform(0, 0);
        SetupTrayIcon();

        // 应用已保存的个性化设置（先主题预设，再用户自定义强调色）
        // 0.4.1 一次性迁移：旧版把默认蓝 #5B9DFF 当成"自定义强调色"存盘，会盖掉主题主色
        if (!_settings.ThemeMigrated041)
        {
            if (string.IsNullOrWhiteSpace(_settings.AccentColor) ||
                string.Equals(_settings.AccentColor, "#5B9DFF", StringComparison.OrdinalIgnoreCase))
                _settings.AccentColor = "";
            if (string.Equals(_settings.ThemePreset, "深蓝", StringComparison.OrdinalIgnoreCase))
                _settings.ThemePreset = "熔光";
            _settings.ThemeMigrated041 = true;
            _settings.Save();
        }

        UiPalette.ApplyPreset(_settings.ThemePreset);
        UiPalette.SetAccent(_settings.AccentColor);

        // 3.9.8 一次性迁移：默认改为浅色「云白」，老用户深色主题平滑切换（仍可在设置里换回）
        if (!_settings.ThemeMigrated398)
        {
            if (_settings.ThemePreset is not ("云白")) _settings.ThemePreset = "云白";
            _settings.ThemeMigrated398 = true;
            _settings.Save();
            UiPalette.ApplyPreset(_settings.ThemePreset);
        }
        Title = _settings.WindowTitle;
        if (!string.IsNullOrEmpty(_settings.BackgroundImagePath))
            ApplyBackgroundImage(_settings.BackgroundImagePath);

        // 游戏进程跟踪：显示 / 隐藏右下角强杀按钮
        _minecraftService.GameStarted += (_, e) => Dispatcher.Invoke(() =>
        {
            KillGameButton.Visibility = Visibility.Visible;
            SideLaunchButton.Content = "● 游戏运行中";
            BringGameWindowToFront(e); // 窗口一出现就自动前置，避免用户误以为启动卡住
        });
        _minecraftService.GameExited += (_, _) => Dispatcher.Invoke(() =>
        {
            KillGameButton.Visibility = Visibility.Collapsed;
            SideLaunchButton.Content = "▶  启动游戏";
        });

        // 版本安装进度 → 下载面板（游戏版本组）
        _minecraftService.FileProgressChanged += (_, e) => Dispatcher.Invoke(() =>
        {
            if (_activeGameDownload == null) return;
            if (e.TotalTasks > 0)
                _activeGameDownload.Progress = (double)e.ProgressedTasks / e.TotalTasks;
            if (!string.IsNullOrEmpty(e.Name))
                _activeGameDownload.Detail = $"{e.ProgressedTasks}/{e.TotalTasks} 个文件 · {e.Name}";
        });
        _minecraftService.ByteProgressChanged += (_, e) => Dispatcher.Invoke(() =>
        {
            if (_activeGameDownload == null || e.TotalBytes <= 0) return;
            _activeGameDownload.Detail =
                $"{FormatBytes(e.ProgressedBytes)} / {FormatBytes(e.TotalBytes)}";
        });

        // 侧栏版本选择记忆
        SideVersionBox.SelectionChanged += (_, _) =>
        {
            if (SideVersionBox.SelectedItem is string v)
            {
                _settings.LastQuickVersion = v;
                _settings.Save();
                UpdateSideLoaderText(v);
            }
        };

        // 下载任务面板数据绑定
        GameDownloadList.ItemsSource = DownloadManagerService.Instance.GameDownloads;
        ModDownloadList.ItemsSource = DownloadManagerService.Instance.ModDownloads;
        DownloadManagerService.Instance.TasksChanged += (_, _) => Dispatcher.Invoke(UpdateDownloadUi);
        UpdateDownloadUi();

        // 调试/自动化用：NEX_START_PAGE=资源下载 可直接打开指定页面，正式使用不受影响
        var startPage = Environment.GetEnvironmentVariable("NEX_START_PAGE");
        ShowPage(string.IsNullOrWhiteSpace(startPage) ? "首页" : startPage);

        // 下载源开关（设置页可选镜像/官方）
        ModrinthService.UseMirror = _settings.UseMirror;

        // 关闭行为：开启「最小化到托盘」时，点关闭只收起窗口，下载与联机不中断
        Closing += (_, e) =>
        {
            if (!_settings.CloseToTray) return;
            e.Cancel = true;
            WindowState = WindowState.Minimized;
        };
        // 调试/自动化用：NEX_CLAIM_URL=<url> 直接打开内置授权窗口（验证 WebView2 渲染）
        var claimPreview = Environment.GetEnvironmentVariable("NEX_CLAIM_URL");
        if (!string.IsNullOrWhiteSpace(claimPreview))
            Loaded += (_, _) => new ClaimWindow(claimPreview) { Owner = this }.Show();
        // 启动即刷新侧栏版本列表（本地扫描，无需联网/无需进版本页）
        RefreshQuickVersionList();
        // 启动即补齐皮肤库（以前只在打开皮肤页时补，外部清空后要进页面才能恢复）
        SeedOfficialSkins();
        Loaded += async (_, _) =>
        {
            await RestoreLoginAsync();
            await CheckUpdateOnStartupAsync();
        };
        // 自动化自检：NEX_CATALOG_E2E=1 时启动后自动进入「资源下载」页（自检逻辑在 BuildDownloadPage 内）
        if (Environment.GetEnvironmentVariable("NEX_CATALOG_E2E") == "1")
            Loaded += (_, _) => ShowPage("资源下载");
        // 自动化自检：NEX_SKIN_E2E=1 时启动后自动进入「账号」页（自检逻辑在账号页内）
        if (Environment.GetEnvironmentVariable("NEX_SKIN_E2E") == "1")
            Loaded += (_, _) => ShowPage("账号");
        // 自动化自检：NEX_MP_EXP=1 时进入「联机 → 实验性功能」并验证体检/查询/邀请码
        if (Environment.GetEnvironmentVariable("NEX_MP_EXP") == "1")
            Loaded += (_, _) => _ = RunExperimentalE2EAsync();
        // 自动化自检：NEX_MODPACK_E2E=1 时验证版本页「整合包」入口、管理窗口与安装向导
        if (Environment.GetEnvironmentVariable("NEX_MODPACK_E2E") == "1")
            Loaded += (_, _) => _ = RunModpackE2EAsync();
        // 自动化自检：NEX_SEC_E2E=1 时验证安全防护页与五道防火墙引擎
        if (Environment.GetEnvironmentVariable("NEX_SEC_E2E") == "1")
            Loaded += (_, _) => _ = RunSecurityE2EAsync();

        // ---- 安全防护：启动五道防火墙（蜜罐/行为监控/系统守护），威胁命中弹窗 ----
        SecurityService.Instance.GameDirectory = _gameDirectory;
        SecurityService.Instance.ThreatDetected += OnSecurityThreat;
        // 24 小时后台守护已在运行 → 本进程只保留扫描/隔离能力，监控交给守护进程，避免重复告警
        SecurityService.Instance.MonitorSuspended = GuardDaemon.IsRunning;
        Loaded += (_, _) => SecurityService.Instance.Start();
        // 开启「24 小时自动防卫」时，启动即确保后台守护在跑（关闭启动器也不中断防护）
        if (Environment.GetEnvironmentVariable("NEX_SEC_E2E") != "1")
            Loaded += (_, _) => { _ = EnsureAlwaysGuardAsync(); };
        Closing += (_, _) =>
        {
            _secGuardTimer?.Stop();
            SecurityService.Instance.Dispose();
        };

        // ---- 新版界面（WebView2）：窗口就绪后加载；失败自动回退经典界面 ----
        Loaded += (_, _) =>
        {
            // 关键：等 UI 线程真正空闲再创建 WebView2 控制器。
            // Loaded 触发时 Splash 刚 Close、首帧布局/渲染还在排队，此时建控制器
            // 会被 UI 线程饿到超时（0x800705B4）——这正是"有时候点不开"的根源。
            // ApplicationIdle 优先级会在所有布局渲染完成后才执行，控制器 1~3 秒即可就绪。
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(InitWebShell));
        };
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1073741824.0:0.##} GB",
        >= 1L << 20 => $"{bytes / 1048576.0:0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes} B"
    };

    #region 标题栏
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            try { DragMove(); } catch { /* 鼠标状态异常时忽略 */ }
        }
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    #endregion

    #region 登录
    private DownloadItem? _activeGameDownload;

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        AccountStatus.Text = "正在打开登录窗口...";
        try
        {
            // 自家 OAuth 登录窗口（WPF + WebView2，云白界面），替代 CmlLib 内置 WinForms 窗口
            var login = new Services.MicrosoftLoginWindow(_settings) { Owner = this };
            login.ShowDialog();

            if (login.Result == null)
            {
                AccountStatus.Text = "已取消登录";
                return;
            }

            _session = login.Result.Value.Session;
            // 记录账号类型与凭据；刷新令牌用于下次启动静默恢复
            _settings.LastAccountType = "microsoft";
            _settings.MicrosoftSessionName = _session.Username ?? "";
            _settings.MicrosoftSessionUuid = _session.UUID ?? "";
            _settings.MicrosoftSessionToken = _session.AccessToken ?? "";
            _settings.MicrosoftRefreshToken = login.Result.Value.RefreshToken;
            _settings.Save();
            SyncCurrentToAccounts();
            UpdateAccountUi();
            MessageBox.Show($"登录成功，欢迎 {_session.Username}！", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AccountStatus.Text = "登录失败";
            var msg = ex.Message;
            if (msg.Contains("WebView2") || msg.Contains("webview") || msg.Contains("Edge"))
                msg = "未检测到 WebView2 运行环境。请安装微软 WebView2 Runtime 后重试。";
            MessageBox.Show($"微软登录失败：\n{msg}", "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            LoginButton.IsEnabled = true;
        }
    }

    private void Offline_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OfflineLoginDialog { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            _session = MinecraftService.CreateOfflineSession(dialog.UserName);
            // 记录账号类型和离线用户名，下次启动自动恢复。
            // 注意：不要覆盖 QuickOfflineName —— 那是「退出登录后默认用谁启动」，
            // 被覆盖会让用户退出后仍然顶着上一个账号，服务器那边等于换了个人。
            _settings.LastAccountType = "offline";
            _settings.LastOfflineName = dialog.UserName;
            _settings.Save();
            SyncCurrentToAccounts();
            UpdateAccountUi();
        }
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        var wasMicrosoft = _settings.LastAccountType == "microsoft";
        _session = null;
        _settings.LastAccountType = "";
        // 退出登录 = 回到「未登录」状态：清掉上次账号名，下次启动用 QuickOfflineName（默认 Player）。
        // 不清的话，界面写着未登录、实际仍以旧账号名启动，服务器那边就是另一个人，登录状态会丢。
        _settings.LastOfflineName = "";
        _settings.Save();
        if (wasMicrosoft) await _minecraftService.SignOutAsync();
        UpdateAccountUi();
    }

    /// <summary>启动器打开时自动恢复上次的登录状态（微软静默刷新 / 离线直接重建）。</summary>
    private async Task RestoreLoginAsync()
    {
        try
        {
            if (!_settings.AutoLogin || string.IsNullOrEmpty(_settings.LastAccountType)) return;

            if (_settings.LastAccountType == "offline" && !string.IsNullOrWhiteSpace(_settings.LastOfflineName))
            {
                _session = MinecraftService.CreateOfflineSession(_settings.LastOfflineName);
                UpdateAccountUi();
                return;
            }

            if (_settings.LastAccountType == "authlib")
            {
                // 外置登录：从多账号列表恢复（令牌过期时启动游戏会失败，需重新登录）
                var authlib = _settings.SavedAccounts.FirstOrDefault(a => a.Type == "authlib" && a.Key == _settings.CurrentAccountKey)
                    ?? _settings.SavedAccounts.FirstOrDefault(a => a.Type == "authlib");
                if (authlib != null)
                {
                    _session = new MSession
                    {
                        Username = authlib.Name,
                        UUID = authlib.Uuid,
                        AccessToken = authlib.AccessToken,
                        UserType = "authlib"
                    };
                    _settings.AuthlibApiRoot = authlib.ApiRoot;
                    _settings.AuthlibServerName = authlib.ServerName;
                    UpdateAccountUi();
                }
                else
                {
                    AccountStatus.Text = "未登录";
                    AccountTypeText.Text = "外置登录已失效，请重新登录";
                }
                return;
            }

            if (_settings.LastAccountType == "microsoft")
            {
                AccountStatus.Text = "正在恢复登录...";
                // 优先用自家 OAuth 刷新令牌静默恢复（自家登录流程写入的）
                if (!string.IsNullOrEmpty(_settings.MicrosoftRefreshToken))
                {
                    var refreshed = await MicrosoftAuthFlow.RefreshAsync(_settings.MicrosoftRefreshToken);
                    if (refreshed != null)
                    {
                        try
                        {
                            var (sess, newRefresh) = await MicrosoftAuthFlow.BuildSessionAsync(
                                refreshed.Value.AccessToken, refreshed.Value.RefreshToken);
                            _session = sess;
                            _settings.MicrosoftRefreshToken = newRefresh;
                        }
                        catch { _session = null; }
                    }
                }
                if (_session == null)
                    _session = await _minecraftService.TrySilentLoginAsync();
                if (_session != null)
                {
                    // 静默刷新成功：更新缓存的会话（令牌已续期）
                    _settings.MicrosoftSessionName = _session.Username ?? "";
                    _settings.MicrosoftSessionUuid = _session.UUID ?? "";
                    _settings.MicrosoftSessionToken = _session.AccessToken ?? "";
                    _settings.Save();
                    UpdateAccountUi();
                }
                else if (!string.IsNullOrEmpty(_settings.MicrosoftSessionToken))
                {
                    // 静默刷新失败：回退到上次缓存的会话（显示账号、短时间内的令牌仍可用于启动）
                    _session = new MSession
                    {
                        Username = _settings.MicrosoftSessionName,
                        UUID = _settings.MicrosoftSessionUuid,
                        AccessToken = _settings.MicrosoftSessionToken,
                        UserType = "msa"
                    };
                    UpdateAccountUi();
                    AccountTypeText.Text = "微软账号（凭据待刷新）";
                }
                else
                {
                    // 完全没有可恢复的信息，提示重新登录（保留记录以便下次重试）
                    AccountStatus.Text = "未登录";
                    AccountTypeText.Text = "登录已过期，请重新登录";
                }
            }
        }
        catch (Exception ex)
        {
            // 恢复登录失败不能影响启动器其他功能
            try
            {
                AccountStatus.Text = "未登录";
                AccountTypeText.Text = "登录恢复失败：" + ex.Message;
            }
            catch { }
        }
    }

    /// <summary>刷新侧栏账号卡片：用户名、账号类型、头像、退出按钮。</summary>
    private void UpdateAccountUi()
    {
        if (_session == null)
        {
            AccountStatus.Text = "未登录";
            AccountTypeText.Text = "点击按钮登录";
            LoginButton.Content = "微软登录";
            LogoutButton.Visibility = Visibility.Collapsed;
            AccountAvatarLetter.Text = "?";
            AccountAvatarBox.Child = AccountAvatarLetter;
            return;
        }

        var username = _session.Username ?? "玩家";
        AccountStatus.Text = username;
        var isMicrosoft = _settings.LastAccountType == "microsoft";
        AccountTypeText.Text = _settings.LastAccountType switch
        {
            "microsoft" => "微软账号",
            "authlib" => string.IsNullOrWhiteSpace(_settings.AuthlibServerName)
                ? "外置登录" : $"外置登录 · {_settings.AuthlibServerName}",
            _ => "离线账号"
        };
        LoginButton.Content = "切换";
        LogoutButton.Visibility = Visibility.Visible;
        AccountAvatarLetter.Text = username.Length > 0
            ? username[..1].ToUpper() : "?";
        AccountAvatarBox.Child = AccountAvatarLetter;
        _ = LoadAvatarAsync(_session);

        // 首页 / 皮肤页 / 账号页显示当前账号信息，登录状态变化后刷新
        if (CurrentPage is "首页" or "皮肤" or "账号")
            ShowPage(CurrentPage, true);
    }

    /// <summary>加载皮肤头像：正版按 UUID 取头部；离线账号优先用自定义皮肤裁脸，否则取默认皮肤头像（都不显示字母）。</summary>
    private async Task LoadAvatarAsync(MSession session)
    {
        try
        {
            // 外置登录：从认证服务器拿皮肤 URL 后裁脸
            if (_settings.LastAccountType == "authlib" && !string.IsNullOrEmpty(_settings.AuthlibApiRoot)
                && !string.IsNullOrEmpty(session.UUID))
            {
                var extUrl = await new Services.AuthlibService()
                    .GetSkinUrlAsync(_settings.AuthlibApiRoot, session.UUID);
                if (!string.IsNullOrEmpty(extUrl))
                {
                    var extBytes = await _skinHttp.GetByteArrayAsync(extUrl);
                    var extBmp = new BitmapImage();
                    extBmp.BeginInit();
                    extBmp.CacheOption = BitmapCacheOption.OnLoad;
                    extBmp.StreamSource = new MemoryStream(extBytes);
                    extBmp.EndInit();
                    extBmp.Freeze();
                    SetAccountAvatar(CreateFaceBitmap(extBmp));
                    return;
                }
            }

            // 离线账号：本启动器里给该玩家设置过自定义皮肤 → 直接裁本地 PNG 的脸部
            if (_settings.LastAccountType != "microsoft"
                && !string.IsNullOrEmpty(_settings.OfflineSkinPath)
                && File.Exists(_settings.OfflineSkinPath)
                && (string.IsNullOrWhiteSpace(_settings.OfflineSkinPlayer)
                    || _settings.OfflineSkinPlayer == session.Username))
            {
                var local = new BitmapImage();
                local.BeginInit();
                local.CacheOption = BitmapCacheOption.OnLoad;
                local.UriSource = new Uri(_settings.OfflineSkinPath);
                local.EndInit();
                local.Freeze();
                SetAccountAvatar(CreateFaceBitmap(local));
                return;
            }

            // 正版账号：先取真实皮肤裁脸（最准），失败再退回头像服务多源兜底
            if (_settings.LastAccountType == "microsoft" && !string.IsNullOrEmpty(session.UUID))
            {
                var skinUrl = await GetOfficialSkinUrlAsync(session.UUID, session.AccessToken);
                if (!string.IsNullOrEmpty(skinUrl))
                {
                    var skinBmp = await LoadRemoteBitmapAsync(skinUrl);
                    if (skinBmp != null) { SetAccountAvatar(CreateFaceBitmap(skinBmp)); return; }
                }
                foreach (var url in HeadImageUrls(session.UUID))
                {
                    var head = await LoadRemoteBitmapAsync(url);
                    if (head != null) { SetAccountAvatar(head); return; }   // 头像服务返回的就是脸，不再裁剪
                }
                return;
            }

            // 离线账号：头像服务按玩家名取（返回的已经是整张脸，绝不能再裁剪！），
            // 之前先裁 (8,8,8,8) 导致只剩一小块纯色 —— 这就是「头像不见了」的原因
            var id = session.Username;
            if (string.IsNullOrEmpty(id)) return;
            foreach (var url in new[]
            {
                $"https://mc-heads.net/avatar/{Uri.EscapeDataString(id)}/104",
                $"https://minotar.net/helm/{Uri.EscapeDataString(id)}/104.png"
            })
            {
                try
                {
                    var bytes = await _imgHttp.GetByteArrayAsync(url);
                    var head = new BitmapImage();
                    head.BeginInit();
                    head.CacheOption = BitmapCacheOption.OnLoad;
                    head.StreamSource = new MemoryStream(bytes);
                    head.EndInit();
                    head.Freeze();
                    SetAccountAvatar(head);
                    return;
                }
                catch { /* 换下一个源 */ }
            }

            // 全部网络源失败：用内置官方皮肤裁脸（本地资源，必定成功）
            var builtin = LoadBuiltinSkinSource();
            if (builtin != null) SetAccountAvatar(CreateFaceBitmap(builtin));
        }
        catch { /* 网络失败时保留首字母头像 */ }
    }

    /// <summary>加载启动器内置的默认皮肤（本地资源，不依赖网络）。</summary>
    private static System.Windows.Media.Imaging.BitmapImage? LoadBuiltinSkinSource()
    {
        try
        {
            var res = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/skins/steve-wide.png"));
            if (res == null) return null;
            var skin = new System.Windows.Media.Imaging.BitmapImage();
            skin.BeginInit();
            skin.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            skin.StreamSource = res.Stream;
            skin.EndInit();
            skin.Freeze();
            return skin;
        }
        catch { return null; }
    }

    /// <summary>把裁好的脸部图放进侧栏头像框（像素图最近邻采样保持锐利）。</summary>
    private void SetAccountAvatar(System.Windows.Media.Imaging.BitmapSource? face)
    {
        if (face == null) return;
        var avatar = new Image { Source = face, Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(avatar, BitmapScalingMode.NearestNeighbor);
        AccountAvatarBox.Child = avatar;
    }

    /// <summary>
    /// 取正版账号当前皮肤直链：优先官方会话服务器（公开接口、不需要令牌、令牌过期也能读），
    /// 再退到需要令牌的 minecraftservices API。
    /// </summary>
    private async Task<string?> GetOfficialSkinUrlAsync(string uuid, string? accessToken)
    {
        try
        {
            var json = await _skinHttp.GetStringAsync(
                $"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}");
            var url = ExtractSkinUrl(json);
            if (!string.IsNullOrEmpty(url)) return url;
        }
        catch { /* 会话服务器不可达，继续下一源 */ }

        if (!string.IsNullOrWhiteSpace(accessToken))
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    "https://api.minecraftservices.com/minecraft/profile");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                using var resp = await _skinHttp.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    if (doc.RootElement.TryGetProperty("skins", out var skins))
                    {
                        string? fallback = null;
                        foreach (var s in skins.EnumerateArray())
                        {
                            var state = s.TryGetProperty("state", out var st) ? st.GetString() : null;
                            var u = s.TryGetProperty("url", out var uu) ? uu.GetString() : null;
                            if (string.IsNullOrEmpty(u)) continue;
                            if (state == "ACTIVE") return u;
                            fallback ??= u;
                        }
                        if (fallback != null) return fallback;
                    }
                }
            }
            catch { }
        }
        return null;
    }

    /// <summary>从 profile JSON 的 properties.textures（base64）里取出 SKIN 地址。</summary>
    private static string? ExtractSkinUrl(string profileJson)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(profileJson);
            if (!doc.RootElement.TryGetProperty("properties", out var props)) return null;
            foreach (var p in props.EnumerateArray())
            {
                if (!p.TryGetProperty("name", out var n) || n.GetString() != "textures") continue;
                if (!p.TryGetProperty("value", out var v)) continue;
                var b64 = v.GetString();
                if (string.IsNullOrEmpty(b64)) continue;
                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                using var t = System.Text.Json.JsonDocument.Parse(decoded);
                if (t.RootElement.TryGetProperty("textures", out var tex)
                    && tex.TryGetProperty("SKIN", out var skin)
                    && skin.TryGetProperty("url", out var u))
                    return u.GetString();
            }
        }
        catch { }
        return null;
    }

    /// <summary>下载并解码远程图片；失败返回 null。</summary>
    private static async Task<BitmapImage?> LoadRemoteBitmapAsync(string url)
    {
        try
        {
            var bytes = await _skinHttp.GetByteArrayAsync(url);
            if (bytes.Length < 100) return null;
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>正版头像多源兜底：国内可达性从高到低排列，返回的图直接就是脸部。</summary>
    private static IEnumerable<string> HeadImageUrls(string uuid)
    {
        var u = Uri.EscapeDataString(uuid);
        yield return $"https://crafatar.com/avatars/{u}?size=104&overlay";
        yield return $"https://mc-heads.net/avatar/{u}/104";
        yield return $"https://minotar.net/helm/{u}/104.png";
    }
    #endregion

    #region 导航
    private void Navigate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Content: string page }) ShowPage(page);
    }

    // 页面缓存：资源下载与皮肤两页最重（网络列表 + 大量图片），切回时直接复用，避免重建与重新请求
    private readonly Dictionary<string, UIElement> _pageCache = new();
    private static bool IsCacheablePage(string page) => page is "资源下载" or "皮肤";

    private void ShowPage(string page, bool forceRebuild = false)
    {
        CurrentPage = page;

        // 新版界面（WebView2）接管首页。
        // 切到原生层必须由用户在新版界面里主动点导航（_webNavRequested）触发；
        // 后台程序化刷新（登录恢复/状态变化等）绝不把用户拽回旧界面。
        if (_webShellReady && !_suppressShellSwitch)
        {
            if (page == "首页")
            {
                ShowWebShell();
                _webNavRequested = false;
                HighlightNav(page);
                return;
            }
            if (_webNavRequested)
            {
                ShowNativeShell();
            }
            _webNavRequested = false;
        }

        // 启动提速：新版界面还没失败时，首页不再构建整套旧版原生页面（纯浪费的几秒）；
        // 真失败需要回退时由 InitWebShell 重建。
        if (!_webShellFailed && page == "首页" && !_suppressShellSwitch)
        {
            HighlightNav(page);
            return;
        }

        HighlightNav(page);

        if (forceRebuild) _pageCache.Remove(page);

        UIElement content;
        if (!forceRebuild && IsCacheablePage(page) && _pageCache.TryGetValue(page, out var cached))
        {
            content = cached;
        }
        else
        {
            content = page switch
            {
                "首页" => BuildHomePage(),
                "游戏版本" => BuildVersionsPage(),
                "版本设置" => BuildVersionSettingsPage(),
                "资源下载" => BuildDownloadPage(_catalogProjectType),
                "模组管理" => BuildModsPage(),
                "皮肤" => BuildSkinsPage(),
                "账号" => BuildAccountsPage(),
                "联机" => BuildMultiplayerPage(),
                "基岩版" => BuildBedrockPage(),
                "工具" => BuildToolsPage(),
                "百宝箱" => BuildTreasurePage(),
                "Java" => BuildJavaPage(),
                "安全防护" => BuildSecurityPage(),
                "设置" => BuildSettingsPage(),
                "个性化" => BuildPersonalizePage(),
                _ => BuildHomePage()
            };
            if (IsCacheablePage(page)) _pageCache[page] = content;
        }

        if (ReferenceEquals(PageHost.Content, content)) return;
        PageHost.Content = content;

        // 轻量淡入：去掉位移，切换更跟手
        PageHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    private void HighlightNav(string page)
    {
        var navs = new[] { NavHome, NavVersions, NavMods, NavModsManage, NavSkins, NavAccounts, NavMulti, NavBedrock, NavTools, NavTreasure, NavJava, NavSecurity, NavSettings, NavPersonalize };
        foreach (var b in navs)
        {
            var active = (string)b.Content == page;
            b.Foreground = active ? Brush(UiPalette.Accent) : Brush(UiPalette.Muted);
            b.Background = active ? Brush("#26" + UiPalette.Accent.TrimStart('#')) : System.Windows.Media.Brushes.Transparent;
            b.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            b.BorderBrush = active ? Brush(UiPalette.Accent) : null;
            b.BorderThickness = active ? new Thickness(3, 0, 0, 0) : new Thickness(0);
        }
    }
    #endregion

    #region 首页
    private UIElement BuildHomePage()
    {
        var root = new StackPanel();
        root.Children.Add(Label("NEX LAUNCHER", 12, UiPalette.Accent));
        root.Children.Add(Heading(_session?.Username != null ? $"欢迎回来，{_session.Username}" : "欢迎回来"));
        root.Children.Add(Body("选择版本，一键开始你的冒险。"));

        // 快速启动：版本选择 + 立即启动（首页主操作）
        root.Children.Add(BuildQuickLaunchCard());

        // 账号与游戏目录：收成一行
        var hero = Card();
        hero.Margin = new Thickness(0, 4, 0, 0);
        var heroRow = new DockPanel { Margin = new Thickness(16, 12, 16, 12) };
        var openDirBtn = new Button
        {
            Content = "打开游戏目录",
            FontSize = 12,
            Padding = new Thickness(12, 7, 12, 7),
            Background = Brush(UiPalette.SurfaceAlt),
            Foreground = Brush(UiPalette.TextDim),
            VerticalAlignment = VerticalAlignment.Center
        };
        openDirBtn.Click += (_, _) => OpenGameDirectory();
        DockPanel.SetDock(openDirBtn, Dock.Right);
        heroRow.Children.Add(openDirBtn);

        var accInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        accInfo.Children.Add(Label("当前账号", 11, UiPalette.Muted));
        accInfo.Children.Add(new TextBlock
        {
            Text = _session != null
                ? $"{_session.Username} · {(_settings.LastAccountType == "microsoft" ? "微软正版" : "离线")}"
                : "未登录，可在侧栏上方或「账号」页登录",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text),
            Margin = new Thickness(0, 2, 0, 0)
        });
        heroRow.Children.Add(accInfo);
        hero.Children.Add(heroRow);
        root.Children.Add(hero);

        // 游戏时长（有记录才显示，一行即可）
        var (totalSeconds, totalLaunches) = PlaytimeStatsService.Instance.GetTotals();
        if (totalLaunches > 0)
        {
            var stats = Card();
            stats.Margin = new Thickness(0, 8, 0, 0);
            stats.Children.Add(new TextBlock
            {
                Text = $"已游玩 {PlaytimeStatsService.FormatDuration(totalSeconds)} · 共启动 {totalLaunches} 次",
                FontSize = 12,
                Foreground = Brush(UiPalette.MutedLight),
                Margin = new Thickness(16, 10, 16, 10)
            });
            root.Children.Add(stats);
        }

        // 功能入口（可点击导航）
        var grid = new UniformGrid { Columns = 2, Margin = new Thickness(0, 18, 0, 0) };
        grid.Children.Add(CardLink("游戏版本", "下载、导入与管理版本", "\uE7FC", "游戏版本"));
        grid.Children.Add(CardLink("资源下载", "模组 · 整合包 · 资源包 · 光影 · 数据包", "\uE74C", "资源下载"));
        grid.Children.Add(CardLink("皮肤", "换上喜欢的皮肤", "\uE771", "皮肤"));
        grid.Children.Add(CardLink("联机", "一键开服，多人游戏", "\uE968", "联机"));
        root.Children.Add(grid);
        return root;
    }

    /// <summary>首页功能入口卡片：整卡可点击，直接跳转到对应页面。</summary>
    private UIElement CardLink(string title, string detail, string icon, string page)
    {
        var btn = new Button
        {
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(16, 14, 16, 14),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brush(UiPalette.Surface),
            BorderBrush = Brush(UiPalette.Border),
            BorderThickness = new Thickness(1)
        };

        var row = new DockPanel { LastChildFill = true };
        var mark = new TextBlock
        {
            Text = icon,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 18,
            Foreground = Brush(UiPalette.Accent),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14, 0, 0, 0)
        };
        DockPanel.SetDock(mark, Dock.Right);
        row.Children.Add(mark);

        var t = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        t.Children.Add(Label(title, 14, UiPalette.Text, FontWeights.SemiBold));
        t.Children.Add(Body(detail));
        row.Children.Add(t);

        btn.Content = row;
        btn.Click += (_, _) => ShowPage(page);
        return btn;
    }
    #endregion

    #region 版本页
#if PUBLIC_EDITION
    /// <summary>公共版「游戏版本」页：不提供在线下载，只支持导入本机已有版本游玩。</summary>
    private UIElement BuildVersionsPage()
    {
        var root = new StackPanel();
        root.Children.Add(Label("游戏", 11, UiPalette.Accent));

        var headRow = new DockPanel();
        var importBtn = new Button
        {
            Content = "+ 导入本机版本",
            Padding = new Thickness(16, 9, 16, 9),
            Background = Brush(UiPalette.Accent),
            Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 4),
            ToolTip = "扫描本机其它 .minecraft 目录（官方启动器 / PCL2 / HMCL 等），把已下载的版本导入当前启动器"
        };
        importBtn.Click += (_, _) => ImportLocalVersions_Click();
        DockPanel.SetDock(importBtn, Dock.Right);
        headRow.Children.Add(importBtn);

        var modpackBtn = ModpackButton();
        DockPanel.SetDock(modpackBtn, Dock.Right);
        headRow.Children.Add(modpackBtn);

        headRow.Children.Add(Heading("游戏版本"));
        root.Children.Add(headRow);

        root.Children.Add(Body("本版本不提供在线下载游戏。点击右上角「导入本机版本」，把官方启动器 / PCL2 / HMCL 等已经下载好的版本一键导入即可游玩；导入时会自动识别 Fabric / Forge / NeoForge / Quilt / OptiFine。"));

        // 悲报公告（公共版专用）
        var notice = Card();
        notice.Margin = new Thickness(0, 12, 0, 4);
        notice.Children.Add(new TextBlock
        {
            Text = "悲报：",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = Brush("#F87171"),
            Margin = new Thickness(16, 14, 16, 2)
        });
        notice.Children.Add(new TextBlock
        {
            Text = "由于法律原因，NEX Launcher 启动器停止下载游戏版本功能，对此表示非常抱歉，后续消息请听从正规渠道，不信谣，不传谣。",
            FontSize = 13,
            Foreground = Brush(UiPalette.TextDim),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22,
            Margin = new Thickness(16, 0, 16, 8)
        });
        notice.Children.Add(new TextBlock
        {
            Text = "—— NEX Launcher 启动器　NEX 工作室",
            FontSize = 12,
            Foreground = Brush(UiPalette.Muted),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 14)
        });
        root.Children.Add(notice);

        // 搜索框：过滤本地版本
        var boxHost = new Border
        {
            Background = Brush(UiPalette.Bg),
            BorderBrush = Brush(UiPalette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Height = 36,
            Margin = new Thickness(0, 12, 0, 10)
        };
        var boxGrid = new Grid();
        boxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        boxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        boxGrid.Children.Add(new TextBlock
        {
            Text = "\uE721",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var searchBox = new TextBox
        {
            Text = _versionQuery,
            FontSize = 13,
            Background = Brushes.Transparent,
            Foreground = Brush(UiPalette.Text),
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0)
        };
        searchBox.TextChanged += (_, _) =>
        {
            _versionQuery = searchBox.Text.Trim();
            _versionShown = 80;
            RefreshVersionList();
        };
        Grid.SetColumn(searchBox, 1);
        boxGrid.Children.Add(searchBox);
        boxHost.Child = boxGrid;
        root.Children.Add(boxHost);

        _versionStatus = new TextBlock { Foreground = Brush(UiPalette.Muted), FontSize = 12, Margin = new Thickness(2, 0, 0, 8) };
        root.Children.Add(_versionStatus);
        _versionListPanel = new StackPanel();
        root.Children.Add(_versionListPanel);

        RefreshVersionList();
        return root;
    }
#else
    private UIElement BuildVersionsPage()
    {
        var root = new StackPanel();
        root.Children.Add(Label("下载", 11, UiPalette.Accent));

        var headRow = new DockPanel();
        var importBtn = new Button
        {
            Content = "导入本机版本",
            Padding = new Thickness(14, 8, 14, 8),
            Background = Brush(UiPalette.SurfaceAlt),
            Foreground = Brush(UiPalette.Accent),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 4),
            ToolTip = "扫描本机其它 .minecraft 目录，把已下载的版本导入当前启动器"
        };
        importBtn.Click += (_, _) => ImportLocalVersions_Click();
        DockPanel.SetDock(importBtn, Dock.Right);
        headRow.Children.Add(importBtn);

        var modpackBtn = ModpackButton();
        DockPanel.SetDock(modpackBtn, Dock.Right);
        headRow.Children.Add(modpackBtn);

        headRow.Children.Add(Heading("游戏版本"));
        root.Children.Add(headRow);

        root.Children.Add(Body("按类型筛选并下载 Minecraft，已安装的版本可直接启动或删除；也可以用右上角「导入本机版本」把其它启动器里已下载的版本搬过来。"));

        // ---- 搜索卡（PCL2 规格：输入即筛选）----
        var searchCard = Card();
        searchCard.Margin = new Thickness(0, 12, 0, 10);
        var searchInner = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        searchCard.Children.Add(searchInner);

        var searchRow = new Grid();
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var boxHost = new Border
        {
            Background = Brush(UiPalette.Bg),
            BorderBrush = Brush(UiPalette.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Height = 36
        };
        var boxGrid = new Grid();
        boxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        boxGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        boxGrid.Children.Add(new TextBlock
        {
            Text = "\uE721",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(12, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var searchBox = new TextBox
        {
            Text = _versionQuery,
            FontSize = 13,
            Background = Brushes.Transparent,
            Foreground = Brush(UiPalette.Text),
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0)
        };
        searchBox.TextChanged += (_, _) =>
        {
            _versionQuery = searchBox.Text.Trim();
            _versionShown = 80;
            RefreshVersionList();
        };
        Grid.SetColumn(searchBox, 1);
        boxGrid.Children.Add(searchBox);
        boxHost.Child = boxGrid;
        searchRow.Children.Add(boxHost);

        var clearBtn = new Button
        {
            Content = "清除",
            Width = 76,
            Height = 36,
            Margin = new Thickness(10, 0, 0, 0),
            Background = Brush(UiPalette.SurfaceAlt),
            Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0),
            FontSize = 12,
            Cursor = Cursors.Hand
        };
        clearBtn.Click += (_, _) =>
        {
            searchBox.Text = "";
            _versionQuery = "";
            _versionFilter = "全部";
            _versionShown = 80;
            _searchChipsPanel?.Children.Clear();
            if (_searchChipsPanel != null) FillVersionChips(_searchChipsPanel);
            RefreshVersionList();
        };
        Grid.SetColumn(clearBtn, 1);
        searchRow.Children.Add(clearBtn);
        searchInner.Children.Add(searchRow);

        // 类型筛选标签
        _searchChipsPanel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        FillVersionChips(_searchChipsPanel);
        searchInner.Children.Add(_searchChipsPanel);
        root.Children.Add(searchCard);

        _versionStatus = new TextBlock { Foreground = Brush(UiPalette.Muted), FontSize = 12, Margin = new Thickness(2, 0, 0, 8) };
        root.Children.Add(_versionStatus);
        _versionListPanel = new StackPanel();
        root.Children.Add(_versionListPanel);

        _ = LoadVersionsAsync();
        return root;
    }
#endif

    private WrapPanel? _searchChipsPanel;
    private string _versionQuery = "";

    private void FillVersionChips(WrapPanel panel)
    {
        foreach (var c in new[] { "全部", "已安装", "正式版", "预览版", "愚人节版", "远古版" })
        {
            var active = c == _versionFilter;
            var chip = new Button
            {
                Content = c,
                Tag = c,
                Padding = new Thickness(13, 5, 13, 5),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                FontSize = 12,
                Background = active ? Brush(UiPalette.Accent) : Brush(UiPalette.SurfaceAlt),
                Foreground = active ? Brush(UiPalette.Bg) : Brush(UiPalette.TextDim),
                BorderThickness = new Thickness(0)
            };
            chip.Click += (_, _) =>
            {
                _versionFilter = c;
                _versionShown = 80;
                foreach (var child in panel.Children)
                {
                    if (child is not Button b) continue;
                    var on = (string)b.Tag == _versionFilter;
                    b.Background = on ? Brush(UiPalette.Accent) : Brush(UiPalette.SurfaceAlt);
                    b.Foreground = on ? Brush(UiPalette.Bg) : Brush(UiPalette.TextDim);
                }
                RefreshVersionList();
            };
            panel.Children.Add(chip);
        }
    }


    #region 快速启动
    private UIElement BuildQuickLaunchCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 14, 0, 12);
        card.Children.Add(Label("快速启动", 11, UiPalette.Muted));

        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var launchBtn = new Button
        {
            Content = "立即启动",
            Padding = new Thickness(18, 8, 18, 8),
            Background = Brush(UiPalette.Accent),
            Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            FontSize = 12
        };
        launchBtn.Click += async (_, _) => await QuickLaunch_Click(launchBtn);
        DockPanel.SetDock(launchBtn, Dock.Right);
        row.Children.Add(launchBtn);

        _quickVersionBox = new ComboBox
        {
            FontSize = 13,
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 0, 10, 0),
            Background = Brush(UiPalette.Bg),
            Foreground = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border)
        };
        _quickVersionBox.SelectionChanged += (_, _) =>
        {
            if (_quickVersionBox.SelectedItem is string v)
            {
                _settings.LastQuickVersion = v;
                _settings.Save();
            }
        };
        row.Children.Add(_quickVersionBox);
        card.Children.Add(row);
        card.Children.Add(Body($"已登录时使用当前账号；未登录时自动以离线账号 \"{_settings.QuickOfflineName}\" 启动。"));
        RefreshQuickVersionList();
        return card;
    }

    /// <summary>合并"远程列表中已安装"与"本地 versions 目录扫描"的版本 ID，供主页/侧栏使用。</summary>
    private List<string> GetInstalledVersionIds()
    {
        // 本地扫描不依赖网络，启动即可用；远程列表加载后按其顺序排在前面
        var local = _minecraftService.GetLocalInstalledVersions();
        var localSet = new HashSet<string>(local);
        var result = _allVersions.Where(v => v.IsInstalled && localSet.Contains(v.Id)).Select(v => v.Id).ToList();
        foreach (var id in local)
            if (!result.Contains(id)) result.Add(id);
        return result;
    }

    private void RefreshQuickVersionList()
    {
        var installed = GetInstalledVersionIds();
        var selected = installed.Contains(_settings.LastQuickVersion)
            ? _settings.LastQuickVersion
            : installed.FirstOrDefault();
        if (_quickVersionBox != null)
        {
            _quickVersionBox.ItemsSource = installed;
            _quickVersionBox.SelectedItem = selected;
            _quickVersionBox.IsEnabled = installed.Count > 0;
        }
        // 侧栏底部版本选择同步
        SideVersionBox.ItemsSource = installed;
        SideVersionBox.SelectedItem = selected;
        SideVersionBox.IsEnabled = installed.Count > 0;
        UpdateSideLoaderText(selected);
    }

    /// <summary>侧栏左下角显示所选版本的加载器类型（原版 / Forge / Fabric 等）。</summary>
    private void UpdateSideLoaderText(string? versionId)
    {
        if (SideLoaderText == null) return;
        if (string.IsNullOrEmpty(versionId))
        {
            SideLoaderText.Text = "";
            return;
        }

        var loader = DetectLoaderType(versionId);
        var baseGame = versionId;
        // 从 "1.20.1-forge-47.2.0" 里提取纯游戏版本号展示
        var m = System.Text.RegularExpressions.Regex.Match(versionId, @"^(\d+\.\d+(\.\d+)?)");
        if (m.Success) baseGame = m.Groups[1].Value;

        SideLoaderText.Text = loader == "原版"
            ? $"{baseGame} · 原版"
            : $"{baseGame} · {loader}";
    }

    /// <summary>识别版本的加载器类型：优先读版本 JSON，其次按 ID 关键词。</summary>
    private string DetectLoaderType(string versionId)
    {
        var id = versionId.ToLowerInvariant();
        if (id.Contains("neoforge")) return "NeoForge";
        if (id.Contains("forge")) return "Forge";
        if (id.Contains("fabric")) return "Fabric";
        if (id.Contains("quilt")) return "Quilt";
        if (id.Contains("optifine")) return "OptiFine";
        if (id.Contains("liteloader")) return "LiteLoader";

        // 读版本 JSON 再确认一遍（有些整合包版本名不带关键词）
        try
        {
            var jsonPath = System.IO.Path.Combine(_gameDirectory, "versions", versionId, $"{versionId}.json");
            if (File.Exists(jsonPath))
            {
                var content = File.ReadAllText(jsonPath).ToLowerInvariant();
                if (content.Contains("neoforge")) return "NeoForge";
                if (content.Contains("forge")) return "Forge";
                if (content.Contains("fabricloader")) return "Fabric";
                if (content.Contains("quilt")) return "Quilt";
            }
        }
        catch { /* 读取失败按原版处理 */ }

        return "原版";
    }

    private async Task QuickLaunch_Click(Button btn)
    {
        if (_quickVersionBox?.SelectedItem is not string versionId)
        {
            MessageBox.Show("没有已安装的版本，请先在下方下载一个版本。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await LaunchVersionAsync(versionId, btn, "立即启动");
    }

    /// <summary>侧栏底部“启动游戏”按钮。</summary>
    private async void SideLaunch_Click(object sender, RoutedEventArgs e)
    {
        if (SideVersionBox.SelectedItem is not string versionId)
        {
            MessageBox.Show("没有已安装的版本，请先到“游戏版本”页下载一个版本。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await LaunchVersionAsync(versionId, SideLaunchButton, "▶  启动游戏");
    }

    /// <summary>统一的启动流程：未登录自动离线会话，启动失败弹错，进程由服务跟踪。</summary>
    private async Task LaunchVersionAsync(string versionId, Button btn, string idleText)
    {
        _session ??= MinecraftService.CreateOfflineSession(_settings.QuickOfflineName);
        UpdateAccountUi();
        btn.IsEnabled = false;
        btn.Content = "启动中...";
        try
        {
            await _minecraftService.StartGameAsync(versionId, _session, _settings);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"启动失败：\n{ex.Message}", "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content = idleText;
        }
    }

    /// <summary>强制结束游戏（右下角电源按钮）。</summary>
    private void KillGame_Click(object sender, RoutedEventArgs e)
    {
        var count = _minecraftService.ProcessCount;
        if (count == 0) { KillGameButton.Visibility = Visibility.Collapsed; return; }
        var result = MessageBox.Show(
            $"确定要强制结束正在运行的游戏吗？（共 {count} 个进程）\n未保存的游戏进度可能丢失！",
            "强制结束游戏", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes) return;
        var killed = _minecraftService.KillAllGames();
        KillGameButton.Visibility = Visibility.Collapsed;
        SideLaunchButton.Content = "▶  启动游戏";
        MessageBox.Show($"已结束 {killed} 个游戏进程。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    #endregion

    private async Task LoadVersionsAsync()
    {
        // 已有缓存则直接显示，不重复请求
        if (_allVersions.Count > 0)
        {
            if (_versionStatus != null) _versionStatus.Text = $"共 {_allVersions.Count} 个版本";
            RefreshVersionList();
            RefreshQuickVersionList();
            return;
        }
        try
        {
            if (_versionStatus != null) _versionStatus.Text = "正在加载版本列表...";
            _allVersions = (await _minecraftService.GetVersionsAsync()).ToList();
            if (_versionStatus != null) _versionStatus.Text = $"共 {_allVersions.Count} 个版本";
            RefreshVersionList();
            RefreshQuickVersionList();
        }
        catch (Exception ex)
        {
            if (_versionStatus != null) _versionStatus.Text = "加载失败：" + ex.Message;
        }
    }

#if PUBLIC_EDITION
    private void RefreshVersionList()
    {
        if (_versionListPanel == null) return;
        _versionListPanel.Children.Clear();

        // 公共版：只显示本地已安装/导入的版本
        var localIds = _minecraftService.GetLocalInstalledVersions()
            .Select(id => _allVersions.FirstOrDefault(v => v.Id == id)
                ?? new MinecraftVersionItem(id, "local", "已安装", "", true))
            .ToList();
        var q = localIds;
        if (!string.IsNullOrEmpty(_versionQuery))
            q = q.Where(v => v.Id.Contains(_versionQuery, StringComparison.OrdinalIgnoreCase)).ToList();

        if (_versionStatus != null)
            _versionStatus.Text = $"已导入 {localIds.Count} 个版本" +
                (string.IsNullOrEmpty(_versionQuery) ? "" : $" · 匹配“{_versionQuery}”");

        if (q.Count == 0)
        {
            _versionListPanel.Children.Add(BuildEmptyVersionHint(true));
            return;
        }
        foreach (var v in q)
            _versionListPanel.Children.Add(BuildVersionRow(v));
    }
#else
    private void RefreshVersionList()
    {
        if (_versionListPanel == null) return;
        _versionListPanel.Children.Clear();

        List<MinecraftVersionItem> q;
        if (_versionFilter == "已安装")
        {
            // 只显示本地已安装的版本（含加载器版本）
            q = _minecraftService.GetLocalInstalledVersions()
                .Select(id => _allVersions.FirstOrDefault(v => v.Id == id)
                    ?? new MinecraftVersionItem(id, "local", "已安装", "", true))
                .ToList();
        }
        else
        {
            q = (_versionFilter == "全部"
                ? _allVersions
                : _allVersions.Where(v => v.Category == _versionFilter)).ToList();

            // 本地扫描补漏：加载器版本（如 1.20.1-fabric）不在远程列表里，补到顶部显示
            var remoteIds = new HashSet<string>(_allVersions.Select(v => v.Id), StringComparer.OrdinalIgnoreCase);
            var localOnly = _minecraftService.GetLocalInstalledVersions()
                .Where(id => !remoteIds.Contains(id))
                .Select(id => new MinecraftVersionItem(id, "local", "已安装", "", true))
                .ToList();
            if (localOnly.Count > 0 && _versionFilter == "全部")
                q = localOnly.Concat(q).ToList();
        }

        // 搜索框：按版本号过滤（PCL2 的版本搜索也是即时过滤）
        if (!string.IsNullOrEmpty(_versionQuery))
            q = q.Where(v => v.Id.Contains(_versionQuery, StringComparison.OrdinalIgnoreCase)).ToList();

        _versionShown = Math.Min(Math.Max(_versionShown, 80), q.Count);
        if (q.Count == 0)
        {
            _versionListPanel.Children.Add(BuildEmptyVersionHint(_versionFilter == "已安装"));
            return;
        }

        if (_versionStatus != null)
            _versionStatus.Text = $"共 {q.Count} 个版本" + (string.IsNullOrEmpty(_versionQuery) ? "" : $" · 匹配“{_versionQuery}”");

        foreach (var v in q.Take(_versionShown))
            _versionListPanel.Children.Add(BuildVersionRow(v));
        if (_versionShown < q.Count)
        {
            var more = new Button
            {
                Content = $"加载更多（剩余 {q.Count - _versionShown} 个）",
                Padding = new Thickness(16, 9, 16, 9),
                Margin = new Thickness(0, 8, 0, 0),
                Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            more.Click += (_, _) => { _versionShown += 80; RefreshVersionList(); };
            _versionListPanel.Children.Add(more);
        }
    }
#endif

    /// <summary>版本类型标签（正式版 / 快照 / 愚人节 / 远古 / 已安装），PCL2 配色。</summary>
    private static Border VersionTypeBadge(string category, bool installed)
    {
        var (bg, fg, text) = category switch
        {
            "正式版" => ("#1E3A5F", "#7FB6FF", "正式版"),
            "预览版" => ("#173D2B", "#5FE09B", "快照"),
            "愚人节版" => ("#33204D", "#C9A2FF", "愚人节"),
            "远古版" => ("#2A2F36", "#9AA4B2", "远古"),
            "已安装" or "local" => ("#123A34", "#4EE0BE", "已安装"),
            _ => ("#2A2F36", "#9AA4B2", category),
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new Border
        {
            Background = Brush(bg),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 5, 0),
            Child = new TextBlock
            {
                Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = Brush(fg)
            }
        });
        if (installed && text != "已安装")
            panel.Children.Add(new Border
            {
                Background = Brush("#123A34"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 5, 0),
                Child = new TextBlock
                {
                    Text = "\uE73E 已安装", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = Brush("#4EE0BE")
                }
            });

        // 返回外层容器（内部只有一个或多个标签，靠 BadgeHost 布局）
        return new Border { Background = Brushes.Transparent, Child = panel };
    }

    /// <summary>版本列表为空时的提示卡片；"已安装"筛选下额外提供导入入口。</summary>
    private UIElement BuildEmptyVersionHint(bool offerImport)
    {
        var card = Card();
        card.Children.Add(new TextBlock
        {
            Text = offerImport ? "还没有安装任何游戏版本" : "这个分类下没有可显示的版本",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text)
        });

        if (!offerImport) return card;

        card.Children.Add(Body(
#if PUBLIC_EDITION
            "把本机其它 .minecraft（官方启动器 / PCL2 / HMCL 等）里已经下载好的版本直接导入，即可游玩；无需联网下载。"
#else
            "可以下载新版本，也可以把本机其它 .minecraft（官方启动器 / PCL2 / HMCL 等）里已经下载好的版本直接导入，无需重新下载。"
#endif
        ));
        var btn = new Button
        {
            Content = "导入本机已有版本",
            Padding = new Thickness(14, 8, 14, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brush(UiPalette.Accent),
            Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand,
            FontSize = 12
        };
        btn.Click += (_, _) => ImportLocalVersions_Click();
        card.Children.Add(btn);
        return card;
    }

    /// <summary>打开"导入本机版本"对话框；导入成功后刷新所有版本展示位置。</summary>
    private void ImportLocalVersions_Click()
    {
        var dlg = new ImportVersionDialog(_gameDirectory) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        // 导入的版本可能本来就在远程版本列表中，需要重算 IsInstalled，否则仍显示"下载"按钮
        var localSet = new HashSet<string>(_minecraftService.GetLocalInstalledVersions(), StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < _allVersions.Count; i++)
            _allVersions[i] = _allVersions[i] with { IsInstalled = localSet.Contains(_allVersions[i].Id) };

        RefreshVersionList();
        RefreshQuickVersionList();

        var msg = $"已成功导入 {dlg.ImportedCount} 个游戏版本，可在「已安装」中查看。";
        if (dlg.Errors.Count > 0)
            msg += "\n\n需要注意：\n" + string.Join("\n", dlg.Errors.Take(8));
        msg += "\n\n提示：未同时复制 libraries / assets 的版本，首次启动会自动联网补全依赖。";
        MessageBox.Show(this, msg, "导入完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>版本行（PCL2 规格）：版本号 + 类型标签 + 发布日期 + 右侧操作按钮。</summary>
    private UIElement BuildVersionRow(MinecraftVersionItem v)
    {
        var border = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Brush(UiPalette.Border),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 9, 10, 9)
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal };
        nameRow.Children.Add(new TextBlock
        {
            Text = v.Id,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = v.IsInstalled ? Brush(UiPalette.Accent) : Brush(UiPalette.Text),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        var badge = VersionTypeBadge(v.Category, v.IsInstalled);
        badge.Margin = new Thickness(8, 0, 0, 0);
        badge.VerticalAlignment = VerticalAlignment.Center;
        nameRow.Children.Add(badge);
        left.Children.Add(nameRow);
        left.Children.Add(new TextBlock
        {
            Text = string.IsNullOrEmpty(v.ReleaseDate) ? "本地已安装版本" : $"发布于 {v.ReleaseDate}",
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 2, 0, 0)
        });
        grid.Children.Add(left);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (v.IsInstalled)
        {
            var settingsBtn = new Button
            {
                Content = "设置",
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brush(UiPalette.SurfaceAlt),
                Foreground = Brush(UiPalette.TextDim),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 12,
                ToolTip = "版本设置：自定义图标 / 名称 / 内存 / Java / 导出启动脚本"
            };
            settingsBtn.Click += (_, _) =>
            {
                _versionSettingsTarget = v.Id;
                ShowPage("版本设置", true);
            };
            actions.Children.Add(settingsBtn);

            var delBtn = new Button
            {
                Content = "删除",
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Background = new SolidColorBrush(Color.FromArgb(0x30, 0xE0, 0x56, 0x56)),
                Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7B, 0x7B)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 12
            };
            delBtn.Click += (_, _) => DeleteGameVersion(v);
            actions.Children.Add(delBtn);

            var runBtn = new Button
            {
                Content = "启动",
                Padding = new Thickness(18, 6, 18, 6),
                Background = Brush(UiPalette.SurfaceAlt),
                Foreground = Brush(UiPalette.Accent),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
            runBtn.Click += async (_, _) => await OnVersionAction(v, runBtn);
            actions.Children.Add(runBtn);
        }
        else
        {
            var dlBtn = new Button
            {
                Content = "下载",
                Padding = new Thickness(18, 6, 18, 6),
                Background = Brush(UiPalette.Accent),
                Foreground = Brush(UiPalette.Bg),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold
            };
            dlBtn.Click += async (_, _) => await OnVersionAction(v, dlBtn);
            actions.Children.Add(dlBtn);
        }

        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        border.Child = grid;
        border.MouseEnter += (_, _) => border.Background = Brush(UiPalette.SurfaceAlt);
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        return border;
    }

    /// <summary>删除本地已安装的游戏版本（带确认），并刷新各处版本列表。</summary>
    private void DeleteGameVersion(MinecraftVersionItem v)
    {
        var sizeText = "";
        try
        {
            var dir = System.IO.Path.Combine(_gameDirectory, "versions", v.Id);
            if (Directory.Exists(dir))
            {
                long size = 0;
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    try { size += new FileInfo(f).Length; } catch { }
                }
                if (size >= 1 << 30) sizeText = $"（约 {size / (1024.0 * 1024 * 1024):0.#} GB）";
                else if (size >= 1 << 20) sizeText = $"（约 {size / (1024.0 * 1024):0} MB）";
            }
        }
        catch { }

        var confirm = MessageBox.Show(
            $"确定要删除游戏版本 \"{v.Id}\" 吗？{sizeText}\n\n将删除 .minecraft\\versions\\{v.Id} 整个文件夹，此操作不可恢复。",
            "删除游戏版本",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var deleted = _minecraftService.DeleteLocalVersion(v.Id);
            if (deleted == null)
            {
                MessageBox.Show("未找到该版本的文件夹，可能已被删除。", "NEX",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                // 更新内存列表并刷新所有展示位置
                var idx = _allVersions.FindIndex(x => x.Id == v.Id);
                if (idx >= 0) _allVersions[idx] = v with { IsInstalled = false };
                RefreshVersionList();
                RefreshQuickVersionList();
                MessageBox.Show($"已删除版本 {v.Id}。", "NEX",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (System.IO.IOException)
        {
            MessageBox.Show("删除失败：该版本可能正在被游戏或杀毒软件占用，请关闭相关程序后重试。", "NEX",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除失败：{ex.Message}", "NEX",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task OnVersionAction(MinecraftVersionItem v, Button btn)
    {
        if (_session == null)
        {
            MessageBox.Show("请先登录（微软或离线账号）。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        btn.IsEnabled = false;
        DownloadItem? dlItem = null;
        try
        {
#if !PUBLIC_EDITION
            if (!v.IsInstalled)
            {
                // 弹出模组加载器选择
                var dlg = new ModloaderDialog(v.Id) { Owner = this };
                if (dlg.ShowDialog() != true) { btn.IsEnabled = true; return; }

                btn.Content = "准备文件...";
                string versionToInstall = v.Id;
                if (dlg.SelectedLoaderId == "forge")
                {
                    // Forge：下载官方 installer 并用本地 Java 安装
                    btn.Content = "正在安装 Forge...";
                    versionToInstall = await new Services.ModloaderService()
                        .InstallForgeAsync(_gameDirectory, v.Id, dlg.SelectedLoaderVersion);
                }
                else if (dlg.SelectedLoaderId != "vanilla")
                {
                    versionToInstall = await new Services.ModloaderService()
                        .InstallLoaderProfileAsync(_gameDirectory, v.Id, dlg.SelectedLoaderId, dlg.SelectedLoaderVersion);
                }
                btn.Content = "下载中...";
                // 注册到右下角下载面板（游戏版本组）
                dlItem = DownloadManagerService.Instance.Add(
                    $"Minecraft {versionToInstall}", DownloadKind.Game);
                dlItem.Status = DownloadStatus.Downloading;
                _activeGameDownload = dlItem;
                try
                {
                    await _minecraftService.InstallAsync(versionToInstall);
                    DownloadManagerService.Instance.Complete(dlItem);
                }
                finally
                {
                    _activeGameDownload = null;
                }
                // 更新列表
                var idx = _allVersions.FindIndex(x => x.Id == v.Id);
                if (idx >= 0) _allVersions[idx] = v with { IsInstalled = true };
                btn.Content = "启动";
                RefreshVersionList();
                RefreshQuickVersionList();
            }
            else
#endif
            {
                btn.Content = "启动中...";
                await _minecraftService.StartGameAsync(v.Id, _session, _settings);
                btn.Content = "启动";
            }
        }
        catch (Exception ex)
        {
            if (dlItem != null) DownloadManagerService.Instance.Fail(dlItem, ex.Message);
            btn.Content = "重试";
            MessageBox.Show($"操作失败：\n{ex.Message}", "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { btn.IsEnabled = true; }
    }
    #endregion

    #region 内容目录
    /// <summary>资源下载页的类型页签：模组 / 整合包 / 资源包 / 光影包 / 数据包（全部走 Modrinth 镜像源）。</summary>
    private static readonly (string Label, string Type)[] CatalogTypes =
    {
        ("模组", "mod"), ("整合包", "modpack"), ("资源包", "resourcepack"),
        ("光影包", "shader"), ("数据包", "datapack"),
    };

    private static string CatalogTitle(string type) => CatalogTypes
        .FirstOrDefault(t => t.Type == type).Label switch
    {
        "模组" => "模组与整合包",
        null or "" => "资源下载",
        var l => l + "下载",
    };

    private UIElement BuildCatalogPage(string defaultType)
    {
        _catalogProjectType = defaultType;
        var root = new StackPanel();
        root.Children.Add(Label("内容", 11, UiPalette.Accent));
        _catalogHeading = Heading(CatalogTitle(defaultType));
        root.Children.Add(_catalogHeading);
        root.Children.Add(Body("热门内容已自动加载，输入关键词搜索。图片与文件均走国内镜像源。"));

        var sub = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 6) };
        foreach (var (label, type) in CatalogTypes)
        {
            var b = new Button
            {
                Content = label,
                Tag = type,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                FontSize = 12,
                Background = type == _catalogProjectType ? Brush(UiPalette.Accent) : Brush(UiPalette.Surface),
                Foreground = type == _catalogProjectType ? Brush(UiPalette.Bg) : Brush(UiPalette.TextDim),
                BorderThickness = new Thickness(0)
            };
            b.Click += (_, _) =>
            {
                _catalogProjectType = type;
                RefreshChips(sub, type);
                if (_catalogHeading != null) _catalogHeading.Text = CatalogTitle(type);
                if (_localModsCard != null)
                    _localModsCard.Visibility = type == "mod" ? Visibility.Visible : Visibility.Collapsed;
                _onCatalogTypeChanged?.Invoke();
                _ = LoadCatalogAsync("");
            };
            sub.Children.Add(b);
        }
        root.Children.Add(sub);

        // PCL2 风格搜索卡片：名称 / 版本 + 来源 / 类型，搜索 / 重置条件
        root.Children.Add(BuildCatalogSearchCard());
        // 版本下拉需要正式版列表，后台补加载（不阻塞页面）
        if (_allVersions.Count == 0) _ = LoadVersionsAsync();

        // 本地已安装模组列表（含加载器识别）——仅"模组"页签显示
        if (defaultType == "mod")
        {
            _localModsCard = BuildLocalModsCard();
            root.Children.Add(_localModsCard);
        }

        _catalogStatus = new TextBlock { Foreground = Brush(UiPalette.Muted), FontSize = 12, Margin = new Thickness(0, 4, 0, 8) };
        root.Children.Add(_catalogStatus);
        _catalogListPanel = new StackPanel();
        root.Children.Add(_catalogListPanel);

        _ = LoadCatalogAsync("");
        return root;
    }

    /// <summary>PCL2 风格搜索卡片：名称 / 来源 / 版本(可输入) / 类型 + 搜索 / 重置条件。</summary>
    private UIElement BuildCatalogSearchCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 14, 0, 10);
        var inner = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
        card.Children.Add(inner);

        inner.Children.Add(Label("搜索 Mod", 14, UiPalette.Text, FontWeights.SemiBold));

        var grid = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 左列：名称 / 版本；右列：来源 / 类型
        _catalogSearchBox = new TextBox
        {
            Padding = new Thickness(10, 7, 10, 7), FontSize = 13,
            Background = Brush(UiPalette.Bg), Foreground = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border), VerticalContentAlignment = VerticalAlignment.Center
        };
        _catalogSearchBox.KeyDown += async (s, e) => { if (e.Key == Key.Enter) await LoadCatalogAsync(_catalogSearchBox.Text); };

        _catalogVersionBox = new ComboBox
        {
            FontSize = 13, IsEditable = true, Text = "全部",
            Background = Brush(UiPalette.Bg), Foreground = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border)
        };
        FillCatalogVersionOptions();

        var sourceBox = new ComboBox
        {
            FontSize = 13, ItemsSource = new[] { "全部", "Modrinth" }, SelectedIndex = 0,
            Background = Brush(UiPalette.Bg), Foreground = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border)
        };

        _catalogCategoryBox = new ComboBox
        {
            FontSize = 13,
            ItemsSource = CatalogCategories(_catalogProjectType),
            SelectedIndex = 0,
            Background = Brush(UiPalette.Bg), Foreground = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border)
        };

        grid.Children.Add(CatalogField("名称", _catalogSearchBox, 0, 0));
        grid.Children.Add(CatalogField("版本", _catalogVersionBox, 0, 2));
        grid.Children.Add(CatalogField("来源", sourceBox, 1, 0));
        grid.Children.Add(CatalogField("类型", _catalogCategoryBox, 1, 2));
        inner.Children.Add(grid);

        // 搜索 / 重置条件
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var searchBtn = new Button
        {
            Content = "搜索", Width = 130, Padding = new Thickness(0, 8, 0, 8), FontSize = 13,
            Background = Brush(UiPalette.Accent), Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontWeight = FontWeights.SemiBold
        };
        searchBtn.Click += async (_, _) => await LoadCatalogAsync(_catalogSearchBox?.Text ?? "");
        var resetBtn = new Button
        {
            Content = "重置条件", Width = 130, Padding = new Thickness(0, 8, 0, 8), FontSize = 13,
            Margin = new Thickness(14, 0, 0, 0),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        resetBtn.Click += (_, _) =>
        {
            if (_catalogSearchBox != null) _catalogSearchBox.Text = "";
            if (_catalogVersionBox != null) _catalogVersionBox.Text = "全部";
            if (_catalogCategoryBox != null) _catalogCategoryBox.SelectedIndex = 0;
            sourceBox.SelectedIndex = 0;
            _ = LoadCatalogAsync("");
        };
        btnRow.Children.Add(searchBtn);
        btnRow.Children.Add(resetBtn);
        inner.Children.Add(btnRow);

        // 模组/整合包切换时刷新类型下拉
        _onCatalogTypeChanged = () =>
        {
            if (_catalogCategoryBox != null)
            {
                _catalogCategoryBox.ItemsSource = CatalogCategories(_catalogProjectType);
                _catalogCategoryBox.SelectedIndex = 0;
            }
        };
        return card;
    }

    private Action? _onCatalogTypeChanged;

    /// <summary>生成"标签 + 控件"的一格，直接设置行列号；第二行加顶部间距。</summary>
    private static FrameworkElement CatalogField(string label, FrameworkElement control, int row, int col)
    {
        var panel = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        if (row > 0) panel.Margin = new Thickness(0, 12, 0, 0);
        panel.Children.Add(new TextBlock
        {
            Text = label, Width = 44, FontSize = 13,
            Foreground = Brush(UiPalette.Muted), VerticalAlignment = VerticalAlignment.Center
        });
        panel.Children.Add(control);
        Grid.SetRow(panel, row);
        Grid.SetColumn(panel, col);
        return panel;
    }

    /// <summary>版本下拉选项：全部 + 常用正式版（也可自行输入）。</summary>
    private void FillCatalogVersionOptions()
    {
        if (_catalogVersionBox == null) return;
        var families = _allVersions
            .Where(v => v.Category == "正式版")
            .Select(v =>
            {
                var parts = v.Id.Split('.');
                return parts.Length >= 2 ? $"{parts[0]}.{parts[1]}" : v.Id;
            })
            .Distinct()
            .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
            .Take(40)
            .ToList();
        if (families.Count == 0)
            families = ["1.21", "1.20", "1.19", "1.18", "1.17", "1.16", "1.12", "1.8", "1.7"];
        _catalogVersionBox.ItemsSource = families;
        _catalogVersionBox.Text = "全部";
    }

    /// <summary>各项目类型的类型（分类）下拉选项与 Modrinth 分类 ID 的映射。</summary>
    private static readonly Dictionary<string, (string Label, string Id)[]> CatalogCategoryMap = new()
    {
        ["mod"] =
        [
            ("全部", ""), ("实用", "utility"), ("优化", "optimization"), ("存储", "storage"),
            ("科技", "technology"), ("魔法", "magic"), ("冒险", "adventure"), ("娱乐", "minigame"),
            ("信息", "management"), ("装饰", "decoration"), ("库", "library"), ("生物", "mobs"),
            ("装备", "equipment"), ("食物", "food"), ("交通", "transportation"), ("世界生成", "worldgen"),
        ],
        ["modpack"] =
        [
            ("全部", ""), ("多人", "multiplayer"), ("轻量", "light"), ("冒险", "adventure"),
            ("科技", "technology"), ("魔法", "magic"), ("优化", "optimization"), ("任务", "quests"),
            ("空岛", "skyblock"), ("探索", "exploration"), ("快节奏", "fast-paced"),
        ],
        ["resourcepack"] =
        [
            ("全部", ""), ("简洁", "simplistic"), ("写实", "realistic"), ("奇幻", "fantasy"),
            ("现代", "futuristic"), ("原版优先", "vanilla-friendly"), ("实用", "helpful"), ("装饰", "decoration"),
        ],
        ["shader"] =
        [
            ("全部", ""), ("卡通", "cartoon"), ("幻想", "fantasy"), ("写实", "realistic"),
            ("半写实", "semi-realistic"), ("原版风格", "vanilla-like"), ("性能优先", "potato"), ("光追", "path-traced"),
        ],
        ["datapack"] =
        [
            ("全部", ""), ("冒险", "adventure"), ("科技", "technology"), ("魔法", "magic"),
            ("实用", "utility"), ("世界生成", "worldgen"), ("小游戏", "games"), ("生物", "mobs"), ("库", "library"),
        ],
        ["plugin"] =
        [
            ("全部", ""), ("管理", "management"), ("游戏玩法", "gameplay"), ("开发", "development"),
            ("优化", "optimization"), ("社会", "social"), ("经济", "economy"), ("安全", "protection"),
            ("聊天", "chat"), ("传送", "teleport"), ("世界管理", "world-management"), ("API", "api"),
        ],
    };

    private static string[] CatalogCategories(string projectType) =>
        CatalogCategoryMap.TryGetValue(projectType, out var list)
            ? list.Select(x => x.Label).ToArray()
            : ["全部"];

    private string? SelectedCatalogCategory()
    {
        if (_catalogCategoryBox?.SelectedItem is not string label || label == "全部")
            return null;
        return CatalogCategoryMap.TryGetValue(_catalogProjectType, out var list)
            ? list.FirstOrDefault(x => x.Label == label).Id is { Length: > 0 } id ? id : null
            : null;
    }

    /// <summary>把"版本"输入展开为该版本族下的所有正式版 ID（如 1.20 → 1.20, 1.20.1 …）。</summary>
    private List<string>? SelectedCatalogGameVersions()
    {
        var text = _catalogVersionBox?.Text?.Trim();
        if (string.IsNullOrEmpty(text) || text == "全部") return null;
        var ids = _allVersions
            .Where(v => v.Category == "正式版" && (v.Id == text || v.Id.StartsWith(text + ".", StringComparison.Ordinal)))
            .Select(v => v.Id)
            .ToList();
        return ids.Count > 0 ? ids : [text];
    }

    private static void RefreshChips(StackPanel panel, string active)
    {
        foreach (Button b in panel.Children)
        {
            var t = (string)b.Tag;
            var on = string.Equals(t, active);
            b.Background = on ? Brush(UiPalette.Accent) : Brush(UiPalette.Surface);
            b.Foreground = on ? Brush(UiPalette.Bg) : Brush(UiPalette.TextDim);
        }
    }

    #region 本地模组
    private readonly LocalModService _localModService = new();
    private StackPanel? _localModsPanel;

    /// <summary>本地模组卡片：扫描 mods 目录并识别每个模组的加载器。</summary>
    private UIElement BuildLocalModsCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 14, 0, 10);
        var inner = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        card.Children.Add(inner);

        var head = new DockPanel();
        var refreshBtn = new Button
        {
            Content = "刷新", Padding = new Thickness(12, 4, 12, 4), FontSize = 11,
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        refreshBtn.Click += (_, _) => RefreshLocalMods();
        DockPanel.SetDock(refreshBtn, Dock.Right);
        head.Children.Add(refreshBtn);
        var openBtn = new Button
        {
            Content = "打开 mods 目录", Padding = new Thickness(12, 4, 12, 4), FontSize = 11,
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0)
        };
        openBtn.Click += (_, _) =>
        {
            var dir = Path.Combine(_gameDirectory, "mods");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        };
        DockPanel.SetDock(openBtn, Dock.Right);
        head.Children.Add(openBtn);
        head.Children.Add(Label("本地模组（自动识别加载器）", 13, UiPalette.Text, FontWeights.SemiBold));
        inner.Children.Add(head);

        _localModsPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        inner.Children.Add(_localModsPanel);
        RefreshLocalMods();
        return card;
    }

    private void RefreshLocalMods()
    {
        if (_localModsPanel == null) return;
        _localModsPanel.Children.Clear();
        List<LocalModInfo> mods;
        try { mods = _localModService.ScanMods(_gameDirectory).ToList(); }
        catch (Exception ex)
        {
            _localModsPanel.Children.Add(new TextBlock
            {
                Text = "扫描失败：" + ex.Message, FontSize = 12,
                Foreground = Brush(UiPalette.Muted)
            });
            return;
        }
        if (mods.Count == 0)
        {
            _localModsPanel.Children.Add(new TextBlock
            {
                Text = "mods 目录为空，从下方搜索并下载模组后会显示在这里。",
                FontSize = 12, Foreground = Brush(UiPalette.Muted),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }
        foreach (var m in mods.Take(50))
        {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var badge = LoaderBadge(m.Loader);
            DockPanel.SetDock(badge, Dock.Left);
            row.Children.Add(badge);
            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrEmpty(m.Version) ? m.FileName : $"{m.FileName}  ({m.Version})",
                FontSize = 12, Foreground = Brush(UiPalette.TextDim),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = m.FullPath
            });
            _localModsPanel.Children.Add(row);
        }
        if (mods.Count > 50)
            _localModsPanel.Children.Add(new TextBlock
            {
                Text = $"…以及另外 {mods.Count - 50} 个模组", FontSize = 11,
                Foreground = Brush(UiPalette.Muted), Margin = new Thickness(0, 4, 0, 0)
            });
    }
    #endregion

    private async Task LoadCatalogAsync(string query)
    {
        _catalogCts?.Cancel();
        _catalogCts = new CancellationTokenSource();
        var token = _catalogCts.Token;

        if (_catalogStatus != null) _catalogStatus.Text = "加载中...";
        try
        {
            var items = await _modrinthService.SearchAsync(
                query, _catalogProjectType,
                category: SelectedCatalogCategory(),
                gameVersions: SelectedCatalogGameVersions(),
                cancellationToken: token);
            if (token.IsCancellationRequested || _catalogListPanel == null) return;
            _catalogListPanel.Children.Clear();
            if (items.Count == 0)
            {
                if (_catalogStatus != null) _catalogStatus.Text = "没有找到符合条件的 Mod，试试放宽筛选条件。";
                return;
            }
            if (_catalogStatus != null) _catalogStatus.Text = $"{items.Count} 个结果";
            foreach (var it in items) _catalogListPanel.Children.Add(BuildCatalogRow(it));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            if (_catalogStatus != null) _catalogStatus.Text = "加载失败：" + ex.Message;
            if (_catalogListPanel != null)
            {
                var retry = new Button
                {
                    Content = "重试",
                    Padding = new Thickness(16, 8, 16, 8),
                    Margin = new Thickness(0, 10, 0, 0),
                    Background = Brush(UiPalette.Accent),
                    Foreground = Brush(UiPalette.Bg),
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand
                };
                retry.Click += async (_, _) => await LoadCatalogAsync(query);
                _catalogListPanel.Children.Add(retry);
            }
        }
    }

    private static readonly HttpClient _imgHttp = new() { Timeout = TimeSpan.FromSeconds(8) };
    // 皮肤头像专用：共享连接池，避免每个头像新建 HttpClient
    private static readonly HttpClient _skinHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    // 皮肤头像缓存：URL -> 裁好的脸部图（翻页返回秒显）
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource> _skinHeadCache = new();

    // 图标缓存 + 并发闸门：列表滚动时同一张图不重复下载，同时最多 6 个请求
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, BitmapSource> _iconCache = new();
    private static readonly SemaphoreSlim _imgGate = new(6, 6);

    private static async Task<BitmapSource?> LoadBitmapAsync(string url, int decodeWidth = 96)
    {
        var key = decodeWidth + "|" + url;
        if (_iconCache.TryGetValue(key, out var hit)) return hit;

        await _imgGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_iconCache.TryGetValue(key, out hit)) return hit;

            // 多源回退：MCIM 镜像对图片会 302 弹回官方 CDN，官方 CDN 在部分用户网络不可达，
            // 导致图标/图片全空。依次尝试 镜像 → 官方 → wsrv.nl 公共图片代理。
            var candidates = new List<string> { ModrinthService.MirrorUrl(url), url };
            var wsrvSrc = url.StartsWith("https://cdn.modrinth.com/")
                ? url["https://cdn.modrinth.com/".Length..] : url;
            candidates.Add($"https://wsrv.nl/?url={Uri.EscapeDataString(wsrvSrc)}&w={Math.Min(decodeWidth, 1024)}");

            byte[]? bytes = null;
            foreach (var candidate in candidates)
            {
                try
                {
                    bytes = await _imgHttp.GetByteArrayAsync(candidate).ConfigureAwait(false);
                    if (bytes is { Length: > 0 }) break;
                }
                catch { /* 尝试下一个源 */ }
                bytes = null;
            }
            if (bytes == null || bytes.Length == 0) return null;

            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();

            if (_iconCache.Count > 800) _iconCache.Clear();
            _iconCache[key] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
        finally
        {
            _imgGate.Release();
        }
    }

    private UIElement BuildCatalogRow(ModrinthProject item)
    {
        var card = Card();
        card.Margin = new Thickness(0, 0, 0, 8);
        var row = new DockPanel { Margin = new Thickness(12, 10, 12, 10) };

        // 右侧下载按钮（先 dock 到右边）
        var dlBtn = new Button
        {
            Content = "下载",
            Padding = new Thickness(16, 7, 16, 7),
            Background = Brush(UiPalette.Accent),
            Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        };
        dlBtn.Click += async (_, _) => await DownloadModAsync(item, dlBtn);
        DockPanel.SetDock(dlBtn, Dock.Right);
        row.Children.Add(dlBtn);

        // 左侧图标（PCL2 规格：44x44 圆角）
        var placeholder = new Border
        {
            Width = 44, Height = 44, CornerRadius = new CornerRadius(6),
            Background = Brush(UiPalette.Border),
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = string.IsNullOrEmpty(item.Title) ? "?" : item.Title[..1].ToUpper(),
                FontSize = 18, FontWeight = FontWeights.Bold,
                Foreground = Brush(UiPalette.Accent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        DockPanel.SetDock(placeholder, Dock.Left);
        row.Children.Add(placeholder);

        if (!string.IsNullOrEmpty(item.IconUrl))
            _ = LoadIconAsync(placeholder, item.IconUrl);

        // 中间文字：标题 | 英文名 → 简介 → 加载器/版本/下载量/更新时间/来源
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // 第 1 行：中文名 | 英文 slug
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        titleRow.Children.Add(new TextBlock
        {
            Text = item.Title, FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrEmpty(item.Slug) && !item.Slug.Equals(item.Title, StringComparison.OrdinalIgnoreCase))
        {
            titleRow.Children.Add(new TextBlock
            {
                Text = $"  |  {item.Slug}", FontSize = 12,
                Foreground = Brush(UiPalette.Muted), VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        stack.Children.Add(titleRow);

        // 第 2 行：简介（单行省略，悬浮可见全文）
        stack.Children.Add(new TextBlock
        {
            Text = item.Description, FontSize = 12,
            Foreground = Brush(UiPalette.MutedLight),
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = item.Description,
            Margin = new Thickness(0, 2, 0, 0)
        });

        // 第 3 行：加载器徽章 + 版本范围 + 下载量 + 更新时间 + 来源
        var metaRow = new WrapPanel { Margin = new Thickness(0, 5, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var loader in item.Loaders)
            metaRow.Children.Add(LoaderBadge(loader));
        var range = VersionRangeSummary(item.Versions);
        if (range != "")
            metaRow.Children.Add(MetaText(range));
        metaRow.Children.Add(MetaText($"↓ {FormatCount(item.Downloads)}"));
        metaRow.Children.Add(MetaText($"↑ {RelativeTime(item.DateModified)}"));
        metaRow.Children.Add(MetaText("Modrinth"));
        stack.Children.Add(metaRow);

        row.Children.Add(stack);
        card.Children.Add(row);
        return card;
    }

    /// <summary>元信息小字（版本范围 / 下载量 / 更新时间 / 来源），PCL2 灰字风格。</summary>
    private static TextBlock MetaText(string text) => new()
    {
        Text = text, FontSize = 11,
        Foreground = Brush(UiPalette.Muted),
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 14, 0)
    };

    /// <summary>版本范围摘要（PCL2 风格如 "1.20+, 1.14-"）：取支持版本族的最高与最低。</summary>
    private static string VersionRangeSummary(IReadOnlyList<string> versions)
    {
        if (versions.Count == 0) return "";
        static string Family(string v)
        {
            var parts = v.Split('.');
            return parts.Length >= 2 && parts[0] == "1" ? $"{parts[0]}.{parts[1]}" : parts[0];
        }
        static double Score(string f)
        {
            double s = 0;
            foreach (var p in f.Split('.'))
                if (double.TryParse(p, System.Globalization.CultureInfo.InvariantCulture, out var d)) s = s * 1000 + d;
            return s;
        }
        var fams = versions.Select(Family).Where(f => f.Length > 0).Distinct().OrderByDescending(Score).ToList();
        if (fams.Count == 0) return "";
        return fams.Count == 1 ? $"{fams[0]}+" : $"{fams[0]}+, {fams[^1]}-";
    }

    /// <summary>下载量中文格式（PCL2 风格：4.68 亿 / 236 万）。</summary>
    private static string FormatCount(long n) =>
        n >= 100_000_000 ? $"{n / 100_000_000.0:0.##} 亿" :
        n >= 10_000 ? $"{n / 10_000.0:0.#} 万" :
        n.ToString("N0");

    /// <summary>相对时间（x 天前 / x 个月前…）。</summary>
    private static string RelativeTime(DateTimeOffset t)
    {
        var span = DateTimeOffset.Now - t;
        return span.TotalMinutes < 1 ? "刚刚"
            : span.TotalMinutes < 60 ? $"{(int)span.TotalMinutes} 分钟前"
            : span.TotalHours < 24 ? $"{(int)span.TotalHours} 小时前"
            : span.TotalDays < 30 ? $"{(int)span.TotalDays} 天前"
            : span.TotalDays < 365 ? $"{(int)(span.TotalDays / 30)} 个月前"
            : $"{(int)(span.TotalDays / 365)} 年前";
    }

    /// <summary>模组加载器彩色徽章，不同加载器不同颜色。</summary>
    private static Border LoaderBadge(string loader)
    {
        var color = loader.ToLowerInvariant() switch
        {
            "forge" => "#C7793A",
            "neoforge" => "#E05A4E",
            "fabric" => "#D8B24A",
            "quilt" => "#9B6BD3",
            _ => "#5B9DFF"
        };
        return new Border
        {
            Background = Brush(color),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 6, 2),
            Child = new TextBlock
            {
                Text = loader,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.White
            }
        };
    }

    private async Task DownloadModAsync(ModrinthProject item, Button btn)
    {
        btn.IsEnabled = false;
        btn.Content = "获取中...";
        // 注册到右下角下载面板（模组组）
        var dlItem = DownloadManagerService.Instance.Add(item.Title, DownloadKind.Mod);
        dlItem.Status = DownloadStatus.Downloading;
        dlItem.Detail = "获取下载地址...";
        try
        {
            var info = await _modrinthService.GetLatestDownloadAsync(item.Id);
            if (info == null)
            {
                btn.Content = "无版本";
                DownloadManagerService.Instance.Fail(dlItem, "无可用版本");
                return;
            }
            // 按类型决定下载目录
            string subDir = _catalogProjectType switch
            {
                "resourcepack" => "resourcepacks",
                "modpack" => "modpacks",
                _ => "mods"
            };
            var dir = Path.Combine(_gameDirectory, subDir);
            Directory.CreateDirectory(dir);
            var destPath = Path.Combine(dir, info.Value.FileName);
            btn.Content = "下载中...";
            var progress = new Progress<(long Received, long Total)>(p =>
            {
                if (p.Total > 0)
                {
                    dlItem.Progress = (double)p.Received / p.Total;
                    dlItem.Detail = $"{FormatBytes(p.Received)} / {FormatBytes(p.Total)}";
                }
                else
                {
                    dlItem.Detail = FormatBytes(p.Received);
                }
            });
            await _modrinthService.DownloadFileAsync(info.Value.Url, destPath, progress);
            DownloadManagerService.Instance.Complete(dlItem);
            btn.Content = "已保存";
        }
        catch (Exception ex)
        {
            DownloadManagerService.Instance.Fail(dlItem, ex.Message);
            btn.Content = "失败";
            MessageBox.Show($"下载失败：\n{ex.Message}", "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btn.IsEnabled = true;
        }
    }

    #region 下载面板
    private void ToggleDownloadPanel_Click(object sender, RoutedEventArgs e)
    {
        var show = DownloadPanel.Visibility != Visibility.Visible;
        DownloadPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        _downloadPanelUserClosed = !show;
    }

    private void ClearFinishedDownloads_Click(object sender, RoutedEventArgs e)
        => DownloadManagerService.Instance.ClearFinished();

    /// <summary>刷新悬浮下载按钮的可见性、角标数字与空态提示。</summary>
    private void UpdateDownloadUi()
    {
        var mgr = DownloadManagerService.Instance;
        var active = mgr.ActiveCount;
        DownloadToggle.Visibility = mgr.HasAny ? Visibility.Visible : Visibility.Collapsed;
        DownloadToggle.Tag = active > 0 ? $"{active} 个下载中" : "全部完成";
        GameDownloadEmpty.Visibility = mgr.GameDownloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ModDownloadEmpty.Visibility = mgr.ModDownloads.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 有新任务时自动展开面板提示一次
        if (active > 0 && DownloadPanel.Visibility != Visibility.Visible && !_downloadPanelUserClosed)
            DownloadPanel.Visibility = Visibility.Visible;
    }

    private bool _downloadPanelUserClosed;
    #endregion
    #endregion

    private static async Task LoadIconAsync(Border placeholder, string url)
    {
        try
        {
            // 统一走 MCIM 镜像；同一 URL 命中缓存就不再发请求
            var bmp = await LoadBitmapAsync(url, 96);
            if (bmp == null) return;
            await placeholder.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    var img = new Image
                    {
                        Source = bmp,
                        Stretch = Stretch.UniformToFill,
                        Width = 44, Height = 44
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.Linear);
                    placeholder.Child = img;
                }
                catch { /* 解码失败保留占位块 */ }
            });
        }
        catch { /* 网络失败保留占位块 */ }
    }

    #region 其他页
    private UIElement BuildFeaturePage(string title, string desc, string status)
    {
        var root = new StackPanel();
        root.Children.Add(Label("管理", 11, UiPalette.Accent));
        root.Children.Add(Heading(title));
        root.Children.Add(Body(desc));
        var card = Card();
        card.Margin = new Thickness(0, 22, 0, 0);
        card.Children.Add(Body(status));
        root.Children.Add(card);
        return root;
    }

    private string? _selectedSkinPath;
    private Image? _skinPreview;
    private TextBlock? _skinStatus;
    private Button? _skinApplyBtn;

    // 账号页皮肤选择器状态（与皮肤页 _selectedSkinPath 相互独立，另用 _accountSkinSelected）
    private string? _accountSkinSelected;
    /// <summary>选中皮肤时已知的模型（wide/slim），用于「应用」时免去弹窗询问。</summary>
    private string? _pendingSkinModel;
    private WrapPanel? _skinLibraryPanel;
    private Button? _skinSavedTab;
    private Button? _skinOfficialTab;

    // 皮肤库状态
    private readonly Services.SkinCatalogService _skinCatalog = new();
    private int _skinGalleryPage = 1;
    private string _skinGalleryKind = "";
    private UniformGrid? _skinGalleryGrid;
    private TextBlock? _skinGalleryPageText;
    private Button? _skinGalleryPrevBtn;
    private Button? _skinGalleryNextBtn;

    private UIElement BuildSkinsPage()
    {
        var root = new StackPanel();
        root.Children.Add(Label("外观", 11, UiPalette.Accent));
        root.Children.Add(Heading("皮肤"));
        root.Children.Add(Body("内置皮肤库（Ely.by 皮肤站，共 20 页 800+ 个皮肤），点击皮肤即可应用到账号。"));

        // ---- 皮肤库工具栏 ----
        var toolbar = Card();
        toolbar.Margin = new Thickness(0, 16, 0, 0);
        var toolbarInner = new DockPanel { Margin = new Thickness(16, 12, 16, 12) };

        var navPanel = new StackPanel { Orientation = Orientation.Horizontal };
        _skinGalleryPrevBtn = new Button
        {
            Content = "◀ 上一页", Padding = new Thickness(12, 7, 12, 7), FontSize = 12,
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        _skinGalleryPrevBtn.Click += (_, _) => { if (_skinGalleryPage > 1) { _skinGalleryPage--; _ = LoadSkinGalleryAsync(); } };
        _skinGalleryNextBtn = new Button
        {
            Content = "下一页 ▶", Padding = new Thickness(12, 7, 12, 7), FontSize = 12,
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Margin = new Thickness(8, 0, 0, 0)
        };
        _skinGalleryNextBtn.Click += (_, _) => { if (_skinGalleryPage < Services.SkinCatalogService.MaxPages) { _skinGalleryPage++; _ = LoadSkinGalleryAsync(); } };
        _skinGalleryPageText = new TextBlock
        {
            Text = "第 1 / 20 页", FontSize = 12, Foreground = Brush(UiPalette.Text),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0)
        };
        navPanel.Children.Add(_skinGalleryPrevBtn);
        navPanel.Children.Add(_skinGalleryNextBtn);
        navPanel.Children.Add(_skinGalleryPageText);
        DockPanel.SetDock(navPanel, Dock.Right);
        toolbarInner.Children.Add(navPanel);

        var kindPanel = new StackPanel { Orientation = Orientation.Horizontal };
        kindPanel.Children.Add(new TextBlock
        {
            Text = "分类", FontSize = 12, Foreground = Brush(UiPalette.Muted),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
        });
        var kindBox = new ComboBox
        {
            FontSize = 12, MinWidth = 120,
            ItemsSource = Services.SkinCatalogService.Kinds.Select(k => k.Label).ToList(),
            SelectedIndex = 0,
            Background = Brush(UiPalette.Bg), Foreground = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border)
        };
        kindBox.SelectionChanged += (_, _) =>
        {
            var idx = kindBox.SelectedIndex;
            if (idx < 0 || idx >= Services.SkinCatalogService.Kinds.Length) return;
            _skinGalleryKind = Services.SkinCatalogService.Kinds[idx].Key;
            _skinGalleryPage = 1;
            _ = LoadSkinGalleryAsync();
        };
        kindPanel.Children.Add(kindBox);
        toolbarInner.Children.Add(kindPanel);
        toolbar.Children.Add(toolbarInner);
        root.Children.Add(toolbar);

        // ---- 皮肤网格 ----
        var galleryCard = Card();
        galleryCard.Margin = new Thickness(0, 10, 0, 0);
        var galleryWrap = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        _skinGalleryGrid = new UniformGrid { Columns = 5 };
        galleryWrap.Children.Add(_skinGalleryGrid);
        galleryCard.Children.Add(galleryWrap);
        root.Children.Add(galleryCard);

        // ---- 本地皮肤文件（上传到正版账号）----
        root.Children.Add(BuildLocalSkinCard());

        // ---- 其他皮肤站直达 ----
        var linksCard = Card();
        linksCard.Margin = new Thickness(0, 10, 0, 0);
        var linksInner = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
        linksInner.Children.Add(Label("没找到心仪的皮肤？去这些皮肤站逛逛", 13, UiPalette.Text, FontWeights.SemiBold));
        var linksRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        void AddLink(string text, string url)
        {
            var b = new Button
            {
                Content = text, Padding = new Thickness(14, 8, 14, 8), FontSize = 12,
                Margin = new Thickness(0, 0, 10, 0),
                Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand
            };
            b.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show("无法打开浏览器：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Warning); }
            };
            linksRow.Children.Add(b);
        }
        AddLink("Ely.by 皮肤站（本皮肤库来源，上万皮肤）", "https://ely.by/skins");
        AddLink("NameMC", "https://namemc.com/skins");
        AddLink("The Skindex", "https://www.minecraftskins.com");
        linksInner.Children.Add(linksRow);
        linksCard.Children.Add(linksInner);
        root.Children.Add(linksCard);

        _ = LoadSkinGalleryAsync();
        return root;
    }

    /// <summary>加载当前页皮肤到网格。</summary>
    private async Task LoadSkinGalleryAsync()
    {
        if (_skinGalleryGrid == null) return;
        if (_skinGalleryPageText != null)
            _skinGalleryPageText.Text = $"第 {_skinGalleryPage} / {Services.SkinCatalogService.MaxPages} 页";
        if (_skinGalleryPrevBtn != null) _skinGalleryPrevBtn.IsEnabled = _skinGalleryPage > 1;
        if (_skinGalleryNextBtn != null)
            _skinGalleryNextBtn.IsEnabled = _skinGalleryPage < Services.SkinCatalogService.MaxPages;

        _skinGalleryGrid.Children.Clear();
        var loading = new TextBlock
        {
            Text = "正在加载皮肤库...", FontSize = 13, Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(4, 8, 0, 8)
        };
        _skinGalleryGrid.Children.Add(loading);

        try
        {
            var items = await _skinCatalog.GetPageAsync(_skinGalleryPage, _skinGalleryKind);
            if (_skinGalleryGrid == null) return;
            _skinGalleryGrid.Children.Clear();
            foreach (var item in items)
                _skinGalleryGrid.Children.Add(BuildSkinCard(item));
            if (items.Count == 0)
            {
                _skinGalleryGrid.Children.Add(new TextBlock
                {
                    Text = "这一页暂时没有皮肤，试试换个分类。", FontSize = 13,
                    Foreground = Brush(UiPalette.Muted), Margin = new Thickness(4, 8, 0, 8)
                });
            }
        }
        catch (Exception ex)
        {
            if (_skinGalleryGrid == null) return;
            _skinGalleryGrid.Children.Clear();
            _skinGalleryGrid.Children.Add(new TextBlock
            {
                Text = "皮肤库加载失败：" + ex.Message + "\n请检查网络，或用下方直达链接访问皮肤站。",
                FontSize = 13, Foreground = Brush(UiPalette.Muted), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 8, 0, 8)
            });
        }
    }

    /// <summary>皮肤网格卡片：头部预览 + 人气。</summary>
    private UIElement BuildSkinCard(SkinItem item)
    {
        var card = new Border
        {
            Margin = new Thickness(6), CornerRadius = new CornerRadius(8),
            Background = Brush(UiPalette.SurfaceAlt), Cursor = Cursors.Hand,
            Padding = new Thickness(10, 12, 10, 10),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var stack = new StackPanel();
        var head = new Image
        {
            Width = 64, Height = 64,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        stack.Children.Add(head);
        stack.Children.Add(new TextBlock
        {
            Text = $"#{item.Id}", FontSize = 11, Foreground = Brush(UiPalette.Text),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0)
        });
        stack.Children.Add(new TextBlock
        {
            Text = item.Wearers > 0 ? $"{FormatSkinCount(item.Wearers)} 人在用" : (item.IsSlim ? "纤细模型" : "经典模型"),
            FontSize = 10, Foreground = Brush(UiPalette.Muted),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0)
        });
        card.Child = stack;
        card.MouseLeftButtonUp += (_, _) => ShowSkinDetail(item);

        // 加载头部预览（从皮肤 PNG 裁剪脸部 8x8）
        _ = LoadSkinHeadAsync(head, item);
        return card;
    }

    private static string FormatSkinCount(int n) => n switch
    {
        >= 10000 => $"{n / 10000.0:0.#} 万",
        >= 1000 => $"{n / 1000.0:0.#} 千",
        _ => n.ToString()
    };

    /// <summary>从皮肤 PNG 裁出 8x8 脸部（兼容 64x/128x 等 HD 皮肤），像素风最近邻采样保证清晰。</summary>
    private static CroppedBitmap? CreateFaceBitmap(BitmapImage bmp)
    {
        // 脸部区域随皮肤尺寸等比缩放：64x64 时是 (8,8,8,8)，128x128 时是 (16,16,16,16)
        var s = Math.Max(1, bmp.PixelWidth / 64);
        var rect = new Int32Rect(8 * s, 8 * s, 8 * s, 8 * s);
        if (rect.X + rect.Width > bmp.PixelWidth || rect.Y + rect.Height > bmp.PixelHeight)
            rect = new Int32Rect(8, 8, 8, 8);
        return new CroppedBitmap(bmp, rect);
    }

    private static async Task LoadSkinHeadAsync(Image target, SkinItem item)
    {
        // 像素图必须用最近邻采样：默认线性插值会把 8x8 小图放大成一团糊
        RenderOptions.SetBitmapScalingMode(target, BitmapScalingMode.NearestNeighbor);

        if (_skinHeadCache.TryGetValue(item.ImageUrl, out var cached))
        {
            target.Source = cached;
            return;
        }

        try
        {
            var bytes = await _skinHttp.GetByteArrayAsync(item.ImageUrl);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // 全尺寸解码后再裁剪，避免先缩放导致脸部错位/发糊
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = new MemoryStream(bytes);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();

                    var face = CreateFaceBitmap(bmp);
                    if (face != null)
                    {
                        if (_skinHeadCache.Count > 600) _skinHeadCache.Clear();
                        _skinHeadCache[item.ImageUrl] = face;
                        target.Source = face;
                    }
                }
                catch { /* 头像裁剪失败留空 */ }
            });
        }
        catch { /* 网络失败留空 */ }
    }

    /// <summary>皮肤详情弹窗：预览 + 应用到正版 / 离线 / 保存本地。</summary>
    private void ShowSkinDetail(SkinItem item)
    {
        var dlg = new Window
        {
            Title = "皮肤详情",
            Width = 420, Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize
        };
        TextOptions.SetTextFormattingMode(dlg, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(dlg, TextRenderingMode.ClearType);

        var outer = new Border
        {
            Background = Brush(UiPalette.Header), BorderBrush = Brush(UiPalette.Border),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Margin = new Thickness(12)
        };
        var inner = new StackPanel { Margin = new Thickness(22, 16, 22, 18) };

        // 标题行
        var titleRow = new DockPanel();
        var closeBtn = new Button
        {
            Content = "✕", Width = 30, Height = 26, FontSize = 12,
            Background = Brushes.Transparent, Foreground = Brush(UiPalette.Muted),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        closeBtn.Click += (_, _) => dlg.Close();
        DockPanel.SetDock(closeBtn, Dock.Right);
        titleRow.Children.Add(closeBtn);
        titleRow.Children.Add(new TextBlock
        {
            Text = $"皮肤 #{item.Id} · {(item.IsSlim ? "纤细模型" : "经典模型")}",
            FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush(UiPalette.Text),
            VerticalAlignment = VerticalAlignment.Center
        });
        inner.Children.Add(titleRow);

        // 预览：头部大图 + 原始皮肤图
        var previewPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        var face = new Image { Width = 96, Height = 96, Margin = new Thickness(0, 0, 16, 0) };
        var full = new Image { Width = 64, Height = 64, Stretch = Stretch.UniformToFill };
        previewPanel.Children.Add(face);
        previewPanel.Children.Add(full);
        inner.Children.Add(previewPanel);
        var metaText = new TextBlock
        {
            Text = item.Wearers > 0 ? $"{FormatSkinCount(item.Wearers)} 人在使用" : "",
            FontSize = 11, Foreground = Brush(UiPalette.Muted),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 6)
        };
        inner.Children.Add(metaText);

        var statusText = new TextBlock
        {
            FontSize = 12, Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 12), MinHeight = 20
        };
        void SetStatus(string s) => statusText.Text = s;

        // 下载皮肤到临时文件并渲染预览
        string? localPath = null;
        var loadTask = Task.Run(async () =>
        {
            localPath = await _skinCatalog.DownloadSkinAsync(item);
        });
        loadTask.ContinueWith(t =>
        {
            Dispatcher.Invoke(() =>
            {
                if (t.IsFaulted || localPath == null)
                {
                    SetStatus("皮肤下载失败，请检查网络后重试。");
                    return;
                }
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(localPath, UriKind.Absolute);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    // 像素风最近邻采样：小图放大不糊
                    RenderOptions.SetBitmapScalingMode(face, BitmapScalingMode.NearestNeighbor);
                    RenderOptions.SetBitmapScalingMode(full, BitmapScalingMode.NearestNeighbor);
                    face.Source = CreateFaceBitmap(bmp);
                    full.Source = bmp;
                }
                catch { SetStatus("皮肤文件无法解析（不是标准皮肤 PNG）。"); }
            });
        });

        Button MakeBtn(string text, bool accent)
        {
            return new Button
            {
                Content = text, Padding = new Thickness(0, 10, 0, 10), FontSize = 13,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10),
                Background = accent ? Brush(UiPalette.Accent) : Brush(UiPalette.SurfaceAlt),
                Foreground = accent ? Brush(UiPalette.Bg) : Brush(UiPalette.TextDim),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand
            };
        }

        // 应用到微软正版
        var msBtn = MakeBtn("应用到微软正版账号", true);
        msBtn.Click += async (_, _) =>
        {
            if (localPath == null) { SetStatus("皮肤还在下载中，请稍候..."); return; }
            if (_session == null || _session.UserType == "legacy")
            {
                SetStatus("当前不是微软正版账号，请先在左上角用微软登录。");
                return;
            }
            msBtn.IsEnabled = false;
            SetStatus("正在上传皮肤到微软账号...");
            try
            {
                await UploadSkinToMicrosoftAsync(localPath, item.IsSlim ? "slim" : "wide");
                SetStatus("已应用到微软正版账号！下次进入游戏即可看到。");
            }
            catch (Exception ex)
            {
                SetStatus("上传失败：" + ex.Message);
            }
            msBtn.IsEnabled = true;
        };
        inner.Children.Add(msBtn);

        // 应用到离线账号
        var offlineBtn = MakeBtn("应用到离线账号", false);
        offlineBtn.Click += async (_, _) =>
        {
            if (localPath == null) { SetStatus("皮肤还在下载中，请稍候..."); return; }
            offlineBtn.IsEnabled = false;
            SetStatus("正在准备离线皮肤组件（首次需下载 authlib-injector）...");
            var err = await Services.LocalSkinServer.Instance.EnsureAgentAsync();
            if (err != null)
            {
                SetStatus("离线皮肤组件下载失败：" + err);
                offlineBtn.IsEnabled = true;
                return;
            }
            // 保存皮肤到启动器数据目录
            var skinDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NEXLauncher", "skins");
            Directory.CreateDirectory(skinDir);
            var destPath = Path.Combine(skinDir, $"offline_{item.Id}.png");
            File.Copy(localPath, destPath, true);

            var playerName = _session?.UserType == "legacy" && _session != null
                ? _session.Username
                : (_settings.QuickOfflineName ?? "Player");
            Services.LocalSkinServer.Instance.ApplySkin(destPath, item.IsSlim ? "slim" : "wide", playerName);
            _settings.OfflineSkinPath = destPath;
            _settings.OfflineSkinModel = item.IsSlim ? "slim" : "wide";
            _settings.OfflineSkinPlayer = playerName;
            _settings.Save();
            SetStatus($"已为离线账号 \"{playerName}\" 设置皮肤！下次启动游戏生效。");
            offlineBtn.IsEnabled = true;
            // 立即刷新侧栏头像为刚设置的皮肤脸部
            if (_session != null) _ = LoadAvatarAsync(_session);
        };
        inner.Children.Add(offlineBtn);

        // 保存到本地
        var saveBtn = MakeBtn("仅保存皮肤文件到电脑", false);
        saveBtn.Click += (_, _) =>
        {
            if (localPath == null) { SetStatus("皮肤还在下载中，请稍候..."); return; }
            var sfd = new SaveFileDialog
            {
                Filter = "PNG 皮肤文件|*.png",
                FileName = $"skin_{item.Id}.png",
                Title = "保存皮肤"
            };
            if (sfd.ShowDialog() == true)
            {
                File.Copy(localPath, sfd.FileName, true);
                SetStatus("已保存到 " + sfd.FileName);
            }
        };
        inner.Children.Add(saveBtn);
        inner.Children.Add(statusText);

        outer.Child = inner;
        dlg.Content = outer;
        dlg.MouseLeftButtonDown += (_, _) => { try { dlg.DragMove(); } catch { } };
        dlg.ShowDialog();
    }

    /// <summary>上传 PNG 到微软正版账号（api.minecraftservices.com）。</summary>
    private async Task UploadSkinToMicrosoftAsync(string skinPath, string model)
    {
        if (_session == null) throw new InvalidOperationException("未登录");
        _skinHttp.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _session.AccessToken);
        using var form = new MultipartFormDataContent();
        using var fileStream = File.OpenRead(skinPath);
        var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
        form.Add(fileContent, "file", Path.GetFileName(skinPath));
        form.Add(new StringContent(string.IsNullOrEmpty(model) ? "wide" : model), "model");
        var resp = await _skinHttp.PostAsync("https://api.minecraftservices.com/minecraft/profile/skins", form);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>本地皮肤文件卡片（原上传功能保留）。</summary>
    private UIElement BuildLocalSkinCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 10, 0, 0);
        var inner = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        inner.Children.Add(Label("本地皮肤文件", 11, UiPalette.Muted));
        var previewRow = new DockPanel { Margin = new Thickness(0, 6, 0, 12) };
        _skinPreview = new Image
        {
            Width = 72, Height = 72, Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 16, 0)
        };
        RenderOptions.SetBitmapScalingMode(_skinPreview, BitmapScalingMode.NearestNeighbor);
        DockPanel.SetDock(_skinPreview, Dock.Left);
        previewRow.Children.Add(_skinPreview);
        var rightPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var fileText = new TextBlock
        {
            Text = "从电脑选择一张 64x64（或 64x128）PNG 皮肤上传到正版账号。",
            FontSize = 12, Foreground = Brush(UiPalette.Muted), Margin = new Thickness(0, 0, 0, 10)
        };
        rightPanel.Children.Add(fileText);
        var browseBtn = new Button
        {
            Content = "浏览皮肤文件...", Padding = new Thickness(14, 8, 14, 8),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.Text),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        browseBtn.Click += BrowseSkin_Click;
        rightPanel.Children.Add(browseBtn);
        previewRow.Children.Add(rightPanel);
        inner.Children.Add(previewRow);
        _skinStatus = new TextBlock
        {
            Foreground = Brush(UiPalette.Muted), FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        inner.Children.Add(_skinStatus);
        _skinApplyBtn = new Button
        {
            Content = "上传皮肤到正版账号", Padding = new Thickness(16, 9, 16, 9),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brush(UiPalette.Accent), Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand, IsEnabled = false
        };
        _skinApplyBtn.Click += async (_, _) =>
        {
            if (string.IsNullOrEmpty(_selectedSkinPath)) return;
            if (_session == null || _session.UserType == "legacy")
            {
                if (_skinStatus != null)
                    _skinStatus.Text = "请先使用微软正版账号登录；离线账号请用上方皮肤库的\"应用到离线账号\"。";
                return;
            }
            _skinApplyBtn.IsEnabled = false;
            if (_skinStatus != null) _skinStatus.Text = "正在上传...";
            try
            {
                await UploadSkinToMicrosoftAsync(_selectedSkinPath, "slim");
                if (_skinStatus != null) _skinStatus.Text = "皮肤上传成功！下次进入游戏即可看到。";
            }
            catch (Exception ex)
            {
                if (_skinStatus != null) _skinStatus.Text = "上传失败: " + ex.Message;
            }
            _skinApplyBtn.IsEnabled = true;
        };
        inner.Children.Add(_skinApplyBtn);
        card.Children.Add(inner);
        return card;
    }

    private void BrowseSkin_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "PNG 皮肤文件|*.png|所有文件|*.*",
            Title = "选择 Minecraft 皮肤"
        };
        if (dlg.ShowDialog() != true) return;
        _selectedSkinPath = dlg.FileName;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(_selectedSkinPath, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            if (_skinPreview != null) _skinPreview.Source = bmp;
            if (_skinStatus != null)
                _skinStatus.Text = $"已选择: {Path.GetFileName(_selectedSkinPath)} ({bmp.PixelWidth}x{bmp.PixelHeight})";
            if (_skinApplyBtn != null) _skinApplyBtn.IsEnabled = true;
        }
        catch (Exception ex)
        {
            if (_skinStatus != null) _skinStatus.Text = "无法加载图片: " + ex.Message;
        }
    }

    #region 账号页
    private Skin3DPreview? _accountSkinPreview;
    private TextBlock? _accountPageStatus;

    /// <summary>账号管理页：当前账号信息 + 离线账号切换 + 3D 皮肤预览（WPF 3D 渲染）。</summary>
    private UIElement BuildAccountsPage()
    {
        var root = new StackPanel();

        // 标题行：账号管理 + 右侧「删除当前账号」「+ 添加账号」
        var headRow = new DockPanel();
        var addBtn = new Button
        {
            Content = "+ 添加账号",
            Padding = new Thickness(16, 8, 16, 8),
            Background = Brush(UiPalette.Accent),
            Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Cursor = Cursors.Hand
        };
        addBtn.Click += (_, _) => AddAccount_Click();
        DockPanel.SetDock(addBtn, Dock.Right);

        var delCurrentBtn = new Button
        {
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 10, 0),
            Background = Brush(UiPalette.SurfaceAlt),
            Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x7B, 0x7B)),
            BorderThickness = new Thickness(0),
            FontSize = 12,
            Cursor = Cursors.Hand,
            ToolTip = "删除当前账号（多账号列表中移除并退出登录）"
        };
        var delCurrentPanel = new StackPanel { Orientation = Orientation.Horizontal };
        delCurrentPanel.Children.Add(new TextBlock
        {
            Text = "\uE74D",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        delCurrentPanel.Children.Add(new TextBlock
        {
            Text = "删除当前账号",
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        delCurrentBtn.Content = delCurrentPanel;
        delCurrentBtn.Click += (_, _) => DeleteCurrentAccount_Click();
        DockPanel.SetDock(delCurrentBtn, Dock.Right);

        headRow.Children.Add(addBtn);
        headRow.Children.Add(delCurrentBtn);
        headRow.Children.Add(Heading("账号管理"));
        root.Children.Add(headRow);
        root.Children.Add(Body("管理你的游戏账号和皮肤"));

        // ---- 我的账号 (N) ----
        // 兼容旧数据：当前账号还没进过多账号列表时自动补录
        if (_session != null && string.IsNullOrEmpty(_settings.CurrentAccountKey))
            SyncCurrentToAccounts();

        var listCard = Card();
        listCard.Margin = new Thickness(0, 14, 0, 0);
        var listInner = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };
        listCard.Children.Add(listInner);

        var listHeader = new StackPanel { Orientation = Orientation.Horizontal };
        listHeader.Children.Add(new TextBlock
        {
            Text = "\uE77B",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = Brush(UiPalette.Accent),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        listHeader.Children.Add(new TextBlock
        {
            Text = $"我的账号 ({_settings.SavedAccounts.Count})",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text),
            VerticalAlignment = VerticalAlignment.Center
        });
        listInner.Children.Add(listHeader);
        listInner.Children.Add(new TextBlock
        {
            Text = "点击任意账号即可切换为当前账号，互不覆盖",
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 4, 0, 12)
        });

        var accountList = new StackPanel();
        listInner.Children.Add(accountList);
        FillAccountList(accountList);
        root.Children.Add(listCard);

        // ---- 皮肤选择器 ----
        root.Children.Add(BuildSkinSelectorCard());

        // ---- 披风（仅正版账号） ----
        root.Children.Add(BuildCapesCard());

        _accountPageStatus = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(2, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        root.Children.Add(_accountPageStatus);

        _ = LoadAccountSkinAsync();

        // 自动化自检：NEX_SKIN_E2E=1 时模拟「点选皮肤→刷新列表→应用」并输出结果到 %TEMP%\nex_skin_e2e.json
        if (Environment.GetEnvironmentVariable("NEX_SKIN_E2E") == "1")
            _ = Task.Run(async () =>
            {
                await Task.Delay(2500);
                await Dispatcher.InvokeAsync(RunSkinE2E);
                // 等 3D 预览加载完皮肤后离屏截图，验证立体感渲染
                await Task.Delay(3000);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_accountSkinPreview != null) SaveElementPng(_accountSkinPreview, "nex_skin3d.png");
                });
            });

        return root;
    }

    /// <summary>皮肤选择器回归自检：验证刷新列表不会清掉选中项，且应用流程能走通。</summary>
    private void RunSkinE2E()
    {
        var result = new Dictionary<string, object?>();
        try
        {
            var files = Directory.GetFiles(SkinLibraryDir, "*.png").OrderByDescending(File.GetLastWriteTime).ToList();
            result["libraryCount"] = files.Count;
            var target = files.FirstOrDefault(f => f.Contains("offline_")) ?? files.FirstOrDefault();
            result["target"] = target;
            // 模拟缩略图点击：设置选中项后刷新列表（旧 bug 会在刷新时把选中项清空）
            _accountSkinSelected = target;
            _pendingSkinModel = null;
            if (_skinLibraryPanel != null) FillSkinLibrary(_skinLibraryPanel);
            result["selectionAfterRefresh"] = _accountSkinSelected;
            result["bugPresent"] = _accountSkinSelected == null;
            if (!string.IsNullOrEmpty(_accountSkinSelected))
            {
                _pendingSkinModel = "wide"; // 跳过模型询问弹窗，直接走离线账号应用流程
                ApplySelectedSkin();
                result["applied"] = _settings.OfflineSkinPath;
                result["model"] = _settings.OfflineSkinModel;
                result["player"] = _settings.OfflineSkinPlayer;
            }
        }
        catch (Exception ex)
        {
            result["error"] = ex.ToString();
        }
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "nex_skin_e2e.json"),
                System.Text.Json.JsonSerializer.Serialize(result));
        }
        catch { }
    }

    /// <summary>填充账号列表（当前账号高亮，其余可一键切换）。</summary>
    private void FillAccountList(StackPanel host)
    {
        host.Children.Clear();
        var currentKey = _settings.CurrentAccountKey;

        if (_settings.SavedAccounts.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "还没有保存任何账号。点右上角「+ 添加账号」添加离线账号、微软正版或外置登录。",
                FontSize = 12,
                Foreground = Brush(UiPalette.Muted),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(4, 0, 0, 8)
            });
        }

        foreach (var item in _settings.SavedAccounts)
        {
            var isCurrent = item.Key == currentKey;
            var row = new Border
            {
                Background = Brush(isCurrent ? "#16202E" : UiPalette.SurfaceAlt),
                BorderBrush = isCurrent ? Brush(UiPalette.Accent) : Brush(UiPalette.Border),
                BorderThickness = isCurrent ? new Thickness(1.5) : new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 10),
                Cursor = Cursors.Hand
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 头像（异步加载皮肤脸部）
            var avatar = new Border
            {
                Width = 42,
                Height = 42,
                CornerRadius = new CornerRadius(8),
                Background = Brush(UiPalette.Bg),
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Center
            };
            var letter = new TextBlock
            {
                Text = item.Name.Length > 0 ? item.Name[..1].ToUpper() : "?",
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(UiPalette.Accent),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            avatar.Child = letter;
            Grid.SetColumn(avatar, 0);
            grid.Children.Add(avatar);
            _ = LoadAccountRowAvatarAsync(avatar, letter, item);

            // 用户名 + 类型
            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            info.Children.Add(new TextBlock
            {
                Text = item.Name + (isCurrent ? "（当前）" : ""),
                FontSize = 14,
                FontWeight = FontWeights.SemiBold,
                Foreground = Brush(UiPalette.Text)
            });
            var (dotColor, typeText) = item.Type switch
            {
                "microsoft" => ("#4EE0BE", "正版（微软）"),
                "authlib" => ("#C9A2FF", $"外置登录 · {item.ServerName}  {item.ApiRoot}"),
                _ => ("#F6A82A", "离线账号"),
            };
            var typeRow = new StackPanel { Orientation = Orientation.Horizontal };
            typeRow.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Fill = Brush(dotColor),
                Margin = new Thickness(0, 0, 6, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            typeRow.Children.Add(new TextBlock
            {
                Text = typeText,
                FontSize = 11,
                Foreground = Brush(UiPalette.Muted),
                VerticalAlignment = VerticalAlignment.Center
            });
            info.Children.Add(typeRow);
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            // 右侧：切换 / 当前 + 删除
            var btns = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };
            if (!isCurrent)
            {
                var sw = new Button
                {
                    Content = "切换",
                    Padding = new Thickness(14, 6, 14, 6),
                    Background = Brush("#223047"),
                    Foreground = Brush(UiPalette.Text),
                    BorderThickness = new Thickness(0),
                    FontSize = 12,
                    Cursor = Cursors.Hand
                };
                sw.Click += (_, _) => ApplyAccountItem(item);
                btns.Children.Add(sw);
            }
            else
            {
                btns.Children.Add(new Border
                {
                    Background = Brush("#26" + UiPalette.Accent.TrimStart('#')),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(14, 6, 14, 6),
                    Child = new TextBlock
                    {
                        Text = "当前",
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brush(UiPalette.Accent)
                    }
                });
            }
            var del = new Button
            {
                Content = "\uE74D",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 13,
                Padding = new Thickness(9, 6, 9, 6),
                Margin = new Thickness(8, 0, 0, 0),
                Background = Brushes.Transparent,
                Foreground = Brush(UiPalette.Muted),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "删除该账号"
            };
            del.Click += (_, _) => DeleteAccountItem(item);
            btns.Children.Add(del);
            Grid.SetColumn(btns, 2);
            grid.Children.Add(btns);

            row.Child = grid;
            row.MouseLeftButtonUp += (_, _) => { if (!isCurrent) ApplyAccountItem(item); };
            host.Children.Add(row);
        }
    }

    /// <summary>加载账号行头像：离线用本地皮肤裁脸，正版/外置走皮肤服务器。</summary>
    private async Task LoadAccountRowAvatarAsync(Border host, TextBlock letter, SavedAccountItem item)
    {
        try
        {
            BitmapImage? skin = null;
            if (item.Type == "offline")
            {
                if (!string.IsNullOrEmpty(_settings.OfflineSkinPath) && File.Exists(_settings.OfflineSkinPath)
                    && (string.IsNullOrWhiteSpace(_settings.OfflineSkinPlayer)
                        || _settings.OfflineSkinPlayer == item.Name))
                {
                    skin = new BitmapImage();
                    skin.BeginInit();
                    skin.CacheOption = BitmapCacheOption.OnLoad;
                    skin.UriSource = new Uri(_settings.OfflineSkinPath);
                    skin.EndInit();
                    skin.Freeze();
                }
            }
            else if (item.Type == "authlib" && !string.IsNullOrEmpty(item.ApiRoot) && !string.IsNullOrEmpty(item.Uuid))
            {
                var skinUrl = await new Services.AuthlibService().GetSkinUrlAsync(item.ApiRoot, item.Uuid);
                if (!string.IsNullOrEmpty(skinUrl))
                {
                    try
                    {
                        var bytes = await _skinHttp.GetByteArrayAsync(skinUrl);
                        skin = new BitmapImage();
                        skin.BeginInit();
                        skin.CacheOption = BitmapCacheOption.OnLoad;
                        skin.StreamSource = new MemoryStream(bytes);
                        skin.EndInit();
                        skin.Freeze();
                    }
                    catch { }
                }
            }
            BitmapSource? directFace = null;   // 头像服务返回的就是脸，无需裁剪
            if (item.Type == "microsoft" && !string.IsNullOrEmpty(item.Uuid))
            {
                // 先取真实皮肤（会话服务器，国内一般可达），失败再多源取现成头像
                var skinUrl = await GetOfficialSkinUrlAsync(item.Uuid, null);
                if (!string.IsNullOrEmpty(skinUrl)) skin = await LoadRemoteBitmapAsync(skinUrl);
                if (skin == null)
                {
                    foreach (var u in HeadImageUrls(item.Uuid))
                    {
                        directFace = await LoadRemoteBitmapAsync(u);
                        if (directFace != null) break;
                    }
                }
            }

            if (skin == null && directFace == null)
            {
                // 没皮肤：离线账号按玩家名取头像（离线 UUID 查不到正版玩家），
                // 全部网络源失败时用内置官方皮肤裁脸兜底
                string[] urls = item.Type == "offline"
                    ? new[]
                    {
                        $"https://mc-heads.net/avatar/{Uri.EscapeDataString(item.Name)}/104",
                        $"https://minotar.net/helm/{Uri.EscapeDataString(item.Name)}/104.png"
                    }
                    : HeadImageUrls(string.IsNullOrEmpty(item.Uuid) ? item.Name : item.Uuid).ToArray();
                foreach (var u in urls)
                {
                    directFace = await LoadRemoteBitmapAsync(u);
                    if (directFace != null) break;
                }
                if (directFace == null)
                {
                    var builtin = LoadBuiltinSkinSource();
                    if (builtin != null) directFace = CreateFaceBitmap(builtin);
                }
            }

            if (skin == null && directFace == null) return;
            var face = directFace ?? CreateFaceBitmap(skin!);
            await host.Dispatcher.InvokeAsync(() =>
            {
                var img = new Image { Source = face, Stretch = Stretch.UniformToFill, Width = 42, Height = 42 };
                RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
                host.Child = img;
            });
        }
        catch { /* 保留首字母 */ }
    }

    /// <summary>切换当前账号（不覆盖其它已保存账号）。</summary>
    private void ApplyAccountItem(SavedAccountItem item)
    {
        switch (item.Type)
        {
            case "microsoft":
                _session = new MSession
                {
                    Username = item.Name,
                    UUID = item.Uuid,
                    AccessToken = item.AccessToken,
                    UserType = "msa"
                };
                _settings.LastAccountType = "microsoft";
                _settings.MicrosoftSessionName = item.Name;
                _settings.MicrosoftSessionUuid = item.Uuid;
                _settings.MicrosoftSessionToken = item.AccessToken;
                _settings.AuthlibApiRoot = "";
                _settings.AuthlibServerName = "";
                break;
            case "authlib":
                _session = new MSession
                {
                    Username = item.Name,
                    UUID = item.Uuid,
                    AccessToken = item.AccessToken,
                    UserType = "authlib"
                };
                _settings.LastAccountType = "authlib";
                _settings.AuthlibApiRoot = item.ApiRoot;
                _settings.AuthlibServerName = item.ServerName;
                // 确保注入器已下载（后台，不阻塞切换）
                _ = LocalSkinServer.Instance.EnsureAgentAsync();
                break;
            default:
                _session = MinecraftService.CreateOfflineSession(item.Name);
                _settings.LastAccountType = "offline";
                _settings.LastOfflineName = item.Name;
                _settings.AuthlibApiRoot = "";
                _settings.AuthlibServerName = "";
                break;
        }
        _settings.CurrentAccountKey = item.Key;
        _settings.Save();
        UpdateAccountUi();
        ShowPage("账号", true);
    }

    /// <summary>删除某个已保存账号；删的是当前账号则同时退出登录。</summary>
    private void DeleteAccountItem(SavedAccountItem item)
    {
        if (MessageBox.Show(this, $"确定删除账号 \"{item.Name}\" 吗？", "NEX",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var isCurrent = item.Key == _settings.CurrentAccountKey;
        _settings.SavedAccounts.RemoveAll(a => a.Key == item.Key);
        if (isCurrent)
        {
            _session = null;
            _settings.LastAccountType = "";
            _settings.CurrentAccountKey = "";
            _settings.AuthlibApiRoot = "";
            _settings.Save();
            UpdateAccountUi();
        }
        else
        {
            _settings.Save();
        }
        ShowPage("账号", true);
    }

    /// <summary>删除当前账号（顶部按钮）。</summary>
    private void DeleteCurrentAccount_Click()
    {
        if (_session == null)
        {
            MessageBox.Show(this, "当前没有登录账号。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var current = _settings.SavedAccounts.FirstOrDefault(a => a.Key == _settings.CurrentAccountKey);
        if (current != null)
        {
            DeleteAccountItem(current);
            return;
        }
        // 当前账号不在列表里（旧数据）：直接退出登录
        if (MessageBox.Show(this, "退出登录当前账号？", "NEX", MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _session = null;
        _settings.LastAccountType = "";
        _settings.Save();
        UpdateAccountUi();
        ShowPage("账号", true);
    }

    /// <summary>「+ 添加账号」：三选一（离线 / 微软正版 / 外置登录）。</summary>
    private void AddAccount_Click()
    {
        var dlg = new Window
        {
            Title = "添加账号",
            Width = 400,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Owner = this
        };
        var border = new Border
        {
            Background = Brush("#0B0F17"),
            CornerRadius = new CornerRadius(14),
            BorderBrush = Brush(UiPalette.Border),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(24)
        };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "添加账号",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text),
            Margin = new Thickness(0, 0, 0, 12)
        });

        void AddOption(string title, string desc, string color, Action action)
        {
            var b = new Border
            {
                Background = Brush(UiPalette.SurfaceAlt),
                BorderBrush = Brush(UiPalette.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 11, 14, 11),
                Margin = new Thickness(0, 0, 0, 10),
                Cursor = Cursors.Hand
            };
            var col = new StackPanel { Orientation = Orientation.Horizontal };
            col.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 9,
                Height = 9,
                Fill = Brush(color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            var txt = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            txt.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush(UiPalette.Text) });
            txt.Children.Add(new TextBlock { Text = desc, FontSize = 11, Foreground = Brush(UiPalette.Muted) });
            col.Children.Add(txt);
            b.Child = col;
            b.MouseLeftButtonUp += (_, _) => { dlg.Close(); action(); };
            b.MouseEnter += (_, _) => b.Background = Brush("#1B2942");
            b.MouseLeave += (_, _) => b.Background = Brush(UiPalette.SurfaceAlt);
            panel.Children.Add(b);
        }

        AddOption("离线账号", "输入任意用户名，单人游戏与局域网联机", "#F6A82A", () =>
        {
            Offline_Click(this, new RoutedEventArgs());
            if (_session != null && _settings.LastAccountType == "offline") SyncCurrentToAccounts();
            ShowPage("账号", true);
        });
        AddOption("微软正版", "使用 Microsoft / Minecraft 正版账号登录", "#4EE0BE", async () =>
        {
            Login_Click(this, new RoutedEventArgs());
            if (_session != null && _settings.LastAccountType == "microsoft") SyncCurrentToAccounts();
            ShowPage("账号", true);
        });
        AddOption("外置登录（authlib-injector）", "LittleSkin 等 Yggdrasil 服务器账号", "#C9A2FF", () =>
        {
            var login = new AuthlibLoginDialog(_settings) { Owner = this };
            login.ShowDialog();
            if (login.ResultAccount != null)
            {
                UpsertSavedAccount(login.ResultAccount);
                ApplyAccountItem(login.ResultAccount);
            }
        });

        var cancel = new Button
        {
            Content = "取消",
            Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            Background = Brush(UiPalette.SurfaceAlt),
            Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };
        cancel.Click += (_, _) => dlg.Close();
        panel.Children.Add(cancel);

        border.Child = panel;
        dlg.Content = border;
        dlg.ShowDialog();
    }

    /// <summary>把当前登录的账号同步进多账号列表（已存在则更新令牌）。</summary>
    private void SyncCurrentToAccounts()
    {
        if (_session == null) return;
        var type = _settings.LastAccountType;
        var item = type switch
        {
            "microsoft" => new SavedAccountItem
            {
                Type = "microsoft",
                Name = _session.Username ?? "",
                Uuid = _session.UUID ?? "",
                AccessToken = _session.AccessToken ?? "",
                ServerName = ""
            },
            "authlib" => new SavedAccountItem
            {
                Type = "authlib",
                Name = _session.Username ?? "",
                Uuid = _session.UUID ?? "",
                AccessToken = _session.AccessToken ?? "",
                ApiRoot = _settings.AuthlibApiRoot,
                ServerName = _settings.AuthlibServerName
            },
            _ => new SavedAccountItem { Type = "offline", Name = _session.Username ?? "", ServerName = "" },
        };
        UpsertSavedAccount(item, setAsCurrent: true);
    }

    private void UpsertSavedAccount(SavedAccountItem item, bool setAsCurrent = false)
    {
        var existing = _settings.SavedAccounts.FirstOrDefault(a => a.Key == item.Key);
        if (existing != null)
        {
            existing.AccessToken = item.AccessToken;
            existing.Uuid = item.Uuid;
            existing.ApiRoot = item.ApiRoot;
            existing.ServerName = item.ServerName;
        }
        else
        {
            _settings.SavedAccounts.Add(item);
        }
        if (setAsCurrent) _settings.CurrentAccountKey = item.Key;
        _settings.Save();
    }

    /// <summary>皮肤选择器卡片：左侧 3D 预览 + 右侧「已保存皮肤 / 官方皮肤」标签。</summary>
    private UIElement BuildSkinSelectorCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 0);
        var inner = new StackPanel { Margin = new Thickness(16, 14, 16, 16) };
        card.Children.Add(inner);

        var skinHeader = new StackPanel { Orientation = Orientation.Horizontal };
        skinHeader.Children.Add(new TextBlock
        {
            Text = "\uE779",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 14,
            Foreground = Brush(UiPalette.Accent),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        skinHeader.Children.Add(new TextBlock
        {
            Text = "皮肤选择器",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text),
            VerticalAlignment = VerticalAlignment.Center
        });
        inner.Children.Add(skinHeader);
        inner.Children.Add(new TextBlock
        {
            Text = "选择和管理你的游戏皮肤（离线账号本地生效；微软正版上传到 minecraft.net；外置请在皮肤站更换）",
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 4, 0, 12)
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var previewBorder = new Border
        {
            Width = 200,
            Height = 300,
            CornerRadius = new CornerRadius(12),
            Background = Brush(UiPalette.Bg),
            BorderBrush = Brush(UiPalette.Border),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 16, 0)
        };
        _accountSkinPreview = new Skin3DPreview();
        previewBorder.Child = _accountSkinPreview;
        Grid.SetColumn(previewBorder, 0);
        grid.Children.Add(previewBorder);

        var right = new StackPanel();
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);

        // 标签按钮
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        var savedTab = TabButton("已保存皮肤", true);
        var officialTab = TabButton("官方皮肤", false);
        savedTab.Click += (_, _) =>
        {
            _accountSkinSelected = null;
            _pendingSkinModel = null;
            SetTab(savedTab, officialTab, true);
            FillSkinLibrary(_skinLibraryPanel!);
        };
        officialTab.Click += (_, _) =>
        {
            _accountSkinSelected = null;
            _pendingSkinModel = null;
            SetTab(savedTab, officialTab, false);
            FillOfficialSkins(_skinLibraryPanel!);
        };
        tabs.Children.Add(savedTab);
        tabs.Children.Add(officialTab);
        right.Children.Add(tabs);

        _skinLibraryPanel = new WrapPanel();
        right.Children.Add(_skinLibraryPanel);

        // 操作行
        var ops = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var uploadBtn = SmallButton("上传皮肤…");
        uploadBtn.Click += (_, _) => UploadSkinToLibrary();
        ops.Children.Add(uploadBtn);
        var applyBtn = SmallButton("应用到当前账号");
        applyBtn.Click += (_, _) => ApplySelectedSkin();
        ops.Children.Add(applyBtn);
        var delBtn = SmallButton("删除");
        delBtn.Click += (_, _) => DeleteSelectedSkin();
        ops.Children.Add(delBtn);
        right.Children.Add(ops);

        SetTab(savedTab, officialTab, true);
        inner.Children.Add(grid);
        FillSkinLibrary(_skinLibraryPanel);
        return card;
    }

    private static Button TabButton(string text, bool active) => new()
    {
        Content = text,
        Padding = new Thickness(18, 8, 18, 8),
        Margin = new Thickness(0, 0, 8, 0),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Cursor = Cursors.Hand,
        Tag = active,
        Background = active ? Brush(UiPalette.Accent) : Brush(UiPalette.SurfaceAlt),
        Foreground = active ? Brush(UiPalette.Bg) : Brush(UiPalette.TextDim),
        BorderThickness = new Thickness(0)
    };

    private void SetTab(Button saved, Button official, bool savedActive)
    {
        _skinSavedTab = saved;
        _skinOfficialTab = official;
        saved.Background = savedActive ? Brush(UiPalette.Accent) : Brush(UiPalette.SurfaceAlt);
        saved.Foreground = savedActive ? Brush(UiPalette.Bg) : Brush(UiPalette.TextDim);
        official.Background = !savedActive ? Brush(UiPalette.Accent) : Brush(UiPalette.SurfaceAlt);
        official.Foreground = !savedActive ? Brush(UiPalette.Bg) : Brush(UiPalette.TextDim);
    }

    private static Button SmallButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(14, 8, 14, 8),
        Margin = new Thickness(0, 0, 8, 6),
        Background = Brush(UiPalette.SurfaceAlt),
        Foreground = Brush(UiPalette.TextDim),
        BorderThickness = new Thickness(0),
        FontSize = 12,
        Cursor = Cursors.Hand
    };

    private static string SkinLibraryDir => LauncherSettings.SkinLibraryDir;

    /// <summary>「已保存皮肤」标签：列出皮肤库里的 PNG（刷新时保留当前选中项）。
    /// 皮肤库为空时自动补齐 9 套内置官方皮肤，避免被外部清理工具清空后库一直空白。</summary>
    private void FillSkinLibrary(WrapPanel host)
    {
        host.Children.Clear();
        Directory.CreateDirectory(SkinLibraryDir);
        var files = Directory.GetFiles(SkinLibraryDir, "*.png")
            .OrderByDescending(File.GetLastWriteTime)
            .ToList();

        if (files.Count == 0)
        {
            SeedOfficialSkins();
            files = Directory.GetFiles(SkinLibraryDir, "*.png")
                .OrderByDescending(File.GetLastWriteTime)
                .ToList();
        }

        if (files.Count == 0)
        {
            host.Children.Add(new TextBlock
            {
                Text = "皮肤库是空的。点下方「上传皮肤…」把 PNG 皮肤文件加进来。",
                FontSize = 12,
                Foreground = Brush(UiPalette.Muted),
                Margin = new Thickness(4, 0, 0, 0)
            });
            return;
        }
        foreach (var f in files) host.Children.Add(BuildSkinThumb(f));
    }

    /// <summary>把启动器内置的 9 套官方皮肤（wide 版本）写入皮肤库；已有同名文件则跳过。</summary>
    private void SeedOfficialSkins()
    {
        try
        {
            foreach (var (id, name, _) in OfficialSkins)
            {
                var dest = Path.Combine(SkinLibraryDir, $"官方-{name}.png");
                if (File.Exists(dest)) continue;
                var src = new Uri($"pack://application:,,,/Assets/skins/{id}-wide.png");
                var resource = Application.GetResourceStream(src);
                if (resource == null) continue;
                using var s = resource.Stream;
                using var d = File.Create(dest);
                s.CopyTo(d);
            }
        }
        catch { /* 补齐失败不阻塞，仍显示空库提示 */ }
    }

    /// <summary>
    /// 官方默认皮肤：Minecraft 1.21 客户端内置的 9 套（Steve / Alex / Ari / Efe / Kai / Makena / Noor / Sunny / Zuri）。
    /// 贴图已随启动器打包在 Assets/skins 下，离线可用；每套同时带 wide（经典）与 slim（纤细）两种模型。
    /// </summary>
    private static readonly (string Id, string Name, bool SlimDefault)[] OfficialSkins =
    [
        ("steve",  "Steve",  false),
        ("alex",   "Alex",   true),
        ("ari",    "Ari",    true),
        ("efe",    "Efe",    true),
        ("kai",    "Kai",    false),
        ("makena", "Makena", false),
        ("noor",   "Noor",   true),
        ("sunny",  "Sunny",  true),
        ("zuri",   "Zuri",   true)
    ];

    /// <summary>内置皮肤目录（随程序发布）。</summary>
    private static string BuiltinSkinDir => Path.Combine(AppContext.BaseDirectory, "Assets", "skins");

    /// <summary>「官方皮肤」标签：9 套原版默认皮肤，点击即预览，可切换宽/细模型。</summary>
    private void FillOfficialSkins(WrapPanel host)
    {
        host.Children.Clear();
        host.Tag = "official";
        _accountSkinSelected = null;

        foreach (var (id, name, slimDefault) in OfficialSkins)
        {
            var slimPath = Path.Combine(BuiltinSkinDir, $"{id}-slim.png");
            var widePath = Path.Combine(BuiltinSkinDir, $"{id}-wide.png");
            var useSlim = slimDefault;
            var current = useSlim ? slimPath : widePath;
            if (!File.Exists(current)) current = File.Exists(widePath) ? widePath : slimPath;
            if (!File.Exists(current)) continue;   // 极端情况：资源缺失就跳过，不让界面炸

            var tile = new Border
            {
                Width = 92,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(6),
                Background = Brush(UiPalette.SurfaceAlt),
                BorderBrush = Brush(UiPalette.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Cursor = Cursors.Hand,
                ToolTip = $"{name}（{(useSlim ? "纤细" : "经典")}模型）— 点击预览，双击可在宽/细模型间切换"
            };

            var col = new StackPanel();
            var faceHost = new Border
            {
                Width = 64, Height = 64,
                CornerRadius = new CornerRadius(6),
                Background = Brush(UiPalette.Bg),
                HorizontalAlignment = HorizontalAlignment.Center,
                ClipToBounds = true
            };
            var img = new Image { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
            try { img.Source = CreateFaceBitmap(LoadBitmapFile(current)); } catch { }
            faceHost.Child = img;
            col.Children.Add(faceHost);

            var label = new TextBlock
            {
                Text = name,
                FontSize = 10,
                Foreground = Brush(UiPalette.Muted),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0)
            };
            col.Children.Add(label);
            var modelTag = new TextBlock
            {
                Text = useSlim ? "纤细" : "经典",
                FontSize = 9,
                Foreground = Brush(UiPalette.Muted),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            col.Children.Add(modelTag);
            tile.Child = col;

            void Select()
            {
                _accountSkinSelected = current;
                _pendingSkinModel = useSlim ? "slim" : "wide";
                // 高亮：清掉其它块
                foreach (var c in host.Children.OfType<Border>())
                {
                    c.BorderBrush = Brush(UiPalette.Border);
                    c.BorderThickness = new Thickness(1);
                }
                tile.BorderBrush = Brush(UiPalette.Accent);
                tile.BorderThickness = new Thickness(1.5);
                _accountSkinPreview?.LoadSkin(current);
                if (_accountPageStatus != null)
                    _accountPageStatus.Text = $"已选中官方皮肤 {name}（{(useSlim ? "纤细" : "经典")}），点「应用到当前账号」生效。";
            }

            tile.MouseLeftButtonUp += (_, e) =>
            {
                if (e.ClickCount < 2) { Select(); return; }
                // 双击：宽/细模型互换
                var other = useSlim ? widePath : slimPath;
                if (!File.Exists(other)) { Select(); return; }
                useSlim = !useSlim;
                current = other;
                try { img.Source = CreateFaceBitmap(LoadBitmapFile(current)); } catch { }
                modelTag.Text = useSlim ? "纤细" : "经典";
                tile.ToolTip = $"{name}（{(useSlim ? "纤细" : "经典")}模型）— 点击预览，双击可在宽/细模型间切换";
                Select();
            };

            host.Children.Add(tile);
        }
    }

    /// <summary>从磁盘读一张 PNG（OnLoad，避免文件被占用）。</summary>
    private static BitmapImage LoadBitmapFile(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private UIElement BuildSkinThumb(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var thumb = new Border
        {
            Width = 86,
            Margin = new Thickness(0, 0, 10, 10),
            Padding = new Thickness(6),
            Background = Brush(UiPalette.SurfaceAlt),
            BorderBrush = path == _accountSkinSelected ? Brush(UiPalette.Accent) : Brush(UiPalette.Border),
            BorderThickness = path == _accountSkinSelected ? new Thickness(1.5) : new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Cursor = Cursors.Hand,
            ToolTip = name
        };
        var col = new StackPanel();
        var faceHost = new Border
        {
            Width = 64,
            Height = 64,
            CornerRadius = new CornerRadius(6),
            Background = Brush(UiPalette.Bg),
            HorizontalAlignment = HorizontalAlignment.Center,
            ClipToBounds = true
        };
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            var face = new Image { Source = CreateFaceBitmap(bmp), Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(face, BitmapScalingMode.NearestNeighbor);
            faceHost.Child = face;
        }
        catch { }
        col.Children.Add(faceHost);
        col.Children.Add(new TextBlock
        {
            Text = name.Length > 9 ? name[..9] + "…" : name,
            FontSize = 10,
            Foreground = Brush(UiPalette.Muted),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0)
        });
        thumb.Child = col;
        thumb.MouseLeftButtonUp += (_, _) =>
        {
            _accountSkinSelected = path;
            _pendingSkinModel = null;
            // 刷新高亮
            if (_skinLibraryPanel != null && ReferenceEquals(_skinLibraryPanel.Tag, "saved")) FillSkinLibrary(_skinLibraryPanel);
            else if (_skinLibraryPanel != null) FillSkinLibrary(_skinLibraryPanel);
            _accountSkinPreview?.LoadSkin(path);
            if (_accountPageStatus != null) _accountPageStatus.Text = $"已选中皮肤：{name}";
        };
        return thumb;
    }

    private void UploadSkinToLibrary()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择皮肤 PNG 文件",
            Filter = "皮肤图片 (*.png)|*.png"
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            Directory.CreateDirectory(SkinLibraryDir);
            var dest = Path.Combine(SkinLibraryDir, Path.GetFileName(dlg.FileName));
            File.Copy(dlg.FileName, dest, overwrite: true);
            if (_skinLibraryPanel != null) FillSkinLibrary(_skinLibraryPanel);
            if (_accountPageStatus != null) _accountPageStatus.Text = "皮肤已加入皮肤库。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导入失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void ApplySelectedSkin()
    {
        if (string.IsNullOrEmpty(_accountSkinSelected) || !File.Exists(_accountSkinSelected))
        {
            MessageBox.Show(this, "请先在上方点选一个皮肤。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_session == null)
        {
            MessageBox.Show(this, "请先添加并切换到一个账号，再把皮肤应用给它。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // 模型：官方皮肤在选中时已确定；自定义皮肤按文件名猜，猜不到才询问
        var model = _pendingSkinModel;
        if (string.IsNullOrEmpty(model))
        {
            var fn = Path.GetFileNameWithoutExtension(_accountSkinSelected).ToLowerInvariant();
            if (fn.EndsWith("-slim") || fn.Contains("alex") || fn.Contains("slim")) model = "slim";
            else if (fn.EndsWith("-wide") || fn.Contains("steve")) model = "wide";
        }
        if (string.IsNullOrEmpty(model))
        {
            model = MessageBox.Show(this,
                "该皮肤使用纤细模型（Alex，手臂 3 像素宽）吗？\n\n是 = 纤细（Alex）\n否 = 经典（Steve，手臂 4 像素宽）",
                "皮肤模型", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes ? "slim" : "wide";
        }

        var accountType = _settings.LastAccountType;

        // ---- 微软正版：直接上传到 minecraft.net ----
        if (accountType == "microsoft")
        {
            if (string.IsNullOrEmpty(_session.AccessToken))
            {
                MessageBox.Show(this, "正版登录令牌无效，请重新登录微软账号后再试。", "NEX",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (_accountPageStatus != null) _accountPageStatus.Text = "正在把皮肤上传到 minecraft.net...";
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    "https://api.minecraftservices.com/minecraft/profile/skins");
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _session.AccessToken);
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(model == "slim" ? "slim" : "classic"), "variant");
                var bytes = await File.ReadAllBytesAsync(_accountSkinSelected);
                var file = new ByteArrayContent(bytes);
                file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
                form.Add(file, "file", Path.GetFileName(_accountSkinSelected));
                req.Content = form;
                using var resp = await _skinHttp.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    _accountSkinPreview?.LoadSkin(_accountSkinSelected);
                    UpdateAccountUi();
                    if (_accountPageStatus != null)
                        _accountPageStatus.Text = $"已为正版账号 {_session.Username} 上传皮肤（{model}），重新进入游戏生效。";
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    MessageBox.Show(this, $"上传皮肤失败（HTTP {(int)resp.StatusCode}）：{body}\n\n可能是登录令牌过期，请到账号页重新登录微软账号。",
                        "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
                    if (_accountPageStatus != null) _accountPageStatus.Text = "皮肤上传失败。";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "上传皮肤失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
                if (_accountPageStatus != null) _accountPageStatus.Text = "皮肤上传失败。";
            }
            return;
        }

        // ---- 外置登录：皮肤由认证服务器（如 LittleSkin）管理，本地无法覆盖 ----
        if (accountType == "authlib")
        {
            try { Clipboard.SetText(_accountSkinSelected); } catch { }
            MessageBox.Show(this,
                "外置登录账号的皮肤由认证服务器管理，启动器不能直接替换。\n\n" +
                "请打开对应皮肤站的「衣柜 / 角色管理」页面上传这个皮肤文件（文件路径已复制到剪贴板）：\n" +
                _accountSkinSelected + "\n\n上传后重新启动游戏即可生效。",
                "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            if (_accountPageStatus != null) _accountPageStatus.Text = "外置账号请到皮肤站衣柜上传该皮肤（路径已复制）。";
            return;
        }

        // ---- 离线账号：本地皮肤服务器注入 ----
        _settings.OfflineSkinPath = _accountSkinSelected;
        _settings.OfflineSkinModel = model;
        _settings.OfflineSkinPlayer = _session.Username ?? "";
        _settings.Save();
        UpdateAccountUi();
        if (_accountPageStatus != null)
            _accountPageStatus.Text = $"已为 {_session.Username} 应用皮肤（{model}），启动游戏生效，侧栏头像同步更新。";
    }

    private void DeleteSelectedSkin()
    {
        if (string.IsNullOrEmpty(_accountSkinSelected) || !File.Exists(_accountSkinSelected))
        {
            MessageBox.Show(this, "请先在上方点选一个皮肤。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"删除皮肤 \"{Path.GetFileName(_accountSkinSelected)}\"？", "NEX",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            File.Delete(_accountSkinSelected);
            _accountSkinSelected = null;
            if (_skinLibraryPanel != null) FillSkinLibrary(_skinLibraryPanel);
            if (_accountPageStatus != null) _accountPageStatus.Text = "皮肤已删除。";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "删除失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>加载当前账号的皮肤到 3D 预览：正版走官方 API，外置走认证服务器，离线用自定义皮肤。</summary>
    private async Task LoadAccountSkinAsync()
    {
        var preview = _accountSkinPreview;
        if (preview == null) return;

        try
        {
            if (_session != null && _settings.LastAccountType == "authlib" && !string.IsNullOrEmpty(_settings.AuthlibApiRoot))
            {
                if (_accountPageStatus != null) _accountPageStatus.Text = "正在从外置服务器获取当前皮肤...";
                var skinUrl = await new Services.AuthlibService()
                    .GetSkinUrlAsync(_settings.AuthlibApiRoot, _session.UUID ?? "");
                if (!string.IsNullOrEmpty(skinUrl))
                {
                    var bytes = await _skinHttp.GetByteArrayAsync(skinUrl);
                    var bmp = new BitmapImage();
                    using var ms = new MemoryStream(bytes);
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    preview.LoadSkinFromBitmap(bmp);
                    if (_accountPageStatus != null) _accountPageStatus.Text = "已加载外置账号当前皮肤。";
                    return;
                }
                if (_accountPageStatus != null) _accountPageStatus.Text = "未能获取外置皮肤（可能未设置皮肤），显示默认外观。";
                preview.LoadSkin(null);
                return;
            }

            if (_session != null && _settings.LastAccountType == "microsoft" && !string.IsNullOrEmpty(_session.UUID))
            {
                if (_accountPageStatus != null) _accountPageStatus.Text = "正在从官方获取当前皮肤...";
                var url = await GetOfficialSkinUrlAsync(_session.UUID, _session.AccessToken);
                if (!string.IsNullOrEmpty(url))
                {
                    var bmp = await LoadRemoteBitmapAsync(url);
                    if (bmp != null)
                    {
                        preview.LoadSkinFromBitmap(bmp);
                        if (_accountPageStatus != null) _accountPageStatus.Text = "已加载正版账号当前皮肤。";
                        return;
                    }
                }
                if (_accountPageStatus != null) _accountPageStatus.Text = "未能获取正版皮肤（网络受限或令牌过期），显示默认皮肤。";
            }
            else if (!string.IsNullOrEmpty(_settings.OfflineSkinPath))
            {
                preview.LoadSkin(_settings.OfflineSkinPath);
                if (_accountPageStatus != null) _accountPageStatus.Text = $"正在使用自定义皮肤：{Path.GetFileName(_settings.OfflineSkinPath)}";
            }
            else
            {
                preview.LoadSkin(null);
                if (_accountPageStatus != null) _accountPageStatus.Text = "未设置皮肤，显示默认外观。可在下方「皮肤选择器」选择皮肤。";
            }
        }
        catch
        {
            preview.LoadSkin(null);
            if (_accountPageStatus != null) _accountPageStatus.Text = "皮肤加载失败，显示默认外观。";
        }
    }
    #endregion

    private UIElement BuildMultiplayerPage()
    {
        var root = new StackPanel();
        root.Children.Add(Label("多人", 11, UiPalette.Accent));
        root.Children.Add(Heading("联机"));
        root.Children.Add(Body("复制服务器地址，在游戏内多人游戏中粘贴。常规联机与实验性工具分标签展示。"));

        // ---- 标签条：常规联机 / 实验性功能 ----
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _mpTabButtons.Clear();
        string[] tabNames = ["常规联机", "实验性功能"];
        for (var i = 0; i < tabNames.Length; i++)
        {
            var index = i;
            var b = new Button
            {
                Content = tabNames[i],
                Tag = index,
                Padding = new Thickness(16, 8, 16, 8),
                Margin = new Thickness(0, 0, 8, 0),
                FontSize = 13,
                Background = Brushes.Transparent,
                Foreground = Brush(UiPalette.Muted),
                BorderThickness = new Thickness(0, 0, 0, 2),
                Cursor = Cursors.Hand
            };
            b.Click += (_, _) =>
            {
                _mpTab = index;
                RefreshMultiplayerTabs();
            };
            _mpTabButtons.Add(b);
            tabs.Children.Add(b);
        }
        root.Children.Add(tabs);
        root.Children.Add(new Border
        {
            Height = 1,
            Background = Brush(UiPalette.Border),
            Margin = new Thickness(0, 0, 0, 6)
        });

        // ---- 常规联机面板 ----
        var normal = new StackPanel();
        _mpNormalPanel = normal;

        // 三步快速联机指引
        var guideCard = Card();
        guideCard.Margin = new Thickness(0, 12, 0, 0);
        var guideInner = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        guideInner.Children.Add(Label("最快联机方式（三步）", 13, UiPalette.Text, FontWeights.SemiBold));
        guideInner.Children.Add(Body("① 点下方“一键开服”，选版本 → 自动下载服务端并启动；"));
        guideInner.Children.Add(Body("② 等待自动开启公网隧道，拿到公网地址（已自动复制）；"));
        guideInner.Children.Add(Body("③ 把地址发给朋友，对方在游戏“多人游戏 → 添加服务器”粘贴即可进入。"));
        guideCard.Children.Add(guideInner);
        normal.Children.Add(guideCard);

        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 0);
        var addr = new TextBox
        {
            Text = "127.0.0.1:25565",
            FontSize = 14,
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 8, 0, 0),
            Background = Brush(UiPalette.Bg),
            Foreground = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border)
        };
        card.Children.Add(addr);
        card.Children.Add(ActionButton("复制地址", (_, _) =>
        {
            Clipboard.SetText(addr.Text.Trim());
            MessageBox.Show("已复制。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
        }));
        normal.Children.Add(card);

        normal.Children.Add(BuildOneClickCard());
        normal.Children.Add(BuildTunnelCard());
        normal.Children.Add(BuildServerCard());
        root.Children.Add(normal);

        // ---- 实验性功能面板 ----
        var exp = BuildExperimentalPanel();
        exp.Visibility = Visibility.Collapsed;
        _mpExpPanel = exp;
        root.Children.Add(exp);

        RefreshMultiplayerTabs();
        return root;
    }

    /// <summary>联机页标签选中态刷新：只切换可见性，保留一键开服等控件的运行状态。</summary>
    private void RefreshMultiplayerTabs()
    {
        for (var i = 0; i < _mpTabButtons.Count; i++)
        {
            var b = _mpTabButtons[i];
            var active = i == _mpTab;
            b.Foreground = Brush(active ? UiPalette.Accent : UiPalette.Muted);
            b.BorderBrush = Brush(active ? UiPalette.Accent : "Transparent");
            b.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
        if (_mpNormalPanel != null)
            _mpNormalPanel.Visibility = _mpTab == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_mpExpPanel != null)
            _mpExpPanel.Visibility = _mpTab == 1 ? Visibility.Visible : Visibility.Collapsed;

        // 进入实验性页时按需刷新一次环境信息
        if (_mpTab == 1) _ = RefreshExperimentalSnapshotAsync();
    }

    #region 一键开服
    private UIElement BuildOneClickCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 0);
        card.Children.Add(Label("一键开服（公网联机）", 13, UiPalette.Text, FontWeights.SemiBold));
        card.Children.Add(Body("自动下载官方服务端 → 自动生成配置（离线模式 / 生存）→ 启动服务器 → 开启公网隧道。"
                            + "启动完成后，把公网地址发给朋友即可直接进入。"));

        var optRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };

        optRow.Children.Add(new TextBlock
        {
            Text = "版本", FontSize = 12, Foreground = Brush(UiPalette.Muted),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        });
        _oneClickVersionBox = new ComboBox
        {
            Width = 130, FontSize = 12, Margin = new Thickness(0, 0, 12, 0),
            Background = Brush(UiPalette.Bg), Foreground = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border)
        };
        optRow.Children.Add(_oneClickVersionBox);

        optRow.Children.Add(new TextBlock
        {
            Text = "内存", FontSize = 12, Foreground = Brush(UiPalette.Muted),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        });
        _oneClickRamBox = new ComboBox
        {
            Width = 80, FontSize = 12, Margin = new Thickness(0, 0, 12, 0),
            Background = Brush(UiPalette.Bg), Foreground = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border),
            ItemsSource = new[] { "2G", "4G", "6G", "8G" },
            SelectedIndex = 0
        };
        optRow.Children.Add(_oneClickRamBox);

        _oneClickOfflineBox = new CheckBox
        {
            Content = "离线模式（允许非正版加入）",
            IsChecked = true,
            FontSize = 12,
            Foreground = Brush(UiPalette.TextDim),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0)
        };
        optRow.Children.Add(_oneClickOfflineBox);

        _oneClickButton = new Button
        {
            Content = "一键开服",
            Padding = new Thickness(18, 8, 18, 8),
            Background = Brush(UiPalette.Accent), Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand, FontSize = 12
        };
        _oneClickButton.Click += async (_, _) => await ToggleOneClickServerAsync();
        optRow.Children.Add(_oneClickButton);
        card.Children.Add(optRow);

        _oneClickStatus = new TextBlock
        {
            Text = "状态：未启动",
            Foreground = Brush(UiPalette.Muted), FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 2)
        };
        card.Children.Add(_oneClickStatus);

        var addrRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 6) };
        _oneClickAddressText = new TextBlock
        {
            Text = "公网地址：-",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Accent), VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        };
        addrRow.Children.Add(_oneClickAddressText);

        _oneClickClaimBtn = new Button
        {
            Content = "去网页授权",
            Padding = new Thickness(12, 5, 12, 5),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.Accent),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed
        };
        _oneClickClaimBtn.Click += (_, _) => OpenClaimPage();
        addrRow.Children.Add(_oneClickClaimBtn);

        var copyClaimBtn1 = new Button
        {
            Content = "复制授权链接",
            Padding = new Thickness(12, 5, 12, 5),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "把 playit.gg 授权链接复制到剪贴板，可在任意浏览器打开（网页报错时可稍后重试或换浏览器）"
        };
        copyClaimBtn1.Click += (_, _) => CopyClaimUrl();
        addrRow.Children.Add(copyClaimBtn1);
        _oneClickCopyClaimBtn = copyClaimBtn1;

        var copyAddrBtn = new Button
        {
            Content = "复制地址",
            Padding = new Thickness(12, 5, 12, 5),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12
        };
        copyAddrBtn.Click += (_, _) => CopyPublicAddress();
        addrRow.Children.Add(copyAddrBtn);
        card.Children.Add(addrRow);

        _oneClickLog = new TextBox
        {
            IsReadOnly = true,
            Height = 150,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Background = Brush(UiPalette.Bg),
            Foreground = Brush(UiPalette.Muted),
            BorderBrush = Brush(UiPalette.Border),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        card.Children.Add(_oneClickLog);

        // ---- 服务器操作台：输入命令（回车或点发送），支持 ↑↓ 翻历史 ----
        var consoleRow = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        consoleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        consoleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _oneClickCmdBox = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Background = Brush(UiPalette.Bg),
            Foreground = Brush(UiPalette.Text),
            CaretBrush = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border),
            Padding = new Thickness(8, 6, 8, 6),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _oneClickCmdBox.KeyDown += OneClickCmdBox_KeyDown;
        consoleRow.Children.Add(_oneClickCmdBox);
        var sendBtn = new Button
        {
            Content = "发送",
            Padding = new Thickness(16, 6, 16, 6),
            Background = Brush(UiPalette.Accent),
            Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(sendBtn, 1);
        sendBtn.Click += (_, _) => SendOneClickCommand();
        consoleRow.Children.Add(sendBtn);
        card.Children.Add(consoleRow);
        card.Children.Add(new TextBlock
        {
            Text = "操作台：输入服务器命令（如 op 玩家名、gamemode creative 玩家名、say 大家好、stop），回车发送，↑↓ 翻历史。",
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 4, 0, 0)
        });

        HookOneClickEvents();
        _ = LoadOneClickVersionsAsync();
        return card;
    }

    private void HookOneClickEvents()
    {
        if (_oneClickHooked) return;
        _oneClickHooked = true;

        _oneClickService.LogReceived += line => Dispatcher.Invoke(() =>
        {
            if (_oneClickLog == null) return;
            _oneClickLog.AppendText(line + Environment.NewLine);
            if (_oneClickLog.LineCount > 300)
            {
                var text = _oneClickLog.Text;
                var cut = text.Length - 20000 > 0 ? text.Length - 20000 : 0;
                _oneClickLog.Text = text[(text.IndexOf('\n', cut) + 1)..];
            }
            _oneClickLog.ScrollToEnd();
        });

        _oneClickService.ServerReady += () => Dispatcher.Invoke(async () =>
        {
            if (_oneClickStatus != null) _oneClickStatus.Text = "状态：服务器已就绪，正在开启公网隧道...";
            await StartTunnelForServerAsync();
        });

        _oneClickService.Exited += () => Dispatcher.Invoke(() =>
        {
            if (_oneClickStatus != null) _oneClickStatus.Text = "状态：服务器已停止";
            if (_oneClickButton != null) { _oneClickButton.Content = "一键开服"; _oneClickButton.IsEnabled = true; }
        });
    }

    /// <summary>操作台输入框按键：回车发送，↑↓ 翻历史命令。</summary>
    private void OneClickCmdBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_oneClickCmdBox == null) return;
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            SendOneClickCommand();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Up)
        {
            if (_oneClickCmdHistory.Count == 0) return;
            if (_oneClickCmdHistoryIndex < 0) _oneClickCmdHistoryIndex = _oneClickCmdHistory.Count - 1;
            else if (_oneClickCmdHistoryIndex > 0) _oneClickCmdHistoryIndex--;
            _oneClickCmdBox.Text = _oneClickCmdHistory[_oneClickCmdHistoryIndex];
            _oneClickCmdBox.CaretIndex = _oneClickCmdBox.Text.Length;
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Down)
        {
            if (_oneClickCmdHistoryIndex < 0) return;
            _oneClickCmdHistoryIndex++;
            if (_oneClickCmdHistoryIndex >= _oneClickCmdHistory.Count)
            {
                _oneClickCmdHistoryIndex = -1;
                _oneClickCmdBox.Text = "";
            }
            else
            {
                _oneClickCmdBox.Text = _oneClickCmdHistory[_oneClickCmdHistoryIndex];
                _oneClickCmdBox.CaretIndex = _oneClickCmdBox.Text.Length;
            }
            e.Handled = true;
        }
    }

    /// <summary>把操作台输入的命令发给服务器（自动去掉开头的 /，空命令忽略）。</summary>
    private void SendOneClickCommand()
    {
        if (_oneClickCmdBox == null) return;
        var cmd = _oneClickCmdBox.Text.Trim();
        if (cmd.Length == 0) return;
        if (cmd.StartsWith('/')) cmd = cmd[1..];

        if (!_oneClickService.IsRunning)
        {
            _oneClickLog?.AppendText("[NEX] 服务器未在运行，无法发送命令。" + Environment.NewLine);
            _oneClickLog?.ScrollToEnd();
            return;
        }

        _oneClickLog?.AppendText("> " + cmd + Environment.NewLine);
        _oneClickLog?.ScrollToEnd();
        _oneClickService.SendCommand(cmd);

        if (_oneClickCmdHistory.Count == 0 || _oneClickCmdHistory[^1] != cmd)
        {
            _oneClickCmdHistory.Add(cmd);
            if (_oneClickCmdHistory.Count > 50) _oneClickCmdHistory.RemoveAt(0);
        }
        _oneClickCmdHistoryIndex = -1;
        _oneClickCmdBox.Text = "";
        _oneClickCmdBox.Focus();
    }

    private async Task LoadOneClickVersionsAsync()
    {
        try
        {
            var versions = await _oneClickService.GetReleaseVersionsAsync(30);
            if (_oneClickVersionBox == null) return;
            _oneClickVersionBox.ItemsSource = versions;
            _oneClickVersionBox.SelectedIndex = 0;
        }
        catch
        {
            if (_oneClickVersionBox != null)
            {
                _oneClickVersionBox.ItemsSource = new[] { "1.21.1" };
                _oneClickVersionBox.SelectedIndex = 0;
            }
        }
    }

    private async Task ToggleOneClickServerAsync()
    {
        if (_oneClickService.IsRunning)
        {
            if (_oneClickStatus != null) _oneClickStatus.Text = "状态：正在停止服务器...";
            await Task.Run(() => _oneClickService.Stop());
            return;
        }

        var version = _oneClickVersionBox?.SelectedItem as string;
        if (string.IsNullOrEmpty(version))
        {
            MessageBox.Show("请先选择服务端版本。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (OneClickServerService.FindJava() == null)
        {
            MessageBox.Show("未检测到 Java。请先安装 Java 21 或更高版本后再开服。",
                "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _oneClickButton!.IsEnabled = false;
        _oneClickButton.Content = "启动中...";
        _oneClickLog?.Clear();

        var ramText = _oneClickRamBox?.SelectedItem as string ?? "2G";
        var ramMb = int.Parse(ramText.TrimEnd('G')) * 1024;
        var offline = _oneClickOfflineBox?.IsChecked == true;

        try
        {
            void Report(string msg)
            {
                if (_oneClickStatus != null) _oneClickStatus.Text = "状态：" + msg;
            }

            var dir = _oneClickService.PrepareServerDirectory(
                _gameDirectory, offline, maxPlayers: 10, motd: $"NEX Server {version}");

            string jar = Path.Combine(dir, "server.jar");
            if (!File.Exists(jar))
                await _oneClickService.DownloadServerJarAsync(version, dir, Report);
            else
                Report("已存在服务端文件，跳过下载。");

            Report($"正在启动 {version} 服务端（内存 {ramText}）...");
            _oneClickService.Start(dir, "server.jar", ramMb);

            if (_oneClickButton != null) _oneClickButton.Content = "停止服务器";
        }
        catch (Exception ex)
        {
            if (_oneClickStatus != null) _oneClickStatus.Text = "开服失败：" + ex.Message;
            if (_oneClickButton != null) { _oneClickButton.Content = "一键开服"; }
        }
        finally
        {
            if (_oneClickButton != null) _oneClickButton.IsEnabled = true;
        }
    }

    /// <summary>服务器就绪后自动开启公网隧道，拿到公网地址。</summary>
    private async Task StartTunnelForServerAsync()
    {
        if (_playitService.IsRunning)
        {
            if (_oneClickStatus != null && _playitService.CurrentAddress != null)
                _oneClickStatus.Text = $"状态：服务器运行中，公网地址 {_playitService.CurrentAddress}";
            return;
        }

        var ready = await _playitService.EnsureAgentAsync(msg =>
            Dispatcher.Invoke(() => { if (_oneClickStatus != null) _oneClickStatus.Text = "状态：" + msg; }));
        if (!ready) return;

        _playitService.Start();
        if (_tunnelStatus != null) _tunnelStatus.Text = "状态：运行中（等待分配地址...）";
        if (_tunnelButton != null) _tunnelButton.Content = "停止隧道";

        if (_oneClickStatus != null)
            _oneClickStatus.Text = "状态：服务器运行中，正在等待公网地址（首次使用请点“去网页授权”）";
    }

    private void CopyPublicAddress()
    {
        var addr = _playitService.CurrentAddress;
        if (string.IsNullOrEmpty(addr))
        {
            if (_playitService.ClaimUrl != null)
            {
                if (MessageBox.Show("隧道已启动，但还差最后一步：需要在浏览器里完成 playit.gg 网页授权才能分配公网地址。\n\n现在打开授权页面吗？",
                        "NEX", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    OpenClaimPage();
                }
            }
            else if (!_playitService.IsRunning)
            {
                MessageBox.Show("隧道未启动。请先点击“一键开服”或隧道卡片的“启动隧道”。",
                    "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("已授权，正在等待 playit.gg 分配公网地址（一般 10 秒内）。\n如果超过一分钟还没出现，请停止隧道后重新启动一次。",
                    "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }
        Clipboard.SetText(addr);
        MessageBox.Show($"已复制公网地址：{addr}\n把它发给朋友，在游戏里“添加服务器”粘贴即可。",
            "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenClaimPage()
    {
        var url = _playitService.ClaimUrl;
        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show("还没有授权链接，请先启动隧道。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // 优先用内置窗口打开：系统浏览器（尤其 Edge）的自动翻译会让 playit.gg 页面
        // 报「Failed to execute 'removeChild' on 'Node'」而无法授权；WebView2 不会自动翻译。
        try
        {
            var win = new ClaimWindow(url) { Owner = this };
            win.Show();
            return;
        }
        catch (TypeInitializationException)
        {
            // WebView2 组件不可用，走系统浏览器兜底
        }
        catch (Exception ex)
        {
            MessageBox.Show("内置授权窗口打开失败（" + ex.Message + "），改用系统浏览器。", "NEX",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show("打开浏览器失败：" + ex.Message + "\n\n授权链接已复制，可以手动粘贴到浏览器打开。", "NEX",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            try { Clipboard.SetText(url); } catch { }
        }
    }
    #endregion

    #region Playit 隧道
    private UIElement BuildTunnelCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 0);
        card.Children.Add(Label("内网穿透隧道", 13, UiPalette.Text, FontWeights.SemiBold));
        card.Children.Add(Body("没有公网 IP 也能让朋友加入你的游戏（由 playit.gg 提供，首次使用自动下载 agent）。"));

        _tunnelStatus = new TextBlock
        {
            Text = _playitService.IsRunning ? "状态：运行中" : "状态：未启动",
            Foreground = Brush(UiPalette.Muted), FontSize = 12, Margin = new Thickness(0, 4, 0, 4)
        };
        card.Children.Add(_tunnelStatus);

        _tunnelAddressText = new TextBlock
        {
            Text = _playitService.CurrentAddress != null ? $"对外地址：{_playitService.CurrentAddress}" : "对外地址：-",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Accent), Margin = new Thickness(0, 0, 0, 8)
        };
        card.Children.Add(_tunnelAddressText);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        _tunnelButton = new Button
        {
            Content = _playitService.IsRunning ? "停止隧道" : "启动隧道",
            Padding = new Thickness(16, 8, 16, 8),
            Background = Brush(UiPalette.Accent), Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0)
        };
        _tunnelButton.Click += async (_, _) => await ToggleTunnelAsync();
        btnRow.Children.Add(_tunnelButton);

        var copyBtn = new Button
        {
            Content = "复制对外地址",
            Padding = new Thickness(14, 8, 14, 8),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        copyBtn.Click += (_, _) =>
        {
            if (_playitService.CurrentAddress != null)
            {
                Clipboard.SetText(_playitService.CurrentAddress);
                MessageBox.Show("已复制对外地址，发给朋友即可加入。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else MessageBox.Show("隧道还没有分配地址，请先启动隧道。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        btnRow.Children.Add(copyBtn);

        _tunnelClaimBtn = new Button
        {
            Content = "去网页授权",
            Padding = new Thickness(14, 8, 14, 8),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.Accent),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        _tunnelClaimBtn.Click += (_, _) => OpenClaimPage();
        btnRow.Children.Add(_tunnelClaimBtn);

        var copyClaimBtn2 = new Button
        {
            Content = "复制授权链接",
            Padding = new Thickness(14, 8, 14, 8),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            Margin = new Thickness(8, 0, 0, 0),
            Visibility = Visibility.Collapsed,
            ToolTip = "把 playit.gg 授权链接复制到剪贴板，可在任意浏览器打开（网页报错时可稍后重试或换浏览器）"
        };
        copyClaimBtn2.Click += (_, _) => CopyClaimUrl();
        btnRow.Children.Add(copyClaimBtn2);
        _tunnelCopyClaimBtn = copyClaimBtn2;
        card.Children.Add(btnRow);

        _tunnelLog = new TextBox
        {
            IsReadOnly = true,
            Height = 120,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Background = Brush(UiPalette.Bg),
            Foreground = Brush(UiPalette.Muted),
            BorderBrush = Brush(UiPalette.Border),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.Wrap
        };
        card.Children.Add(_tunnelLog);

        HookPlayitEvents();
        return card;
    }

    private void HookPlayitEvents()
    {
        if (_playitHooked) return;
        _playitHooked = true;
        _playitService.LogReceived += line => Dispatcher.Invoke(() =>
        {
            if (_tunnelLog == null) return;
            _tunnelLog.AppendText(line + Environment.NewLine);
            // 日志最多保留 200 行，避免无限增长
            if (_tunnelLog.LineCount > 200)
            {
                var text = _tunnelLog.Text;
                _tunnelLog.Text = text[(text.IndexOf('\n', text.Length - 12000 > 0 ? text.Length - 12000 : 0) + 1)..];
            }
            _tunnelLog.ScrollToEnd();
        });
        _playitService.AddressDetected += address => Dispatcher.Invoke(() =>
        {
            if (_tunnelAddressText != null) _tunnelAddressText.Text = $"对外地址：{address}";
            if (_oneClickAddressText != null) _oneClickAddressText.Text = $"公网地址：{address}";
            if (_oneClickStatus != null && _oneClickService.IsRunning)
                _oneClickStatus.Text = $"状态：服务器运行中，公网地址 {address}（发给朋友即可加入）";
            if (_oneClickClaimBtn != null) _oneClickClaimBtn.Visibility = Visibility.Collapsed;
            if (_tunnelClaimBtn != null) _tunnelClaimBtn.Visibility = Visibility.Collapsed;
            if (_tunnelCopyClaimBtn != null) _tunnelCopyClaimBtn.Visibility = Visibility.Collapsed;
            if (_oneClickCopyClaimBtn != null) _oneClickCopyClaimBtn.Visibility = Visibility.Collapsed;
            // 自动复制公网地址，方便直接发给朋友
            if (_settings.AutoCopyAddress)
            {
                try
                {
                    Clipboard.SetText(address);
                    if (_oneClickStatus != null && _oneClickService.IsRunning)
                        _oneClickStatus.Text = $"状态：服务器运行中，公网地址 {address}（已自动复制，直接发给朋友即可）";
                }
                catch { /* 剪贴板占用时忽略 */ }
            }
        });
        _playitService.ClaimUrlDetected += url => Dispatcher.Invoke(() =>
        {
            if (_tunnelClaimBtn != null) _tunnelClaimBtn.Visibility = Visibility.Visible;
            if (_oneClickClaimBtn != null) _oneClickClaimBtn.Visibility = Visibility.Visible;
            if (_tunnelCopyClaimBtn != null) _tunnelCopyClaimBtn.Visibility = Visibility.Visible;
            if (_oneClickCopyClaimBtn != null) _oneClickCopyClaimBtn.Visibility = Visibility.Visible;
            if (_tunnelStatus != null) _tunnelStatus.Text = "状态：需要网页授权（点右侧“去网页授权”）";
            // 授权链接自动复制：playit.gg 网页偶尔报错，方便用户复制到别的浏览器重开
            if (_settings.AutoCopyAddress)
            {
                try { Clipboard.SetText(url); } catch { }
            }
        });
        _playitService.Exited += () => Dispatcher.Invoke(() =>
        {
            if (_tunnelStatus != null) _tunnelStatus.Text = "状态：已退出（网络中断或 agent 异常，可重新启动隧道）";
            if (_tunnelButton != null) { _tunnelButton.Content = "启动隧道"; _tunnelButton.IsEnabled = true; }
            if (_tunnelCopyClaimBtn != null) _tunnelCopyClaimBtn.Visibility = Visibility.Collapsed;
            if (_oneClickCopyClaimBtn != null) _oneClickCopyClaimBtn.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>复制 playit.gg 授权链接到剪贴板。</summary>
    private void CopyClaimUrl()
    {
        var url = _playitService.ClaimUrl;
        if (string.IsNullOrEmpty(url))
        {
            MessageBox.Show("还没有授权链接，请先启动隧道。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            Clipboard.SetText(url);
            if (_tunnelStatus != null) _tunnelStatus.Text = "状态：授权链接已复制，请在浏览器打开完成授权";
            MessageBox.Show("授权链接已复制到剪贴板：\n" + url +
                "\n\n在浏览器打开并登录 playit.gg 完成授权。" +
                "\n如果页面报错，稍后重新打开一次即可（这是 playit.gg 网页偶发问题）。",
                "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch { /* 剪贴板占用时忽略 */ }
    }

    private async Task ToggleTunnelAsync()
    {
        if (_playitService.IsRunning)
        {
            _playitService.Stop();
            if (_tunnelStatus != null) _tunnelStatus.Text = "状态：未启动";
            if (_tunnelAddressText != null) _tunnelAddressText.Text = "对外地址：-";
            if (_tunnelButton != null) _tunnelButton.Content = "启动隧道";
            return;
        }

        if (_tunnelButton != null) { _tunnelButton.IsEnabled = false; _tunnelButton.Content = "准备中..."; }
        var ready = await _playitService.EnsureAgentAsync(msg =>
            Dispatcher.Invoke(() => { if (_tunnelStatus != null) _tunnelStatus.Text = msg; }));
        if (!ready)
        {
            if (_tunnelButton != null) { _tunnelButton.IsEnabled = true; _tunnelButton.Content = "启动隧道"; }
            return;
        }
        _playitService.Start();
        if (_tunnelStatus != null) _tunnelStatus.Text = "状态：运行中（等待分配地址...）";
        if (_tunnelButton != null) { _tunnelButton.IsEnabled = true; _tunnelButton.Content = "停止隧道"; }
    }
    #endregion

    #region 服务端管理
    private UIElement BuildServerCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 0);
        card.Children.Add(Label("服务端管理", 13, UiPalette.Text, FontWeights.SemiBold));
        card.Children.Add(Body("把服务端整合包（.zip）拖到下方区域，自动解压、写 eula、生成开服脚本；也可点击浏览选择文件。"));

        var dropZone = new Border
        {
            AllowDrop = true,
            BorderBrush = Brush(UiPalette.Accent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Background = Brush(UiPalette.Header),
            Padding = new Thickness(16, 20, 16, 20),
            Margin = new Thickness(0, 8, 0, 8),
            Child = new TextBlock
            {
                Text = "拖拽服务端整合包（.zip）到这里",
                FontSize = 13, Foreground = Brush(UiPalette.Muted),
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        dropZone.DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        dropZone.Drop += async (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                await ImportServerPackageAsync(files[0]);
        };
        card.Children.Add(dropZone);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
        var browseBtn = new Button
        {
            Content = "浏览整合包...",
            Padding = new Thickness(14, 7, 14, 7),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.Text),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0)
        };
        browseBtn.Click += async (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "服务端整合包|*.zip", Title = "选择服务端整合包" };
            if (dlg.ShowDialog() == true) await ImportServerPackageAsync(dlg.FileName);
        };
        btnRow.Children.Add(browseBtn);

        var openRootBtn = new Button
        {
            Content = "打开服务端目录",
            Padding = new Thickness(14, 7, 14, 7),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        openRootBtn.Click += (_, _) => Process.Start(new ProcessStartInfo
        {
            FileName = _serverDirectoryService.ServersRoot, UseShellExecute = true
        });
        btnRow.Children.Add(openRootBtn);
        card.Children.Add(btnRow);

        _serverStatus = new TextBlock
        {
            Foreground = Brush(UiPalette.Muted), FontSize = 12,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4)
        };
        card.Children.Add(_serverStatus);

        _serverListPanel = new StackPanel();
        card.Children.Add(_serverListPanel);
        RefreshServerList();
        return card;
    }

    private async Task ImportServerPackageAsync(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("目前仅支持 .zip 格式的服务端整合包。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_serverStatus != null) _serverStatus.Text = "导入中...";
        try
        {
            var server = await Task.Run(() => _serverDirectoryService.ImportPackage(path,
                msg => Dispatcher.Invoke(() => { if (_serverStatus != null) _serverStatus.Text = msg; })));
            if (_serverStatus != null) _serverStatus.Text = $"已导入：{server.Name}";
            RefreshServerList();
        }
        catch (Exception ex)
        {
            if (_serverStatus != null) _serverStatus.Text = "导入失败：" + ex.Message;
        }
    }

    private void RefreshServerList()
    {
        if (_serverListPanel == null) return;
        _serverListPanel.Children.Clear();
        var servers = _serverDirectoryService.ListServers();
        if (servers.Count == 0)
        {
            _serverListPanel.Children.Add(new TextBlock
            {
                Text = "还没有服务端实例。",
                FontSize = 12, Foreground = Brush(UiPalette.Muted),
                Margin = new Thickness(0, 4, 0, 4)
            });
            return;
        }
        foreach (var s in servers)
        {
            var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };

            var startBtn = new Button
            {
                Content = "启动",
                Padding = new Thickness(16, 6, 16, 6),
                Background = Brush(UiPalette.Accent), Foreground = Brush(UiPalette.Bg),
                BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand, FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0)
            };
            startBtn.Click += (_, _) =>
            {
                try { _serverDirectoryService.StartServer(s); }
                catch (Exception ex)
                {
                    MessageBox.Show($"启动失败：\n{ex.Message}", "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            DockPanel.SetDock(startBtn, Dock.Right);
            row.Children.Add(startBtn);

            var openBtn = new Button
            {
                Content = "目录",
                Padding = new Thickness(12, 6, 12, 6),
                Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0)
            };
            openBtn.Click += (_, _) => Process.Start(new ProcessStartInfo
            {
                FileName = s.Directory, UseShellExecute = true
            });
            DockPanel.SetDock(openBtn, Dock.Right);
            row.Children.Add(openBtn);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = s.Name, FontSize = 13, FontWeight = FontWeights.SemiBold,
                Foreground = Brush(UiPalette.Text)
            });
            info.Children.Add(new TextBlock
            {
                Text = s.JarFile != null ? $"服务端核心：{s.JarFile}" : "未找到服务端 jar，请手动放入",
                FontSize = 11, Foreground = Brush(UiPalette.Muted)
            });
            row.Children.Add(info);
            _serverListPanel.Children.Add(row);
        }
    }
    #endregion


    #region 更新
    private readonly UpdateService _updateService = new();

    /// <summary>启动时静默检查更新：发现新版本弹窗询问，可选择不更新。</summary>
    private async Task CheckUpdateOnStartupAsync()
    {
        if (!_settings.AutoCheckUpdate || string.IsNullOrWhiteSpace(_settings.UpdateManifestUrl))
            return;
        try
        {
            var info = await _updateService.CheckForUpdateAsync(_settings.UpdateManifestUrl);
            if (info == null) return;
            var msg = $"发现新版本 v{info.Version}（当前 v{AppInfo.Version}）";
            if (!string.IsNullOrWhiteSpace(info.Notes)) msg += $"\n\n{info.Notes}";
            msg += "\n\n是否立即下载并安装？";
            if (MessageBox.Show(msg, "启动器更新", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                return;
            await DownloadAndApplyUpdateAsync(info);
        }
        catch { /* 启动时检查失败不打扰用户 */ }
    }

    /// <summary>下载更新包（带进度窗口）并应用，应用后自动重启。</summary>
    private async Task DownloadAndApplyUpdateAsync(UpdateInfo info)
    {
        var win = new Window
        {
            Title = "正在更新启动器",
            Width = 400, Height = 150,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            WindowStyle = WindowStyle.None, AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false
        };
        var border = new System.Windows.Controls.Border
        {
            Background = Brush(UiPalette.Header), CornerRadius = new CornerRadius(12),
            BorderBrush = Brush(UiPalette.Border), BorderThickness = new Thickness(1)
        };
        var stack = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        stack.Children.Add(new TextBlock
        {
            Text = $"正在下载 v{info.Version} ...", FontSize = 13,
            Foreground = Brush(UiPalette.Text), Margin = new Thickness(0, 0, 0, 10)
        });
        var bar = new System.Windows.Controls.ProgressBar
        {
            Minimum = 0, Maximum = 100, Height = 6,
            Foreground = Brush(UiPalette.Accent), Background = Brush(UiPalette.SurfaceAlt),
            BorderThickness = new Thickness(0)
        };
        stack.Children.Add(bar);
        var detail = new TextBlock { Text = "准备中...", FontSize = 11, Foreground = Brush(UiPalette.Muted), Margin = new Thickness(0, 8, 0, 0) };
        stack.Children.Add(detail);
        border.Child = stack;
        win.Content = border;
        win.Show();

        var progress = new Progress<(long Received, long Total)>(t =>
        {
            if (t.Total > 0) bar.Value = (double)t.Received / t.Total * 100;
            detail.Text = $"{FormatBytes(t.Received)} / {FormatBytes(t.Total)}";
        });
        try
        {
            var zip = await _updateService.DownloadAsync(info, progress);
            detail.Text = "正在应用更新...";
            _updateService.ApplyUpdate(zip); // 内部会关闭当前程序并自动重启
        }
        catch (Exception ex)
        {
            win.Close();
            MessageBox.Show($"更新失败：\n{ex.Message}", "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    #endregion

    private UIElement BuildSettingsPage()
    {
        var root = new StackPanel();
        root.Children.Add(Label("偏好", 11, UiPalette.Accent));
        root.Children.Add(Heading("设置"));

        // ---- 常规 ----
        var dirCard = Card();
        dirCard.Margin = new Thickness(0, 22, 0, 0);
        dirCard.Children.Add(Label("游戏目录", 13, UiPalette.Text, FontWeights.SemiBold));
        dirCard.Children.Add(Body(_gameDirectory));
        dirCard.Children.Add(ActionButton("打开游戏目录", (_, _) => OpenGameDirectory()));
        root.Children.Add(dirCard);

        // ---- 登录 ----
        var loginCard = Card();
        loginCard.Margin = new Thickness(0, 14, 0, 0);
        loginCard.Children.Add(Label("登录", 13, UiPalette.Text, FontWeights.SemiBold));
        var autoLoginBox = new CheckBox
        {
            Content = "启动时自动恢复上次登录（微软 / 离线账号）",
            IsChecked = _settings.AutoLogin,
            FontSize = 12, Foreground = Brush(UiPalette.TextDim),
            Margin = new Thickness(0, 8, 0, 0)
        };
        autoLoginBox.Click += (_, _) => { _settings.AutoLogin = autoLoginBox.IsChecked == true; _settings.Save(); };
        loginCard.Children.Add(autoLoginBox);
        var offlineNameBox = SettingTextBox(_settings.QuickOfflineName, "快速启动默认离线用户名");
        loginCard.Children.Add(Label("默认离线用户名", 11, UiPalette.Muted));
        loginCard.Children.Add(offlineNameBox);
        offlineNameBox.LostFocus += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(offlineNameBox.Text))
            {
                _settings.QuickOfflineName = offlineNameBox.Text.Trim();
                _settings.Save();
            }
        };
        root.Children.Add(loginCard);

        // ---- 游戏启动 ----
        var launchCard = Card();
        launchCard.Margin = new Thickness(0, 14, 0, 0);
        launchCard.Children.Add(Label("游戏启动", 13, UiPalette.Text, FontWeights.SemiBold));

        launchCard.Children.Add(Label("最大内存分配", 11, UiPalette.Muted));
        var ramBox = new ComboBox
        {
            FontSize = 12, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 2, 0, 8),
            Background = Brush(UiPalette.Bg), Foreground = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border), HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 200
        };
        var ramOptions = new[] { 1024, 2048, 4096, 6144, 8192, 12288, 16384 };
        ramBox.ItemsSource = ramOptions.Select(r => $"{r / 1024.0:0.#} GB（{r} MB）").ToList();
        var curRamIdx = Array.IndexOf(ramOptions, _settings.MaxRamMb);
        ramBox.SelectedIndex = curRamIdx >= 0 ? curRamIdx : 2;
        ramBox.SelectionChanged += (_, _) =>
        {
            _settings.MaxRamMb = ramOptions[ramBox.SelectedIndex];
            _settings.Save();
        };
        launchCard.Children.Add(ramBox);

        launchCard.Children.Add(Label("Java 路径（留空自动检测）", 11, UiPalette.Muted));
        var javaRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
        var javaBox = SettingTextBox(_settings.JavaPath, @"例如 C:\Program Files\Java\jdk-21\bin\javaw.exe");
        javaBox.Width = 340;
        javaRow.Children.Add(javaBox);
        var javaBrowse = new Button
        {
            Content = "浏览...", Padding = new Thickness(12, 5, 12, 5), Margin = new Thickness(8, 0, 0, 0),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12
        };
        javaBrowse.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "javaw.exe|javaw.exe|java.exe|java.exe|所有文件|*.*" };
            if (dlg.ShowDialog() == true) javaBox.Text = dlg.FileName;
        };
        javaRow.Children.Add(javaBrowse);
        launchCard.Children.Add(javaRow);
        javaBox.LostFocus += (_, _) => { _settings.JavaPath = javaBox.Text.Trim(); _settings.Save(); };

        launchCard.Children.Add(Label("游戏窗口分辨率", 11, UiPalette.Muted));
        var resRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 8) };
        var widthBox = SettingTextBox(_settings.GameWindowWidth.ToString(), "宽");
        widthBox.Width = 90;
        var heightBox = SettingTextBox(_settings.GameWindowHeight.ToString(), "高");
        heightBox.Width = 90;
        heightBox.Margin = new Thickness(8, 0, 0, 0);
        resRow.Children.Add(widthBox);
        resRow.Children.Add(heightBox);
        var fullScreenBox = new CheckBox
        {
            Content = "全屏启动", IsChecked = _settings.GameFullScreen,
            FontSize = 12, Foreground = Brush(UiPalette.TextDim),
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0)
        };
        fullScreenBox.Click += (_, _) => { _settings.GameFullScreen = fullScreenBox.IsChecked == true; _settings.Save(); };
        resRow.Children.Add(fullScreenBox);
        launchCard.Children.Add(resRow);
        void SaveRes()
        {
            if (int.TryParse(widthBox.Text, out var w)) _settings.GameWindowWidth = w;
            if (int.TryParse(heightBox.Text, out var h)) _settings.GameWindowHeight = h;
            _settings.Save();
        }
        widthBox.LostFocus += (_, _) => SaveRes();
        heightBox.LostFocus += (_, _) => SaveRes();

        launchCard.Children.Add(Label("额外 JVM 参数（可选）", 11, UiPalette.Muted));
        var jvmBox = SettingTextBox(_settings.ExtraJvmArgs, "例如 -XX:+UseG1GC -Dfile.encoding=UTF-8");
        launchCard.Children.Add(jvmBox);
        jvmBox.LostFocus += (_, _) => { _settings.ExtraJvmArgs = jvmBox.Text.Trim(); _settings.Save(); };
        root.Children.Add(launchCard);

        // ---- 联机 ----
        var netCard = Card();
        netCard.Margin = new Thickness(0, 14, 0, 0);
        netCard.Children.Add(Label("联机", 13, UiPalette.Text, FontWeights.SemiBold));
        var autoCopyBox = new CheckBox
        {
            Content = "公网地址分配后自动复制到剪贴板",
            IsChecked = _settings.AutoCopyAddress,
            FontSize = 12, Foreground = Brush(UiPalette.TextDim),
            Margin = new Thickness(0, 8, 0, 0)
        };
        autoCopyBox.Click += (_, _) => { _settings.AutoCopyAddress = autoCopyBox.IsChecked == true; _settings.Save(); };
        netCard.Children.Add(autoCopyBox);
        root.Children.Add(netCard);

        // ---- 下载与实例（对齐 Glowstone：下载源 / 版本隔离 / 下载目录） ----
        var dlCard = Card();
        dlCard.Margin = new Thickness(0, 14, 0, 0);
        dlCard.Children.Add(Label("下载与实例", 13, UiPalette.Text, FontWeights.SemiBold));
        dlCard.Children.Add(Body("启动时会自动测速；如果觉得慢，可以在这里手动指定下载源。版本、模组、资源包都走这个源。"));

        var srcRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 8) };
        var mirrorBtn = SmallButton(_settings.UseMirror ? "● 镜像源（推荐，国内更快）" : "○ 镜像源（推荐，国内更快）");
        var officialBtn = SmallButton(_settings.UseMirror ? "○ 官方源" : "● 官方源");
        if (_settings.UseMirror) mirrorBtn.Foreground = Brush(UiPalette.Accent);
        else officialBtn.Foreground = Brush(UiPalette.Accent);
        mirrorBtn.Click += (_, _) =>
        {
            _settings.UseMirror = true;
            _settings.Save();
            ShowPage("设置", true);
        };
        officialBtn.Click += (_, _) =>
        {
            _settings.UseMirror = false;
            _settings.Save();
            ShowPage("设置", true);
        };
        srcRow.Children.Add(mirrorBtn);
        srcRow.Children.Add(officialBtn);
        dlCard.Children.Add(Label("下载源", 11, UiPalette.Muted));
        dlCard.Children.Add(srcRow);

        var isolateBox = new CheckBox
        {
            Content = "版本隔离（每个版本使用独立的 mods / config / saves，互不干扰）",
            IsChecked = _settings.VersionIsolation,
            FontSize = 12,
            Foreground = Brush(UiPalette.TextDim),
            Margin = new Thickness(0, 4, 0, 6)
        };
        isolateBox.Click += (_, _) =>
        {
            _settings.VersionIsolation = isolateBox.IsChecked == true;
            _settings.Save();
        };
        dlCard.Children.Add(isolateBox);

        dlCard.Children.Add(Label("资源默认下载目录（留空 = 游戏目录内对应位置）", 11, UiPalette.Muted));
        var dlDirRow = new StackPanel { Orientation = Orientation.Horizontal };
        var dlDirBox = SettingTextBox(_settings.DownloadDir, "例如 D:\\Minecraft\\downloads");
        dlDirBox.Width = 340;
        dlDirRow.Children.Add(dlDirBox);
        var dlDirPick = SmallButton("选择…");
        dlDirPick.Margin = new Thickness(8, 0, 0, 0);
        dlDirPick.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择默认下载目录" };
            if (dlg.ShowDialog(this) == true)
            {
                dlDirBox.Text = dlg.FolderName;
                _settings.DownloadDir = dlg.FolderName;
                _settings.Save();
            }
        };
        dlDirRow.Children.Add(dlDirPick);
        var dlDirSave = SmallButton("保存");
        dlDirSave.Margin = new Thickness(8, 0, 0, 0);
        dlDirSave.Click += (_, _) =>
        {
            _settings.DownloadDir = dlDirBox.Text.Trim();
            _settings.Save();
        };
        dlDirRow.Children.Add(dlDirSave);
        dlCard.Children.Add(dlDirRow);
        root.Children.Add(dlCard);

        // ---- 窗口行为 ----
        var winCard = Card();
        winCard.Margin = new Thickness(0, 14, 0, 0);
        winCard.Children.Add(Label("窗口行为", 13, UiPalette.Text, FontWeights.SemiBold));
        var trayBox = new CheckBox
        {
            Content = "点击右上角关闭时最小化到托盘（下载与联机不中断）",
            IsChecked = _settings.CloseToTray,
            FontSize = 12,
            Foreground = Brush(UiPalette.TextDim),
            Margin = new Thickness(0, 8, 0, 0)
        };
        trayBox.Click += (_, _) =>
        {
            _settings.CloseToTray = trayBox.IsChecked == true;
            _settings.Save();
        };
        winCard.Children.Add(trayBox);
        root.Children.Add(winCard);

        // ---- 更新 ----
        var updateCard = Card();
        updateCard.Margin = new Thickness(0, 14, 0, 0);
        updateCard.Children.Add(Label("更新", 13, UiPalette.Text, FontWeights.SemiBold));
        updateCard.Children.Add(Body($"当前版本：v{AppInfo.Version}"));

        var autoUpdateBox = new CheckBox
        {
            Content = "启动时自动检查更新（发现新版本会弹窗询问，可以选择暂不更新）",
            IsChecked = _settings.AutoCheckUpdate,
            FontSize = 12, Foreground = Brush(UiPalette.TextDim),
            Margin = new Thickness(0, 4, 0, 4)
        };
        autoUpdateBox.Click += (_, _) => { _settings.AutoCheckUpdate = autoUpdateBox.IsChecked == true; _settings.Save(); };
        updateCard.Children.Add(autoUpdateBox);

        updateCard.Children.Add(Label("更新源地址（指向 manifest.json 的公开直链，留空则不检查）", 11, UiPalette.Muted));
        var updateUrlBox = SettingTextBox(_settings.UpdateManifestUrl, "https://example.com/NEXLauncher/manifest.json");
        updateUrlBox.Width = 480;
        updateUrlBox.HorizontalAlignment = HorizontalAlignment.Left;
        updateCard.Children.Add(updateUrlBox);
        updateUrlBox.LostFocus += (_, _) => { _settings.UpdateManifestUrl = updateUrlBox.Text.Trim(); _settings.Save(); };

        var updateBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        Button checkUpdateBtn = null!;
        checkUpdateBtn = ActionButton("检查更新", async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(updateUrlBox.Text.Trim()))
            {
                MessageBox.Show("请先在上方填写更新源地址。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            checkUpdateBtn.IsEnabled = false;
            try
            {
                var info = await _updateService.CheckForUpdateAsync(updateUrlBox.Text.Trim());
                if (info == null)
                {
                    MessageBox.Show($"已是最新版本（v{AppInfo.Version}）。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    var msg = $"发现新版本 v{info.Version}（当前 v{AppInfo.Version}）";
                    if (!string.IsNullOrWhiteSpace(info.Notes)) msg += $"\n\n{info.Notes}";
                    msg += "\n\n是否立即下载并安装？";
                    if (MessageBox.Show(msg, "启动器更新", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                        await DownloadAndApplyUpdateAsync(info);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"检查更新失败：\n{ex.Message}", "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { checkUpdateBtn.IsEnabled = true; }
        });
        updateBtnRow.Children.Add(checkUpdateBtn);
        updateCard.Children.Add(updateBtnRow);
        root.Children.Add(updateCard);

        root.Children.Add(BuildAboutCard());

        return root;
    }

    /// <summary>关于卡片：软件信息、版权声明、免责声明。</summary>
    private UIElement BuildAboutCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 0);
        card.Children.Add(Label("关于", 13, UiPalette.Text, FontWeights.SemiBold));

        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        nameRow.Children.Add(new TextBlock
        {
            Text = "NEX Launcher",
            FontSize = 18, FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text), VerticalAlignment = VerticalAlignment.Center
        });
        nameRow.Children.Add(new Border
        {
            Background = Brush(UiPalette.SurfaceAlt),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = $"v{AppInfo.Version}",
                FontSize = 11, Foreground = Brush(UiPalette.Accent)
            }
        });
        card.Children.Add(nameRow);

        card.Children.Add(new TextBlock
        {
            Text = "版权所有 © 2026 NEX Launcher 作者。保留一切权利。",
            FontSize = 12, Foreground = Brush(UiPalette.MutedLight),
            Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap
        });

        card.Children.Add(new TextBlock
        {
            Text = "本软件为免费非商业的第三方工具，采用「保留所有权利」许可：可免费使用和原样分享，"
                 + "禁止修改、反编译、改名再发布及任何商业用途。商业授权请联系作者。",
            FontSize = 12, Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap
        });

        card.Children.Add(new TextBlock
        {
            Text = "免责声明：本软件与 Mojang Studios、Microsoft、网易公司无隶属或合作关系，"
                 + "「Minecraft」「我的世界」及相关内容的一切权利归其各自权利人所有。"
                 + "本软件不提供任何游戏本体文件，请通过官方渠道获取正版授权。",
            FontSize = 12, Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap
        });

        // Minecraft Usage Guidelines 要求显著标注的英文声明原文
        card.Children.Add(new TextBlock
        {
            Text = "NOT AN OFFICIAL MINECRAFT PRODUCT. NOT APPROVED BY OR ASSOCIATED WITH MOJANG OR MICROSOFT.",
            FontSize = 11, Foreground = Brush(UiPalette.Muted),
            FontStyle = FontStyles.Italic,
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap
        });

        card.Children.Add(new TextBlock
        {
            Text = "第三方组件：CmlLib.Core（MIT）· XboxAuthNet（MIT）· .NET 8/WPF（MIT）· "
                 + "Modrinth API · Ely.by · playit.gg · authlib-injector（GPL-3.0）",
            FontSize = 11, Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap
        });

        return card;
    }

    private TextBox SettingTextBox(string text, string placeholder) => new()
    {
        Text = text,
        FontSize = 12,
        Padding = new Thickness(8, 5, 8, 5),
        Margin = new Thickness(0, 2, 0, 8),
        Background = Brush(UiPalette.Bg),
        Foreground = Brush(UiPalette.Text),
        BorderBrush = Brush(UiPalette.Border),
        ToolTip = placeholder
    };

    private static readonly string[] PresetColors =
    {
        "#5B9DFF", "#4ECB73", "#F76965", "#F0A020",
        "#A855F7", "#EC4899", "#14B8A6", "#F43F5E",
        "#E6EDF3", "#7D8B9E"
    };

    private UIElement BuildPersonalizePage()
    {
        var root = new StackPanel();
        root.Children.Add(Label("外观", 11, UiPalette.Accent));
        root.Children.Add(Heading("个性化"));
        root.Children.Add(Body("自定义启动器主题、强调色、背景图和标题。"));

        // 主题预设卡片
        var themeCard = Card();
        themeCard.Margin = new Thickness(0, 22, 0, 0);
        var themeInner = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        themeInner.Children.Add(Label("界面主题", 13, UiPalette.Text, FontWeights.SemiBold));
        themeInner.Children.Add(Body("一键切换整套界面配色（深色系列）。"));
        var themeRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        foreach (var preset in UiPalette.Presets)
        {
            var isActive = preset.Name == _settings.ThemePreset;
            var btn = new Button
            {
                Padding = new Thickness(0), Margin = new Thickness(0, 0, 10, 8),
                Cursor = Cursors.Hand, BorderThickness = new Thickness(isActive ? 2 : 1),
                BorderBrush = isActive ? Brush(UiPalette.Text) : Brush(preset.Border),
                Background = Brush(preset.Bg),
                Width = 118, Height = 64,
                Content = new StackPanel
                {
                    Children =
                    {
                        new Border
                        {
                            Height = 14, Background = Brush(preset.Header),
                            Margin = new Thickness(6, 6, 6, 0)
                        },
                        new Border
                        {
                            Height = 12, Width = 44, HorizontalAlignment = HorizontalAlignment.Left,
                            Background = Brush(preset.Accent), Margin = new Thickness(6, 8, 0, 0)
                        },
                        new TextBlock
                        {
                            Text = preset.Name, FontSize = 11,
                            Foreground = Brush(preset.Text), Margin = new Thickness(6, 5, 0, 0)
                        }
                    }
                }
            };
            btn.Click += (_, _) =>
            {
                _settings.ThemePreset = preset.Name;
                // 主题预设自带强调色，同步覆盖用户强调色
                _settings.AccentColor = preset.Accent;
                _settings.Save();
                UiPalette.ApplyPreset(preset.Name);
                Title = _settings.WindowTitle;
                if (!string.IsNullOrEmpty(_settings.BackgroundImagePath))
                    ApplyBackgroundImage(_settings.BackgroundImagePath);
                ShowPage(CurrentPage, true);
            };
            themeRow.Children.Add(btn);
        }
        themeInner.Children.Add(themeRow);
        themeCard.Children.Add(themeInner);
        root.Children.Add(themeCard);

        // 主题色卡片
        var colorCard = Card();
        colorCard.Margin = new Thickness(0, 22, 0, 0);
        var colorInner = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        colorInner.Children.Add(Label("主题色", 13, UiPalette.Text, FontWeights.SemiBold));
        colorInner.Children.Add(Body("选择一个强调色，用于按钮、高亮和链接。"));
        var colorGrid = new UniformGrid { Columns = 10, Margin = new Thickness(0, 10, 0, 0) };
        foreach (var c in PresetColors)
        {
            var swatch = new Border
            {
                Width = 32, Height = 32, CornerRadius = new CornerRadius(8),
                Background = Brush(c), Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand, Tag = c
            };
            var isActive = string.Equals(c, _settings.AccentColor, StringComparison.OrdinalIgnoreCase);
            if (isActive) swatch.BorderBrush = Brush("#FFFFFF");
            swatch.MouseLeftButtonDown += (_, _) =>
            {
                _settings.AccentColor = c;
                _settings.Save();
                ApplyAccentColor(c);
            };
            colorGrid.Children.Add(swatch);
        }
        colorInner.Children.Add(colorGrid);
        colorCard.Children.Add(colorInner);
        root.Children.Add(colorCard);

        // 背景图卡片
        var bgCard = Card();
        bgCard.Margin = new Thickness(0, 16, 0, 0);
        var bgInner = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        bgInner.Children.Add(Label("背景图", 13, UiPalette.Text, FontWeights.SemiBold));
        bgInner.Children.Add(Body("选择一张图片作为窗口背景（留空则使用纯色背景）。"));
        var bgPath = new TextBlock
        {
            Text = string.IsNullOrEmpty(_settings.BackgroundImagePath) ? "未设置" : _settings.BackgroundImagePath,
            FontSize = 12, Foreground = Brush(UiPalette.Muted), Margin = new Thickness(0, 4, 0, 8),
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        bgInner.Children.Add(bgPath);
        var bgRow = new StackPanel { Orientation = Orientation.Horizontal };
        var browseBg = new Button
        {
            Content = "选择图片...", Padding = new Thickness(14, 7, 14, 7),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.Text),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0)
        };
        browseBg.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog { Filter = "图片|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dlg.ShowDialog() == true)
            {
                _settings.BackgroundImagePath = dlg.FileName;
                _settings.Save();
                bgPath.Text = dlg.FileName;
                ApplyBackgroundImage(dlg.FileName);
            }
        };
        var clearBg = new Button
        {
            Content = "清除背景", Padding = new Thickness(14, 7, 14, 7),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand
        };
        clearBg.Click += (_, _) =>
        {
            _settings.BackgroundImagePath = "";
            _settings.Save();
            bgPath.Text = "未设置";
            ApplyBackgroundImage(null);
        };
        bgRow.Children.Add(browseBg);
        bgRow.Children.Add(clearBg);
        bgInner.Children.Add(bgRow);

        // 背景不透明度
        bgInner.Children.Add(Label("背景不透明度", 11, UiPalette.Muted));
        var opacitySlider = new Slider
        {
            Minimum = 0.05, Maximum = 0.8, Value = _settings.BackgroundOpacity,
            Width = 260, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 2, 0, 4)
        };
        opacitySlider.ValueChanged += (_, _) =>
        {
            _settings.BackgroundOpacity = Math.Round(opacitySlider.Value, 2);
            _settings.Save();
            if (!string.IsNullOrEmpty(_settings.BackgroundImagePath))
                ApplyBackgroundImage(_settings.BackgroundImagePath);
        };
        bgInner.Children.Add(opacitySlider);
        bgCard.Children.Add(bgInner);
        root.Children.Add(bgCard);

        // 标题卡片
        var titleCard = Card();
        titleCard.Margin = new Thickness(0, 16, 0, 0);
        var titleInner = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        titleInner.Children.Add(Label("窗口标题", 13, UiPalette.Text, FontWeights.SemiBold));
        titleInner.Children.Add(Body("自定义启动器窗口标题文字。"));
        var titleBox = new TextBox
        {
            Text = _settings.WindowTitle, FontSize = 13,
            Padding = new Thickness(8, 6, 8, 6),
            Background = Brush(UiPalette.Bg), Foreground = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border), Margin = new Thickness(0, 4, 0, 8)
        };
        titleInner.Children.Add(titleBox);
        var saveTitle = new Button
        {
            Content = "保存标题", Padding = new Thickness(14, 7, 14, 7),
            Background = Brush(UiPalette.Accent), Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold, Cursor = Cursors.Hand
        };
        saveTitle.Click += (_, _) =>
        {
            _settings.WindowTitle = string.IsNullOrWhiteSpace(titleBox.Text) ? "NEX Launcher" : titleBox.Text;
            _settings.Save();
            Title = _settings.WindowTitle;
            MessageBox.Show("标题已保存。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        titleInner.Children.Add(saveTitle);
        titleCard.Children.Add(titleInner);
        root.Children.Add(titleCard);

        return root;
    }

    private void ApplyAccentColor(string hex)
    {
        try
        {
            UiPalette.SetAccent(hex);
            // 重新渲染当前页面以应用新颜色
            if (PageHost.Content is UIElement) ShowPage(CurrentPage, true);
        }
        catch { }
    }

    private string CurrentPage = "首页";

    private void ApplyBackgroundImage(string? path)
    {
        try
        {
            var border = (Border)Content;
            if (string.IsNullOrEmpty(path))
            {
                // 必须恢复主题背景色：无边框透明窗口若 Background 为 null，
                // 窗口会完全透明且鼠标点击会穿透到桌面
                border.Background = (Brush)border.FindResource("BgBrush");
            }
            else
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(path, UriKind.Absolute);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();

                // 关键：先把主题背景色铺底，再把背景图按设定透明度叠上去，
                // 合成出一张完全不透明的位图。直接给 ImageBrush 设 Opacity 会让
                // 窗口真实透明（无边框透明窗口），把桌面壁纸和图标透出来。
                var themeBg = (SolidColorBrush)border.FindResource("BgBrush");
                var composed = ComposeOpaqueBackground(bmp, themeBg.Color, _settings.BackgroundOpacity);
                composed.Freeze();
                border.Background = new ImageBrush(composed)
                {
                    Stretch = Stretch.UniformToFill,
                    Opacity = 1
                };
            }
        }
        catch { }
    }

    /// <summary>背景色打底 + 原图按 opacity 叠加，输出不透明位图（保证窗口永不透出桌面）。</summary>
    private static BitmapSource ComposeOpaqueBackground(BitmapImage source, Color backdrop, double opacity)
    {
        opacity = Math.Clamp(opacity, 0.05, 1.0);
        var w = source.PixelWidth;
        var h = source.PixelHeight;

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(backdrop), null, new Rect(0, 0, w, h));
            dc.PushOpacity(opacity);
            dc.DrawImage(source, new Rect(0, 0, w, h));
            dc.Pop();
        }
        var result = new RenderTargetBitmap(w, h, source.DpiX, source.DpiY, PixelFormats.Pbgra32);
        result.Render(visual);
        return result;
    }
    #endregion

    #region 工具
    private void OpenGameDirectory()
    {
        Directory.CreateDirectory(_gameDirectory);
        Process.Start(new ProcessStartInfo { FileName = _gameDirectory, UseShellExecute = true });
    }

    private static RoundedPanel Card() => new()
    {
        Background = Brush(UiPalette.Surface),
        Stroke = Brush(UiPalette.Border),
        StrokeThickness = 1,
        Radius = 12,
        Margin = new Thickness(0, 0, 0, 0)
    };

    private static UIElement CardLink(string title, string detail, string icon)
    {
        var card = Card();
        card.Margin = new Thickness(0, 0, 10, 10);
        var row = new DockPanel();
        var mark = Label(icon, 22, UiPalette.Accent);
        DockPanel.SetDock(mark, Dock.Right);
        row.Children.Add(mark);
        var t = new StackPanel();
        t.Children.Add(Label(title, 14, UiPalette.Text, FontWeights.SemiBold));
        t.Children.Add(Body(detail));
        row.Children.Add(t);
        card.Children.Add(row);
        return card;
    }

    private static Button ActionButton(string text, RoutedEventHandler action)
    {
        var b = new Button
        {
            Content = text,
            Padding = new Thickness(16, 9, 16, 9),
            Margin = new Thickness(0, 12, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = Brush(UiPalette.Accent),
            Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand
        };
        b.Click += action;
        return b;
    }

    private static TextBlock Heading(string t) => Label(t, 28, UiPalette.Text, FontWeights.SemiBold);

    private static TextBlock Body(string t) => new()
    {
        Text = t, FontSize = 13, Foreground = Brush(UiPalette.MutedLight),
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4), LineHeight = 21
    };

    private static TextBlock Label(string t, double size, string color, FontWeight? w = null) => new()
    {
        Text = t, FontSize = size, Foreground = Brush(color),
        FontWeight = w ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 4)
    };

    private static SolidColorBrush Brush(string c) => new((Color)ColorConverter.ConvertFromString(c));
    #endregion

    // ---- 游戏窗口前置：窗口一弹出就切到前台，避免用户停留在启动器误以为启动卡住 ----
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private static async void BringGameWindowToFront(Process game)
    {
        try
        {
            // Minecraft 启动到出窗口通常 30~150 秒（首次更久），轮询等窗口句柄出现
            for (var i = 0; i < 150 && !game.HasExited; i++)
            {
                await Task.Delay(1000);
                try { game.Refresh(); } catch { }
                var h = game.MainWindowHandle;
                if (h == IntPtr.Zero) continue;
                ShowWindow(h, 5);            // SW_SHOW
                SetForegroundWindow(h);
                return;
            }
        }
        catch { /* 进程已退出等情况忽略 */ }
    }
}
