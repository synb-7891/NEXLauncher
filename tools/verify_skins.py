"""皮肤站验证：1000 页 / 页码跳转 / 应用反馈。直接拉起带 CDP 的启动器进程。"""
import os, json, time, subprocess, urllib.request, base64, sys

EXE = r'D:\NEXLauncher\dist\_single-405\NEXLauncher.exe'
PORT = 9333
os.environ['WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS'] = f'--remote-debugging-port={PORT} --remote-allow-origins=*'
for k in list(os.environ):
    if 'proxy' in k.lower(): os.environ.pop(k, None)

p = subprocess.Popen([EXE], creationflags=0x08000000)

def targets():
    try:
        op = urllib.request.build_opener(urllib.request.ProxyHandler({}))
        with op.open(f'http://127.0.0.1:{PORT}/json/list', timeout=2) as r:
            return json.load(r)
    except Exception:
        return []

tgt = None
for _ in range(40):
    time.sleep(0.7)
    for t in targets():
        if t.get('type') == 'page' and 'nex.local' in (t.get('url') or ''):
            tgt = t; break
    if tgt: break
if not tgt:
    print('FAIL: 未找到页面目标'); sys.exit(1)

import websocket
ws = websocket.create_connection(tgt['webSocketDebuggerUrl'], timeout=90)
seq = [0]
def ev(expr, await_p=False):
    seq[0] += 1
    ws.send(json.dumps({'id': seq[0], 'method': 'Runtime.evaluate',
                        'params': {'expression': expr, 'awaitPromise': await_p, 'returnByValue': True}}))
    while True:
        m = json.loads(ws.recv())
        if m.get('id') == seq[0]:
            return m['result']['result'].get('value')

ok = True
def check(name, cond):
    global ok
    print(('PASS' if cond else 'FAIL'), name)
    if not cond: ok = False

label = ev("(async()=>{if(window.goto)goto('skins');"
           "await new Promise(r=>setTimeout(r,3000));"
           "if(window.__loadSkins)await window.__loadSkins(1);"
           "await new Promise(r=>setTimeout(r,4500));"
           "return document.getElementById('skinPage').textContent})()", True)
check('总页数显示 1000（实际：' + str(label) + '）', '1000' in str(label))
check('首页 10 个卡片', ev("document.querySelectorAll('#skinGrid .skin-card').length") == 10)
check('黑名单生效（无 Hitler 皮肤）',
      not ev("document.getElementById('skinGrid').innerHTML.toLowerCase().includes('hitler')"))

ev("document.getElementById('skinPage').click()")
check('点页数弹出跳层', ev("document.getElementById('skinJump').style.display") != 'none')
check('页码按钮共 1000 个', ev("document.querySelectorAll('#skinJumpGrid .sjump').length") == 1000)
check('当前页高亮', ev("document.querySelector('#skinJumpGrid .sjump.cur')?.dataset.p") == '1')

ev("(async()=>{document.querySelector('#skinJumpGrid .sjump[data-p=\"500\"]').click();"
   "await new Promise(r=>setTimeout(r,4500))})()", True)
label2 = ev("document.getElementById('skinPage').textContent")
check('点 500 直接跳转（实际：' + str(label2) + '）', '500' in str(label2))
check('500 页 10 个卡片', ev("document.querySelectorAll('#skinGrid .skin-card').length") == 10)

ev("(async()=>{document.getElementById('skinPage').click();"
   "document.querySelector('#skinJumpInput').value=987;"
   "document.querySelector('#skinJumpInput').dispatchEvent(new KeyboardEvent('keydown',{key:'Enter'}));"
   "await new Promise(r=>setTimeout(r,4500))})()", True)
label3 = ev("document.getElementById('skinPage').textContent")
check('输入 987 回车跳转（实际：' + str(label3) + '）', '987' in str(label3))

# 应用按钮反馈：模拟点击第一张卡片的「应用」后出现 toast
ev("(async()=>{document.querySelector('#skinGrid .skin-card .t-btn').click();"
   "await new Promise(r=>setTimeout(r,6000))})()", True)
toast_txt = ev("document.getElementById('toastMsg')?.textContent || ''")
print('应用后 toast:', toast_txt)
check('点「应用」出现可见反馈', bool(toast_txt))

ev("document.getElementById('skinJump').style.display='none'")
seq[0] += 1
ws.send(json.dumps({'id': seq[0], 'method': 'Page.captureScreenshot', 'params': {'format': 'png'}}))
while True:
    m = json.loads(ws.recv())
    if m.get('id') == seq[0]:
        open(r'D:\NEXLauncher\tools\_skins_1000.png', 'wb').write(base64.b64decode(m['result']['data']))
        break
print('截图: tools/_skins_1000.png')
ws.close()
p.terminate()
print('RESULT:', 'ALL PASS' if ok else 'HAS FAILURES')
