# -*- coding: utf-8 -*-
# 端到端验证：PostJson 线程修复后，mods-list / skins-page 是否真正渲染
import os, json, time, subprocess, urllib.request, sys

EXE = r'D:\NEXLauncher\bin\Release\net8.0-windows\NEXLauncher.exe'
PORT = 9334
os.environ['WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'] = f'--remote-debugging-port={PORT} --remote-allow-origins=*'

p = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), creationflags=0x08000000)

def targets():
    try:
        with urllib.request.urlopen(f'http://127.0.0.1:{PORT}/json/list', timeout=2) as r:
            return json.load(r)
    except Exception:
        return []

tgt = None
for _ in range(40):
    time.sleep(0.7)
    for t in targets():
        if t.get('type') == 'page' and 'nex.local' in (t.get('url') or ''):
            tgt = t
            break
    if tgt:
        break

if not tgt:
    print('未找到页面目标')
    for t in targets():
        print('  ', t.get('type'), t.get('url'))
    try: p.kill()
    except Exception: pass
    sys.exit(2)

print('已连接:', tgt['url'][:60])

import websocket
ws = websocket.create_connection(tgt['webSocketDebuggerUrl'], timeout=60)
mid = [0]

def ev(expr, timeout=40):
    mid[0] += 1
    ws.send(json.dumps({'id': mid[0], 'method': 'Runtime.evaluate',
                        'params': {'expression': expr, 'awaitPromise': True,
                                   'returnByValue': True, 'timeout': timeout*1000}}))
    while True:
        msg = json.loads(ws.recv())
        if msg.get('id') == mid[0]:
            r = msg.get('result', {})
            if 'exceptionDetails' in r:
                return 'JS异常: ' + json.dumps(r['exceptionDetails'], ensure_ascii=False)[:200]
            return r.get('result', {}).get('value')

# 等 ready
for _ in range(40):
    time.sleep(0.7)
    if ev('document.readyState === "complete" && !!window.__loadMods') is True:
        break

print('--- 测试 1：模组扫描 ---')
r = ev('''(async () => {
  await window.__loadMods();
  return {
    hint: (document.getElementById("modHint")||{}).textContent || "",
    list: (document.getElementById("modList")||{}).innerHTML?.slice(0,150) || ""
  };
})()''')
print('modHint/modList:', json.dumps(r, ensure_ascii=False))

print('--- 测试 2：皮肤库拉取 ---')
r = ev('''(async () => {
  await window.__loadSkins(1);
  return {
    page: (document.getElementById("skinPage")||{}).textContent || "",
    count: (document.getElementById("skinCount")||{}).textContent || "",
    cards: document.querySelectorAll("#skinGrid .skin-card").length,
    first: (document.getElementById("skinGrid")||{}).innerHTML?.slice(0,120) || ""
  };
})()''')
print('皮肤页:', json.dumps(r, ensure_ascii=False))

try: p.kill()
except Exception: pass
print('完成')
