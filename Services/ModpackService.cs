using System.Diagnostics;
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
    string InstalledAt,
    string? VersionId = null,   // 整合包对应的独立游戏版本 ID（PCL2 式：versions/&lt;id&gt;/）
    bool Isolated = true);      // 该版本是否强制版本隔离（整合包一律隔离）

/// <summary>
/// 整合包安装：支持 Modrinth 的 .mrpack 与 CurseForge 的 .zip。
/// 与 PCL2 一致：整合包装成**一个独立的游戏版本**——
///   versions/&lt;整合包名&gt;/&lt;整合包名&gt;.json（inheritsFrom 基底版本）
///   versions/&lt;整合包名&gt;/mods、config、resourcepacks…（全部内容都在这个版本目录里）
/// 这样不会动到玩家已有的 mods / 配置，装完在版本列表里选中它就能直接开玩。
/// 安装记录写在 nex-modpacks/&lt;id&gt;/pack.json，卸载时整目录删除。
/// </summary>
public sealed class ModpackService
{
    /// <summary>版本目录里的标记文件：证明这个版本是整合包实例（启动时强制版本隔离）。</summary>
    public const string MarkerFileName = ".nex-modpack";

    private static readonly HttpClient Http = MirrorHttp.CreateClient(TimeSpan.FromMinutes(30));

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
    /// 安装整合包：在 versions/ 下新建一个独立版本 → 下载 files[] → 解压 overrides → 写记录。
    /// progress 回调：(阶段文字, 0~1 进度)。
    /// </summary>
    /// <param name="baseVersionId">基底版本（原版或 Fabric/Forge 加载器版本），新版本 inheritsFrom 它。</param>
    /// <param name="versionName">用户可见的版本名，为空时用「包名 版本」。</param>
    public static async Task<ModpackRecord> InstallAsync(
        ModpackInfo info, string gameDir, string? baseVersionId,
        string? versionName = null,
        IProgress<(string Message, double Progress)>? progress = null,
        CancellationToken ct = default)
    {
        var root = Path.GetFullPath(gameDir);
        Directory.CreateDirectory(root);

        var baseId = (baseVersionId ?? "").Trim();
        if (baseId.Length == 0) baseId = (info.McVersion ?? "").Trim();
        if (baseId.Length == 0)
            throw new InvalidOperationException("请先选择一个基底游戏版本（整合包要挂在这个版本上才能启动）。");

        var versionsDir = Path.Combine(root, "versions");
        Directory.CreateDirectory(versionsDir);

        // 1) 新版本目录 + 版本 json（PCL2 同款：整合包 = 一个独立版本实例）
        var wanted = string.IsNullOrWhiteSpace(versionName) ? $"{info.Name} {info.Version}" : versionName.Trim();
        var versionId = UniqueVersionId(versionsDir, Slug(wanted));
        var versionDir = Path.Combine(versionsDir, versionId);
        Directory.CreateDirectory(versionDir);
        progress?.Report(($"正在创建独立版本「{versionId}」…", 0.01));

        // 版本 json 只写继承关系，其余（库、主类、参数）由基底版本提供，CmlLib 会自动合并
        WriteVersionJson(versionDir, versionId, baseId);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(versionDir, MarkerFileName),
                $"name={info.Name}\nversion={info.Version}\nbase={baseId}\ninstalled={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n", ct);
        }
        catch { /* 标记失败不影响安装 */ }

        // 2) 下载 files[]（只下客户端需要的），并行下载全部落到版本目录里
        var clientFiles = info.Files.Where(f => f.ClientEnv != "unsupported").ToList();
        long totalBytes = clientFiles.Sum(f => f.Size > 0 ? f.Size : 8L * 1024 * 1024);
        long done = 0;
        var completedCount = 0;
        var installed = new List<string>();
        var failures = new List<string>();
        var reportLock = new object();

        // 预解析目标路径（非法路径直接报错）
        var plan = new List<(ModpackFile File, string Dest, string Rel)>();
        foreach (var f in clientFiles)
        {
            var rel = SafeRelative(f.Path);
            var dest = Path.GetFullPath(Path.Combine(versionDir, rel));
            if (!dest.StartsWith(versionDir, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("整合包内包含非法路径：" + f.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            plan.Add((f, dest, rel));
        }

        async Task DownloadOneAsync((ModpackFile File, string Dest, string Rel) item)
        {
            ct.ThrowIfCancellationRequested();
            var (f, dest, rel) = item;
            var fileName = Path.GetFileName(dest);

            if (f.Url != null)
            {
                // 候选源：镜像开启 → 镜像优先、官方兜底；关闭则相反
                var candidates = MirrorHttp.Candidates(f.Url);
                Exception? lastErr = null;
                var ok = false;

                // 第 1 轮用首选源，失败后第 2 轮换备用源（共两次机会）
                for (var attempt = 0; attempt < 2 && !ok; attempt++)
                {
                    var url = candidates[Math.Min(attempt, candidates.Count - 1)];
                    try
                    {
                        await DownloadFileAsync(url, dest, (read, _) =>
                        {
                            var baseDone = Interlocked.Read(ref done);
                            var pct = totalBytes > 0
                                ? Math.Min(0.98, (baseDone + read) / (double)totalBytes)
                                : 0.5;
                            progress?.Report(($"正在下载 {fileName}（{completedCount + 1}/{clientFiles.Count}）", pct));
                        }, ct);
                        ok = true;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { lastErr = ex; }
                }

                if (!ok)
                {
                    lock (reportLock) failures.Add($"{fileName}：{lastErr?.Message}");
                    return;
                }

                // 校验哈希（有就校验，失败换源重下一次）
                if (!await VerifyAsync(dest, f.Sha512, f.Sha1))
                {
                    progress?.Report(($"文件校验失败，换源重新下载 {fileName}", 0.5));
                    try { File.Delete(dest); } catch { }
                    try
                    {
                        await DownloadFileAsync(candidates[^1], dest, null, ct);
                        if (!await VerifyAsync(dest, f.Sha512, f.Sha1))
                            lock (reportLock) failures.Add($"{fileName}：哈希校验失败");
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lock (reportLock) failures.Add($"{fileName}：重下失败（{ex.Message}）");
                    }
                }
            }
            else if (!File.Exists(dest))
            {
                // 没有下载地址（如 CurseForge 包）：留空，最后统一提示
                return;
            }

            if (f.Size > 0) Interlocked.Add(ref done, f.Size);
            var cnt = Interlocked.Increment(ref completedCount);
            lock (reportLock) installed.Add(rel);
            progress?.Report(($"已安装 {fileName}（{cnt}/{clientFiles.Count}）",
                totalBytes > 0 ? Math.Min(0.99, Interlocked.Read(ref done) / (double)totalBytes) : 0.5));
        }

        // 并行下载：同时 6 个文件，远快于逐个串行；单个失败不中断整体
        const int MaxParallel = 6;
        var cancelled = false;
        using (var gate = new SemaphoreSlim(MaxParallel, MaxParallel))
        {
            var tasks = plan.Select(async item =>
            {
                try { await gate.WaitAsync(ct); }
                catch (OperationCanceledException) { cancelled = true; return; }
                try { await DownloadOneAsync(item); }
                catch (OperationCanceledException) { cancelled = true; }
                catch (Exception ex)
                {
                    lock (reportLock) failures.Add($"{Path.GetFileName(item.Dest)}：{ex.Message}");
                }
                finally { gate.Release(); }
            });
            await Task.WhenAll(tasks);
        }
        if (cancelled || ct.IsCancellationRequested) ct.ThrowIfCancellationRequested();

        if (failures.Count > 0)
            throw new InvalidOperationException(
                $"有 {failures.Count} 个文件下载失败（已安装 {installed.Count}/{clientFiles.Count}）：\n"
                + string.Join("\n", failures.Take(8))
                + (failures.Count > 8 ? $"\n… 等共 {failures.Count} 个" : "")
                + "\n\n可尝试：1) 设置里关闭/开启「下载镜像加速」后重试；2) 检查网络后重试。已下载的文件会保留。");

        progress?.Report(($"全部文件下载完成（{installed.Count}/{clientFiles.Count}）", 0.98));

        // 3) 解压 overrides / client-overrides 到版本目录
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

                var dest = Path.GetFullPath(Path.Combine(versionDir, SafeRelative(rel)));
                if (!dest.StartsWith(versionDir, StringComparison.OrdinalIgnoreCase)) continue;

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
        // 记录 ID 直接用版本 ID：版本名已去重，保证多条整合包记录不会互相覆盖
        var record = new ModpackRecord(Slug(versionId), info.Name, info.Version,
            info.McVersion, info.LoaderId, info.LoaderVersion, info.Format, installed, null,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), versionId, true);
        var dir = Path.Combine(InstancesRoot(root), record.Id);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "pack.json"),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), ct);

        progress?.Report(($"安装完成：新版本「{versionId}」已加入版本列表", 1.0));
        return record;
    }

    /// <summary>版本名去重：已存在就加 -2 / -3…</summary>
    private static string UniqueVersionId(string versionsDir, string baseId)
    {
        var id = baseId;
        var i = 2;
        while (Directory.Exists(Path.Combine(versionsDir, id)) || File.Exists(Path.Combine(versionsDir, id + ".json")))
            id = $"{baseId}-{i++}";
        return id;
    }

    /// <summary>写整合包版本 json：只声明继承关系，其余由基底版本合并提供。</summary>
    private static void WriteVersionJson(string versionDir, string versionId, string inheritsFrom)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        var json = new Dictionary<string, object?>
        {
            ["id"] = versionId,
            ["inheritsFrom"] = inheritsFrom,
            ["type"] = "release",
            ["time"] = now,
            ["releaseTime"] = now
        };
        File.WriteAllText(Path.Combine(versionDir, versionId + ".json"),
            JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>判断某个版本是不是整合包实例（启动时必须版本隔离）。</summary>
    public static bool IsModpackVersion(string gameDir, string? versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return false;
        try
        {
            var dir = Path.Combine(Path.GetFullPath(gameDir), "versions", versionId!);
            return File.Exists(Path.Combine(dir, MarkerFileName));
        }
        catch { return false; }
    }

    /// <summary>
    /// 把旧式安装（文件散在游戏根目录）的整合包迁移成一个独立版本：
    /// 新建 versions/&lt;名字&gt;/，把记录里的文件从根目录搬进去，根目录恢复干净。
    /// 不重新下载任何东西，也不动别的版本。
    /// </summary>
    public static async Task<ModpackRecord> MigrateLegacyAsync(string gameDir, ModpackRecord legacy,
        string? versionName = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrEmpty(legacy.VersionId)) return legacy;

        var root = Path.GetFullPath(gameDir);
        var versionsDir = Path.Combine(root, "versions");
        Directory.CreateDirectory(versionsDir);

        var baseId = PickBaseFor(root, legacy);
        if (string.IsNullOrWhiteSpace(baseId))
            throw new InvalidOperationException("本机找不到可用的基底版本，先在「游戏版本」页装一个再迁移。");

        var wanted = string.IsNullOrWhiteSpace(versionName) ? $"{legacy.Name} {legacy.Version}" : versionName.Trim();
        var versionId = UniqueVersionId(versionsDir, Slug(wanted));
        var versionDir = Path.Combine(versionsDir, versionId);
        Directory.CreateDirectory(versionDir);

        WriteVersionJson(versionDir, versionId, baseId);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(versionDir, MarkerFileName),
                $"name={legacy.Name}\nversion={legacy.Version}\nbase={baseId}\nmigrated={DateTime.Now:yyyy-MM-dd HH:mm:ss}\n", ct);
        }
        catch { }

        // 搬家：记录里的相对路径 → 版本目录（同结构）；源文件不存在的跳过
        foreach (var rel in legacy.Files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var src = Path.GetFullPath(Path.Combine(root, rel));
                if (!src.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(src)) continue;
                var dst = Path.GetFullPath(Path.Combine(versionDir, rel));
                if (!dst.StartsWith(versionDir, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                if (File.Exists(dst)) continue;
                File.Move(src, dst);
            }
            catch { /* 单个文件被占用就跳过，别中断整体迁移 */ }
        }

        var record = new ModpackRecord(Slug(versionId), legacy.Name, legacy.Version, legacy.McVersion,
            legacy.LoaderId, legacy.LoaderVersion, legacy.Format, legacy.Files, null,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"), versionId, true);

        var oldDir = Path.Combine(InstancesRoot(root), legacy.Id);
        var newDir = Path.Combine(InstancesRoot(root), record.Id);
        Directory.CreateDirectory(newDir);
        await File.WriteAllTextAsync(Path.Combine(newDir, "pack.json"),
            JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true }), ct);
        if (!string.Equals(oldDir, newDir, StringComparison.OrdinalIgnoreCase))
        {
            try { if (Directory.Exists(oldDir)) Directory.Delete(oldDir, true); } catch { }
        }
        return record;
    }

    /// <summary>给旧记录挑一个基底版本：优先「同加载器 + 同 MC 版本」，其次同 MC 版本。</summary>
    private static string? PickBaseFor(string root, ModpackRecord legacy)
    {
        var dir = Path.Combine(root, "versions");
        if (!Directory.Exists(dir)) return null;
        var names = Directory.GetDirectories(dir)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n) && File.Exists(Path.Combine(dir, n!, n + ".json")))
            .Select(n => n!)
            .ToList();
        if (!string.IsNullOrEmpty(legacy.LoaderId) && !string.IsNullOrEmpty(legacy.McVersion))
        {
            var best = names.FirstOrDefault(n => n.Contains(legacy.LoaderId, StringComparison.OrdinalIgnoreCase)
                                                 && n.Contains(legacy.McVersion, StringComparison.OrdinalIgnoreCase));
            if (best != null) return best;
        }
        if (!string.IsNullOrEmpty(legacy.McVersion))
        {
            var best = names.FirstOrDefault(n => n.Equals(legacy.McVersion, StringComparison.OrdinalIgnoreCase))
                       ?? names.FirstOrDefault(n => n.Contains(legacy.McVersion, StringComparison.OrdinalIgnoreCase));
            if (best != null) return best;
        }
        return names.FirstOrDefault();
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
                    if (rec == null) continue;
                    // 版本已在「游戏版本」页被删掉的记录自动忽略（不再显示幽灵条目）
                    if (!string.IsNullOrEmpty(rec.VersionId)
                        && !Directory.Exists(Path.Combine(gameDir, "versions", rec.VersionId!)))
                        continue;
                    list.Add(rec);
                }
                catch { /* 单条记录损坏忽略 */ }
            }
        }
        catch { }
        return list.OrderByDescending(r => r.InstalledAt).ToList();
    }

    /// <summary>卸载整合包：整个独立版本目录（versions/&lt;id&gt;）一并删除，不动其他版本。</summary>
    public static void Uninstall(string gameDir, ModpackRecord record, bool restoreBackup)
    {
        var root = Path.GetFullPath(gameDir);

        // 新版安装：整合包就是一个独立版本，整目录删掉即可（不碰别人的 mods/配置）
        if (!string.IsNullOrWhiteSpace(record.VersionId))
        {
            try
            {
                var vdir = Path.GetFullPath(Path.Combine(root, "versions", record.VersionId!));
                if (vdir.StartsWith(Path.Combine(root, "versions"), StringComparison.OrdinalIgnoreCase)
                    && Directory.Exists(vdir))
                    Directory.Delete(vdir, true);
            }
            catch { }
        }
        else
        {
            // 旧版安装（文件散落在根目录）：按记录逐个删
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

    /// <summary>超过这个大小就走分段并发：国内直连 Modrinth CDN 对单连接限速（实测 ~30KB/s），
    /// 多连接叠加能到 4 倍以上；小文件分段反而多花握手时间，不值得。</summary>
    private const long SegmentThreshold = 1024 * 1024;

    /// <summary>分段下载的最多并发连接数（实测单连接被限速 ~30KB/s，8 路能跑到 300KB/s）。</summary>
    private const int SegmentParallel = 8;

    /// <summary>每段最小 256KB，段太小会让请求数膨胀。</summary>
    private const long MinSegmentSize = 256 * 1024;

    private static async Task DownloadFileAsync(string url, string dest, Action<long, long>? onProgress, CancellationToken ct)
    {
        // 先试分段并发；服务器不支持 Range / 文件太小时返回 false，回落到整文件单连接
        try
        {
            if (await TrySegmentDownloadAsync(url, dest, onProgress, ct))
                return;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* 分段失败（含断点不兼容）继续走整文件下载 */ }

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
                // 1MB 缓冲：小文件多的大批量下载场景下比默认 80K 快数倍
                var buffer = new byte[1024 * 1024];
                long read = 0;
                var lastReport = Stopwatch.GetTimestamp();
                int n;
                while ((n = await net.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    // 至少间隔 100ms 才回报一次进度，避免 6 个并行任务刷爆 UI 消息队列
                    var now = Stopwatch.GetTimestamp();
                    if (now - lastReport > Stopwatch.Frequency / 10)
                    {
                        lastReport = now;
                        onProgress?.Invoke(read, total);
                    }
                }
                onProgress?.Invoke(read, total);
                return;
            }
            catch (Exception ex) when (attempt < 2 && ex is not OperationCanceledException)
            {
                last = ex;
                await Task.Delay(500 * (attempt + 1), ct);
            }
        }
        throw last ?? new IOException("下载失败：" + url);
    }

    /// <summary>
    /// 分段并发下载：用 HTTP Range 把文件切成若干段并行拉取，直接按偏移写进同一个文件。
    /// 返回 false 表示这个文件/服务器不适用分段（不支持 Range、长度未知、文件太小），调用方回落到整文件下载。
    /// </summary>
    private static async Task<bool> TrySegmentDownloadAsync(string url, string dest,
        Action<long, long>? onProgress, CancellationToken ct)
    {
        long total;
        using (var probe = new HttpRequestMessage(HttpMethod.Get, url))
        {
            probe.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var resp = await Http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct);
            // 服务器不支持 Range 会返回 200 整包，这里直接放弃分段
            if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent) return false;
            var cr = resp.Content.Headers.ContentRange;
            if (cr?.Length is not long len || len <= SegmentThreshold) return false;
            total = len;
        }

        var segCount = (int)Math.Min(SegmentParallel, Math.Max(0, (total + MinSegmentSize - 1) / MinSegmentSize));
        if (segCount < 2) return false;
        var segSize = (total + segCount - 1) / segCount;

        long done = 0;
        var lastReport = Stopwatch.GetTimestamp();

        await using var output = new FileStream(dest, FileMode.Create, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.Asynchronous);
        output.SetLength(total);      // 先占位，各段按偏移写入互不干扰
        var handle = output.SafeFileHandle;

        using var gate = new SemaphoreSlim(SegmentParallel, SegmentParallel);
        var tasks = new List<Task>(segCount);
        for (var i = 0; i < segCount; i++)
        {
            var from = i * segSize;
            var to = Math.Min(total - 1, from + segSize - 1);
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
                    using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    // 必须真的是 206：个别 CDN 会忽略 Range 直接回 200 整包，
                    // 那样按偏移写会把文件写坏（越界、内容错位），这里立刻放弃交给整文件下载
                    if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                        throw new IOException("分段下载被服务器拒绝（" + (int)resp.StatusCode + "）");
                    await using var net = await resp.Content.ReadAsStreamAsync(ct);
                    var buffer = new byte[512 * 1024];
                    var offset = from;
                    var limit = to - from + 1;          // 本段字节数，严格截断
                    var written = 0L;
                    int n;
                    while (written < limit && (n = await net.ReadAsync(buffer, ct)) > 0)
                    {
                        if (written + n > limit) n = (int)(limit - written);
                        await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, n), offset, ct);
                        offset += n;
                        written += n;
                        var now = Interlocked.Add(ref done, n);
                        var tick = Stopwatch.GetTimestamp();
                        if (tick - lastReport > Stopwatch.Frequency / 10)
                        {
                            lastReport = tick;
                            onProgress?.Invoke(now, total);
                        }
                    }
                    if (written < limit) throw new IOException("分段下载不完整");
                }
                finally { gate.Release(); }
            }, ct));
        }
        await Task.WhenAll(tasks);

        if (new FileInfo(dest).Length != total) return false;   // 长度不符，交给整文件下载兜底
        onProgress?.Invoke(total, total);
        return true;
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
