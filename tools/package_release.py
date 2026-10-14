"""Package a published demo with a manifest and fixed ZIP timestamps."""
import argparse,hashlib,json,re,zipfile
from pathlib import Path
root=Path(__file__).resolve().parents[1]
p=argparse.ArgumentParser();p.add_argument('--version',required=True);args=p.parse_args()
if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?',args.version):p.error('Use a semantic release version')
source=root/'artifacts/publish'
if not (source/'Loom.Demo.dll').exists():raise SystemExit('Publish the demo first')
files={str(path.relative_to(source)).replace('\\','/'):path.read_bytes() for path in sorted(source.rglob('*')) if path.is_file()}
for name in ('README.md','Loom_Introduction.html','docs/DEVELOPMENT.md'):files[name]=(root/name).read_bytes()
manifest={'version':args.version,'sdk':json.loads((root/'global.json').read_text())['sdk']['version'],'deployment':'framework-dependent; .NET 8 ASP.NET Core runtime required','files':{name:hashlib.sha256(data).hexdigest() for name,data in sorted(files.items())}}
files['release-manifest.json']=(json.dumps(manifest,indent=2)+'\n').encode()
output=root/'artifacts'/f'Loom-{args.version}.zip'
with zipfile.ZipFile(output,'w',compression=zipfile.ZIP_DEFLATED) as z:
    for name,data in sorted(files.items()):
        info=zipfile.ZipInfo(name,date_time=(2020,1,1,0,0,0));info.compress_type=zipfile.ZIP_DEFLATED;info.external_attr=0o100644<<16;z.writestr(info,data)
checksum=hashlib.sha256(output.read_bytes()).hexdigest();output.with_suffix('.zip.sha256').write_text(checksum+'  '+output.name+'\n')
print(output)
