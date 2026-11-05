import json, urllib.request, urllib.parse, sys

TOK = open(r'D:\NEXLauncher\release\.github-token').read().strip()
OWNER, REPO = 'synb-7891', 'NEXLauncher'
TAG = 'v4.0.2'
ZIP = r'D:\NEXLauncher\dist\NEXLauncher-4.0.2.zip'
NAME = 'NEXLauncher-4.0.2.zip'

def api(path, method='GET', data=None, base='https://api.github.com'):
    req = urllib.request.Request(base + path, method=method)
    req.add_header('Authorization', 'Bearer ' + TOK)
    req.add_header('Accept', 'application/vnd.github+json')
    req.add_header('User-Agent', 'NEX')
    if data is not None:
        req.data = data if isinstance(data, bytes) else json.dumps(data, ensure_ascii=False).encode('utf-8')
        req.add_header('Content-Type', 'application/zip' if isinstance(data, bytes) else 'application/json; charset=utf-8')
    with urllib.request.urlopen(req, timeout=600) as r:
        return json.loads(r.read().decode() or '{}')

BODY = """## NEX Launcher v4.0.2

### 修复："总是回退旧界面"的总根因
- 定位到本机存在**进程级时钟加速**干扰源（变速类工具钩住时间函数），导致 WebView2 内部
  所有超时瞬间到期，控制器创建必失败（0x800705B4），界面静默回退旧版
- 本版**不再使用任何按时间的初始化超时**：改为连续换目录重试 + 成功目录记忆（webshell.good），
  时钟正常窗口内一次即可绑定成功
- 绑定前主动整理内存，低内存机器上大幅提升 WebView2 启动成功率
- 初始化期间显示品牌加载页，任何情况下不再闪出旧界面
- 不再内置 sw.js，避免被安全防护模块误隔离弹告警

### 实测
- 冷启动 5.7 秒进入新版「开始页面」，控制器绑定 865ms（修复前连续 10 次失败）

### 下载
- `NEXLauncher-4.0.2.zip`：解压出 `NEXLauncher.exe`，双击即用（Windows 10/11 x64）
"""

# 已有同名 release 则复用
rel = None
try:
    rel = api(f'/repos/{OWNER}/{REPO}/releases/tags/{TAG}')
except Exception:
    pass

if rel and rel.get('id'):
    rid = rel['id']
    print('复用已有 release:', rid)
else:
    rel = api(f'/repos/{OWNER}/{REPO}/releases', 'POST', {
        'tag_name': TAG, 'name': 'NEX Launcher v4.0.2',
        'body': BODY, 'draft': False, 'prerelease': False})
    rid = rel['id']
    print('创建 release:', rid, rel.get('html_url'))

# 同名资产先删
for a in api(f'/repos/{OWNER}/{REPO}/releases/{rid}/assets'):
    if a.get('name') == NAME:
        api(f'/repos/{OWNER}/{REPO}/releases/assets/{a["id"]}', 'DELETE')
        print('已删除旧资产:', a['id'])

data = open(ZIP, 'rb').read()
url = f'https://uploads.github.com/repos/{OWNER}/{REPO}/releases/{rid}/assets?name=' + urllib.parse.quote(NAME)
req = urllib.request.Request(url, data=data, method='POST')
req.add_header('Authorization', 'Bearer ' + TOK)
req.add_header('Content-Type', 'application/zip')
req.add_header('User-Agent', 'NEX')
with urllib.request.urlopen(req, timeout=600) as r:
    up = json.loads(r.read().decode())

print('上传完成:', up.get('browser_download_url'), f'{up.get("size", 0)/1048576:.1f}MB')
print('RELEASE_ID=' + str(rid))
