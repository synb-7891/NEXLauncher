import json, os, sys, urllib.request

TOK = open(r'D:\NEXLauncher\release\.github-token').read().strip()
OWNER, REPO = 'synb-7891', 'NEXLauncher'
TAG = 'v4.0.12'

BODY = """## NEX Launcher v4.0.12

### 自动更新从「弹窗问一下」改成「悄无声息装好」
- 启动 8 秒后在后台静默检查并下载新版本，全程不弹窗、不打断
- **关闭启动器时自动覆盖安装**，下次打开就是新版；游戏目录、账号、整合包、设置全部保留
- 程序目录不可写（装在 Program Files / 只读盘）时才退回原来的「询问后安装」，避免静默覆盖必然失败
- 同一份更新包最多重试 2 次，覆盖失败不会陷入死循环；`%LocalAppData%\\NEXLauncher\\update.log` 可查每次结果

### 新增：单实例保护
- 重复双击启动器只会把已有窗口拉到前台，不再开第二个进程
- 以前两个实例共用同一份 WebView2 配置目录，后开的那个几十秒后会自己退出，现在不会了

### 修复（都是真问题）
- **更新时中文路径必然失败**：安装脚本按系统 ANSI 解码，`C:\\Users\\吴\\...` 这类路径在批处理里会乱码，robocopy 报 123 直接放弃。现在脚本保持纯 ASCII，路径全部走环境变量传递
- **等待进程退出等于没等**：无控制台环境下 `timeout` 会立刻报错返回，旧脚本因此 2 秒就抢着覆盖。改用 `ping` 延时 + 按 PID 轮询
- 安装完成后清理临时解压目录（以前会残留 68MB）

### 下载
- `NEXLauncher-4.0.12.zip`：解压出 `NEXLauncher.exe`，双击即用（Windows 10/11 x64）
"""

def api(path, method='GET', data=None, raw=False, ctype=None):
    req = urllib.request.Request('https://api.github.com' + path, method=method)
    req.add_header('Authorization', 'Bearer ' + TOK)
    req.add_header('Accept', 'application/vnd.github+json')
    req.add_header('User-Agent', 'NEX')
    if data is not None:
        req.data = data
        req.add_header('Content-Type', ctype or 'application/json; charset=utf-8')
    with urllib.request.urlopen(req, timeout=900) as r:
        body = r.read()
        return body if raw else json.loads(body.decode() or '{}')

# 1. 建 release（已存在则复用）
rel = None
try:
    rel = api(f'/repos/{OWNER}/{REPO}/releases/tags/{TAG}')
    print('release exists', rel['id'])
except Exception:
    rel = api(f'/repos/{OWNER}/{REPO}/releases', method='POST', data=json.dumps({
        'tag_name': TAG, 'name': f'NEX Launcher {TAG}', 'body': BODY,
        'draft': False, 'prerelease': False
    }, ensure_ascii=False).encode('utf-8'))
    print('release created', rel['id'])

assets = {a['name']: a['id'] for a in rel.get('assets', [])}

# 2. 上传资产
for name in ['NEXLauncher-4.0.12.zip', 'NEXLauncher-4.0.12-update.zip']:
    path = os.path.join(r'D:\NEXLauncher\dist', name)
    size = os.path.getsize(path)
    if name in assets:
        api(f'/repos/{OWNER}/{REPO}/releases/assets/{assets[name]}', method='DELETE')
        print('deleted old asset', name)
    up = rel['upload_url'].split('{')[0] + '?name=' + urllib.request.quote(name)
    req = urllib.request.Request(up, method='POST', data=open(path, 'rb').read())
    req.add_header('Authorization', 'Bearer ' + TOK)
    req.add_header('Content-Type', 'application/octet-stream')
    req.add_header('User-Agent', 'NEX')
    with urllib.request.urlopen(req, timeout=1800) as r:
        a = json.loads(r.read().decode())
    print('uploaded', a['name'], a['size'], 'expected', size)
