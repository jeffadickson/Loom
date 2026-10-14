"""Real Identity UI/account lifecycle against an isolated persistent account database."""
import os, subprocess, sys, tempfile, time, urllib.request
from pathlib import Path
root=Path(__file__).resolve().parents[1]
dotnet=sys.argv[1] if len(sys.argv)>1 else 'dotnet'
with tempfile.TemporaryDirectory() as tmp:
    data=Path(tmp)/'identity';mail=Path(tmp)/'mail'
    env=dict(os.environ,LOOM_PORT='5087',ASPNETCORE_ENVIRONMENT='Development',Authentication__Mode='Identity',Identity__DataDirectory=str(data),Identity__Email__PickupDirectory=str(mail),Identity__PublicOrigin='http://127.0.0.1:5087',Identity__AllowRegistration='true',LOOM_IDENTITY_MAIL=str(mail))
    command=[dotnet,'bin/'+os.environ.get('LOOM_CONFIGURATION','Release')+'/net8.0/Loom.Demo.dll']
    setup=subprocess.run(command+['--identity-admin=admin@example.test'],cwd=root/'src/Loom.Demo',env=env,input='Admin-test-password-42!\n',text=True,capture_output=True)
    if setup.returncode:raise RuntimeError(setup.stdout+setup.stderr)
    for phase in ['lifecycle','restart']:
        if phase=='restart':env['Identity__AllowRegistration']='false'
        logpath=Path(tmp)/(phase+'.log')
        with logpath.open('w') as log:
            host=subprocess.Popen(command+['--memory-demo'],cwd=root/'src/Loom.Demo',env=env,stdout=log,stderr=log)
            try:
                for _ in range(150):
                    try:urllib.request.urlopen('http://127.0.0.1:5087/health',timeout=1).close();break
                    except Exception:
                        if host.poll() is not None:raise RuntimeError(logpath.read_text())
                        time.sleep(.1)
                result=subprocess.run(['node',str(root/'tools/test_identity.cjs'),phase],env=env)
                if result.returncode:raise RuntimeError('Identity browser test failed. Host log:\n'+logpath.read_text()[-12000:])
            finally:host.terminate();host.wait(timeout=10)
