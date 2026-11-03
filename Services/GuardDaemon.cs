using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Win32;

namespace NEXLauncher.Services;

/// <summary>后台守护的运行状态（来自心跳文件）。</summary>
public sealed record GuardStatus(
    bool Running, int Pid, DateTime Started, DateTime LastBeat,
    double HeartbeatAgeSeconds, string Version = "", string LastError = "")
{
    public TimeSpan Uptime => Running && Started > DateTime.MinValue ? DateTime.Now - Started : TimeSpan.Zero;

    public static GuardStatus Stopped { get; } =
        new(false, 0, DateTime.MinValue, DateTime.MinValue, double.MaxValue);
}

/// <summary>
/// 24 小时后台守护：以 <c>--guard</c> 参数启动的独立进程，不显示窗口，
/// 使用与启动器完全相同的五道防火墙引擎。启动器关闭后守护进程继续运行，
/// 并通过 HKCU Run 在登录 Windows 后自动启动。
///
/// 设计要点：
///  - 单实例 Mutex，避免重复守护导致重复告警；
///  - 每 5 秒写心跳文件（PID / 启动时间 / 存活时间），启动器据此显示真实状态；
///  - 每 2 秒检查停止信号与设置文件，设置改动即时生效；
///  - 守护运行期间启动器自身暂停后台监控（只保留扫描/隔离能力），避免双份监控。
/// </summary>
public static class GuardDaemon
{
    public const string GuardArgument = "--guard";
    public const string StopArgument = "--guard-stop";
    private const string MutexName = @"Local\NEXGuardDaemon-7F3C9A21";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "NEXGuard";

    private static Mutex? _mutex;
    private static SecurityService? _engine;
    private static System.Timers.Timer? _beatTimer;
    private static System.Timers.Timer? _controlTimer;
    private static System.Timers.Timer? _gameScanTimer;
    private static DateTime _startedAt = DateTime.Now;
    private static DateTime _settingsStamp = DateTime.MinValue;
    private static string _lastError = "";

    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXLauncher");
    public static string HeartbeatPath => Path.Combine(DataDir, "guard-heartbeat.json");
    public static string StopFlagPath => Path.Combine(DataDir, "guard.stop");
    public static string LogPath => Path.Combine(DataDir, "guard.log");

    /// <summary>当前可执行文件路径（单文件发布时即 NEXLauncher.exe）。</summary>
    public static string ExecutablePath
    {
        get
        {
            try
            {
                var p = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(p) && File.Exists(p)) return p;
                return Process.GetCurrentProcess().MainModule?.FileName ?? "";
            }
            catch { return ""; }
        }
    }

    public static bool IsGuardArgs(string[] args) =>
        args.Any(a => a.Equals(GuardArgument, StringComparison.OrdinalIgnoreCase) ||
                      a.Equals("/guard", StringComparison.OrdinalIgnoreCase));

    public static bool IsStopArgs(string[] args) =>
        args.Any(a => a.Equals(StopArgument, StringComparison.OrdinalIgnoreCase) ||
                      a.Equals("/guard-stop", StringComparison.OrdinalIgnoreCase));

    // ================= 守护模式入口 =================

    /// <summary>以守护模式运行（由 App.OnStartup 调用）。调用后进程常驻后台。</summary>
    public static void Run()
    {
        try { Directory.CreateDirectory(DataDir); } catch { }
        try { if (File.Exists(StopFlagPath)) File.Delete(StopFlagPath); } catch { }

        _mutex = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            Log("已有后台守护在运行，本次启动退出。");
            ShutdownApp();
            return;
        }

        _startedAt = DateTime.Now;
        _settingsStamp = SettingsStamp();

        _engine = new SecurityService();
        _engine.ThreatDetected += e =>
            Log($"[{e.Level}] {e.Layer} · {e.Title}｜{e.Detail.Replace('\n', ' ')}");
        try
        {
            _engine.Start();
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log("防护引擎启动失败：" + ex);
        }

        var settings = LauncherSettings.Load();
        if (settings.SecAlwaysGuard)
        {
            TrySetAutoStart(true, out var msg);
            Log("开机自启：" + msg);
        }

        _beatTimer = new System.Timers.Timer(5_000) { AutoReset = true };
        _beatTimer.Elapsed += (_, _) => { try { WriteHeartbeat(); } catch { } };
        _beatTimer.Start();

        _controlTimer = new System.Timers.Timer(2_000) { AutoReset = true };
        _controlTimer.Elapsed += (_, _) => ControlTick();
        _controlTimer.Start();

        // 游戏目录定时扫描：启动 1 分钟后首扫，之后每 30 分钟一次
        _ = Task.Delay(60_000).ContinueWith(_ =>
        {
            try { _engine?.ScanGameEnvironmentAndReport(); } catch { }
        });
        _gameScanTimer = new System.Timers.Timer(30 * 60 * 1000) { AutoReset = true };
        _gameScanTimer.Elapsed += (_, _) => { try { _engine?.ScanGameEnvironmentAndReport(); } catch { } };
        _gameScanTimer.Start();

        WriteHeartbeat();
        Log($"后台守护已启动 · PID {Environment.ProcessId} · v{typeof(GuardDaemon).Assembly.GetName().Version}");
    }

    private static void ControlTick()
    {
        try
        {
            // 1) 停止信号
            if (File.Exists(StopFlagPath))
            {
                try { File.Delete(StopFlagPath); } catch { }
                Log("收到停止信号，后台守护退出。");
                ShutdownApp();
                return;
            }

            // 2) 设置热重载：启动器里改了开关，守护立即跟随
            var stamp = SettingsStamp();
            if (stamp != _settingsStamp)
            {
                _settingsStamp = stamp;
                _engine?.ApplySettings();
                var s = LauncherSettings.Load();
                TrySetAutoStart(s.SecAlwaysGuard, out _);
                Log("检测到防护设置变更，已重新加载。");
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Log("守护控制循环异常：" + ex.Message);
        }
    }

    private static void ShutdownApp()
    {
        try { _beatTimer?.Stop(); } catch { }
        try { _controlTimer?.Stop(); } catch { }
        try { _gameScanTimer?.Stop(); } catch { }
        try { _engine?.Dispose(); } catch { }
        try { if (File.Exists(HeartbeatPath)) File.Delete(HeartbeatPath); } catch { }
        try { _mutex?.ReleaseMutex(); } catch { }
        try { _mutex?.Dispose(); } catch { }
        try
        {
            if (Application.Current != null) Application.Current.Dispatcher.InvokeShutdown();
            else Environment.Exit(0);
        }
        catch { Environment.Exit(0); }
    }

    private static void WriteHeartbeat()
    {
        var hb = new Heartbeat
        {
            Pid = Environment.ProcessId,
            Started = _startedAt.ToString("O"),
            LastBeat = DateTime.Now.ToString("O"),
            Version = typeof(GuardDaemon).Assembly.GetName().Version?.ToString() ?? "0.4.0",
            LastError = _lastError
        };
        Directory.CreateDirectory(DataDir);
        var tmp = HeartbeatPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(hb));
        File.Move(tmp, HeartbeatPath, overwrite: true);
    }

    private sealed class Heartbeat
    {
        public int Pid { get; set; }
        public string Started { get; set; } = "";
        public string LastBeat { get; set; } = "";
        public string Version { get; set; } = "";
        public string LastError { get; set; } = "";
    }

    private static DateTime SettingsStamp()
    {
        try
        {
            var f = Path.Combine(DataDir, "settings.json");
            return File.Exists(f) ? File.GetLastWriteTimeUtc(f) : DateTime.MinValue;
        }
        catch { return DateTime.MinValue; }
    }

    // ================= 状态查询（启动器使用） =================

    public static GuardStatus GetStatus()
    {
        try
        {
            if (!File.Exists(HeartbeatPath)) return GuardStatus.Stopped;
            var hb = JsonSerializer.Deserialize<Heartbeat>(File.ReadAllText(HeartbeatPath));
            if (hb == null || hb.Pid <= 0) return GuardStatus.Stopped;

            var lastBeat = DateTime.TryParse(hb.LastBeat, out var t) ? t : DateTime.MinValue;
            var started = DateTime.TryParse(hb.Started, out var s) ? s : DateTime.MinValue;
            var age = (DateTime.Now - lastBeat).TotalSeconds;
            var running = age < 30 && IsProcessAlive(hb.Pid);
            return new GuardStatus(running, hb.Pid, started, lastBeat, age, hb.Version, hb.LastError);
        }
        catch { return GuardStatus.Stopped; }
    }

    public static bool IsRunning => GetStatus().Running;

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            var name = p.ProcessName;
            return name.Contains("NEX", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ================= 启动 / 停止（启动器使用） =================

    /// <summary>确保后台守护在运行；已在运行则直接返回 true。</summary>
    public static bool EnsureRunning(out string message)
    {
        var st = GetStatus();
        if (st.Running)
        {
            message = $"后台守护已在运行（PID {st.Pid}）";
            return true;
        }

        var exe = ExecutablePath;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            message = "无法定位启动器程序路径，请从安装目录重新打开 NEX。";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(exe, GuardArgument)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory
            });

            for (var i = 0; i < 30; i++)
            {
                Thread.Sleep(200);
                if (GetStatus().Running)
                {
                    message = "后台守护已启动：关闭启动器后仍会继续防护。";
                    return true;
                }
            }
            message = "后台守护启动超时，请稍后重试。";
            return false;
        }
        catch (Exception ex)
        {
            message = "启动后台守护失败：" + ex.Message;
            return false;
        }
    }

    /// <summary>停止后台守护（先写停止信号，超时则结束进程）。</summary>
    public static bool Stop(out string message)
    {
        if (!GetStatus().Running)
        {
            TryDeleteHeartbeat();
            message = "后台守护未在运行。";
            return true;
        }

        try { File.WriteAllText(StopFlagPath, DateTime.Now.ToString("O")); } catch { }

        for (var i = 0; i < 25; i++)
        {
            Thread.Sleep(200);
            if (!GetStatus().Running)
            {
                TryDeleteHeartbeat();
                message = "后台守护已停止。";
                return true;
            }
        }

        // 停止信号未被响应（进程卡住）→ 直接结束
        try
        {
            var st = GetStatus();
            if (st.Pid > 0) Process.GetProcessById(st.Pid).Kill();
        }
        catch { }
        Thread.Sleep(300);
        TryDeleteHeartbeat();
        message = "后台守护已强制停止。";
        return true;
    }

    private static void TryDeleteHeartbeat()
    {
        try { if (File.Exists(HeartbeatPath)) File.Delete(HeartbeatPath); } catch { }
    }

    // ================= 开机自启 =================

    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string v &&
                   v.Contains(GuardArgument, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>写入/移除 HKCU 开机自启项（无需管理员权限）。</summary>
    public static bool TrySetAutoStart(bool on, out string message)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key == null)
            {
                message = "无法写入开机自启项（注册表被拒绝）。";
                return false;
            }

            if (on)
            {
                var exe = ExecutablePath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    message = "无法定位启动器程序路径，未设置开机自启。";
                    return false;
                }
                var value = $"\"{exe}\" {GuardArgument}";
                if (key.GetValue(RunValueName) as string != value)
                    key.SetValue(RunValueName, value);
                message = "已设置：登录 Windows 后自动开始防护。";
            }
            else
            {
                key.DeleteValue(RunValueName, false);
                message = "已取消开机自启。";
            }
            return true;
        }
        catch (Exception ex)
        {
            message = "设置开机自启失败：" + ex.Message;
            return false;
        }
    }

    // ================= 日志 =================

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n";
            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 2 * 1024 * 1024)
            {
                // 日志超过 2MB 滚动一次，避免无限增长
                var old = LogPath + ".old";
                try { if (File.Exists(old)) File.Delete(old); } catch { }
                try { File.Move(LogPath, old); } catch { }
            }
            File.AppendAllText(LogPath, line);
        }
        catch { }
    }
}
