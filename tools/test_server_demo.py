"""Exercise an actual configured demo host against an isolated CI database, twice."""
import json,os,subprocess,sys,tempfile,time,urllib.request,urllib.error
from pathlib import Path
root=Path(__file__).resolve().parents[1]
dotnet=sys.argv[1] if len(sys.argv)>1 else 'dotnet'
assert os.environ.get('Database__Provider') in ('PostgreSql','SqlServer','Sqlite')
assert os.environ.get('Database__ConnectionString')
def request(path,body=None,role='Admin'):
    req=urllib.request.Request('http://127.0.0.1:5084'+path,data=None if body is None else json.dumps(body).encode(),headers={'X-Loom-Demo':'local','X-Demo-Role':role,'Content-Type':'application/json'})
    return json.load(urllib.request.urlopen(req,timeout=20))
with tempfile.TemporaryDirectory() as tmp:
    for iteration in range(2):
        logpath=Path(tmp)/'host.log'
        with logpath.open('w') as log:
            host=subprocess.Popen([dotnet,'bin/Debug/net8.0/Loom.Demo.dll','--science-demo'],cwd=root/'src/Loom.Demo',env=dict(os.environ,LOOM_PORT='5084'),stdout=log,stderr=log)
            try:
                for _ in range(200):
                    try:meta=request('/api/meta');break
                    except Exception:
                        if host.poll() is not None:raise RuntimeError(logpath.read_text())
                        time.sleep(.1)
                else:raise RuntimeError(logpath.read_text())
                assert meta['provider']['name'].lower()==os.environ['Database__Provider'].lower()
                rows=request('/api/documents/TestResult');assert rows['total']==2+iteration
                if iteration==0:
                    values={'Name':'Persistent API example','Outcome':'Pass','Cost':'12345678901234567.89','SampleCount':'9007199254740993'}
                    saved=request('/api/documents/TestResult',values);identifier=saved['id']
                    try:request('/api/documents/TestResult',values,role='Viewer');raise AssertionError('Viewer wrote a document')
                    except urllib.error.HTTPError as e:assert e.code==403
                read=request('/api/documents/TestResult/'+identifier)
                assert read['values']['Cost']=='12345678901234567.89' and read['values']['SampleCount']=='9007199254740993'
                assert len(request('/api/documents/TestResult/'+identifier+'/audit'))==1
                assert request('/api/documents/TestResult?sort=Cost&descending=true&take=1')['items'][0]['id']==identifier
                print('PASS configured demo host:',meta['provider']['name'],'restart' if iteration else 'create','exact values, query, audit and seed count')
            finally:host.terminate();host.wait(timeout=10)
