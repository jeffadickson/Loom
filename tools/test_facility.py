"""Run the real two-laboratory app, browser workflow and pending-job restart drill."""
import hashlib, http.cookiejar, io, json, os, subprocess, sys, tempfile, time, urllib.request, zipfile
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
            package=api('/api/facility/north/releases/'+release['id']+'/package')
            with zipfile.ZipFile(io.BytesIO(package)) as archive:
                manifest=json.loads(archive.read('manifest.json'))
                assert manifest['version']==1 and len(manifest['files'])==4
                assert set(archive.namelist())=={'manifest.json','report.html','evidence.bin','provenance.json','readme.txt'}
                for item in manifest['files']:
                    content=archive.read(item['path'])
                    assert len(content)==item['length']
                    assert hashlib.sha256(content).hexdigest().upper()==item['sha256']
                assert archive.read('report.html')==html
                provenance=json.loads(archive.read('provenance.json'))
                assert provenance['tenant']=='north' and len(provenance['records'])==9
                assert b'Waveform' not in archive.read('provenance.json')
                entries={name:archive.read(name) for name in archive.namelist()}
            packagepath=Path(tmp)/'evidence.zip';packagepath.write_bytes(package)
            # The verifier must return before Identity/database/host initialization.
            offlineenv=dict(os.environ,Authentication__Mode='Identity')
            def verify(*args):
                return subprocess.run([dotnet,str(demo),'--verify-evidence',*map(str,args)],cwd=tmp,
                    env=offlineenv,capture_output=True,text=True,timeout=30)
            checked=verify(packagepath)
            assert checked.returncode==0,checked.stderr
            assert 'Evidence integrity verified' in checked.stdout
            with zipfile.ZipFile(packagepath,'w',compression=zipfile.ZIP_DEFLATED) as archive:
                for name,content in entries.items():
                    archive.writestr(name,b'corrupted' if name=='evidence.bin' else content)
            assert verify(packagepath).returncode!=0
            assert verify(Path(tmp)/'missing.zip').returncode!=0
            assert verify().returncode!=0
            assert not (Path(tmp)/'report.html').exists()
            print('PASS independent ZIP hashes, selected provenance and offline CLI success/corruption/usage checks')
            # Browser inspects recovered report and responsive layout without rerunning the completed workflow.
            subprocess.run([os.environ.get('NODE','node'),str(root/'tools/test_facility_recovery.cjs')],env=dict(os.environ,FACILITY_ORIGIN=origin),check=True)
            print('PASS persisted queued report resumes after process restart and produces exactly one release')
        finally:host.terminate();host.wait(timeout=10)
