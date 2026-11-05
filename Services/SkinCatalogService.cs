using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NEXLauncher.Services;

/// <summary>皮肤库里的一个皮肤条目。</summary>
public record SkinItem(
    int Id,
    string ImageUrl,
    bool IsSlim,
    string Color,
    List<string> Tags,
    int Wearers);

/// <summary>
/// 皮肤目录服务：接入 Ely.by 公开皮肤库（共 10000 个 / 接口 250 页 × 40 个）。
/// 对用户展示为 1000 页 × 每页 10 个：把接口的每一页切成 4 个展示页，
/// 正好覆盖皮肤库全部内容。
/// 接口：GET https://ely.by/api/legacy/skins?page=N
/// </summary>
public sealed class SkinCatalogService
{
    /// <summary>提供给用户的最大页数（每页 10 个皮肤，1~1000 页）。</summary>
    public const int MaxPages = 1000;

    /// <summary>每一展示页显示的皮肤数。</summary>
    public const int PageSize = 10;

    /// <summary>接口每页固定返回 40 个（limit 参数被接口忽略）。</summary>
    private const int ApiPageSize = 40;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    // 接口页缓存：key = "接口页码|分类"，同一接口页不重复请求
    private static readonly Dictionary<string, IReadOnlyList<SkinItem>> Cache = new();

    /// <summary>皮肤分类（对应 ely.by 的 kind 参数）。</summary>
    public static readonly (string Key, string Label)[] Kinds =
    {
        ("", "全部"),
        ("Comics", "漫画"),
        ("Adventure", "冒险"),
        ("Heroes", "英雄"),
        ("Evildoers", "反派"),
        ("Weekend", "休闲"),
        ("Characters", "角色"),
        ("Historical", "历史"),
        ("Fantasy", "奇幻"),
        ("Scientific", "科幻"),
        ("Other", "其他"),
    };

    /// <summary>获取一页皮肤；page 从 1 开始，最大 MaxPages。kind 为空表示全部分类。</summary>
    public async Task<IReadOnlyList<SkinItem>> GetPageAsync(int page, string kind = "",
        CancellationToken ct = default)
    {
        page = Math.Clamp(page, 1, MaxPages);
        // 展示页 → 接口页换算：接口每页 40 个，展示每页 10 个，一个接口页切 4 个展示页
        var apiPage = (page + PageSize - 1) / (ApiPageSize / PageSize);
        var slice = (page - 1) % (ApiPageSize / PageSize);
        var key = $"{apiPage}|{kind}";
        IReadOnlyList<SkinItem> apiItems;
        IReadOnlyList<SkinItem>? cached;
        lock (Cache) Cache.TryGetValue(key, out cached);
        if (cached is not null)
        {
            apiItems = cached;
        }
        else
        {
            apiItems = await FetchApiPageAsync(apiPage, kind, ct);
            lock (Cache) Cache[key] = apiItems;
        }

        var start = slice * PageSize;
        if (start >= apiItems.Count) return Array.Empty<SkinItem>();
        return apiItems.Skip(start).Take(PageSize).ToList();
    }

    /// <summary>请求一个接口页（40 个皮肤），完成黑名单过滤。</summary>
    private async Task<IReadOnlyList<SkinItem>> FetchApiPageAsync(int apiPage, string kind,
        CancellationToken ct)
    {
        var url = $"https://ely.by/api/legacy/skins?page={apiPage}&limit={ApiPageSize}";
        if (!string.IsNullOrEmpty(kind)) url += $"&kind={Uri.EscapeDataString(kind)}";

        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var result = new List<SkinItem>();
        if (root.TryGetProperty("items", out var items))
        {
            foreach (var it in items.EnumerateArray())
            {
                var skinUrl = it.TryGetProperty("skin_url", out var su) ? su.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(skinUrl)) continue;

                var tags = new List<string>();
                if (it.TryGetProperty("tags", out var tg) && tg.ValueKind == JsonValueKind.Array)
                    foreach (var t in tg.EnumerateArray())
                        tags.Add(t.GetString() ?? "");

                // 内容安全：标签命中黑名单（包含式匹配，如 "Adolf Hitler" 命中 "adolf"）的皮肤直接跳过
                if (tags.Any(t => IsBlockedTag(t))) continue;

                result.Add(new SkinItem(
                    it.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                    skinUrl,
                    it.TryGetProperty("is_slim", out var slim) && slim.GetBoolean(),
                    it.TryGetProperty("color", out var color) ? color.GetString() ?? "#FFFFFF" : "#FFFFFF",
                    tags,
                    it.TryGetProperty("count_wearers", out var wearers) ? wearers.GetInt32() : 0));
            }
        }

        return result;
    }

    /// <summary>下载皮肤 PNG 到临时文件，返回本地路径。</summary>
    public async Task<string> DownloadSkinAsync(SkinItem item, CancellationToken ct = default)
    {
        var bytes = await Http.GetByteArrayAsync(item.ImageUrl, ct);
        var path = Path.Combine(Path.GetTempPath(), "NEXLauncher", $"skin_{item.Id}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, bytes, ct);
        return path;
    }

    /// <summary>明显不当内容的标签黑名单（小写）。</summary>
    private static readonly HashSet<string> SkinTagBlocklist = new()
    {
        "porn", "pornhub", "nude", "nudes", "nsfw", "sex", "xxx", "hitler", "adolf", "nazi",
        "isis", "pornographic", "hentai", "boobs", "naked", "18+", "r18"
    };

    /// <summary>包含式匹配：标签里出现任一黑名单词即拦截（如 "Adolf Hitler" 命中 "adolf"）。</summary>
    private static bool IsBlockedTag(string tag)
    {
        var t = tag.ToLowerInvariant();
        return SkinTagBlocklist.Any(b => t.Contains(b));
    }
}
