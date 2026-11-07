using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace NEXLauncher.Services;

public sealed record JavaRuntime(string JavaPath, string Home, string Version, int Major, string Bit)
{
    public string Title => $"Java {Version}（{(Bit == "64" ? "64 位" : "32 位")}）";
}

/// <summary>
/// Java 运行时管理：自动扫描本机已安装的 Java（注册表 / PATH / JAVA_HOME / 常见安装目录 /
/// .minecraft 自带运行时 / 本启动器下载的 JRE），并支持从 Adoptium 一键下载安装。
/// </summary>
public sealed class JavaService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>本启动器管理的 Java 安装目录。</summary>
    public static string ManagedRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXLauncher", "java");

    /// <summary>扫描本机所有可用 Java（去重、按主版本降序）。</summary>
    public IReadOnlyList<JavaRuntime> Detect(string? preferredPath = null)
    {
        var found = new List<JavaRuntime>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? exe)
        {
            if (string.IsNullOrWhiteSpace(exe)) return;
            try
            {
                exe = Path.GetFullPath(exe);
                if (!File.Exists(exe)) return;
                var home = Directory.GetParent(Path.GetDirectoryName(exe)!)?.FullName;
                if (home == null || !seen.Add(home)) return;
                var info = ReadInfo(exe, home);
                if (info != null) found.Add(info);
            }
            catch { /* 单个候选失败不影响整体 */ }
        }

        // 1. 用户在设置里指定的
        Add(preferredPath);

        // 2. JAVA_HOME 与 PATH
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome)) Add(Path.Combine(javaHome, "bin", "javaw.exe"));
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var d = dir.Trim().Trim('"');
            Add(Path.Combine(d, "javaw.exe"));
            Add(Path.Combine(d, "java.exe"));
        }

        // 3. 注册表（JavaSoft 各版本）
        foreach (var root in new[] { @"SOFTWARE\JavaSoft", @"SOFTWARE\WOW6432Node\JavaSoft" })
        {
            foreach (var sub in new[] { "JDK", "JRE", "Java Development Kit", "Java Runtime Environment" })
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey($@"{root}\{sub}");
                    if (key == null) continue;
                    foreach (var ver in key.GetSubKeyNames())
                    {
                        using var vk = key.OpenSubKey(ver);
                        if (vk?.GetValue("JavaHome") is string jh && !string.IsNullOrEmpty(jh))
                            Add(Path.Combine(jh, "bin", "javaw.exe"));
                    }
                }
                catch { }
            }
        }

        // 4. 常见安装目录（各发行版）
        var parents = new List<string>();
        foreach (var pf in new[]
                 {
                     Environment.GetEnvironmentVariable("ProgramFiles"),
                     Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                     Environment.GetEnvironmentVariable("ProgramW6432"),
                 })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            parents.Add(Path.Combine(pf, "Java"));
            parents.Add(Path.Combine(pf, "Eclipse Adoptium"));
            parents.Add(Path.Combine(pf, "Eclipse Foundation"));
            parents.Add(Path.Combine(pf, "Microsoft"));
            parents.Add(Path.Combine(pf, "Zulu"));
            parents.Add(Path.Combine(pf, "Amazon Corretto"));
            parents.Add(Path.Combine(pf, "BellSoft"));
            parents.Add(Path.Combine(pf, "Semeru"));
            parents.Add(pf);
        }
        parents.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));
        parents.Add(ManagedRoot);

        foreach (var parent in parents)
        {
            if (!Directory.Exists(parent)) continue;
            foreach (var dir in SafeEnumerate(parent))
            {
                Add(Path.Combine(dir, "bin", "javaw.exe"));
                // .minecraft/runtime/<name>/<platform> 多一层
                foreach (var inner in SafeEnumerate(dir))
                    Add(Path.Combine(inner, "bin", "javaw.exe"));
            }
        }

        // 5. 官方启动器自带的运行时
        var mcRuntime = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft", "runtime");
        if (Directory.Exists(mcRuntime))
            foreach (var d in SafeEnumerate(mcRuntime))
                foreach (var inner in SafeEnumerate(d))
                    Add(Path.Combine(inner, "bin", "javaw.exe"));

        return found
            .OrderByDescending(j => j.Major)
            .ThenByDescending(j => j.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IEnumerable<string> SafeEnumerate(string dir)
    {
        try { return Directory.EnumerateDirectories(dir).ToList(); }
        catch { return Array.Empty<string>(); }
    }

    private static JavaRuntime? ReadInfo(string exe, string home)
    {
        var version = "";
        // 优先读 release 文件（快，不启动进程）
        try
        {
            var release = Path.Combine(home, "release");
            if (File.Exists(release))
            {
                foreach (var line in File.ReadAllLines(release))
                {
                    if (line.StartsWith("JAVA_VERSION=", StringComparison.OrdinalIgnoreCase))
                    {
                        version = line.Split('=', 2)[1].Trim().Trim('"');
                        break;
                    }
                }
            }
        }
        catch { }

        if (string.IsNullOrEmpty(version)) version = QueryVersion(exe);
        if (string.IsNullOrEmpty(version)) return null;

        var major = ParseMajor(version);
        if (major <= 0) return null;

        var bit = "64";
        try
        {
            if (File.Exists(exe))
            {
                // 32 位 java 会位于 Program Files (x86) 或 SYSTEM32；用 PE 头判断更准确
                bit = Is64BitPe(exe) ? "64" : "32";
            }
        }
        catch { }

        return new JavaRuntime(exe, home, version, major, bit);
    }

    private static string QueryVersion(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return "";
            var text = p.StandardError.ReadToEnd() + p.StandardOutput.ReadToEnd();
            p.WaitForExit(4000);
            var m = Regex.Match(text, "version\\s+\"([^\"]+)\"");
            return m.Success ? m.Groups[1].Value : "";
        }
        catch { return ""; }
    }

    private static int ParseMajor(string version)
    {
        var v = version.Trim();
        if (v.StartsWith("1.")) v = v[2..];              // 1.8.0_381 → 8
        var head = v.Split('.', '_', '-', '+')[0];
        return int.TryParse(head, out var n) ? n : 0;
    }

    private static bool Is64BitPe(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            using var br = new BinaryReader(fs);
            if (br.ReadUInt16() != 0x5A4D) return true;   // MZ
            fs.Position = 0x3C;
            var peOffset = br.ReadInt32();
            fs.Position = peOffset + 4;
            var machine = br.ReadUInt16();
            return machine == 0x8664 || machine == 0xAA64; // x64 / arm64
        }
        catch { return true; }
    }

    /// <summary>从 Adoptium 下载并安装 Temurin JRE（默认 21），返回 javaw.exe 路径。</summary>
    public async Task<string> InstallAsync(int major, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report($"正在查询 Temurin JRE {major} 下载地址...");
        var api = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot" +
                  "?architecture=x64&image_type=jre&os=windows&vendor=eclipse";
        var json = await Http.GetStringAsync(api, cancellationToken);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException($"没有找到适用于 Windows x64 的 JRE {major}。");

        var pkg = doc.RootElement[0].GetProperty("binary").GetProperty("package");
        var link = pkg.GetProperty("link").GetString()
                   ?? throw new InvalidOperationException("下载地址为空。");
        var name = pkg.TryGetProperty("name", out var n) ? n.GetString() ?? "jre.zip" : "jre.zip";
        var size = pkg.TryGetProperty("size", out var s) ? s.GetInt64() : 0;

        Directory.CreateDirectory(ManagedRoot);
        var zipPath = Path.Combine(Path.GetTempPath(), "nex-" + name);
        progress?.Report($"正在下载 {name}（{size / 1048576.0:0.#} MB）...");

        // Adoptium 的包实际托管在 GitHub Releases，国内直连很慢：
        // 先试加速镜像，失败再回落官方直链
        var candidates = new List<string>();
        if (link.Contains("github.com", StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add("https://gh-proxy.com/" + link);
            candidates.Add("https://ghproxy.net/" + link);
        }
        candidates.Add(link);

        Exception? last = null;
        var downloaded = false;
        foreach (var url in candidates)
        {
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? size;
                await using var src = await resp.Content.ReadAsStreamAsync(cancellationToken);
                await using var dst = File.Create(zipPath);
                var buffer = new byte[81920];
                long read = 0; var lastReport = 0;
                int n2;
                while ((n2 = await src.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n2), cancellationToken);
                    read += n2;
                    if (total > 0)
                    {
                        var pct = (int)(read * 100 / total);
                        if (pct >= lastReport + 5)
                        {
                            lastReport = pct;
                            progress?.Report($"正在下载 Java：{pct}%（{read / 1048576.0:0.#} / {total / 1048576.0:0.#} MB）");
                        }
                    }
                }
                downloaded = true;
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { last = ex; try { File.Delete(zipPath); } catch { } }
        }
        if (!downloaded)
            throw new InvalidOperationException("Java 下载失败：" + (last?.Message ?? "所有下载源都不可用"));

        progress?.Report("正在解压...");
        var extractDir = Path.Combine(ManagedRoot, $"temurin-{major}");
        if (Directory.Exists(extractDir))
        {
            try { Directory.Delete(extractDir, true); } catch { }
        }
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, true);
        try { File.Delete(zipPath); } catch { }

        // Adoptium 包内一般是 jdk-21.0.x+y-jre/ 这一层
        var javaw = Directory.EnumerateFiles(extractDir, "javaw.exe", SearchOption.AllDirectories)
            .FirstOrDefault(p => p.Replace('\\', '/').Contains("/bin/", StringComparison.OrdinalIgnoreCase));
        if (javaw == null) throw new InvalidOperationException("解压后没有找到 javaw.exe，安装包可能不完整。");

        progress?.Report("Java 安装完成。");
        return javaw;
    }
}
