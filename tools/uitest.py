import os, json, time, subprocess, urllib.request, sys

EXE = r'D:\NEXLauncher\bin\Release\net8.0-windows\NEXLauncher.exe'
PORT = 9333
os.environ['WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'] = f'--remote-debugging-port={PORT}'

env = dict(os.environ)
p = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), env=env,
                     creationflags=0x08000000)

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
        if t.get('type') == 'page' and 'webshell' in (t.get('url') or ''):
            tgt = t
            break
    if tgt:
        break

if not tgt:
    print('未找到 WebView2 页面目标；当前 targets:')
    for t in targets():
        print('  ', t.get('type'), t.get('url'))
    try: p.kill()
    except Exception: pass
    sys.exit(2)

print('已连接页面:', tgt['url'])

import websocket
ws = websocket.create_connection(tgt['webSocketDebuggerUrl'], timeout=180)
mid = [0]
def ev(expr, await_promise=True, timeout=180):
    mid[0] += 1
    ws.send(json.dumps({'id': mid[0], 'method': 'Runtime.evaluate',
                        'params': {'expression': expr, 'awaitPromise': await_promise,
                                   'returnByValue': True, 'timeout': timeout * 1000}}))
    while True:
        msg = json.loads(ws.recv())
        if msg.get('id') == mid[0]:
            r = msg.get('result', {})
            if 'exceptionDetails' in r:
                return {'__error': str(r['exceptionDetails'])[:500]}
            return r.get('result', {}).get('value')

time.sleep(3.5)

JS = r'''
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  const click = el => { if (!el) return false; el.dispatchEvent(new MouseEvent('click', {bubbles:true})); return true; };
  const txt = id => (document.getElementById(id) || {}).textContent || '';
  const out = { home: {}, ops: {} };

  out.bridge = !!(window.chrome && window.chrome.webview);
  out.home = {
    news: document.querySelectorAll('#newsList .news-item').length,
    hours: txt('statHours'), launches: txt('statLaunches'),
    ram: txt('ramTxt'), java: txt('javaTxt'),
    mods: txt('resMods'), saves: txt('resSaves'),
    sec: txt('homeSecMeta'), last: txt('lastPlayTxt')
  };

  const run = async (op, wait) => {
    const el = document.querySelector('[data-op="' + op + '"]');
    if (!el) { out.ops[op] = 'NO-ELEMENT'; return; }
    click(el);
    await sleep(wait || 2500);
    const w = document.getElementById('toolOut');
    out.ops[op] = (w ? w.textContent : '').replace(/\s+/g, ' ').slice(0, 220);
  };

  await run('java-list', 3000);
  await run('mem-info', 2500);
  await run('saves-list', 3000);
  await run('crash-list', 3500);
  await run('audit', 4000);
  await run('quarantine-list', 2500);

  // 模组页
  click(document.querySelector('[data-page="mods"]'));
  await sleep(2000);
  out.mods = { hint: txt('modHint'), rows: document.querySelectorAll('#modList .ver-item').length };

  // 皮肤页
  click(document.querySelector('[data-page="skins"]'));
  await sleep(4500);
  out.skins = {
    cards: document.querySelectorAll('#skinGrid .skin-card').length,
    page: txt('skinPage'),
    first: (document.querySelector('#skinGrid .skin-meta') || {}).textContent || ''
  };

  // 安全页
  click(document.querySelector('[data-page="security"]'));
  await sleep(2500);
  out.security = {
    q: document.querySelectorAll('#qList .canary-row').length,
    meta: (document.querySelector('#page-security .sec-hero .meta') || {}).textContent || '',
    events: document.querySelectorAll('#page-security .log-row').length
  };

  // 设置开关真实值
  click(document.querySelector('[data-page="settings"]'));
  await sleep(1500);
  out.settings = [...document.querySelectorAll('[data-setting]')].map(s => s.dataset.setting + '=' + s.classList.contains('on'));
  return JSON.stringify(out);
})()
'''

res = ev(JS, timeout=180)
print('\n================ 真实界面端到端结果 ================')
if isinstance(res, dict) and '__error' in res:
    print('执行异常:', res['__error'][:400])
else:
    try:
        data = json.loads(res) if isinstance(res, str) else res
        print(json.dumps(data, ensure_ascii=False, indent=2))
    except Exception:
        print(res)

try: ws.close()
except Exception: pass
time.sleep(0.5)
try: p.kill()
except Exception: pass
print('\n已关闭测试实例')
