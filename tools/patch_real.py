import io, re, sys

P = r'D:\NEXLauncher\Assets\webshell\index.html'
s = io.open(P, encoding='utf-8').read()
orig_len = len(s)
changes = []

def rep(pattern, new, name, count=1, flags=re.S):
    global s
    m = re.search(pattern, s, flags)
    if not m:
        print('!! 未匹配:', name)
        sys.exit(1)
    s = s[:m.start()] + new + s[m.end():]
    changes.append(name)

# ---------------------------------------------------------------- 1. 百宝箱整页
TOOLS = r'''      <section class="page" id="page-tools">
        <div class="panel" style="margin-bottom:16px">
          <div style="display:flex;align-items:center;gap:12px;flex-wrap:wrap">
            <div style="flex:1;min-width:220px">
              <h2 style="margin-bottom:4px">执行结果</h2>
              <span class="meta" id="toolHint">下面每个按钮都是真实操作：读写本机文件、调用真实接口，失败会告诉你原因</span>
            </div>
            <div class="live-chip" id="toolChip" style="display:none"><i></i><span id="toolChipTxt">执行中…</span></div>
          </div>
          <div id="toolOut" class="tool-out">还没有执行任何操作</div>
        </div>

        <div class="tool-sec">
          <h3>运行环境</h3>
          <div class="tool-grid">
            <div class="tool" data-op="java-list">
              <div class="ic amber"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M8 18c-3 0-4-1.5-2-2.5M13 20c3.5.5 6-1 5-3M7 4c-2 3 1 4 1 6M13 3c-3 4 2 6 1 9M17 5c-2 2 0 3 0 5"/></svg>
              <div><b>Java 运行环境</b><span>扫描本机全部 JDK / JRE 并显示真实版本</span></div>
            </div>
            <div class="tool" data-op="java-open">
              <div class="ic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7h6l2 2h10v10H3z"/></svg>
              <div><b>打开 Java 目录</b><span>在资源管理器中打开当前使用的 Java</span></div>
            </div>
            <div class="tool" data-op="java-install" data-major="21">
              <div class="ic green"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M4 21h16"/></svg>
              <div><b>下载 Java 21</b><span>从 Adoptium 官方源真实下载并安装（约 180 MB）</span></div>
            </div>
            <div class="tool" data-op="mem-info">
              <div class="ic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="3" y="7" width="18" height="10" rx="2"/><path d="M7 7v10M12 7v10M17 7v10"/></svg>
              <div><b>内存实况</b><span>读取本机物理内存 / 可用内存 / 当前分配</span></div>
            </div>
            <div class="tool" data-op="mem-optimize">
              <div class="ic green"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12h4l2-5 4 10 2-5h4"/></svg>
              <div><b>内存优化</b><span>真实回收进程工作集，并按本机内存设推荐 Xmx</span></div>
            </div>
          </div>
        </div>

        <div class="tool-sec">
          <h3>游戏维护</h3>
          <div class="tool-grid">
            <div class="tool" data-op="saves-list">
              <div class="ic purple"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 5h16v14H4z"/><path d="M4 9h16M9 5v4"/></svg>
              <div><b>存档管理</b><span>列出真实存档与备份，可备份 / 恢复</span></div>
            </div>
            <div class="tool" data-op="saves-backup">
              <div class="ic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M4 21h16"/></svg>
              <div><b>备份全部存档</b><span>打包到 .minecraft\nex-backups（真实 zip）</span></div>
            </div>
            <div class="tool" data-op="crash-list">
              <div class="ic amber"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3 2 21h20L12 3z"/><path d="M12 10v5M12 18v.01"/></svg>
              <div><b>崩溃报告</b><span>读取 crash-reports 与 latest.log 并给出原因</span></div>
            </div>
            <div class="tool" data-op="clean-junk">
              <div class="ic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 6h18M8 6V4h8v2M6 6l1 15h10l1-15"/></svg>
              <div><b>清理游戏垃圾</b><span>真实删除 logs / crash-reports，并报告释放空间</span></div>
            </div>
            <div class="tool" data-op="open-folder" data-which="mods">
              <div class="ic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7h6l2 2h10v10H3z"/></svg>
              <div><b>打开 mods 目录</b><span>直达当前版本的模组文件夹</span></div>
            </div>
            <div class="tool" data-op="open-folder" data-which="saves">
              <div class="ic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7h6l2 2h10v10H3z"/></svg>
              <div><b>打开 saves 目录</b><span>直达存档文件夹</span></div>
            </div>
          </div>
        </div>

        <div class="tool-sec">
          <h3>基岩版与披风</h3>
          <div class="tool-grid">
            <div class="tool" data-op="bedrock-detect">
              <div class="ic green"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/></svg>
              <div><b>检测基岩版</b><span>检查系统里是否真的装了基岩版</span></div>
            </div>
            <div class="tool" data-op="bedrock-launch">
              <div class="ic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m5 3 14 9-14 9V3z"/></svg>
              <div><b>启动基岩版</b><span>调用 minecraft:// 协议真实拉起游戏</span></div>
            </div>
            <div class="tool" data-op="bedrock-import">
              <div class="ic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M4 18v3h16v-3"/></svg>
              <div><b>导入到基岩版</b><span>选 mcworld / mcpack，交给基岩版真实导入</span></div>
            </div>
            <div class="tool" data-op="capes-list">
              <div class="ic purple"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M8 3h8l-1 12-3 6-3-6L8 3z"/></svg>
              <div><b>披风管理</b><span>调 Mojang 接口读你正版账号的真实披风</span></div>
            </div>
          </div>
        </div>

        <div class="tool-sec">
          <h3>下载与联机</h3>
          <div class="tool-grid">
            <div class="tool" data-op="download-file">
              <div class="ic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M4 21h16"/></svg>
              <div><b>下载任意文件</b><span>填地址 → 选保存位置 → 真实下载（带进度）</span></div>
            </div>
            <div class="tool" data-op="server-status">
              <div class="ic green"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3 3 15 0 18"/></svg>
              <div><b>服务器状态</b><span>输入服务器地址，查真实在线人数与版本</span></div>
            </div>
            <div class="tool" data-op="lan-info">
              <div class="ic amber"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 12h4l3-7 3 14 3-7h3"/></svg>
              <div><b>局域网联机地址</b><span>取本机真实内网 IP 并复制到剪贴板</span></div>
            </div>
            <div class="tool" data-op="server-start">
              <div class="ic purple"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="4" width="18" height="7" rx="2"/><rect x="3" y="13" width="18" height="7" rx="2"/></svg>
              <div><b>一键开服</b><span>下载官方服务端并真实启动（首次约 50 MB）</span></div>
            </div>
            <div class="tool" data-op="server-stop">
              <div class="ic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><rect x="6" y="6" width="12" height="12" rx="2"/></svg>
              <div><b>停止服务器</b><span>结束正在运行的服务端进程</span></div>
            </div>
          </div>
        </div>

        <div class="tool-sec">
          <h3>实用工具与站点</h3>
          <div class="tool-grid">
            <div class="tool" data-op="open-url" data-url="https://modrinth.com/mods">
              <div class="ic green"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3 3 15 0 18"/></svg>
              <div><b>Modrinth 官网</b><span>在浏览器打开真实站点</span></div>
            </div>
            <div class="tool" data-op="open-url" data-url="https://namemc.com/">
              <div class="ic amber"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="11" cy="11" r="7"/><path d="m20 20-4-4"/></svg>
              <div><b>NameMC 查询</b><span>查玩家 ID 与正版皮肤</span></div>
            </div>
            <div class="tool" data-op="open-url" data-url="https://mcim.z0z0r4.top/">
              <div class="ic"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M4 6h16v12H4z"/><path d="M4 10h16"/></svg>
              <div><b>MCIM 镜像站</b><span>国内镜像，加速下载游戏资源</span></div>
            </div>
            <div class="tool" data-op="open-url" data-url="https://minecraft.wiki/w/Crafting">
              <div class="ic purple"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M4 4h7v7H4zM13 4h7v7h-7zM4 13h7v7H4zM13 13h7v7h-7z"/></svg>
              <div><b>合成表查询</b><span>官方 Wiki 合成配方</span></div>
            </div>
          </div>
        </div>
      </section>'''
rep(r'      <section class="page" id="page-tools">.*?\n      </section>', TOOLS, '百宝箱整页')

# ---------------------------------------------------------------- 2. 模组页
MODS = r'''      <section class="page" id="page-mods">
        <div class="panel">
          <div style="display:flex;align-items:center;gap:12px;flex-wrap:wrap;margin-bottom:12px">
            <div style="flex:1;min-width:200px">
              <h2 style="margin-bottom:4px">模组管理 <span id="modVer" style="font-weight:600;color:var(--accent)"></span></h2>
              <span class="meta" id="modHint">正在扫描 mods 目录…</span>
            </div>
            <button class="abtn" id="modReload">刷新</button>
            <button class="abtn" data-op="open-folder" data-which="mods">打开 mods 目录</button>
          </div>
          <div id="modList"><div class="meta" style="padding:8px 2px">正在读取…</div></div>
        </div>
      </section>'''
rep(r'      <section class="page" id="page-mods">.*?\n      </section>', MODS, '模组页')

# ---------------------------------------------------------------- 3. 皮肤页
SKIN_HEAD = r'''        <div class="skin-bar">
          <input class="skin-search" id="skinSearch" placeholder="搜索皮肤 / 作者…（回车）">
          <button class="cat on" data-kind="">全部</button><button class="cat" data-kind="Heroes">英雄</button>
          <button class="cat" data-kind="Evildoers">反派</button><button class="cat" data-kind="Fantasy">奇幻</button>
          <button class="cat" data-kind="Scientific">科幻</button><button class="cat" data-kind="Historical">历史</button>
          <button class="cat" data-kind="Comics">漫画</button><button class="cat" data-kind="Characters">角色</button>
          <button class="cat" data-kind="Weekend">休闲</button>
          <div style="margin-left:auto;display:flex;align-items:center;gap:9px;font-size:12px;color:var(--muted)">
            <span id="skinCount">皮肤库来自 Ely.by 公开接口</span>
          </div>
        </div>
        <div class="skin-grid" id="skinGrid"></div>
        <div class="skin-foot">
          <button class="abtn" id="skinPrev">上一页</button>
          <span style="font-size:12px;color:var(--muted)" id="skinPage">第 1 / 20 页</span>
          <button class="abtn" id="skinNext">下一页</button>
          <button class="abtn primary" data-op="skin-upload">上传本地皮肤</button>
          <button class="abtn" data-op="open-folder" data-which="skins">打开皮肤文件夹</button>
          <button class="abtn" data-op="open-url" data-url="https://littleskin.cn/">LittleSkin 皮肤站</button>
          <button class="abtn" data-op="open-url" data-url="https://namemc.com/">NameMC 查询</button>
        </div>'''
rep(r'        <div class="skin-bar">.*?        </div>\n      </section>', SKIN_HEAD + '\n      </section>', '皮肤页')

# ---------------------------------------------------------------- 4. 首页
rep(r'<div class="news-item"><span class="t">11-02</span>.*?<div class="widget w-stats"',
    '<div id="newsList"><div class="news-item"><span class="t">—</span>正在读取 GitHub 更新日志…</div></div>\n          </div>\n\n          <div class="widget w-stats"',
    '首页公告')

rep(r'<h3>本周游戏数据</h3>\s*<div class="stat-row">.*?</div>\s*</div>\s*<div class="bars">.*?</div>',
    '''<h3>游戏数据 <span style="font-size:11px;color:var(--muted);font-weight:400">真实统计</span></h3>
            <div class="stat-row">
              <div class="stat-cell"><b id="statHours">—</b><span>累计时长</span></div>
              <div class="stat-cell"><b id="statLaunches">—</b><span>启动次数</span></div>
            </div>
            <div class="bars" id="statBars"></div>''',
    '首页统计')

rep(r'<div class="res-row">.*?</div>\n          </div>\n\n          <div class="widget w-sec"',
    '''<div class="res-row" id="resRow">
              <span class="res-tag" data-goto="mods">模组 · <b id="resMods">—</b></span>
              <span class="res-tag" data-op="open-folder" data-which="saves">存档 · <b id="resSaves">—</b></span>
              <span class="res-tag" data-op="open-folder" data-which="resourcepacks">资源包 · <b id="resRp">—</b></span>
              <span class="res-tag" data-op="open-folder" data-which="shaderpacks">光影 · <b id="resSp">—</b></span>
              <span class="res-tag" data-goto="res">去逛 Modrinth →</span>
            </div>
          </div>

          <div class="widget w-sec"''',
    '首页资源计数')

rep(r'<div class="quick-row">.*?</div>',
    '''<div class="quick-row">
              <button class="qbtn" data-op="server-start">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><circle cx="12" cy="12" r="9"/><path d="M3 12h18M12 3c3 3 3 15 0 18"/></svg>
                一键开服
              </button>
              <button class="qbtn" data-op="lan-info">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="12" height="12" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>
                联机地址
              </button>
              <button class="qbtn" data-goto="res">
                <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M12 3v12"/><path d="m7 10 5 5 5-5"/><path d="M4 21h16"/></svg>
                逛资源站
              </button>
            </div>''',
    '首页快捷功能')

rep(r'<span>内存 <b id="ramTxt">2\.4</b>/4\.0 GB</span>\s*<div class="ram-meter"><i></i></div>\s*<span>Java 21 · 镜像加速中</span>',
    '''<span>最大内存 <b id="ramTxt">—</b> MB</span>
              <div class="ram-meter"><i id="ramBar" style="width:40%"></i></div>
              <span id="javaTxt">正在检测 Java…</span>''',
    '首页内存/Java')

rep(r'<span style="font-size:11\.5px;color:var\(--muted\)">最近游玩 · 昨天</span>',
    '<span style="font-size:11.5px;color:var(--muted)" id="lastPlayTxt">—</span>',
    '首页最近游玩')

rep(r'<div class="meta">五道防线 · 已守护 2 小时 53 分</div>',
    '<div class="meta" id="homeSecMeta">五道防线 · 正在读取守护状态…</div>',
    '首页防护状态')

# ---------------------------------------------------------------- 5. 安全页隔离区
rep(r'<div class="canary-row" style="margin-bottom:0">\s*<span class="k" style="color:#F87171.*?</div>\s*</div>\s*</div>\s*</section>',
    '''<div id="qList"><div class="meta" style="padding:6px 2px">正在读取隔离区…</div></div>
              <div style="display:flex;gap:9px;margin-top:12px;flex-wrap:wrap">
                <button class="abtn" data-op="quarantine-list">刷新隔离区</button>
                <button class="abtn primary" style="margin-left:auto" data-op="audit">立即体检</button>
              </div>
            </div>
          </div>
        </div>
      </section>''',
    '安全页隔离区')

# ---------------------------------------------------------------- 6. 设置页开关 -> 真实设置
rep(r'<div class="set-row"><div class="lbl">下载镜像加速<span>使用国内镜像下载资源</span></div><div class="switch on" data-sw></div></div>',
    '<div class="set-row"><div class="lbl">下载镜像加速<span>使用国内镜像下载游戏资源（写入 settings.json）</span></div><div class="switch" data-setting="UseMirror"></div></div>',
    '设置-镜像')
rep(r'<div class="set-row"><div class="lbl">版本隔离<span>每个版本独立存档与配置</span></div><div class="switch on" data-sw></div></div>',
    '<div class="set-row"><div class="lbl">版本隔离<span>每个版本使用独立的 mods / 配置目录</span></div><div class="switch" data-setting="VersionIsolation"></div></div>',
    '设置-隔离')
rep(r'<div class="set-row"><div class="lbl">自动检查更新<span>更新源：jsDelivr 公开仓库</span></div><div class="switch on" data-sw></div></div>',
    '<div class="set-row"><div class="lbl">全屏启动游戏<span>启动时直接用全屏窗口</span></div><div class="switch" data-setting="GameFullScreen"></div></div>',
    '设置-全屏')
rep(r'<div class="set-row"><div class="lbl">关闭到托盘<span>点关闭按钮最小化到托盘</span></div><div class="switch" data-sw></div></div>',
    '<div class="set-row"><div class="lbl">关闭到托盘<span>点关闭按钮最小化到托盘</span></div><div class="switch" data-setting="CloseToTray"></div></div>',
    '设置-托盘')
rep(r'自动防卫 <div class="switch on" data-sw></div>',
    '自动防卫 <div class="switch" data-setting="SecAlwaysGuard"></div>',
    '安全-自动防卫开关')
rep(r'<div class="set-row"><div class="lbl">最大内存<span>当前 4096 MB</span></div>',
    '<div class="set-row"><div class="lbl">最大内存<span id="ramLbl">当前 — MB</span></div>',
    '设置-内存标签')

# ---------------------------------------------------------------- 7. 追加样式
CSS = r'''
/* ===== 真实能力桥 UI ===== */
.tool-out{margin-top:12px;padding:12px 14px;border-radius:12px;background:var(--surface-alt);
  border:1px solid var(--border);font-size:12px;color:var(--text-dim);line-height:1.75;
  max-height:340px;overflow:auto;white-space:pre-wrap;word-break:break-all}
.tool-out b{color:var(--text)}
.t-row{display:flex;align-items:center;gap:9px;padding:7px 0;border-bottom:1px dashed var(--border)}
.t-row:last-child{border-bottom:0}
.t-row .grow{flex:1;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
.t-tag{font-size:10.5px;padding:2px 7px;border-radius:20px;background:rgba(34,211,166,.14);color:#12A37E;font-weight:700}
.t-tag.bad{background:rgba(248,113,113,.14);color:#E23B3B}
.t-btn{font-size:11px;padding:3px 9px;border-radius:8px;border:1px solid var(--border);
  background:var(--surface);color:var(--text);cursor:pointer}
.t-btn:hover{border-color:var(--accent);color:var(--accent)}
.t-btn.danger:hover{border-color:#F87171;color:#F87171}
.tool.busy{opacity:.45;pointer-events:none}
.tool-out.err{background:rgba(248,113,113,.08);border-color:rgba(248,113,113,.3);color:#D93B3B}
.tool-out pre{margin:6px 0 0;font-size:11px;color:var(--muted);white-space:pre-wrap;max-height:180px;overflow:auto}
/* 皮肤头 */
.skin-card{background:var(--surface);border:1px solid var(--border);border-radius:14px;padding:12px;
  display:flex;flex-direction:column;align-items:center;gap:8px;transition:.18s}
.skin-card:hover{border-color:var(--accent);transform:translateY(-2px)}
.skin-head{width:56px;height:56px;border-radius:12px;image-rendering:pixelated;
  background-size:512px 512px;background-position:-64px -64px;background-color:#DDE7F2}
.skin-meta{font-size:11px;color:var(--muted);text-align:center}
.skin-tags{display:flex;gap:4px;flex-wrap:wrap;justify-content:center}
.skin-tags i{font-style:normal;font-size:10px;padding:1px 6px;border-radius:20px;
  background:var(--surface-alt);color:var(--muted)}
'''
s = s.replace('</style>', CSS + '</style>', 1)
changes.append('样式')

io.open(P, 'w', encoding='utf-8').write(s)
print('替换完成:', ', '.join(changes))
print('文件大小:', len(s), '(原', orig_len, ')')
