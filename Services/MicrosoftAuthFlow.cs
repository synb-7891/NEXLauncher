using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CmlLib.Core.Auth;

namespace NEXLauncher.Services;

/// <summary>
/// 微软正版登录 OAuth 流程（自家实现，替代 CmlLib 内置 WinForms 登录窗口——
/// 那个窗口在本机 WebView2 环境下会白屏）。
/// 链路：MSA OAuth → Xbox Live (XBL) → XSTS → Minecraft 认证 → 拉取玩家档案。
/// </summary>
public static class MicrosoftAuthFlow
{
    // CmlLib / 开源启动器社区通用的 Mojang 官方 Xbox 客户端标识
    private const string ClientId = "00000000402b5328";
    private const string RedirectUri = "https://login.live.com/oauth20_desktop.srf";
    private const string Scope = "service::user.auth.xboxlive.com::MBI_SSL";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static readonly string AuthorizeUrl =
        "https://login.live.com/oauth20_authorize.srf" +
        "?client_id=" + ClientId +
        "&response_type=code" +
        "&redirect_uri=" + Uri.EscapeDataString(RedirectUri) +
        "&scope=" + Uri.EscapeDataString(Scope) +
        "&prompt=login&locale=zh-CN";

    public static string? ExtractCode(string url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        if (!url.StartsWith("https://login.live.com/oauth20_desktop.srf", StringComparison.OrdinalIgnoreCase))
            return null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            var code = q["code"];
            if (!string.IsNullOrWhiteSpace(code)) return code;
        }
        return null;
    }

    private static async Task<Dictionary<string, object?>> PostFormAsync(Dictionary<string, string> form)
    {
        using var content = new FormUrlEncodedContent(form);
        using var resp = await Http.PostAsync("https://login.live.com/oauth20_token.srf", content);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception("微软令牌交换失败 (" + (int)resp.StatusCode + ")");
        return JsonSerializer.Deserialize<Dictionary<string, object?>>(body)
               ?? throw new Exception("微软令牌响应解析失败");
    }

    /// <summary>授权码 → MSA 令牌（access_token + refresh_token）。</summary>
    public static async Task<(string AccessToken, string RefreshToken)> ExchangeCodeAsync(string code)
    {
        var data = await PostFormAsync(new Dictionary<string, string>
        {
            ["client_id"] = ClientId,
            ["code"] = code,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = RedirectUri
        });
        var access = data.TryGetValue("access_token", out var a) ? a?.ToString() : null;
        var refresh = data.TryGetValue("refresh_token", out var r) ? r?.ToString() : "";
        if (string.IsNullOrWhiteSpace(access)) throw new Exception("微软未返回访问令牌");
        return (access, refresh ?? "");
    }

    /// <summary>刷新令牌 → 新 MSA 令牌（用于启动时静默恢复登录）。失败返回 null（需重新登录）。</summary>
    public static async Task<(string AccessToken, string RefreshToken)?> RefreshAsync(string refreshToken)
    {
        try
        {
            var data = await PostFormAsync(new Dictionary<string, string>
            {
                ["client_id"] = ClientId,
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["scope"] = Scope
            });
            var access = data.TryGetValue("access_token", out var a) ? a?.ToString() : null;
            var refresh = data.TryGetValue("refresh_token", out var r) ? r?.ToString() : refreshToken;
            if (string.IsNullOrWhiteSpace(access)) return null;
            return (access, refresh ?? refreshToken);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<JsonElement> PostJsonAsync(string url, string json)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await Http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception("认证服务返回 " + (int)resp.StatusCode);
        return JsonSerializer.Deserialize<JsonElement>(body);
    }

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    /// <summary>MSA 令牌 → 完整 Minecraft 会话（XBL → XSTS → MC → Profile）。</summary>
    public static async Task<(MSession Session, string RefreshToken)> BuildSessionAsync(
        string msaToken, string refreshToken)
    {
        // 1) Xbox Live 用户令牌
        var xblJson = await PostJsonAsync(
            "https://user.auth.xboxlive.com/user/authenticate",
            "{\"Properties\":{\"AuthMethod\":\"RPS\",\"SiteName\":\"user.auth.xboxlive.com\",\"RpsTicket\":\"d=" +
            msaToken + "\"}," +
            "\"RelyingParty\":\"http://auth.xboxlive.com\",\"TokenType\":\"JWT\"}");
        var xblToken = Str(xblJson, "Token");
        var uhs = "";
        if (xblJson.TryGetProperty("DisplayClaims", out var dc) &&
            dc.TryGetProperty("xui", out var xui) && xui.ValueKind == JsonValueKind.Array &&
            xui.GetArrayLength() > 0)
            uhs = Str(xui[0], "uhs");
        if (string.IsNullOrWhiteSpace(xblToken) || string.IsNullOrWhiteSpace(uhs))
            throw new Exception("Xbox Live 认证失败");

        // 2) XSTS 令牌
        var xstsJson = await PostJsonAsync(
            "https://xsts.auth.xboxlive.com/xsts/authorize",
            "{\"Properties\":{\"SandboxId\":\"RETAIL\",\"UserTokens\":[\"" + xblToken + "\"]}," +
            "\"RelyingParty\":\"rp://api.minecraftservices.com/\",\"TokenType\":\"JWT\"}");
        var xstsToken = Str(xstsJson, "Token");
        if (string.IsNullOrWhiteSpace(xstsToken))
        {
            // 2148916238 = 账号未满 18 岁 / 未加入家庭组等常见情况
            var code = xstsJson.TryGetProperty("XErr", out var xerr) ? xerr.GetInt64() : 0L;
            throw new Exception(code switch
            {
                2148916233 => "该微软账号没有 Xbox 主机档案，请先在 xbox.com 免费创建",
                2148916238 => "该账号是未成年人，需要先加入家庭组",
                _ => "XSTS 认证失败 (" + code + ")"
            });
        }

        // 3) Minecraft 登录
        var mcJson = await PostJsonAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            "{\"identityToken\":\"XBL3.0 x=" + uhs + ";" + xstsToken + "\"}");
        var mcToken = Str(mcJson, "access_token");
        if (string.IsNullOrWhiteSpace(mcToken)) throw new Exception("Minecraft 登录失败");

        // 4) 玩家档案
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new Exception("该微软账号尚未购买 Minecraft: Java Edition");
        var profile = JsonSerializer.Deserialize<JsonElement>(await resp.Content.ReadAsStringAsync());
        var name = Str(profile, "name");
        var id = Str(profile, "id");
        if (string.IsNullOrWhiteSpace(name)) throw new Exception("档案为空：该账号可能没有 Minecraft 档案");

        var session = new MSession
        {
            Username = name,
            AccessToken = mcToken,
            UUID = id,
            UserType = "msa"
        };
        return (session, refreshToken);
    }
}
