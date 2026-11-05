import json, urllib.request, urllib.parse, sys

TOK = open(r'D:\NEXLauncher\release\.github-token').read().strip()
OWNER, REPO = 'synb-7891', 'NEXLauncher'
TAG = 'v4.0.5'
ZIP = r'D:\NEXLauncher\dist\NEXLauncher-4.0.5.zip'
NAME = 'NEXLauncher-4.0.5.zip'

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

BODY = """## NEX Launcher v4.0.5

### 新增：PCL2 同款右下角悬浮按钮（强制结束 + 下载进度）
- **强制结束按钮**：游戏运行时出现在右下角，红色图标；两步确认防误触
  （第一次点击变「确认结束？」，3.5 秒内再点才真正结束全部游戏进程树）
- **下载进度按钮**：圆环实时显示活动任务平均进度，红色徽标显示进行中任务数；
  点击弹出下载管理面板，**游戏版本 / 资源·模组 两个分区**与 PCL2 布局一致
- 每个任务条目显示名称、进度条、百分比、已下载/总大小；支持一键「清空已完成」
- 下载中心数据由原生真实下载任务驱动（版本安装 / 模组 / 资源 / 整合包），
  1 秒级快照推送，无变化不发包（本地桥零开销）

### 修复与稳定化
- 沿用 v4.0.3/4.0.4 的线程亲和性与 Fabric/Quilt 全版本修复

### 下载
- `NEXLauncher-4.0.5.zip`：解压出 `NEXLauncher.exe`，双击即用（Windows 10/11 x64）
"""

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
        'tag_name': TAG, 'name': 'NEX Launcher v4.0.5',
        'body': BODY, 'draft': False, 'prerelease': False})
    rid = rel['id']
    print('创建 release:', rid, rel.get('html_url'))

# 删旧资产再传
for a in api(f'/repos/{OWNER}/{REPO}/releases/{rid}/assets'):
    if a['name'] == NAME:
        api(f"/repos/{OWNER}/{REPO}/releases/assets/{a['id']}", 'DELETE')
        print('已删除旧资产', a['name'])

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
