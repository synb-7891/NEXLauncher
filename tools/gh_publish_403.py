import json, urllib.request, urllib.parse, sys

TOK = open(r'D:\NEXLauncher\release\.github-token').read().strip()
OWNER, REPO = 'synb-7891', 'NEXLauncher'
TAG = 'v4.0.3'
ZIP = r'D:\NEXLauncher\dist\NEXLauncher-4.0.3.zip'
NAME = 'NEXLauncher-4.0.3.zip'

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

BODY = """## NEX Launcher v4.0.3

### 修复：皮肤站/模组管理永久卡在"正在读取…"
- **根因**：WebView2 的消息接口有线程亲和性，必须在 UI 线程调用；之前工具回复
  （mods-list / skins-page 等）都在后台线程里直接发送，调用抛异常被静默吞掉——
  C# 日志显示"回复成功"，页面却永远收不到，表现为永久加载
- 修复后所有发往页面的消息强制回 UI 线程

### 实测（CDP 直连页面端到端验证）
- 模组管理：正确显示扫描结果与 mods 目录路径
- 皮肤站：真实拉取 Ely.by 皮肤库，38 张皮肤卡片完整渲染（第 1/20 页）

### 下载
- `NEXLauncher-4.0.3.zip`：解压出 `NEXLauncher.exe`，双击即用（Windows 10/11 x64）
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
        'tag_name': TAG, 'name': 'NEX Launcher v4.0.3',
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
