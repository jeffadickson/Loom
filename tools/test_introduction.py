"""Test the standalone page and an isolated real C# memory host with Playwright."""
import os, subprocess, sys, tempfile, time, urllib.request
from pathlib import Path
root=Path(__file__).resolve().parents[1]
dotnet=sys.argv[1] if len(sys.argv)>1 else 'dotnet'
with tempfile.TemporaryDirectory() as tmp:
    logpath=Path(tmp)/'host.log'
    with logpath.open('w') as log:
        host=subprocess.Popen([dotnet,'bin/'+os.environ.get('LOOM_CONFIGURATION','Debug')+'/net8.0/Loom.Demo.dll','--memory-demo'],cwd=root/'src/Loom.Demo',env=dict(os.environ,LOOM_PORT='5083'),stdout=log,stderr=log)
        try:
            for _ in range(150):
                try:urllib.request.urlopen('http://127.0.0.1:5083/health',timeout=1).close();break
                except Exception:
                    if host.poll() is not None:raise RuntimeError(logpath.read_text())
                    time.sleep(.1)
            else:raise RuntimeError('Host did not become ready')
            subprocess.run(['node',str(root/'tools/test_introduction.cjs')],check=True)
        finally:host.terminate();host.wait(timeout=10)
