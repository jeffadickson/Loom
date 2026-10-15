"""Tenant operations authorization, ciphertext/audit checks and real restart delivery."""
import http.cookiejar,json,os,sqlite3,subprocess,sys,tempfile,time,urllib.request,urllib.error
from pathlib import Path
root=Path(__file__).resolve().parents[1]
dotnet=str(Path(sys.argv[1]).resolve()) if len(sys.argv)>1 and Path(sys.argv[1]).exists() else (sys.argv[1] if len(sys.argv)>1 else 'dotnet')
demo=root/'src/Loom.Demo/bin'/os.environ.get('LOOM_CONFIGURATION','Release')/'net8.0/Loom.Demo.dll'
origin='http://127.0.0.1:5094'
def api(client,path,body=None,expected=200):
    req=urllib.request.Request(origin+path,data=None if body is None else json.dumps(body).encode(),headers={'Content-Type':'application/json','X-Loom-Demo':'local'})
    try:
        with client.open(req) as response:
            assert response.status==expected,(response.status,expected)
            return json.loads(response.read() or b'null')
    except urllib.error.HTTPError as error:
        assert error.code==expected,(error.code,expected,error.read())
def login(user):
    client=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    api(client,'/auth/login',{'id':user,'password':'Loom-jobs-demo!'})
    return client
def ready(host,log):
    for _ in range(200):
        try:urllib.request.urlopen(origin+'/health',timeout=1).close();return
        except Exception:
            if host.poll() is not None:raise RuntimeError(log.read_text())
            time.sleep(.1)
    raise RuntimeError('Tenant operations host did not start')
with tempfile.TemporaryDirectory() as temp:
    directory=Path(temp)/'tenants';logpath=Path(temp)/'host.log'
    env=dict(os.environ,LOOM_PORT='5094',Jobs__TenantDataDirectory=str(directory),Jobs__PauseWorker='true')
    with logpath.open('w') as log:
        host=subprocess.Popen([dotnet,str(demo),'--tenant-operations-demo'],cwd=root/'src/Loom.Demo',env=env,stdout=log,stderr=log)
        try:
            ready(host,logpath);north=login('jobs-admin');south=login('jobs-south');global_admin=login('jobs-global')
            assert [c['id'] for c in api(north,'/api/jobs/contexts')]==['north']
            api(global_admin,'/api/jobs/contexts',expected=403)
            api(north,'/api/jobs/tenants/south/records/Delivery',expected=403)
            job=api(north,'/api/jobs/tenants/north/deliveries',{'handler':'demo.receipt','message':'CONFIDENTIAL-TENANT-PAYLOAD','due':'2026-01-01T00:00:00Z'})
            assert 'values' not in job
            api(south,f"/api/jobs/tenants/north/deliveries/{job['id']}/cancel",{'versionNumber':job['version']},expected=403)
            api(north,'/api/jobs/tenants/north/schedules',{'name':'Protected schedule','handler':'demo.receipt','message':'CONFIDENTIAL-SCHEDULE-PAYLOAD','intervalSeconds':3600,'firstDue':'2099-01-01T00:00:00Z','missedRuns':'Skip'})
            subprocess.run([os.environ.get('NODE','node'),str(root/'tools/test_tenant_jobs.cjs')],env=dict(os.environ,TENANT_JOBS_ORIGIN=origin),check=True)
        finally:host.terminate();host.wait(timeout=10)
    # No plaintext in the raw database or audit snapshots; keys live in a separate file.
    database=directory/'north/documents.db'
    raw=database.read_bytes();assert b'CONFIDENTIAL-TENANT-PAYLOAD' not in raw;assert b'CONFIDENTIAL-SCHEDULE-PAYLOAD' not in raw
    connection=sqlite3.connect(database)
    try:
        # Audit table naming and serialized values are checked without relying on compressed raw byte searches.
        tables=[row[0] for row in connection.execute("SELECT name FROM sqlite_master WHERE type='table'")]
        audit=next(t for t in tables if 'audit' in t.lower())
        for row in connection.execute('SELECT * FROM "'+audit+'"'):
            for value in row:
                if isinstance(value,str) and value.startswith('{'):
                    data=json.loads(value)
                    if isinstance(data,dict) and 'values' in data:
                        assert 'Payload' not in data['values'];assert 'LeaseToken' not in data['values']
    finally:connection.close()
    env['Jobs__PauseWorker']='false'
    with logpath.open('a') as log:
        host=subprocess.Popen([dotnet,str(demo),'--tenant-operations-demo'],cwd=root/'src/Loom.Demo',env=env,stdout=log,stderr=log)
        try:
            ready(host,logpath);north=login('jobs-admin');south=login('jobs-south')
            for _ in range(100):
                deliveries=api(north,'/api/jobs/tenants/north/records/Delivery')['items']
                if any(d['id']==job['id'] and d['values']['Status']=='Complete' for d in deliveries):break
                time.sleep(.1)
            else:raise AssertionError('Protected delivery did not resume after restart')
            assert api(south,'/api/jobs/tenants/south/records/Receipt')['total']==0
            assert api(north,'/api/jobs/tenants/north/records/Receipt')['items'][0]['values']['Message']=='Protected delivery received'
            print('PASS tenant operations: membership isolation, global-role denial, protected restart, audit minimization')
        finally:host.terminate();host.wait(timeout=10)
