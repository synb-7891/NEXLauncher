import os, json, time, subprocess, urllib.request, glob
EXE = r'D:\NEXLauncher\dist\_single-405\NEXLauncher.exe'
os.environ['WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'] = '--remote-debugging-port=9333 --remote-allow-origins=*'
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))
p = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), creationflags=0x08000000)
tgt = None
for _ in range(45):
    time.sleep(0.7)
    try:
        with opener.open('http://127.0.0.1:9333/json/list', timeout=2) as r:
            ts = json.load(r)
        tgt = next((t for t in ts if t.get('type') == 'page' and 'nex.local' in (t.get('url') or '')), None)
    except Exception:
        pass
    if tgt:
        break
if not tgt:
    print('FAIL 未找到页面'); p.kill(); raise SystemExit(2)

import websocket
ws = websocket.create_connection(tgt['webSocketDebuggerUrl'], timeout=300)
mid = [0]
def ev(expr):
    mid[0] += 1
    ws.send(json.dumps({'id': mid[0], 'method': 'Runtime.evaluate',
                        'params': {'expression': expr, 'awaitPromise': True,
                                   'returnByValue': True, 'timeout': 300000}}))
    while True:
        m = json.loads(ws.recv())
        if m.get('id') == mid[0]:
            r = m.get('result', {})
            if 'exceptionDetails' in r:
                return {'__err': str(r['exceptionDetails'])[:400]}
            return r.get('result', {}).get('value')

time.sleep(5)

# ---------- 1. Sodium 弹窗：过滤态（服务端只给适配版本） ----------
out = ev(r'''(async()=>{
  const sleep=ms=>new Promise(r=>setTimeout(r,ms));
  await sleep(1000);
  const o={};
  window.startContentDownload('sodium','Sodium','mod');
  await sleep(500);
  o.maskOpen = document.getElementById('verMask').classList.contains('show');
  o.title = (document.querySelector('#verMask .dep-title b')||{}).textContent;
  for(let i=0;i<75;i++){ await sleep(400); if(document.querySelectorAll('#verRows .vrow').length) break; }
  const rows=[...document.querySelectorAll('#verRows .vrow')];
  o.rowCount = rows.length;
  o.cntTxt = (document.getElementById('verCnt')||{}).textContent;
  o.fitTxt = (document.getElementById('verFitTxt')||{}).textContent;
  o.subTxt = (document.getElementById('verSub')||{}).textContent;
  o.badges = document.querySelectorAll('#verRows .vbadge').length;
  o.firstRow = rows.length? rows[0].innerText.replace(/\n/g,' | ') : '';
  o.hasMcPill = !!document.querySelector('#verRows .vpill.mc');
  o.selCount = document.querySelectorAll('#verRows .vrow.sel').length;
  o.goEnabled = !document.getElementById('verGo').disabled;
  o.onlyFitOn = document.getElementById('verFitOnly').classList.contains('on');
  return o;
})()''')
print('--- 1 版本弹窗（默认只看适配） ---')
print(json.dumps(out, ensure_ascii=False, indent=1))

# ---------- 2. 关掉过滤 → 拉全量 ----------
out2 = ev(r'''(async()=>{
  const sleep=ms=>new Promise(r=>setTimeout(r,ms));
  const o={};
  document.getElementById('verFitOnly').click();
  await sleep(600);
  o.loadingTxt = (document.getElementById('verRows')||{}).innerText.slice(0,40);
  for(let i=0;i<110;i++){ await sleep(400); if(document.querySelectorAll('#verRows .vrow').length) break; }
  o.rowCountAll = document.querySelectorAll('#verRows .vrow').length;
  o.cntAll = (document.getElementById('verCnt')||{}).textContent;
  o.onlyFitOn = document.getElementById('verFitOnly').classList.contains('on');
  // 重新打开过滤（此时是全量数据，本地过滤）
  document.getElementById('verFitOnly').click();
  await sleep(500);
  o.rowCountBack = document.querySelectorAll('#verRows .vrow').length;
  o.cntBack = (document.getElementById('verCnt')||{}).textContent;
  return o;
})()''')
print('--- 2 关掉过滤拉全量 ---')
print(json.dumps(out2, ensure_ascii=False, indent=1))

# ---------- 3. 选 1.21.11 版本真实下载（资源包，落盘后清理） ----------
start = time.time()
out3 = ev(r'''(async()=>{
  const sleep=ms=>new Promise(r=>setTimeout(r,ms));
  const o={};
  window.startContentDownload('faithful-32x','Faithful 32x','resourcepack');
  for(let i=0;i<75;i++){ await sleep(400); if(document.querySelectorAll('#verRows .vrow').length) break; }
  const rows=[...document.querySelectorAll('#verRows .vrow')];
  o.rows = rows.length;
  o.cnt = (document.getElementById('verCnt')||{}).textContent;
  const pick = rows.find(r=>/1\.21\.11/i.test(r.innerText)) || rows[0];
  o.pick = pick? pick.innerText.replace(/\n/g,' | ') : '(空)';
  if(!pick) return o;
  pick.dispatchEvent(new MouseEvent('click',{bubbles:true}));
  await sleep(250);
  document.getElementById('verGo').click();
  await sleep(700);
  o.maskClosed = !document.getElementById('verMask').classList.contains('show');
  o.depShown = document.getElementById('depMask').classList.contains('show');
  o.toast1 = (document.getElementById('toastMsg')||{}).textContent;
  return o;
})()''')
print('--- 3 选版本并开始下载 ---')
print(json.dumps(out3, ensure_ascii=False, indent=1))

# 等落盘
rp = os.path.join(os.environ['APPDATA'], '.minecraft', 'resourcepacks')
found = None
for _ in range(45):
    time.sleep(1.5)
    hits = [f for f in glob.glob(os.path.join(rp, '*')) if os.path.getmtime(f) > start - 2]
    if hits:
        found = max(hits, key=os.path.getmtime)
        break
print('--- 4 落盘检查 ---')
print('目录:', rp)
print('新文件:', found, ('%.1f MB' % (os.path.getsize(found)/1048576)) if found else '')

# ---------- 5. Iris：确认前置弹窗按所选版本匹配到 Sodium ----------
out5 = ev(r'''(async()=>{
  const sleep=ms=>new Promise(r=>setTimeout(r,ms));
  const o={};
  window.startContentDownload('iris','Iris Shaders','mod');
  for(let i=0;i<75;i++){ await sleep(400);
    const s=(document.getElementById('verSub')||{}).textContent||'';
    if(document.querySelectorAll('#verRows .vrow').length && s.includes('Iris')) break; }
  const rows=[...document.querySelectorAll('#verRows .vrow')];
  o.irisRows = rows.length;
  o.irisSub = (document.getElementById('verSub')||{}).textContent;
  const pick = rows.find(r=>/1\.21\.11/i.test(r.innerText)) || rows[0];
  o.irisPick = pick? pick.innerText.replace(/\n/g,' | ') : '(空)';
  if(!pick) return o;
  pick.dispatchEvent(new MouseEvent('click',{bubbles:true}));
  await sleep(250);
  document.getElementById('verGo').click();
  for(let i=0;i<50;i++){ await sleep(400); if(document.getElementById('depMask').classList.contains('show')) break; }
  o.depShown = document.getElementById('depMask').classList.contains('show');
  o.depSub = (document.getElementById('depSub')||{}).textContent;
  o.depNames = [...document.querySelectorAll('#depList .dep-item b')].map(x=>x.textContent);
  o.depFiles = [...document.querySelectorAll('#depList .dep-item .meta')].map(x=>x.textContent);
  // 不真下载：清掉待下载项并关闭弹窗
  pendingDl = null;
  document.getElementById('depMask').classList.remove('show');
  return o;
})()''')
print('--- 5 Iris 前置弹窗 ---')
print(json.dumps(out5, ensure_ascii=False, indent=1))

# 截图弹窗（重开一次 Sodium 供视觉检查）
ev(r'''(async()=>{const sleep=ms=>new Promise(r=>setTimeout(r,ms));
  window.startContentDownload('sodium','Sodium','mod');
  for(let i=0;i<60;i++){ await sleep(400); if(document.querySelectorAll('#verRows .vrow').length) break; }
  await sleep(400); return true;})()''')
time.sleep(1)
mid[0] += 1
ws.send(json.dumps({'id': mid[0], 'method': 'Page.captureScreenshot', 'params': {'format': 'png'}}))
while True:
    m = json.loads(ws.recv())
    if m.get('id') == mid[0]:
        import base64
        open(r'D:\NEXLauncher\tools\_verpick.png', 'wb').write(base64.b64decode(m['result']['data']))
        print('截图: tools/_verpick.png')
        break
p.kill()
print('DONE')
