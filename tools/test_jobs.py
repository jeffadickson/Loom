"""Exercise authenticated operations UI, then restart the paused worker against persisted schedules."""
import http.cookiejar,json,os,sqlite3,subprocess,sys,tempfile,time,urllib.request
from pathlib import Path
root=Path(__file__).resolve().parents[1]
dotnet=str(Path(sys.argv[1]).resolve()) if len(sys.argv)>1 and Path(sys.argv[1]).exists() else (sys.argv[1] if len(sys.argv)>1 else 'dotnet')
demo=root/'src/Loom.Demo/bin'/os.environ.get('LOOM_CONFIGURATION','Release')/'net8.0/Loom.Demo.dll'
origin='http://127.0.0.1:5093'
def ready(host,log):
    for _ in range(200):
        try:urllib.request.urlopen(origin+'/health',timeout=1).close();return
        except Exception:
            if host.poll() is not None:raise RuntimeError(log.read_text())
            time.sleep(.1)
    raise RuntimeError('Jobs host did not start')
def client():return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
def api(opener,path,body=None):
    req=urllib.request.Request(origin+path,data=None if body is None else json.dumps(body).encode(),headers={'Content-Type':'application/json','X-Loom-Demo':'local'})
    with opener.open(req) as r:return json.loads(r.read() or b'null')
with tempfile.TemporaryDirectory() as tmp:
    database=Path(tmp)/'jobs.db';logpath=Path(tmp)/'host.log';env=dict(os.environ,LOOM_PORT='5093',Jobs__Database=str(database),Jobs__PauseWorker='true')
    with logpath.open('w') as log:
        host=subprocess.Popen([dotnet,str(demo),'--jobs-demo'],cwd=root/'src/Loom.Demo',env=env,stdout=log,stderr=log)
        try:
            ready(host,logpath);opener=client();api(opener,'/auth/login',{'id':'jobs-admin','password':'Loom-jobs-demo!'})
            fixture=api(opener,'/api/jobs/deliveries',{'handler':'demo.failure','message':'Fixture exhausted delivery','due':'2099-01-01T00:00:00Z'})
            # Seed a terminal failure without waiting through backoff; shared contracts test actual exhaustion.
            connection=sqlite3.connect(database)
            try:
                connection.execute('UPDATE Delivery SET Status=?, Attempts=5, version=version+1 WHERE id=?',('Failed',fixture['id']));connection.commit()
            finally:connection.close()
            for i in range(27):api(opener,'/api/jobs/deliveries',{'handler':'demo.receipt','message':f'Future delivery {i}','due':'2099-01-01T00:00:00Z'})
            subprocess.run([os.environ.get('NODE','node'),str(root/'tools/test_jobs.cjs')],env=dict(os.environ,JOBS_ORIGIN=origin),check=True)
        finally:host.terminate();host.wait(timeout=10)
    env['Jobs__PauseWorker']='false'
    with logpath.open('a') as log:
        host=subprocess.Popen([dotnet,str(demo),'--jobs-demo'],cwd=root/'src/Loom.Demo',env=env,stdout=log,stderr=log)
        try:
            ready(host,logpath);opener=client();api(opener,'/auth/login',{'id':'jobs-admin','password':'Loom-jobs-demo!'})
            for _ in range(100):
                receipts=api(opener,'/api/jobs/records/Receipt')['items']
                if any(r['values']['Key'].startswith('schedule:') for r in receipts):break
                time.sleep(.1)
            scheduled=[r for r in receipts if r['values']['Key'].startswith('schedule:')];assert len(scheduled)==1,receipts
            assert scheduled[0]['values']['Message']=='Scheduled check complete'
            schedule=api(opener,'/api/jobs/records/Schedule')['items'][0]
            assert int(schedule['values']['NextDueAt'])>int(time.time()*1000)
            print('PASS jobs restart: persisted schedule enqueued and delivered one occurrence')
        finally:host.terminate();host.wait(timeout=10)
