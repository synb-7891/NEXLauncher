import os, json, time, subprocess, urllib.request, sys

EXE = r'D:\NEXLauncher\dist\_single-405\NEXLauncher.exe'
PORT = 9333
os.environ['WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'] = (
    f'--remote-debugging-port={PORT} --remote-allow-origins=*')

opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

def targets():
    try:
        with opener.open(f'http://127.0.0.1:{PORT}/json/list', timeout=2) as r:
            return json.load(r)
    except Exception:
        return []

p = subprocess.Popen([EXE], cwd=os.path.dirname(EXE),
                     creationflags=0x08000000)

tgt = None
for _ in range(40):
    time.sleep(0.7)
    for t in targets():
        if t.get('type') == 'page' and ('webshell' in (t.get('url') or '')
                                        or 'nex.local' in (t.get('url') or '')):
            tgt = t
            break
    if tgt:
        break

if not tgt:
    print('FAIL: 未找到 webshell 页面')
    for t in targets():
        print('  ', t.get('type'), t.get('url'))
    try: p.kill()
    except Exception: pass
    sys.exit(2)

print('已连接页面:', tgt['url'])

import websocket
ws = websocket.create_connection(tgt['webSocketDebuggerUrl'], timeout=120)
mid = [0]
def ev(expr, await_promise=True):
    mid[0] += 1
    ws.send(json.dumps({'id': mid[0], 'method': 'Runtime.evaluate',
                        'params': {'expression': expr, 'awaitPromise': await_promise,
                                   'returnByValue': True, 'timeout': 60000}}))
    while True:
        msg = json.loads(ws.recv())
        if msg.get('id') == mid[0]:
            r = msg.get('result', {})
            if 'exceptionDetails' in r:
                return {'__error': str(r['exceptionDetails'])[:400]}
            return r.get('result', {}).get('value')

time.sleep(4)

JS = r'''
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  await sleep(1500);
  const out = {};
  // 1. 版本号
  out.version = (document.body.innerText.match(/v?4\.0\.5/) || [])[0] || '(无)';
  // 2. 三个角标元素存在
  out.fabStop = !!document.getElementById('killBtn');
  out.fabDl = !!document.getElementById('dlFab');
  out.panel = !!document.getElementById('dlPanel');
  // 3. 注入下载快照 → 徽标 / 圆环 / 面板
  onDlTasks({game:[{name:'1.21.11 测试版本', pct:42, status:'downloading', detail:'12MB/28MB'},
                   {name:'1.20.1 Forge', pct:100, status:'done', detail:'完成'}],
             mod:[{name:'sodium.jar', pct:80, status:'downloading', detail:'1.1MB/1.4MB'}]});
  await sleep(300);
  out.badge = document.getElementById('dlBadge').textContent;
  out.icoText = document.getElementById('dlIco').textContent;
  // 打开面板检查分区渲染
  document.getElementById('dlFab').dispatchEvent(new MouseEvent('click', {bubbles:true}));
  await sleep(200);
  const g = document.getElementById('dlGame'), m = document.getElementById('dlMod');
  out.panelOpen = document.getElementById('dlPanel').classList.contains('show');
  out.gameSec = (g.innerText.match(/1\.21\.11 测试版本|42/) || [])[0] || '(空)';
  out.modSec = (m.innerText.match(/sodium\.jar|80/) || [])[0] || '(空)';
  // 4. 强制结束两步确认
  const kb = document.getElementById('killBtn');
  out.killVisible = kb.classList.contains('show');
  kb.dispatchEvent(new MouseEvent('click', {bubbles:true}));
  await sleep(100);
  out.killArm1 = kb.classList.contains('arm') && kb.querySelector('span').textContent.includes('确认');
  kb.dispatchEvent(new MouseEvent('click', {bubbles:true}));
  await sleep(100);
  out.killArm2 = !kb.classList.contains('arm');
  await sleep(600);
  // 5. 游戏运行事件 → 按钮显隐
  onGameRunning({running:true});
  await sleep(150);
  out.killShownWhenRun = kb.classList.contains('show');
  onGameRunning({running:false});
  await sleep(150);
  out.killHiddenWhenExit = !kb.classList.contains('show');
  return out;
})()
'''

res = ev(JS)
print(json.dumps(res, ensure_ascii=False, indent=1))

# 截图留档
try:
    mid[0] += 1
    ws.send(json.dumps({'id': mid[0], 'method': 'Page.captureScreenshot',
                        'params': {'format': 'png'}}))
    while True:
        msg = json.loads(ws.recv())
        if msg.get('id') == mid[0]:
            import base64
            data = base64.b64decode(msg['result']['data'])
            open(r'D:\NEXLauncher\tools\_verify_405.png', 'wb').write(data)
            print('截图: tools/_verify_405.png')
            break
except Exception as e:
    print('截图失败:', e)

try: p.kill()
except Exception: pass
