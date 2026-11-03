using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace NEXLauncher.Services;

/// <summary>
/// Playit.gg 内网穿透隧道：自动下载 playit-agent，启动/停止隧道，解析对外地址。
/// Agent 保存在 %LocalAppData%\NEXLauncher\tools\playit.exe。
/// </summary>
public sealed class PlayitTunnelService
{
    /// <summary>下载源：官方 GitHub 直连 + 国内加速镜像（按顺序尝试）。</summary>
    private static readonly string[] DownloadUrls =
    {
        "https://github.com/playit-cloud/playit-agent/releases/latest/download/playit-windows-x86_64.exe",
        "https://ghproxy.net/https://github.com/playit-cloud/playit-agent/releases/latest/download/playit-windows-x86_64.exe",
        "https://gh-proxy.com/https://github.com/playit-cloud/playit-agent/releases/latest/download/playit-windows-x86_64.exe",
    };

    private static readonly string AgentPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEXLauncher", "tools", "playit.exe");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static readonly Regex AddressPattern =
        new(@"\b(?:[a-z0-9-]+\.)*(?:at\.playit\.gg|playit\.gg)(?::\d{2,5})?\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ClaimPattern =
        new(@"https?://(?:[a-z0-9-]+\.)*playit\.gg/claim/[A-Za-z0-9_-]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private Process? _process;

    public event Action<string>? LogReceived;
    public event Action<string>? AddressDetected;
    public event Action<string>? ClaimUrlDetected;
    public event Action? Exited;

    public bool IsRunning => _process is { HasExited: false };
    public string? CurrentAddress { get; private set; }
    /// <summary>首次使用需要打开的授权地址（playit.gg/claim/xxx）。</summary>
    public string? ClaimUrl { get; private set; }

    /// <summary>agent 文件是否完整可用（真实文件约 5.4MB，小于 1MB 视为下载中断的损坏文件）。</summary>
    public bool IsAgentValid =>
        File.Exists(AgentPath) && new FileInfo(AgentPath).Length >= 1_000_000;

    /// <summary>确保 agent 已下载；返回是否就绪。进度通过回调上报。</summary>
    public async Task<bool> EnsureAgentAsync(Action<string> reportProgress, CancellationToken cancellationToken = default)
    {
        // 损坏/截断的旧文件直接删掉重下
        if (File.Exists(AgentPath) && !IsAgentValid)
        {
            reportProgress("检测到隧道程序文件不完整，正在重新下载...");
            try { File.Delete(AgentPath); } catch { }
        }
        if (IsAgentValid) return true;

        reportProgress("正在下载 playit-agent（约 5.4MB）...");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AgentPath)!);
            string? lastError = null;
            foreach (var url in DownloadUrls)
            {
                try
                {
                    await DownloadAgentAsync(url, reportProgress, cancellationToken);
                    reportProgress("playit-agent 下载完成。");
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                    reportProgress($"下载源失败（{ex.Message}），尝试下一个下载源...");
                    try { File.Delete(AgentPath + ".downloading"); } catch { }
                }
            }
            throw new IOException("所有下载源均失败：" + lastError);
        }
        catch (Exception ex)
        {
            try { if (File.Exists(AgentPath)) File.Delete(AgentPath); } catch { }
            try { File.Delete(AgentPath + ".downloading"); } catch { }
            reportProgress("下载失败：" + ex.Message + "（可重试开服或检查网络）");
            return false;
        }
    }

    private static async Task DownloadAgentAsync(string url, Action<string> reportProgress, CancellationToken cancellationToken)
    {
        using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(cancellationToken);
        var tmpPath = AgentPath + ".downloading";
        await using (var dst = File.Create(tmpPath))
        {
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
                read += n;
                if (total > 0) reportProgress($"下载 playit-agent：{read * 100 / total}%");
            }
            // 下载完整性校验：长度不符视为失败
            if (total > 0 && read != total)
                throw new IOException($"下载不完整（{read}/{total} 字节），网络中断");
        }
        if (new FileInfo(tmpPath).Length < 1_000_000)
            throw new IOException("下载的文件大小异常，可能被网络拦截");
        File.Move(tmpPath, AgentPath, overwrite: true);
    }

    /// <summary>启动隧道进程，后台读取输出并解析对外地址。</summary>
    public void Start()
    {
        if (IsRunning || !IsAgentValid) return;

        CurrentAddress = null;
        ClaimUrl = null;
        var psi = new ProcessStartInfo
        {
            FileName = AgentPath,
            WorkingDirectory = Path.GetDirectoryName(AgentPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => HandleLine(e.Data);
        _process.ErrorDataReceived += (_, e) => HandleLine(e.Data);
        _process.Exited += (_, _) => Exited?.Invoke();
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private void HandleLine(string? line)
    {
        if (line == null) return;
        LogReceived?.Invoke(line);

        if (ClaimUrl == null)
        {
            var claim = ClaimPattern.Match(line);
            if (claim.Success)
            {
                ClaimUrl = claim.Value;
                ClaimUrlDetected?.Invoke(claim.Value);
            }
        }

        if (CurrentAddress == null)
        {
            var m = AddressPattern.Match(line);
            if (m.Success)
            {
                // 授权链接里的域名不是对外地址，排除掉
                if (!m.Value.Contains("/claim", StringComparison.OrdinalIgnoreCase))
                {
                    CurrentAddress = m.Value;
                    AddressDetected?.Invoke(m.Value);
                }
            }
        }
    }

    public void Stop()
    {
        try
        {
            if (_process is { HasExited: false })
                _process.Kill(entireProcessTree: true);
        }
        catch { }
        _process = null;
        CurrentAddress = null;
    }
}
