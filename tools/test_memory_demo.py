"""Run real memory-backed demo, exercise browser, then restart to prove reset."""
import json, os, subprocess, sys, tempfile, time, urllib.request
from pathlib import Path
root=Path(__file__).resolve().parents[1]
dotnet=sys.argv[1] if len(sys.argv)>1 else 'dotnet'
def get(path):
    req=urllib.request.Request('http://127.0.0.1:5080/'+path,headers={'X-Loom-Demo':'local','X-Demo-Role':'Admin'})
    return json.load(urllib.request.urlopen(req))
with tempfile.TemporaryDirectory() as tmp:
    for iteration in range(2):
        with open(Path(tmp)/'server.log','w') as log:
            server=subprocess.Popen([dotnet,'bin/Debug/net8.0/Loom.Demo.dll','--memory-demo'],cwd=root/'src/Loom.Demo',stdout=log,stderr=log)
            try:
                for _ in range(150):
                    try: get('health');break
                    except Exception:
                        if server.poll() is not None:raise RuntimeError((Path(tmp)/'server.log').read_text())
                        time.sleep(.1)
                else:raise RuntimeError('Host not ready')
                meta=get('api/meta');assert meta['provider']['name']=='Memory' and not meta['provider']['durable'] and not meta['designer']
                docs=get('api/documents/TestResult')['items'];assert len(docs)==2
                thermal=next(d for d in docs if d['values']['Name'].startswith('Thermal'))
                assert thermal['values']['Cost']=='1234.56' and thermal['values']['SampleCount']=='9007199254740993'
                if iteration==0:subprocess.run(['node',str(root/'tools/test_memory_ui.cjs')],check=True)
                else:print('PASS memory demo: fresh seed after restart, no persisted edits')
            finally:
                server.terminate();server.wait(timeout=10)
