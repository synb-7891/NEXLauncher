import io, re, json, subprocess, os, sys

SRC = r'D:\NEXLauncher\Assets\webshell\index.html'
OUT = r'D:\NEXLauncher\tools\_harness.html'
s = io.open(SRC, encoding='utf-8').read()

MOCK = r'''
<script>
window.__sent = [];
window.__errs = [];
window.__handlers = [];
const FAKE = {
  "java-list": { items: [
      { title: "Java 21（64 位）", path: "C:\\Program Files\\Java\\jdk-21\\bin\\javaw.exe", current: true },
      { title: "Java 8（64 位）", path: "C:\\Program Files\\Java\\jre1.8\\bin\\javaw.exe", current: false } ] },
  "saves-list": { worlds: [{ name: "world", size: "12.3 MB", last: "2026-11-02 21:00" }],
                  backups: [{ name: "world_20261103.zip", path: "D:\\b\\world.zip", world: "world", size: "9.1 MB" }] },
  "crash-list": { reports: [{ file: "crash-1.txt", time: "11-02 20:00", size: "12 KB", reason: "OutOfMemoryError" }],
                  diagnosis: ["内存不足（OutOfMemoryError）→ 调大最大内存"], logTail: "[20:00:01] [main/INFO]: test log" },
  "capes-list": { capes: [{ id: "c1", alias: "Migrator", state: "ACTIVE" }, { id: "c2", alias: "MineCon", state: "INACTIVE" }] },
  "audit": { badCount: 1, findings: [{ name: "勒索诱饵", ok: true, detail: "5 个诱饵在位" },
                                     { name: "开机启动项", ok: false, detail: "1 个可疑项" }] },
  "mods-list": { dir: "C:\\mc\\mods", items: [
      { file: "sodium.jar", name: "sodium.jar", size: "1.1 MB", enabled: true, time: "2026-11-01" },
      { file: "jei.jar.disabled", name: "jei.jar", size: "6.2 MB", enabled: false, time: "2026-10-20" } ] },
  "skins-page": { page: 1, maxPages: 20, items: [
      { id: 1, url: "data:image/png;base64,iVBORw0KGgo=", slim: false, tags: ["Heroes"], wearers: 12 } ] },
  "stats": { seconds: 45360, launches: 23, text: "12 小时 36 分", max: 40000,
             perVersion: [{ version: "1.21.4", seconds: 40000, text: "11 小时", launches: 12 }] },
  "quarantine-list": { dir: "C:\\q", items: [{ quarantined: "C:\\q\\evil.jar.20261103.quar", file: "evil.jar.20261103.quar", original: "C:\\t\\evil.jar", reason: "x", time: "t", size: "1 KB" }] },
  "server-status": { host: "mc.hypixel.net", players: 30000, max: 100000, version: "1.8-1.21", motd: "Hypixel" },
  "dock": { account: { name: "Steve", kind: "离线模式", loggedIn: false },
            versions: { installed: 3, selected: "1.21.4" },
            ram: { total: 32768, avail: 12000, max: 6144, recommended: 6144 },
            java: { found: true, title: "Java 21（64 位）", path: "x", major: 21 },
            stats: { seconds: 45360, launches: 23, text: "12 小时 36 分", lastVersion: "1.21.4", lastPlayed: "11-02 21:00" },
            resources: { mods: 5, resourcepacks: 2, shaderpacks: 0, saves: 1, dataPacks: 0 },
            security: { guardRunning: true, pid: 1234, uptime: "2 小时", canaries: 5 },
            news: [{ date: "11-03", note: "主动拦截引擎上线" }] },
  "settings-get": { UseMirror: true, VersionIsolation: true, CloseToTray: false, AutoLogin: true, GameFullScreen: false, SecAlwaysGuard: true }
};
window.chrome = window.chrome || {};
window.chrome.webview = {
  postMessage: function (o) {
    window.__sent.push(o);
    if (o.cmd === "tool") {
      const d = FAKE[o.op] || {};
      setTimeout(function () {
        window.__handlers.forEach(function (h) {
          h({ data: { type: "tool", op: o.op, ok: true, msg: "已执行 " + o.op, data: d } });
        });
      }, 5);
    }
  },
  addEventListener: function (t, h) { if (t === "message") window.__handlers.push(h); }
};
window.onerror = function (m) { window.__errs.push(String(m)); };
</script>
'''

if '<head>' in s:
    i = s.index('<head>') + 6
    s = s[:i] + MOCK + s[i:]
else:
    i = s.index('<script>')
    s = s[:i] + MOCK + s[i:]

TEST = r'''
<script>
(async function () {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  await sleep(500);
  const report = { ops: [], settings: [], errors: [], missing: [] };
  const els = [...document.querySelectorAll("[data-op]")];
  for (const el of els) {
    const op = el.dataset.op;
    try {
      el.click();
      await sleep(40);
      const o = (window.__sent || []).filter(x => x.cmd === "tool" && x.op === op);
      report.ops.push({ op, posted: o.length > 0, args: o[0] || null });
    } catch (e) { report.errors.push(op + ": " + e.message); }
  }
  const sws = [...document.querySelectorAll("[data-setting]")];
  for (const sw of sws) {
    try { const was = sw.classList.contains("on"); sw.click(); await sleep(30);
      report.settings.push({ key: sw.dataset.setting, toggled: sw.classList.contains("on") !== was }); }
    catch (e) { report.errors.push(sw.dataset.setting + ": " + e.message); }
  }
  // 各页渲染检查
  await sleep(300);
  report.render = {
    toolOutLen: (document.getElementById("toolOut") || {}).innerHTML.length,
    modRows: document.querySelectorAll("#modList .ver-item").length,
    skinCards: document.querySelectorAll("#skinGrid .skin-card").length,
    qRows: document.querySelectorAll("#qList .canary-row").length,
    newsItems: document.querySelectorAll("#newsList .news-item").length,
    statHours: (document.getElementById("statHours") || {}).textContent,
    resMods: (document.getElementById("resMods") || {}).textContent,
    javaTxt: (document.getElementById("javaTxt") || {}).textContent,
    homeSec: (document.getElementById("homeSecMeta") || {}).textContent
  };
  report.errors = report.errors.concat(window.__errs);
  const pre = document.createElement("pre");
  pre.id = "__report";
  pre.textContent = JSON.stringify(report);
  document.body.appendChild(pre);

  /* 可视报告（用于截图读取） */
  const box = document.createElement("div");
  box.style.cssText = "position:fixed;inset:0;z-index:99999;background:#fff;color:#111;font:14px/1.55 monospace;padding:16px;overflow:auto;white-space:pre-wrap";
  const badOps = report.ops.filter(o => !o.posted).map(o => o.op);
  const lines = [];
  lines.push("ops=" + report.ops.length + "  posted=" + (report.ops.length - badOps.length) + "  missing=" + (badOps.join(",") || "none"));
  lines.push("settings=" + report.settings.map(s => s.key + ":" + (s.toggled ? "T" : "F")).join(" "));
  lines.push("render=" + JSON.stringify(report.render));
  lines.push("errors=" + (report.errors.join(" | ") || "none"));
  box.textContent = lines.join("\n");
  document.body.appendChild(box);
  for (let i = 1; i < 5000; i++) { clearInterval(i); clearTimeout(i); }
})();
</script>
'''

s = s.replace('</body>', TEST + '</body>', 1)
io.open(OUT, 'w', encoding='utf-8').write(s)
print('harness 写入', len(s))

EDGE = r'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
if not os.path.exists(EDGE):
    EDGE = r'C:\Program Files\Microsoft\Edge\Application\msedge.exe'

SHOT = r'D://NEXLauncher//tools//_report.png'
subprocess.run([EDGE, '--headless=new', '--disable-gpu', '--no-sandbox', '--hide-scrollbars',
                '--window-size=1280,900', '--virtual-time-budget=9000',
                '--screenshot=' + SHOT,
                '--user-data-dir=' + os.path.join(os.environ['TEMP'], 'nexharness2'),
                'file:///' + OUT.replace('\\', '/')],
               capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=150)
print('截图:', SHOT, os.path.exists(SHOT), os.path.getsize(SHOT) if os.path.exists(SHOT) else 0)
