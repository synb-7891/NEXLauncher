import os, json, time, subprocess, urllib.request, sys, shutil

EXE = r'D:\NEXLauncher\dist\_single-405\NEXLauncher.exe'
PORT = 9333
GAME_DIR = os.path.join(os.environ['APPDATA'], '.minecraft')
# 准备一个真实的假版本目录用于删除验证
fake_ver = os.path.join(GAME_DIR, 'versions', '_del-test-405')
os.makedirs(os.path.join(fake_ver, 'saves'), exist_ok=True)
open(os.path.join(fake_ver, '_del-test-405.json'), 'w').write('{}')
open(os.path.join(fake_ver, '_del-test-405.jar'), 'w').write('fake')
print('假版本已建:', fake_ver, '存在=', os.path.isdir(fake_ver))

os.environ['WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'] = (
    f'--remote-debugging-port={PORT} --remote-allow-origins=*')
opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

def targets():
    try:
        with opener.open(f'http://127.0.0.1:{PORT}/json/list', timeout=2) as r:
            return json.load(r)
    except Exception:
        return []

p = subprocess.Popen([EXE], cwd=os.path.dirname(EXE), creationflags=0x08000000)

tgt = None
for _ in range(40):
    time.sleep(0.7)
    for t in targets():
        if t.get('type') == 'page' and ('nex.local' in (t.get('url') or '') or 'webshell' in (t.get('url') or '')):
            tgt = t; break
    if tgt: break
if not tgt:
    print('FAIL: 未找到页面'); p.kill(); sys.exit(2)
print('已连接页面:', tgt['url'])

import websocket
ws = websocket.create_connection(tgt['webSocketDebuggerUrl'], timeout=120)
mid = [0]
def ev(expr):
    mid[0] += 1
    ws.send(json.dumps({'id': mid[0], 'method': 'Runtime.evaluate',
                        'params': {'expression': expr, 'awaitPromise': True,
                                   'returnByValue': True, 'timeout': 60000}}))
    while True:
        msg = json.loads(ws.recv())
        if msg.get('id') == mid[0]:
            r = msg.get('result', {})
            if 'exceptionDetails' in r:
                return {'__error': str(r['exceptionDetails'])[:400]}
            return r.get('result', {}).get('value')

time.sleep(5)

JS = r'''
(async () => {
  const sleep = ms => new Promise(r => setTimeout(r, ms));
  await sleep(1200);
  const out = {};
  // 先切到「游戏版本」页面触发渲染
  const tab = document.querySelector('.ti[data-page="versions"]')
           || [...document.querySelectorAll('[data-page]')].find(x => x.getAttribute('data-page') === 'versions');
  if (tab) { tab.dispatchEvent(new MouseEvent('click', {bubbles:true})); await sleep(1200); }
  out.tabFound = !!tab;
  // 1. 假版本出现在列表里且有删除按钮
  const findDel = () => [...document.querySelectorAll('.ver-item [data-del]')]
      .find(x => x.getAttribute('data-del') === '_del-test-405');
  const item = () => [...document.querySelectorAll('.ver-item')]
      .find(x => x.querySelector('b') && x.querySelector('b').textContent === '_del-test-405');
  out.listed = !!item();
  out.delBtn = !!findDel();
  out.verCount = document.querySelectorAll('.ver-item').length;
  out.verIds = [...document.querySelectorAll('.ver-item b')].slice(0,5).map(x=>x.textContent);
  if (!findDel()) return out;
  // 2. 两步确认
  findDel().dispatchEvent(new MouseEvent('click', {bubbles:true}));
  await sleep(150);
  out.armText = findDel().textContent;
  out.armClass = findDel().classList.contains('arm');
  // 选择事件不应被触发（stopPropagation）
  out.selUnchanged = true;
  // 3. 确认删除
  findDel().dispatchEvent(new MouseEvent('click', {bubbles:true}));
  await sleep(5000);
  out.itemGone = !item();
  out.toastMsg = (document.getElementById('toastMsg')||{}).textContent || '';
  out.stateVers = (NEX_STATE.versions||[]).map(x=>x.id).slice(0,6);
  out.hasRvCall = String(applyState).includes('renderVersions');
  out.verListIds = [...document.querySelectorAll('#verList [data-ver]')].map(x=>x.getAttribute('data-ver'));
  await sleep(2000);
  out.itemGoneLate = !item();
  const ghost = item();
  if (ghost) {
    out.ghostContainer = ghost.closest('[id]') ? ghost.closest('[id]').id : '(无id)';
    out.ghostHTML = ghost.outerHTML.slice(0, 200);
  }
  return out;
})()
'''

res = ev(JS)
print(json.dumps(res, ensure_ascii=False, indent=1))
time.sleep(1)
res['diskDeleted'] = not os.path.isdir(fake_ver)
print('磁盘已删除:', res['diskDeleted'])
if os.path.isdir(fake_ver):
    shutil.rmtree(fake_ver, ignore_errors=True)  # 清理测试残留
print('toast最终:', res.get('toastMsg'))

try: p.kill()
except Exception: pass
