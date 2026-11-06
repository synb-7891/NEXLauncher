using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using NEXLauncher.Services;

namespace NEXLauncher;

/// <summary>
/// 新版界面的真实能力桥：界面里每一个按钮最终都落到这里，调真实的服务 / 真实文件系统 / 真实进程。
/// 没有「演示」分支——做不到的事会如实返回失败原因，绝不假装成功。
/// </summary>
public partial class MainWindow
{
    private static readonly HttpClient _toolHttp = new() { Timeout = TimeSpan.FromMinutes(30) };

    /// <summary>
    /// 页面为每个请求生成的唯一回执号（nexTool 的 rid）。
    /// 以前回复里只有 op（操作名），页面端快速连点同一功能时无法区分是哪一次请求的回复，
    /// 导致除最后一次外的所有请求永远等不到结果、180 秒后集体报"操作超时"。
    /// 回复时原样带回 rid，页面即可精确配对。AsyncLocal 会随 await/Task.Run 流动，不用改几十个调用点。
    /// </summary>
    private static readonly System.Threading.AsyncLocal<string?> ToolRid = new();

    private void HandleWebTool(JsonElement root)
    {
        var op = root.TryGetProperty("op", out var o) ? (o.GetString() ?? "") : "";
        ToolRid.Value = root.TryGetProperty("rid", out var ridEl) ? ridEl.GetString() : null;
        ToolLog("recv op=" + op + (ToolRid.Value != null ? " rid=" + ToolRid.Value : ""));
        if (string.IsNullOrWhiteSpace(op)) return;
        _ = Task.Run(async () =>
        {
            try { await RunToolAsync(op, root); }
            catch (Exception ex) { ToolLog("EXC op=" + op + " " + ex.GetType().Name + ": " + ex.Message); ToolReply(op, false, ex.Message); }
        });
    }

    private static readonly string ToolLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXLauncher", "tool-debug.log");

    private static void ToolLog(string line)
    {
        try { File.AppendAllText(ToolLogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {line}\n"); } catch { }
    }

    private void ToolReply(string op, bool ok, string? msg = null, object? data = null)
    {
        ToolLog($"reply op={op} ok={ok} msg={(msg ?? "").Replace("\n", " ")}");
        PostJson(new { type = "tool", op, rid = ToolRid.Value, ok, msg = msg ?? "", data });
    }

    private void ToolStage(string op, string msg) => PostJson(new { type = "tool", op, rid = ToolRid.Value, ok = true, stage = msg });

    private string ModuleDir => ModsDirFor(CurrentVersionId());

    private string CurrentVersionId()
    {
        try
        {
            return Dispatcher.Invoke(() => (SideVersionBox.SelectedItem as string))
                   ?? (_settings.LastQuickVersion ?? "");
        }
        catch { return _settings.LastQuickVersion ?? ""; }
    }

    /// <summary>从版本 ID 里解析纯游戏版本号：1.21.11 / fabric-loader-0.20.1-1.21.11 / 1.20.1-forge-47.2.0。</summary>
    private static string McVersionOf(string versionId)
    {
        if (string.IsNullOrWhiteSpace(versionId)) return "";
        var ms = System.Text.RegularExpressions.Regex.Matches(versionId, @"\d+\.\d+(?:\.\d+)?");
        if (ms.Count == 0) return "";
        // 以数字开头的（1.21.11 / 1.20.1-forge-...）取第一段；加载器前缀的（fabric-loader-0.20.1-1.21.11）取最后一段
        return char.IsDigit(versionId.TrimStart()[0]) ? ms[0].Value : ms[^1].Value;
    }

    /// <summary>当前版本对应的 Modrinth 加载器标识（原版返回空串）。</summary>
    private string CurrentLoaderKey()
    {
        var loader = DetectLoaderType(CurrentVersionId()).ToLowerInvariant();
        return loader switch
        {
            "fabric" => "fabric",
            "forge" => "forge",
            "neoforge" => "neoforge",
            "quilt" => "quilt",
            _ => ""
        };
    }

    private async Task RunToolAsync(string op, JsonElement a)
    {
        switch (op)
        {
            /* ---------------- 运行环境 ---------------- */
            case "java-list": JavaList(op); break;
            case "java-use": JavaUse(op, Str(a, "path")); break;
            case "java-open": OpenFolderPath(op, JavaHome()); break;
            case "java-install": await JavaInstallAsync(op, Int(a, "major", 21)); break;

            /* ---------------- 内存 ---------------- */
            case "mem-info": MemInfo(op); break;
            case "mem-optimize": await Task.Run(() => MemOptimize(op)); break;

            /* ---------------- 存档 ---------------- */
            case "saves-list": SavesList(op); break;
            case "saves-backup": await Task.Run(() => SavesBackup(op)); break;
            case "saves-restore": await Task.Run(() => SavesRestore(op, Str(a, "path"))); break;

            /* ---------------- 崩溃报告 / 清理 ---------------- */
            case "crash-list": await Task.Run(() => CrashList(op)); break;
            case "clean-junk": await Task.Run(() => CleanJunk(op)); break;

            /* ---------------- 目录 ---------------- */
            case "open-folder": OpenFolderByKey(op, Str(a, "which")); break;

            /* ---------------- 基岩版 ---------------- */
            case "bedrock-detect": BedrockDetect(op); break;
            case "bedrock-launch": BedrockLaunch(op); break;
            case "bedrock-import": BedrockImport(op); break;

            /* ---------------- 披风 ---------------- */
            case "capes-list": await CapesListAsync(op); break;
            case "cape-set": await CapeSetAsync(op, Str(a, "id")); break;

            /* ---------------- 下载 / 服务器 / 网络 ---------------- */
            case "download-file": await DownloadFileAsync(op); break;
            case "server-status": await ServerStatusAsync(op, Str(a, "host")); break;
            case "lan-info": LanInfo(op); break;
            case "copy": CopyText(op, Str(a, "text")); break;

            /* ---------------- 模组 ---------------- */
            case "mods-list": await Task.Run(() => ModsList(op)); break;
            case "mod-toggle": await Task.Run(() => ModToggle(op, Str(a, "file"))); break;
            case "mod-delete": await Task.Run(() => ModDelete(op, Str(a, "file"))); break;

            /* ---------------- 资源下载：版本选择 / 前置查询 ---------------- */
            case "res-versions": await ResVersionsAsync(op, Str(a, "id"), Str(a, "type"), Bool(a, "all")); break;
            case "res-deps": await Task.Run(() => ResDepsAsync(op, Str(a, "id"), Str(a, "versionId"))); break;
            case "curated": CuratedList(op); break;
            case "curated-install": await CuratedInstallAsync(op, Str(a, "id")); break;

            /* ---------------- 皮肤 ---------------- */
            case "skins-page": await SkinsPageAsync(op, Int(a, "page", 1), Str(a, "kind")); break;
            case "skin-apply": await SkinApplyAsync(op, Str(a, "url"), Bool(a, "slim")); break;
            case "skin-upload": await SkinUploadAsync(op); break;
            case "open-url": OpenUrlSafe(Str(a, "url")); ToolReply(op, true, "已在浏览器打开"); break;

            /* ---------------- 统计 / 公告 ---------------- */
            case "stats": Stats(op); break;
            case "news": await NewsAsync(op); break;
            case "dock": await DockAsync(op); break;

            /* ---------------- 安全 ---------------- */
            case "quarantine-list": QuarantineList(op); break;
            case "quarantine-restore": QuarantineRestore(op, Str(a, "quarantined")); break;
            case "quarantine-delete": QuarantineDelete(op, Str(a, "quarantined")); break;
            case "audit": Audit(op); break;

            /* ---------------- 一键开服 ---------------- */
            case "server-start": await ServerStartAsync(op); break;
            case "server-stop": ServerStop(op); break;
            case "server-state": ServerState(op); break;

            /* ---------------- 设置项（真实读写 settings.json） ---------------- */
            case "settings-get": SettingsGet(op); break;
            case "setting-set": SettingSet(op, Str(a, "key"), Bool(a, "value")); break;

            default:
                ToolReply(op, false, "未知操作：" + op);
                break;
        }
    }

    // ------------------------------------------------------------------ 参数工具

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static int Int(JsonElement e, string name, int def)
        => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : def;

    private static bool Bool(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // ------------------------------------------------------------------ Java

    private void JavaList(string op)
    {
        var list = new JavaService().Detect(_settings.JavaPath);
        var items = list.Select(r => new
        {
            title = r.Title,
            path = r.JavaPath,
            home = r.Home,
            version = r.Version,
            major = r.Major,
            bit = r.Bit,
            current = string.Equals(r.JavaPath, _settings.JavaPath, StringComparison.OrdinalIgnoreCase)
        }).ToList();
        ToolReply(op, true, items.Count == 0 ? "未扫描到 Java，点「下载 Java 21」自动安装" : $"扫描到 {items.Count} 个 Java 运行时",
            new { items, count = items.Count });
    }

    private string JavaHome()
    {
        var list = new JavaService().Detect(_settings.JavaPath);
        var cur = list.FirstOrDefault(r => string.Equals(r.JavaPath, _settings.JavaPath, StringComparison.OrdinalIgnoreCase))
                  ?? list.FirstOrDefault();
        return cur?.Home ?? "";
    }

    private async Task JavaInstallAsync(string op, int major)
    {
        ToolStage(op, $"正在下载 Java {major}…");
        var svc = new JavaService();
        var path = await svc.InstallAsync(major, new Progress<string>(m => ToolStage(op, m)));
        if (!string.IsNullOrWhiteSpace(path))
        {
            _settings.JavaPath = path;
            _settings.Save();
        }
        ToolReply(op, true, $"Java {major} 已安装：{path}", new { path });
    }

    // ------------------------------------------------------------------ 内存

    private ulong TotalPhysicalMb()
    {
        var st = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref st)) return 0;
        return st.ullTotalPhys / 1024 / 1024;
    }

    private int RecommendRam(int totalMb)
    {
        // 与经典界面同一套规则：>=16G 给 6G，8~16G 给一半，<=8G 给 2.5G
        if (totalMb >= 16384) return 6144;
        if (totalMb >= 8192) return (int)(totalMb / 2);
        if (totalMb >= 4096) return 2560;
        return 1536;
    }

    private void MemInfo(string op)
    {
        var total = (int)TotalPhysicalMb();
        var avail = (int)AvailablePhysicalMb();
        ToolReply(op, true, $"物理内存 {total} MB · 可用 {avail} MB",
            new { total, avail, current = _settings.MaxRamMb, recommended = RecommendRam(total) });
    }

    private void MemOptimize(string op)
    {
        var before = AvailablePhysicalMb();
        var done = 0;
        foreach (var p in Process.GetProcesses())
        {
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(0x0100 | 0x0400, false, p.Id);
                if (h == IntPtr.Zero) continue;
                if (EmptyWorkingSet(h)) done++;
            }
            catch { }
            finally
            {
                if (h != IntPtr.Zero) CloseHandle(h);
                p.Dispose();
            }
        }
        var after = AvailablePhysicalMb();
        var total = (int)TotalPhysicalMb();
        var rec = RecommendRam(total);
        _settings.MaxRamMb = rec;
        if (_settings.MinRamMb > rec) _settings.MinRamMb = rec;
        _settings.Save();
        try { Dispatcher.Invoke(PushState); } catch { }
        ToolReply(op, true,
            $"已整理 {done} 个进程，可用内存 {before} → {after} MB（释放 {Math.Max(0, (long)after - (long)before)} MB）；已把最大内存设为推荐的 {rec} MB",
            new { done, before, after, recommended = rec });
    }

    // ------------------------------------------------------------------ 存档

    private void SavesList(string op)
    {
        var svc = new SaveManagerService();
        var worlds = svc.ListWorlds(_gameDirectory).Select(w => new
        {
            name = w.Name,
            path = w.Path,
            size = w.SizeText,
            bytes = w.SizeBytes,
            last = w.LastWrite.ToString("yyyy-MM-dd HH:mm")
        }).ToList();
        var backups = svc.ListBackups(_gameDirectory).Select(b => new
        {
            name = Path.GetFileName(b.Path),
            path = b.Path,
            world = b.WorldName,
            size = b.SizeText,
            created = b.Created.ToString("yyyy-MM-dd HH:mm")
        }).ToList();
        ToolReply(op, true, worlds.Count == 0 ? "还没有存档" : $"共 {worlds.Count} 个存档 · {backups.Count} 个备份",
            new { worlds, backups });
    }

    private void SavesBackup(string op)
    {
        var svc = new SaveManagerService();
        var worlds = svc.ListWorlds(_gameDirectory);
        if (worlds.Count == 0) { ToolReply(op, false, "没有可备份的存档"); return; }
        var ok = 0;
        long bytes = 0;
        var names = new List<string>();
        foreach (var w in worlds)
        {
            try
            {
                var zip = svc.BackupWorld(w.Path, _gameDirectory);
                ok++;
                bytes += new FileInfo(zip).Length;
                names.Add(Path.GetFileName(zip));
            }
            catch (Exception ex) { Debug.WriteLine(ex.Message); }
        }
        ToolReply(op, ok > 0, ok == 0
                ? "备份失败，请检查磁盘空间或权限"
                : $"已备份 {ok} 个存档（{bytes / 1048576.0:0.#} MB）→ {SaveManagerService.BackupsDir(_gameDirectory)}",
            new { count = ok, bytes, files = names, dir = SaveManagerService.BackupsDir(_gameDirectory) });
    }

    private void SavesRestore(string op, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) { ToolReply(op, false, "备份文件不存在"); return; }
        try
        {
            var outPath = new SaveManagerService().RestoreBackup(path, _gameDirectory);
            ToolReply(op, true, "已恢复到：" + outPath, new { path = outPath });
        }
        catch (Exception ex) { ToolReply(op, false, "恢复失败：" + ex.Message); }
    }

    // ------------------------------------------------------------------ 崩溃报告 / 清理

    private void CrashList(string op)
    {
        var reports = new List<object>();
        var dir = Path.Combine(_gameDirectory, "crash-reports");
        if (Directory.Exists(dir))
        {
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.txt")
                         .OrderByDescending(x => x.LastWriteTime).Take(10))
            {
                var head = "";
                try
                {
                    var lines = File.ReadAllLines(f.FullName).Take(80).ToList();
                    var desc = lines.FirstOrDefault(l => l.StartsWith("Description:", StringComparison.OrdinalIgnoreCase));
                    var cause = lines.FirstOrDefault(l => l.Contains("Caused by:", StringComparison.OrdinalIgnoreCase));
                    head = (desc ?? "").Replace("Description:", "").Trim();
                    if (!string.IsNullOrWhiteSpace(cause)) head += " · " + cause.Replace("Caused by:", "").Trim();
                    if (head.Length > 160) head = head[..160];
                }
                catch { }
                reports.Add(new { file = f.Name, time = f.LastWriteTime.ToString("MM-dd HH:mm"), size = $"{f.Length / 1024.0:0.#} KB", reason = head });
            }
        }

        var diagnosis = new List<string>();
        var log = Path.Combine(_gameDirectory, "logs", "latest.log");
        string logTail = "";
        if (File.Exists(log))
        {
            try
            {
                var all = File.ReadAllLines(log);
                logTail = string.Join("\n", all.TakeLast(40));
                var joined = logTail + "\n" + string.Join("\n",
                    reports.Select(r => r.GetType().GetProperty("reason")?.GetValue(r)?.ToString() ?? ""));

                if (joined.Contains("OutOfMemoryError")) diagnosis.Add("内存不足（OutOfMemoryError）→ 建议在设置里调大最大内存，或减少模组数量");
                if (joined.Contains("ModResolutionException") || joined.Contains("Incompatible mod set")) diagnosis.Add("模组依赖冲突 → 检查是否有两个模组要求不同版本的同一前置");
                if (joined.Contains("NoClassDefFoundError")) diagnosis.Add("缺少依赖库 → 通常是模组前置没装或版本不匹配");
                if (joined.Contains("UnsupportedClassVersionError") || joined.Contains("class file version")) diagnosis.Add("Java 版本不匹配 → 1.17+ 需要 Java 17/21，1.16- 需要 Java 8");
                if (joined.Contains("GLFW error") || joined.Contains("Pixel format not accelerated")) diagnosis.Add("显卡驱动/OpenGL 问题 → 更新显卡驱动，或关闭光影");
                if (joined.Contains("DuplicateModsFoundException")) diagnosis.Add("同一个模组装了多份 → 删掉 mods 里重复的 jar");
                if (diagnosis.Count == 0) diagnosis.Add("未匹配到已知崩溃特征，可把 latest.log 发到 QQ 频道求助");
            }
            catch { }
        }
        else diagnosis.Add("没有找到 logs/latest.log，可能还没启动过游戏");

        ToolReply(op, true, reports.Count == 0 ? "没有崩溃报告，日志正常" : $"发现 {reports.Count} 份崩溃报告",
            new { reports, diagnosis, logTail });
    }

    private void CleanJunk(string op)
    {
        long totalBytes = 0;
        var files = 0;
        var dirs = new List<string>();
        var versionsRoot = Path.Combine(_gameDirectory, "versions");
        if (Directory.Exists(versionsRoot))
            foreach (var d in Directory.GetDirectories(versionsRoot)) dirs.Add(d);
        dirs.Add(_gameDirectory);

        foreach (var root in dirs)
        {
            foreach (var sub in new[] { "logs", "crash-reports" })
            {
                var path = Path.Combine(root, sub);
                if (!Directory.Exists(path)) continue;
                foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        var info = new FileInfo(f);
                        totalBytes += info.Length;
                        info.Delete();
                        files++;
                    }
                    catch { }
                }
            }
        }
        ToolReply(op, files > 0, files == 0
                ? "没有需要清理的日志与崩溃报告"
                : $"已清理 {files} 个文件，释放 {totalBytes / 1048576.0:0.#} MB",
            new { files, bytes = totalBytes });
    }

    // ------------------------------------------------------------------ 目录

    private void OpenFolderByKey(string op, string which)
    {
        var skinsDir = Path.Combine(_gameDirectory, "skins");
        var target = which switch
        {
            "mods" => ModuleDir,
            "saves" => Path.Combine(_gameDirectory, "saves"),
            "logs" => Path.Combine(_gameDirectory, "logs"),
            "skins" => skinsDir,
            "backups" => SaveManagerService.BackupsDir(_gameDirectory),
            "crash" => Path.Combine(_gameDirectory, "crash-reports"),
            "resourcepacks" => Path.Combine(_gameDirectory, "resourcepacks"),
            "shaderpacks" => Path.Combine(_gameDirectory, "shaderpacks"),
            "quarantine" => SecurityService.Instance.QuarantineDir,
            _ => _gameDirectory
        };
        OpenFolderPath(op, target);
    }

    private void OpenFolderPath(string op, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path)) { ToolReply(op, false, "目录不存在"); return; }
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{path}\"", UseShellExecute = true });
            ToolReply(op, true, "已打开：" + path, new { path });
        }
        catch (Exception ex) { ToolReply(op, false, "打开失败：" + ex.Message); }
    }

    // ------------------------------------------------------------------ 基岩版

    private static string BedrockPackageDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Packages", "Microsoft.MinecraftUWP_8wekyb3d8bbwe");

    private void BedrockDetect(string op)
    {
        var dir = BedrockPackageDir;
        var installed = Directory.Exists(dir);
        ToolReply(op, true, installed ? "已检测到基岩版" : "未检测到基岩版（需先在微软商店安装）",
            new { installed, dir });
    }

    private void BedrockLaunch(string op)
    {
        if (!Directory.Exists(BedrockPackageDir))
        {
            ToolReply(op, false, "没有检测到基岩版，请先在微软商店搜索 Minecraft 安装");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = "minecraft://", UseShellExecute = true });
            ToolReply(op, true, "已调用 minecraft:// 启动基岩版");
        }
        catch (Exception ex) { ToolReply(op, false, "启动失败：" + ex.Message); }
    }

    private void BedrockImport(string op)
    {
        var files = default(string[]);
        Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择要导入基岩版的文件",
                Filter = "基岩版文件 (*.mcworld;*.mcaddon;*.mcpack;*.mctemplate)|*.mcworld;*.mcaddon;*.mcpack;*.mctemplate|所有文件|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog(this) == true) files = dlg.FileNames;
        });
        if (files == null || files.Length == 0) { ToolReply(op, false, "已取消"); return; }
        var ok = 0;
        foreach (var f in files)
        {
            try { Process.Start(new ProcessStartInfo { FileName = f, UseShellExecute = true }); ok++; }
            catch { }
        }
        ToolReply(op, ok > 0, ok == 0 ? "系统无法打开这些文件，请先运行一次基岩版" : $"已把 {ok} 个文件交给基岩版导入", new { ok });
    }

    // ------------------------------------------------------------------ 披风（真实 Mojang 接口）

    private async Task CapesListAsync(string op)
    {
        var token = _session?.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            ToolReply(op, false, "披风是微软正版账号专属功能，请先在「账号」页用微软正版登录");
            return;
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await _toolHttp.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                ToolReply(op, false, $"Mojang 返回 {(int)resp.StatusCode}：登录可能已过期，请重新登录");
                return;
            }
            using var doc = JsonDocument.Parse(body);
            var capes = new List<object>();
            if (doc.RootElement.TryGetProperty("capes", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in arr.EnumerateArray())
                {
                    capes.Add(new
                    {
                        id = c.TryGetProperty("id", out var i) ? i.GetString() : "",
                        alias = c.TryGetProperty("alias", out var al) ? al.GetString() : "",
                        state = c.TryGetProperty("state", out var st) ? st.GetString() : "",
                        url = c.TryGetProperty("url", out var u) ? u.GetString() : ""
                    });
                }
            }
            ToolReply(op, true, capes.Count == 0 ? "这个账号没有可用的披风" : $"共 {capes.Count} 个披风", new { capes });
        }
        catch (Exception ex) { ToolReply(op, false, "获取披风失败：" + ex.Message); }
    }

    private async Task CapeSetAsync(string op, string id)
    {
        var token = _session?.AccessToken;
        if (string.IsNullOrWhiteSpace(token)) { ToolReply(op, false, "需要微软正版登录"); return; }
        try
        {
            using var req = string.IsNullOrWhiteSpace(id) || id == "none"
                ? new HttpRequestMessage(HttpMethod.Delete, "https://api.minecraftservices.com/minecraft/profile/capes/active")
                : new HttpRequestMessage(HttpMethod.Put, "https://api.minecraftservices.com/minecraft/profile/capes/active")
                { Content = new StringContent(JsonSerializer.Serialize(new { capeId = id }), Encoding.UTF8, "application/json") };
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = await _toolHttp.SendAsync(req);
            if (resp.IsSuccessStatusCode)
                ToolReply(op, true, string.IsNullOrWhiteSpace(id) || id == "none" ? "已关闭披风显示" : "披风已切换");
            else
                ToolReply(op, false, $"设置失败（HTTP {(int)resp.StatusCode}），披风需在官方启动器或 minecraft.net 拥有");
        }
        catch (Exception ex) { ToolReply(op, false, "设置失败：" + ex.Message); }
    }

    // ------------------------------------------------------------------ 下载 / 服务器 / 网络

    private async Task DownloadFileAsync(string op)
    {
        var start = await Dispatcher.InvokeAsync(() =>
        {
            var dlg = new DirectDownloadDialog { Owner = this };
            return dlg.ShowDialog() == true ? (dlg.Url, dlg.SavePath) : (null, null);
        });
        var url = start.Item1; var dest = start.Item2;
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(dest)) { ToolReply(op, false, "已取消"); return; }
        await DownloadToAsync(op, url, dest);
    }

    private async Task DownloadToAsync(string op, string url, string dest)
    {
        try
        {
            ToolStage(op, "正在连接…");
            using var resp = await _toolHttp.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            await using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None);
            await using var stream = await resp.Content.ReadAsStreamAsync();
            var buffer = new byte[81920];
            long received = 0;
            int read, lastPct = -1;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read));
                received += read;
                if (total > 0)
                {
                    var pct = (int)(received * 100 / total);
                    if (pct != lastPct && pct % 5 == 0) { lastPct = pct; ToolStage(op, $"已下载 {pct}%（{received / 1048576.0:0.#}/{total / 1048576.0:0.#} MB）"); }
                }
            }
            ToolReply(op, true, $"下载完成：{dest}", new { path = dest, bytes = received });
        }
        catch (Exception ex) { ToolReply(op, false, "下载失败：" + ex.Message); }
    }

    private async Task ServerStatusAsync(string op, string host)
    {
        if (string.IsNullOrWhiteSpace(host)) { ToolReply(op, false, "请输入服务器地址"); return; }
        host = host.Trim();
        ToolStage(op, $"正在直连 {host} 查询状态…");

        // 直接用 Minecraft Server List Ping 协议连服务器，比第三方接口更准也更快
        var r = await ServerPing.QueryAsync(host, 6000);
        if (!r.Online)
        {
            ToolReply(op, false, $"{host} 连不上：{r.Error}\n（服务器没开、端口写错，或对方防火墙拦了）", new { host, online = false });
            return;
        }
        ToolReply(op, true, $"{host} 在线 · {r.Players}/{r.Max} 人 · {r.Version}",
            new { host, online = true, players = r.Players, max = r.Max, version = r.Version, motd = r.Motd });
    }

    private void LanInfo(string op)
    {
        try
        {
            var ips = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(u => u.Address.ToString())
                .Where(s => !s.StartsWith("169.254"))
                .ToList();
            var ip = ips.FirstOrDefault() ?? "";
            if (string.IsNullOrEmpty(ip)) { ToolReply(op, false, "没有找到局域网 IP，请检查网络连接"); return; }
            var text = $"{ip}:25565";
            ClipboardSet(text);
            ToolReply(op, true, $"局域网地址 {text} 已复制。在游戏里按 ESC →「对局域网开放」，好友用这个地址就能进来", new { ip, port = 25565, text, all = ips });
        }
        catch (Exception ex) { ToolReply(op, false, "获取 IP 失败：" + ex.Message); }
    }

    private void ClipboardSet(string text)
        => Dispatcher.Invoke(() => { try { Clipboard.SetText(text); } catch { } });

    private void CopyText(string op, string text)
    {
        if (string.IsNullOrEmpty(text)) { ToolReply(op, false, "没有可复制的内容"); return; }
        ClipboardSet(text);
        ToolReply(op, true, "已复制到剪贴板", new { text });
    }

    // ------------------------------------------------------------------ 模组

    // ------------------------------------------------------------ 内置精选（Modrinth 上没有的）

    /// <summary>内置精选列表：Xray 矿透、彗星、Wurst、Fabric API（带当前版本适配标记）。</summary>
    private void CuratedList(string op)
    {
        var mc = McVersionOf(CurrentVersionId());
        var loader = CurrentLoaderKey();
        ToolReply(op, true, $"内置精选 {Services.CuratedService.Picks.Count} 项", new
        {
            mc,
            loader,
            items = Services.CuratedService.Picks.Select(p => new
            {
                id = p.Id,
                title = p.Title,
                subtitle = p.Subtitle,
                badge = p.Badge,
                kind = p.Kind,
                loader = p.Loader,
                // 需要 Fabric 的项在没有 Fabric 的版本上标成「需 Fabric」
                fit = p.Loader.Length == 0 || p.Loader.Equals(loader, StringComparison.OrdinalIgnoreCase)
            })
        });
    }

    /// <summary>一键装内置精选：解析官方直链 → 落到 mods / resourcepacks，客户端类自动补 Fabric API。</summary>
    private async Task CuratedInstallAsync(string op, string id)
    {
        var pick = Services.CuratedService.Find(id);
        if (pick == null) { ToolReply(op, false, "未知的内置项目：" + id); return; }

        var mc = McVersionOf(CurrentVersionId());
        var loader = CurrentLoaderKey();
        if (string.IsNullOrWhiteSpace(mc)) { ToolReply(op, false, "先在左上角选一个游戏版本"); return; }
        if (pick.Loader == "fabric" && loader != "fabric")
        {
            ToolReply(op, false,
                $"「{pick.Title}」是 Fabric 模组，当前版本（{CurrentVersionId()}）不是 Fabric。" +
                "先在「下载版本」里装一个 Fabric 版本，或把版本切到 Fabric 再装。");
            return;
        }

        try
        {
            var files = new List<Services.CuratedFile>();
            ToolStage(op, "正在解析官方下载地址…");
            files.AddRange(await Services.CuratedService.ResolveAsync(pick.Id, mc));
            if (files.Count == 0) { ToolReply(op, false, $"没有找到适配 {mc} 的文件"); return; }

            // 彗星 / Wurst 必须带 Fabric API，否则进游戏直接崩
            if (pick.Kind == "mod" && pick.Id is "meteor" or "wurst")
            {
                var modsDir = Path.Combine(_gameDirectory, "mods");
                var hasApi = Directory.Exists(modsDir) &&
                             Directory.GetFiles(modsDir, "*fabric-api*.jar").Length > 0;
                if (!hasApi) files.AddRange(await Services.CuratedService.ResolveAsync("fabricapi", mc));
            }

            var saved = new List<string>();
            foreach (var f in files)
            {
                var dir = pick.Kind == "mod"
                    ? ModsDirFor(CurrentVersionId())
                    : Path.Combine(_gameDirectory, DlSubDir(pick.Kind));
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir, f.FileName);
                if (File.Exists(dest) && new FileInfo(dest).Length > 0)
                {
                    saved.Add(Path.GetFileName(dest) + "（已存在）");
                    continue;
                }
                ToolStage(op, $"正在下载 {f.FileName}…");
                await DownloadQuietAsync(op, f.Url, dest);
                saved.Add(Path.GetFileName(dest));
            }

            ToolReply(op, true,
                $"已装好：{string.Join("、", saved)}。重启游戏生效" +
                (pick.Kind == "resourcepack" ? "；资源包还要在「选项 → 资源包」里点一下启用。" : "。"),
                new { files = saved });
        }
        catch (Exception ex) { ToolReply(op, false, "安装失败：" + ex.Message); }
    }

    /// <summary>只推进度、不单独回复的下载（一次操作下载多个文件时用）。</summary>
    private async Task DownloadQuietAsync(string op, string url, string dest)
    {
        var svc = new Services.ModrinthService();
        var progress = new Progress<(long Received, long Total)>(p =>
        {
            if (p.Total > 0)
            {
                var pct = (int)(p.Received * 100 / p.Total);
                if (pct % 10 == 0)
                    ToolStage(op, $"正在下载 {Path.GetFileName(dest)}… {pct}%（{p.Received / 1048576.0:0.#}/{p.Total / 1048576.0:0.#} MB）");
            }
        });
        try
        {
            await svc.DownloadFileAsync(url, dest, progress);
        }
        catch (Exception)
        {
            // 镜像/代理链路都失败时，绕开系统代理直连原站再试一次
            try { File.Delete(dest); } catch { }
            await Services.CuratedService.DownloadDirectAsync(url, dest, progress);
        }
    }

    /// <summary>
    /// PCL2 式「选择下载版本」：列出项目全部文件版本，并把匹配「当前游戏版本 + 加载器」的排在前面、打上适配标记。
    /// </summary>
    private async Task ResVersionsAsync(string op, string projectId, string type, bool all)
    {
        if (string.IsNullOrWhiteSpace(projectId)) { ToolReply(op, false, "缺少项目 ID"); return; }
        try
        {
            var svc = new Services.ModrinthService();
            var curMc = McVersionOf(CurrentVersionId());
            var curLoader = CurrentLoaderKey();
            var isMod = string.IsNullOrWhiteSpace(type) || type == "mod";

            /* 第一次只拉「适配当前版本」的少量文件（服务端过滤，秒回）；
               用户关掉过滤器时再拉全量（Sodium 这种 200+ 版本的项目全量很慢） */
            List<Services.ModrinthVersion> vers;
            var filtered = false;
            if (!all && !string.IsNullOrEmpty(curMc))
            {
                vers = await svc.GetVersionsFilteredAsync(projectId, curMc, isMod ? curLoader : null);
                filtered = true;
                if (vers.Count == 0)
                {
                    vers = await svc.GetVersionsAsync(projectId);
                    filtered = false;
                }
            }
            else
            {
                vers = await svc.GetVersionsAsync(projectId);
            }

            var rows = vers
                .Where(v => v.Files.Count > 0)
                .Select(v =>
                {
                    var mcOk = string.IsNullOrEmpty(curMc) || v.GameVersions.Contains(curMc);
                    var loaderOk = !isMod || string.IsNullOrEmpty(curLoader) ||
                                   v.Loaders.Any(l => l.Equals(curLoader, StringComparison.OrdinalIgnoreCase));
                    var f = v.PrimaryFile ?? v.Files[0];
                    return new
                    {
                        id = v.Id,
                        versionNumber = v.VersionNumber,
                        name = v.Name,
                        gameVersions = v.GameVersions,
                        loaders = v.Loaders,
                        type = v.VersionType,
                        date = v.DatePublished.LocalDateTime.ToString("yyyy-MM-dd"),
                        sort = v.DatePublished.UtcDateTime,
                        downloads = v.Downloads,
                        fileName = f.FileName,
                        size = f.Size,
                        sizeText = f.Size >= 1048576 ? $"{f.Size / 1048576.0:0.#} MB" : $"{Math.Max(1, f.Size / 1024)} KB",
                        mc = v.GameVersions.FirstOrDefault() ?? "",
                        fit = mcOk && loaderOk
                    };
                })
                .OrderByDescending(x => x.fit)
                .ThenByDescending(x => x.sort)
                .Take(200)
                .Select(x => new
                {
                    x.id, x.versionNumber, x.name, x.gameVersions, x.loaders,
                    x.type, x.date, x.downloads, x.fileName, x.size, x.sizeText, x.mc, x.fit
                })
                .ToList();

            ToolReply(op, true,
                rows.Count == 0 ? "该项目没有可下载的文件" : $"共 {rows.Count} 个文件版本，{rows.Count(r => r.fit)} 个适配 {curMc}",
                new
                {
                    current = curMc,
                    loader = curLoader,
                    filtered,
                    count = rows.Count,
                    fitCount = rows.Count(r => r.fit),
                    versions = rows
                });
        }
        catch (Exception ex)
        {
            ToolReply(op, false, $"获取版本列表失败：{ex.Message}");
        }
    }

    /// <summary>查询项目必需前置（下载前弹窗提醒用）。可按选定的文件版本精确匹配前置的游戏版本。查询失败按无前置处理，不阻塞下载。</summary>
    private async Task ResDepsAsync(string op, string projectId, string? versionId)
    {
        try
        {
            var svc = new Services.ModrinthService();
            string? game = null, loader = null;
            if (!string.IsNullOrWhiteSpace(versionId))
            {
                var v = await svc.GetVersionAsync(projectId, versionId);
                game = v?.GameVersions.FirstOrDefault();
                loader = v?.Loaders.FirstOrDefault(l => !l.Equals("minecraft", StringComparison.OrdinalIgnoreCase));
            }
            else
            {
                game = McVersionOf(CurrentVersionId());
                loader = CurrentLoaderKey();
            }
            var deps = await svc.GetRequiredDependenciesAsync(projectId, versionId, game, loader);
            ToolReply(op, true,
                deps.Count > 0 ? $"检测到 {deps.Count} 个必需前置" : "无前置",
                new
                {
                    count = deps.Count,
                    deps = deps.Select(d => new
                    {
                        projectId = d.ProjectId,
                        title = d.Title,
                        iconUrl = d.IconUrl,
                        fileName = d.FileName
                    })
                });
        }
        catch (Exception ex)
        {
            // 查不到前置就当没有，别把下载流程卡死
            ToolReply(op, true, "前置查询失败，按无前置处理", new { count = 0, deps = Array.Empty<object>(), fail = ex.Message });
        }
    }

    /// <summary>从模组 jar 内提取图标（fabric.mod.json 的 icon 字段），转 base64 data URI。</summary>
    private static string? ExtractJarIcon(string jarPath)
    {
        try
        {
            using var zip = System.IO.Compression.ZipFile.OpenRead(jarPath);
            string? iconPath = null;
            var fmj = zip.GetEntry("fabric.mod.json") ?? zip.GetEntry("quilt.mod.json");
            if (fmj != null)
            {
                using var sr = new StreamReader(fmj.Open());
                using var doc = System.Text.Json.JsonDocument.Parse(sr.ReadToEnd());
                iconPath = doc.RootElement.TryGetProperty("icon", out var ic) &&
                           ic.ValueKind == JsonValueKind.String
                    ? ic.GetString()
                    : null;
            }
            System.IO.Compression.ZipArchiveEntry? entry = null;
            if (!string.IsNullOrEmpty(iconPath))
                entry = zip.GetEntry(iconPath.Replace('\\', '/'));
            entry ??= zip.GetEntry("assets/icon.png")
                   ?? zip.GetEntry("icon.png")
                   ?? zip.GetEntry("pack.png");
            if (entry == null || entry.Length > 256 * 1024) return null;   // 太大直接放弃，避免拖慢扫描
            using var ms = new MemoryStream();
            using (var es = entry.Open()) es.CopyTo(ms);
            return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        }
        catch { return null; }
    }

    private void ModsList(string op)
    {
        var dir = ModuleDir;
        var items = new List<object>();
        if (Directory.Exists(dir))
        {
            foreach (var f in new DirectoryInfo(dir).GetFiles("*.jar*").OrderBy(x => x.Name))
            {
                var disabled = f.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                var icon = ExtractJarIcon(f.FullName);
                items.Add(new
                {
                    file = f.Name,
                    name = disabled ? f.Name[..^9] : f.Name,
                    size = $"{f.Length / 1048576.0:0.#} MB",
                    enabled = !disabled,
                    time = f.LastWriteTime.ToString("yyyy-MM-dd"),
                    icon
                });
            }
        }
        ToolReply(op, true,
            items.Count == 0 ? $"mods 目录还是空的（{dir}）" : $"共 {items.Count} 个模组 · {items.Count(x => (bool)x.GetType().GetProperty("enabled")!.GetValue(x)!)} 个启用",
            new { dir, items, count = items.Count });
    }

    /// <summary>
    /// 把界面上传来的文件名解析成真实文件：同名、带/不带 .disabled、大小写不同的都认。
    /// 前端列表可能停留在切换前的状态（旧名字），只按原名找会误报「文件不存在」。
    /// </summary>
    private static string? ResolveModFile(string dir, string file)
    {
        if (string.IsNullOrWhiteSpace(file)) return null;
        var direct = Path.Combine(dir, file);
        if (File.Exists(direct)) return direct;

        var baseName = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? file[..^9] : file;
        foreach (var candidate in new[] { baseName, baseName + ".disabled" })
        {
            var p = Path.Combine(dir, candidate);
            if (File.Exists(p)) return p;
        }

        if (!Directory.Exists(dir)) return null;
        return new DirectoryInfo(dir).GetFiles("*.jar*")
            .FirstOrDefault(f => string.Equals(f.Name, baseName, StringComparison.OrdinalIgnoreCase)
                              || string.Equals(f.Name, baseName + ".disabled", StringComparison.OrdinalIgnoreCase))
            ?.FullName;
    }

    private void ModToggle(string op, string file)
    {
        var dir = ModuleDir;
        var path = ResolveModFile(dir, file);
        if (path == null) { ToolReply(op, false, "文件不存在，可能已被移动"); return; }
        try
        {
            // 以磁盘上的真实文件名判断方向，不信界面传来的名字
            if (path.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                var target = path[..^9];
                File.Move(path, target, true);
                ToolReply(op, true, "已启用 " + Path.GetFileName(target), new { enabled = true });
            }
            else
            {
                var target = path + ".disabled";
                File.Move(path, target, true);
                ToolReply(op, true, "已停用 " + Path.GetFileName(path), new { enabled = false });
            }
        }
        catch (Exception ex) { ToolReply(op, false, "操作失败：" + ex.Message); }
    }

    private void ModDelete(string op, string file)
    {
        var path = ResolveModFile(ModuleDir, file);
        if (path == null) { ToolReply(op, false, "文件不存在（可能已经删掉了，点「刷新」更新列表）"); return; }
        try { File.Delete(path); ToolReply(op, true, "已删除 " + Path.GetFileName(path)); }
        catch (Exception ex) { ToolReply(op, false, "删除失败：" + ex.Message); }
    }

    // ------------------------------------------------------------------ 皮肤（真实皮肤库 + 真实应用）

    private async Task SkinsPageAsync(string op, int page, string kind)
    {
        try
        {
            var items = await new SkinCatalogService().GetPageAsync(page, kind);
            var data = items.Select(s => new
            {
                id = s.Id,
                url = s.ImageUrl,
                slim = s.IsSlim,
                color = s.Color,
                tags = s.Tags.Take(3),
                wearers = s.Wearers
            }).ToList();
            ToolReply(op, true, $"第 {page} 页 · {data.Count} 个皮肤",
                new { page, kind, maxPages = SkinCatalogService.MaxPages, items = data });
        }
        catch (Exception ex) { ToolReply(op, false, "皮肤库连接失败：" + ex.Message); }
    }

    private async Task SkinApplyAsync(string op, string url, bool slim)
    {
        if (string.IsNullOrWhiteSpace(url)) { ToolReply(op, false, "皮肤地址为空"); return; }
        try
        {
            ToolStage(op, "正在下载皮肤…");
            var bytes = await _toolHttp.GetByteArrayAsync(url);
            var dir = Path.Combine(_gameDirectory, "skins");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"nex_{DateTime.Now:yyyyMMddHHmmss}.png");
            await File.WriteAllBytesAsync(path, bytes);
            var name = _session?.Username;
            if (string.IsNullOrWhiteSpace(name)) name = _settings.LastOfflineName;
            if (string.IsNullOrWhiteSpace(name)) name = "Player";

            // 关键：必须先把 authlib-injector 准备好，否则启动时无法注入皮肤代理，
            // 游戏里根本看不到这套皮肤（表现为「应用了没变化」）。
            ToolStage(op, "正在准备皮肤组件…");
            var agentErr = await LocalSkinServer.Instance.EnsureAgentAsync();
            if (agentErr != null)
            {
                ToolReply(op, false, "皮肤组件（authlib-injector）下载失败：" + agentErr + "，请检查网络后重试",
                    new { path });
                return;
            }

            var ok = LocalSkinServer.Instance.ApplySkin(path, slim ? "slim" : "wide", name, PublicSkinUrlOf(url));
            _settings.OfflineSkinPath = path;
            _settings.OfflineSkinModel = slim ? "slim" : "wide";
            _settings.OfflineSkinPlayer = name;
            _settings.OfflineSkinPublicUrl = PublicSkinUrlOf(url) ?? "";
            _settings.Save();

            // 服务器皮肤覆盖：登录服的皮肤由服务器下发，本地皮肤服务器管不到，
            // 装 CustomSkinLoader 强制用本地皮肤（对服务器也生效）
            var cslMsg = await SetupCustomSkinLoaderAsync(name, path);

            var pub = PublicSkinUrlOf(url);
            // 说清楚「别人能不能看到」：外服以服务器皮肤系统为准，本地只保证自己可见
            var scope = pub != null
                ? "，已记录公网贴图地址，别人客户端能直接下载"
                : LocalSkinServer.Instance.LanReachable
                    ? "，局域网 / 自己开服里的其他玩家能直接下载"
                    : "，这张皮肤仅在本机可见（别人的客户端拿不到本地文件）";

            ToolReply(op, ok,
                ok
                    ? $"皮肤已应用（{name}）{scope}；游戏内立即生效：Esc → 保存并退出 → 再进存档或服务器（不用重启游戏）" + cslMsg
                    : "已下载到本地，但应用失败：请确认皮肤是 64x64 或 64x32 的 PNG",
                new { path, publicUrl = pub, lan = LocalSkinServer.Instance.LanUrl });
        }
        catch (Exception ex) { ToolReply(op, false, "应用失败：" + ex.Message); }
    }

    /// <summary>
    /// 皮肤库（Ely.by）给的是公网地址，可以直接写进玩家档案，让别的玩家客户端也取到；
    /// 本地路径 / 回环地址返回 null（别人访问不到）。
    /// </summary>
    private static string? PublicSkinUrlOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != "http" && u.Scheme != "https") return null;
        var host = u.Host.ToLowerInvariant();
        if (host is "localhost" or "127.0.0.1") return null;
        // Ely.by 返回的可能是 http，统一升到 https 更稳
        if (host.EndsWith("ely.by")) return "https://" + u.Host + u.PathAndQuery;
        return u.ToString();
    }

    private async Task SkinUploadAsync(string op)
    {
        string? file = null;
        Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Title = "选择皮肤文件", Filter = "皮肤图片 (*.png)|*.png" };
            if (dlg.ShowDialog(this) == true) file = dlg.FileName;
        });
        if (file == null) { ToolReply(op, false, "已取消"); return; }
        try
        {
            var name = _session?.Username;
            if (string.IsNullOrWhiteSpace(name)) name = _settings.LastOfflineName;
            if (string.IsNullOrWhiteSpace(name)) name = "Player";
            // 同「皮肤站应用」：先保证注入器就绪，否则游戏里不会显示
            var agentErr = await LocalSkinServer.Instance.EnsureAgentAsync();
            if (agentErr != null)
            {
                ToolReply(op, false, "皮肤组件（authlib-injector）下载失败：" + agentErr + "，请检查网络后重试");
                return;
            }
            var ok = LocalSkinServer.Instance.ApplySkin(file, "wide", name, null);
            _settings.OfflineSkinPath = file;
            _settings.OfflineSkinPlayer = name;
            _settings.OfflineSkinPublicUrl = "";
            _settings.Save();
            var cslMsg = await SetupCustomSkinLoaderAsync(name, file);
            ToolReply(op, ok, ok ? $"本地皮肤已应用；游戏内立即生效：Esc → 保存并退出 → 再进存档（不用重启游戏）{cslMsg}" : "应用失败：皮肤必须是 64x64 或 64x32 的 PNG", new { path = file });
        }
        catch (Exception ex) { ToolReply(op, false, "应用失败：" + ex.Message); }
    }

    /// <summary>
    /// 安装/更新 CustomSkinLoader（服务器皮肤覆盖）。
    /// 登录服务器时你自己的皮肤由服务器下发（PlayerInfo），本地皮肤服务器无法覆盖；
    /// CSL 会强制按玩家名从本地文件取皮肤，对单人存档和服务器都生效。
    /// 返回追加到成功提示的一句话；任何失败都静默返回空串，不阻塞皮肤应用。
    /// </summary>
    private async Task<string> SetupCustomSkinLoaderAsync(string name, string skinPath)
    {
        try
        {
            var versionId = CurrentVersionId();
            if (string.IsNullOrWhiteSpace(versionId)) return "";
            var verDir = Path.Combine(_gameDirectory, "versions", versionId);
            var modsDirs = new[] { Path.Combine(verDir, "mods"), Path.Combine(_gameDirectory, "mods") };

            // 1) 取适配当前游戏版本的 CSL 构建（下载一次后复用缓存）
            var cacheJar = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NEXLauncher", "tools", "CustomSkinLoader.jar");
            var mc = McVersionOf(versionId);
            if (!File.Exists(cacheJar) && !string.IsNullOrWhiteSpace(mc))
            {
                foreach (var loader in new[] { "fabric", "neoforge", "forge" })
                {
                    var q = "https://api.modrinth.com/v2/project/customskinloader/version" +
                            $"?game_versions=%5B%22{Uri.EscapeDataString(mc)}%22%5D&loaders=%5B%22{loader}%22%5D";
                    var json = await _toolHttp.GetStringAsync(q);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) continue;
                    var url = doc.RootElement[0].GetProperty("files")[0].GetProperty("url").GetString();
                    if (string.IsNullOrWhiteSpace(url)) continue;
                    var bytes = await _toolHttp.GetByteArrayAsync(url);
                    if (bytes.Length < 100_000 || bytes[0] != 0x50 || bytes[1] != 0x4B) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(cacheJar)!);
                    await File.WriteAllBytesAsync(cacheJar, bytes);
                    break;
                }
            }

            var cslReady = false;
            if (File.Exists(cacheJar))
            {
                foreach (var dir in modsDirs)
                {
                    Directory.CreateDirectory(dir);
                    File.Copy(cacheJar, Path.Combine(dir, "CustomSkinLoader.jar"), true);
                    cslReady = true;
                }
            }
            if (!cslReady) return "";

            // 2) 皮肤落到 CSL 的 LocalSkin/skins（按玩家名命名），并清掉 CSL 缓存保证换图立即生效
            //    注意 CSL Legacy 加载器的默认根是 LocalSkin/skins/{USERNAME}.png（带 skins 子目录）
            var cslDirs = new[] { Path.Combine(_gameDirectory, "CustomSkinLoader"),
                                  Path.Combine(verDir, "CustomSkinLoader") };
            foreach (var cslDir in cslDirs)
            {
                var local = Path.Combine(cslDir, "LocalSkin");
                Directory.CreateDirectory(Path.Combine(local, "skins"));
                Directory.CreateDirectory(Path.Combine(local, "capes"));
                Directory.CreateDirectory(Path.Combine(local, "elytras"));
                File.Copy(skinPath, Path.Combine(local, "skins", $"{name}.png"), true);
                // 清掉历史上误放到 LocalSkin 根目录的皮肤
                var legacy = Path.Combine(local, $"{name}.png");
                if (File.Exists(legacy)) { try { File.Delete(legacy); } catch { } }
                foreach (var c in new[] { "caches", "ProfileCache", "Cache" })
                {
                    var cd = Path.Combine(cslDir, c);
                    if (Directory.Exists(cd)) { try { Directory.Delete(cd, true); } catch { } }
                }
            }

            // 3) 配置：LocalSkin 置顶。skin 必须带 {USERNAME} 占位符——
            //    CSL Legacy 加载器把 skin 当文件路径用，没有占位符就会拿目录名当文件找，永远找不到皮肤
            var cfg = new
            {
                version = "15.1",
                buildNumber = 41,
                loadlist = new object[]
                {
                    new { name = "LocalSkin", type = "Legacy", checkPNG = false, model = "auto",
                          skin = "LocalSkin/skins/{USERNAME}.png",
                          cape = "LocalSkin/capes/{USERNAME}.png",
                          elytra = "LocalSkin/elytras/{USERNAME}.png" },
                    new { name = "Mojang", type = "MojangAPI" }
                },
                enableDynamicSkull = true,
                enableTransparentSkin = true,
                forceLoadAllTextures = true,
                enableCape = false,
                threadPoolSize = 8,
                enableLogStdOut = false,
                cacheExpiry = 1
            };
            var cfgJson = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
            foreach (var cslDir in cslDirs)
            {
                var p = Path.Combine(cslDir, "CustomSkinLoader.json");
                var needWrite = true;
                if (File.Exists(p))
                {
                    // 已有配置且 LocalSkin 条目已带 {USERNAME} 占位符才保留（尊重用户自定义），
                    // 否则视为旧版启动器写坏的配置，覆盖
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(p));
                        if (doc.RootElement.TryGetProperty("loadlist", out var ll) && ll.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var e in ll.EnumerateArray())
                            {
                                var isLocal = e.TryGetProperty("name", out var n) && n.GetString() == "LocalSkin";
                                var hasPh = e.TryGetProperty("skin", out var s) &&
                                            (s.GetString()?.Contains("{USERNAME}") ?? false);
                                if (isLocal && hasPh) { needWrite = false; break; }
                            }
                        }
                    }
                    catch { needWrite = true; }
                }
                if (needWrite) await File.WriteAllTextAsync(p, cfgJson);
            }
            return "；已装 CustomSkinLoader，服务器里的皮肤也会用这套";
        }
        catch { return ""; }
    }

    // ------------------------------------------------------------------ 统计 / 公告

    private void Stats(string op)
    {
        var (seconds, launches) = PlaytimeStatsService.Instance.GetTotals();
        var per = PlaytimeStatsService.Instance.GetAll()
            .OrderByDescending(kv => kv.Value.TotalSeconds)
            .Take(8)
            .Select(kv => new
            {
                version = kv.Key,
                seconds = kv.Value.TotalSeconds,
                text = PlaytimeStatsService.FormatDuration(kv.Value.TotalSeconds),
                launches = kv.Value.LaunchCount,
                last = kv.Value.LastPlayed?.ToString("yyyy-MM-dd HH:mm") ?? "—"
            }).ToList();
        var max = per.Count == 0 ? 1 : Math.Max(1, per.Max(p => p.seconds));
        ToolReply(op, true, $"已游玩 {PlaytimeStatsService.FormatDuration(seconds)} · 共启动 {launches} 次",
            new { seconds, launches, text = PlaytimeStatsService.FormatDuration(seconds), perVersion = per, max });
    }

    private async Task NewsAsync(string op)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.github.com/repos/synb-7891/NEXLauncher/releases?per_page=3");
            req.Headers.UserAgent.ParseAdd("NEXLauncher");
            using var resp = await _toolHttp.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            var list = new List<object>();
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() : "";
                var pub = r.TryGetProperty("published_at", out var p) && p.GetString() is { } ps
                          && DateTimeOffset.TryParse(ps, out var dt) ? dt.ToLocalTime().ToString("MM-dd") : "";
                var name = r.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
                var note = r.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
                var line = note.Split('\n').Select(x => x.Trim().TrimStart('-', '*', ' '))
                    .FirstOrDefault(x => x.Length > 3) ?? "";
                if (line.Length > 80) line = line[..80] + "…";
                list.Add(new { tag, date = pub, name, note = line });
            }
            ToolReply(op, true, $"最新 {list.Count} 条更新", new { items = list, source = "GitHub Releases" });
        }
        catch (Exception ex) { ToolReply(op, false, "获取更新日志失败（可能没联网）：" + ex.Message); }
    }

    // ------------------------------------------------------------------ 首页整体快照

    private async Task DockAsync(string op)
    {
        int Count(string dirOrSub, string pattern) => CountIn(dirOrSub, pattern);

        int CountIn(string dirOrSub, string pattern)
        {
            try
            {
                var p = Path.IsPathRooted(dirOrSub) ? dirOrSub : Path.Combine(_gameDirectory, dirOrSub);
                return Directory.Exists(p) ? Directory.GetFiles(p, pattern).Length : 0;
            }
            catch { return 0; }
        }

        var (seconds, launches) = PlaytimeStatsService.Instance.GetTotals();
        var installed = GetInstalledVersionIds();
        var mods = Count(ModuleDir, "*.jar");
        var sec = BuildSecurityState();

        var lastVer = PlaytimeStatsService.Instance.GetAll()
            .OrderByDescending(kv => kv.Value.LastPlayed ?? DateTime.MinValue)
            .FirstOrDefault();

        var java = new JavaService().Detect(_settings.JavaPath);
        var curJava = java.FirstOrDefault(r => string.Equals(r.JavaPath, _settings.JavaPath, StringComparison.OrdinalIgnoreCase))
                      ?? java.FirstOrDefault();

        ToolReply(op, true, null, new
        {
            account = new
            {
                name = _session?.Username ?? (_settings.LastOfflineName ?? ""),
                kind = string.Equals(_settings.LastAccountType, "microsoft", StringComparison.OrdinalIgnoreCase) ? "微软正版" : "离线模式",
                loggedIn = _session != null
            },
            versions = new { installed = installed.Count, selected = CurrentVersionId() },
            ram = new
            {
                total = (int)TotalPhysicalMb(),
                avail = (int)AvailablePhysicalMb(),
                max = _settings.MaxRamMb,
                recommended = RecommendRam((int)TotalPhysicalMb())
            },
            java = new
            {
                found = curJava != null,
                title = curJava?.Title ?? "未检测到 Java",
                path = curJava?.JavaPath ?? "",
                major = curJava?.Major ?? 0
            },
            stats = new { seconds, launches, text = PlaytimeStatsService.FormatDuration(seconds), lastVersion = lastVer.Key, lastPlayed = lastVer.Value?.LastPlayed?.ToString("MM-dd HH:mm") ?? "" },
            resources = new
            {
                mods,
                resourcepacks = Count("resourcepacks", "*"),
                shaderpacks = Count("shaderpacks", "*"),
                saves = DirectoryCount(Path.Combine(_gameDirectory, "saves")),
                dataPacks = Count("datapacks", "*")
            },
            security = new
            {
                guardRunning = sec.GetType().GetProperty("guardRunning")?.GetValue(sec) is true,
                pid = sec.GetType().GetProperty("pid")?.GetValue(sec),
                uptime = sec.GetType().GetProperty("uptime")?.GetValue(sec)?.ToString() ?? "",
                canaries = (sec.GetType().GetProperty("canaries")?.GetValue(sec) as List<string>)?.Count ?? 0
            },
        });
    }

    private static int DirectoryCount(string dir)
    {
        try { return Directory.Exists(dir) ? Directory.GetDirectories(dir).Length : 0; }
        catch { return 0; }
    }

    // ------------------------------------------------------------------ 隔离区 / 体检

    private void QuarantineList(string op)
    {
        var items = SecurityService.Instance.ListQuarantine()
            .Select(q => new
            {
                quarantined = q.Quarantined,
                file = Path.GetFileName(q.Quarantined),
                original = q.Original,
                reason = q.Reason,
                time = q.Time,
                size = File.Exists(q.Quarantined) ? $"{new FileInfo(q.Quarantined).Length / 1024.0:0.#} KB" : "—"
            }).ToList();
        ToolReply(op, true, items.Count == 0 ? "隔离区是空的" : $"隔离区有 {items.Count} 个样本", new { items, dir = SecurityService.Instance.QuarantineDir });
    }

    private void QuarantineRestore(string op, string quarantined)
    {
        var item = SecurityService.Instance.ListQuarantine()
            .FirstOrDefault(q => string.Equals(q.Quarantined, quarantined, StringComparison.OrdinalIgnoreCase));
        if (item == null) { ToolReply(op, false, "找不到该隔离样本"); return; }
        try { SecurityService.Instance.RestoreQuarantine(item); ToolReply(op, true, "已恢复到 " + item.Original); }
        catch (Exception ex) { ToolReply(op, false, "恢复失败：" + ex.Message); }
    }

    private void QuarantineDelete(string op, string quarantined)
    {
        var item = SecurityService.Instance.ListQuarantine()
            .FirstOrDefault(q => string.Equals(q.Quarantined, quarantined, StringComparison.OrdinalIgnoreCase));
        if (item == null) { ToolReply(op, false, "找不到该隔离样本"); return; }
        try { SecurityService.Instance.DeleteQuarantine(item); ToolReply(op, true, "已彻底删除该样本"); }
        catch (Exception ex) { ToolReply(op, false, "删除失败：" + ex.Message); }
    }

    private void Audit(string op)
    {
        var findings = new List<object>();
        void Add(string name, bool ok, string detail) => findings.Add(new { name, ok, detail });

        // 1) 诱饵在位（缺的会自动补部署）
        var canaryCount = 0;
        try { canaryCount = SecurityService.Instance.DeployCanaries(); } catch { }
        Add("勒索诱饵", canaryCount >= 5, canaryCount >= 5 ? $"{canaryCount} 个诱饵在位" : $"只部署成功 {canaryCount} 个诱饵，请检查目录权限");

        // 2) 守护进程
        var sec = BuildSecurityState();
        var guardRunning = sec.GetType().GetProperty("guardRunning")?.GetValue(sec) is true;
        Add("守护进程", guardRunning, guardRunning ? "独立守护进程正在运行" : "未检测到守护进程（仅进程内监控）");

        // 3) hosts 快照
        var hostsSnap = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXLauncher", "hosts.snapshot");
        Add("hosts 防劫持", File.Exists(hostsSnap), File.Exists(hostsSnap) ? "已保存可信快照，被改动会自动还原" : "还没有 hosts 快照，开启系统守护后自动生成");

        // 4) mods 目录里的可执行伪装
        var bad = 0;
        var modsDir = ModuleDir;
        if (Directory.Exists(modsDir))
        {
            foreach (var f in Directory.GetFiles(modsDir))
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".exe" or ".bat" or ".cmd" or ".ps1" or ".vbs" or ".scr" or ".dll") bad++;
                if (System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), @"\.[a-z0-9]{2,4}\.(jar|zip)$")) bad++;
            }
        }
        Add("模组目录扫描", bad == 0, bad == 0 ? "未发现可执行伪装文件" : $"发现 {bad} 个可疑文件，建议检查 mods 目录");

        // 5) 启动项
        var startupBad = 0;
        try
        {
            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            foreach (var n in run?.GetValueNames() ?? Array.Empty<string>())
            {
                var v = run!.GetValue(n)?.ToString() ?? "";
                if (v.Contains("\\Temp\\", StringComparison.OrdinalIgnoreCase) || v.Contains("\\AppData\\Local\\Temp", StringComparison.OrdinalIgnoreCase))
                    startupBad++;
            }
        }
        catch { }
        Add("开机启动项", startupBad == 0, startupBad == 0 ? "启动项里没有来自临时目录的可疑程序" : $"发现 {startupBad} 个临时目录启动项，疑似病毒驻留");

        // 6) 隔离区
        var q = SecurityService.Instance.ListQuarantine();
        Add("隔离区", true, q.Count == 0 ? "隔离区为空，没有待处理威胁" : $"{q.Count} 个样本已隔离，可在下方恢复或删除");

        var badCount = findings.Count(f => f.GetType().GetProperty("ok")?.GetValue(f) is false);
        ToolReply(op, badCount == 0, badCount == 0 ? $"体检完成：{findings.Count} 项全部正常" : $"体检完成：{badCount} 项需要处理", new { findings, badCount });
    }

    // ------------------------------------------------------------------ 一键开服（真实下载服务端并启动）

    private void ServerState(string op)
        => ToolReply(op, true, _oneClickService.IsRunning ? "服务器运行中" : "服务器未运行",
            new { running = _oneClickService.IsRunning, dir = _oneClickService.ServerDir });

    private async Task ServerStartAsync(string op)
    {
        if (_oneClickService.IsRunning) { ToolReply(op, false, "服务器已经在运行"); return; }
        if (OneClickServerService.FindJava() == null)
        {
            ToolReply(op, false, "未检测到 Java 17+，请先在「运行环境」里下载 Java 21");
            return;
        }
        try
        {
            ToolStage(op, "正在获取官方服务端版本列表…");
            var versions = await _oneClickService.GetReleaseVersionsAsync(10);
            var version = versions.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(version)) { ToolReply(op, false, "获取服务端版本失败，请检查网络"); return; }

            ToolStage(op, $"准备服务端目录（版本 {version}）…");
            var dir = _oneClickService.PrepareServerDirectory(_gameDirectory, offlineMode: true, maxPlayers: 10, motd: "NEX Server " + version);
            var jar = Path.Combine(dir, "server.jar");
            if (!File.Exists(jar))
                await _oneClickService.DownloadServerJarAsync(version, dir, m => ToolStage(op, m));
            else
                ToolStage(op, "已存在服务端文件，跳过下载");

            var ram = Math.Clamp(_settings.MaxRamMb, 1024, 8192);
            ToolStage(op, $"正在启动 {version} 服务端（内存 {ram} MB）…");
            _oneClickService.Start(dir, "server.jar", ram);
            ToolReply(op, true, $"服务器已启动：{version} · 内存 {ram} MB · 目录 {dir}", new { version, dir, ram });
        }
        catch (Exception ex) { ToolReply(op, false, "开服失败：" + ex.Message); }
    }

    private void ServerStop(string op)
    {
        try
        {
            if (!_oneClickService.IsRunning) { ToolReply(op, false, "服务器没有在运行"); return; }
            _oneClickService.Stop();
            ToolReply(op, true, "服务器已停止");
        }
        catch (Exception ex) { ToolReply(op, false, "停止失败：" + ex.Message); }
    }

    private void OpenUrlSafe(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return;
        if (u.Scheme != "http" && u.Scheme != "https") return;
        try { Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true }); } catch { }
    }

    // ------------------------------------------------------------------ 设置项

    private void JavaUse(string op, string path)
    {
        if (!File.Exists(path)) { ToolReply(op, false, "找不到 java.exe：" + path); return; }
        _settings.JavaPath = path;
        _settings.Save();
        try { Dispatcher.Invoke(PushState); } catch { }
        ToolReply(op, true, "已设为默认 Java：" + path, new { path });
    }

    private void SettingsGet(string op)
        => ToolReply(op, true, null, new
        {
            UseMirror = _settings.UseMirror,
            VersionIsolation = _settings.VersionIsolation,
            CloseToTray = _settings.CloseToTray,
            AutoLogin = _settings.AutoLogin,
            GameFullScreen = _settings.GameFullScreen,
            SecAlwaysGuard = _settings.SecAlwaysGuard,
            MaxRamMb = _settings.MaxRamMb,
            dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NEXLauncher"),
            configFile = "settings.json"
        });

    private void SettingSet(string op, string key, bool value)
    {
        switch (key)
        {
            case "UseMirror": _settings.UseMirror = value; break;
            case "VersionIsolation": _settings.VersionIsolation = value; break;
            case "CloseToTray": _settings.CloseToTray = value; break;
            case "AutoLogin": _settings.AutoLogin = value; break;
            case "GameFullScreen": _settings.GameFullScreen = value; break;
            case "SecAlwaysGuard":
                _settings.SecAlwaysGuard = value;
                try { SecurityService.Instance.ApplySettings(); } catch { }
                break;
            default: ToolReply(op, false, "不支持的设置项：" + key); return;
        }
        _settings.Save();
        try { Dispatcher.Invoke(PushState); } catch { }
        ToolReply(op, true, "已保存到 settings.json", new { key, value });
    }

}

/// <summary>任意地址下载：让用户填 URL 并选保存位置（真实 SaveFileDialog）。</summary>
public sealed class DirectDownloadDialog : Window
{
    private readonly System.Windows.Controls.TextBox _url;
    private readonly System.Windows.Controls.TextBox _path;

    public string Url => _url.Text.Trim();
    public string SavePath => _path.Text.Trim();

    public DirectDownloadDialog()
    {
        Title = "下载任意文件";
        Width = 520; Height = 210;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF3, 0xF5, 0xF9));

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "下载地址（http/https）", FontSize = 12, Margin = new Thickness(0, 0, 0, 4) });
        _url = new System.Windows.Controls.TextBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0) };
        panel.Children.Add(_url);

        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "保存到", FontSize = 12, Margin = new Thickness(0, 12, 0, 4) });
        var row = new System.Windows.Controls.Grid();
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = GridLength.Auto });
        _path = new System.Windows.Controls.TextBox { Height = 30, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 0, 8, 0) };
        row.Children.Add(_path);
        var browse = new System.Windows.Controls.Button { Content = "浏览…", Width = 70, Margin = new Thickness(8, 0, 0, 0) };
        System.Windows.Controls.Grid.SetColumn(browse, 1);
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { FileName = "download.bin", Filter = "所有文件|*.*" };
            if (dlg.ShowDialog(this) == true) _path.Text = dlg.FileName;
        };
        row.Children.Add(browse);
        panel.Children.Add(row);

        var btns = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 80, Height = 30 };
        cancel.Click += (_, _) => { DialogResult = false; Close(); };
        var ok = new System.Windows.Controls.Button { Content = "开始下载", Width = 100, Height = 30, Margin = new Thickness(8, 0, 0, 0) };
        ok.Click += (_, _) =>
        {
            if (!Uri.TryCreate(_url.Text.Trim(), UriKind.Absolute, out var u) || (u.Scheme != "http" && u.Scheme != "https"))
            { MessageBox.Show(this, "请输入有效的 http/https 地址", "NEX"); return; }
            if (string.IsNullOrWhiteSpace(_path.Text.Trim()))
            { MessageBox.Show(this, "请选择保存位置", "NEX"); return; }
            DialogResult = true; Close();
        };
        btns.Children.Add(cancel);
        btns.Children.Add(ok);
        panel.Children.Add(btns);

        Content = panel;
    }
}
