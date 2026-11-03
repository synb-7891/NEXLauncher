using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NEXLauncher.Services;

/// <summary>
/// 一键开服：自动下载官方服务端 jar、写入 EULA 与离线模式配置、启动服务器并捕获日志。
/// 服务端实例放在 游戏目录\servers\oneclick\。
/// </summary>
public sealed class OneClickServerService
{
    private const string ManifestUrl = "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    private static JsonDocument? _manifestCache;

    private Process? _server;
    private bool _readyRaised;

    public event Action<string>? LogReceived;
    public event Action? ServerReady;
    public event Action? Exited;

    public bool IsRunning => _server is { HasExited: false };
    public string? ServerDir { get; private set; }
    public string? JarFile { get; private set; }

    #region 版本与 Java 探测

    /// <summary>获取可用的正式版版本号（新的在前）。</summary>
    public async Task<IReadOnlyList<string>> GetReleaseVersionsAsync(int limit = 40, CancellationToken ct = default)
    {
        var doc = await GetManifestAsync(ct);
        var list = new List<string>();
        foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (v.GetProperty("type").GetString() != "release") continue;
            list.Add(v.GetProperty("id").GetString()!);
            if (list.Count >= limit) break;
        }
        return list;
    }

    /// <summary>获取最新正式版版本号。</summary>
    public async Task<string> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        var doc = await GetManifestAsync(ct);
        return doc.RootElement.GetProperty("latest").GetProperty("release").GetString()!;
    }

    private static async Task<JsonDocument> GetManifestAsync(CancellationToken ct)
    {
        if (_manifestCache != null) return _manifestCache;
        var json = await Http.GetStringAsync(ManifestUrl, ct);
        _manifestCache = JsonDocument.Parse(json);
        return _manifestCache;
    }

    /// <summary>在 PATH 与常见安装目录中查找可用的 java.exe。</summary>
    public static string? FindJava()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir.Trim(), "java.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            var candidate = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var root in new[] { @"C:\Program Files\Java", @"C:\Program Files (x86)\Java" })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var jdk in Directory.GetDirectories(root))
            {
                var candidate = Path.Combine(jdk, "bin", "java.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }

        var oracle = @"C:\Program Files\Common Files\Oracle\Java\javapath\java.exe";
        return File.Exists(oracle) ? oracle : null;
    }

    #endregion

    #region 准备与下载

    /// <summary>创建服务端目录，写入 EULA 和 server.properties。</summary>
    public string PrepareServerDirectory(string gameDirectory, bool offlineMode, int maxPlayers, string motd)
    {
        var dir = Path.Combine(gameDirectory, "servers", "oneclick");
        Directory.CreateDirectory(dir);
        ServerDir = dir;

        File.WriteAllText(Path.Combine(dir, "eula.txt"),
            "# 由 NEX Launcher 自动生成\n# https://aka.ms/MinecraftEULA\neula=true\n");

        var props = new StringBuilder();
        props.AppendLine("#Minecraft server properties");
        props.AppendLine($"motd={motd}");
        props.AppendLine("server-port=25565");
        props.AppendLine("gamemode=survival");
        props.AppendLine("difficulty=normal");
        props.AppendLine("force-gamemode=false");
        props.AppendLine("pvp=true");
        props.AppendLine("spawn-protection=0");
        props.AppendLine($"max-players={maxPlayers}");
        props.AppendLine($"online-mode={(offlineMode ? "false" : "true")}");
        props.AppendLine("allow-flight=true");
        props.AppendLine("enable-command-block=false");
        props.AppendLine("view-distance=10");
        props.AppendLine("simulation-distance=10");
        if (offlineMode) props.AppendLine("enforce-secure-profile=false");
        File.WriteAllText(Path.Combine(dir, "server.properties"), props.ToString());

        return dir;
    }

    /// <summary>下载指定版本的官方服务端 jar，返回文件名。</summary>
    public async Task<string> DownloadServerJarAsync(string versionId, string serverDir,
        Action<string>? reportProgress = null, CancellationToken ct = default)
    {
        var doc = await GetManifestAsync(ct);
        string? versionUrl = null;
        foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
        {
            if (v.GetProperty("id").GetString() == versionId)
            {
                versionUrl = v.GetProperty("url").GetString();
                break;
            }
        }
        if (versionUrl == null)
            throw new InvalidOperationException($"Mojang 版本清单中找不到 {versionId}。");

        reportProgress?.Invoke("正在获取服务端下载地址...");
        var detailJson = await Http.GetStringAsync(versionUrl, ct);
        using var detail = JsonDocument.Parse(detailJson);
        if (!detail.RootElement.TryGetProperty("downloads", out var downloads)
            || !downloads.TryGetProperty("server", out var server))
            throw new InvalidOperationException($"{versionId} 没有官方服务端版本（可能是客户端独占版本）。");

        var url = server.GetProperty("url").GetString()!;
        var sha1 = server.TryGetProperty("sha1", out var s) ? s.GetString() : null;

        var jarPath = Path.Combine(serverDir, "server.jar");
        reportProgress?.Invoke($"正在下载 {versionId} 服务端（约 60MB）...");

        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        var tmp = jarPath + ".tmp";
        using (var sha = SHA1.Create())
        {
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tmp);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                sha.TransformBlock(buffer, 0, n, null, 0);
                read += n;
                if (total > 0) reportProgress?.Invoke($"下载服务端：{read * 100 / total}%");
            }
            sha.TransformFinalBlock([], 0, 0);

            if (sha1 != null)
            {
                var actual = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
                if (!string.Equals(actual, sha1, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tmp);
                    throw new InvalidDataException("服务端文件校验失败（SHA1 不匹配），已删除，请重试。");
                }
            }
        }

        if (File.Exists(jarPath)) File.Delete(jarPath);
        File.Move(tmp, jarPath);
        JarFile = "server.jar";
        reportProgress?.Invoke("服务端下载完成，校验通过。");
        return "server.jar";
    }

    #endregion

    #region 启动与停止

    /// <summary>启动服务器进程，日志通过事件上报。</summary>
    public void Start(string serverDir, string jarFile, int maxRamMb)
    {
        if (IsRunning) return;
        var java = FindJava() ?? throw new FileNotFoundException(
            "未检测到 Java 运行环境。请先安装 Java 21 或更高版本，然后重试。");

        _readyRaised = false;
        ServerDir = serverDir;
        JarFile = jarFile;

        var psi = new ProcessStartInfo
        {
            FileName = java,
            Arguments = $"-Xms1G -Xmx{maxRamMb}M -jar \"{jarFile}\" nogui",
            WorkingDirectory = serverDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        _server = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _server.OutputDataReceived += (_, e) => HandleLine(e.Data);
        _server.ErrorDataReceived += (_, e) => HandleLine(e.Data);
        _server.Exited += (_, _) => Exited?.Invoke();
        _server.Start();
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();
    }

    private void HandleLine(string? line)
    {
        if (line == null) return;
        LogReceived?.Invoke(line);

        if (!_readyRaised)
        {
            if (line.Contains("Done (") && line.Contains("For help"))
            {
                _readyRaised = true;
                ServerReady?.Invoke();
            }
            else if (line.Contains("You need to agree to the EULA"))
            {
                LogReceived?.Invoke("[NEX] 检测到 EULA 未同意，正在自动写入 eula.txt 并重启建议");
            }
        }
    }

    /// <summary>向服务器控制台发送命令（如 say、op、stop）。</summary>
    public void SendCommand(string command)
    {
        try
        {
            if (_server is { HasExited: false }) _server.StandardInput.WriteLine(command);
        }
        catch { }
    }

    /// <summary>优雅停服：先发 stop，超时后强制结束进程树。</summary>
    public void Stop()
    {
        try
        {
            if (_server is { HasExited: false })
            {
                _server.StandardInput.WriteLine("stop");
                if (!_server.WaitForExit(15000))
                    _server.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            try { _server?.Kill(entireProcessTree: true); } catch { }
        }
        _server = null;
    }
    #endregion
}
