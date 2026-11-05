using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace NEXLauncher.Services;

public partial class MicrosoftLoginWindow : Window
{
    private readonly LauncherSettings _settings;
    private bool _completed;

    /// <summary>登录成功后的结果（null = 用户关闭 / 失败）。</summary>
    public (CmlLib.Core.Auth.MSession Session, string RefreshToken)? Result { get; private set; }

    public MicrosoftLoginWindow(LauncherSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        Loaded += async (_, _) =>
        {
            try
            {
                var dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NEXLauncher", "LoginWebView2");
                System.IO.Directory.CreateDirectory(dir);
                var env = await CoreWebView2Environment.CreateAsync(null, dir);
                await Web.EnsureCoreWebView2Async(env);

                var core = Web.CoreWebView2;
                core.Settings.AreDefaultContextMenusEnabled = true;   // 登录页需要复制粘贴
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.DocumentTitleChanged += (_, _) => Title = core.DocumentTitle ?? Title;

                // 微软桌面流：登录完成后会跳到 oauth20_desktop.srf?code=XXXX
                core.NavigationStarting += (_, e) =>
                {
                    var code = MicrosoftAuthFlow.ExtractCode(e.Uri);
                    if (code == null) return;
                    e.Cancel = true;
                    _ = FinishLoginAsync(code);
                };
                core.NavigationCompleted += (_, _) => LoadingMask.Visibility = Visibility.Collapsed;

                Web.Source = new Uri(MicrosoftAuthFlow.AuthorizeUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show("登录窗口初始化失败：\n" + ex.Message +
                    "\n\n请确认已安装 Microsoft Edge WebView2 运行时。", "NEX Launcher",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
        };
    }

    private async Task FinishLoginAsync(string code)
    {
        if (_completed) return;
        _completed = true;
        LoadingMask.Visibility = Visibility.Visible;
        try
        {
            var (access, refresh) = await MicrosoftAuthFlow.ExchangeCodeAsync(code);
            var (session, finalRefresh) = await MicrosoftAuthFlow.BuildSessionAsync(access, refresh);
            Result = (session, finalRefresh);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            _completed = false;
            LoadingMask.Visibility = Visibility.Collapsed;
            MessageBox.Show("登录失败：\n" + ex.Message, "NEX Launcher",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleMax(); return; }
        try { DragMove(); } catch { }
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMax()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
}
