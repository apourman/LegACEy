# Crawls asheron.fandom.com Category:Landblocks: each member is a landblock id page that redirects to its place's article.
# Writes raw.json beside this file; compile.py turns it into Content/landblock-names.tsv (run it after this).
import json, os, re, time, urllib.parse, urllib.request
API = 'https://asheron.fandom.com/api.php'
def get(**q):
    q['format'] = 'json'
    req = urllib.request.Request(API + '?' + urllib.parse.urlencode(q), headers={'User-Agent': 'LegACEy landblock names (personal project)'})
    for i in range(4):
        try: return json.load(urllib.request.urlopen(req, timeout=30))
        except Exception as e: time.sleep(2 + i * 3); err = e
    raise err
titles, cont = [], {}
while True:
    d = get(action='query', list='categorymembers', cmtitle='Category:Landblocks', cmlimit=500, **cont)
    titles += [m['title'] for m in d['query']['categorymembers'] if m['ns'] == 0]
    if 'continue' not in d: break
    cont = {'cmcontinue': d['continue']['cmcontinue']}
print(len(titles), 'members')
out = {}
for i in range(0, len(titles), 50):
    batch = titles[i:i + 50]
    d = get(action='query', prop='revisions', rvprop='content', rvslots='main', titles='|'.join(batch), redirects=1)['query']
    to = {r['from']: r['to'] for r in d.get('redirects', [])}
    norm = {n['from']: n['to'] for n in d.get('normalized', [])}
    pages = {p['title']: p for p in d['pages'].values()}
    for t in batch:
        target = to.get(norm.get(t, t), norm.get(t, t))
        p = pages.get(target, {})
        text = p.get('revisions', [{}])[0].get('slots', {}).get('main', {}).get('*', '')
        field = lambda k: (m.group(1).strip() if (m := re.search(r'\|\s*' + k + r'\s*=\s*([^\n|]*)', text)) else '')
        out[t] = {'title': target, 'redirect': target != t, 'summary': (m.group(1) if (m := re.search(r'\{\{(\w[\w ]*?) Summary', text)) else ''),
                  'coordinates': field('Coordinates'), 'levels': field('Level Restrictions'), 'text': text if target == t else ''}
    time.sleep(1)
json.dump(out, open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'raw.json'), 'w'), indent=1)
