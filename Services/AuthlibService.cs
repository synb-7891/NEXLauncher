using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace NEXLauncher.Services;

/// <summary>
/// 外置登录（authlib-injector / Yggdrasil 协议）：支持 LittleSkin 等任意外置认证服务器。
/// 登录成功后用返回的 accessToken / UUID 组会话启动游戏，并注入
/// -javaagent:authlib-injector.jar=&lt;API Root&gt;。
/// </summary>
public sealed class AuthlibService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public sealed record ServerInfo(string ServerName, string ApiRoot);
    public sealed record AuthProfile(string Id, string Name);
    public sealed record LoginResult(string AccessToken, AuthProfile Profile, string ServerName);

    /// <summary>规范化 API Root：补协议、去尾部斜杠。</summary>
    public static string NormalizeApiRoot(string root)
    {
        root = root.Trim().TrimEnd('/');
        if (!root.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !root.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            root = "https://" + root;
        return root;
    }

    /// <summary>读取服务器元数据（GET API Root → meta.serverName），同时验证地址可用。</summary>
    public async Task<ServerInfo> FetchServerInfoAsync(string apiRoot)
    {
        apiRoot = NormalizeApiRoot(apiRoot);
        var json = await Http.GetStringAsync(apiRoot);
        using var doc = JsonDocument.Parse(json);
        var name = "外置服务器";
        if (doc.RootElement.TryGetProperty("meta", out var meta)
            && meta.TryGetProperty("serverName", out var sn))
            name = sn.GetString() ?? name;
        return new ServerInfo(name, apiRoot);
    }

    /// <summary>登录（POST /authserver/authenticate）。返回会话令牌与角色档案。</summary>
    public async Task<LoginResult> AuthenticateAsync(string apiRoot, string user, string password, string clientToken)
    {
        apiRoot = NormalizeApiRoot(apiRoot);
        // Yggdrasil 协议要求 JSON 请求体：agent 必须是对象、requestUser 必须是布尔值。
        // 之前误用表单编码会被服务器拒绝（"requestUser must be a boolean value, agent must be an object"）。
        var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["agent"] = new Dictionary<string, object?> { ["name"] = "Minecraft", ["version"] = 1 },
            ["username"] = user,
            ["password"] = password,
            ["clientToken"] = clientToken,
            ["requestUser"] = false
        });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync(apiRoot + "/authserver/authenticate", content);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            var msg = TryReadErrorMessage(body) ?? $"认证服务器返回 {(int)resp.StatusCode}（可能是账号或密码错误）";
            throw new Exception(msg);
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var accessToken = root.TryGetProperty("accessToken", out var at) ? at.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(accessToken))
            throw new Exception("认证服务器未返回 accessToken。");

        AuthProfile? profile = null;
        if (root.TryGetProperty("selectedProfile", out var sel) && sel.ValueKind == JsonValueKind.Object
            && sel.TryGetProperty("id", out _))
        {
            profile = ReadProfile(sel);
        }
        else if (root.TryGetProperty("availableProfiles", out var list) && list.ValueKind == JsonValueKind.Array
                 && list.GetArrayLength() > 0)
        {
            profile = ReadProfile(list[0]);
        }
        if (profile == null)
            throw new Exception("该账号下没有可用的游戏角色，请先在服务器官网创建角色。");

        return new LoginResult(accessToken, profile, await FetchServerNameSafeAsync(apiRoot));
    }

    private static AuthProfile ReadProfile(JsonElement el) => new(
        el.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
        el.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "");

    private static string? TryReadErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errorMessage", out var m))
                return m.GetString();
        }
        catch { }
        return null;
    }

    private async Task<string> FetchServerNameSafeAsync(string apiRoot)
    {
        try { return (await FetchServerInfoAsync(apiRoot)).ServerName; }
        catch { return "外置服务器"; }
    }

    /// <summary>读取角色皮肤 URL（GET /sessionserver/.../profile/{uuid} → textures 属性）。</summary>
    public async Task<string?> GetSkinUrlAsync(string apiRoot, string uuid)
    {
        try
        {
            apiRoot = NormalizeApiRoot(apiRoot);
            var json = await Http.GetStringAsync(
                $"{apiRoot}/sessionserver/session/minecraft/profile/{uuid}");
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("properties", out var props)) return null;
            foreach (var p in props.EnumerateArray())
            {
                if (p.TryGetProperty("name", out var n) && n.GetString() == "textures"
                    && p.TryGetProperty("value", out var v))
                {
                    var b64 = v.GetString();
                    if (string.IsNullOrEmpty(b64)) continue;
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                    using var tdoc = JsonDocument.Parse(decoded);
                    if (tdoc.RootElement.TryGetProperty("textures", out var tex)
                        && tex.TryGetProperty("SKIN", out var skin)
                        && skin.TryGetProperty("url", out var url))
                        return url.GetString();
                }
            }
        }
        catch { }
        return null;
    }

    /// <summary>给游戏启动用的 -javaagent 参数（指向真实外置认证服务器）。</summary>
    public static string? GetAgentArgument(string apiRoot)
    {
        if (string.IsNullOrWhiteSpace(apiRoot) || !LocalSkinServer.Instance.AgentReady) return null;
        return $"-javaagent:{LocalSkinServer.AgentJarPath}={NormalizeApiRoot(apiRoot)}";
    }
}
