"""Small standard-library launcher; MSBuild owns the build graph.
Python 3.11+ is the bootstrap prerequisite. Installs are local to .tools.
"""
import argparse,hashlib,json,os,platform,re,shutil,subprocess,sys,tarfile,tempfile,urllib.request,zipfile
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
MANIFEST=json.loads((ROOT/'build/toolchains.json').read_text())

def rid():
    system={'Windows':'win','Linux':'linux','Darwin':'osx'}.get(platform.system())
    arch={'x86_64':'x64','amd64':'x64','arm64':'arm64','aarch64':'arm64'}.get(platform.machine().lower())
    value=f'{system}-{arch}'
    if value not in MANIFEST['dotnet']:raise RuntimeError(f'Unsupported bootstrap platform: {value}. Install the pinned SDK manually.')
    return value

def environment():
    env=dict(os.environ)
    sdk=ROOT/'.tools/dotnet'
    node=ROOT/'.tools/node';nodebin=node if os.name=='nt' else node/'bin'
    env['PATH']=os.pathsep.join([str(sdk),str(nodebin),env.get('PATH','')])
    if (sdk/('dotnet.exe' if os.name=='nt' else 'dotnet')).exists():env['DOTNET_ROOT']=str(sdk)
    env['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
    env['PYTHONUTF8']='1'
    return env

def run(args,**kwargs):
    print('+',' '.join(map(str,args)),flush=True)
    subprocess.run(list(map(str,args)),cwd=ROOT,env=environment(),check=True,**kwargs)

def install(kind):
    target=ROOT/'.tools'/kind
    expected=MANIFEST['sdkVersion' if kind=='dotnet' else 'nodeVersion']
    executable=target/('dotnet.exe' if os.name=='nt' else 'dotnet') if kind=='dotnet' else target/('node.exe' if os.name=='nt' else 'bin/node')
    if executable.exists():
        version=subprocess.check_output([str(executable),'--version'],cwd=ROOT,text=True).strip().lstrip('v')
        if version==expected:print(f'{kind} {expected} already installed');return
        raise RuntimeError(f'{target} contains {version}; move that directory aside before installing {expected}.')
    spec=MANIFEST[kind][rid()];algorithm='sha512' if 'sha512' in spec else 'sha256'
    target.parent.mkdir(exist_ok=True)
    with tempfile.TemporaryDirectory(dir=target.parent) as temp:
        temp=Path(temp);archive=temp/('download.zip' if spec['url'].endswith('.zip') else 'download.tar.gz')
        print('Downloading',spec['url'],flush=True)
        with urllib.request.urlopen(spec['url'],timeout=120) as source,archive.open('wb') as output:shutil.copyfileobj(source,output)
        with archive.open('rb') as source:actual=hashlib.file_digest(source,algorithm).hexdigest()
        if actual.lower()!=spec[algorithm].lower():raise RuntimeError('Archive checksum mismatch; nothing installed.')
        extracted=temp/'unpack';extracted.mkdir()
        if archive.suffix=='.zip':
            with zipfile.ZipFile(archive) as z:
                for entry in z.namelist():
                    if not (extracted/entry).resolve().is_relative_to(extracted.resolve()):raise RuntimeError('Unsafe archive path')
                z.extractall(extracted)
        else:
            with tarfile.open(archive) as t:t.extractall(extracted,filter='data')
        source=next(extracted.iterdir()) if kind=='node' else extracted
        if target.exists():raise RuntimeError(f'{target} already exists; inspect it before retrying.')
        source.rename(target)
    print(f'Installed {kind} {expected} in {target}')

def doctor(browser=False,database=False):
    failed=False
    for tool,expected in [('dotnet',MANIFEST['sdkVersion'])]+([('node','v'+MANIFEST['nodeVersion'])] if browser else []):
        executable=shutil.which(tool,path=environment()['PATH'])
        if not executable:print(f'MISSING {tool}: run python tools/dev.py setup'+(' --browser' if browser else ''));failed=True;continue
        result=subprocess.run([executable,'--version'],cwd=ROOT,env=environment(),text=True,capture_output=True)
        actual=result.stdout.strip();ok=result.returncode==0 and actual==expected
        print(('OK' if ok else 'MISMATCH'),tool,actual or result.stderr.strip(),'expected',expected);failed|=not ok
    if database:
        docker=shutil.which('docker',path=environment()['PATH'])
        if not docker or subprocess.run([docker,'info'],capture_output=True).returncode:print('MISSING working Docker daemon; start Docker Desktop or Docker Engine.');failed=True
        else:print('OK Docker daemon; SQL Server container tests require an x64 Linux container host.')
    if failed:raise RuntimeError('Resolve the requirements above, then rerun doctor.')

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('command',choices=['setup','doctor','restore','build','test','browser','database','pack','run'])
    parser.add_argument('--browser',action='store_true',help='Install/check pinned Node and Chromium too')
    parser.add_argument('--database',action='store_true',help='Check Docker availability')
    parser.add_argument('--version',default='0.12.0-alpha.1')
    args=parser.parse_args()
    if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?",args.version):parser.error("Use a semantic release version")
    if args.command=='setup':
        install('dotnet')
        if args.browser:
            install('node');node=shutil.which('node',path=environment()['PATH'])
            npm=ROOT/'.tools/node'/('node_modules/npm/bin/npm-cli.js' if os.name=='nt' else 'lib/node_modules/npm/bin/npm-cli.js')
            run([node,npm,'ci']);run([node,ROOT/'node_modules/playwright/cli.js','install','chromium'])
        doctor(args.browser,args.database);return
    if args.command=='doctor':doctor(args.browser,args.database);return
    dotnet=shutil.which('dotnet',path=environment()['PATH'])
    if not dotnet:raise RuntimeError('Run python tools/dev.py setup first.')
    if args.command=='run':run([dotnet,'run','--project','src/Loom.Demo','--','--memory-demo']);return
    targets={'restore':'Restore','build':'Build','test':'Test','browser':'BrowserTest','database':'DatabaseTest','pack':'Pack'}
    if args.command=='database':doctor(database=True)
    if args.command=='browser':doctor(browser=True)
    run([dotnet,'msbuild','build/Loom.proj','-t:'+targets[args.command],'-p:DotNet='+dotnet,'-p:Python='+sys.executable,'-p:Version='+args.version])
if __name__=='__main__':
    try:main()
    except (RuntimeError,subprocess.CalledProcessError,OSError) as error:print('ERROR:',error,file=sys.stderr);sys.exit(1)
