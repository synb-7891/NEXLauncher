# -*- coding: utf-8 -*-
"""webshell/index.html 补丁：镜像搜索、整合包真实下载、PCL 风格加载器弹窗"""
import io, sys

P = 'Assets/webshell/index.html'
s = io.open(P, encoding='utf-8').read()
orig_len = len(s)

def rep(old, new, tag=''):
    global s
    if old not in s:
        print('FAIL anchor:', tag); sys.exit(1)
    s = s.replace(old, new, 1)
    print('ok:', tag)

# ---------- A) 镜像助手 + 中文翻译 ----------
rep('''function fmtN(x){ x = +x||0; return x>=1e6?(x/1e6).toFixed(1)+"M":x>=1e3?(x/1e3).toFixed(1)+"k":String(x); }''',
'''function fmtN(x){ x = +x||0; return x>=1e6?(x/1e6).toFixed(1)+"M":x>=1e3?(x/1e3).toFixed(1)+"k":String(x); }
/* 国内镜像：图标与文件统一改走 MCIM，官方 CDN 直连基本超时 */
function mrUrl(u){ u = String(u==null?"":u); return u.replace("https://cdn.modrinth.com","https://mod.mcimirror.top").replace("https://api.modrinth.com","https://mod.mcimirror.top/modrinth"); }
function hasCJK(t){ return /[\\u4e00-\\u9fff]/.test(t||""); }
/* Modrinth 只认英文关键词：中文先翻译（失败用原词） */
async function translateQ(q){
  try{
    const r = await fetch("https://api.mymemory.translated.net/get?q="+encodeURIComponent(q)+"&langpair=zh-CN|en-US",{signal:AbortSignal.timeout(5000)});
    const d = await r.json();
    const t = d && d.responseData && d.responseData.translatedText;
    if(t && !/^MYMEMORY/i.test(t)) return t;
  }catch(e){}
  return q;
}''', tag='helpers')

# ---------- B) 卡片图标走镜像 ----------
rep('<img class="mod-ico" src="${esc(h.icon_url)}" loading="lazy"',
    '<img class="mod-ico" src="${esc(mrUrl(h.icon_url))}" loading="lazy"', tag='card icon')

# ---------- C) 搜索：镜像优先 → 官方回落 ----------
rep('''  const url = "https://api.modrinth.com/v2/search?limit=20&index=" + resState.sort +
    "&offset=" + ((resState.page-1)*20) +
    "&query=" + encodeURIComponent(resState.q) +
    "&facets=" + encodeURIComponent(JSON.stringify(facets));
  let hits = null;
  try {
    const r = await fetch(url, { signal: AbortSignal.timeout(9000) });
    if (!r.ok) throw 0;
    hits = (await r.json()).hits;
    resState.offline = false;
  } catch(e){ hits = null; }''',
'''  let qUse = resState.q;
  if (reset && hasCJK(qUse)) qUse = await translateQ(qUse);
  const qs = "limit=20&index=" + resState.sort +
    "&offset=" + ((resState.page-1)*20) +
    "&query=" + encodeURIComponent(qUse) +
    "&facets=" + encodeURIComponent(JSON.stringify(facets));
  /* MCIM 国内镜像优先（直连秒回），失败回落官方源 */
  const endpoints = ["https://mod.mcimirror.top/modrinth/v2/search?" + qs,
                     "https://api.modrinth.com/v2/search?" + qs];
  let hits = null;
  for (const ep of endpoints){
    try {
      const r = await fetch(ep, { signal: AbortSignal.timeout(8000) });
      if (!r.ok) throw 0;
      hits = (await r.json()).hits;
      resState.offline = false;
      break;
    } catch(e){ hits = null; }
  }''', tag='mirror search')

# ---------- D) 详情弹窗：镜像 banner + 真实下载按钮 ----------
rep('${h.icon_url ? `<img class="banner" src="${esc(h.icon_url)}"',
    '${h.icon_url ? `<img class="banner" src="${esc(mrUrl(h.icon_url))}"', tag='detail banner')

rep('''    <div class="actions">
      <button class="dl" id="resDl">前往下载页</button>
      <button class="gh" id="resCp">复制链接</button>
      <button class="gh" id="resX">关闭</button>
    </div>`;
  document.getElementById("resModal").classList.add("show");
  document.getElementById("resDl").onclick = ()=>openExternal(page);''',
'''    <div class="actions">
      <button class="dl" id="resInst">${h.project_type==="modpack" ? "下载安装" : "下载到游戏目录"}</button>
      <button class="gh" id="resDl">前往下载页</button>
      <button class="gh" id="resCp">复制链接</button>
      <button class="gh" id="resX">关闭</button>
    </div>`;
  document.getElementById("resModal").classList.add("show");
  document.getElementById("resInst").onclick = ()=>{
    if (h.project_type === "modpack"){
      toast("正在获取 " + h.title + " 的整合包文件…", "info");
      if (NEX_APP) nexPost({cmd:"action", action:"install-modpack", id: h.project_id || h.slug, title: h.title});
      else toast("请在桌面版中使用一键安装");
    } else {
      toast("正在下载到游戏目录…", "info");
      if (NEX_APP) nexPost({cmd:"action", action:"download-content", id: h.project_id || h.slug, title: h.title, type: h.project_type || resState.type});
      else toast("请在桌面版中使用一键下载");
    }
    closeResModal();
  };
  document.getElementById("resDl").onclick = ()=>openExternal(page);''', tag='detail actions')

# ---------- E) PCL 风格加载器选择弹窗（CSS） ----------
rep('.res-modal .dl{background:linear-gradient(135deg,var(--accent),color-mix(in srgb,var(--accent) 60%,#000));color:#fff}',
'''.res-modal .dl{background:linear-gradient(135deg,var(--accent),color-mix(in srgb,var(--accent) 60%,#000));color:#fff}
/* ==== PCL 风格 · 模组加载器选择弹窗 ==== */
.ldm-row{display:flex;align-items:center;gap:14px;padding:16px 18px;border:1px solid var(--border);
  border-radius:14px;margin-top:10px;cursor:pointer;background:var(--surface);transition:border-color .15s,background .15s}
.ldm-row:hover{border-color:var(--accent)}
.ldm-row.sel{border-color:var(--accent);background:color-mix(in srgb,var(--accent) 8%,var(--surface))}
.ldm-row.dis{opacity:.45;cursor:not-allowed}
.ldm-name{font-weight:800;font-size:15px;width:86px}
.ldm-stat{flex:1;font-size:13px;color:var(--muted)}
.ldm-stat.ok{color:var(--accent);font-weight:600}
.ldm-arrow{color:var(--muted);font-size:12px;transition:transform .15s}
.ldm-row.sel .ldm-arrow{transform:rotate(90deg)}
.ldm-ver{display:none;margin:-4px 0 0 100px;padding:0 0 4px}
.ldm-row.sel .ldm-ver{display:block}
.ldm-ver select{width:100%;padding:10px 12px;border-radius:10px;border:1px solid var(--border);
  background:var(--surface-alt);color:var(--text);font-family:var(--font);font-size:13px}
.ldm-head{display:flex;align-items:center;gap:12px}
.ldm-head .ldm-ico{width:44px;height:44px;border-radius:12px;background:var(--surface-alt);display:grid;place-items:center}
.ldm-go{width:100%;margin-top:18px;padding:14px;border:0;border-radius:14px;cursor:pointer;
  font-family:var(--font);font-weight:800;font-size:15px;color:#fff;
  background:linear-gradient(135deg,var(--accent),color-mix(in srgb,var(--accent) 55%,#000))}''', tag='ldm css')

# ---------- F) 弹窗 HTML ----------
rep('''        <div class="res-modal" id="resModal">
          <div class="box" id="resBox"></div>
        </div>''',
'''        <div class="res-modal" id="resModal">
          <div class="box" id="resBox"></div>
        </div>
        <div class="res-modal" id="ldModal">
          <div class="box" id="ldBox" style="width:min(560px,92vw)"></div>
        </div>''', tag='ldm html')

# ---------- G) 版本点击 → 弹加载器选择（PCL 行为） ----------
rep('''      }else{
        if(NEX_INSTALLING){ toast(`正在安装 ${NEX_INSTALLING}，请等待完成`); return; }
        NEX_INSTALLING = id;
        renderCatalog();
        if(NEX_APP) nexPost({cmd:"action", action:"install-version", id});
        toast(`开始下载 ${id}`, "info");
      }''',
'''      }else{
        if(NEX_INSTALLING){ toast(`正在安装 ${NEX_INSTALLING}，请等待完成`); return; }
        openLoaderDialog(id);   /* PCL 行为：先选模组加载器，再开始下载 */
      }''', tag='version click')

# ---------- H) 加载器弹窗逻辑 ----------
rep('function onInstallStart(id){ NEX_INSTALLING = id; renderCatalog(); }',
'''/* ================= PCL 风格 · 模组加载器选择 ================= */
const LD_LOADERS = [
  {id:"forge",   name:"Forge",   stat:"正在获取…", avail:null},
  {id:"neoforge",name:"NeoForge",stat:"暂不支持，即将上线", avail:false},
  {id:"fabric",  name:"Fabric",  stat:"正在获取…", avail:null},
  {id:"optifine",name:"OptiFine",stat:"无", avail:false},
];
const LD_INFO = {};          /* loader-info 缓存 */
let LD_CURRENT = null;       /* 当前弹窗的版本 id */

function ldRowHTML(ld){
  const dis = ld.avail === false;
  const statCls = ld.avail === true ? "ldm-stat ok" : "ldm-stat";
  let ver = "";
  if (ld.avail === true && LD_INFO[ld.id] && LD_INFO[ld.id].versions && LD_INFO[ld.id].versions.length){
    const vs = LD_INFO[ld.id].versions;
    const rec = LD_INFO[ld.id].recommended;
    const opts = vs.map((v,i)=>`<option value="${esc(v)}"${(ld.id==="forge"&&v===rec)||i===0?" selected":""}>${esc(v)}${i===0?"（最新）":(ld.id==="forge"&&v===rec?"（推荐）":"")}</option>`).join("");
    ver = `<div class="ldm-ver"><select data-ldver="${ld.id}">${opts}</select></div>`;
  }
  return `<div class="ldm-row${dis?" dis":""}" data-ld="${ld.id}">
    <span class="ldm-name">${ld.name}</span>
    <span class="${statCls}">${ld.stat}</span>
    <span class="ldm-arrow">${dis?"":"❯"}</span>
    ${ver}
  </div>`;
}
function renderLoaderDialog(){
  const box = document.getElementById("ldBox");
  if(!box || !LD_CURRENT) return;
  box.innerHTML = `
    <div class="ldm-head">
      <span class="ldm-ico">${verIcon()}</span>
      <div><b style="font-size:16px">${esc(LD_CURRENT.id)}</b>
      <div class="meta">选择要一并安装的模组加载器（可不选，纯净原版）</div></div>
    </div>
    ${LD_LOADERS.map(ldRowHTML).join("")}
    <button class="ldm-go" id="ldGo" ${LD_CURRENT.loading?"disabled":""}>${LD_CURRENT.loading?"正在获取加载器信息…":"⬇ 开始下载"}</button>
    <button id="ldCancel" style="width:100%;margin-top:10px;padding:12px;border-radius:14px;border:1px solid var(--border);background:var(--surface-alt);color:var(--text-dim);font-family:var(--font);cursor:pointer">取消</button>`;
  box.querySelectorAll(".ldm-row").forEach(r=>{
    r.onclick = (e)=>{
      if (e.target.tagName === "SELECT" || e.target.tagName === "OPTION") return;
      const ld = LD_LOADERS.find(x=>x.id===r.dataset.ld);
      if (!ld || ld.avail !== true) return;
      LD_CURRENT.loader = LD_CURRENT.loader===ld.id ? null : ld.id;
      renderLoaderDialog();
    };
  });
  box.querySelectorAll("[data-ldver]").forEach(sl=>{ sl.onclick = e=>e.stopPropagation(); });
  const go = document.getElementById("ldGo");
  if (go) go.onclick = ()=>{
    const id = LD_CURRENT.id;
    const ld = LD_LOADERS.find(x=>x.id===LD_CURRENT.loader);
    const loader = ld ? ld.id : null;
    let lv = null;
    if (loader){
      const sl = box.querySelector('[data-ldver="'+loader+'"]');
      lv = sl ? sl.value : ((LD_INFO[loader]||{}).versions||[])[0] || null;
    }
    closeLoaderDialog();
    NEX_INSTALLING = id;
    renderCatalog();
    nexPost({cmd:"action", action:"install-version", id, loader: loader||"vanilla", loaderVersion: lv||""});
    toast(loader ? ("开始下载 " + id + " · " + ld.name) : ("开始下载 " + id + " · 原版"), "info");
  };
  const cc = document.getElementById("ldCancel");
  if (cc) cc.onclick = closeLoaderDialog;
}
function openLoaderDialog(id){
  LD_CURRENT = {id, loader:null, loading:true};
  LD_LOADERS.forEach(l=>{ if(l.avail!==false){ l.stat="正在获取…"; l.avail=null; } });
  renderLoaderDialog();
  document.getElementById("ldModal").classList.add("show");
  if (NEX_APP) nexPost({cmd:"loader-info", id});
  else { LD_LOADERS.find(l=>l.id==="forge").stat="桌面版可用"; LD_LOADERS.find(l=>l.id==="fabric").stat="桌面版可用"; LD_CURRENT.loading=false; renderLoaderDialog(); }
}
function closeLoaderDialog(){
  const m = document.getElementById("ldModal");
  if (m) m.classList.remove("show");
  LD_CURRENT = null;
}
document.getElementById("ldModal").addEventListener("click", e=>{ if(e.target.id==="ldModal") closeLoaderDialog(); });
function onLoaderInfo(m){
  const put = (key, obj)=>{
    const ld = LD_LOADERS.find(x=>x.id===key);
    if (!ld) return;
    if (obj && obj.versions && obj.versions.length){
      LD_INFO[key] = obj;
      ld.avail = true;
      const latest = obj.versions[0];
      ld.stat = obj.recommended ? ("最新 " + latest + " · 推荐 " + obj.recommended + " · 可以添加") : ("最新 " + latest + " · 可以添加");
    } else {
      ld.avail = false;
      ld.stat = m.fail ? "获取失败，请检查网络" : "该版本暂无可用加载器";
    }
  };
  put("forge", m.forge); put("fabric", m.fabric); put("quilt", m.quilt);
  if (LD_CURRENT && LD_CURRENT.id === m.id){
    LD_CURRENT.loading = false;
    renderLoaderDialog();
  }
}
function onInstallStart(id){ NEX_INSTALLING = id; closeLoaderDialog(); renderCatalog(); }''', tag='ldm js')

# ---------- I) 消息路由加 loader-info ----------
rep('''      case "install-busy": toast(`正在安装 ${m.current}，请等待完成`); break;''',
'''      case "install-busy": toast(`正在安装 ${m.current}，请等待完成`); break;
      case "loader-info": onLoaderInfo(m); break;
      case "download-ok": toast(m.msg || "下载完成"); break;
      case "download-fail": toast(m.msg || "下载失败", "info"); break;''', tag='msg route')

io.open(P, 'w', encoding='utf-8').write(s)
print('done: %d -> %d chars' % (orig_len, len(s)))
