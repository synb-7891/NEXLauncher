using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using NEXLauncher.Services;

namespace NEXLauncher;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // ---- 24 小时后台守护：独立进程、无窗口，关闭启动器后继续防护 ----
        if (GuardDaemon.IsGuardArgs(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            GuardDaemon.Run();
            return; // 不调用 base：不创建任何窗口
        }

        // 命令行停止守护（供脚本 / 卸载使用）
        if (GuardDaemon.IsStopArgs(e.Args))
        {
            GuardDaemon.Stop(out _);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        var splash = new SplashWindow();
        MainWindow = splash;
        splash.Show();
    }

    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NEXLauncher", "crash.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n\n");
        }
        catch { }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("UI Exception: " + e.Exception);
        MessageBox.Show($"发生错误：\n{e.Exception.Message}\n\n{e.Exception.StackTrace}", "NEX", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log("Fatal: " + ex);
    }
}
