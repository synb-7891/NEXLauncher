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
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            try
            {
                var mainWindow = new MainWindow();
                Application.Current.MainWindow = mainWindow;

                // 新版界面（WebView2）首帧前，主窗口本身就是云白外壳（标题栏 + 云白底），
                // 不会出现黑屏或旧界面；直接显示，启动最快。
                mainWindow.Show();
                // 主窗口本身已是云白外壳（WebView2 加载完才展开），立即关掉启动画面，不再遮挡
                Close();

                var revealed = false;
                void Reveal()
                {
                    if (revealed) return;
                    revealed = true;
                    mainWindow.RevealShell();
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
