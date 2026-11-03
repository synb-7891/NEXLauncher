using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Installers;
using CmlLib.Core.ProcessBuilder;

namespace NEXLauncher.Services;

public sealed record MinecraftVersionItem(string Id, string Type, string Category, string ReleaseDate, bool IsInstalled);

public sealed class MinecraftService
{
    private readonly string _gameDirectory;
    private readonly MinecraftLauncher _launcher;
    private readonly List<Process> _gameProcesses = [];

    /// <summary>安装进度：文件数（来自 CmlLib，安装版本时触发）</summary>
    public event EventHandler<InstallerProgressChangedEventArgs>? FileProgressChanged;
    /// <summary>安装进度：字节数（来自 CmlLib，安装版本时触发）</summary>
    public event EventHandler<ByteProgress>? ByteProgressChanged;

    /// <summary>游戏进程启动 / 退出（在主窗口更新强杀按钮）</summary>
    public event EventHandler<Process>? GameStarted;
    public event EventHandler? GameExited;

    public MinecraftService(string gameDirectory)
    {
        _gameDirectory = gameDirectory;
        _launcher = new MinecraftLauncher(new MinecraftPath(gameDirectory));
        _launcher.FileProgressChanged += (_, e) => FileProgressChanged?.Invoke(this, e);
        _launcher.ByteProgressChanged += (_, e) => ByteProgressChanged?.Invoke(this, e);
    }

    /// <summary>
    /// 串行化安装/构建：CmlLib 的 MinecraftLauncher 内部有“安装中”状态位，
    /// 之前一次安装被取消或失败后状态位不复位，会导致后续启动报「Already installing」。
    /// 因此每次安装/启动都用全新实例，并用信号量保证同一时间只有一个安装任务。
    /// </summary>
    private readonly SemaphoreSlim _installLock = new(1, 1);

    private MinecraftLauncher CreateLauncher()
    {
        var l = new MinecraftLauncher(new MinecraftPath(_gameDirectory));
        l.FileProgressChanged += (_, e) => FileProgressChanged?.Invoke(this, e);
        l.ByteProgressChanged += (_, e) => ByteProgressChanged?.Invoke(this, e);
        return l;
    }

    private static void SafeDispose(MinecraftLauncher? l)
    {
        if (l is IDisposable d) { try { d.Dispose(); } catch { } }
    }

    private CmlLib.Core.Auth.Microsoft.JELoginHandler CreateLoginHandler()
        => new JELoginHandlerBuilder()
            .WithAccountManager(Path.Combine(_gameDirectory, "nex_accounts.json"))
            .Build();

    /// <summary>
    /// 微软账号登录：使用 CmlLib 内置的 Mojang 官方 OAuth Client Info + WebView2 嵌入式登录窗口。
    /// 无需自行配置 Azure Client ID，走官方 Minecraft 登录链路，最稳定。
    /// </summary>
    public async Task<MSession> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        return await CreateLoginHandler().AuthenticateInteractively(cancellationToken);
    }

    /// <summary>
    /// 尝试用本地缓存的账号凭据静默恢复微软登录（不弹任何窗口）。
    /// 没有缓存或凭据已失效时返回 null。
    /// </summary>
    public async Task<MSession?> TrySilentLoginAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await CreateLoginHandler().AuthenticateSilently(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>退出微软账号并清除本地缓存凭据。</summary>
    public async Task SignOutAsync()
    {
        try
        {
            await CreateLoginHandler().Signout();
        }
        catch { /* 无缓存账号时忽略 */ }
    }

    /// <summary>创建离线（盗版）会话。UUID 遵循 Minecraft 离线玩家规范：OfflinePlayer:&lt;名&gt; 的 MD5 v3 UUID。</summary>
    public static MSession CreateOfflineSession(string username)
    {
        username = username.Trim();
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(System.Text.Encoding.UTF8.GetBytes("OfflinePlayer:" + username));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        var uuid = new Guid(hash).ToString("N");

        return new MSession
        {
            Username = username,
            UUID = uuid,
            AccessToken = Guid.NewGuid().ToString("N"),
            UserType = "legacy"
        };
    }

    /// <summary>
    /// 扫描本地 versions 目录，返回已安装的版本 ID（按最近安装时间排序，无需联网）。
    /// </summary>
    public List<string> GetLocalInstalledVersions()
    {
        var dir = Path.Combine(_gameDirectory, "versions");
        var result = new List<(string Id, DateTime Time)>();
        if (Directory.Exists(dir))
        {
            foreach (var d in Directory.GetDirectories(dir))
            {
                var name = Path.GetFileName(d);
                var json = Path.Combine(d, name + ".json");
                if (File.Exists(json))
                    result.Add((name, File.GetLastWriteTimeUtc(json)));
            }
        }
        return result.OrderByDescending(x => x.Time).Select(x => x.Id).ToList();
    }

    public async Task<IReadOnlyList<MinecraftVersionItem>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        var versions = await _launcher.GetAllVersionsAsync(cancellationToken);
        return versions
            .Select(version => new MinecraftVersionItem(
                version.Name,
                version.Type ?? "unknown",
                Categorize(version.Name, version.Type ?? ""),
                version.ReleaseTime.ToString("yyyy-MM-dd"),
                File.Exists(Path.Combine(_gameDirectory, "versions", version.Name, $"{version.Name}.json"))))
            .ToList();
    }

    public async Task InstallAsync(string versionId, CancellationToken cancellationToken = default)
    {
        await _installLock.WaitAsync(cancellationToken);
        MinecraftLauncher? l = null;
        try
        {
            l = CreateLauncher();
            await l.InstallAsync(versionId, cancellationToken);
        }
        finally
        {
            SafeDispose(l);
            _installLock.Release();
        }
    }

    /// <summary>
    /// 删除本地已安装的游戏版本（versions/ 目录 + assets 里的独立索引保持不动）。
    /// 返回删除的文件夹路径；版本不存在返回 null。
    /// </summary>
    public string? DeleteLocalVersion(string versionId)
    {
        var dir = Path.Combine(_gameDirectory, "versions", versionId);
        if (!Directory.Exists(dir)) return null;

        // 保护：只允许删除 versions 目录下的一级子目录，防止路径拼接意外
        var full = Path.GetFullPath(dir);
        var root = Path.GetFullPath(Path.Combine(_gameDirectory, "versions"));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
            full.TrimEnd(Path.DirectorySeparatorChar).Equals(root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            return null;

        var info = new DirectoryInfo(full);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            // 通过"链接导入"加入的版本：只删除链接本身，绝不递归删除源目录内容
            Directory.Delete(full);
        }
        else
        {
            Directory.Delete(full, true);
        }
        return full;
    }

    /// <summary>
    /// 启动游戏并返回进程对象。内存 / Java 路径 / 窗口尺寸等从启动器设置读取。
    /// </summary>
    /// <summary>按空格拆分参数，支持双引号包裹（存档名可能含空格）。</summary>
    private static List<string> SplitArguments(string raw)
    {
        var result = new List<string>();
        var sb = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in raw)
        {
            if (c == '"') { quoted = !quoted; continue; }
            if (c == ' ' && !quoted)
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    /// <summary>
    /// 组装启动参数：内存 / Java / 窗口 / 额外 JVM 参数 / 外置登录 agent / 离线皮肤 agent。
    /// 启动游戏与「导出启动脚本」共用同一份逻辑，保证行为一致。
    /// </summary>
    private static MLaunchOption CreateLaunchOption(MSession session, LauncherSettings settings, string? extraGameArgs)
    {
        var option = new MLaunchOption
        {
            Session = session,
            MaximumRamMb = Math.Clamp(settings.MaxRamMb, 512, 32768),
            FullScreen = settings.GameFullScreen,
            GameLauncherName = "NEXLauncher"
        };
        if (!string.IsNullOrWhiteSpace(settings.JavaPath) && File.Exists(settings.JavaPath))
            option.JavaPath = settings.JavaPath;
        if (settings.GameWindowWidth > 0 && settings.GameWindowHeight > 0)
        {
            option.ScreenWidth = settings.GameWindowWidth;
            option.ScreenHeight = settings.GameWindowHeight;
        }

        var jvm = new List<MArgument>();
        if (settings.MinRamMb > 0)
            jvm.Add(new MArgument($"-Xms{Math.Clamp(settings.MinRamMb, 256, 32768)}M"));
        if (!string.IsNullOrWhiteSpace(settings.ExtraJvmArgs))
            jvm.AddRange(settings.ExtraJvmArgs
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(a => new MArgument(a)));
        if (jvm.Count > 0) option.ExtraJvmArguments = jvm;

        // 额外的游戏参数（例如 --quickPlaySingleplayer <存档名> 直接进入某个世界）
        if (!string.IsNullOrWhiteSpace(extraGameArgs))
        {
            option.ExtraGameArguments = SplitArguments(extraGameArgs)
                .Select(a => new MArgument(a))
                .ToList();
        }

        // 外置登录（authlib-injector）：注入真实的 Yggdrasil 认证服务器
        if (session.UserType == "authlib" && !string.IsNullOrWhiteSpace(settings.AuthlibApiRoot))
        {
            var extAgent = AuthlibService.GetAgentArgument(settings.AuthlibApiRoot);
            if (extAgent != null)
            {
                var extArgs = new List<MArgument> { new(extAgent) };
                if (option.ExtraJvmArguments != null)
                    extArgs.AddRange(option.ExtraJvmArguments);
                option.ExtraJvmArguments = extArgs;
            }
            else
            {
                throw new InvalidOperationException(
                    "外置登录组件（authlib-injector）尚未下载完成，请重新启动该账号一次以自动下载。");
            }
        }

        // 离线自定义皮肤：通过 authlib-injector 指向本地皮肤服务器
        // （只对离线会话生效，且皮肤所属用户名匹配当前玩家）
        if (session.UserType == "legacy" && !string.IsNullOrWhiteSpace(settings.OfflineSkinPath))
        {
            var server = LocalSkinServer.Instance;
            if (File.Exists(settings.OfflineSkinPath) && server.AgentReady
                && (string.IsNullOrWhiteSpace(settings.OfflineSkinPlayer)
                    || settings.OfflineSkinPlayer == session.Username))
            {
                server.ApplySkin(settings.OfflineSkinPath, settings.OfflineSkinModel, session.Username);
                var agent = server.GetAgentArgument();
                if (agent != null)
                {
                    var args = new List<MArgument> { new(agent) };
                    if (option.ExtraJvmArguments != null)
                        args.AddRange(option.ExtraJvmArguments);
                    option.ExtraJvmArguments = args;
                }
            }
        }

        return option;
    }

    /// <summary>导出版本的完整启动命令（不启动游戏），用于「导出启动脚本」。</summary>
    public async Task<string> BuildLaunchCommandAsync(string versionId, MSession session, LauncherSettings settings,
        string? extraGameArgs = null, CancellationToken cancellationToken = default)
    {
        var option = CreateLaunchOption(session, settings, extraGameArgs);
        MinecraftLauncher? launcher = null;
        try
        {
            launcher = CreateLauncher();
            var p = await launcher.BuildProcessAsync(versionId, option, cancellationToken);
            var args = p.StartInfo.Arguments;
            if (string.IsNullOrWhiteSpace(args) && p.StartInfo.ArgumentList.Count > 0)
                args = string.Join(' ', p.StartInfo.ArgumentList.Select(QuoteArg));
            var exe = string.IsNullOrWhiteSpace(p.StartInfo.FileName) ? "java" : p.StartInfo.FileName;
            return $"\"{exe}\" {args}";
        }
        finally
        {
            SafeDispose(launcher);
        }
    }

    private static string QuoteArg(string a) => a.Contains(' ') ? $"\"{a}\"" : a;

    public async Task<Process> StartGameAsync(string versionId, MSession session, LauncherSettings settings,
        string? extraGameArgs = null, CancellationToken cancellationToken = default)
    {
        // 安装（补齐缺失文件）与构建启动命令都在独立实例上做：
        // 复用同一个 MinecraftLauncher 时，上一次被取消的安装会让它一直处于“安装中”，
        // 再启动就抛「Already installing」。
        await _installLock.WaitAsync(cancellationToken);
        MinecraftLauncher? installLauncher = null;
        try
        {
            installLauncher = CreateLauncher();
            await installLauncher.InstallAsync(versionId, cancellationToken);
        }
        finally
        {
            SafeDispose(installLauncher);
            _installLock.Release();
        }

        var option = CreateLaunchOption(session, settings, extraGameArgs);

        Process process;
        MinecraftLauncher? buildLauncher = null;
        try
        {
            buildLauncher = CreateLauncher();
            process = await buildLauncher.BuildProcessAsync(versionId, option, cancellationToken);
        }
        finally
        {
            SafeDispose(buildLauncher);
        }
        process.EnableRaisingEvents = true;
        PlaytimeStatsService.Instance.AttachProcess(versionId, process);

        lock (_gameProcesses) _gameProcesses.Add(process);
        process.Exited += (_, _) =>
        {
            lock (_gameProcesses) _gameProcesses.Remove(process);
            if (ProcessCount == 0) GameExited?.Invoke(this, EventArgs.Empty);
        };

        process.Start();
        GameStarted?.Invoke(this, process);
        return process;
    }

    /// <summary>当前由本启动器拉起的游戏进程数。</summary>
    public int ProcessCount
    {
        get
        {
            lock (_gameProcesses)
            {
                _gameProcesses.RemoveAll(p => p.HasExited);
                return _gameProcesses.Count;
            }
        }
    }

    /// <summary>强制结束所有由本启动器启动的游戏进程（含子进程树）。返回结束的进程数。</summary>
    public int KillAllGames()
    {
        List<Process> snapshot;
        lock (_gameProcesses) snapshot = _gameProcesses.ToList();
        var killed = 0;
        foreach (var p in snapshot)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                    killed++;
                }
            }
            catch { /* 进程可能刚退出 */ }
        }
        return killed;
    }

    public static string Categorize(string versionId, string type)
    {
        if (IsAprilFools(versionId)) return "愚人节版";
        switch (type.ToLowerInvariant())
        {
            case "release": return "正式版";
            case "old_alpha":
            case "old_beta": return "远古版";
            default: return "预览版";
        }
    }

    private static readonly HashSet<string> AprilFoolsIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "15w14a", "3D Shareware v1.34", "20w14infinite",
        "22w13oneblockatime", "23w13a_or_b", "24w14potato", "25w14craftmine",
    };

    private static bool IsAprilFools(string versionId)
    {
        if (AprilFoolsIds.Contains(versionId)) return true;
        var lower = versionId.ToLowerInvariant();
        if (lower.Contains("w14") && (lower.Contains("infinite") || lower.Contains("shareware")
            || lower.Contains("potato") || lower.Contains("craftmine") || lower.Contains("or_b")))
            return true;
        return false;
    }
}
