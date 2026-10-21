# Turns raw.json (crawl.py) into Content/landblock-names.tsv: one line per landblock, `XXYY<tab>name[|name...]`.
# Rebuild: `python3 scripts/content/landblock-names/compile.py` from the repo root (paths are relative to this file).
# Run `python3 crawl.py` first (about 25 requests): it writes raw.json, which is not tracked.
import json, os, re
here = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(here, 'raw.json'), encoding='utf-8') as f:
    d = json.load(f)
def clean(n):
    n = re.sub(r'^\d+(\.\d+)?[NS],?\s*\d+(\.\d+)?[EW]\s*-\s*', '', n)     # "80.8N, 57.4W - Penguin Rookery"
    return re.sub(r'\s*\(\d+(\.\d+)?[NS],?\s*\d+(\.\d+)?[EW]\)$', '', n)  # "Tusker Temple (0.7N 68.1W)"
lines = []
for lb, v in sorted(d.items()):
    if v['redirect']: names = [v['title']]
    else: names = [l.split('|')[-1].strip() for l in re.findall(r'\[\[([^\]]+)\]\]', v['text']) if not re.match(r'(Category|File):', l)]
    names = list(dict.fromkeys(clean(n) for n in names))
    assert names and not any('|' in n or '\t' in n or '\n' in n for n in names), lb
    lines.append(f"{int(lb, 16):04X}\t{'|'.join(names)}")
out = os.path.join(here, '..', '..', '..', 'Content', 'landblock-names.tsv')
with open(out, 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(lines) + '\n')
print(len(lines), 'landblocks,', sum('|' in l for l in lines), 'shared by several places ->', os.path.normpath(out))
