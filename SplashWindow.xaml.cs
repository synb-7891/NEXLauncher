using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace NEXLauncher;

public partial class SplashWindow : Window
{
    private readonly DispatcherTimer _timer;

    public SplashWindow()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            try
            {
                var mainWindow = new MainWindow();
                Application.Current.MainWindow = mainWindow;

                // 新版界面（WebView2）就绪前主窗口保持完全不可见，避免旧界面闪现：
                // 以透明、不抢焦点方式启动（拿到窗口句柄供 WebView2 初始化），就绪后淡入。
                mainWindow.Opacity = 0d;
                mainWindow.ShowActivated = false;
                mainWindow.Show();

                var revealed = false;
                void Reveal()
                {
                    if (revealed) return;
                    revealed = true;
                    mainWindow.RevealShell();
                    Close();
                }
                mainWindow.WebShellReady += Reveal;

                // 兜底：15 秒内无论如何都显示窗口（WebView2 卡死等情况）
                var safety = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
                safety.Tick += (_, _) => { safety.Stop(); Reveal(); };
                safety.Start();
            }
            catch (Exception ex)
            {
                // 主窗口构造失败时把完整堆栈落盘，便于定位"启动失败"类问题
                LogCrash(ex);
                MessageBox.Show($"启动失败：\n{ex.Message}", "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
                Application.Current.Shutdown();
            }
        };
        _timer.Start();
    }

    private static void LogCrash(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NEXLauncher");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 启动失败（主窗口构造）:\n{ex}\n\n");
        }
        catch { }
    }
}
