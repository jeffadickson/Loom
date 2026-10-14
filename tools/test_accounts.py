"""Exercise real cookie login, role spoofing, row scopes and field redaction over HTTP."""
import base64, hashlib, http.cookiejar, json, os, struct, subprocess, sys, tempfile, time, urllib.request, urllib.error
from pathlib import Path
root=Path(__file__).resolve().parents[1]
dotnet=sys.argv[1] if len(sys.argv)>1 else 'dotnet'
password='temporary-test-password-only'
salt=os.urandom(16)
password_hash=base64.b64encode(b'\x01'+struct.pack('>III',2,100000,len(salt))+salt+hashlib.pbkdf2_hmac('sha512',password.encode(),salt,100000,32)).decode()
env=dict(os.environ,LOOM_PORT='5086',Authentication__Mode='Accounts')
for i,(name,role) in enumerate([('administrator','Admin'),('analyst','Editor'),('other','Editor')]):
    for key,value in [('Id',name),('Role',role),('PasswordHash',password_hash)]:env[f'Authentication__Accounts__{i}__{key}']=value
base='http://127.0.0.1:5086'
def request(opener,path,body=None,method=None):
    req=urllib.request.Request(base+path,data=None if body is None else json.dumps(body).encode(),method=method,headers={'Content-Type':'application/json','X-Loom-Demo':'local','X-Demo-Role':'Admin'})
    try:
        with opener.open(req) as r:return r.status,r.read()
    except urllib.error.HTTPError as e:return e.code,e.read()
with tempfile.TemporaryDirectory() as tmp:
    with open(Path(tmp)/'host.log','w') as log:
        host=subprocess.Popen([dotnet,'bin/'+os.environ.get('LOOM_CONFIGURATION','Release')+'/net8.0/Loom.Demo.dll','--memory-demo'],cwd=root/'src/Loom.Demo',env=env,stdout=log,stderr=log)
        try:
            for _ in range(150):
                try:urllib.request.urlopen(base+'/health',timeout=1).close();break
                except Exception:
                    if host.poll() is not None:raise RuntimeError((Path(tmp)/'host.log').read_text())
                    time.sleep(.1)
            opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
            assert request(opener,'/api/meta')[0]==401
            assert request(opener,'/auth/login',{'id':'analyst','password':'wrong'})[0]==401
            assert request(opener,'/auth/login',{'id':'analyst','password':password})[0]==200
            meta=json.loads(request(opener,'/api/meta')[1]);assert meta['actor']=={'id':'analyst','role':'Editor'}
            results=json.loads(request(opener,'/api/documents/TestResult')[1]);assert all('Cost' not in d['values'] for d in results['items'])
            runs=json.loads(request(opener,'/api/documents/LabRun')[1]);assert runs['total']==1
            run=runs['items'][0];result=results['items'][0]
            assert request(opener,'/api/documents/TestResult/'+result['id'],{'version':result['version'],'values':{'Cost':'1.00'}},'PUT')[0]==403
            assert request(opener,'/api/documents/TestResult?sort=Cost')[0]==403
            assert request(opener,'/api/documents/TestResult?search=1234')[0]==403
            status,report=request(opener,'/api/laboratory/runs/'+run['id']+'/print');assert status==200 and b'<th>Cost</th>' not in report
            assert request(opener,'/auth/logout',{},'POST')[0]==204
            assert request(opener,'/api/meta')[0]==401
            assert request(opener,'/auth/login',{'id':'other','password':password})[0]==200
            assert json.loads(request(opener,'/api/documents/LabRun')[1])['total']==0
            assert request(opener,'/api/documents/LabRun/'+run['id'])[0]==404
            assert request(opener,'/api/laboratory/runs/'+run['id']+'/print')[0]==404
            print('PASS cookie authentication, logout, spoof rejection, row scopes, field redaction and report authorization')
        finally:host.terminate();host.wait(timeout=10)
