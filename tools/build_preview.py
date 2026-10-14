"""Bundle the real client with an explicitly labeled offline UI adapter."""
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
www = root / 'src/Loom.Demo/wwwroot'
models = [json.loads(p.read_text()) for p in sorted((root / 'src/Loom.Demo/Metadata').glob('*.json'))]
html = (www / 'index.html').read_text()
html = html.replace('<link rel="stylesheet" href="/style.css">', '<style>' + (www / 'style.css').read_text() + '</style>')
scripts = 'window.LOOM_MODELS=' + json.dumps(models) + ';\n' + (root / 'tools/offline-adapter.js').read_text() + '\n' + (www / 'app.js').read_text()
html = html.replace('<script src="/app.js"></script>', '<script>' + scripts.replace('</script', '<\\/script') + '</script>')
html = html.replace('<title>Loom • Equipment desk</title>', '<title>Loom • Offline framework demo</title>')
out = root / 'Loom_Browser_Demo.html'
out.write_text(html)
print(out)
