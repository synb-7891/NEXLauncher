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
/// 皮肤目录服务：接入 Ely.by 公开皮肤库（10000+ 皮肤），启动器内固定提供 20 页浏览。
/// 接口：GET https://ely.by/api/legacy/skins?page=N
/// </summary>
public sealed class SkinCatalogService
{
    /// <summary>提供给用户的最大页数（每页 40 个）。</summary>
    public const int MaxPages = 20;
    public const int PageSize = 40;

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    // 页码缓存：同一页不重复请求
    private static readonly Dictionary<int, IReadOnlyList<SkinItem>> Cache = new();

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
        var key = page * 100 + (kind?.GetHashCode() % 100 ?? 0);
        if (Cache.TryGetValue(key, out var cached)) return cached;

        var url = $"https://ely.by/api/legacy/skins?page={page}&limit={PageSize}";
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

                // 内容安全：跳过标签明显不当的皮肤
                if (tags.Any(t => SkinTagBlocklist.Contains(t.ToLowerInvariant()))) continue;

                result.Add(new SkinItem(
                    it.TryGetProperty("id", out var id) ? id.GetInt32() : 0,
                    skinUrl,
                    it.TryGetProperty("is_slim", out var slim) && slim.GetBoolean(),
                    it.TryGetProperty("color", out var color) ? color.GetString() ?? "#FFFFFF" : "#FFFFFF",
                    tags,
                    it.TryGetProperty("count_wearers", out var wearers) ? wearers.GetInt32() : 0));
            }
        }

        Cache[key] = result;
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
}
