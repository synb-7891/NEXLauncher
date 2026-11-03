import base64, json, os, urllib.error, urllib.parse, urllib.request

TOK = open('release/.github-token').read().strip()
OWNER, REPO = 'synb-7891', 'NEXLauncher'
RID = 430559195
FULL = 'release/NEXLauncher-0.4.0-公共版.zip'
LITE = 'release/NEXLauncher-0.4.0.zip'
FULL_NAME = 'NEXLauncher-0.4.0-full.zip'
LITE_NAME = 'NEXLauncher-0.4.0.zip'


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


def upload(path, name):
    data = open(path, 'rb').read()
    url = f'https://uploads.github.com/repos/{OWNER}/{REPO}/releases/{RID}/assets?name=' + urllib.parse.quote(name)
    req = urllib.request.Request(url, data=data, method='POST')
    req.add_header('Authorization', 'Bearer ' + TOK)
    req.add_header('Content-Type', 'application/zip')
    req.add_header('User-Agent', 'NEX')
    with urllib.request.urlopen(req, timeout=1800) as r:
        d = json.loads(r.read().decode())
        print('UPLOADED', d['name'], round(d['size'] / 1048576, 2), 'MB')
        return d


assets = api(f'/repos/{OWNER}/{REPO}/releases/{RID}/assets')
existing = {a['name']: a for a in assets}
print('current assets:', list(existing))

# 1) 重命名大包为 ASCII 名
big = [a for a in assets if a['size'] > 50 * 1048576]
if big and big[0]['name'] != FULL_NAME:
    if FULL_NAME in existing:
        api(f'/repos/{OWNER}/{REPO}/releases/assets/{existing[FULL_NAME]["id"]}', 'DELETE')
    r = api(f'/repos/{OWNER}/{REPO}/releases/assets/{big[0]["id"]}', 'PATCH', {'name': FULL_NAME})
    print('RENAMED ->', r['name'])

# 2) 上传精简版
if LITE_NAME not in existing:
    upload(LITE, LITE_NAME)
else:
    print('lite asset already exists')

# 3) 更新 release 说明中的文件名
body = api(f'/repos/{OWNER}/{REPO}/releases/{RID}')['body']
body = body.replace('`NEXLauncher-0.4.0-公共版.zip`', '`NEXLauncher-0.4.0-full.zip`')
api(f'/repos/{OWNER}/{REPO}/releases/{RID}', 'PATCH', {'body': body})
print('release body updated')

# 4) 更新仓库 manifest.json 指向 release 资源
man_path = f'/repos/{OWNER}/{REPO}/contents/manifest.json'
cur = api(man_path)
old = json.loads(base64.b64decode(cur['content']).decode('utf-8'))
new = {
    'version': '0.4.0',
    'url': f'https://github.com/{OWNER}/{REPO}/releases/download/v0.4.0/{FULL_NAME}',
    'notes': old['notes'],
    'publishedAt': old.get('publishedAt', '2026-11-01'),
}
payload = {
    'message': 'manifest: 0.4.0 指向公共版 release 资源',
    'content': base64.b64encode(json.dumps(new, ensure_ascii=False, indent=2).encode('utf-8')).decode(),
    'sha': cur['sha'],
    'branch': 'main',
}
r = api(man_path, 'PUT', payload)
print('manifest updated ->', r['commit']['sha'][:8])

for a in api(f'/repos/{OWNER}/{REPO}/releases/{RID}/assets'):
    print('ASSET', a['name'], round(a['size'] / 1048576, 2), 'MB', a['browser_download_url'])
