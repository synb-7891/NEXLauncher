using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NEXLauncher.Services;

public sealed class ModrinthService
{
    private static readonly HttpClient Client = CreateClient();

    // 内容分发走 MCIM 镜像（z0z0r4 发起的公益镜像，国内可达）：直接替换域名即可
    private const string MirrorApiBase = "https://mod.mcimirror.top/modrinth";
    private const string OfficialApiBase = "https://api.modrinth.com";
    private const string CdnBase = "https://mod.mcimirror.top";

    /// <summary>是否使用国内镜像源（设置页「下载源」开关）；false 时直连官方。</summary>
    public static bool UseMirror { get; set; } = true;

    /// <summary>当前首选 API 根地址（随「下载源」设置切换）。</summary>
    private static string ApiBase => UseMirror ? MirrorApiBase : OfficialApiBase;

    /// <summary>备用 API 根地址：首选源失败时自动回落到另一端，保证国内/外网络都可用。</summary>
    private static string FallbackApiBase => UseMirror ? OfficialApiBase : MirrorApiBase;

    /// <summary>把 Modrinth 官方 CDN 链接改写为镜像链接（图标、文件下载共用）。</summary>
    public static string MirrorUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return url ?? "";
        if (!UseMirror) return url;
        return url.Replace("https://cdn.modrinth.com", CdnBase)
                  .Replace("https://api.modrinth.com", ApiBase);
    }

    // 内存缓存：同一签名不重复请求
    private static readonly Dictionary<string, ModrinthSearchPage> Cache = new();

    public async Task<IReadOnlyList<ModrinthProject>> SearchAsync(
        string query,
        string projectType,
        string? category = null,
        IEnumerable<string>? gameVersions = null,
        string index = "downloads",
        CancellationToken cancellationToken = default)
    {
        var page = await SearchPagedAsync(query, projectType, category,
            gameVersions?.ToList(), null, index, 0, 24, cancellationToken);
        return page.Hits;
    }

    /// <summary>获取项目最新版本的下载地址和文件名（按发布时间取最新）。</summary>
    public async Task<(string Url, string FileName)?> GetLatestDownloadAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var versions = await GetVersionsAsync(projectId, cancellationToken);
        var first = versions
            .Where(v => v.Files.Count > 0)
            .OrderByDescending(v => v.DatePublished)
            .FirstOrDefault();
        if (first == null) return null;
        var file = first.PrimaryFile ?? first.Files[0];
        return (file.Url, file.FileName);
    }

    /// <summary>
    /// 取「指定的那一个版本」（PCL2 式版本选择弹窗选完后用）；versionId 为空或找不到时退回最新版本。
    /// 指定版本走 /v2/version/{id} 单条查询，不拉整个项目的版本列表（Sodium 这种 200+ 版本会慢十几秒）。
    /// </summary>
    public async Task<ModrinthVersion?> GetVersionAsync(string projectId, string? versionId = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            try
            {
                var one = await GetStringWithFallbackAsync(
                    $"/v2/version/{Uri.EscapeDataString(versionId)}", 15, cancellationToken);
                var v = JsonSerializer.Deserialize<ModrinthVersion>(one);
                if (v != null && v.Files.Count > 0) return v;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { /* 单条查询失败退回列表 */ }
        }

        var versions = await GetVersionsAsync(projectId, cancellationToken);
        var withFiles = versions.Where(v => v.Files.Count > 0).ToList();
        if (withFiles.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(versionId))
        {
            var hit = withFiles.FirstOrDefault(v => v.Id == versionId);
            if (hit != null) return hit;
        }
        return withFiles.OrderByDescending(v => v.DatePublished).First();
    }

    /// <summary>
    /// 查询必需前置依赖（弹窗提醒用）。可按「选定文件版本 + 目标游戏版本 + 加载器」精确挑选前置文件，
    /// 避免给 1.21.11 的模组配上 26.3 的前置；单个前置查询失败自动跳过。
    /// </summary>
    public async Task<List<ModrinthDepInfo>> GetRequiredDependenciesAsync(string projectId,
        string? versionId = null, string? gameVersion = null, string? loader = null,
        CancellationToken cancellationToken = default)
    {
        var picked = await GetVersionAsync(projectId, versionId, cancellationToken);
        if (picked == null) return [];

        var result = new List<ModrinthDepInfo>();
        foreach (var dep in picked.Dependencies
                     .Where(d => d.DependencyType == "required" && !string.IsNullOrEmpty(d.ProjectId)))
        {
            try
            {
                var proj = await GetProjectAsync(dep.ProjectId, cancellationToken);
                var dl = await PickDownloadAsync(dep.ProjectId, gameVersion, loader, cancellationToken)
                         ?? await GetLatestDownloadAsync(dep.ProjectId, cancellationToken);
                if (dl == null) continue;
                result.Add(new ModrinthDepInfo
                {
                    ProjectId = dep.ProjectId,
                    Title = proj?.Title ?? dep.ProjectId,
                    IconUrl = proj?.IconUrl ?? "",
                    Url = dl.Value.Url,
                    FileName = dl.Value.FileName
                });
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested) { }
        }
        return result;
    }

    /// <summary>在项目里挑一个「匹配目标游戏版本 + 加载器」的最新文件；没有完全匹配就退回该加载器最新。</summary>
    private async Task<(string Url, string FileName)?> PickDownloadAsync(string projectId,
        string? gameVersion, string? loader, CancellationToken cancellationToken)
    {
        // 先让服务端按 游戏版本 + 加载器 过滤（快），拿不到再退回全量
        var versions = await GetVersionsFilteredAsync(projectId, gameVersion, loader, cancellationToken);
        if (versions.Count == 0)
            versions = await GetVersionsAsync(projectId, cancellationToken);
        var withFiles = versions.Where(v => v.Files.Count > 0).ToList();
        if (withFiles.Count == 0) return null;

        IEnumerable<ModrinthVersion> cand = withFiles;
        if (!string.IsNullOrWhiteSpace(loader) &&
            !loader.Equals("原版", StringComparison.OrdinalIgnoreCase))
        {
            var lid = loader.ToLowerInvariant();
            var byLoader = withFiles
                .Where(v => v.Loaders.Any(l => l.Equals(lid, StringComparison.OrdinalIgnoreCase)))
                .ToList();
            if (byLoader.Count > 0) cand = byLoader;
        }
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            var byGame = cand.Where(v => v.GameVersions.Contains(gameVersion)).ToList();
            if (byGame.Count > 0) cand = byGame;
        }

        var best = cand.OrderByDescending(v => v.DatePublished).First();
        var file = best.PrimaryFile ?? best.Files[0];
        return (file.Url, file.FileName);
    }

    /// <summary>MCIM 提供的项目简介中文翻译（仅镜像端有此接口，与「下载源」设置无关）。失败返回 null。</summary>
    public async Task<string?> GetTranslatedSummaryAsync(string projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = $"https://mod.mcimirror.top/translate/modrinth?project_id={Uri.EscapeDataString(projectId)}";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var resp = await Client.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("translated", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch { /* 翻译失败回退英文简介 */ }
        return null;
    }

    /// <summary>把中文搜索词翻译成英文（MyMemory 免费接口），失败返回 null。</summary>
    public static async Task<string?> TranslateQueryToEnglishAsync(string query, CancellationToken cancellationToken = default)
    {
        try
        {
            var url = "https://api.mymemory.translated.net/get?q="
                      + Uri.EscapeDataString(query) + "&langpair=zh-CN|en-US";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(12));
            using var resp = await Client.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync(cts.Token);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("responseData", out var rd)
                && rd.TryGetProperty("translatedText", out var t) && t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString()?.Trim();
                if (!string.IsNullOrEmpty(s) && !s.StartsWith("MYMEMORY", StringComparison.OrdinalIgnoreCase))
                    return s;
            }
        }
        catch { /* 翻译失败则用原始关键词 */ }
        return null;
    }

    /// <summary>字符串是否包含中文（用于决定是否走翻译搜索）。</summary>
    public static bool ContainsCjk(string text) => text.Any(c => c is >= '一' and <= '鿿');

    /// <summary>分页搜索：返回命中列表与总数（PCL2 风格列表需要总数算页数）。</summary>
    public async Task<ModrinthSearchPage> SearchPagedAsync(
        string query,
        string projectType,
        string? category,
        IReadOnlyCollection<string>? gameVersions,
        IReadOnlyCollection<string>? loaders,
        string index,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        var gv = gameVersions?.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList() ?? [];
        var ld = loaders?.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList() ?? [];

        var cacheKey = $"{projectType}|{category}|{index}|{query}|{string.Join(",", gv)}|{string.Join(",", ld)}|{offset}|{limit}";
        if (Cache.TryGetValue(cacheKey, out var cached))
            return cached;

        // facets：外层 AND、内层 OR
        var facetList = new List<string[]> { new[] { $"project_type:{projectType}" } };
        if (!string.IsNullOrEmpty(category))
            facetList.Add(new[] { $"categories:{category}" });
        if (gv.Count > 0)
            facetList.Add(gv.Select(v => $"versions:{v}").ToArray());
        if (ld.Count > 0)
            facetList.Add(ld.Select(l => $"categories:{l}").ToArray());
        var facets = JsonSerializer.Serialize(facetList);

        var url = $"/v2/search?query={Uri.EscapeDataString(query)}"
                  + $"&facets={Uri.EscapeDataString(facets)}&index={index}"
                  + $"&offset={Math.Max(0, offset)}&limit={Math.Clamp(limit, 1, 50)}";

        var json = await GetStringWithFallbackAsync(url, 20, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("hits", out var hits))
            return new ModrinthSearchPage();

        var total = root.TryGetProperty("total_hits", out var th) && th.TryGetInt32(out var t) ? t : 0;
        var result = hits.Deserialize<List<ModrinthProject>>() ?? [];
        var page = new ModrinthSearchPage { Hits = result, TotalHits = Math.Max(total, result.Count) };
        if (Cache.Count > 120) Cache.Clear();
        Cache[cacheKey] = page;
        return page;
    }

    /// <summary>项目详情：简介正文、图库、关注数、来源链接等。</summary>
    public async Task<ModrinthProjectDetail?> GetProjectAsync(string projectId, CancellationToken cancellationToken = default)
    {
        var json = await GetStringWithFallbackAsync(
            $"/v2/project/{Uri.EscapeDataString(projectId)}", 20, cancellationToken);
        return JsonSerializer.Deserialize<ModrinthProjectDetail>(json);
    }

    /// <summary>项目的全部版本（含每个版本的游戏版本、加载器、文件）。</summary>
    public Task<List<ModrinthVersion>> GetVersionsAsync(string projectId, CancellationToken cancellationToken = default)
        => GetVersionsFilteredAsync(projectId, null, null, cancellationToken);

    /// <summary>
    /// 按「游戏版本 + 加载器」让服务端先筛（Modrinth 支持 game_versions / loaders 查询参数），
    /// 避免像 Sodium 这种 200+ 版本的项目拉全量 JSON 卡十几秒。带 3 分钟内存缓存。
    /// </summary>
    public async Task<List<ModrinthVersion>> GetVersionsFilteredAsync(string projectId,
        string? gameVersion, string? loader, CancellationToken cancellationToken = default)
    {
        var key = $"{projectId}|{gameVersion}|{loader}";
        if (VersionCache.TryGetValue(key, out var hit) && (DateTime.UtcNow - hit.At).TotalMinutes < 3)
            return hit.List;

        var q = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
            q.Add("game_versions=" + Uri.EscapeDataString($"[\"{gameVersion}\"]"));
        if (!string.IsNullOrWhiteSpace(loader))
            q.Add("loaders=" + Uri.EscapeDataString($"[\"{loader}\"]"));
        var rel = $"/v2/project/{Uri.EscapeDataString(projectId)}/version" +
                  (q.Count > 0 ? "?" + string.Join("&", q) : "");

        var json = await GetStringWithFallbackAsync(rel, 25, cancellationToken);
        var list = JsonSerializer.Deserialize<List<ModrinthVersion>>(json) ?? [];

        if (VersionCache.Count > 80) VersionCache.Clear();
        VersionCache[key] = (DateTime.UtcNow, list);
        return list;
    }

    private static readonly Dictionary<string, (DateTime At, List<ModrinthVersion> List)> VersionCache = new();

    /// <summary>
    /// 首选源请求失败（超时/断连/非 2xx/解析前错误）时自动用备用源重试一次；
    /// 只有两端都失败才把异常抛给调用方。调用方主动取消时不回落，直接传播取消。
    /// </summary>
    private static async Task<string> GetStringWithFallbackAsync(string relative, int timeoutSec, CancellationToken cancellationToken)
    {
        try
        {
            return await GetStringAsync(ApiBase + relative, timeoutSec, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return await GetStringAsync(FallbackApiBase + relative, timeoutSec, cancellationToken);
        }
    }

    private static async Task<string> GetStringAsync(string url, int timeoutSec, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
        using var resp = await Client.GetAsync(url, cts.Token);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(cts.Token);
    }

    /// <summary>下载文件到指定目录，可通过 byteProgress 回报（已下载字节, 总字节）。
    /// 镜像会 302 回源站，源站不可达时自动回落官方 CDN。</summary>
    public async Task DownloadFileAsync(string rawUrl, string destPath,
        IProgress<(long Received, long Total)>? byteProgress = null, CancellationToken cancellationToken = default)
    {
        try
        {
            await DownloadFromAsync(MirrorUrl(rawUrl), destPath, byteProgress, cancellationToken);
        }
        catch (Exception)
        {
            // 镜像/源站链路失败 → 官方 CDN 直连
            await DownloadFromAsync(rawUrl, destPath, byteProgress, cancellationToken);
        }
    }

    private static async Task DownloadFromAsync(string fileUrl, string destPath,
        IProgress<(long Received, long Total)>? byteProgress, CancellationToken cancellationToken)
    {
        using var resp = await Client.GetAsync(fileUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(cancellationToken);
        await using var dst = File.Create(destPath);
        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, cancellationToken)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), cancellationToken);
            read += n;
            byteProgress?.Report((read, total));
        }
        byteProgress?.Report((read, total > 0 ? total : read));
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NEXLauncher", "0.4.0"));
        return client;
    }
}

public sealed class ModrinthProject
{
    [JsonPropertyName("project_id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("slug")]
    public string Slug { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("project_type")]
    public string ProjectType { get; init; } = string.Empty;

    [JsonPropertyName("downloads")]
    public long Downloads { get; init; }

    [JsonPropertyName("author")]
    public string Author { get; init; } = string.Empty;

    [JsonPropertyName("icon_url")]
    public string? IconUrl { get; init; }

    /// <summary>Modrinth 分类，模组类项目的加载器（forge/fabric/quilt/neoforge）也在其中。</summary>
    [JsonPropertyName("categories")]
    public List<string> Categories { get; init; } = [];

    /// <summary>支持的版本 ID 列表（搜索接口直接返回，如 1.20.1、1.19.4…）。</summary>
    [JsonPropertyName("versions")]
    public List<string> Versions { get; init; } = [];

    /// <summary>最近更新时间。</summary>
    [JsonPropertyName("date_modified")]
    public DateTimeOffset DateModified { get; init; }

    private static readonly HashSet<string> KnownLoaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "forge", "neoforge", "fabric", "quilt", "liteloader", "rift"
    };

    /// <summary>该项目支持的模组加载器列表（规范化为首字母大写显示名）。</summary>
    public IReadOnlyList<string> Loaders => Categories
        .Where(c => KnownLoaders.Contains(c))
        .Select(c => c.Equals("neoforge", StringComparison.OrdinalIgnoreCase) ? "NeoForge"
            : char.ToUpperInvariant(c[0]) + c[1..].ToLowerInvariant())
        .Distinct()
        .ToList();
}

/// <summary>一页搜索结果：命中列表 + 总命中数（用于计算总页数）。</summary>
public sealed class ModrinthSearchPage
{
    public List<ModrinthProject> Hits { get; init; } = [];
    public int TotalHits { get; init; }
}

/// <summary>项目详情（/v2/project/{id}）。</summary>
public sealed class ModrinthProjectDetail
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("slug")] public string Slug { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("body")] public string Body { get; init; } = "";
    [JsonPropertyName("icon_url")] public string? IconUrl { get; init; }
    [JsonPropertyName("downloads")] public long Downloads { get; init; }
    [JsonPropertyName("followers")] public int Followers { get; init; }
    [JsonPropertyName("categories")] public List<string> Categories { get; init; } = [];
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; init; } = [];
    [JsonPropertyName("loaders")] public List<string> Loaders { get; init; } = [];
    [JsonPropertyName("gallery")] public List<ModrinthGalleryImage> Gallery { get; init; } = [];
    [JsonPropertyName("source_url")] public string? SourceUrl { get; init; }
    [JsonPropertyName("issues_url")] public string? IssuesUrl { get; init; }
    [JsonPropertyName("wiki_url")] public string? WikiUrl { get; init; }
    [JsonPropertyName("published")] public DateTimeOffset Published { get; init; }
    [JsonPropertyName("updated")] public DateTimeOffset Updated { get; init; }
}

public sealed class ModrinthGalleryImage
{
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("featured")] public bool Featured { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
}

/// <summary>项目的一个版本（含文件列表）。</summary>
public sealed class ModrinthVersion
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; init; } = "";
    [JsonPropertyName("version_type")] public string VersionType { get; init; } = "release";
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; init; } = [];
    [JsonPropertyName("loaders")] public List<string> Loaders { get; init; } = [];
    [JsonPropertyName("date_published")] public DateTimeOffset DatePublished { get; init; }
    [JsonPropertyName("downloads")] public long Downloads { get; init; }
    [JsonPropertyName("files")] public List<ModrinthFile> Files { get; init; } = [];
    [JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; init; } = [];

    public ModrinthFile? PrimaryFile =>
        Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("project_id")] public string ProjectId { get; init; } = "";
    [JsonPropertyName("version_id")] public string? VersionId { get; init; }
    [JsonPropertyName("dependency_type")] public string DependencyType { get; init; } = "required";
}

/// <summary>前置依赖的展示信息（弹窗用）：项目名 + 图标 + 最新文件。</summary>
public sealed class ModrinthDepInfo
{
    public string ProjectId { get; init; } = "";
    public string Title { get; init; } = "";
    public string IconUrl { get; init; } = "";
    public string FileName { get; init; } = "";
    public string Url { get; init; } = "";
}

public sealed class ModrinthFile
{
    [JsonPropertyName("url")] public string Url { get; init; } = "";
    [JsonPropertyName("filename")] public string FileName { get; init; } = "";
    [JsonPropertyName("primary")] public bool Primary { get; init; }
    [JsonPropertyName("size")] public long Size { get; init; }
}
