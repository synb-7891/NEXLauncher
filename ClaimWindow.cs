using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace NEXLauncher;

/// <summary>
/// playit.gg 授权窗口：用内置 WebView2 打开 claim 页面，而不是系统浏览器。
/// 原因：Edge 等浏览器的“自动翻译”会破坏 playit.gg 的 React 页面，
/// 报「Failed to execute 'removeChild' on 'Node'」（意外申请错误）。
/// WebView2 内核不会自动翻译，从根上避开这个问题；同时保留复制链接 / 系统浏览器兜底。
/// </summary>
public sealed class ClaimWindow : Window
{
    private readonly Microsoft.Web.WebView2.Wpf.WebView2 _web = new()
    {
        DefaultBackgroundColor = System.Drawing.Color.FromArgb(2, 8, 23)
    };
    private readonly TextBlock _urlText = new()
    {
        FontSize = 11,
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8")),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(10, 0, 10, 0)
    };
    private readonly TextBlock _status = new()
    {
        FontSize = 12,
        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8")),
        Margin = new Thickness(0, 6, 0, 0)
    };

    public string ClaimUrl { get; }

    public ClaimWindow(string url)
    {
        ClaimUrl = url;
        Title = "playit.gg 授权 — 登录并点击 Add agent，完成后会自动分配公网地址";
        Width = 1080;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#020817"));

        var btnStyle = Application.Current.Resources["RoundedButtonStyle"] as Style;
        Brush surface = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#141E32"));
        Brush textDim = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#CBD5E1"));
        Brush accent = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F6A82A"));

        Button MakeBtn(string text, Brush fg, RoutedEventHandler onClick, string? tip = null)
        {
            var b = new Button
            {
                Content = text,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(6, 0, 0, 0),
                Background = surface,
                Foreground = fg,
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#1E293B")),
                Cursor = System.Windows.Input.Cursors.Hand,
                FontSize = 12
            };
            if (btnStyle != null) b.Style = btnStyle;
            if (tip != null) b.ToolTip = tip;
            b.Click += onClick;
            return b;
        }

        var top = new DockPanel { Margin = new Thickness(12, 10, 12, 8), LastChildFill = true };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        btns.Children.Add(MakeBtn("刷新页面", textDim, (_, _) => { try { _web.Reload(); } catch { } }, "重新加载授权页面"));
        btns.Children.Add(MakeBtn("复制授权链接", textDim, (_, _) =>
        {
            try { Clipboard.SetText(ClaimUrl); _status.Text = "已复制授权链接。"; } catch { }
        }, "复制到剪贴板，可粘贴到其它浏览器打开"));
        btns.Children.Add(MakeBtn("用系统浏览器打开", textDim, (_, _) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = ClaimUrl, UseShellExecute = true }); }
            catch (Exception ex) { _status.Text = "打开失败：" + ex.Message; }
        }, "如果内置页面有问题，可用系统浏览器打开（记得关闭该页面的自动翻译）"));
        DockPanel.SetDock(btns, Dock.Right);
        top.Children.Add(btns);
        top.Children.Add(_urlText);
        _urlText.Text = url;

        var tip = new TextBlock
        {
            Text = "在页面登录 playit.gg 并点击 “Add agent” 完成授权，公网地址会自动出现在启动器里。若页面报 removeChild 错误，是浏览器自动翻译导致的——本窗口不会翻译，请放心使用。",
            FontSize = 11,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#8A939B")),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(14, 4, 14, 8)
        };

        var root = new DockPanel();
        root.Children.Add(_web);                       // fill
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        DockPanel.SetDock(tip, Dock.Bottom); root.Children.Add(tip);
        var statusHost = new Border
        {
            Child = _status,
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0B1220")),
            Padding = new Thickness(14, 2, 14, 2)
        };
        DockPanel.SetDock(statusHost, Dock.Bottom); root.Children.Add(statusHost);
        Content = root;

        Loaded += async (_, _) => await InitWebAsync();
    }

    private async Task InitWebAsync()
    {
        _status.Text = "正在加载授权页面...";
        try
        {
            // 用户数据目录放到 LOCALAPPDATA，避免 exe 目录不可写时初始化失败
            var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(
                userDataFolder: System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NEXLauncher", "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
            _web.CoreWebView2.DocumentTitleChanged += (_, _) =>
                Title = (_web.CoreWebView2.DocumentTitle ?? "playit.gg") + " — NEX 授权";
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                // 登录/帮助等新窗口一律在内置窗口内打开，避免跳出系统浏览器再踩翻译坑
                e.Handled = true;
                _web.CoreWebView2.Navigate(e.Uri);
            };
            _web.Source = new Uri(ClaimUrl);
            _status.Text = "页面加载完成。完成登录授权后，回到启动器查看公网地址。";
        }
        catch (Exception ex)
        {
            // WebView2 运行时缺失等：退回系统浏览器
            _status.Text = "内置浏览器不可用（" + ex.Message + "），已改用系统浏览器打开。";
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = ClaimUrl, UseShellExecute = true });
                Close();
            }
            catch
            {
                MessageBox.Show("无法打开授权页面：" + ex.Message + "\n\n授权链接：" + ClaimUrl,
                    "NEX", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }
}
