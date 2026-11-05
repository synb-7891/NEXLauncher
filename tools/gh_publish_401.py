import json, urllib.request, urllib.parse, sys

TOK = open(r'D:\NEXLauncher\release\.github-token').read().strip()
OWNER, REPO = 'synb-7891', 'NEXLauncher'
TAG = 'v4.0.1'
ZIP = r'D:\NEXLauncher\dist\NEXLauncher-4.0.1.zip'
NAME = 'NEXLauncher-4.0.1.zip'

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

BODY = """## NEX Launcher v4.0.1

### 修复
- **双击打不开**：桌面旧版只是引导壳、缺运行库。本版为单文件自包含，拷到哪里都能跑
- **WebView2 初始化超时（0x800705B4）**：界面资源嵌入主程序 + UI 空闲后再建控制器 + 超时自动换目录重试
- 系统 WebView2 运行时已升到 142，旧 SDK 不兼容，本次同步升级到 1.0.4314.35

### 新增
- 「设置 → 联系我们」与侧栏「联系我们」显示官方 QQ 群 **1134583092**，一键复制群号
- QQ 频道入口收进群内：加群后从群公告进入
- 界面资源（HTML/图标）嵌入 exe，单文件即可运行

### 下载
- `NEXLauncher-4.0.1.zip`：解压出 `NEXLauncher.exe`，双击即用（Windows 10/11 x64）
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
        'tag_name': TAG, 'name': 'NEX Launcher v4.0.1',
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
