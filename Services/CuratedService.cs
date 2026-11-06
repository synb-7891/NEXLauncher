using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NEXLauncher.Services;

/// <summary>
/// 内置精选：Modrinth 上没有的项目（Meteor Client / Wurst）和搜不到的资源包（Xray）在这里补齐，
/// 直链由各自官方渠道实时解析，不写死会过期的下载地址。
/// - Xray   → Modrinth 项目 x-ray（按当前游戏版本挑）
/// - 彗星   → meteorclient.com 官方下载接口（带版本号，永远拿最新构建）
/// - Wurst  → GitHub Wurst-MCX2 release 里挑 -MC&lt;版本&gt;.jar
/// - 前置   → Modrinth fabric-api（按当前游戏版本）
/// </summary>
public static class CuratedService
{
    private static readonly HttpClient Http = CreateClient();

    // GitHub 走直连：系统代理常常把 api.github.com 转成 502，直连反而通
    private static readonly HttpClient Direct = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    public static IReadOnlyList<CuratedPick> Picks { get; } = new List<CuratedPick>
    {
        new()
        {
            Id = "xray", Title = "Xray 矿透资源包", Badge = "矿透", Kind = "resourcepack",
            Subtitle = "Spectator Xray · 矿石高亮，纯资源包不需要装模组"
        },
        new()
        {
            Id = "meteor", Title = "Meteor Client · 彗星", Badge = "客户端", Kind = "mod", Loader = "fabric",
            Subtitle = "Fabric 客户端 · Xray / KillAura / Flight，自动带前置"
        },
        new()
        {
            Id = "wurst", Title = "Wurst Client", Badge = "客户端", Kind = "mod", Loader = "fabric",
            Subtitle = "经典 Fabric 客户端 · X-Ray / Nuker / Freecam，自动带前置"
        },
        new()
        {
            Id = "fabricapi", Title = "Fabric API · 前置", Badge = "前置", Kind = "mod", Loader = "fabric",
            Subtitle = "彗星 / Wurst 的必需前置，单独安装也放这里"
        }
    };

    public static CuratedPick? Find(string id) =>
        Picks.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 解析某个内置项在当前游戏版本下的真实下载地址。返回空列表表示当前版本没有可用文件。
    /// </summary>
    public static async Task<List<CuratedFile>> ResolveAsync(string id, string mcVersion, CancellationToken ct = default)
    {
        var result = new List<CuratedFile>();
        switch (id.ToLowerInvariant())
        {
            case "xray":
                {
                    var f = await ModrinthFileOfAsync("x-ray", mcVersion, null, ct);
                    if (f != null)
                        result.Add(new CuratedFile(f.Url, SafeName(f.FileName, "Spectator-Xray.zip", ".zip")));
                    break;
                }
            case "fabricapi":
                {
                    var f = await ModrinthFileOfAsync("fabric-api", mcVersion, "fabric", ct);
                    if (f != null)
                        result.Add(new CuratedFile(f.Url, SafeName(f.FileName, "fabric-api.jar", ".jar")));
                    break;
                }
            case "meteor":
                {
                    // 官方接口直接回 jar，文件名在 content-disposition 里
                    var ver = string.IsNullOrWhiteSpace(mcVersion) ? "" : mcVersion;
                    if (ver.Length == 0) throw new InvalidOperationException("先选一个游戏版本");
                    var url = $"https://meteorclient.com/api/download?version={Uri.EscapeDataString(ver)}";
                    using var head = new HttpRequestMessage(HttpMethod.Head, url);
                    using var resp = await Http.SendAsync(head, ct);
                    if (!resp.IsSuccessStatusCode)
                        throw new InvalidOperationException($"彗星还没有 {ver} 的构建（官方接口返回 {(int)resp.StatusCode}）");
                    var name = resp.Content.Headers.ContentDisposition?.FileNameStar
                               ?? resp.Content.Headers.ContentDisposition?.FileName?.Trim('"');
                    result.Add(new CuratedFile(url, SafeName(name, $"meteor-client-{ver}.jar", ".jar")));
                    break;
                }
            case "wurst":
                {
                    var ver = string.IsNullOrWhiteSpace(mcVersion) ? "" : mcVersion;
                    if (ver.Length == 0) throw new InvalidOperationException("先选一个游戏版本");
                    var hit = await WurstAssetAsync(ver, ct);
                    if (hit == null)
                        throw new InvalidOperationException($"Wurst 还没有 {ver} 的构建");
                    result.Add(hit);
                    break;
                }
            default:
                throw new InvalidOperationException("未知的内置项目：" + id);
        }
        return result;
    }

    /// <summary>Wurst 的下载直链：先试 GitHub release（动态），失败再用已知版本的固定地址。</summary>
    private static async Task<CuratedFile?> WurstAssetAsync(string mcVersion, CancellationToken ct)
    {
        try
        {
            var json = await Direct.GetStringAsync(
                "https://api.github.com/repos/Wurst-Imperium/Wurst-MCX2/releases?per_page=10", ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (!rel.TryGetProperty("assets", out var assets)) continue;
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.GetProperty("name").GetString() ?? "";
                    if (!name.EndsWith($"-MC{mcVersion}.jar", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Contains("sources", StringComparison.OrdinalIgnoreCase)) continue;
                    var url = a.GetProperty("browser_download_url").GetString();
                    if (!string.IsNullOrEmpty(url)) return new CuratedFile(url, SafeName(name, $"Wurst-{mcVersion}.jar", ".jar"));
                }
            }
        }
        catch { /* 网络不通时走固定地址 */ }

        if (KnownWurst.TryGetValue(mcVersion, out var known))
            return new CuratedFile(
                $"https://github.com/Wurst-Imperium/Wurst-MCX2/releases/download/{known.Tag}/{known.Name}",
                known.Name);
        return null;
    }

    // 已人工验证过的版本（GitHub API 不可达时的兜底）
    private static readonly Dictionary<string, (string Tag, string Name)> KnownWurst = new()
    {
        ["1.21.11"] = ("v7.56.2", "Wurst-Client-v7.56.2-MC1.21.11.jar")
    };

    private static async Task<ModrinthFile?> ModrinthFileOfAsync(string projectId, string mcVersion,
        string? loader, CancellationToken ct)
    {
        var svc = new ModrinthService();
        var vers = new List<ModrinthVersion>();
        if (!string.IsNullOrWhiteSpace(mcVersion))
            vers = await svc.GetVersionsFilteredAsync(projectId, mcVersion, loader, ct);
        if (vers.Count == 0)
            vers = await svc.GetVersionsAsync(projectId, ct);
        var best = vers.Where(v => v.Files.Count > 0)
                       .OrderByDescending(v => v.DatePublished)
                       .FirstOrDefault();
        return best?.PrimaryFile ?? best?.Files.FirstOrDefault();
    }

    /// <summary>
    /// 文件名清洗：Modrinth 上不少资源名带 § 颜色代码（Windows 不能做文件名），
    /// 这里去掉控制符并补上扩展名。
    /// </summary>
    private static string SafeName(string? raw, string fallback, string ext)
    {
        var s = (raw ?? "").Trim();
        s = Regex.Replace(s, "§.", "");                       // §b §l 之类
        s = Regex.Replace(s, "[\\x00-\\x1f<>:\"/\\\\|?*]", ""); // 非法文件名字符
        s = s.Replace(' ', '-').Trim('-', '.', ' ');
        if (s.Length == 0) s = fallback;
        if (!s.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) s += ext;
        return s;
    }

    /// <summary>最后一次兜底：绕过系统代理直接下（代理把 GitHub 转成 502 时用）。</summary>
    public static async Task DownloadDirectAsync(string url, string dest,
        IProgress<(long Received, long Total)>? progress, CancellationToken ct = default)
    {
        using var resp = await Direct.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
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
            progress?.Report((read, total));
        }
        progress?.Report((read, total > 0 ? total : read));
    }

    private static HttpClient CreateClient()
    {
        // 官方站点偶尔要跟随跳转，UA 必须给，否则 GitHub API 会拒
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NEXLauncher", "0.4.0"));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return c;
    }
}

public sealed class CuratedPick
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    public string Subtitle { get; init; } = "";
    public string Badge { get; init; } = "";
    public string Kind { get; init; } = "mod";
    public string Loader { get; init; } = "";
}

public sealed record CuratedFile(string Url, string FileName);
