"""Build a self-contained, explicitly simulated edition of the live introduction."""
from pathlib import Path
root=Path(__file__).resolve().parents[1]
www=root/'src/Loom.Demo/wwwroot'
html=(www/'introduction.html').read_text(encoding='utf-8')
html=html.replace('<link rel="stylesheet" href="/introduction.css">','<style>'+(www/'introduction.css').read_text(encoding='utf-8')+'</style>')
script='window.LOOM_INTRO_STANDALONE=true;\n'+(www/'introduction.js').read_text(encoding='utf-8')
html=html.replace('<script src="/introduction.js"></script>','<script>'+script.replace('</script','<\\/script')+'</script>')
(root/'Loom_Introduction.html').write_text(html,encoding='utf-8')
print(root/'Loom_Introduction.html')
