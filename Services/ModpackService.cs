using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace NEXLauncher.Services;

/// <summary>整合包里单个待下载的文件（.mrpack 的 files[]）。</summary>
public sealed record ModpackFile(string Path, string? Url, long Size, string? Sha1, string? Sha512, string ClientEnv);

/// <summary>解析后的整合包预览信息（不落地任何文件）。</summary>
public sealed record ModpackInfo(
    string SourcePath,
    string Format,          // "mrpack" / "curseforge" / "zip"
    string Name,
    string Version,
    string McVersion,
    string LoaderId,        // "" / fabric / forge / neoforge / quilt
    string LoaderVersion,
    List<ModpackFile> Files,
    long TotalBytes,
    int OverrideEntries,
    int MissingDownloadCount);

/// <summary>已安装整合包记录（用于卸载与展示）。</summary>
public sealed record ModpackRecord(
    string Id,
    string Name,
    string Version,
    string McVersion,
    string LoaderId,
    string LoaderVersion,
    string Format,
    List<string> Files,
    string? BackupDir,
    string InstalledAt);

/// <summary>
/// 整合包安装：支持 Modrinth 的 .mrpack 与 CurseForge 的 .zip。
/// 直接把 mods / config / resourcepacks / overrides 落进游戏目录（本启动器的游戏目录即 .minecraft 根），
/// 安装前可选择备份已有 mods，安装后写 nex-modpacks/&lt;id&gt;/pack.json 记录，便于一键卸载。
/// </summary>
public sealed class ModpackService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NEXLauncher/0.4 (+https://github.com/synb-7891/NEXLauncher)");
        return c;
    }

    public static string InstancesRoot(string gameDir) => Path.Combine(gameDir, "nex-modpacks");

    /// <summary>读取整合包元信息（支持 .mrpack / CurseForge .zip / 普通整合 zip）。</summary>
    public static async Task<ModpackInfo> InspectAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("找不到整合包文件", path);

        await using var fs = File.OpenRead(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: false);

        var name = Path.GetFileNameWithoutExtension(path);
        var version = "1.0";
        var mcVersion = "";
        var loaderId = "";
        var loaderVersion = "";
        var files = new List<ModpackFile>();
        var format = "zip";
        var overrides = 0;
        var missing = 0;

        var indexEntry = FindEntry(zip, "modrinth.index.json");
        var manifestEntry = FindEntry(zip, "manifest.json");

        if (indexEntry != null)
        {
            format = "mrpack";
            await using var s = indexEntry.Open();
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            var root = doc.RootElement;
            name = Str(root, "name") ?? name;
            version = Str(root, "versionId") ?? version;
            if (root.TryGetProperty("dependencies", out var deps))
            {
                mcVersion = Str(deps, "minecraft") ?? "";
                foreach (var (key, id) in new[]
                         {
                             ("fabric-loader", "fabric"), ("quilt-loader", "quilt"),
                             ("forge", "forge"), ("neoforge", "neoforge")
                         })
                {
                    var v = Str(deps, key);
                    if (string.IsNullOrEmpty(v)) continue;
                    loaderId = id;
                    loaderVersion = v;
                    break;
                }
            }
            if (root.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var f in arr.EnumerateArray())
                {
                    var rel = Str(f, "path");
                    if (string.IsNullOrWhiteSpace(rel)) continue;
                    var clientEnv = f.TryGetProperty("env", out var env) ? (Str(env, "client") ?? "required") : "required";
                    if (clientEnv == "unsupported") continue;

                    string? url = null;
                    if (f.TryGetProperty("downloads", out var dls) && dls.ValueKind == JsonValueKind.Array)
                        url = dls.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
                    long size = 0;
                    if (f.TryGetProperty("fileSize", out var fsEl) && fsEl.TryGetInt64(out var fsv)) size = fsv;

                    string? sha1 = null, sha512 = null;
                    if (f.TryGetProperty("hashes", out var hs))
                    {
                        sha1 = Str(hs, "sha1");
                        sha512 = Str(hs, "sha512");
                    }
                    if (string.IsNullOrEmpty(url)) missing++;
                    files.Add(new ModpackFile(rel.Replace('\\', '/'), url, size, sha1, sha512, clientEnv));
                }
            }
        }
        else if (manifestEntry != null)
        {
            format = "curseforge";
            await using var s = manifestEntry.Open();
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
            var root = doc.RootElement;
            name = Str(root, "name") ?? name;
            version = Str(root, "version") ?? version;
            if (root.TryGetProperty("minecraft", out var mc))
            {
                mcVersion = Str(mc, "version") ?? "";
                if (mc.TryGetProperty("modLoaders", out var loaders) && loaders.ValueKind == JsonValueKind.Array)
                {
                    foreach (var l in loaders.EnumerateArray())
                    {
                        var id = Str(l, "id") ?? "";
                        var primary = l.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True;
                        if (!primary && loaderId.Length > 0) continue;
                        var dash = id.IndexOf('-');
                        if (dash <= 0) continue;
                        var kind = id[..dash];
                        loaderId = kind switch { "fabric" => "fabric", "quilt" => "quilt", "forge" => "forge", "neoforge" => "neoforge", _ => "" };
                        loaderVersion = id[(dash + 1)..];
                        if (primary) break;
                    }
                }
            }
            // CurseForge 包内的文件需要官方 API Key 才能下载，这里只统计数量，安装时提示手动补
            if (root.TryGetProperty("files", out var arr) && arr.ValueKind == JsonValueKind.Array)
                missing = arr.GetArrayLength();
        }

        foreach (var e in zip.Entries)
        {
            var n = e.FullName.Replace('\\', '/');
            if (n.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)
                || n.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase))
                overrides++;
        }

        long total = files.Sum(f => f.Size);
        return new ModpackInfo(path, format, name, version, mcVersion, loaderId, loaderVersion,
            files, total, overrides, format == "curseforge" ? missing : missing);
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string name) =>
        zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    /// <summary>
    /// 安装整合包：下载文件 → 解压 overrides → 写记录。
    /// progress 回调：(阶段文字, 0~1 进度)。
    /// </summary>
    public static async Task<ModpackRecord> InstallAsync(
        ModpackInfo info, string gameDir, bool backupMods,
        IProgress<(string Message, double Progress)>? progress = null,
        CancellationToken ct = default)
    {
        var root = Path.GetFullPath(gameDir);
        Directory.CreateDirectory(root);

        var id = Slug($"{info.Name}-{info.Version}");
        string? backupDir = null;

        // 1) 备份已有 mods（避免两套整合包互相污染）
        var modsDir = Path.Combine(root, "mods");
        if (backupMods && Directory.Exists(modsDir) && Directory.EnumerateFileSystemEntries(modsDir).Any())
        {
            backupDir = Path.Combine(root, "mods.bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            progress?.Report(("正在备份现有 mods 文件夹…", 0.01));
            await Task.Run(() => CopyDirectory(modsDir, backupDir), ct);
        }

        // 2) 下载 files[]（只下客户端需要的）
        var clientFiles = info.Files.Where(f => f.ClientEnv != "unsupported").ToList();
        long totalBytes = clientFiles.Sum(f => f.Size > 0 ? f.Size : 8L * 1024 * 1024);
        long done = 0;
        var installed = new List<string>();

        foreach (var f in clientFiles)
        {
            ct.ThrowIfCancellationRequested();
            var rel = SafeRelative(f.Path);
            var dest = Path.GetFullPath(Path.Combine(root, rel));
            if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("整合包内包含非法路径：" + f.Path);

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            if (f.Url != null)
            {
                await DownloadFileAsync(f.Url, dest, (read, _) =>
                {
                    var pct = totalBytes > 0 ? Math.Min(0.98, (done + read) / (double)totalBytes) : 0.5;
                    progress?.Report(($"正在下载 {Path.GetFileName(dest)}（{installed.Count + 1}/{clientFiles.Count}）", pct));
                }, ct);

                // 校验哈希（有就校验，失败重下一次）
                if (!await VerifyAsync(dest, f.Sha512, f.Sha1))
                {
                    progress?.Report(($"文件校验失败，重新下载 {Path.GetFileName(dest)}", 0.5));
                    try { File.Delete(dest); } catch { }
                    await DownloadFileAsync(f.Url, dest, null, ct);
                }
            }
            else if (!File.Exists(dest))
            {
                // 没有下载地址（如 CurseForge 包）：留空，最后统一提示
            }

            done += f.Size > 0 ? f.Size : 0;
            installed.Add(rel);
            progress?.Report(($"已安装 {Path.GetFileName(dest)}（{installed.Count}/{clientFiles.Count}）",
                totalBytes > 0 ? Math.Min(0.99, done / (double)totalBytes) : 0.5));
        }

        // 3) 解压 overrides / client-overrides
        progress?.Report(("正在解压 overrides（配置、资源包、模组配置）…", 0.99));
        using (var fs = File.OpenRead(info.SourcePath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            foreach (var e in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var n = e.FullName.Replace('\\', '/');
                if (n.EndsWith('/')) continue;

                string? rel = null;
                if (n.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)) rel = n["overrides/".Length..];
                else if (n.StartsWith("client-overrides/", StringComparison.OrdinalIgnoreCase)) rel = n["client-overrides/".Length..];
                if (string.IsNullOrWhiteSpace(rel)) continue;

                var dest = Path.GetFullPath(Path.Combine(root, SafeRelative(rel)));
                if (!dest.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await Task.Run(() =>
                {
                    using var src = e.Open();
                    using var outFs = File.Create(dest);
                    src.CopyTo(outFs);
                }, ct);
            }
        }

        // 4) 写安装记录
        var record = new ModpackRecord(id, info.Name, info.Version, info.McVersion, info.LoaderId,
            info.LoaderVersion, info.Format, installed, backupDir, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        var dir = Path.Combine(InstancesRoot(root), id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "pack.json"),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), ct);

        progress?.Report(("安装完成", 1.0));
        return record;
    }

    /// <summary>列出已安装的整合包。</summary>
    public static List<ModpackRecord> ListInstalled(string gameDir)
    {
        var list = new List<ModpackRecord>();
        try
        {
            var root = InstancesRoot(gameDir);
            if (!Directory.Exists(root)) return list;
            foreach (var dir in Directory.GetDirectories(root))
            {
                var file = Path.Combine(dir, "pack.json");
                if (!File.Exists(file)) continue;
                try
                {
                    var rec = JsonSerializer.Deserialize<ModpackRecord>(File.ReadAllText(file));
                    if (rec != null) list.Add(rec);
                }
                catch { /* 单条记录损坏忽略 */ }
            }
        }
        catch { }
        return list.OrderByDescending(r => r.InstalledAt).ToList();
    }

    /// <summary>卸载整合包：删除它安装的文件与记录；restoreBackup 时把备份的 mods 还原。</summary>
    public static void Uninstall(string gameDir, ModpackRecord record, bool restoreBackup)
    {
        var root = Path.GetFullPath(gameDir);
        foreach (var rel in record.Files)
        {
            try
            {
                var full = Path.GetFullPath(Path.Combine(root, rel));
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(full)) File.Delete(full);
            }
            catch { }
        }

        if (restoreBackup && !string.IsNullOrEmpty(record.BackupDir) && Directory.Exists(record.BackupDir))
        {
            var mods = Path.Combine(root, "mods");
            try
            {
                if (Directory.Exists(mods)) Directory.Delete(mods, true);
                CopyDirectory(record.BackupDir, mods);
                Directory.Delete(record.BackupDir, true);
            }
            catch { }
        }

        try
        {
            var dir = Path.Combine(InstancesRoot(root), record.Id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch { }
    }

    // ---- 辅助 ----

    private static string SafeRelative(string rel)
    {
        var r = rel.Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
        if (r.Contains("..")) throw new InvalidOperationException("整合包内包含非法路径：" + rel);
        return r;
    }

    private static string Slug(string s)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.') sb.Append(c);
            else if (c is ' ' or '/') sb.Append('-');
        }
        var outp = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "-{2,}", "-").Trim('-', '.');
        return string.IsNullOrWhiteSpace(outp) ? "pack-" + DateTime.Now.Ticks : outp;
    }

    private static async Task DownloadFileAsync(string url, string dest, Action<long, long>? onProgress, CancellationToken ct)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength ?? 0;
                await using var net = await resp.Content.ReadAsStreamAsync(ct);
                await using var fs = File.Create(dest);
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await net.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    onProgress?.Invoke(read, total);
                }
                return;
            }
            catch (Exception ex) when (attempt < 2 && ex is not OperationCanceledException)
            {
                last = ex;
                await Task.Delay(800 * (attempt + 1), ct);
            }
        }
        throw last ?? new IOException("下载失败：" + url);
    }

    private static async Task<bool> VerifyAsync(string file, string? sha512, string? sha1)
    {
        try
        {
            if (string.IsNullOrEmpty(sha512) && string.IsNullOrEmpty(sha1)) return true;
            await using var fs = File.OpenRead(file);
            if (!string.IsNullOrEmpty(sha512))
            {
                var hash = await SHA512.HashDataAsync(fs);
                return Convert.ToHexString(hash).Equals(sha512, StringComparison.OrdinalIgnoreCase);
            }
            fs.Position = 0;
            var h1 = await SHA1.HashDataAsync(fs);
            return Convert.ToHexString(h1).Equals(sha1, StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(src, dest));
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(src, dest), true);
    }
}
