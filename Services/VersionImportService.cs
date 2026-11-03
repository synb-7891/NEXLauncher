using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace NEXLauncher.Services;

/// <summary>从其它 .minecraft 目录扫描到的可导入版本。</summary>
public sealed class ImportableVersion
{
    public string Id { get; init; } = "";
    /// <summary>源 .minecraft 根目录</summary>
    public string SourceGameDir { get; init; } = "";
    /// <summary>源 versions\&lt;id&gt; 目录</summary>
    public string SourceVersionDir { get; init; } = "";
    /// <summary>原版 / Fabric / Forge / NeoForge / Quilt / OptiFine</summary>
    public string Loader { get; init; } = "原版";
    /// <summary>该加载器所基于的游戏版本（原版则为自身）</summary>
    public string BaseVersion { get; init; } = "";
    public long SizeBytes { get; set; }
    public bool ExistsInTarget { get; set; }
    /// <summary>源目录与目标目录是同一个，无需导入</summary>
    public bool IsSameDirectory { get; set; }

    public string SizeText => SizeBytes switch
    {
        >= 1L << 30 => $"{SizeBytes / (1024.0 * 1024 * 1024):0.#} GB",
        >= 1L << 20 => $"{SizeBytes / (1024.0 * 1024):0.#} MB",
        >= 1L << 10 => $"{SizeBytes / 1024.0:0} KB",
        _ => $"{SizeBytes} B"
    };
}

public enum VersionImportMode
{
    /// <summary>复制一份到当前游戏目录（独立、安全）</summary>
    Copy,
    /// <summary>用目录链接指向原位置（不占空间，源目录删除后失效）</summary>
    Link
}

public sealed class ImportProgress
{
    public int Done { get; init; }
    public int Total { get; init; }
    public string Current { get; init; } = "";
    public double Percent => Total <= 0 ? 0 : Math.Min(100, Done * 100.0 / Total);
}

/// <summary>
/// 导入本机已有游戏版本：扫描其它 .minecraft 目录、识别加载器、复制或建立目录链接。
/// </summary>
public sealed class VersionImportService
{
    private readonly string _targetGameDir;

    public VersionImportService(string targetGameDir)
    {
        _targetGameDir = Path.GetFullPath(targetGameDir);
    }

    public string TargetGameDir => _targetGameDir;

    #region 扫描

    /// <summary>本机常见 .minecraft 位置（当前启动器、官方默认、各固定磁盘的常见目录）。</summary>
    public static List<string> DiscoverGameDirectories(CancellationToken ct = default)
    {
        var result = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.GetFullPath(path);
                if (!Directory.Exists(full)) return;
                if (!Directory.Exists(Path.Combine(full, "versions"))) return;
                if (!result.Contains(full, StringComparer.OrdinalIgnoreCase)) result.Add(full);
            }
            catch { }
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Add(Path.Combine(AppContext.BaseDirectory, ".minecraft"));
        Add(Path.Combine(appData, ".minecraft"));
        Add(Path.Combine(localAppData, ".minecraft"));
        Add(Path.Combine(profile, ".minecraft"));
        Add(Path.Combine(profile, "Desktop", ".minecraft"));

        // 各固定磁盘根目录及其一级子目录（PCL2 / HMCL / 整合包常见的放置位置）
        string[] candidates = [".minecraft", "Minecraft", "mc", "Games", "PCL", "HMCL", "HMCL-3.6", "启动器"];
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (ct.IsCancellationRequested) return result;
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                var root = drive.RootDirectory.FullName;
                foreach (var name in candidates)
                {
                    Add(Path.Combine(root, name, ".minecraft"));
                    Add(Path.Combine(root, name));
                }
                Add(Path.Combine(root, ".minecraft"));

                string[] subs;
                try { subs = Directory.GetDirectories(root); }
                catch { continue; }
                foreach (var sub in subs.Take(80))
                {
                    if (ct.IsCancellationRequested) return result;
                    Add(Path.Combine(sub, ".minecraft"));
                }
            }
            catch { }
        }

        return result;
    }

    /// <summary>扫描一个目录（.minecraft 根目录或 versions 目录本身）中的可导入版本。</summary>
    public List<ImportableVersion> Scan(string directory, CancellationToken ct = default)
    {
        var gameDir = NormalizeGameDir(directory);
        if (gameDir == null) return [];

        var versionsDir = Path.Combine(gameDir, "versions");
        var list = new List<ImportableVersion>();
        string[] dirs;
        try { dirs = Directory.GetDirectories(versionsDir); }
        catch { return list; }

        var sameDir = string.Equals(
            Path.GetFullPath(gameDir).TrimEnd(Path.DirectorySeparatorChar),
            _targetGameDir.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

        foreach (var dir in dirs)
        {
            if (ct.IsCancellationRequested) break;
            var id = Path.GetFileName(dir);
            // 只接受标准的 versions\<id>\<id>.json 结构，避免导入无法启动的残缺版本
            var json = Path.Combine(dir, id + ".json");
            if (!File.Exists(json)) continue;

            var (loader, baseVersion) = ReadVersionMeta(json);
            list.Add(new ImportableVersion
            {
                Id = id,
                SourceGameDir = gameDir,
                SourceVersionDir = dir,
                Loader = loader,
                BaseVersion = baseVersion,
                SizeBytes = SafeDirectorySize(dir),
                ExistsInTarget = !sameDir && File.Exists(Path.Combine(_targetGameDir, "versions", id, id + ".json")),
                IsSameDirectory = sameDir
            });
        }

        return list
            .OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.SourceGameDir, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>扫描多个目录并按版本 ID 聚合，标记同一个版本的不同副本。</summary>
    public List<ImportableVersion> ScanMany(IEnumerable<string> directories, CancellationToken ct = default)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<ImportableVersion>();
        foreach (var dir in directories)
        {
            if (ct.IsCancellationRequested) break;
            foreach (var v in Scan(dir, ct))
            {
                // 同一目录扫两次去重（源目录 + 目标目录相同也不需要重复列出）
                if (!seen.Add(v.SourceVersionDir)) continue;
                result.Add(v);
            }
        }
        return result;
    }

    private static string? NormalizeGameDir(string directory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(directory)) return null;
            var full = Path.GetFullPath(directory.Trim().Trim('"'));
            if (!Directory.Exists(full)) return null;
            // 用户可能直接选中了 versions 目录
            if (string.Equals(Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)), "versions",
                    StringComparison.OrdinalIgnoreCase))
            {
                var parent = Path.GetDirectoryName(full.TrimEnd(Path.DirectorySeparatorChar));
                if (parent != null && Directory.Exists(Path.Combine(parent, "versions"))) return parent;
            }
            return Directory.Exists(Path.Combine(full, "versions")) ? full : null;
        }
        catch { return null; }
    }

    /// <summary>从版本 JSON 判断加载器类型与基础版本。</summary>
    private static (string Loader, string BaseVersion) ReadVersionMeta(string jsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            var root = doc.RootElement;
            var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            var inherits = root.TryGetProperty("inheritsFrom", out var inhEl) ? inhEl.GetString() ?? "" : "";

            var libIds = new List<string>();
            if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
            {
                foreach (var lib in libs.EnumerateArray())
                {
                    if (lib.TryGetProperty("name", out var n) && n.GetString() is { } s) libIds.Add(s);
                }
                // 只要关键几项，避免大版本 JSON 全量遍历过慢
            }

            var probe = (id + " " + string.Join(" ", libIds.Take(200))).ToLowerInvariant();
            var loader = "原版";
            if (probe.Contains("neoforge")) loader = "NeoForge";
            else if (probe.Contains("quilt-loader") || probe.Contains("quilt")) loader = "Quilt";
            else if (probe.Contains("fabric-loader") || probe.Contains("fabric")) loader = "Fabric";
            else if (probe.Contains("forge")) loader = "Forge";
            else if (probe.Contains("optifine")) loader = "OptiFine";

            var baseVersion = string.IsNullOrWhiteSpace(inherits) ? id : inherits;
            return (loader, baseVersion);
        }
        catch
        {
            return ("未知", "");
        }
    }

    private static long SafeDirectorySize(string dir)
    {
        long size = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(f).Length; } catch { }
            }
        }
        catch { }
        return size;
    }

    #endregion

    #region 导入

    /// <summary>
    /// 导入一个版本。返回实际导入到的目标路径。
    /// <paramref name="withLibraries"/> 为 true 时同时补齐 libraries / assets（可离线启动）。
    /// </summary>
    public Task<string> ImportAsync(ImportableVersion version, VersionImportMode mode, bool withLibraries,
        IProgress<ImportProgress>? progress, CancellationToken ct = default)
        => Task.Run(() => ImportCore(version, mode, withLibraries, progress, ct), ct);

    private string ImportCore(ImportableVersion version, VersionImportMode mode, bool withLibraries,
        IProgress<ImportProgress>? progress, CancellationToken ct)
    {
        var id = version.Id;
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            id.Contains("..") || id.Contains('/') || id.Contains('\\'))
            throw new InvalidOperationException("版本名包含非法字符，无法导入。");

        var versionsRoot = Path.GetFullPath(Path.Combine(_targetGameDir, "versions"));
        var target = Path.GetFullPath(Path.Combine(versionsRoot, id));
        if (!target.StartsWith(versionsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("目标路径越界，已中止导入。");

        if (!Directory.Exists(version.SourceVersionDir))
            throw new DirectoryNotFoundException($"源版本目录不存在：{version.SourceVersionDir}");

        Directory.CreateDirectory(versionsRoot);
        if (Directory.Exists(target))
        {
            // 覆盖导入：只删除 versions 下的一级子目录（已通过前缀校验）
            try { SafeDeleteDirectory(target); }
            catch (Exception ex) { throw new IOException($"无法覆盖已有版本文件夹：{ex.Message}", ex); }
        }

        if (mode == VersionImportMode.Link)
        {
            CreateJunction(target, version.SourceVersionDir);
        }
        else
        {
            var plan = new List<(string Src, string Dst)>();
            CollectFiles(version.SourceVersionDir, target, plan);
            if (withLibraries)
            {
                // 只补齐目标缺失的库与资源，不覆盖已有文件
                CollectMissing(Path.Combine(version.SourceGameDir, "libraries"),
                    Path.Combine(_targetGameDir, "libraries"), plan);
                CollectMissing(Path.Combine(version.SourceGameDir, "assets"),
                    Path.Combine(_targetGameDir, "assets"), plan);
            }

            var total = plan.Count;
            var done = 0;
            foreach (var (src, dst) in plan)
            {
                ct.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true);
                done++;
                if (done % 24 == 0 || done == total)
                    progress?.Report(new ImportProgress { Done = done, Total = total, Current = Path.GetFileName(src) });
            }
            progress?.Report(new ImportProgress { Done = total, Total = total, Current = "" });
        }

        return target;
    }

    private static void CollectFiles(string srcDir, string dstDir, List<(string, string)> plan)
    {
        foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(srcDir, file);
            plan.Add((file, Path.Combine(dstDir, rel)));
        }
    }

    /// <summary>收集目标缺失的文件（同名同大小视为已存在，跳过）。</summary>
    private static void CollectMissing(string srcDir, string dstDir, List<(string, string)> plan)
    {
        if (!Directory.Exists(srcDir)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(srcDir, file);
                var dst = Path.Combine(dstDir, rel);
                try
                {
                    var info = new FileInfo(file);
                    var dstInfo = new FileInfo(dst);
                    if (dstInfo.Exists && dstInfo.Length == info.Length) continue;
                    plan.Add((file, dst));
                }
                catch { }
            }
        }
        catch { }
    }

    /// <summary>
    /// 删除版本目录。若目标是目录链接（junction），只删除链接本身，绝不递归删除源目录内容。
    /// </summary>
    private static void SafeDeleteDirectory(string path)
    {
        var info = new DirectoryInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(path);
            return;
        }
        Directory.Delete(path, true);
    }

    /// <summary>用 mklink /J 建立目录联接（不需要管理员权限）。</summary>
    private static void CreateJunction(string target, string source)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{target}\" \"{source}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 mklink。");
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(); } catch { }
                throw new IOException("创建目录链接超时。");
            }
            if (p.ExitCode != 0 || !Directory.Exists(target))
                throw new IOException("创建目录链接失败（源目录可能在不同磁盘，或目标已存在）。");
        }
        catch (IOException) { throw; }
        catch (Exception ex)
        {
            throw new IOException($"创建目录链接失败：{ex.Message}", ex);
        }
    }

    #endregion
}
