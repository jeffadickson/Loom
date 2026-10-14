"""Verify release hashes and start the extracted application, not the source tree."""
import hashlib,json,os,subprocess,sys,tempfile,time,urllib.request,zipfile
from pathlib import Path
archive=Path(sys.argv[1]);dotnet=sys.argv[2] if len(sys.argv)>2 else 'dotnet'
with tempfile.TemporaryDirectory(prefix='loom-release-') as directory:
    root=Path(directory)
    with zipfile.ZipFile(archive) as z:
        manifest=json.loads(z.read('release-manifest.json'))
        assert set(z.namelist())==set(manifest['files'])|{'release-manifest.json'}
        for name,expected in manifest['files'].items():
            assert (root/name).resolve().is_relative_to(root.resolve()),name
            assert not name.startswith('data/'),name
            assert hashlib.sha256(z.read(name)).hexdigest()==expected,name
        z.extractall(root)
    with (root/'smoke.log').open('w') as log:
        process=subprocess.Popen([dotnet,'Loom.Demo.dll','--memory-demo'],cwd=root,env=dict(os.environ,LOOM_PORT='5085'),stdout=log,stderr=log)
        try:
            for _ in range(150):
                try:
                    with urllib.request.urlopen('http://127.0.0.1:5085/health',timeout=1) as response:assert json.load(response)['provider']=='Memory'
                    break
                except Exception:
                    if process.poll() is not None:raise RuntimeError((root/'smoke.log').read_text())
                    time.sleep(.1)
            else:raise RuntimeError('Packaged host did not become ready')
            with urllib.request.urlopen('http://127.0.0.1:5085/introduction.html') as response:assert b'The test-results desk' in response.read()
            request=urllib.request.Request('http://127.0.0.1:5085/api/documents/TestResult',headers={'X-Loom-Demo':'local','X-Demo-Role':'Admin'})
            with urllib.request.urlopen(request) as response:assert json.load(response)['total']==2
            print('PASS release: all file hashes verified; extracted app, metadata, seed records and static assets run')
        finally:process.terminate();process.wait(timeout=10)
