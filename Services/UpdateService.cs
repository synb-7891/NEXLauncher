using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows;

namespace NEXLauncher.Services;

/// <summary>启动器当前版本号。发布新版本时同步修改这里。</summary>
public static class AppInfo
{
    public const string Version = "0.4.0";
}

public sealed record UpdateInfo(string Version, string Url, string Notes, string PublishedAt)
{
    public string ZipFileName => $"NEXLauncher-{Version}.zip";
}

/// <summary>
/// 启动器自动更新：从更新源（一个公开可访问的 manifest.json）检查新版本，
/// 下载 zip 更新包并用外部脚本在退出后覆盖程序目录。
/// manifest 格式：
/// { "version": "0.3.0", "url": "https://.../NEXLauncher-0.3.0.zip", "notes": "更新内容", "publishedAt": "2026-11-01" }
/// </summary>
public sealed class UpdateService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>比较两个 x.y.z 版本号，返回 -1/0/1。</summary>
    public static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var pb = b.Split('.').Select(s => int.TryParse(s, out var n) ? n : 0).ToArray();
        var len = Math.Max(pa.Length, pb.Length);
        for (var i = 0; i < len; i++)
        {
            var x = i < pa.Length ? pa[i] : 0;
            var y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static readonly HttpClient ApiHttp = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "Accept", "application/vnd.github.raw" } }
    };

    /// <summary>
    /// 获取更新清单。优先走 GitHub API 直读仓库文件（内容永远最新、国内可达），
    /// 失败时回退到配置的 manifestUrl（jsDelivr，分支缓存最长可能滞后 12 小时）。
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(string manifestUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(manifestUrl)) return null;

        string? json = null;
        // 主源：从 jsDelivr 地址解析出 owner/repo，用 GitHub API 直读（绕过 CDN 缓存）
        var m = System.Text.RegularExpressions.Regex.Match(
            manifestUrl, @"jsdelivr\.net/gh/([^/]+)/([^/@]+)(?:@[^/]+)?/(.+\.json)");
        if (m.Success)
        {
            try
            {
                json = await ApiHttp.GetStringAsync(
                    $"https://api.github.com/repos/{m.Groups[1].Value}/{m.Groups[2].Value}/contents/{m.Groups[3].Value}", ct);
            }
            catch { /* 回退到 CDN */ }
        }
        json ??= await Http.GetStringAsync(manifestUrl, ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
        var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrWhiteSpace(version) || string.IsNullOrWhiteSpace(url)) return null;
        if (CompareVersions(version, AppInfo.Version) <= 0) return null;
        var notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? "" : "";
        var published = root.TryGetProperty("publishedAt", out var p) ? p.GetString() ?? "" : "";
        return new UpdateInfo(version, url, notes, published);
    }

    /// <summary>下载更新包到临时目录，返回 zip 路径。</summary>
    public async Task<string> DownloadAsync(UpdateInfo info, IProgress<(long Received, long Total)>? progress,
        CancellationToken ct = default)
    {
        var dest = Path.Combine(Path.GetTempPath(), info.ZipFileName);
        using var resp = await Http.GetAsync(info.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(dest);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            progress?.Report((read, total > 0 ? total : read));
        }
        return dest;
    }

    /// <summary>
    /// 应用更新：解压到临时目录，启动外部脚本（等待本进程退出 → 覆盖程序目录 → 重启启动器），然后关闭当前程序。
    /// </summary>
    public void ApplyUpdate(string zipPath)
    {
        var extractDir = Path.Combine(Path.GetTempPath(), "NEXLauncher-update-" + Guid.NewGuid().ToString("N")[..8]);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        // zip 里可能直接是文件，也可能带一层目录；找到包含 NEXLauncher.dll 的目录
        var rootDir = Directory
            .EnumerateDirectories(extractDir, "*", SearchOption.AllDirectories)
            .FirstOrDefault(d => File.Exists(Path.Combine(d, "NEXLauncher.dll")))
            ?? (File.Exists(Path.Combine(extractDir, "NEXLauncher.dll")) ? extractDir : null)
            ?? throw new InvalidDataException("更新包内容不完整（未找到 NEXLauncher.dll）。");

        var appDir = AppContext.BaseDirectory;
        var exePath = Path.Combine(appDir, "NEXLauncher.exe");
        var script = Path.Combine(Path.GetTempPath(), "NEXLauncher-update.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            $"robocopy \"{rootDir}\" \"{appDir}\" /E /IS /IT /NFL /NDL /NJH /NJS >nul\r\n" +
            $"start \"\" \"{exePath}\"\r\n" +
            "del \"%~f0\"\r\n");

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{script}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        Application.Current.Shutdown();
    }
}
