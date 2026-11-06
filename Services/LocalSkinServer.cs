using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NEXLauncher.Services;

/// <summary>
/// 本地皮肤服务器：配合 authlib-injector，让离线账号也能显示自定义皮肤。
/// 原理：启动游戏时注入 -javaagent:authlib-injector.jar=http://127.0.0.1:端口，
/// 游戏向会话服务器查询皮肤时会被重定向到本服务器，由我们返回设置的皮肤。
/// 注意：1.19+ 客户端会校验 textures 属性签名，因此服务器用自带的 RSA 密钥对
/// 属性值签名，并把公钥通过 API 根元数据（signaturePublickey）告知 authlib-injector。
/// </summary>
public sealed class LocalSkinServer
{
    private static readonly Lazy<LocalSkinServer> _instance = new(() => new LocalSkinServer());
    public static LocalSkinServer Instance => _instance.Value;

    private HttpListener? _listener;
    private Thread? _thread;
    public int Port { get; private set; }
    public string? SkinPath { get; private set; }
    public string Model { get; private set; } = "wide"; // wide / slim
    public string PlayerName { get; private set; } = "Player";

    /// <summary>
    /// 皮肤的公网原始地址（来自 Ely.by 等皮肤库）。
    /// 有值时档案里的纹理直接指向这个公网地址——别的玩家客户端也能取到，
    /// 于是服务器里/局域网里别人也能看见你的皮肤（本地回环地址他们访问不到）。
    /// </summary>
    public string? PublicSkinUrl { get; private set; }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // 签名密钥（持久化到 tools/skin_signing.key，PEM 格式）
    private RSA? _rsa;
    private string? _publicKeyPem;

    // 皮肤内容指纹：拼到皮肤 URL 上做缓存穿透。
    // 客户端（1.19+）会按 URL 缓存贴图到 .minecraft/assets/skins，URL 不变就一直用旧图，
    // 换皮肤后不生效基本都是这个原因。
    private string _skinStamp = "0";

    // agent 下载失败后的冷却，避免每次启动都卡在网络请求上
    private DateTime _agentLastTry = DateTime.MinValue;

    /// <summary>authlib-injector 是否已就绪（jar 已下载）。</summary>
    public bool AgentReady => File.Exists(AgentJarPath);

    public static string AgentJarPath
    {
        get
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NEXLauncher", "tools");
            return Path.Combine(dir, "authlib-injector.jar");
        }
    }

    private static string SigningKeyPath =>
        Path.Combine(Path.GetDirectoryName(AgentJarPath)!, "skin_signing.key");

    /// <summary>启动服务器并设置皮肤。返回是否成功。</summary>
    public bool ApplySkin(string skinPath, string model, string playerName, string? publicUrl = null)
    {
        try
        {
            SkinPath = skinPath;
            Model = model == "slim" ? "slim" : "wide";
            PlayerName = playerName;
            PublicSkinUrl = NormalizePublicUrl(publicUrl);
            _skinStamp = Stamp(skinPath);
            EnsureKey();
            EnsureStarted();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>只接受真正的公网 http(s) 地址；空串/本地路径一律视为没有。</summary>
    private static string? NormalizePublicUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        if (u.Scheme != "http" && u.Scheme != "https") return null;
        var host = u.Host.ToLowerInvariant();
        if (host is "localhost" or "127.0.0.1" or "0.0.0.0") return null;
        if (host.EndsWith(".local") || host.EndsWith(".lan")) return null;
        // Ely.by 会给 http 地址，统一升 https（客户端对明文图片更严格）
        if (host.EndsWith("ely.by")) return "https://" + u.Host + u.PathAndQuery;
        return u.ToString();
    }

    /// <summary>皮肤内容指纹（长度 + 内容哈希），用于让客户端重新下载贴图。</summary>
    private static string Stamp(string skinPath)
    {
        try
        {
            if (!File.Exists(skinPath)) return "0";
            var bytes = File.ReadAllBytes(skinPath);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();
        }
        catch { return "0"; }
    }

    /// <summary>加载或生成签名密钥对（RSA-2048，PEM 持久化）。</summary>
    private void EnsureKey()
    {
        if (_rsa != null) return;
        _rsa = RSA.Create(2048);
        try
        {
            if (File.Exists(SigningKeyPath))
            {
                var pem = File.ReadAllText(SigningKeyPath);
                _rsa.ImportFromPem(pem);
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SigningKeyPath)!);
                File.WriteAllText(SigningKeyPath, _rsa.ExportPkcs8PrivateKeyPem());
            }
        }
        catch
        {
            // 密钥损坏：重新生成
            try { File.WriteAllText(SigningKeyPath, _rsa.ExportPkcs8PrivateKeyPem()); } catch { }
        }
        var spki = _rsa.ExportSubjectPublicKeyInfo();
        _publicKeyPem = "-----BEGIN PUBLIC KEY-----\n"
            + Convert.ToBase64String(spki, Base64FormattingOptions.InsertLineBreaks)
            + "\n-----END PUBLIC KEY-----";
    }

    /// <summary>确保服务器在运行（端口被占用时自动换下一个）。</summary>
    private void EnsureStarted()
    {
        if (_listener != null && _listener.IsListening) return;

        for (var port = 28810; port < 28840; port++)
        {
            try
            {
                _listener = new HttpListener();
                _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                _listener.Start();
                Port = port;
                break;
            }
            catch { _listener = null; }
        }
        if (_listener == null || !_listener.IsListening)
            throw new IOException("无法启动本地皮肤服务器（28810-28839 端口均被占用）");

        _thread = new Thread(ListenLoop) { IsBackground = true };
        _thread.Start();
    }

    /// <summary>记录一次请求，便于确认游戏是否真的来取皮肤（诊断「应用了但没变化」）。</summary>
    private static void LogHit(string path, string query)
    {
        try
        {
            var log = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NEXLauncher", "skin-server.log");
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {path}{query}\n";
            // 只保留最近 200 行，避免无限增长
            var fi = new FileInfo(log);
            if (fi.Exists && fi.Length > 64 * 1024)
                File.WriteAllText(log, "");
            File.AppendAllText(log, line);
        }
        catch { }
    }

    private void ListenLoop()
    {        var listener = _listener!;
        while (listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = listener.GetContext(); }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }

            try { Handle(ctx); }
            catch { try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { } }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        LogHit(path, ctx.Request.Url?.Query ?? "");

        // authlib-injector 启动时先请求根地址拿元数据：
        // 顶层 skinDomains + signaturePublickey（签名公钥），meta 里是展示信息
        if (path == "/")
        {
            var pub = (_publicKeyPem ?? "").Replace("\r\n", "\\n").Replace("\n", "\\n");
            // skinDomains 必须包含纹理所在域名，否则客户端会拒收这张贴图
            var domains = new List<string> { "127.0.0.1", "localhost" };
            if (!string.IsNullOrEmpty(PublicSkinUrl) && Uri.TryCreate(PublicSkinUrl, UriKind.Absolute, out var pu))
            {
                var h = pu.Host.ToLowerInvariant();
                if (!domains.Contains(h)) domains.Add(h);
            }
            var domainsJson = "[" + string.Join(",", domains.ConvertAll(d => "\"" + d + "\"")) + "]";
            WriteJson(ctx,
                "{\"skinDomains\":" + domainsJson + "," +
                "\"signaturePublickey\":\"" + pub + "\"," +
                "\"meta\":{\"serverName\":\"NEX Launcher Local Skin\"," +
                "\"implementationName\":\"nex-launcher\",\"implementationVersion\":\"0.4\"," +
                "\"feature.legacy_skin_api\":true,\"feature.no_mojang_namespace\":true}}");
            return;
        }

        // /sessionserver/session/minecraft/profile/<uuid>（可选 ?unsigned=false / .json 后缀）
        const string profilePrefix = "/sessionserver/session/minecraft/profile/";
        if (path.StartsWith(profilePrefix) && path.Length > profilePrefix.Length)
        {
            var uuid = path[profilePrefix.Length..].TrimEnd('/');
            if (uuid.Contains('.')) uuid = uuid.Split('.')[0];
            WriteJson(ctx, BuildProfileJson(uuid));
            return;
        }

        // /api/profiles/minecraft/<name> → 按名字查档案
        const string namePrefix = "/api/profiles/minecraft/";
        if (path.StartsWith(namePrefix) && path.Length > namePrefix.Length)
        {
            var name = Uri.UnescapeDataString(path[namePrefix.Length..].TrimEnd('/'));
            WriteJson(ctx, BuildProfileJson(OfflineUuid(name), name));
            return;
        }

        // /sessionserver/session/minecraft/hasJoined（开服/局域网校验）：返回带皮肤的档案
        if (path == "/sessionserver/session/minecraft/hasJoined")
        {
            var name = ctx.Request.QueryString["username"] ?? PlayerName;
            WriteJson(ctx, BuildProfileJson(OfflineUuid(name), name));
            return;
        }

        // /sessionserver/session/minecraft/join：离线场景直接放行
        if (path == "/sessionserver/session/minecraft/join")
        {
            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
            return;
        }

        // /skins/<任意名>.png → 皮肤文件
        if (path.StartsWith("/skins/") && path.EndsWith(".png") && SkinPath != null && File.Exists(SkinPath))
        {
            ctx.Response.ContentType = "image/png";
            ctx.Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
            var bytes = File.ReadAllBytes(SkinPath);
            ctx.Response.ContentLength64 = bytes.Length;
            ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
            ctx.Response.OutputStream.Close();
            return;
        }

        ctx.Response.StatusCode = 204;
        ctx.Response.Close();
    }

    /// <summary>离线玩家 UUID（与 Minecraft 离线规范一致）。</summary>
    private static string OfflineUuid(string name)
    {
        using var md5 = MD5.Create();
        var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash).ToString("N");
    }

    /// <summary>构造游戏可识别的玩家档案 JSON（含 base64 且已签名的 textures 属性）。</summary>
    private string BuildProfileJson(string uuid, string? name = null)
    {
        var playerName = name ?? PlayerName;
        if (string.IsNullOrEmpty(uuid) || SkinPath == null || !File.Exists(SkinPath) || _rsa == null)
        {
            // 未设置皮肤：返回无纹理档案
            return "{\"id\":\"" + uuid + "\",\"name\":\"" + playerName + "\",\"properties\":[]}";
        }

        // 有公网地址就用公网地址：别的玩家客户端也能下载到（回环地址他们访问不到）
        var skinUrl = !string.IsNullOrEmpty(PublicSkinUrl)
            ? PublicSkinUrl!
            : $"http://127.0.0.1:{Port}/skins/skin.png?v={_skinStamp}";
        var textures = Model == "slim"
            ? "{\"SKIN\":{\"url\":\"" + skinUrl + "\",\"metadata\":{\"model\":\"slim\"}}}"
            : "{\"SKIN\":{\"url\":\"" + skinUrl + "\"}}";
        var textureValue = "{\"timestamp\":" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() +
                           ",\"profileId\":\"" + uuid + "\",\"profileName\":\"" + playerName +
                           "\",\"signatureRequired\":true,\"textures\":" + textures + "}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(textureValue));

        // 对属性 value 做 RSA-SHA1 签名（与 Yggdrasil 一致），公钥在 API 根公布
        var sig = Convert.ToBase64String(_rsa.SignData(Encoding.UTF8.GetBytes(b64), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));

        return "{\"id\":\"" + uuid + "\",\"name\":\"" + playerName +
               "\",\"properties\":[{\"name\":\"textures\",\"value\":\"" + b64 +
               "\",\"signature\":\"" + sig + "\"}]}";
    }

    private static void WriteJson(HttpListenerContext ctx, string json)
    {
        ctx.Response.ContentType = "application/json";
        var bytes = Encoding.UTF8.GetBytes(json);
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    /// <summary>
    /// 下载 authlib-injector（官方源 + GitHub 备用）。返回错误信息，成功返回 null。
    /// 这是「离线皮肤」生效的必备组件：没有它，游戏不会去问本地皮肤服务器要贴图。
    /// </summary>
    public async Task<string?> EnsureAgentAsync(CancellationToken ct = default)
    {
        if (AgentReady) return null;
        // 失败冷却 60 秒：避免网络不通时每次启动/每次点应用都长时间等待
        if (_agentLastError != null && (DateTime.Now - _agentLastTry).TotalSeconds < 60) return _agentLastError;
        _agentLastTry = DateTime.Now;

        var urls = new List<string>();
        try
        {
            var meta = await Http.GetStringAsync("https://authlib-injector.yushi.moe/artifact/latest.json", ct);
            using var doc = JsonDocument.Parse(meta);
            var u = doc.RootElement.TryGetProperty("download_url", out var du) ? du.GetString() : null;
            if (!string.IsNullOrWhiteSpace(u)) urls.Add(u!);
        }
        catch (Exception ex) { _agentLastError = "获取版本信息失败：" + ex.Message; }

        // 备用源（GitHub 直链，1.2.8 为当前稳定版）
        urls.Add("https://authlib-injector.yushi.moe/artifact/56/authlib-injector-1.2.8.jar");
        urls.Add("https://github.com/yushijinhun/authlib-injector/releases/download/v1.2.8/authlib-injector-1.2.8.jar");

        foreach (var url in urls.Distinct())
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url, ct);
                if (bytes.Length < 50_000 || bytes[0] != 0x50 || bytes[1] != 0x4B)
                {
                    _agentLastError = "下载到的文件不是有效的 jar";
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(AgentJarPath)!);
                await File.WriteAllBytesAsync(AgentJarPath, bytes, ct);
                _agentLastError = null;
                return null;
            }
            catch (Exception ex) { _agentLastError = ex.Message; }
        }
        return _agentLastError ?? "下载失败";
    }

    private string? _agentLastError;

    /// <summary>给游戏启动用的 -javaagent 参数（authlib-injector 就绪且皮肤已设置时返回）。</summary>
    public string? GetAgentArgument()
    {
        if (!AgentReady || SkinPath == null) return null;
        return $"-javaagent:{AgentJarPath}=http://127.0.0.1:{Port}";
    }
}
