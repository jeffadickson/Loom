"""Run the real two-laboratory app, browser workflow and pending-job restart drill."""
import http.cookiejar, json, os, subprocess, sys, tempfile, time, urllib.request
from pathlib import Path
root=Path(__file__).resolve().parents[1]
dotnet=str(Path(sys.argv[1]).resolve()) if len(sys.argv)>1 and Path(sys.argv[1]).exists() else (sys.argv[1] if len(sys.argv)>1 else 'dotnet')
demo=root/'src/Loom.Demo/bin'/os.environ.get('LOOM_CONFIGURATION','Release')/'net8.0/Loom.Demo.dll'
origin='http://127.0.0.1:5091'
def ready(host,log):
    for _ in range(200):
        try:urllib.request.urlopen(origin+'/health',timeout=1).close();return
        except Exception:
            if host.poll() is not None:raise RuntimeError(log.read_text())
            time.sleep(.1)
    raise RuntimeError('Facility host did not start')
with tempfile.TemporaryDirectory() as tmp:
    env=dict(os.environ,LOOM_PORT='5091',Facility__DataDirectory=str(Path(tmp)/'tenants'),Facility__PauseWorker='true')
    logpath=Path(tmp)/'host.log'
    with logpath.open('w') as log:
        host=subprocess.Popen([dotnet,str(demo),'--facility-demo'],cwd=root/'src/Loom.Demo',env=env,stdout=log,stderr=log)
        try:
            ready(host,logpath)
            subprocess.run([os.environ.get('NODE','node'),str(root/'tools/test_facility.cjs')],env=dict(os.environ,FACILITY_ORIGIN=origin,FACILITY_PAUSED='1'),check=True)
        finally:host.terminate();host.wait(timeout=10)
    # Restart the same persistent database with its worker enabled.
    env['Facility__PauseWorker']='false'
    with logpath.open('a') as log:
        host=subprocess.Popen([dotnet,str(demo),'--facility-demo'],cwd=root/'src/Loom.Demo',env=env,stdout=log,stderr=log)
        try:
            ready(host,logpath)
            opener=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
            def api(path,body=None):
                request=urllib.request.Request(origin+path,data=None if body is None else json.dumps(body).encode(),headers={'Content-Type':'application/json','X-Loom-Demo':'local'})
                with opener.open(request) as response:return response.read()
            api('/auth/login',{'id':'north-manager','password':'Loom-facility-demo!'})
            for _ in range(100):
                overview=json.loads(api('/api/facility/north/overview'))
                if overview['records']['Release']:break
                time.sleep(.1)
            assert len(overview['records']['Release'])==1
            release=overview['records']['Release'][0]
            html=api('/api/facility/north/releases/'+release['id'])
            assert b'20.125' in html and b'north-reviewer' in html and b'SYNTHETIC-CAL' in html
            assert overview['records']['Request'][0]['values']['Status']=='Released'
            assert overview['records']['ReleaseJob'][0]['values']['Status']=='Complete'
            # Browser inspects recovered report and responsive layout without rerunning the completed workflow.
            subprocess.run([os.environ.get('NODE','node'),str(root/'tools/test_facility_recovery.cjs')],env=dict(os.environ,FACILITY_ORIGIN=origin),check=True)
            print('PASS persisted queued report resumes after process restart and produces exactly one release')
        finally:host.terminate();host.wait(timeout=10)
