using System.Net;
using System.Net.Http;

namespace NEXLauncher.Services;

/// <summary>
/// 下载镜像加速：
/// 把 Mojang / Fabric / Modrinth 官方下载地址按已验证的映射规则重写到国内镜像（BMCLAPI / MCIM），
/// 使版本 JSON、client.jar、libraries、assets、Fabric/Quilt meta、Modrinth CDN 全部直连国内，避免官方源卡死。
///
/// 映射规则（2026-11 实测全部 200 可用）：
///   launchermeta.mojang.com          → bmclapi2.bangbang93.com           （路径原样保留）
///   piston-meta.mojang.com           → bmclapi2.bangbang93.com           （路径原样保留）
///   piston-data.mojang.com           → bmclapi2.bangbang93.com           （路径原样保留，/v1/objects/&lt;sha1&gt;/…）
///   launcher.mojang.com              → bmclapi2.bangbang93.com
///   libraries.minecraft.net          → bmclapi2.bangbang93.com/maven
///   resources.download.minecraft.net → bmclapi2.bangbang93.com/assets
///   meta.fabricmc.net                → bmclapi2.bangbang93.com/fabric-meta
///   meta.quiltmc.org                 → bmclapi2.bangbang93.com/quilt-meta
///   maven.fabricmc.net               → bmclapi2.bangbang93.com/maven
///   cdn.modrinth.com                 → mod.mcimirror.top                 （Modrinth CDN，MCIM 镜像）
///   api.modrinth.com                 → mod.mcimirror.top/modrinth
/// </summary>
public static class MirrorHttp
{
    private const string BmclApi = "https://bmclapi2.bangbang93.com";
    private const string Mcim = "https://mod.mcimirror.top";

    /// <summary>是否启用镜像（跟随设置里的「下载镜像加速」）。</summary>
    public static bool UseMirror => ModrinthService.UseMirror;

    /// <summary>
    /// 把官方源 URL 重写为镜像 URL；不匹配任何规则时原样返回。
    /// </summary>
    public static string? Rewrite(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (url.StartsWith("https://launchermeta.mojang.com/", StringComparison.Ordinal))
            return BmclApi + url["https://launchermeta.mojang.com".Length..];
        if (url.StartsWith("https://piston-meta.mojang.com/", StringComparison.Ordinal))
            return BmclApi + url["https://piston-meta.mojang.com".Length..];
        if (url.StartsWith("https://piston-data.mojang.com/", StringComparison.Ordinal))
            return BmclApi + url["https://piston-data.mojang.com".Length..];
        if (url.StartsWith("https://launcher.mojang.com/", StringComparison.Ordinal))
            return BmclApi + url["https://launcher.mojang.com".Length..];
        if (url.StartsWith("https://libraries.minecraft.net/", StringComparison.Ordinal))
            return BmclApi + "/maven/" + url["https://libraries.minecraft.net/".Length..];
        if (url.StartsWith("https://resources.download.minecraft.net/", StringComparison.Ordinal))
            return BmclApi + "/assets/" + url["https://resources.download.minecraft.net/".Length..];
        if (url.StartsWith("https://meta.fabricmc.net/", StringComparison.Ordinal))
            return BmclApi + "/fabric-meta/" + url["https://meta.fabricmc.net/".Length..];
        if (url.StartsWith("https://meta.quiltmc.org/", StringComparison.Ordinal))
            return BmclApi + "/quilt-meta/" + url["https://meta.quiltmc.org/".Length..];
        if (url.StartsWith("https://maven.fabricmc.net/", StringComparison.Ordinal))
            return BmclApi + "/maven/" + url["https://maven.fabricmc.net/".Length..];
        if (url.StartsWith("https://maven.minecraftforge.net/", StringComparison.Ordinal))
            return BmclApi + "/maven/" + url["https://maven.minecraftforge.net/".Length..];
        if (url.StartsWith("https://cdn.modrinth.com/", StringComparison.Ordinal))
            return Mcim + url["https://cdn.modrinth.com".Length..];
        if (url.StartsWith("https://api.modrinth.com/", StringComparison.Ordinal))
            return Mcim + "/modrinth/" + url["https://api.modrinth.com/".Length..];
        return null;
    }

    /// <summary>创建启用了镜像重写的 HttpClient（镜像开关关闭时行为与普通 HttpClient 相同）。</summary>
    public static HttpClient CreateClient(TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        };
        HttpClient c;
        if (UseMirror)
            c = new HttpClient(new MirrorRedirectHandler(handler))
            {
                Timeout = timeout ?? TimeSpan.FromMinutes(30)
            };
        else
            c = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromMinutes(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("NEXLauncher/4.1 (+https://github.com/synb-7891/NEXLauncher)");
        return c;
    }

    /// <summary>
    /// 返回一个文件的候选下载地址列表：镜像开启时镜像优先、官方兜底；
    /// 关闭时官方优先、镜像兜底。给下载器做「主源失败自动换源」。
    /// </summary>
    public static List<string> Candidates(string url)
    {
        var list = new List<string>();
        if (string.IsNullOrEmpty(url)) return list;
        var mirror = Rewrite(url);
        if (mirror == null || mirror == url)
        {
            list.Add(url);
            return list;
        }
        if (UseMirror) { list.Add(mirror); list.Add(url); }
        else { list.Add(url); list.Add(mirror); }
        return list;
    }

    /// <summary>请求出站前把官方主机改写为镜像主机的 HttpMessageHandler。</summary>
    private sealed class MirrorRedirectHandler : DelegatingHandler
    {
        public MirrorRedirectHandler(HttpMessageHandler inner) : base(inner) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri != null)
            {
                var rewritten = Rewrite(request.RequestUri.ToString());
                if (rewritten != null && !string.Equals(rewritten, request.RequestUri.ToString(), StringComparison.Ordinal))
                    request.RequestUri = new Uri(rewritten);
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
