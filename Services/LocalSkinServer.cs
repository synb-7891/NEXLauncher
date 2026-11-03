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

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    // 签名密钥（持久化到 tools/skin_signing.key，PEM 格式）
    private RSA? _rsa;
    private string? _publicKeyPem;

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
    public bool ApplySkin(string skinPath, string model, string playerName)
    {
        try
        {
            SkinPath = skinPath;
            Model = model == "slim" ? "slim" : "wide";
            PlayerName = playerName;
            EnsureKey();
            EnsureStarted();
            return true;
        }
        catch
        {
            return false;
        }
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

    private void ListenLoop()
    {
        var listener = _listener!;
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

        // authlib-injector 启动时先请求根地址拿元数据：
        // 顶层 skinDomains + signaturePublickey（签名公钥），meta 里是展示信息
        if (path == "/")
        {
            var pub = (_publicKeyPem ?? "").Replace("\r\n", "\\n").Replace("\n", "\\n");
            WriteJson(ctx,
                "{\"skinDomains\":[\"127.0.0.1\",\"localhost\"]," +
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

        var skinUrl = $"http://127.0.0.1:{Port}/skins/skin.png";
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

    /// <summary>下载 authlib-injector（官方国内镜像）。返回错误信息，成功返回 null。</summary>
    public async Task<string?> EnsureAgentAsync(CancellationToken ct = default)
    {
        if (AgentReady) return null;
        try
        {
            var meta = await Http.GetStringAsync("https://authlib-injector.yushi.moe/artifact/latest.json", ct);
            using var doc = JsonDocument.Parse(meta);
            var url = doc.RootElement.GetProperty("download_url").GetString()!;
            var bytes = await Http.GetByteArrayAsync(url, ct);

            Directory.CreateDirectory(Path.GetDirectoryName(AgentJarPath)!);
            await File.WriteAllBytesAsync(AgentJarPath, bytes, ct);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>给游戏启动用的 -javaagent 参数（authlib-injector 就绪且皮肤已设置时返回）。</summary>
    public string? GetAgentArgument()
    {
        if (!AgentReady || SkinPath == null) return null;
        return $"-javaagent:{AgentJarPath}=http://127.0.0.1:{Port}";
    }
}
