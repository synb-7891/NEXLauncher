using System.IO;
using System.Net.Http;
using System.Text.Json;

namespace NEXLauncher.Services;

public sealed class ModloaderOption
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
}

public sealed class ModloaderService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static readonly ModloaderOption[] Loaders =
    {
        new() { Id = "vanilla", Name = "原版", Description = "不安装模组加载器，纯净游戏" },
        new() { Id = "fabric",  Name = "Fabric", Description = "轻量模组加载器，模组生态最丰富" },
        new() { Id = "forge",   Name = "Forge",  Description = "最老牌的模组加载器，大型模组首选" },
        new() { Id = "quilt",   Name = "Quilt",  Description = "Fabric 的分支，兼容性更好" },
    };

    public sealed record ForgeLoaderInfo(List<string> Versions, string? Latest, string? Recommended);

    /// <summary>
    /// 获取 Fabric 可用的加载器版本列表。
    /// 注意：Fabric meta 只把「最新一个」loader 标记 stable=true，其余全部是 false；
    /// 若只过滤 stable，下拉框永远只剩 1 个版本。这里返回全部版本（稳定版排最前），
    /// 默认选中项即最新稳定版，行为与 HMCL/PCL 一致。
    /// 数据源：官方 meta 优先，失败自动切 BMCLAPI 国内镜像（实测 0.6s vs 官方 5.9s）。
    /// </summary>
    public async Task<List<string>> GetFabricLoaderVersionsAsync(string gameVersion)
    {
        var json = await FetchWithFallbackAsync(
            $"https://meta.fabricmc.net/v2/versions/loader/{gameVersion}",
            $"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{gameVersion}");
        using var doc = JsonDocument.Parse(json);
        var stableList = new List<string>();
        var unstableList = new List<string>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            // Fabric meta 结构：{ loader: { version: "0.20.1", stable: true }, ... }
            if (!item.TryGetProperty("loader", out var loader)) continue;
            if (!loader.TryGetProperty("version", out var ver)) continue;
            var version = ver.GetString();
            if (string.IsNullOrEmpty(version)) continue;
            var stable = loader.TryGetProperty("stable", out var s) && s.GetBoolean();
            if (stable) stableList.Add(version); else unstableList.Add(version);
        }
        // 同版本可能同时出现在多个 intermediary/映射条目下，去重
        return stableList.Concat(unstableList).Distinct().ToList();
    }

    /// <summary>获取 Quilt 可用的加载器版本列表（官方源失败自动切 BMCLAPI 镜像）。</summary>
    public async Task<List<string>> GetQuiltLoaderVersionsAsync(string gameVersion)
    {
        var json = await FetchWithFallbackAsync(
            $"https://meta.quiltmc.org/v3/versions/loader/{gameVersion}",
            $"https://bmclapi2.bangbang93.com/quilt-meta/v3/versions/loader/{gameVersion}");
        using var doc = JsonDocument.Parse(json);
        var result = new List<string>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            // Quilt meta 结构：{ loader: { version: "0.20.x", ... }, ... }
            if (!item.TryGetProperty("loader", out var loader)) continue;
            if (!loader.TryGetProperty("version", out var ver)) continue;
            var version = ver.GetString();
            if (!string.IsNullOrEmpty(version)) result.Add(version);
        }
        return result.Distinct().ToList();
    }

    /// <summary>
    /// 主源 + 镜像源双线路抓取：主源失败（超时/网络抖动）自动切镜像，
    /// 每个源各重试一次。两条路都断才向上抛异常。
    /// </summary>
    private static async Task<string> FetchWithFallbackAsync(string primary, string mirror)
    {
        Exception? last = null;
        foreach (var url in new[] { primary, primary, mirror, mirror })
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var body = await Http.GetStringAsync(url);
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "NEX-fabric-debug.log"),
                    $"[{DateTime.Now:HH:mm:ss}] OK {new Uri(url).Host}{new Uri(url).AbsolutePath} len={body.Length} {sw.ElapsedMilliseconds}ms\n"); }
                catch { }
                return body;
            }
            catch (Exception ex)
            {
                last = ex;
                try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "NEX-fabric-debug.log"),
                    $"[{DateTime.Now:HH:mm:ss}] FAIL {new Uri(url).Host}{new Uri(url).AbsolutePath} {ex.InnerException?.Message ?? ex.Message} {sw.ElapsedMilliseconds}ms\n"); }
                catch { }
            }
        }
        throw last ?? new Exception("所有数据源均不可用");
    }

    /// <summary>获取 Forge 可用的加载器版本（最新在前），并附官方"最新/推荐"标记。优先走 BMCLAPI 国内镜像，失败回落官方源。</summary>
    public async Task<ForgeLoaderInfo> GetForgeLoaderInfoAsync(string gameVersion)
    {
        // 1) BMCLAPI 镜像（国内直连快）
        try
        {
            var json = await Http.GetStringAsync($"https://bmclapi2.bangbang93.com/forge/minecraft/{gameVersion}");
            using var doc = JsonDocument.Parse(json);
            var versions = doc.RootElement.EnumerateArray()
                .Select(e => e.TryGetProperty("version", out var v) ? v.GetString() : null)
                .Where(v => !string.IsNullOrEmpty(v))
                .Select(v => v!)
                .Distinct()
                .OrderByDescending(ForgeVersionSortKey)
                .ToList();
            if (versions.Count > 0)
            {
                var (latest, recommended) = await TryGetForgePromotionsAsync(gameVersion);
                latest ??= versions[0];
                return new ForgeLoaderInfo(versions, latest, recommended);
            }
        }
        catch { /* 镜像失败走官方源 */ }

        // 2) 官方源回落：全量版本列表（maven-metadata 按 发布时间 升序，取反即最新在前）
        var xml = await Http.GetStringAsync("https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml");
        var official = new List<string>();
        using (var reader = System.Xml.XmlReader.Create(new StringReader(xml)))
        {
            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element
                    && reader.LocalName == "version" && reader.Read())
                {
                    var v = reader.Value.Trim();
                    if (v.StartsWith(gameVersion + "-", StringComparison.Ordinal))
                        official.Add(v[(gameVersion.Length + 1)..]);
                }
            }
        }
        if (official.Count == 0)
            return new ForgeLoaderInfo([], null, null);
        official.Reverse();

        var (latest2, recommended2) = await TryGetForgePromotionsAsync(gameVersion);
        return new ForgeLoaderInfo(official, latest2 ?? official[0], recommended2);
    }

    /// <summary>读取 Forge 官方推广表（latest / recommended），失败不影响版本列表。</summary>
    private async Task<(string? Latest, string? Recommended)> TryGetForgePromotionsAsync(string gameVersion)
    {
        try
        {
            var promos = await Http.GetStringAsync(
                "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json");
            using var doc = JsonDocument.Parse(promos);
            if (doc.RootElement.TryGetProperty("promos", out var promosObj))
            {
                string? latest = promosObj.TryGetProperty(gameVersion + "-latest", out var l) ? l.GetString() : null;
                string? recommended = promosObj.TryGetProperty(gameVersion + "-recommended", out var r) ? r.GetString() : null;
                return (latest, recommended);
            }
        }
        catch { /* 推广表拿不到不影响版本列表 */ }
        return (null, null);
    }

    /// <summary>按数字段比较 Forge 版本号（如 47.4.0 > 47.3.22 > 47.3.9），用于镜像列表排序。</summary>
    private static long ForgeVersionSortKey(string v)
    {
        long key = 0;
        foreach (var seg in v.Split('.'))
        {
            var digits = new string(seg.TakeWhile(char.IsDigit).ToArray());
            key = key * 1000 + (long.TryParse(digits, out var n) ? Math.Clamp(n, 0, 999) : 0);
            if (key > 1_000_000_000_000) break;
        }
        return key;
    }

    /// <summary>
    /// 安装 Forge：下载官方 installer 并用本地 Java 执行 --installClient，
    /// 然后扫描 versions 目录返回生成的版本 ID（如 1.20.1-forge-47.2.0）。
    /// </summary>
    public async Task<string> InstallForgeAsync(string gameDirectory, string gameVersion, string forgeVersion)
    {
        var instId = $"{gameVersion}-{forgeVersion}";
        var url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{instId}/forge-{instId}-installer.jar";
        var tempJar = Path.Combine(Path.GetTempPath(), $"NEX-forge-{instId}-installer.jar");

        using (var dl = new HttpClient { Timeout = TimeSpan.FromMinutes(5) })
        {
            var bytes = await dl.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(tempJar, bytes);
        }

        var java = OneClickServerService.FindJava()
            ?? throw new FileNotFoundException(
                "安装 Forge 需要 Java 运行环境。请先安装 Java（建议 17 或 21），或在设置中配置 Java 路径后重试。");

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = java,
            Arguments = $"-jar \"{tempJar}\" --installClient \"{gameDirectory}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var installer = System.Diagnostics.Process.Start(psi)
            ?? throw new Exception("无法启动 Forge 安装器。");
        var output = installer.StandardOutput.ReadToEnd();
        var err = installer.StandardError.ReadToEnd();
        await installer.WaitForExitAsync();
        if (installer.ExitCode != 0)
            throw new Exception($"Forge 安装器失败（退出码 {installer.ExitCode}）：\n{err}\n{output}");

        // 安装器生成的版本目录名因版本而异（forge-1.20.1-47.2.0 / 1.20.1-forge-47.2.0），按特征匹配
        var versionsDir = Path.Combine(gameDirectory, "versions");
        var hit = Directory.GetDirectories(versionsDir)
            .Where(d =>
            {
                var n = Path.GetFileName(d);
                return n.Contains(forgeVersion) && n.Contains(gameVersion)
                    && File.Exists(Path.Combine(d, n + ".json"));
            })
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault()
            ?? throw new Exception("Forge 安装器已运行，但未在 versions 目录找到生成的版本。");

        try { File.Delete(tempJar); } catch { }
        return Path.GetFileName(hit);
    }

    /// <summary>下载指定加载器的 profile JSON，保存到 versions 目录，返回版本 ID。</summary>
    public async Task<string> InstallLoaderProfileAsync(string gameDirectory, string gameVersion, string loaderId, string loaderVersion)
    {
        string url, versionId;
        if (loaderId == "fabric")
        {
            url = $"https://meta.fabricmc.net/v2/versions/loader/{gameVersion}/{loaderVersion}/profile/json";
            versionId = $"fabric-loader-{loaderVersion}-{gameVersion}";
        }
        else if (loaderId == "quilt")
        {
            url = $"https://meta.quiltmc.org/v3/versions/loader/{gameVersion}/{loaderVersion}/profile/json";
            versionId = $"quilt-loader-{loaderVersion}-{gameVersion}";
        }
        else
        {
            return gameVersion; // 原版
        }

        var json = loaderId == "fabric"
            ? await FetchWithFallbackAsync(
                url,
                $"https://bmclapi2.bangbang93.com/fabric-meta/v2/versions/loader/{gameVersion}/{loaderVersion}/profile/json")
            : loaderId == "quilt"
                ? await FetchWithFallbackAsync(
                    url,
                    $"https://bmclapi2.bangbang93.com/quilt-meta/v3/versions/loader/{gameVersion}/{loaderVersion}/profile/json")
                : await Http.GetStringAsync(url);
        var dir = Path.Combine(gameDirectory, "versions", versionId);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, versionId + ".json"), json);
        return versionId;
    }
}
