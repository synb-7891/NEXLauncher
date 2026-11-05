using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NEXLauncher.Services;

namespace NEXLauncher;

/// <summary>
/// 「安全防护」页：五道防火墙状态与开关、一键体检、事件日志、隔离区，
/// 以及「24 小时自动防卫」后台守护。
/// 完整版与公共版功能完全一致。
/// </summary>
public partial class MainWindow
{
    private StackPanel? _secEventsPanel;
    private StackPanel? _secQuarPanel;
    private StackPanel? _secHealthPanel;
    private ScrollViewer? _secRootScroll;

    // ---- 24 小时自动防卫 ----
    private TextBlock? _guardStateText;
    private TextBlock? _guardDetailText;
    private Border? _guardPill;
    private Border? _guardKnob;
    private bool _guardOn;
    private bool _guardStarting;
    private DateTime _lastGuardAttempt = DateTime.MinValue;
    private DispatcherTimer? _secGuardTimer;

    private UIElement BuildSecurityPage()
    {
        var root = new StackPanel { MaxWidth = 1000 };
        root.Children.Add(Label("防护", 11, UiPalette.Accent));
        root.Children.Add(Heading("安全防护"));

        // ---- 总览 ----
        var hero = Card();
        hero.Margin = new Thickness(0, 14, 0, 0);
        var heroInner = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        heroInner.Children.Add(new TextBlock
        {
            Text = "五道防火墙 · 全程本地运行 · 不上传任何数据",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text)
        });
        heroInner.Children.Add(Body(
            "① 下载防护：模组/资源落地前扫描伪装文件（EXE 冒充 jar、脚本冒充资源包）\n" +
            "② 勒索蜜罐：桌面/文档/图片/游戏目录布置诱饵文件，被加密改动立即告警并自动还原\n" +
            "③ 行为监控：短时间大量文件改写（批量加密特征）立即报警\n" +
            "④ 系统守护：hosts、注册表启动项、启动文件夹变更告警，防劫持防驻留\n" +
            "⑤ 游戏防护：mods/插件/资源包伪装检测 + 启动参数注入检查\n" +
            "⑥ 24 小时自动防卫：独立后台守护进程，关闭启动器后防护不中断，开机自动开始防护"));
        heroInner.Children.Add(new TextBlock
        {
            Text = "提示：本防护与 Windows 安全中心（Defender）互补，建议两者同时开启。",
            FontSize = 11.5,
            Foreground = Brush(UiPalette.Muted),
            Margin = new Thickness(0, 4, 0, 0)
        });

        var checkBtn = ActionButton("🛡 一键体检", (_, _) => RunSecurityHealthCheck());
        checkBtn.Margin = new Thickness(0, 8, 0, 0);
        heroInner.Children.Add(checkBtn);
        hero.Children.Add(heroInner);
        root.Children.Add(hero);

        // ---- 24 小时自动防卫（后台守护进程） ----
        root.Children.Add(BuildAlwaysGuardCard());

        // ---- 体检结果 ----
        _secHealthPanel = new StackPanel();
        root.Children.Add(_secHealthPanel);

        // ---- 五道防火墙开关 ----
        var fwCard = Card();
        fwCard.Margin = new Thickness(0, 14, 0, 0);
        fwCard.Children.Add(Label("防火墙开关", 13, UiPalette.Text, FontWeights.SemiBold));
        fwCard.Children.Add(SecToggle("第一道 · 下载防护", "资源下载与整合包落地时自动扫描伪装文件，威胁自动进隔离区",
            _settings.SecDownloadShield, v => _settings.SecDownloadShield = v));
        fwCard.Children.Add(SecToggle("第二道 · 勒索蜜罐", "在桌面/文档/图片/游戏目录布置诱饵文件（NEX防护诱饵_请勿删除.txt，不含任何真实数据），被加密改动立即告警",
            _settings.SecRansomGuard, v => _settings.SecRansomGuard = v));
        fwCard.Children.Add(SecToggle("第三道 · 行为监控", "监控批量文件改写行为，4 秒内 150+ 次改写视为勒索加密特征",
            _settings.SecBehaviorMonitor, v => _settings.SecBehaviorMonitor = v));
        fwCard.Children.Add(SecToggle("第四道 · 系统守护", "监控 hosts 文件、开机自启动项、启动文件夹变更，防域名劫持与病毒驻留",
            _settings.SecSystemGuard, v => _settings.SecSystemGuard = v));
        fwCard.Children.Add(SecToggle("第五道 · 游戏防护", "体检时扫描 mods/插件/资源包伪装文件与版本启动参数注入",
            _settings.SecGameGuard, v => _settings.SecGameGuard = v));
        fwCard.Children.Add(SecToggle("自动防御（激进）", "检测到勒索行为时自动终止临时目录里近期启动的可疑进程。误杀风险略高，默认关闭",
            _settings.SecAutoKill, v => _settings.SecAutoKill = v));
        root.Children.Add(fwCard);

        // ---- 主动拦截引擎 ----
        var blockCard = Card();
        blockCard.Margin = new Thickness(0, 14, 0, 0);
        blockCard.Children.Add(Label("主动拦截引擎", 13, UiPalette.Text, FontWeights.SemiBold));
        var blockDesc = new TextBlock
        {
            Text = "开启后真正动手处置，而不只是记日志：可疑进程终止 + 映像隔离或删除、伪装文件落地即隔离、恶意启动项自动摘除、hosts 劫持自动还原、U 盘 autorun 病毒清除。",
            FontSize = 11.5,
            Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 6)
        };
        blockCard.Children.Add(blockDesc);
        blockCard.Children.Add(SecToggle("主动拦截", "实时判定可疑进程与文件，命中即刻终止/隔离（每 3 秒扫描一次新进程）",
            _settings.SecActiveBlock, v => _settings.SecActiveBlock = v));
        blockCard.Children.Add(SecToggle("拦截后彻底删除", "关闭时只移入隔离区（可从隔离区还原）；开启后直接删除文件，不给病毒留副本",
            _settings.SecAutoDelete, v => _settings.SecAutoDelete = v));
        blockCard.Children.Add(SecToggle("Windows Defender 复核", "对拦截到的文件调用系统 Defender 二次确认并清除，双引擎交叉验证",
            _settings.SecDefenderAssist, v => _settings.SecDefenderAssist = v));

        var svc = SecurityService.Instance;
        var blockStat = new TextBlock
        {
            Text = $"本次运行已拦截处置 {svc.BlockedCount} 次 · Defender 联动：{(svc.DefenderAvailable ? "可用" : "未检测到")}" +
                   (_settings.SecActiveBlock ? "" : " · 当前未开启拦截"),
            FontSize = 11.5,
            Foreground = Brush(UiPalette.Accent),
            Margin = new Thickness(0, 8, 0, 0)
        };
        blockCard.Children.Add(blockStat);
        root.Children.Add(blockCard);

        // ---- 事件日志 ----
        var evtCard = Card();
        evtCard.Margin = new Thickness(0, 14, 0, 0);
        var evtHead = new DockPanel();
        var evtClear = new Button
        {
            Content = "清空日志",
            Padding = new Thickness(12, 5, 12, 5),
            Background = Brush(UiPalette.SurfaceAlt),
            Foreground = Brush(UiPalette.Muted),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center
        };
        evtClear.Click += (_, _) =>
        {
            SecurityService.Instance.ClearEvents();
            RefreshSecurityLists();
        };
        DockPanel.SetDock(evtClear, Dock.Right);
        evtHead.Children.Add(evtClear);
        evtHead.Children.Add(Label("安全事件日志", 13, UiPalette.Text, FontWeights.SemiBold));
        evtCard.Children.Add(evtHead);
        _secEventsPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        evtCard.Children.Add(_secEventsPanel);
        root.Children.Add(evtCard);

        // ---- 隔离区 ----
        var quarCard = Card();
        quarCard.Margin = new Thickness(0, 14, 0, 0);
        quarCard.Children.Add(Label("隔离区", 13, UiPalette.Text, FontWeights.SemiBold));
        quarCard.Children.Add(Body("被拦截的威胁文件会改名放进隔离区（不删除、不再运行），确认后可彻底删除或误报还原。"));
        _secQuarPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        quarCard.Children.Add(_secQuarPanel);
        root.Children.Add(quarCard);

        RefreshSecurityLists();
        return root;
    }

    // ================= 24 小时自动防卫 =================

    /// <summary>「24 小时自动防卫」卡片：独立后台守护进程，关闭启动器后仍持续防护。</summary>
    private UIElement BuildAlwaysGuardCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 14, 0, 0);

        var inner = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
        inner.Children.Add(Label("24 小时自动防卫", 14, UiPalette.Text, FontWeights.SemiBold));
        inner.Children.Add(Body(
            "开启后 NEX 会在后台运行一个独立的守护进程：关闭启动器窗口、甚至完全退出启动器，防护依然继续；" +
            "登录 Windows 后也会自动开始防护，不需要先打开启动器。"));

        _guardStateText = new TextBlock
        {
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        };
        inner.Children.Add(_guardStateText);

        _guardDetailText = new TextBlock
        {
            FontSize = 11.5,
            Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        };
        inner.Children.Add(_guardDetailText);

        // 开关行
        var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = "开启自动防卫（后台常驻 + 开机自启）",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(UiPalette.Text)
        });
        text.Children.Add(new TextBlock
        {
            Text = "开启：退出启动器后仍然防护；关闭：退出启动器即停止防护（只保留启动器运行期间的防护）。",
            FontSize = 11.5,
            Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 12, 0)
        });
        text.Cursor = Cursors.Hand;
        text.MouseLeftButtonUp += async (_, _) => await ToggleAlwaysGuardAsync();
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        _guardPill = new Border
        {
            Width = 48,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Tag = "sec-toggle-guard",
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "点击开启 / 关闭 24 小时自动防卫"
        };
        _guardKnob = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10) };
        _guardPill.Child = _guardKnob;
        _guardPill.MouseLeftButtonUp += async (_, _) => await ToggleAlwaysGuardAsync();
        Grid.SetColumn(_guardPill, 1);
        row.Children.Add(_guardPill);
        inner.Children.Add(row);

        // 操作按钮
        var btns = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        var restart = SmallSecBtn("重新启动守护", UiPalette.TextDim);
        restart.Padding = new Thickness(14, 7, 14, 7);
        restart.Click += async (_, _) => await StartGuardAsync(autoStart: true, manual: true);
        btns.Children.Add(restart);

        var openLog = SmallSecBtn("查看守护日志", UiPalette.TextDim);
        openLog.Margin = new Thickness(8, 0, 0, 0);
        openLog.Padding = new Thickness(14, 7, 14, 7);
        openLog.Click += (_, _) => OpenGuardLogFile();
        btns.Children.Add(openLog);
        inner.Children.Add(btns);

        card.Children.Add(inner);

        // 每 2 秒刷新真实状态；守护意外退出时自动重启
        _secGuardTimer?.Stop();
        _secGuardTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _secGuardTimer.Tick += async (_, _) =>
        {
            RefreshGuardStatusUi();
            await GuardWatchdogTickAsync();
        };
        _secGuardTimer.Start();

        RefreshGuardStatusUi();
        return card;
    }

    private void RefreshGuardStatusUi()
    {
        if (_guardStateText == null || _guardDetailText == null || _guardPill == null || _guardKnob == null) return;

        _guardOn = _settings.SecAlwaysGuard;
        var st = GuardDaemon.GetStatus();
        var autoStart = GuardDaemon.IsAutoStartEnabled();

        if (!_guardOn)
        {
            _guardStateText.Text = "⚪ 未开启 —— 关闭启动器后防护会停止";
            _guardStateText.Foreground = Brush(UiPalette.Muted);
            _guardDetailText.Text = "打开上面的开关即可启动后台守护；仅写入当前用户的开机自启项，不需要管理员权限。";
        }
        else if (st.Running)
        {
            _guardStateText.Text = $"🟢 后台守护运行中 · PID {st.Pid} · 已持续 {FormatGuardUptime(st.Uptime)}";
            _guardStateText.Foreground = Brush("#4ECB73");
            _guardDetailText.Text =
                $"心跳 {(st.HeartbeatAgeSeconds < 0 ? 0 : st.HeartbeatAgeSeconds):F0} 秒前 · 开机自启：{(autoStart ? "已开启" : "未开启（切换一次开关可重新登记）")}\n" +
                "守护进程独立于启动器：即使启动器已退出，蜜罐、行为监控、系统守护仍在后台工作。";
        }
        else
        {
            _guardStateText.Text = "🟠 已开启，但后台守护当前未运行";
            _guardStateText.Foreground = Brush("#F6A82A");
            _guardDetailText.Text = _guardStarting
                ? "正在启动后台守护…"
                : "正在自动重启后台守护；若长时间无法启动，请点击「重新启动守护」。";
        }

        _guardPill.Background = Brush(_guardOn ? UiPalette.Accent : UiPalette.SurfaceAlt);
        _guardPill.BorderBrush = Brush(_guardOn ? UiPalette.Accent : UiPalette.Border);
        _guardPill.BorderThickness = new Thickness(1);
        _guardKnob.Background = Brush(_guardOn ? UiPalette.Bg : UiPalette.Muted);
        _guardKnob.HorizontalAlignment = _guardOn ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _guardKnob.Margin = new Thickness(3, 0, 3, 0);
    }

    private async Task ToggleAlwaysGuardAsync()
    {
        if (_guardStarting) return;

        _guardOn = !_settings.SecAlwaysGuard;
        _settings.SecAlwaysGuard = _guardOn;
        _settings.Save();
        RefreshGuardStatusUi();

        if (_guardOn)
        {
            await StartGuardAsync(autoStart: true, manual: true);
        }
        else
        {
            GuardDaemon.TrySetAutoStart(false, out _);
            await StopGuardAsync();
        }
    }

    /// <summary>启动（或确保）后台守护在运行，并让本进程暂停后台监控，避免重复告警。</summary>
    private async Task StartGuardAsync(bool autoStart, bool manual)
    {
        if (_guardStarting) return;
        _guardStarting = true;
        _lastGuardAttempt = DateTime.Now;
        RefreshGuardStatusUi();

        var message = "";
        try
        {
            if (autoStart) GuardDaemon.TrySetAutoStart(true, out _);
            var (ok, msg) = await Task.Run(() =>
            {
                var r = GuardDaemon.EnsureRunning(out var m);
                return (ok: r, msg: m);
            });
            message = msg;
            if (ok) SecurityService.Instance.SetMonitorSuspended(true);
        }
        catch (Exception ex)
        {
            message = ex.Message;
        }
        finally
        {
            _guardStarting = false;
        }

        RefreshGuardStatusUi();
        GuardDaemon.Log("启动器请求启动后台守护：" + message);

        if (manual && !GuardDaemon.IsRunning)
            MessageBox.Show(this, message, "NEX 安全防护", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private async Task StopGuardAsync()
    {
        _guardStarting = true;
        RefreshGuardStatusUi();
        try { await Task.Run(() => GuardDaemon.Stop(out _)); }
        catch { }
        finally { _guardStarting = false; }

        // 守护停止后由启动器自己接管后台监控
        SecurityService.Instance.SetMonitorSuspended(false);
        RefreshGuardStatusUi();
    }

    /// <summary>看门狗：开关开启但守护意外退出时自动拉起（20 秒冷却，避免反复失败刷日志）。</summary>
    private async Task GuardWatchdogTickAsync()
    {
        if (!_settings.SecAlwaysGuard || _guardStarting) return;
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        if (GuardDaemon.GetStatus().Running) return;
        if ((DateTime.Now - _lastGuardAttempt).TotalSeconds < 20) return;

        await StartGuardAsync(autoStart: true, manual: false);
    }

    /// <summary>启动器启动时调用：按设置自动拉起守护进程。</summary>
    private async Task EnsureAlwaysGuardAsync()
    {
        if (!_settings.SecAlwaysGuard) return;

        if (GuardDaemon.GetStatus().Running)
        {
            // 守护已在运行（上次退出启动器后它一直在），本进程停止监控避免重复告警
            SecurityService.Instance.SetMonitorSuspended(true);
            GuardDaemon.TrySetAutoStart(true, out _);
            RefreshGuardStatusUi();
            return;
        }

        await StartGuardAsync(autoStart: true, manual: false);
    }

    private void OpenGuardLogFile()
    {
        try
        {
            Directory.CreateDirectory(GuardDaemon.DataDir);
            if (!File.Exists(GuardDaemon.LogPath))
                File.WriteAllText(GuardDaemon.LogPath, "暂无守护日志。\r\n");
            Process.Start(new ProcessStartInfo { FileName = GuardDaemon.LogPath, UseShellExecute = true });
        }
        catch (Exception ex) { ShowSecError(ex); }
    }

    private static string FormatGuardUptime(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "刚刚启动";
        if (t.TotalHours >= 24) return $"{(int)t.TotalDays} 天 {t.Hours} 小时 {t.Minutes} 分";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes} 分 {t.Seconds} 秒";
        return $"{t.Seconds} 秒";
    }

    private UIElement SecToggle(string title, string desc, bool initial, Action<bool> setter)
    {
        var row = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush(UiPalette.Text)
        });
        text.Children.Add(new TextBlock
        {
            Text = desc, FontSize = 11.5, Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 12, 0)
        });
        text.Cursor = System.Windows.Input.Cursors.Hand;
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        // 自绘开关（深色主题下 CheckBox 不可见，改用圆角滑块）
        var on = initial;
        var pill = new Border
        {
            Width = 48,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Tag = "sec-toggle",
            Cursor = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "点击开关/关闭"
        };
        var knob = new Border
        {
            Width = 20,
            Height = 20,
            CornerRadius = new CornerRadius(10)
        };
        pill.Child = knob;

        void Render()
        {
            pill.Background = Brush(on ? UiPalette.Accent : UiPalette.SurfaceAlt);
            pill.BorderBrush = Brush(on ? UiPalette.Accent : UiPalette.Border);
            pill.BorderThickness = new Thickness(1);
            knob.Background = Brush(on ? UiPalette.Bg : UiPalette.Muted);
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            knob.Margin = new Thickness(3, 0, 3, 0);
        }
        void Toggle()
        {
            on = !on;
            setter(on);
            _settings.Save();
            SecurityService.Instance.ApplySettings();
            Render();
        }
        pill.MouseLeftButtonUp += (_, _) => Toggle();
        text.MouseLeftButtonUp += (_, _) => Toggle();
        Render();

        Grid.SetColumn(pill, 1);
        row.Children.Add(pill);
        return row;
    }

    private void RunSecurityHealthCheck()
    {
        if (_secHealthPanel == null) return;
        _secHealthPanel.Children.Clear();
        var card = Card();
        card.Margin = new Thickness(0, 14, 0, 0);
        card.Children.Add(Label("体检报告（" + DateTime.Now.ToString("HH:mm:ss") + "）", 13, UiPalette.Text, FontWeights.SemiBold));

        Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
        List<string> lines;
        bool hasIssues;
        try
        {
            (lines, hasIssues) = SecurityService.Instance.RunHealthCheck();
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        foreach (var line in lines)
        {
            card.Children.Add(new TextBlock
            {
                Text = line,
                FontSize = 12,
                Foreground = Brush(line.StartsWith("❌") ? "#F87171" : line.StartsWith("⚠️") ? "#F6A82A" : UiPalette.TextDim),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });
        }
        card.Children.Add(new TextBlock
        {
            Text = hasIssues ? "发现风险项，建议按上面提示处理。" : "全部检查通过，防护状态良好。",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush(hasIssues ? "#F6A82A" : "#4ECB73"),
            Margin = new Thickness(0, 10, 0, 0)
        });
        _secHealthPanel.Children.Add(card);
    }

    private void RefreshSecurityLists()
    {
        // 事件日志
        if (_secEventsPanel != null)
        {
            _secEventsPanel.Children.Clear();
            var events = SecurityService.Instance.ReadEvents(50);
            if (events.Count == 0)
            {
                _secEventsPanel.Children.Add(new TextBlock
                {
                    Text = "暂无安全事件。防护运行中，有异常会第一时间记录在这里。",
                    FontSize = 12, Foreground = Brush(UiPalette.Muted), Margin = new Thickness(0, 2, 0, 0)
                });
            }
            else
            {
                foreach (var e in events.Take(30))
                {
                    var row = new Border
                    {
                        Background = Brush(e.Level == "严重" ? "#2A1418" : e.Level == "警告" ? "#241C0E" : UiPalette.SurfaceAlt),
                        BorderBrush = Brush(UiPalette.Border),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(10, 7, 10, 7),
                        Margin = new Thickness(0, 0, 0, 6)
                    };
                    var sp = new StackPanel();
                    sp.Children.Add(new TextBlock
                    {
                        Text = $"[{e.Level}] {e.Layer} · {e.Title}",
                        FontSize = 12,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = Brush(e.Level == "严重" ? "#F87171" : e.Level == "警告" ? "#F6A82A" : UiPalette.Text),
                        TextWrapping = TextWrapping.Wrap
                    });
                    sp.Children.Add(new TextBlock
                    {
                        Text = e.Time + "\n" + Breakable(e.Detail),
                        FontSize = 11,
                        Foreground = Brush(UiPalette.Muted),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 3, 0, 0)
                    });
                    row.Child = sp;
                    _secEventsPanel.Children.Add(row);
                }
            }
        }

        // 隔离区
        if (_secQuarPanel != null)
        {
            _secQuarPanel.Children.Clear();
            var items = SecurityService.Instance.ListQuarantine();
            if (items.Count == 0)
            {
                _secQuarPanel.Children.Add(new TextBlock
                {
                    Text = "隔离区是空的 —— 没有拦截到威胁文件。",
                    FontSize = 12, Foreground = Brush(UiPalette.Muted), Margin = new Thickness(0, 2, 0, 0)
                });
            }
            else
            {
                foreach (var item in items)
                {
                    var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var info = new StackPanel();
                    info.Children.Add(new TextBlock
                    {
                        Text = Path.GetFileName(item.Original),
                        FontSize = 12, FontWeight = FontWeights.SemiBold,
                        Foreground = Brush("#F87171"), TextWrapping = TextWrapping.Wrap
                    });
                    info.Children.Add(new TextBlock
                    {
                        Text = item.Time + " · " + item.Reason + "\n原位置：" + Breakable(item.Original),
                        FontSize = 11, Foreground = Brush(UiPalette.Muted), TextWrapping = TextWrapping.Wrap
                    });
                    Grid.SetColumn(info, 0);
                    row.Children.Add(info);

                    var restoreBtn = SmallSecBtn("还原", UiPalette.TextDim);
                    restoreBtn.Click += (_, _) =>
                    {
                        try
                        {
                            SecurityService.Instance.RestoreQuarantine(item);
                            RefreshSecurityLists();
                        }
                        catch (Exception ex) { ShowSecError(ex); }
                    };
                    Grid.SetColumn(restoreBtn, 1);
                    row.Children.Add(restoreBtn);

                    var delBtn = SmallSecBtn("彻底删除", "#F87171");
                    delBtn.Margin = new Thickness(8, 0, 0, 0);
                    delBtn.Click += (_, _) =>
                    {
                        if (MessageBox.Show(this,
                                $"彻底删除隔离文件？\n\n{Path.GetFileName(item.Original)}\n\n删除后无法恢复。",
                                "NEX 安全防护", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                            return;
                        try
                        {
                            SecurityService.Instance.DeleteQuarantine(item);
                            RefreshSecurityLists();
                        }
                        catch (Exception ex) { ShowSecError(ex); }
                    };
                    Grid.SetColumn(delBtn, 2);
                    row.Children.Add(delBtn);

                    _secQuarPanel.Children.Add(row);
                }
            }
        }
    }

    private static Button SmallSecBtn(string text, string color) => new()
    {
        Content = text,
        Padding = new Thickness(12, 4, 12, 4),
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#141E32")),
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
        BorderThickness = new Thickness(0),
        Cursor = System.Windows.Input.Cursors.Hand,
        FontSize = 11,
        VerticalAlignment = VerticalAlignment.Center
    };

    private void ShowSecError(Exception ex) =>
        MessageBox.Show(this, "操作失败：\n" + ex.Message, "NEX 安全防护",
            MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>给无空格的长路径/长词插入零宽断行点，避免把版面撑宽。</summary>
    private static string Breakable(string s) =>
        (s ?? "").Replace("\\", "\\\u200b").Replace("/", "/\u200b");

    /// <summary>威胁命中弹窗（后台线程触发时自动切回 UI 线程）。</summary>
    private void OnSecurityThreat(SecurityEvent e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RefreshSecurityLists();
            MessageBox.Show(this,
                $"{e.Title}\n\n{e.Detail}\n\n来源：{e.Layer} · {e.Time}\n\n详见「安全防护」页的事件日志。",
                "NEX 安全防护告警", MessageBoxButton.OK,
                e.Level == "严重" ? MessageBoxImage.Error : MessageBoxImage.Warning);
        });
    }

    /// <summary>资源下载落地后调用：扫描并隔离威胁文件。返回是否为威胁。</summary>
    private bool ScanDownloadedFile(string path)
    {
        if (!_settings.SecDownloadShield) return false;
        var v = SecurityService.Instance.ScanFile(path);
        if (!v.Threat) return false;
        SecurityService.Instance.Quarantine(path, v.Reason);
        return true;
    }

    // ================= 自动化自检（NEX_SEC_E2E=1） =================

    private async Task RunSecurityE2EAsync()
    {
        var result = new Dictionary<string, object?>();
        try
        {
            await Task.Delay(2200);
            await Dispatcher.InvokeAsync(() =>
            {
                // 自检用大窗口，避免小窗口下右缘内容被裁影响验证
                Width = 1300; Height = 880;
                WindowState = WindowState.Normal;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ShowPage("安全防护");
            });
            await Task.Delay(1500);
            result["pageBuilt"] = await Dispatcher.InvokeAsync(() => _secEventsPanel != null && _secQuarPanel != null);
            result["guardCard"] = await Dispatcher.InvokeAsync(() => _guardStateText != null && _guardPill != null);
            result["guardStatusText"] = await Dispatcher.InvokeAsync(() => _guardStateText?.Text ?? "");
            result["guardDaemonRunning"] = GuardDaemon.IsRunning;
            result["guardAutoStart"] = GuardDaemon.IsAutoStartEnabled();
            result["toggleCount"] = await Dispatcher.InvokeAsync(() =>
                FindVisualChildren<Border>(PageHost).Count(b => "sec-toggle".Equals(b.Tag)));
            result["layout"] = await Dispatcher.InvokeAsync(() =>
            {
                var list = new List<object>();
                foreach (var cb in FindVisualChildren<CheckBox>(PageHost).Take(6))
                {
                    var p = cb.TransformToVisual(PageHost).Transform(new Point(0, 0));
                    list.Add($"checkbox at ({p.X:F0},{p.Y:F0}) size {cb.ActualWidth:F0}x{cb.ActualHeight:F0} vis={cb.IsVisible}");
                }
                list.Add($"PageHost {PageHost.ActualWidth:F0}x{PageHost.ActualHeight:F0}");
                if (PageHost.Content is FrameworkElement fe)
                    list.Add($"content {fe.ActualWidth:F0}x{fe.ActualHeight:F0} desired {fe.DesiredSize.Width:F0}");
                return list;
            });
            await Dispatcher.InvokeAsync(() => SaveElementPng(PageHost, "nex_sec_page.png"));
            await Dispatcher.InvokeAsync(() => SaveElementPng(this, "nex_full_window.png"));

            // 引擎级测试（指向临时目录，不碰真实个人目录）
            var engineResult = await Task.Run(() => SecurityEngineSelfTest());
            foreach (var kv in engineResult) result[kv.Key] = kv.Value;

            result["ok"] = engineResult.Values.All(v => v is bool b && b)
                           && (bool)result["pageBuilt"]!
                           && (bool)result["guardCard"]!;
        }
        catch (Exception ex)
        {
            result["ok"] = false;
            result["error"] = ex.ToString();
        }
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(Path.GetTempPath(), "nex_sec_e2e.json"),
                System.Text.Json.JsonSerializer.Serialize(result,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        await Dispatcher.InvokeAsync(Close);
    }

    /// <summary>安全引擎自测：在临时目录模拟勒索行为与伪装文件，验证五道防线真实可用。</summary>
    private static Dictionary<string, object> SecurityEngineSelfTest()
    {
        var r = new Dictionary<string, object>();
        var tmp = Path.Combine(Path.GetTempPath(), "nex-sec-test-" + DateTime.Now.ToString("HHmmss"));
        Directory.CreateDirectory(tmp);
        try
        {
            var sec = new SecurityService
            {
                WatchDirsOverride = [tmp],
                GameDirectory = tmp,
                CanaryDirOverride = Path.Combine(tmp, "canaries.json")
            };
            try
            {
                // 全部用内存设置（SettingsOverride），不碰真实设置文件
                sec.SettingsOverride = new LauncherSettings
                {
                    SecRansomGuard = true,
                    SecBehaviorMonitor = false,
                    SecSystemGuard = false,
                    SecDownloadShield = true,
                    QuarantineDir = Path.Combine(tmp, "quarantine")
                };
                sec.ApplySettings();

                // 第一道：伪装文件扫描
                var fakeJar = Path.Combine(tmp, "evil.jar");
                File.WriteAllBytes(fakeJar, [0x4D, 0x5A, 0x90, 0x00, 0x01, 0x02, 0x03, 0x04]); // MZ 头
                var fakePng = Path.Combine(tmp, "evil.png");
                File.WriteAllBytes(fakePng, [0x4D, 0x5A, 0x90, 0x00]);
                var goodJar = Path.Combine(tmp, "good.jar");
                File.WriteAllBytes(goodJar, [0x50, 0x4B, 0x03, 0x04, 0x01, 0x02]);
                r["scanFakeJar"] = sec.ScanFile(fakeJar).Threat;
                r["scanFakePng"] = sec.ScanFile(fakePng).Threat;
                r["scanGoodJar"] = !sec.ScanFile(goodJar).Threat;

                // 隔离
                var q = sec.Quarantine(fakeJar, "自测：MZ 冒充 jar");
                r["quarantineMoved"] = File.Exists(q) && !File.Exists(fakeJar);
                r["quarantineListed"] = sec.ListQuarantine().Count == 1;

                // 第二道：蜜罐触发
                sec.DeployCanaries();
                var canaryMap = JsonSerializer.Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(Path.Combine(tmp, "canaries.json")));
                var canaryPath = canaryMap!.Values.First().Split('|')[0];
                var fired = new TaskCompletionSource<bool>();
                sec.ThreatDetected += e => { if (e.Layer.Contains("蜜罐")) fired.TrySetResult(true); };
                // 模拟勒索软件加密诱饵
                File.WriteAllText(canaryPath, Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(64)));
                r["canaryTriggered"] = fired.Task.Wait(15_000);
                // 蜜罐已自动还原（还原是异步的，轮询最多 8 秒）
                var marker = canaryMap.Values.First().Split('|')[1];
                var restored = false;
                for (var i = 0; i < 16; i++)
                {
                    try { restored = File.ReadAllText(canaryPath).Contains(marker); } catch { }
                    if (restored) break;
                    Thread.Sleep(500);
                }
                r["canaryRestored"] = restored;

                // 事件日志
                r["eventLogged"] = sec.ReadEvents(50).Any(e => e.Level == "严重");
            }
            finally
            {
                sec.Dispose();
            }
        }
        catch (Exception ex)
        {
            r["selfTestError"] = ex.ToString();
        }
        finally
        {
            try { Directory.Delete(tmp, true); } catch { }
        }
        return r;
    }
}
