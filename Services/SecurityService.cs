using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace NEXLauncher.Services;

/// <summary>安全事件（写入事件日志与 UI 提示）。</summary>
public sealed record SecurityEvent(
    string Time, string Level, string Layer, string Title, string Detail, string? TargetPath = null);

/// <summary>单文件扫描结论。</summary>
public sealed record ScanVerdict(string FilePath, bool Threat, string Level, string Reason);

/// <summary>
/// NEX 安全防护引擎（五道防火墙，全部本地实现，不上传任何数据）：
///  1. 下载防护   —— 资源落地前扫描：伪装扩展名（PE/脚本冒充 jar、png 等）、危险脚本、异常大小。
///  2. 勒索蜜罐   —— 在桌面/文档/图片/游戏目录布置诱饵文件，被改动立即告警并自动还原。
///  3. 行为监控   —— FileSystemWatcher 监控文档/游戏目录，短时间大量改写（批量加密特征）即报警。
///  4. 系统守护   —— hosts 文件、注册表 Run/RunOnce、启动文件夹变更检测（快照比对）。
///  5. 游戏防护   —— mods/plugins 伪装 jar 检测、版本 JSON 启动参数注入检查、可疑计划任务。
/// 命中威胁的文件进入隔离区（改名 + 记录，不直接删除，可还原）。
/// </summary>
public sealed class SecurityService : IDisposable
{
    public static SecurityService Instance { get; } = new();

    /// <summary>监控目录可注入（自动化测试用）；null = 默认（桌面/文档/图片/游戏目录）。</summary>
    public List<string>? WatchDirsOverride { get; set; }
    /// <summary>设置可注入（自动化测试用）；null = 读真实设置文件。</summary>
    public LauncherSettings? SettingsOverride { get; set; }
    /// <summary>蜜罐登记文件路径可注入（自动化测试用）。</summary>
    public string? CanaryDirOverride { get; set; }
    public string GameDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    private LauncherSettings _settings = LauncherSettings.Load();
    private LauncherSettings Settings => SettingsOverride ?? _settings;

    /// <summary>
    /// true = 本进程不启动后台监控（由独立的 24 小时守护进程负责），
    /// 但下载扫描、隔离区、事件读取等即时能力照常可用。
    /// 避免启动器与守护进程同时监控导致重复告警。
    /// </summary>
    public bool MonitorSuspended { get; set; }

    public void SetMonitorSuspended(bool suspended)
    {
        MonitorSuspended = suspended;
        ApplySettings();
    }

    public string QuarantineDir =>
        string.IsNullOrWhiteSpace(Settings.QuarantineDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NEXLauncher", "quarantine")
            : Settings.QuarantineDir;
    private string EventLogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NEXLauncher", "security-events.jsonl");
    private string CanaryDir =>
        CanaryDirOverride ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NEXLauncher", "canaries.json");
    private string HostsSnapshotPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NEXLauncher", "sec-hosts.snap");
    private string AutorunSnapshotPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NEXLauncher", "sec-autorun.snap");

    public event Action<SecurityEvent>? ThreatDetected;

    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly System.Timers.Timer? _sysTimer;
    private readonly System.Timers.Timer? _behaviorTimer;
    private readonly Queue<(DateTime Time, string Dir)> _changeBurst = new();
    private bool _started;
    private readonly object _lock = new();

    // 每次蜜罐触发的去重（同一蜜罐 30 秒内只报一次）
    private readonly Dictionary<string, DateTime> _canaryLastHit = new();

    public SecurityService()
    {
        if (Environment.GetEnvironmentVariable("NEX_SEC_NO_TIMERS") == "1") return;
        _sysTimer = new System.Timers.Timer(20_000) { AutoReset = true };
        _sysTimer.Elapsed += (_, _) => { try { CheckSystemGuard(); } catch { } };
        _behaviorTimer = new System.Timers.Timer(1_000) { AutoReset = true };
        _behaviorTimer.Elapsed += (_, _) => CheckBurst();
        // 主动拦截：进程行为扫描（3 秒一次）与移动盘 autorun 巡检（6 秒一次）
        _procTimer = new System.Timers.Timer(3_000) { AutoReset = true };
        _procTimer.Elapsed += (_, _) => { try { ScanProcesses(); } catch { } };
        _driveTimer = new System.Timers.Timer(6_000) { AutoReset = true };
        _driveTimer.Elapsed += (_, _) => { try { CheckRemovableDrives(); } catch { } };
    }

    /// <summary>按设置启动/停止各道防线（每次设置变更后调用）。</summary>
    public void ApplySettings()
    {
        _settings = LauncherSettings.Load();
        var s = SettingsOverride ?? _settings;
        if (MonitorSuspended)
        {
            StopWatchers();
            if (_behaviorTimer != null) _behaviorTimer.Enabled = false;
            if (_procTimer != null) _procTimer.Enabled = false;
            if (_driveTimer != null) _driveTimer.Enabled = false;
            _sysTimer?.Stop();
            return;
        }
        if (s.SecRansomGuard || s.SecBehaviorMonitor) StartWatchers(s);
        else StopWatchers();
        if (_behaviorTimer != null) _behaviorTimer.Enabled = s.SecBehaviorMonitor;
        if (s.SecSystemGuard) _sysTimer?.Start(); else _sysTimer?.Stop();
        if (s.SecRansomGuard) DeployCanaries();
        // 主动拦截引擎
        if (_procTimer != null) _procTimer.Enabled = s.SecActiveBlock;
        if (_driveTimer != null) _driveTimer.Enabled = s.SecActiveBlock;
        if (s.SecActiveBlock) PrimeKnownProcesses();
    }

    // ================= 启动 / 停止 =================

    public void Start()
    {
        if (_started) return;
        _started = true;
        ApplySettings();
    }

    private void StartWatchers(LauncherSettings s)
    {
        StopWatchers();
        var dirs = WatchDirsOverride ?? new List<string>();
        if (WatchDirsOverride == null)
        {
            void Add(string p) { if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) dirs.Add(p); }
            Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            Add(GameDirectory);
        }
        foreach (var dir in dirs.Distinct())
        {
            try
            {
                var w = new FileSystemWatcher(dir)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024
                };
                w.Changed += (_, e) => OnFsEvent(dir, e.FullPath, "修改");
                w.Created += (_, e) => OnFsEvent(dir, e.FullPath, "新建");
                w.Renamed += (_, e) => OnFsEvent(dir, e.FullPath, "重命名");
                w.Error += (_, e) =>
                {
                    try { GuardDaemon.Log("文件监控异常：" + e.GetException().Message); } catch { }
                };
                w.EnableRaisingEvents = true;
                _watchers.Add(w);
                if (Environment.GetEnvironmentVariable("NEX_SEC_DEBUG") == "1")
                    GuardDaemon.Log($"[FS] 监控目录已启用：{dir}");
            }
            catch (Exception ex)
            {
                GuardDaemon.Log($"无法监控目录 {dir}：{ex.Message}");
            }
        }
        if (_watchers.Count > 0 && Environment.GetEnvironmentVariable("NEX_SEC_DEBUG") == "1")
            GuardDaemon.Log($"文件监控已启用：{_watchers.Count} 个目录");
    }

    private void StopWatchers()
    {
        foreach (var w in _watchers) { try { w.EnableRaisingEvents = false; w.Dispose(); } catch { } }
        _watchers.Clear();
    }

    public void Dispose()
    {
        StopWatchers();
        _sysTimer?.Dispose();
        _behaviorTimer?.Dispose();
        _procTimer?.Dispose();
        _driveTimer?.Dispose();
    }

    // ================= 第二道：勒索蜜罐 =================

    /// <summary>蜜罐文件（文件名, 内容标记）。内容被改 = 大概率被加密/篡改。</summary>
    private static readonly (string Name, string Marker)[] CanaryMarkers =
    [
        ("NEX防护诱饵_请勿删除.txt", "NEX-CANARY-1"),
        ("我的恢复密钥_备份_NEX.txt", "NEX-CANARY-2"),
        ("账号密码本_NEX勿动.txt", "NEX-CANARY-3"),
        ("重要文件勿删_NEX.txt", "NEX-CANARY-4"),
        ("wallet_backup_nex.txt", "NEX-CANARY-5"),
    ];

    /// <summary>布置/修复蜜罐文件（隐藏属性）。返回布置数量。</summary>
    public int DeployCanaries()
    {
        var dirs = WatchDirsOverride;
        List<string> targets;
        if (dirs != null) targets = dirs.Where(Directory.Exists).Take(5).ToList();
        else
        {
            targets = new List<string>();
            void Add(string p) { if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) targets.Add(p); }
            Add(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
            Add(GameDirectory);
            Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        }
        if (targets.Count == 0) return 0;

        var map = new Dictionary<string, string>();
        var count = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var (name, marker) = CanaryMarkers[i % CanaryMarkers.Length];
            var path = Path.Combine(targets[i], name);
            try
            {
                var content = $"这是 NEX Launcher 安全防护的诱饵文件（{marker}）。\r\n" +
                              "它不包含任何真实数据。勒索软件加密文件时通常会先碰这类文件，\r\n" +
                              "一旦它被改动，NEX 会立即发现并告警。请勿删除本文件。\r\n" +
                              $"部署时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}\r\n{marker}\r\n";
                if (!File.Exists(path) || !File.ReadAllText(path).Contains(marker))
                    File.WriteAllText(path, content);
                File.SetAttributes(path, FileAttributes.Archive); // 保持普通可见文件：勒索软件更会去加密，用户也看得见
                map[targets[i]] = $"{path}|{marker}";
                count++;
            }
            catch { }
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CanaryDir)!);
            File.WriteAllText(CanaryDir, JsonSerializer.Serialize(map));
        }
        catch { }
        return count;
    }

    private void OnFsEvent(string rootDir, string fullPath, string kind)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("NEX_SEC_DEBUG") == "1")
                GuardDaemon.Log($"[FS] {kind} {fullPath}");

            // 0) 主动拦截：新落地/被改写的可疑文件立即隔离，使其无法被执行
            if (kind is "新建" or "重命名" or "修改")
                InspectNewFile(fullPath);

            // 1) 蜜罐命中？
            if (File.Exists(CanaryDir))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(CanaryDir));
                var hit = map?.Values.FirstOrDefault(v =>
                {
                    var parts = v.Split('|');
                    return parts.Length == 2 && string.Equals(parts[0], fullPath, StringComparison.OrdinalIgnoreCase);
                });
                if (hit != null)
                {
                    var marker = hit.Split('|')[1];
                    // 内容还有标记 = 只是重写（自己部署），不算
                    if (File.Exists(fullPath))
                    {
                        try
                        {
                            if (File.ReadAllText(fullPath).Contains(marker)) goto burst;
                        }
                        catch { /* 读不了 = 被占用，按触发处理 */ }
                    }
                    var key = marker;
                    lock (_lock)
                    {
                        if (_canaryLastHit.TryGetValue(key, out var last) && (DateTime.Now - last).TotalSeconds < 30)
                            goto burst;
                        _canaryLastHit[key] = DateTime.Now;
                    }
                    Raise(new SecurityEvent(
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "严重", "第二道·勒索蜜罐",
                        "蜜罐文件被篡改！疑似勒索软件正在加密你的文件",
                        $"诱饵文件被{kind}：{fullPath}\n这是勒索软件加密行为的典型前兆。已自动还原诱饵文件以便持续监测。" +
                        (Settings.SecAutoKill ? "\n自动防御已开启：正在终止可疑进程。" : "\n建议立即断网并检查任务管理器中的可疑进程。"),
                        fullPath));
                    _ = Task.Run(() =>
                    {
                        try { DeployCanaries(); } catch { }
                        if (Settings.SecActiveBlock) KillRecentSuspects("蜜罐诱饵被篡改，疑似勒索加密进行中");
                        else if (Settings.SecAutoKill) AutoKillSuspects();
                    });
                    return;
                }
            }

        burst:
            // 2) 行为监控：突发大量改写
            if (Settings.SecBehaviorMonitor)
                lock (_changeBurst)
                {
                    _changeBurst.Enqueue((DateTime.Now, rootDir));
                }
        }
        catch { }
    }

    // ================= 第三道：行为监控（批量加密特征） =================

    private int _burstAlerts;
    private DateTime _lastBurstAlert = DateTime.MinValue;

    private void CheckBurst()
    {
        if (!Settings.SecBehaviorMonitor) return;
        int count;
        List<string> roots;
        lock (_changeBurst)
        {
            var cutoff = DateTime.Now.AddSeconds(-4);
            while (_changeBurst.Count > 0 && _changeBurst.Peek().Time < cutoff) _changeBurst.Dequeue();
            count = _changeBurst.Count;
            roots = _changeBurst.Select(x => x.Dir).Distinct().ToList();
        }
        // 4 秒内 ≥150 次文件改写 = 批量加密特征（正常使用达不到；复制大量文件也可能触发，但只告警不动文件）
        if (count < 150) return;
        if ((DateTime.Now - _lastBurstAlert).TotalSeconds < 60) return;
        _lastBurstAlert = DateTime.Now;

        lock (_changeBurst) _changeBurst.Clear();
        if (Interlocked.Increment(ref _burstAlerts) > 6) return; // 防刷屏
        Raise(new SecurityEvent(
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "严重", "第三道·行为监控",
            "检测到批量文件改写！疑似勒索软件正在加密文档",
            $"最近 4 秒内在 {roots.Count} 个受监控目录出现大量文件改写（{count} 次）。\n" +
            "这是勒索软件批量加密的典型行为。建议：① 立即断网 ② 打开任务管理器结束可疑进程 ③ 检查文档是否已被加密。",
            roots.FirstOrDefault()));
        if (Settings.SecAutoKill && !Settings.SecActiveBlock) Task.Run(AutoKillSuspects);
        else if (Settings.SecActiveBlock) Task.Run(() => KillRecentSuspects("检测到批量文件改写（勒索加密特征）"));
    }

    /// <summary>自动防御：终止临时目录/近期启动的可疑进程（只杀当前用户可杀的，杀不动就跳过）。</summary>
    public int AutoKillSuspects()
    {
        var killed = 0;
        var me = Environment.UserName;
        var temp = Path.GetTempPath().ToLowerInvariant();
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).ToLowerInvariant();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id <= 4 || p.ProcessName is "System" or "Idle") continue;
                string path;
                try { path = p.MainModule?.FileName?.ToLowerInvariant() ?? ""; } catch { continue; }
                if (string.IsNullOrEmpty(path)) continue;
                var startedRecently = false;
                try { startedRecently = (DateTime.Now - p.StartTime).TotalMinutes < 20; } catch { }
                var susDir = path.StartsWith(temp) || path.StartsWith(localApp + "\\temp");
                if (susDir && startedRecently && !path.Contains("nexlauncher"))
                {
                    p.Kill(entireProcessTree: true);
                    killed++;
                    Raise(new SecurityEvent(
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "警告", "自动防御",
                        $"已终止可疑进程 {p.ProcessName} (PID {p.Id})",
                        $"路径：{path}\n原因：位于临时目录且为近期启动，且当时检测到勒索行为特征。"));
                }
            }
            catch { /* 无权限/已退出 */ }
            finally { try { p.Dispose(); } catch { } }
        }
        return killed;
    }

    // ================= 第四道：系统关键位守护 =================

    private string CollectAutorunSnapshot()
    {
        var sb = new StringBuilder();
        void Keys(RegistryKey root, string sub)
        {
            try
            {
                using var k = root.OpenSubKey(sub);
                if (k == null) return;
                foreach (var name in k.GetValueNames())
                {
                    // 自家「24 小时守护」的开机自启项不参与变更比对，避免自己告警自己
                    if (string.Equals(name, "NEXGuard", StringComparison.OrdinalIgnoreCase)) continue;
                    sb.AppendLine($"{sub}\\{name}={k.GetValue(name)}");
                }
            }
            catch { }
        }
        Keys(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Run");
        Keys(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\RunOnce");
        Keys(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\Run");
        Keys(Registry.LocalMachine, @"Software\Microsoft\Windows\CurrentVersion\RunOnce");
        try
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            if (Directory.Exists(startup))
                foreach (var f in Directory.GetFiles(startup))
                    sb.AppendLine($"startup:{f}");
        }
        catch { }
        return sb.ToString();
    }

    /// <summary>
    /// 自动清除指向临时/下载目录的恶意启动项（HKCU 优先，HKLM 需要管理员权限）。
    /// 这是"病毒驻留"最常见的形式，直接删掉注册表值即可断掉它的开机自启。
    /// </summary>
    private static bool TryRemoveSuspiciousAutorun(string line)
    {
        try
        {
            var eq = line.IndexOf('=');
            if (eq < 0) return false;
            var keyPath = line[..eq];
            var lastSlash = keyPath.LastIndexOf('\\');
            if (lastSlash < 0) return false;
            var sub = keyPath[..lastSlash];
            var valueName = keyPath[(lastSlash + 1)..];

            if (!sub.EndsWith(@"\Run", StringComparison.OrdinalIgnoreCase) &&
                !sub.EndsWith(@"\RunOnce", StringComparison.OrdinalIgnoreCase)) return false;

            var value = line[(eq + 1)..].ToLowerInvariant();
            var suspicious = value.Contains(@"\temp\") || value.Contains(@"\tmp\") ||
                             value.Contains(@"\downloads\") || value.Contains(@"\appdata\local\temp");
            if (!suspicious) return false;

            foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using var k = root.OpenSubKey(sub, writable: true);
                    if (k == null) continue;
                    if (k.GetValue(valueName) == null) continue;
                    k.DeleteValue(valueName, throwOnMissingValue: false);
                    return true;
                }
                catch { }
            }
        }
        catch { }
        return false;
    }

    private static string ReadHosts()
    {
        try { return File.ReadAllText(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts")); }
        catch { return ""; }
    }

    /// <summary>系统守护轮询：hosts 与启动项快照比对，变更即告警。</summary>
    public void CheckSystemGuard()
    {
        if (!Settings.SecSystemGuard) return;

        var hosts = ReadHosts();
        if (File.Exists(HostsSnapshotPath))
        {
            var old = File.ReadAllText(HostsSnapshotPath);
            if (old != hosts && hosts.Length > 0)
            {
                var added = hosts.Split('\n').Except(old.Split('\n')).Where(l => l.Trim().Length > 0).ToList();
                var restored = false;
                if (Settings.SecActiveBlock && added.Count > 0)
                {
                    // 主动拦截：把 hosts 写回快照内容（需要管理员权限，失败则退化为告警）
                    try
                    {
                        var hostsPath = Path.Combine(
                            Environment.GetFolderPath(Environment.SpecialFolder.System), @"drivers\etc\hosts");
                        File.WriteAllText(hostsPath, old);
                        restored = true;
                    }
                    catch { }
                }
                File.WriteAllText(HostsSnapshotPath, restored ? old : hosts);
                if (added.Count > 0)
                {
                    if (restored) Interlocked.Increment(ref _blockedCount);
                    Raise(new SecurityEvent(
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), restored ? "严重" : "警告", "第四道·系统守护",
                        restored ? "hosts 被篡改，已自动还原" : "hosts 文件被修改",
                        "hosts 新增了 " + added.Count + " 行：\n" + string.Join("\n", added.Take(10)) +
                        (restored
                            ? "\n已按快照自动还原，域名劫持已被阻断。"
                            : "\n恶意软件常通过 hosts 劫持游戏登录/下载域名。请以管理员身份运行启动器以启用自动还原。")));
                }
            }
        }
        else if (hosts.Length > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HostsSnapshotPath)!);
            File.WriteAllText(HostsSnapshotPath, hosts);
        }

        var autorun = CollectAutorunSnapshot();
        if (File.Exists(AutorunSnapshotPath))
        {
            var old = File.ReadAllText(AutorunSnapshotPath);
            if (old != autorun)
            {
                var added = autorun.Split('\n').Except(old.Split('\n')).Where(l => l.Trim().Length > 0).ToList();
                var removed = new List<string>();
                if (Settings.SecActiveBlock && added.Count > 0)
                    removed = added.Where(TryRemoveSuspiciousAutorun).ToList();

                if (removed.Count > 0) Interlocked.Increment(ref _blockedCount);
                File.WriteAllText(AutorunSnapshotPath, removed.Count > 0 ? CollectAutorunSnapshot() : autorun);

                if (added.Count > 0)
                    Raise(new SecurityEvent(
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), removed.Count > 0 ? "严重" : "警告",
                        "第四道·系统守护",
                        removed.Count > 0
                            ? $"已自动清除 {removed.Count} 个恶意开机启动项"
                            : "发现新的开机自启动项",
                        string.Join("\n", added.Take(10)) +
                        (removed.Count > 0
                            ? "\n这些启动项指向临时目录/下载目录，已判定为病毒驻留并从注册表中删除。"
                            : "\n病毒常把自己加进启动项实现开机运行。若不是你安装的软件，请到任务管理器 → 启动 应用里禁用。")));
            }
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutorunSnapshotPath)!);
            File.WriteAllText(AutorunSnapshotPath, autorun);
        }
    }

    // ================= 第一道：下载防护 + 第五道：游戏防护共用扫描 =================

    /// <summary>扫描单个文件：伪装扩展名 / 危险脚本 / 可疑特征。Threat=true 时会被建议隔离。</summary>
    public ScanVerdict ScanFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return new ScanVerdict(path, false, "干净", "文件不存在");
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var size = new FileInfo(path).Length;

            byte[] head = new byte[Math.Min(4096, size)];
            await_using_open_read(path, head);

            bool Is(byte a, byte b) => head.Length >= 2 && head[0] == a && head[1] == b;

            // 1) PE 可执行文件（MZ）冒充数据文件
            if (head.Length >= 2 && head[0] == 0x4D && head[1] == 0x5A)
            {
                if (ext is ".jar" or ".png" or ".jpg" or ".jpeg" or ".zip" or ".mrpack" or ".txt" or ".json" or ".litematic" or ".schem")
                    return new ScanVerdict(path, true, "严重",
                        $"文件伪装：扩展名是 {ext}，但内容是 Windows 可执行程序（EXE）。极大概率是木马！");
                if (ext is ".exe" or ".dll" or ".scr") return new ScanVerdict(path, false, "可执行文件", "EXE/DLL（正常可执行文件）");
            }

            // 2) 脚本冒充资源包/模组（zip 头 PK 之外的内容）
            if (ext is ".jar" or ".zip" or ".mrpack" && head.Length >= 2 && !Is(0x50, 0x4B) && size > 0)
                return new ScanVerdict(path, true, "严重",
                    $"文件伪装：扩展名是 {ext}（应为 ZIP 结构），但内容不是压缩包。可能是改名后的脚本/木马。");

            // 3) 图片伪装：内容既不是 PNG 也不是 JPEG
            if (ext is ".png" or ".jpg" or ".jpeg" && size > 0)
            {
                var png = head.Length >= 4 && head[0] == 0x89 && head[1] == 0x50;
                var jpg = head.Length >= 2 && head[0] == 0xFF && head[1] == 0xD8;
                if (!png && !jpg)
                    return new ScanVerdict(path, true, "严重",
                        $"文件伪装：扩展名是 {ext}，但内容既不是 PNG 也不是 JPEG。可能是改名后的可执行/脚本文件。");
            }

            // 4) 直接下到 mods 的危险脚本（脚本不可能是有用模组）
            if (ext is ".scr" or ".cmd" or ".com" or ".pif" or ".cpl" or ".msi" or ".hta" or ".wsf" or ".vbs" or ".js" or ".jse" or ".vbe" or ".ps1" or ".bat")
                return new ScanVerdict(path, true, "严重",
                    $"下载内容是可直接执行的脚本/安装器（{ext}），正规模组/资源不会是这种格式。");

            // 5) 超大“图片”（>200MB 的 png/jpg 几乎必有问题）
            if (ext is ".png" or ".jpg" or ".jpeg" && size > 200L * 1024 * 1024)
                return new ScanVerdict(path, true, "可疑", $"图片异常巨大（{size / 1048576} MB）。");

            return new ScanVerdict(path, false, "干净", "未发现已知威胁特征");
        }
        catch (Exception ex)
        {
            return new ScanVerdict(path, false, "无法扫描", ex.Message);
        }
    }

    private static void await_using_open_read(string path, byte[] head)
    {
        using var fs = File.OpenRead(path);
        var read = 0;
        while (read < head.Length)
        {
            var n = fs.Read(head, read, head.Length - read);
            if (n <= 0) break;
            read += n;
        }
    }

    /// <summary>第五道：游戏环境体检（mods/plugins 伪装检测 + 启动参数注入 + 可疑计划任务）。返回发现的问题列表。</summary>
    public List<string> ScanGameEnvironment()
    {
        var issues = new List<string>();
        foreach (var sub in new[] { "mods", "plugins", "resourcepacks", "shaderpacks", "modpacks" })
        {
            var dir = Path.Combine(GameDirectory, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
            {
                var v = ScanFile(f);
                if (v.Threat) issues.Add($"[{sub}] {Path.GetFileName(f)}：{v.Reason}");
            }
        }
        // 版本 JSON 启动参数注入检查
        var versionsDir = Path.Combine(GameDirectory, "versions");
        if (Directory.Exists(versionsDir))
        {
            foreach (var json in Directory.GetFiles(versionsDir, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var text = File.ReadAllText(json);
                    foreach (var arg in new[] { "-javaagent:", "-agentlib:", "-agentpath:", "curl ", "certutil", "bitsadmin", "powershell -" })
                    {
                        var idx = text.IndexOf(arg, StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) continue;
                        var snippet = text.Substring(idx, Math.Min(160, text.Length - idx)).Split('"')[0];
                        issues.Add($"[版本 {Path.GetFileNameWithoutExtension(json)}] 启动参数包含 {arg.TrimEnd(':')}：{snippet}");
                    }
                }
                catch { }
            }
        }
        return issues;
    }

    /// <summary>
    /// 扫描游戏环境并把发现写入事件日志（后台守护进程定时调用），
    /// 让伪装文件即使不是通过下载进入 mods、甚至启动器没开时也能被发现。
    /// 返回发现的可疑项数量。
    /// </summary>
    public int ScanGameEnvironmentAndReport()
    {
        try
        {
            if (!Settings.SecGameGuard) return 0;
            var found = ScanGameEnvironment();
            if (found.Count == 0) return 0;
            Raise(new SecurityEvent(
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "警告", "第五道·游戏防护",
                $"游戏目录发现 {found.Count} 个可疑项",
                string.Join("\n", found.Take(20))));
            return found.Count;
        }
        catch { return 0; }
    }

    // ================= 主动拦截引擎（不只是告警，直接处置） =================

    private System.Timers.Timer? _procTimer;
    private System.Timers.Timer? _driveTimer;
    private readonly HashSet<int> _knownPids = [];
    private readonly HashSet<string> _knownDrives = new(StringComparer.OrdinalIgnoreCase);
    private int _blockedCount;
    private readonly object _blockLock = new();

    /// <summary>累计已拦截次数（终止进程 / 隔离文件 / 清除启动项）。</summary>
    public int BlockedCount => Volatile.Read(ref _blockedCount);

    public bool DefenderAvailable => FindMpCmdRun() != null;

    /// <summary>双扩展名伪装：报告.pdf.exe / 照片.jpg.scr 之类。</summary>
    private static readonly Regex DoubleExtRx = new(
        @"\.(pdf|docx?|xlsx?|pptx?|jpe?g|png|gif|bmp|txt|rtf|mp[34]|avi|mkv|zip|rar|7z|jar|iso|img|url)\s*\.\s*(exe|scr|com|pif|bat|cmd|js|jse|vbs|vbe|wsf|hta|msi)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>仿冒系统进程名（用数字 0 替换字母 o 之类）。</summary>
    private static readonly Regex FakeSystemRx = new(
        @"^(svch0st|scvhost|lsas|lsasss|csrsss|winl0gon|expl0rer|taskh0st|rundl132|sp00lsv|conhosts|dllh0st|smsss)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsRiskyExt(string ext) =>
        ext is ".exe" or ".scr" or ".com" or ".pif" or ".bat" or ".cmd" or ".js" or ".jse"
            or ".vbs" or ".vbe" or ".wsf" or ".hta" or ".msi" or ".lnk" or ".inf";

    private void PrimeKnownProcesses()
    {
        lock (_blockLock)
        {
            if (_knownPids.Count > 0) return;
            try
            {
                foreach (var p in Process.GetProcesses())
                {
                    _knownPids.Add(p.Id);
                    p.Dispose();
                }
            }
            catch { }
        }
    }

    /// <summary>给进程打分：命中危险特征越多分越高，达到阈值才处置，避免误杀正常软件。</summary>
    private (int Score, List<string> Reasons) ScoreProcess(string path)
    {
        var reasons = new List<string>();
        var score = 0;
        var lower = path.ToLowerInvariant();
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(path).ToLowerInvariant();

        // 系统目录与自家程序永不处置
        if (lower.StartsWith(@"c:\windows\")) return (0, reasons);
        if (lower.Contains("nexlauncher") || lower.Contains("nexguard")) return (0, reasons);
        if (lower.StartsWith(@"c:\program files\") || lower.StartsWith(@"c:\program files (x86)\")) return (0, reasons);

        if (DoubleExtRx.IsMatch(name)) { score += 55; reasons.Add("双扩展名伪装（如 报告.pdf.exe）"); }

        if (FakeSystemRx.IsMatch(name))
        {
            score += 60;
            reasons.Add("仿冒系统进程名");
        }

        var isExe = ext is ".exe" or ".scr" or ".com" or ".pif";
        if (!isExe) return (score, reasons);

        var temp = Path.GetTempPath().ToLowerInvariant();
        var inTemp = lower.StartsWith(temp) || lower.Contains(@"\temp\") || lower.Contains(@"\downloads\");
        if (inTemp) { score += 20; reasons.Add("从临时/下载目录直接运行"); }

        // 文件名像随机串：长度 ≥12 且没有两个连续元音
        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Length >= 12 && !stem.Contains(' ') && !Regex.IsMatch(stem, "[aeiouAEIOU]{2}"))
        {
            score += 15;
            reasons.Add("文件名高度随机");
        }

        // 隐藏属性 + 可执行 = 典型隐藏病毒
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Hidden) != 0)
            {
                score += 25;
                reasons.Add("隐藏属性的可执行文件");
            }
        }
        catch { }

        return (score, reasons);
    }

    /// <summary>每 3 秒扫一遍新出现的进程，命中危险特征立即终止并隔离映像。</summary>
    private void ScanProcesses()
    {
        if (!Settings.SecActiveBlock) return;
        PrimeKnownProcesses();

        Process[] all;
        try { all = Process.GetProcesses(); } catch { return; }

        var live = new HashSet<int>();
        foreach (var p in all)
        {
            try
            {
                live.Add(p.Id);
                bool isNew;
                lock (_blockLock) isNew = _knownPids.Add(p.Id);
                if (!isNew) continue;
                if (p.Id <= 8) continue;

                string path;
                try { path = p.MainModule?.FileName ?? ""; } catch { continue; }
                if (string.IsNullOrWhiteSpace(path)) continue;

                var (score, reasons) = ScoreProcess(path);
                if (score < 55) continue;

                BlockProcess(p, path, score, reasons);
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }

        // 清理已退出的 PID，避免集合无限增长
        lock (_blockLock)
        {
            if (_knownPids.Count > 600)
            {
                var stale = _knownPids.Where(id => !live.Contains(id)).ToList();
                foreach (var id in stale) _knownPids.Remove(id);
            }
        }
    }

    /// <summary>终止进程树 + 隔离映像文件 + 可选彻底删除。</summary>
    private void BlockProcess(Process p, string path, int score, List<string> reasons)
    {
        var name = p.ProcessName;
        var pid = p.Id;
        var detail = string.Join("；", reasons);

        var killed = false;
        try
        {
            p.Kill(entireProcessTree: true);
            killed = true;
        }
        catch
        {
            try
            {
                using var k = Process.Start(new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
                { UseShellExecute = false, CreateNoWindow = true });
                k?.WaitForExit(3000);
                killed = k != null && k.HasExited && k.ExitCode == 0;
            }
            catch { }
        }

        var dest = SafeQuarantine(path, $"主动拦截：{detail}（风险评分 {score}）");
        Interlocked.Increment(ref _blockedCount);

        if (Settings.SecAutoDelete && !string.IsNullOrEmpty(dest))
        {
            try { File.Delete(dest); dest = ""; } catch { }
        }

        Raise(new SecurityEvent(
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "严重", "主动拦截",
            killed
                ? $"已终止并清除可疑进程 {name} (PID {pid})"
                : $"发现可疑进程 {name} (PID {pid})，但权限不足未能终止",
            $"判定依据：{detail}（风险评分 {score}）\n程序路径：{path}\n" +
            (killed ? "进程树已终止。" : "请以管理员身份重新打开启动器后重试。") +
            (string.IsNullOrEmpty(dest) ? "" : $"\n映像已隔离：{dest}"),
            path));

        if (Settings.SecDefenderAssist && !string.IsNullOrEmpty(dest))
            _ = Task.Run(() => DefenderRemove(dest));
    }

    /// <summary>不抛异常的隔离：先尝试移动，失败则复制后删除，再失败返回空串。</summary>
    private string SafeQuarantine(string path, string reason)
    {
        try { return Quarantine(path, reason); }
        catch
        {
            try
            {
                Directory.CreateDirectory(QuarantineDir);
                var dest = Path.Combine(QuarantineDir,
                    Path.GetFileName(path) + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".quar");
                File.Copy(path, dest, true);
                try { File.Delete(path); } catch { }
                return dest;
            }
            catch { return ""; }
        }
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _inspected =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>文件落地检查：新出现的可疑文件立即移入隔离区，使其无法被双击执行。</summary>
    private void InspectNewFile(string fullPath)
    {
        if (!Settings.SecActiveBlock) return;
        var ext = Path.GetExtension(fullPath).ToLowerInvariant();
        var name = Path.GetFileName(fullPath);
        if (!IsRiskyExt(ext) && !DoubleExtRx.IsMatch(name)) return;

        // 同一文件 5 秒内只处理一次（写入过程会连发多个 Changed 事件）
        var now = DateTime.Now;
        if (_inspected.TryGetValue(fullPath, out var last) && (now - last).TotalSeconds < 5) return;
        _inspected[fullPath] = now;
        if (_inspected.Count > 2000) _inspected.Clear();

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(400); // 等写入落盘
                if (!File.Exists(fullPath)) return;

                if (DoubleExtRx.IsMatch(name))
                {
                    HandleThreatFile(fullPath, "双扩展名伪装，是钓鱼木马最常见的传播手法");
                    return;
                }

                if (ext == ".inf" && name.Equals("autorun.inf", StringComparison.OrdinalIgnoreCase))
                {
                    HandleThreatFile(fullPath, "autorun.inf 是 U 盘病毒的自启动入口");
                    return;
                }

                if (ext == ".lnk")
                {
                    var raw = ReadShortcutRaw(fullPath);
                    if (raw.Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
                        raw.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
                        raw.Contains("mshta", StringComparison.OrdinalIgnoreCase) ||
                        raw.Contains("wscript", StringComparison.OrdinalIgnoreCase) ||
                        raw.Contains("cscript", StringComparison.OrdinalIgnoreCase))
                    {
                        HandleThreatFile(fullPath, "快捷方式指向命令行/脚本，是伪装成文档的病毒入口");
                        return;
                    }
                    return;
                }

                var v = ScanFile(fullPath);
                if (v.Threat && v.Level == "严重")
                    HandleThreatFile(fullPath, v.Reason);
            }
            catch { }
        });
    }

    /// <summary>把 .lnk 的原始字节按 ASCII/Unicode 双解码，用来找命令行目标。</summary>
    private static string ReadShortcutRaw(string lnkPath)
    {
        try
        {
            var bytes = File.ReadAllBytes(lnkPath);
            return Encoding.ASCII.GetString(bytes) + "\n" + Encoding.Unicode.GetString(bytes);
        }
        catch { return ""; }
    }

    /// <summary>处置一个已确认的威胁文件：隔离（或删除）+ 记录 + Defender 复核。</summary>
    private void HandleThreatFile(string path, string reason)
    {
        var name = Path.GetFileName(path);
        var dest = SafeQuarantine(path, "主动拦截：" + reason);
        if (!string.IsNullOrEmpty(dest)) Interlocked.Increment(ref _blockedCount);

        var deleted = false;
        if (Settings.SecAutoDelete && !string.IsNullOrEmpty(dest))
        {
            try { File.Delete(dest); deleted = true; dest = ""; } catch { }
        }

        Raise(new SecurityEvent(
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "严重", "主动拦截",
            deleted ? $"已拦截并删除 {name}" : $"已拦截并隔离 {name}",
            $"{reason}\n原路径：{path}\n" +
            (deleted
                ? "该文件已被彻底删除，无法再被执行。"
                : $"已隔离到：{dest}\n可在「隔离区」查看或还原。") +
            (Settings.SecDefenderAssist ? "\n已交由 Windows Defender 复核。" : ""),
            path));

        if (Settings.SecDefenderAssist && !string.IsNullOrEmpty(dest))
            _ = Task.Run(() => DefenderRemove(dest));
    }

    /// <summary>紧急处置：诱饵触发或批量加密时，终止近期启动的可疑进程。</summary>
    public int KillRecentSuspects(string why)
    {
        var killed = 0;
        Process[] all;
        try { all = Process.GetProcesses(); } catch { return 0; }

        foreach (var p in all)
        {
            try
            {
                if (p.Id <= 8) continue;
                string path;
                try { path = p.MainModule?.FileName ?? ""; } catch { continue; }
                if (string.IsNullOrWhiteSpace(path)) continue;

                var (score, reasons) = ScoreProcess(path);
                if (score < 45) continue;

                var recent = false;
                try { recent = (DateTime.Now - p.StartTime).TotalMinutes < 15; } catch { }
                if (!recent) continue;

                try
                {
                    p.Kill(entireProcessTree: true);
                    killed++;
                    var dest = SafeQuarantine(path, $"紧急处置：{why}");
                    Interlocked.Increment(ref _blockedCount);
                    Raise(new SecurityEvent(
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "严重", "主动拦截",
                        $"已紧急终止可疑进程 {p.ProcessName} (PID {p.Id})",
                        $"触发原因：{why}\n判定依据：{string.Join("；", reasons)}\n路径：{path}\n" +
                        (string.IsNullOrEmpty(dest) ? "映像文件被占用，未能隔离。" : $"映像已隔离：{dest}"),
                        path));
                }
                catch { }
            }
            catch { }
            finally { try { p.Dispose(); } catch { } }
        }
        return killed;
    }

    // ---- Windows Defender 联动（对隔离文件做二次确认与清除） ----

    private static string? FindMpCmdRun()
    {
        try
        {
            var p1 = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Windows Defender\MpCmdRun.exe");
            if (File.Exists(p1)) return p1;

            const string platform = @"C:\ProgramData\Microsoft\Windows Defender\Platform";
            if (Directory.Exists(platform))
            {
                foreach (var d in Directory.GetDirectories(platform).OrderByDescending(x => x))
                {
                    var exe = Path.Combine(d, "MpCmdRun.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>调用系统 Defender 扫描并清除指定文件。返回 true 表示 Defender 确认并处理了威胁。</summary>
    public bool DefenderRemove(string filePath)
    {
        var mp = FindMpCmdRun();
        if (mp == null) return false;
        try
        {
            var psi = new ProcessStartInfo(mp,
                $"-Scan -ScanType 3 -File \"{filePath}\" -Disinfect -Timeout 60")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            p.WaitForExit(70_000);
            return p.HasExited && p.ExitCode == 2; // 2 = 发现并已处理
        }
        catch { return false; }
    }

    // ---- 移动盘（U 盘）autorun 巡检 ----

    private void CheckRemovableDrives()
    {
        if (!Settings.SecActiveBlock) return;
        try
        {
            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Removable || !d.IsReady) continue;
                var root = d.RootDirectory.FullName;
                current.Add(root);
                if (!_knownDrives.Add(root)) continue;

                var autorun = Path.Combine(root, "autorun.inf");
                if (File.Exists(autorun))
                    HandleThreatFile(autorun, "移动盘中的 autorun.inf 是 U 盘病毒的自启动入口");

                foreach (var f in Directory.GetFiles(root, "*.exe"))
                {
                    var v = ScanFile(f);
                    if (v.Threat) HandleThreatFile(f, v.Reason);
                }

                Raise(new SecurityEvent(
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "信息", "移动盘巡检",
                    $"已检查移动盘 {d.Name}", "autorun.inf 与根目录可执行文件已扫描。"));
            }
            // 拔掉的盘下次重新检查
            _knownDrives.RemoveWhere(x => !current.Contains(x));
        }
        catch { }
    }

    // ================= 隔离区 =================

    /// <summary>把威胁文件移入隔离区（改名 .quar + 写记录，不删除）。返回隔离后的路径。</summary>
    public string Quarantine(string path, string reason)
    {
        Directory.CreateDirectory(QuarantineDir);
        var name = Path.GetFileName(path);
        var dest = Path.Combine(QuarantineDir, name + "." + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".quar");
        try { File.Move(path, dest, overwrite: false); }
        catch (IOException) { File.Copy(path, dest, true); File.Delete(path); }

        var rec = new
        {
            Original = path,
            Quarantined = dest,
            Reason = reason,
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };
        File.AppendAllText(Path.Combine(QuarantineDir, "quarantine.jsonl"),
            JsonSerializer.Serialize(rec) + Environment.NewLine);
        Raise(new SecurityEvent(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), "警告", "隔离区",
            $"已隔离威胁文件 {name}", reason + $"\n原始位置：{path}\n隔离位置：{dest}", dest));
        return dest;
    }

    public sealed record QuarantineItem(string Original, string Quarantined, string Reason, string Time);

    public List<QuarantineItem> ListQuarantine()
    {
        var list = new List<QuarantineItem>();
        try
        {
            var f = Path.Combine(QuarantineDir, "quarantine.jsonl");
            if (!File.Exists(f)) return list;
            foreach (var line in File.ReadAllLines(f))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var r = doc.RootElement;
                    list.Add(new QuarantineItem(
                        r.GetProperty("Original").GetString() ?? "",
                        r.GetProperty("Quarantined").GetString() ?? "",
                        r.GetProperty("Reason").GetString() ?? "",
                        r.GetProperty("Time").GetString() ?? ""));
                }
                catch { }
            }
        }
        catch { }
        return list;
    }

    /// <summary>还原隔离文件到原位。</summary>
    public void RestoreQuarantine(QuarantineItem item)
    {
        if (File.Exists(item.Quarantined) && !string.IsNullOrEmpty(item.Original))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(item.Original)!);
            File.Move(item.Quarantined, item.Original, overwrite: true);
        }
    }

    public void DeleteQuarantine(QuarantineItem item)
    {
        if (File.Exists(item.Quarantined)) File.Delete(item.Quarantined);
    }

    // ================= 事件日志 =================

    private void Raise(SecurityEvent e)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(EventLogPath)!);
            File.AppendAllText(EventLogPath, JsonSerializer.Serialize(e) + Environment.NewLine);
        }
        catch { }
        ThreatDetected?.Invoke(e);
    }

    public List<SecurityEvent> ReadEvents(int max = 200)
    {
        try
        {
            var f = EventLogPath;
            if (!File.Exists(f)) return [];
            var lines = File.ReadAllLines(f);
            return lines[^Math.Min(lines.Length, max)..]
                .Select(l => { try { return JsonSerializer.Deserialize<SecurityEvent>(l); } catch { return null; } })
                .Where(e => e != null).Cast<SecurityEvent>()
                .Reverse().ToList();
        }
        catch { return []; }
    }

    public void ClearEvents()
    {
        try { if (File.Exists(EventLogPath)) File.Delete(EventLogPath); } catch { }
    }

    // ================= 体检 =================

    /// <summary>一键体检：五道防线逐项检查，返回 (结论行列表, 是否有问题)。</summary>
    public (List<string> Lines, bool HasIssues) RunHealthCheck()
    {
        var lines = new List<string>();
        var issues = false;

        // 1) 下载防护
        lines.Add(Settings.SecDownloadShield
            ? "✅ 第一道·下载防护：已开启（资源下载落地即扫描伪装文件）"
            : "⚠️ 第一道·下载防护：已关闭，建议开启");

        // 2) 蜜罐
        var canaryCount = Settings.SecRansomGuard ? DeployCanaries() : 0;
        lines.Add(Settings.SecRansomGuard
            ? $"✅ 第二道·勒索蜜罐：已布置 {canaryCount} 个诱饵文件，正在监控"
            : "⚠️ 第二道·勒索蜜罐：已关闭，无法提前发现勒索加密");

        // 3) 行为监控
        lines.Add(Settings.SecBehaviorMonitor
            ? "✅ 第三道·行为监控：正在监控文档/桌面/游戏目录的批量加密行为"
            : "⚠️ 第三道·行为监控：已关闭");

        // 4) 系统守护
        if (Settings.SecSystemGuard)
        {
            CheckSystemGuard();
            var hosts = ReadHosts();
            var suspiciousHosts = hosts.Split('\n')
                .Count(l => l.Trim().Length > 0 && !l.TrimStart().StartsWith("#"));
            lines.Add($"✅ 第四道·系统守护：hosts（{suspiciousHosts} 条映射）、启动项、启动文件夹持续监控中");
        }
        else lines.Add("⚠️ 第四道·系统守护：已关闭");

        // 5) 游戏防护
        if (Settings.SecGameGuard)
        {
            var found = ScanGameEnvironment();
            if (found.Count == 0)
                lines.Add("✅ 第五道·游戏防护：mods / 插件 / 资源包 / 启动参数均未发现可疑内容");
            else
            {
                issues = true;
                lines.Add($"❌ 第五道·游戏防护：发现 {found.Count} 个可疑项：");
                lines.AddRange(found.Take(20));
            }
        }
        else lines.Add("⚠️ 第五道·游戏防护：已关闭");

        // 24 小时自动防卫
        var gs = GuardDaemon.GetStatus();
        if (Settings.SecAlwaysGuard)
            lines.Add(gs.Running
                ? $"✅ 24 小时自动防卫：后台守护运行中（PID {gs.Pid}，已持续 {gs.Uptime:hh\\:mm\\:ss}）——关闭启动器仍继续防护"
                : "⚠️ 24 小时自动防卫：已开启但后台守护未运行，请到页面顶部重新启动守护");
        else
            lines.Add("⚠️ 24 小时自动防卫：未开启，关闭启动器后防护会停止");

        // 隔离区
        var quar = ListQuarantine();
        lines.Add(quar.Count == 0
            ? "✅ 隔离区：空"
            : $"⚠️ 隔离区：有 {quar.Count} 个被隔离的威胁文件，可到隔离区查看/彻底删除");
        if (quar.Count > 0) issues = true;

        return (lines, issues);
    }
}
