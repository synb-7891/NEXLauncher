using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NEXLauncher.Services;

namespace NEXLauncher;

public partial class App : Application
{
    // ---- 单实例：同一时间只允许一个启动器主进程 ----
    private const string InstanceMutexName = "NEXLauncher.SingleInstance.v1";
    private const string ActivateEventName = "NEXLauncher.Activate.v1";
    private static Mutex? _instanceMutex;
    private static EventWaitHandle? _activateSignal;
    private static bool _daemonMode;

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
            _daemonMode = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            GuardDaemon.Run();
            return; // 不调用 base：不创建任何窗口
        }

        // 命令行停止守护（供脚本 / 卸载使用）
        if (GuardDaemon.IsStopArgs(e.Args))
        {
            _daemonMode = true;
            GuardDaemon.Stop(out _);
            Shutdown();
            return;
        }

        // ---- 单实例保护：重复双击只把已有窗口拉到前台，不再开第二个进程
        // （两个实例共用同一份 WebView2 配置目录，会互相把对方挤掉）----
        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var splash = new SplashWindow();
        MainWindow = splash;
        splash.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 关闭启动器时顺手把已下载的更新装上（不重开窗口，下次打开即新版）
        if (!_daemonMode)
        {
            try
            {
                if (!UpdateService.IsApplyingUpdate)
                    UpdateService.TryApplyPending(restart: false);
            }
            catch { }
        }
        base.OnExit(e);
    }

    /// <summary>
    /// 抢占单实例锁。返回 false 表示已有实例在跑（此时已通知它把窗口弹到前台）。
    /// </summary>
    private bool AcquireSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirst);
            if (!isFirst)
            {
                // 已有实例：唤醒它，然后自己安静退出
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var existing))
                    {
                        existing.Set();
                        existing.Dispose();
                    }
                }
                catch { }
                return false;
            }

            _activateSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            var listener = new Thread(new ThreadStart(ListenForActivation))
            {
                IsBackground = true,
                Name = "NEX-Activate"
            };
            listener.Start();
            return true;
        }
        catch
        {
            // 锁不可用（极端权限场景）时宁可不挡用户，照常启动
            return true;
        }
    }

    private void ListenForActivation()
    {
        while (true)
        {
            try
            {
                if (_activateSignal == null) return;
                if (!_activateSignal.WaitOne()) continue;
                Dispatcher.Invoke(BringToFront, DispatcherPriority.Send);
            }
            catch
            {
                return;
            }
        }
    }

    /// <summary>把主窗口从最小化/后台拉回前台（第二次双击时用）。</summary>
    private void BringToFront()
    {
        var windows = Windows.Cast<Window>().ToList();
        var target = windows.FirstOrDefault(w => w.IsVisible && w is NEXLauncher.MainWindow)
                     ?? windows.FirstOrDefault(w => w.IsVisible)
                     ?? Current?.MainWindow;
        if (target == null) return;

        try
        {
            if (!target.IsVisible) target.Show();
            if (target.WindowState == WindowState.Minimized) target.WindowState = WindowState.Normal;
            target.ShowInTaskbar = true;
            target.Topmost = true;   // 闪过置顶一次，绕过系统的前台窗口限制
            target.Topmost = false;
            target.Activate();
            var handle = new WindowInteropHelper(target).Handle;
            if (handle != IntPtr.Zero) SetForegroundWindow(handle);
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

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
