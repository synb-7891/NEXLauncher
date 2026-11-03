import base64, json, urllib.request

TOK = open('release/.github-token').read().strip()
OWNER, REPO = 'synb-7891', 'NEXLauncher'


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


def put_file(path, text, message):
    cur = None
    try:
        cur = api(f'/repos/{OWNER}/{REPO}/contents/{path}')
    except Exception:
        cur = None
    payload = {
        'message': message,
        'content': base64.b64encode(text.encode('utf-8')).decode(),
        'branch': 'main',
    }
    if cur:
        payload['sha'] = cur['sha']
    r = api(f'/repos/{OWNER}/{REPO}/contents/{path}', 'PUT', payload)
    print('committed', path, r['commit']['sha'][:8])


man = json.loads(base64.b64decode(api(f'/repos/{OWNER}/{REPO}/contents/manifest.json')['content']).decode('utf-8'))
man['notes'] = (
    'NEX 0.4.0：账号管理全面升级——多账号切换（互不覆盖）、外置登录（LittleSkin 等 Yggdrasil 服务器）、'
    '皮肤选择器与 3D 皮肤预览；已移除在线下载游戏版本功能，改为导入本机已有版本游玩；'
    '修复背景图透出桌面、资源图标/详情图片加载失败；联机隧道改良（授权链接自动复制）。'
    '完整版解压即用，无需安装 .NET 运行时。'
)
put_file('manifest.json', json.dumps(man, ensure_ascii=False, indent=2) + '\n', 'manifest: 修正 0.4.0 说明文字')

readme = '''# NEX Launcher

轻量的 Minecraft 第三方启动器（WPF / .NET 8），支持微软正版登录、离线登录、外置登录（Yggdrasil / authlib-injector）、
多账号管理、3D 皮肤预览、PCL2 风格的资源下载（整合包 / 模组 / 资源包 / 光影包 / 数据包）、联机隧道与工具箱。

## 下载

| 版本 | 文件 | 大小 | 说明 |
| --- | --- | --- | --- |
| 公共版（推荐） | [NEXLauncher-0.4.0-full.zip](https://github.com/synb-7891/NEXLauncher/releases/download/v0.4.0/NEXLauncher-0.4.0-full.zip) | 约 68 MB | 自包含版，解压即用，无需安装 .NET 运行时 |
| 精简版 | [NEXLauncher-0.4.0.zip](https://github.com/synb-7891/NEXLauncher/releases/download/v0.4.0/NEXLauncher-0.4.0.zip) | 约 1.5 MB | 需要电脑已安装 .NET 8 桌面运行时 |

全部版本见 [Releases](https://github.com/synb-7891/NEXLauncher/releases)。

## 0.4.0 说明

- **已移除在线下载游戏版本功能**，改为「导入本机版本」：自动扫描官方启动器 / PCL2 / HMCL 的 `.minecraft` 目录，
  一键导入已有版本，并识别 Fabric / Forge / NeoForge / Quilt / OptiFine。
- 账号管理：多账号互不覆盖、外置登录、皮肤库与 3D 皮肤预览。
- 资源下载按 PCL2 规格重做，走 MCIM 镜像加速。
- 需要 Windows 10 及以上（x64）。

## 更新源

`manifest.json` 为启动器自动更新清单，格式：

```json
{ "version": "0.4.0", "url": "更新包直链", "notes": "更新内容", "publishedAt": "2026-11-01" }
```

## 许可

见 [LICENSE.txt](LICENSE.txt)。本项目与 Mojang / Microsoft 无关。
'''
put_file('README.md', readme, 'docs: 添加 README 与下载说明')
print('done')
