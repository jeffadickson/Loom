"""Start an isolated real host, exercise its HTTP API, optionally run browser tests."""
import os
import json
import io
import zipfile
import subprocess
import sys
import tempfile
import time
import urllib.request
from pathlib import Path

root = Path(__file__).resolve().parents[1]
dotnet = sys.argv[1] if len(sys.argv) > 1 else 'dotnet'
with tempfile.TemporaryDirectory(prefix='loom-http-') as tmp:
    env = dict(os.environ, Database__ConnectionString=f'Data Source={tmp}/test.db')
    with open(Path(tmp) / 'server.log', 'w') as log:
        server = subprocess.Popen([dotnet, 'bin/Debug/net8.0/Loom.Demo.dll'], cwd=root / 'src/Loom.Demo', env=env, stdout=log, stderr=log)
        try:
            for _ in range(100):
                try:
                    urllib.request.urlopen('http://127.0.0.1:5080/health', timeout=1).close()
                    break
                except Exception:
                    if server.poll() is not None:
                        raise RuntimeError('Host exited: ' + (Path(tmp) / 'server.log').read_text())
                    time.sleep(.1)
            else:
                raise RuntimeError('Host did not become ready')
            subprocess.run([sys.executable, str(root / 'tools/test_api.py')], check=True)
            import json
            request = urllib.request.Request('http://127.0.0.1:5080/api/saved-reports/EquipmentRegister', headers={'X-Loom-Demo':'local','X-Demo-Role':'Viewer'})
            report = json.load(urllib.request.urlopen(request))
            assert report['columns'][1]['name'] == 'tag'
            assert any(row[1] == 'LAB-001' for row in report['rows'])
            print('PASS report API: persisted definition resolves readable columns and populated values')
            if env.get('Models__Source') == 'CSharp':
                request = urllib.request.Request('http://127.0.0.1:5080/api/meta', headers={'X-Loom-Demo':'local','X-Demo-Role':'Admin'})
                meta = json.load(urllib.request.urlopen(request))
                assert not meta['designer'] and meta['modelSource'] == 'CSharp'
                request = urllib.request.Request('http://127.0.0.1:5080/api/designer/export', headers={'X-Loom-Demo':'local','X-Demo-Role':'Admin'})
                try:
                    urllib.request.urlopen(request)
                    raise AssertionError('C# designer should be blocked')
                except urllib.error.HTTPError as e:
                    assert e.code == 422
                print('PASS CSharp authority: browser designer disabled and endpoint guarded')
            if '--browser' in sys.argv:
                subprocess.run(['node', str(root / 'tools/test_ui.cjs')], check=True)
            if '--datatypes' in sys.argv:
                subprocess.run(['node', str(root / 'tools/test_datatype_designer.cjs')], check=True)
            if '--conversion' in sys.argv:
                subprocess.run(['node', str(root / 'tools/test_conversions.cjs')], check=True)
            if '--required' in sys.argv:
                subprocess.run(['node', str(root / 'tools/test_required_fields.cjs')], check=True)
            if '--backfill' in sys.argv:
                subprocess.run(['node', str(root / 'tools/test_backfills.cjs')], check=True)
                request = urllib.request.Request('http://127.0.0.1:5080/api/designer/export', headers={'X-Loom-Demo':'local','X-Demo-Role':'Admin'})
                with zipfile.ZipFile(io.BytesIO(urllib.request.urlopen(request).read())) as archive:
                    backfill_model = [json.loads(archive.read(name).decode('utf-8-sig')) for name in archive.namelist() if name.startswith('Metadata/') and name.endswith('.json')]
            if '--bindings' in sys.argv:
                subprocess.run(['node', str(root / 'tools/test_report_bindings.cjs')], check=True)
            if '--bands' in sys.argv:
                subprocess.run(['node', str(root / 'tools/test_report_bands.cjs')], check=True)
            if '--layout' in sys.argv:
                subprocess.run(['node', str(root / 'tools/test_report_designer.cjs')], check=True)
            if '--writer' in sys.argv:
                subprocess.run(['node', str(root / 'tools/test_writer.cjs')], check=True)
            if '--designer' in sys.argv:
                subprocess.run(['node', str(root / 'tools/test_designer.cjs')], check=True)
        finally:
            server.terminate()
            server.wait(timeout=10)

    if '--designer' in sys.argv:
        with open(Path(tmp) / 'restart.log', 'w') as log:
            server = subprocess.Popen([dotnet, 'bin/Debug/net8.0/Loom.Demo.dll'], cwd=root / 'src/Loom.Demo', env=env, stdout=log, stderr=log)
            try:
                for _ in range(100):
                    try:
                        urllib.request.urlopen('http://127.0.0.1:5080/health', timeout=1).close()
                        break
                    except Exception:
                        if server.poll() is not None:
                            raise RuntimeError((Path(tmp) / 'restart.log').read_text())
                        time.sleep(.1)
                else:
                    raise RuntimeError('Restarted host did not become ready')
                def get(path):
                    request = urllib.request.Request('http://127.0.0.1:5080' + path, headers={'X-Loom-Demo':'local', 'X-Demo-Role':'Admin'})
                    return urllib.request.urlopen(request).read()
                meta = json.loads(get('/api/meta'))
                asset_model = next(t for t in meta['types'] if t['name'] == 'Asset')
                assert asset_model['version'] == 2
                assets = json.loads(get('/api/documents/Asset'))['items']
                asset = next(a for a in assets if a['values'].get('serialNumber') == 'LAB-001')
                assert asset['values']['warrantyExpires'] == '2028-01-15'
                print('PASS restart: accepted model and populated fields survive full host restart')
                orders = json.loads(get('/api/documents/WorkOrder'))['items']
                assert any(o['values']['asset'] == asset['id'] for o in orders)
                assert len(json.loads(get('/api/designer/history'))) == 1
                print('PASS restart: linked work orders and migration ledger survive')
                get_export_bytes = get('/api/designer/export')
                with zipfile.ZipFile(io.BytesIO(get_export_bytes)) as archive:
                    name = next(n for n in archive.namelist() if n.endswith('Asset.json'))
                    exported = json.loads(archive.read(name))
                    assert exported['id'] == asset_model['id']
                    assert next(f for f in exported['fields'] if f['name']=='serialNumber')['id'] == next(f for f in asset_model['fields'] if f['name']=='serialNumber')['id']
                print('PASS export: accepted JSON preserves permanent model and field IDs')
                report = json.loads(get('/api/saved-reports/EquipmentRegister'))
                assert report['columns'][1]['name'] == 'serialNumber'
                assert any(row[1] == 'LAB-001' for row in report['rows'])
                print('PASS report API: unchanged saved report survives designer rename and host restart')
            finally:
                server.terminate()
                server.wait(timeout=10)

        # CLI targets the same database using an isolated source catalog.
        cli_root = Path(tmp) / 'source'
        (cli_root / 'Metadata').mkdir(parents=True)
        with zipfile.ZipFile(io.BytesIO(get_export_bytes)) as archive:
            for name in archive.namelist():
                if name.startswith('Metadata/') and name.endswith('.json'):
                    (cli_root / name).write_bytes(archive.read(name))
        asset_file = cli_root / 'Metadata/Asset.json'
        target = json.loads(asset_file.read_text(encoding='utf-8-sig'))
        target['version'] += 1
        next(f for f in target['fields'] if f['name']=='serialNumber')['label'] = 'Asset serial number'
        asset_file.write_text(json.dumps(target))
        command = [dotnet, str(root / 'src/Loom.Demo/bin/Debug/net8.0/Loom.Demo.dll'), '--contentRoot', str(cli_root)]
        cli_env = dict(env, Database__Provider='Sqlite')
        plan = json.loads(subprocess.check_output(command + ['--plan-model'], env=cli_env))
        rejected = subprocess.run(command + ['--apply-model=wrong'], env=cli_env, capture_output=True)
        assert rejected.returncode != 0
        subprocess.run(command + ['--apply-model=' + plan['id']], env=cli_env, check=True)
        print('PASS CLI: preview, wrong-plan refusal and reviewed apply against existing database')

    if '--layout' in sys.argv:
        with open(Path(tmp) / 'layout-restart.log', 'w') as log:
            server = subprocess.Popen([dotnet, 'bin/Debug/net8.0/Loom.Demo.dll'], cwd=root / 'src/Loom.Demo', env=env, stdout=log, stderr=log)
            try:
                for _ in range(100):
                    try:
                        urllib.request.urlopen('http://127.0.0.1:5080/health', timeout=1).close()
                        break
                    except Exception:
                        if server.poll() is not None: raise RuntimeError('Restart failed')
                        time.sleep(.1)
                def layout_get(path):
                    return urllib.request.urlopen(urllib.request.Request('http://127.0.0.1:5080'+path,headers={'X-Loom-Demo':'local','X-Demo-Role':'Admin'})).read()
                saved = json.loads(layout_get('/api/report-designs/Asset'))
                assert saved['version'] == 1 and saved['layout']['title'] == 'Laboratory equipment register'
                assert b'Laboratory equipment register' in layout_get('/api/reports/Asset/print')
                print('PASS visual layout: saved definition and output survive full host restart')
            finally:
                server.terminate()
                server.wait(timeout=10)

    if '--backfill' in sys.argv:
        import sqlite3
        cli_root = Path(tmp) / 'backfill-source'
        (cli_root / 'Metadata').mkdir(parents=True)
        field_id = 'abcdefabcdefabcdefabcdefabcdefab'
        for model in backfill_model:
            if model['name'] == 'Asset':
                model['version'] += 1
                model['fields'].append(dict(name='department', label='Department', type='Text', required=True, id=field_id))
            (cli_root / 'Metadata' / (model['name'] + '.json')).write_text(json.dumps(model))
        fills_file = cli_root / 'backfills.json'
        fills_file.write_text(json.dumps([dict(fieldId=field_id, value='Engineering')]))
        command = [dotnet, str(root / 'src/Loom.Demo/bin/Debug/net8.0/Loom.Demo.dll'), '--contentRoot', str(cli_root), '--backfills=' + str(fills_file)]
        cli_env = dict(env, Database__Provider='Sqlite')
        plan = json.loads(subprocess.check_output(command + ['--plan-model'], env=cli_env))
        fills_file.write_text(json.dumps([dict(fieldId=field_id, value='Changed after review')]))
        refused = subprocess.run(command + ['--apply-model=' + plan['id']], env=cli_env, capture_output=True)
        assert refused.returncode != 0
        fills_file.write_text(json.dumps([dict(fieldId=field_id, value='Engineering')]))
        subprocess.run(command + ['--apply-model=' + plan['id']], env=cli_env, check=True)
        with sqlite3.connect(Path(tmp) / 'test.db') as db:
            assert db.execute("SELECT count(*) FROM Asset WHERE department='Engineering'").fetchone()[0] == 4
            assert db.execute("SELECT count(*) FROM Asset WHERE site='Main laboratory'").fetchone()[0] == 3
            assert db.execute('SELECT count(*) FROM _migrations').fetchone()[0] == 2
        print('PASS backfill CLI: reviewed value enforced; a new process preserves browser backfills and fills four records')
