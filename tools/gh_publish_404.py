import json, urllib.request, urllib.parse, sys

TOK = open(r'D:\NEXLauncher\release\.github-token').read().strip()
OWNER, REPO = 'synb-7891', 'NEXLauncher'
TAG = 'v4.0.4'
ZIP = r'D:\NEXLauncher\dist\NEXLauncher-4.0.4.zip'
NAME = 'NEXLauncher-4.0.4.zip'

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

BODY = """## NEX Launcher v4.0.4

### 修复：1.21+ 版本 Fabric 显示「暂无可用加载器」/ 只有一个版本
- **根因一（显示层）**：C# 回传 Fabric/Quilt 数据时用的是裸数组，网页端按
  `{versions:[...]}` 对象格式解析——数据其实每次都成功拿到了，却被当成"失败"丢弃
- **根因二（数据层）**：Fabric 官方 meta 只把最新一个 loader 标记为稳定版，
  之前只过滤稳定版导致下拉框永远只剩 1 个；现在返回全部版本（最新稳定版排首位，
  默认选中），与 HMCL/PCL2 行为一致
- 数据源增加 BMCLAPI 国内镜像双线路 + 自动重试（官方源失败自动切换）

### 实测（CDP 直连页面端到端验证）
- 1.21.11 弹窗：Fabric「最新 0.20.1 · 可以添加」，下拉 30 个版本
  （0.20.1 / 0.20.0 / 0.19.5 / 0.19.4 / 0.19.3 …），Forge 不受影响

### 下载
- `NEXLauncher-4.0.4.zip`：解压出 `NEXLauncher.exe`，双击即用（Windows 10/11 x64）
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
        'tag_name': TAG, 'name': 'NEX Launcher v4.0.4',
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
