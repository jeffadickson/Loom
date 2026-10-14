"""Integration test against the real running ASP.NET host. Python standard library only."""
import json
import time
import urllib.request
import urllib.error
from concurrent.futures import ThreadPoolExecutor

base = 'http://127.0.0.1:5080'
count = 0

def request(path, method='GET', body=None, role='Admin', headers=True):
    h = {'Content-Type': 'application/json'}
    if headers:
        h.update({'X-Loom-Demo': 'local', 'X-Demo-Role': role})
    r = urllib.request.Request(base + path, data=None if body is None else json.dumps(body).encode(), headers=h, method=method)
    try:
        with urllib.request.urlopen(r) as response:
            data = response.read()
            return response.status, json.loads(data) if data and 'json' in response.headers.get('Content-Type', '') else data.decode()
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()

def check(ok, text):
    global count
    assert ok, text
    print('PASS API:', text)
    count += 1

check(request('/api/meta', headers=False)[0] == 403, 'missing demo header denied')
check(request('/api/meta', role='Unknown')[0] == 403, 'unknown role denied')
check(request('/api/meta', role='Viewer')[0] == 200, 'metadata endpoint')
asset = {'name': '=FORMULA("x")', 'tag': 'API-' + str(time.time_ns()), 'category': 'Electronics', 'location': 'A', 'active': True}
status, d = request('/api/documents/Asset', 'POST', asset)
check(status == 201, 'create returns 201')
url = '/api/documents/Asset/' + d['id']
check(request(url, role='Viewer')[0] == 200, 'read by viewer')
check(request(url, 'PUT', {'version': 1, 'values': asset}, 'Viewer')[0] == 403, 'write by viewer denied')
check(request('/api/documents/Asset', 'POST', asset)[0] == 409, 'duplicate reports conflict')
with ThreadPoolExecutor(max_workers=5) as pool:
    responses = list(pool.map(lambda _: request(url, 'PUT', {'version': 1, 'values': asset})[0], range(5)))
check(responses.count(200) == 1 and responses.count(409) == 4, 'HTTP concurrency guard')
status, csv = request('/api/reports/Asset/csv?search=FORMULA')
check(status == 200 and "'=FORMULA" in csv, 'CSV formula neutralization')
check(request('/api/documents/Asset?sort=bad')[0] == 400, 'invalid sorting field')
check(request('/api/documents/Asset?take=201')[0] == 400, 'page bound')
check(request('/api/documents/DoesNotExist')[0] == 404, 'unknown model')
check(request(url, 'DELETE', role='Viewer')[0] == 400, 'required version binding')
check(request(url + '?version=2', 'DELETE')[0] == 204, 'delete returns 204')
check(request(url)[0] == 404, 'deleted document is absent')
check(len(request(url + '/audit')[1]) == 3, 'deleted record history retained')
print(f'{count} HTTP checks passed.')
