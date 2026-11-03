using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NEXLauncher.Services;

namespace NEXLauncher;

/// <summary>
/// 联机页「实验性功能」标签：联机体检、服务器状态查询、邀请码、IPv6 直连、自动重连开关。
/// 这些功能不依赖第三方服务，全部本地实现；实验性意味着接口/行为后续可能调整。
/// </summary>
public partial class MainWindow
{
    // ---- 联机页标签 ----
    private int _mpTab;
    private readonly List<Button> _mpTabButtons = [];
    private UIElement? _mpNormalPanel;
    private UIElement? _mpExpPanel;

    // ---- 实验性功能控件 ----
    private TextBox? _expLog;
    private TextBlock? _expSnapshot;
    private TextBox? _expPingAddress;
    private TextBlock? _expPingResult;
    private Image? _expPingIcon;
    private TextBox? _expInviteBox;
    private TextBox? _expInviteInput;
    private TextBlock? _expIpv6Result;
    private CheckBox? _expAutoReconnect;
    private DispatcherTimer? _expReconnectTimer;
    private DateTime _expLastReconnect = DateTime.MinValue;
    private bool _expDiagnosing;

    /// <summary>实验性功能面板（整页）。</summary>
    private UIElement BuildExperimentalPanel()
    {
        var root = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };

        // 顶部提示条
        var notice = Card();
        notice.Margin = new Thickness(0, 4, 0, 0);
        var noticeInner = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
        var badge = new StackPanel { Orientation = Orientation.Horizontal };
        badge.Children.Add(new Border
        {
            Background = Brush(UiPalette.Accent),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "BETA",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brush(UiPalette.Bg)
            }
        });
        badge.Children.Add(Label("实验性功能", 14, UiPalette.Text, FontWeights.SemiBold));
        noticeInner.Children.Add(badge);
        noticeInner.Children.Add(Body("下面的工具用于排查联机故障、验证服务器互通、分享地址。全部为本地计算，不上传任何数据；"
                                    + "接口和按钮位置仍可能调整，遇到异常可随时切回「常规联机」。"));
        _expSnapshot = new TextBlock
        {
            Text = "当前状态：读取中...",
            FontSize = 12,
            Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 2)
        };
        noticeInner.Children.Add(_expSnapshot);
        notice.Children.Add(noticeInner);
        root.Children.Add(notice);

        root.Children.Add(BuildDiagnosticsCard());
        root.Children.Add(BuildPingCard());
        root.Children.Add(BuildInviteCard());
        root.Children.Add(BuildIpv6Card());
        root.Children.Add(BuildExpToggleCard());
        return root;
    }

    /// <summary>卡片 1：联机体检。</summary>
    private UIElement BuildDiagnosticsCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 0);
        card.Children.Add(Label("联机体检（一键诊断）", 13, UiPalette.Text, FontWeights.SemiBold));
        card.Children.Add(Body("依次检查 Java、服务端端口、本地连通、隧道状态、公网地址可达性与网络环境，"
                            + "把结果整理成报告——朋友进不来时先跑一次就知道卡在哪一步。"));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var runBtn = new Button
        {
            Content = "开始体检",
            Padding = new Thickness(18, 8, 18, 8),
            Background = Brush(UiPalette.Accent), Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand, FontSize = 12, Margin = new Thickness(0, 0, 8, 0)
        };
        runBtn.Click += async (_, _) => await RunMultiplayerDiagnosticsAsync();
        row.Children.Add(runBtn);

        var copyBtn = new Button
        {
            Content = "复制报告",
            Padding = new Thickness(14, 8, 14, 8),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12
        };
        copyBtn.Click += (_, _) =>
        {
            var text = _expLog?.Text ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                MessageBox.Show("还没有体检报告，请先点「开始体检」。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Clipboard.SetText(text);
            MessageBox.Show("体检报告已复制，可直接粘贴给朋友或反馈。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        row.Children.Add(copyBtn);
        card.Children.Add(row);

        _expLog = new TextBox
        {
            IsReadOnly = true,
            Height = 190,
            Margin = new Thickness(0, 8, 0, 0),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11,
            Background = Brush(UiPalette.Bg),
            Foreground = Brush(UiPalette.MutedLight),
            BorderBrush = Brush(UiPalette.Border),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            TextWrapping = TextWrapping.NoWrap,
            AcceptsReturn = true,
            Text = "点「开始体检」，结果会显示在这里。"
        };
        card.Children.Add(_expLog);
        return card;
    }

    /// <summary>卡片 2：服务器状态查询（Server List Ping）。</summary>
    private UIElement BuildPingCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 0);
        card.Children.Add(Label("服务器状态查询（MOTD / 在线人数 / 延迟）", 13, UiPalette.Text, FontWeights.SemiBold));
        card.Children.Add(Body("直接按 Minecraft 服务器列表协议查询任意服务器（含你自己的公网地址或本地 127.0.0.1），"
                            + "显示版本、在线人数、简介与握手延迟——用来确认朋友能否真的连上。"));

        var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _expPingAddress = new TextBox
        {
            Text = _playitService.CurrentAddress ?? "127.0.0.1:25565",
            FontSize = 12,
            Padding = new Thickness(8, 6, 8, 6),
            Background = Brush(UiPalette.Bg),
            Foreground = Brush(UiPalette.Text),
            CaretBrush = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border),
            VerticalContentAlignment = VerticalAlignment.Center
        };
        _expPingAddress.KeyDown += async (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Enter) return;
            e.Handled = true;
            await PingExpServerAsync();
        };
        row.Children.Add(_expPingAddress);

        var pingBtn = new Button
        {
            Content = "查询",
            Padding = new Thickness(16, 6, 16, 6),
            Background = Brush(UiPalette.Accent), Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0)
        };
        pingBtn.Click += async (_, _) => await PingExpServerAsync();
        Grid.SetColumn(pingBtn, 1);
        row.Children.Add(pingBtn);
        card.Children.Add(row);

        var resultRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
        _expPingIcon = new Image
        {
            Width = 56,
            Height = 56,
            Margin = new Thickness(0, 0, 12, 0),
            Visibility = Visibility.Collapsed
        };
        RenderOptions.SetBitmapScalingMode(_expPingIcon, BitmapScalingMode.NearestNeighbor);
        resultRow.Children.Add(_expPingIcon);

        _expPingResult = new TextBlock
        {
            Text = "尚未查询。",
            FontSize = 12,
            Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            VerticalAlignment = VerticalAlignment.Center
        };
        resultRow.Children.Add(_expPingResult);
        card.Children.Add(resultRow);
        return card;
    }

    /// <summary>卡片 3：邀请码。</summary>
    private UIElement BuildInviteCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 0);
        card.Children.Add(Label("邀请码分享", 13, UiPalette.Text, FontWeights.SemiBold));
        card.Children.Add(Body("把公网地址编码成一串短邀请码，绕过聊天软件对长地址/冒号的折叠与拦截；"
                            + "好友把邀请码粘回启动器即可还原成服务器地址。"));

        _expInviteBox = new TextBox
        {
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 6, 0, 0),
            Background = Brush(UiPalette.Bg),
            Foreground = Brush(UiPalette.Accent),
            BorderBrush = Brush(UiPalette.Border),
            Text = ""
        };
        card.Children.Add(_expInviteBox);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var genBtn = new Button
        {
            Content = "生成邀请码",
            Padding = new Thickness(16, 7, 16, 7),
            Background = Brush(UiPalette.Accent), Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand, FontSize = 12, Margin = new Thickness(0, 0, 8, 0)
        };
        genBtn.Click += (_, _) => GenerateInviteCode();
        btnRow.Children.Add(genBtn);

        var copyBtn = new Button
        {
            Content = "复制邀请码",
            Padding = new Thickness(14, 7, 14, 7),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12
        };
        copyBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_expInviteBox?.Text))
            {
                MessageBox.Show("请先点「生成邀请码」。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Clipboard.SetText(_expInviteBox.Text);
            MessageBox.Show("邀请码已复制，发给朋友即可。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
        };
        btnRow.Children.Add(copyBtn);
        card.Children.Add(btnRow);

        var parseRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        parseRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        parseRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _expInviteInput = new TextBox
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Padding = new Thickness(8, 6, 8, 6),
            Background = Brush(UiPalette.Bg),
            Foreground = Brush(UiPalette.Text),
            CaretBrush = Brush(UiPalette.Text),
            BorderBrush = Brush(UiPalette.Border),
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = "把好友发来的邀请码粘贴到这里"
        };
        parseRow.Children.Add(_expInviteInput);

        var parseBtn = new Button
        {
            Content = "解析邀请码",
            Padding = new Thickness(16, 6, 16, 6),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.Accent),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0)
        };
        parseBtn.Click += (_, _) => ParseInviteCode();
        Grid.SetColumn(parseBtn, 1);
        parseRow.Children.Add(parseBtn);
        card.Children.Add(parseRow);
        return card;
    }

    /// <summary>卡片 4：IPv6 直连。</summary>
    private UIElement BuildIpv6Card()
    {
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 0);
        card.Children.Add(Label("IPv6 直连（免隧道）", 13, UiPalette.Text, FontWeights.SemiBold));
        card.Children.Add(Body("校园网/部分宽带会分配公网 IPv6，双方都有 IPv6 时可以跳过内网穿透直接连，延迟更低。"
                            + "下面列出本机可用于直连的 IPv6 地址。"));

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        var scanBtn = new Button
        {
            Content = "检测本机 IPv6",
            Padding = new Thickness(16, 7, 16, 7),
            Background = Brush(UiPalette.Accent), Foreground = Brush(UiPalette.Bg),
            BorderThickness = new Thickness(0), FontWeight = FontWeights.SemiBold,
            Cursor = Cursors.Hand, FontSize = 12, Margin = new Thickness(0, 0, 8, 0)
        };
        scanBtn.Click += (_, _) => ScanIpv6Addresses();
        row.Children.Add(scanBtn);

        var copyBtn = new Button
        {
            Content = "复制首个地址",
            Padding = new Thickness(14, 7, 14, 7),
            Background = Brush(UiPalette.SurfaceAlt), Foreground = Brush(UiPalette.TextDim),
            BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12
        };
        copyBtn.Click += (_, _) => CopyFirstIpv6();
        row.Children.Add(copyBtn);
        card.Children.Add(row);

        _expIpv6Result = new TextBlock
        {
            Text = "尚未检测。",
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(0, 8, 0, 0)
        };
        card.Children.Add(_expIpv6Result);
        return card;
    }

    /// <summary>卡片 5：实验性开关。</summary>
    private UIElement BuildExpToggleCard()
    {
        var card = Card();
        card.Margin = new Thickness(0, 16, 0, 0);
        card.Children.Add(Label("实验性开关", 13, UiPalette.Text, FontWeights.SemiBold));

        _expAutoReconnect = new CheckBox
        {
            Content = "隧道掉线后自动重连（每 20 秒检查，服务端仍在运行时自动重启隧道）",
            IsChecked = _settings.AutoReconnectTunnel,
            FontSize = 12,
            Foreground = Brush(UiPalette.TextDim),
            Margin = new Thickness(0, 8, 0, 0)
        };
        _expAutoReconnect.Checked += (_, _) => SetAutoReconnect(true);
        _expAutoReconnect.Unchecked += (_, _) => SetAutoReconnect(false);
        card.Children.Add(_expAutoReconnect);

        card.Children.Add(new TextBlock
        {
            Text = "开关会写入设置，下次启动仍然生效。自动重连只在「一键开服」的服务端仍在运行时生效，"
                 + "避免在你关服后还把隧道拉起来。",
            FontSize = 11,
            Foreground = Brush(UiPalette.Muted),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });

        if (_settings.AutoReconnectTunnel) EnsureReconnectTimer();
        return card;
    }

    // ============================================================
    //  实验性功能实现
    // ============================================================

    /// <summary>实验性页顶部的环境快照。</summary>
    private async Task RefreshExperimentalSnapshotAsync()
    {
        if (_expSnapshot == null) return;
        try
        {
            var server = _oneClickService.IsRunning ? "服务端运行中" : "服务端未运行";
            var tunnel = _playitService.IsRunning
                ? (_playitService.CurrentAddress is { Length: > 0 } a ? $"隧道运行中（{a}）" : "隧道运行中（等待地址）")
                : "隧道未启动";
            var agent = _playitService.IsAgentValid ? "agent 就绪" : "agent 未下载";
            var java = OneClickServerService.FindJava();
            var javaText = string.IsNullOrEmpty(java) ? "未找到 Java" : "Java 就绪";
            _expSnapshot.Text = $"当前状态：{server} · {tunnel} · {agent} · {javaText}";

            if (_expPingAddress != null && string.IsNullOrWhiteSpace(_expPingAddress.Text))
                _expPingAddress.Text = _playitService.CurrentAddress ?? "127.0.0.1:25565";
            await Task.CompletedTask;
        }
        catch
        {
            _expSnapshot.Text = "当前状态：读取失败（可运行一次体检查看详情）";
        }
    }

    /// <summary>联机体检：逐项检查并输出报告。</summary>
    private async Task RunMultiplayerDiagnosticsAsync()
    {
        if (_expLog == null || _expDiagnosing) return;
        _expDiagnosing = true;
        var sb = new StringBuilder();
        void W(string s)
        {
            sb.AppendLine(s);
            _expLog.Text = sb.ToString();
            _expLog.ScrollToEnd();
        }

        W($"NEX 联机体检 · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W(new string('─', 52));

        // 1) Java
        try
        {
            var java = OneClickServerService.FindJava();
            if (!string.IsNullOrEmpty(java) && File.Exists(java))
            {
                var ver = await GetJavaVersionAsync(java);
                W($"✓ Java：{java}");
                if (!string.IsNullOrWhiteSpace(ver)) W($"          版本：{ver}");
                var major = ParseJavaMajor(ver);
                if (major > 0 && major < 17)
                    W("⚠ Java 版本偏低，1.17+ 服务端需要 Java 17 及以上（百宝箱 → Java 可一键安装）。");
            }
            else
            {
                W("✗ Java：未找到。开服需要 Java 17 或更高（百宝箱 → Java 可一键安装）。");
            }
        }
        catch (Exception ex) { W("✗ Java 检测失败：" + ex.Message); }

        // 2) 服务端与端口
        var port = 25565;
        try
        {
            if (_oneClickService.IsRunning)
            {
                W($"✓ 服务端：运行中（{_oneClickService.ServerDir}）");
                port = ReadServerPort(_oneClickService.ServerDir) ?? 25565;
                W($"          监听端口：{port}");
            }
            else
            {
                W("· 服务端：未在启动器内运行（若用游戏内「对局域网开放」可忽略本项）。");
                port = ReadServerPort(_oneClickService.ServerDir) ?? 25565;
            }
        }
        catch (Exception ex) { W("· 服务端信息读取失败：" + ex.Message); }

        // 3) 本地端口连通
        try
        {
            var (ok, ms) = await TcpProbeAsync("127.0.0.1", port, 1200);
            W(ok
                ? $"✓ 本地端口：127.0.0.1:{port} 可连接（{ms} ms）"
                : $"✗ 本地端口：127.0.0.1:{port} 连不上（服务器可能还在启动，或端口被改过）。");
        }
        catch (Exception ex) { W("✗ 本地端口检测失败：" + ex.Message); }

        // 4) 隧道
        try
        {
            var agent = _playitService.IsAgentValid ? "已就绪" : "未下载（首次用隧道会自动下载）";
            W($"· 隧道程序：{agent}");
            if (_playitService.IsRunning)
            {
                W("✓ 隧道：运行中");
                var addr = _playitService.CurrentAddress;
                if (string.IsNullOrEmpty(addr))
                {
                    W("⚠ 隧道还没分配公网地址：首次使用需点「去网页授权」完成 playit.gg 授权。");
                    if (!string.IsNullOrEmpty(_playitService.ClaimUrl))
                        W($"          授权链接：{_playitService.ClaimUrl}");
                }
                else
                {
                    W($"          公网地址：{addr}");
                    var (host, p) = SplitAddress(addr);
                    var dns = await SafeDnsAsync(host);
                    W(dns != null ? $"          域名解析：{host} → {dns}" : $"✗ 域名解析失败：{host}（检查网络或 DNS）");
                    var (ok, ms) = await TcpProbeAsync(host, p, 3000);
                    W(ok
                        ? $"✓ 公网地址：{addr} 外部可连通（{ms} ms）"
                        : $"✗ 公网地址：{addr} 连不上（隧道未就绪 / 授权未完成 / 服务端未监听）。");
                }
            }
            else W("· 隧道：未启动。");
        }
        catch (Exception ex) { W("· 隧道检测失败：" + ex.Message); }

        // 5) 网络环境
        try
        {
            var pub = await GetPublicIpAsync();
            W(pub != null ? $"· 出口公网 IP：{pub}" : "· 出口公网 IP：获取失败（可能被网络限制）");
            var lan = GetLocalIPv4();
            W(lan.Count > 0
                ? "· 局域网地址：" + string.Join("、", lan.Select(ip => $"{ip}:{port}"))
                : "· 局域网地址：未获取到");
            W("  同一路由器下的朋友可直接用局域网地址加入，不必走隧道。");
        }
        catch (Exception ex) { W("· 网络信息获取失败：" + ex.Message); }

        W(new string('─', 52));
        W("体检完成。若有 ✗ 项，优先解决第一处 ✗；仅 ⚠ 项通常不影响联机。");
        W($"建议：把你的公网地址发给朋友前，先用上面的「服务器状态查询」确认能返回 MOTD。");
        _expDiagnosing = false;
        await RefreshExperimentalSnapshotAsync();
    }

    /// <summary>服务器列表 Ping：返回 MOTD / 版本 / 在线人数 / 延迟，并展示服务器图标。</summary>
    private async Task PingExpServerAsync()
    {
        if (_expPingResult == null || _expPingAddress == null) return;
        var raw = _expPingAddress.Text.Trim();
        if (raw.Length == 0)
        {
            _expPingResult.Text = "请先填写服务器地址，例如 127.0.0.1:25565 或 xxx.at.playit.gg:12345。";
            return;
        }

        var (host, port) = SplitAddress(raw);
        _expPingResult.Foreground = Brush(UiPalette.Muted);
        _expPingResult.Text = "查询中...";
        if (_expPingIcon != null) _expPingIcon.Visibility = Visibility.Collapsed;

        try
        {
            var r = await ServerListPingAsync(host, port, 8000);
            var lines = new List<string>
            {
                $"✓ 在线 · {r.Version}",
                $"人数：{r.Online}/{r.Max}    延迟：{r.LatencyMs} ms",
                $"简介：{r.Motd}"
            };
            _expPingResult.Text = string.Join(Environment.NewLine, lines);
            _expPingResult.Foreground = Brush(UiPalette.TextDim);

            if (_expPingIcon != null && !string.IsNullOrEmpty(r.IconBase64))
            {
                try
                {
                    var b64 = r.IconBase64;
                    var comma = b64.IndexOf(',');
                    if (comma >= 0) b64 = b64[(comma + 1)..];
                    var bytes = Convert.FromBase64String(b64);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = new MemoryStream(bytes);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    _expPingIcon.Source = bmp;
                    _expPingIcon.Visibility = Visibility.Visible;
                }
                catch { /* 图标解码失败不影响结果 */ }
            }
        }
        catch (Exception ex)
        {
            _expPingResult.Foreground = Brush(UiPalette.Muted);
            _expPingResult.Text = $"✗ 查询失败：{ex.Message}{Environment.NewLine}"
                                + "常见原因：服务器未启动、端口填错、公网隧道没连上、地址需要带端口。";
        }
    }

    // ---- 邀请码 ----
    private sealed record InvitePayload(int V, string Address, string Note);

    private void GenerateInviteCode()
    {
        if (_expInviteBox == null) return;
        var addr = _playitService.CurrentAddress ?? _playitService.ClaimUrl;
        if (string.IsNullOrWhiteSpace(addr)) addr = "127.0.0.1:25565";
        try
        {
            var payload = new InvitePayload(1, addr, "NEX Launcher 联机邀请");
            var json = JsonSerializer.Serialize(payload);
            var code = "NEX-" + Base64UrlEncode(Encoding.UTF8.GetBytes(json));
            _expInviteBox.Text = code;
            Clipboard.SetText(code);
            MessageBox.Show("邀请码已生成并复制到剪贴板：\n" + code, "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("生成失败：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ParseInviteCode()
    {
        var code = _expInviteInput?.Text.Trim() ?? "";
        if (code.Length == 0)
        {
            MessageBox.Show("请先粘贴好友发来的邀请码。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            if (code.StartsWith("NEX-", StringComparison.OrdinalIgnoreCase)) code = code[4..];
            var json = Encoding.UTF8.GetString(Base64UrlDecode(code));
            var payload = JsonSerializer.Deserialize<InvitePayload>(json);
            if (payload == null || string.IsNullOrWhiteSpace(payload.Address))
                throw new FormatException("邀请码内容不完整");

            Clipboard.SetText(payload.Address);
            if (_expPingAddress != null) _expPingAddress.Text = payload.Address;
            MessageBox.Show($"邀请码解析成功，服务器地址：\n{payload.Address}\n\n已复制到剪贴板，"
                          + "在游戏「多人游戏 → 添加服务器」粘贴即可。", "NEX",
                          MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("邀请码无效：" + ex.Message, "NEX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var v = s.Replace('-', '+').Replace('_', '/');
        switch (v.Length % 4)
        {
            case 2: v += "=="; break;
            case 3: v += "="; break;
        }
        return Convert.FromBase64String(v);
    }

    // ---- IPv6 ----
    private List<string> _ipv6Found = [];

    private void ScanIpv6Addresses()
    {
        if (_expIpv6Result == null) return;
        _ipv6Found = GetGlobalIpv6();
        if (_ipv6Found.Count == 0)
        {
            _expIpv6Result.Text = "未检测到公网 IPv6 地址。\n"
                                + "说明：多数家庭宽带默认只有 IPv4，可尝试在路由器里开启 IPv6；"
                                + "没有 IPv6 时用公网隧道即可。";
            return;
        }

        var port = ReadServerPort(_oneClickService.ServerDir) ?? 25565;
        var sb = new StringBuilder();
        sb.AppendLine($"检测到 {_ipv6Found.Count} 个可用 IPv6 地址（端口 {port}）：");
        foreach (var ip in _ipv6Found) sb.AppendLine("  [" + ip + "]:" + port);
        sb.AppendLine("把上面任一条发给同样有 IPv6 的朋友，在游戏里添加服务器即可直连。");
        _expIpv6Result.Text = sb.ToString().TrimEnd();
    }

    private void CopyFirstIpv6()
    {
        if (_ipv6Found.Count == 0) ScanIpv6Addresses();
        if (_ipv6Found.Count == 0)
        {
            MessageBox.Show("没有可用的 IPv6 地址。", "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var port = ReadServerPort(_oneClickService.ServerDir) ?? 25565;
        var text = $"[{_ipv6Found[0]}]:{port}";
        Clipboard.SetText(text);
        MessageBox.Show("已复制：\n" + text, "NEX", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static List<string> GetGlobalIpv6()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    var a = ua.Address;
                    if (a.AddressFamily != AddressFamily.InterNetworkV6) continue;
                    if (IPAddress.IsLoopback(a) || a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || a.IsIPv6Multicast) continue;
                    var s = a.ToString();
                    if (s.StartsWith("fd", StringComparison.OrdinalIgnoreCase)) continue; // 唯一本地地址（内网）
                    if (!list.Contains(s)) list.Add(s);
                }
            }
        }
        catch { }
        return list;
    }

    // ---- 自动重连 ----
    private void SetAutoReconnect(bool value)
    {
        _settings.AutoReconnectTunnel = value;
        _settings.Save();
        if (value)
        {
            EnsureReconnectTimer();
            if (_expLog != null)
            {
                _expLog.AppendText($"[{DateTime.Now:HH:mm:ss}] 已开启隧道自动重连。" + Environment.NewLine);
                _expLog.ScrollToEnd();
            }
        }
        else
        {
            _expReconnectTimer?.Stop();
            if (_expLog != null)
            {
                _expLog.AppendText($"[{DateTime.Now:HH:mm:ss}] 已关闭隧道自动重连。" + Environment.NewLine);
                _expLog.ScrollToEnd();
            }
        }
    }

    private void EnsureReconnectTimer()
    {
        if (_expReconnectTimer != null)
        {
            if (!_expReconnectTimer.IsEnabled) _expReconnectTimer.Start();
            return;
        }
        _expReconnectTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        _expReconnectTimer.Tick += async (_, _) => await ExperimentalReconnectTickAsync();
        _expReconnectTimer.Start();
    }

    private async Task ExperimentalReconnectTickAsync()
    {
        try
        {
            if (!_settings.AutoReconnectTunnel) return;
            if (!_oneClickService.IsRunning) return;
            if (_playitService.IsRunning && !string.IsNullOrEmpty(_playitService.CurrentAddress)) return;
            if (DateTime.UtcNow - _expLastReconnect < TimeSpan.FromSeconds(45)) return;

            _expLastReconnect = DateTime.UtcNow;
            if (_expLog != null)
            {
                _expLog.AppendText($"[{DateTime.Now:HH:mm:ss}] 隧道已掉线，正在自动重连..." + Environment.NewLine);
                _expLog.ScrollToEnd();
            }
            if (_playitService.IsRunning) _playitService.Stop();
            await StartTunnelForServerAsync();
            if (_expLog != null)
            {
                _expLog.AppendText($"[{DateTime.Now:HH:mm:ss}] 重连请求已发出，等待公网地址..."
                                 + Environment.NewLine);
                _expLog.ScrollToEnd();
            }
        }
        catch (Exception ex)
        {
            if (_expLog != null)
            {
                _expLog.AppendText($"[{DateTime.Now:HH:mm:ss}] 自动重连失败：{ex.Message}" + Environment.NewLine);
                _expLog.ScrollToEnd();
            }
        }
    }

    // ============================================================
    //  协议 / 系统辅助
    // ============================================================

    private sealed record PingResult(string Version, int Online, int Max, string Motd, int LatencyMs, string? IconBase64);

    /// <summary>Minecraft 服务器列表查询（handshake + status）。</summary>
    private static async Task<PingResult> ServerListPingAsync(string host, int port, int timeoutMs)
    {
        using var client = new TcpClient { NoDelay = true };
        var connect = client.ConnectAsync(host, port);
        if (await Task.WhenAny(connect, Task.Delay(timeoutMs)) != connect)
            throw new TimeoutException("连接服务器超时");
        await connect;

        using var ns = client.GetStream();
        ns.ReadTimeout = timeoutMs;
        ns.WriteTimeout = timeoutMs;

        // handshake：协议号 760（1.19.1，状态查询对协议号不敏感）
        using (var ms = new MemoryStream())
        {
            WriteVarInt(ms, 0x00);
            WriteVarInt(ms, 760);
            WriteString(ms, host);
            ms.WriteByte((byte)(port >> 8));
            ms.WriteByte((byte)(port & 0xFF));
            WriteVarInt(ms, 1);  // next state = status
            await WritePacketAsync(ns, ms.ToArray());
        }
        await WritePacketAsync(ns, new byte[] { 0x00 }); // status request

        var sw = Stopwatch.StartNew();
        var len = await ReadVarIntAsync(ns);
        if (len <= 0 || len > 4 * 1024 * 1024) throw new InvalidDataException("返回数据长度异常");
        var buf = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = await ns.ReadAsync(buf.AsMemory(read, len - read));
            if (n <= 0) throw new IOException("连接被服务器关闭");
            read += n;
        }
        var latency = (int)sw.ElapsedMilliseconds;

        using var payload = new MemoryStream(buf);
        _ = await ReadVarIntAsync(payload);      // packet id（0x00）
        var json = await ReadStringAsync(payload);

        using var doc = JsonDocument.Parse(json);
        var rootEl = doc.RootElement;
        var version = rootEl.TryGetProperty("version", out var v) && v.TryGetProperty("name", out var vn)
            ? vn.GetString() ?? "未知" : "未知";
        var online = 0; var max = 0;
        if (rootEl.TryGetProperty("players", out var players))
        {
            if (players.TryGetProperty("online", out var o) && o.TryGetInt32(out var ov)) online = ov;
            if (players.TryGetProperty("max", out var m) && m.TryGetInt32(out var mv)) max = mv;
        }
        var motd = rootEl.TryGetProperty("description", out var desc) ? FlattenChat(desc) : "";
        var icon = rootEl.TryGetProperty("favicon", out var fav) ? fav.GetString() : null;

        return new PingResult(version, online, max, string.IsNullOrWhiteSpace(motd) ? "（无）" : motd, latency, icon);
    }

    /// <summary>把聊天组件（字符串或 text/extra 结构）压平成纯文本，去掉 § 颜色码。</summary>
    private static string FlattenChat(JsonElement el)
    {
        var sb = new StringBuilder();
        void Walk(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.String:
                    sb.Append(e.GetString());
                    break;
                case JsonValueKind.Array:
                    foreach (var c in e.EnumerateArray()) Walk(c);
                    break;
                case JsonValueKind.Object:
                    if (e.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        sb.Append(t.GetString());
                    if (e.TryGetProperty("extra", out var extra)) Walk(extra);
                    break;
            }
        }
        Walk(el);
        var s = sb.ToString();
        var clean = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '§' && i + 1 < s.Length) { i++; continue; }
            clean.Append(s[i]);
        }
        return clean.ToString().Replace("\n", " ").Trim();
    }

    private static Task WritePacketAsync(Stream s, byte[] payload)
    {
        using var ms = new MemoryStream();
        WriteVarInt(ms, payload.Length);
        ms.Write(payload);
        return s.WriteAsync(ms.ToArray()).AsTask();
    }

    private static void WriteVarInt(Stream s, int value)
    {
        var v = (uint)value;
        while (true)
        {
            var b = (byte)(v & 0x7F);
            v >>= 7;
            if (v != 0) s.WriteByte((byte)(b | 0x80));
            else { s.WriteByte(b); return; }
        }
    }

    private static async Task<int> ReadVarIntAsync(Stream s)
    {
        var result = 0;
        for (var shift = 0; shift < 35; shift += 7)
        {
            var buf = new byte[1];
            var n = await s.ReadAsync(buf.AsMemory(0, 1));
            if (n <= 0) throw new IOException("读取数据时连接中断");
            var b = buf[0];
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
        }
        throw new InvalidDataException("VarInt 过长");
    }

    private static void WriteString(Stream s, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(s, bytes.Length);
        s.Write(bytes);
    }

    private static async Task<string> ReadStringAsync(Stream s)
    {
        var len = await ReadVarIntAsync(s);
        if (len < 0 || len > 2 * 1024 * 1024) throw new InvalidDataException("字符串长度异常");
        var buf = new byte[len];
        var read = 0;
        while (read < len)
        {
            var n = await s.ReadAsync(buf.AsMemory(read, len - read));
            if (n <= 0) throw new IOException("读取字符串时连接中断");
            read += n;
        }
        return Encoding.UTF8.GetString(buf);
    }

    /// <summary>拆地址：支持 host:port、[ipv6]:port、纯 host（默认 25565）。</summary>
    private static (string Host, int Port) SplitAddress(string raw, int defaultPort = 25565)
    {
        var s = raw.Trim();
        // 去掉可能的 playit 地址前缀或协议
        if (s.Contains("://")) s = s[(s.IndexOf("://", StringComparison.Ordinal) + 3)..];
        s = s.TrimEnd('/');
        if (s.StartsWith('['))
        {
            var close = s.IndexOf(']');
            if (close > 0)
            {
                var h = s[1..close];
                var rest = s[(close + 1)..];
                if (rest.StartsWith(':') && int.TryParse(rest[1..], out var p)) return (h, p);
                return (h, defaultPort);
            }
        }
        var idx = s.LastIndexOf(':');
        if (idx > 0 && s.IndexOf(':') == idx && int.TryParse(s[(idx + 1)..], out var port))
            return (s[..idx], port);
        return (s, defaultPort);
    }

    private static async Task<(bool Ok, int Ms)> TcpProbeAsync(string host, int port, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var c = new TcpClient();
            var task = c.ConnectAsync(host, port);
            if (await Task.WhenAny(task, Task.Delay(timeoutMs)) != task) return (false, timeoutMs);
            await task;
            return (c.Connected, (int)sw.ElapsedMilliseconds);
        }
        catch { return (false, (int)sw.ElapsedMilliseconds); }
    }

    private static async Task<string?> SafeDnsAsync(string host)
    {
        try
        {
            var addrs = await Dns.GetHostAddressesAsync(host);
            return addrs.Length > 0 ? addrs[0].ToString() : null;
        }
        catch { return null; }
    }

    private static async Task<string?> GetPublicIpAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var s = await http.GetStringAsync("https://api.ipify.org");
            return s.Trim();
        }
        catch { return null; }
    }

    private static List<string> GetLocalIPv4()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var s = ua.Address.ToString();
                    if (s.StartsWith("127.") || s.StartsWith("169.254.")) continue;
                    if (!list.Contains(s)) list.Add(s);
                }
            }
        }
        catch { }
        return list;
    }

    /// <summary>读 server.properties 的 server-port（读不到返回 null）。</summary>
    private static int? ReadServerPort(string? serverDir)
    {
        try
        {
            if (string.IsNullOrEmpty(serverDir)) return null;
            var file = Path.Combine(serverDir, "server.properties");
            if (!File.Exists(file)) return null;
            foreach (var line in File.ReadAllLines(file))
            {
                var t = line.Trim();
                if (!t.StartsWith("server-port", StringComparison.OrdinalIgnoreCase)) continue;
                var idx = t.IndexOf('=');
                if (idx < 0) continue;
                if (int.TryParse(t[(idx + 1)..].Trim(), out var p) && p > 0 && p < 65536) return p;
            }
        }
        catch { }
        return null;
    }

    private static async Task<string> GetJavaVersionAsync(string java)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = java,
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            var first = err.Split('\n').FirstOrDefault(l => l.Contains("version", StringComparison.OrdinalIgnoreCase));
            return first?.Trim().Trim('"') ?? "";
        }
        catch { return ""; }
    }

    private static int ParseJavaMajor(string versionLine)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(versionLine, "\"(\\d+)(?:\\.(\\d+))?");
            if (!m.Success) return 0;
            var major = int.Parse(m.Groups[1].Value);
            if (major == 1 && m.Groups[2].Success) return int.Parse(m.Groups[2].Value);
            return major;
        }
        catch { return 0; }
    }

    #region 自动化自检（NEX_MP_EXP=1 触发）
    /// <summary>联机页「实验性功能」回归自检：切标签、跑体检、查询公网服务器、邀请码往返、IPv6 扫描。</summary>
    private async Task RunExperimentalE2EAsync()
    {
        var result = new Dictionary<string, object?>();
        try
        {
            await Task.Delay(1500);
            ShowPage("联机");
            _mpTab = 1;
            RefreshMultiplayerTabs();
            await Task.Delay(1500);

            result["expPanelVisible"] = _mpExpPanel?.Visibility == Visibility.Visible;
            result["normalPanelHidden"] = _mpNormalPanel?.Visibility != Visibility.Visible;
            result["tabCount"] = _mpTabButtons.Count;
            result["tabLabels"] = string.Join(" / ", _mpTabButtons.Select(b => b.Content?.ToString()));

            // 1) 体检
            await RunMultiplayerDiagnosticsAsync();
            await Task.Delay(600);
            var diag = _expLog?.Text ?? "";
            result["diagLength"] = diag.Length;
            result["diagOk"] = diag.Count(c => c == '✓');
            result["diagFail"] = diag.Count(c => c == '✗');
            result["diagHasJava"] = diag.Contains("Java");
            result["diagHasTunnel"] = diag.Contains("隧道");

            // 2) 服务器状态查询（公网知名服务器，验证协议实现）
            if (_expPingAddress != null) _expPingAddress.Text = "mc.hypixel.net";
            await PingExpServerAsync();
            await Task.Delay(800);
            result["pingText"] = _expPingResult?.Text;
            result["pingOk"] = _expPingResult?.Text?.StartsWith("✓") == true;

            // 3) 邀请码往返（不弹窗，直接走编解码）
            var payload = new InvitePayload(1, _playitService.CurrentAddress ?? "127.0.0.1:25565", "NEX Launcher 联机邀请");
            var code = "NEX-" + Base64UrlEncode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)));
            if (_expInviteBox != null) _expInviteBox.Text = code;
            var decoded = JsonSerializer.Deserialize<InvitePayload>(
                Encoding.UTF8.GetString(Base64UrlDecode(code[4..])));
            result["inviteCode"] = code;
            result["inviteRoundTrip"] = decoded?.Address == payload.Address;

            // 4) IPv6 扫描
            ScanIpv6Addresses();
            result["ipv6Text"] = _expIpv6Result?.Text?.Split('\n').FirstOrDefault();
            result["ipv6Count"] = _ipv6Found.Count;

            // 5) 自动重连开关持久化
            result["autoReconnectDefault"] = _settings.AutoReconnectTunnel;

            await Task.Delay(600);
            await Dispatcher.InvokeAsync(() => SaveElementPng(PageHost, "nex_mp_exp.png"));
            result["screenshot"] = "nex_mp_exp.png";
        }
        catch (Exception ex)
        {
            result["error"] = ex.ToString();
        }
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "nex_mp_exp_e2e.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 忽略自检输出失败 */ }
    }
    #endregion
}
