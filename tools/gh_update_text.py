import base64, json, urllib.request

TOK = open('release/.github-token').read().strip()
OWNER, REPO = 'synb-7891', 'NEXLauncher'
RID = 430559195


def api(path, method='GET', data=None):
    req = urllib.request.Request('https://api.github.com' + path, method=method)
    req.add_header('Authorization', 'Bearer ' + TOK)
    req.add_header('Accept', 'application/vnd.github+json')
    req.add_header('User-Agent', 'NEX')
    if data is not None:
        req.data = json.dumps(data, ensure_ascii=False).encode('utf-8')
        req.add_header('Content-Type', 'application/json; charset=utf-8')
    with urllib.request.urlopen(req, timeout=180) as r:
        return json.loads(r.read().decode() or '{}')


extra = '''### 修复与新功能
- 修复 playit.gg 授权页报错：改为启动器内置授权窗口（不受浏览器自动翻译影响），保留复制链接与系统浏览器入口
- 修复外置登录报「requestUser / agent must be...」：登录请求改为标准 Yggdrasil JSON
- 修复正版账号头像与皮肤不显示：改走官方会话服务器 + 多源头像兜底；3D 皮肤预览同步修复
- 修复启动报「Already installing」：安装改用独立实例并串行化
- 新增：版本设置（图标/名称/描述/收藏/内存/Java/导出启动脚本/补全文件/删除版本）、模组管理（启用禁用/删除/筛选/检查更新/导出整合包）、Java 管理（扫描/下载/导入）、基岩版（启动/导入/商店）、百宝箱（内存优化/清理垃圾/直链下载）、正版披风切换、工具箱资源导航
- 官方默认皮肤 9 套（Steve / Alex / Ari / Efe / Kai / Makena / Noor / Sunny / Zuri，宽/细双模型）内置离线可用
- 3D 皮肤预览增强：多光源明暗面、关节摆动动画、地面投影、惯性拖动
- 公共版「游戏版本」页增加「悲报」公告（无在线下载，仅导入本机版本）
'''

rel = api(f'/repos/{OWNER}/{REPO}/releases/{RID}')
body = rel['body']
if '### 修复与新功能' not in body:
    body = body.rstrip() + '\n\n' + extra
api(f'/repos/{OWNER}/{REPO}/releases/{RID}', 'PATCH', {'body': body})
print('release body updated')

man_path = f'/repos/{OWNER}/{REPO}/contents/manifest.json'
cur = api(man_path)
man = json.loads(base64.b64decode(cur['content']).decode('utf-8'))
man['notes'] = (
    'NEX 0.4.0：修复 playit.gg 授权页报错（内置授权窗口）、外置登录、正版头像/皮肤、启动 Already installing；'
    '新增版本设置、模组管理、Java 管理、基岩版、百宝箱、正版披风、9 套官方默认皮肤与 3D 皮肤预览增强。'
    '完整版解压即用，无需安装 .NET 运行时。'
)
man['publishedAt'] = '2026-11-01'
payload = {
    'message': 'manifest: 0.4.0 更新说明',
    'content': base64.b64encode(json.dumps(man, ensure_ascii=False, indent=2).encode('utf-8')).decode(),
    'sha': cur['sha'],
    'branch': 'main',
}
r = api(man_path, 'PUT', payload)
print('manifest updated ->', r['commit']['sha'][:8])
