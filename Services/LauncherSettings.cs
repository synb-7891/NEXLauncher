using System.IO;
using System.Text.Json;

namespace NEXLauncher.Services;

public sealed class LauncherSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEXLauncher",
        "settings.json");

    public string MicrosoftClientId { get; set; } = string.Empty;

    // ---- 个性化 ----
    /// <summary>自定义强调色；留空表示跟随主题预设的主色。</summary>
    public string AccentColor { get; set; } = "";
    public string BackgroundImagePath { get; set; } = "";
    public double BackgroundOpacity { get; set; } = 0.35;
    public string WindowTitle { get; set; } = "NEX Launcher";
    public string ThemePreset { get; set; } = "云白";
    /// <summary>0.4.1 一次性迁移标记：旧版默认蓝被写成自定义强调色，会覆盖主题主色。</summary>
    public bool ThemeMigrated041 { get; set; }

    /// <summary>3.9.8 一次性迁移：默认主题从深色切换为浅色「云白」。</summary>
    public bool ThemeMigrated398 { get; set; }

    // ---- 登录状态保持 ----
    /// <summary>上次登录的账号类型："" / "microsoft" / "offline"</summary>
    public string LastAccountType { get; set; } = "";
    /// <summary>上次离线账号用户名</summary>
    public string LastOfflineName { get; set; } = "";
    /// <summary>启动器启动时自动恢复上次登录</summary>
    public bool AutoLogin { get; set; } = true;

    // ---- 微软账号会话缓存（静默刷新失败时的后备显示与启动凭据）----
    public string MicrosoftSessionName { get; set; } = "";
    public string MicrosoftSessionUuid { get; set; } = "";
    public string MicrosoftSessionToken { get; set; } = "";
    /// <summary>微软 OAuth 刷新令牌（自家登录流程，用于启动时静默恢复）。</summary>
    public string MicrosoftRefreshToken { get; set; } = "";

    // ---- 游戏启动 ----
    public string LastQuickVersion { get; set; } = "";
    public string QuickOfflineName { get; set; } = "Player";
    public int MaxRamMb { get; set; } = 4096;
    /// <summary>最小内存（-Xms），0 表示跟随最大内存的一半。</summary>
    public int MinRamMb { get; set; } = 1024;
    public string JavaPath { get; set; } = "";
    public int GameWindowWidth { get; set; } = 854;
    public int GameWindowHeight { get; set; } = 480;
    public bool GameFullScreen { get; set; } = false;
    public string ExtraJvmArgs { get; set; } = "";

    // ---- 下载与实例 ----
    /// <summary>true = 优先国内镜像（MCIM / BMCLAPI），false = 官方源。</summary>
    public bool UseMirror { get; set; } = true;
    /// <summary>版本隔离：每个版本使用独立的 mods / config / saves（versions/&lt;id&gt;/ 下）。</summary>
    public bool VersionIsolation { get; set; } = true;
    /// <summary>资源默认下载目录；为空表示游戏目录内对应位置。</summary>
    public string DownloadDir { get; set; } = "";
    /// <summary>点右上角关闭时最小化到托盘（而不是退出）。</summary>
    public bool CloseToTray { get; set; } = false;
    /// <summary>每个版本的个性化配置（图标、名称、描述、收藏、内存、Java）。</summary>
    public Dictionary<string, VersionProfileItem> VersionProfiles { get; set; } = [];

    // ---- 联机 ----
    /// <summary>检测到公网地址后自动复制到剪贴板</summary>
    public bool AutoCopyAddress { get; set; } = true;
    /// <summary>实验性：隧道掉线后自动重连（服务端仍在运行时生效）</summary>
    public bool AutoReconnectTunnel { get; set; } = false;

    // ---- 安全防护（五道防火墙） ----
    /// <summary>第一道：下载防护（资源/整合包落地前扫描伪装文件）</summary>
    public bool SecDownloadShield { get; set; } = true;
    /// <summary>第二道：勒索软件蜜罐（Documents/桌面/游戏目录放诱饵文件并监控）</summary>
    public bool SecRansomGuard { get; set; } = true;
    /// <summary>第三道：批量加密行为监控（短时间大量改写文档时告警）</summary>
    public bool SecBehaviorMonitor { get; set; } = true;
    /// <summary>第四道：系统关键位守护（hosts / 启动项 / 启动文件夹变更告警）</summary>
    public bool SecSystemGuard { get; set; } = true;
    /// <summary>第五道：游戏环境防护（mods/插件伪装检测、启动参数注入检查）</summary>
    public bool SecGameGuard { get; set; } = true;
    /// <summary>检测到勒索行为时自动终止可疑进程（临时目录/近期启动的可疑程序）</summary>
    public bool SecAutoKill { get; set; } = false;
    /// <summary>主动拦截引擎：实时判定可疑进程与可疑文件，命中即刻终止/隔离（不只是告警）</summary>
    public bool SecActiveBlock { get; set; } = true;
    /// <summary>拦截后直接删除源文件（关闭 = 只移入隔离区，可从隔离区还原）</summary>
    public bool SecAutoDelete { get; set; } = false;
    /// <summary>对可疑文件调用系统 Windows Defender 做二次确认与清除</summary>
    public bool SecDefenderAssist { get; set; } = true;
    /// <summary>24 小时自动防卫：关闭启动器后由后台守护进程继续防护，并随 Windows 登录自启</summary>
    public bool SecAlwaysGuard { get; set; } = true;
    /// <summary>隔离区路径（空 = 默认 %LocalAppData%/NEXLauncher/quarantine）</summary>
    public string QuarantineDir { get; set; } = "";

    // ---- 皮肤 ----
    /// <summary>离线账号自定义皮肤 PNG 路径；为空表示未启用</summary>
    public string OfflineSkinPath { get; set; } = "";
    /// <summary>离线皮肤模型：wide（经典）/ slim（纤细）</summary>
    public string OfflineSkinModel { get; set; } = "wide";
    /// <summary>离线皮肤所属用户名（皮肤只对该名字的离线账号生效）</summary>
    public string OfflineSkinPlayer { get; set; } = "";

    // ---- 更新 ----
    /// <summary>启动时自动检查更新</summary>
    public bool AutoCheckUpdate { get; set; } = true;
    /// <summary>更新源（指向一个 manifest.json 的公开直链）；留空则不检查</summary>
    public string UpdateManifestUrl { get; set; } = "https://cdn.jsdelivr.net/gh/synb-7891/NEXLauncher@main/manifest.json";

    // ---- 多账号管理（账号页）----
    /// <summary>保存的账号列表（离线 / 微软 / 外置登录），互不覆盖，可一键切换。</summary>
    public List<SavedAccountItem> SavedAccounts { get; set; } = [];
    /// <summary>当前账号在 SavedAccounts 中的唯一键（type:name:apiRoot）。</summary>
    public string CurrentAccountKey { get; set; } = "";
    /// <summary>authlib-injector 客户端令牌（外置登录必需，要求每次启动一致）。</summary>
    public string AuthlibClientToken { get; set; } = "";
    /// <summary>当前外置登录账号的认证服务器 API Root。</summary>
    public string AuthlibApiRoot { get; set; } = "";
    /// <summary>当前外置登录账号的服务器名称（用于显示）。</summary>
    public string AuthlibServerName { get; set; } = "";

    /// <summary>皮肤库目录（皮肤选择器「已保存皮肤」）。</summary>
    public static string SkinLibraryDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NEXLauncher", "skins");

    public static LauncherSettings Load()
    {
        if (!File.Exists(SettingsPath))
        {
            return new LauncherSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsPath))
                ?? new LauncherSettings();
        }
        catch (JsonException)
        {
            return new LauncherSettings();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }

    /// <summary>浅拷贝一份（用于把版本级覆盖合并进来，避免污染全局设置）。</summary>
    public LauncherSettings Clone()
        => JsonSerializer.Deserialize<LauncherSettings>(JsonSerializer.Serialize(this)) ?? new LauncherSettings();
}

/// <summary>账号管理页保存的账号（离线 / 微软正版 / 外置登录）。</summary>
public sealed class SavedAccountItem
{
    /// <summary>offline / microsoft / authlib</summary>
    public string Type { get; set; } = "offline";
    public string Name { get; set; } = "";
    public string Uuid { get; set; } = "";
    /// <summary>微软 / 外置登录的访问令牌</summary>
    public string AccessToken { get; set; } = "";
    /// <summary>外置登录的认证服务器 API Root（如 https://littleskin.cn/api/yggdrasil）</summary>
    public string ApiRoot { get; set; } = "";
    /// <summary>外置登录服务器显示名（如 LittleSkin）</summary>
    public string ServerName { get; set; } = "";

    /// <summary>唯一键：类型+名字（+服务器），用于定位"当前账号"。</summary>
    public string Key => $"{Type}:{Name}:{ApiRoot}";
}

/// <summary>单个游戏版本的个性化配置（版本设置页保存）。</summary>
public sealed class VersionProfileItem
{
    /// <summary>显示名称（留空则用版本 ID）</summary>
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>自定义图标（PNG/ICO 的绝对路径）</summary>
    public string IconPath { get; set; } = "";
    public bool Favorite { get; set; }
    /// <summary>该版本专用最大内存（MB），0 表示跟随全局设置</summary>
    public int MaxRamMb { get; set; }
    /// <summary>该版本专用最小内存（MB），0 表示跟随全局设置</summary>
    public int MinRamMb { get; set; }
    /// <summary>该版本专用 Java 路径，空表示跟随全局设置</summary>
    public string JavaPath { get; set; } = "";
}
