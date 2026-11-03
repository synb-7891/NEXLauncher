import json, sys, urllib.request, urllib.error

tok = open('release/.github-token').read().strip()
RID = 430559195

def api(p, method='GET', data=None):
    req = urllib.request.Request('https://api.github.com' + p, method=method)
    req.add_header('Authorization', 'Bearer ' + tok)
    req.add_header('Accept', 'application/vnd.github+json')
    req.add_header('User-Agent', 'NEX')
    if data is not None:
        req.data = json.dumps(data, ensure_ascii=False).encode('utf-8')
        req.add_header('Content-Type', 'application/json; charset=utf-8')
    with urllib.request.urlopen(req, timeout=120) as r:
        return json.loads(r.read().decode() or '{}')

assets = api(f'/repos/synb-7891/NEXLauncher/releases/{RID}/assets')
for a in assets:
    print(a['id'], repr(a['name']), round(a['size'] / 1048576, 2), 'MB')

target = [a for a in assets if a['size'] > 50 * 1048576][0]
new_name = 'NEXLauncher-0.4.0-\u516c\u5171\u7248.zip'
r = api(f'/repos/synb-7891/NEXLauncher/releases/assets/{target["id"]}', 'PATCH', {'name': new_name})
print('renamed ->', repr(r['name']), r['browser_download_url'])
