# Reads a p30-bar.ps1 folder against the bank: what was said, what was heard, what came back, how long it really took, and the key's
# first opinion of the answer. Open questions are marked for reading; nothing here is the last word on an answer.
import collections, datetime, json, os, re, statistics, sys

folder = sys.argv[1]
HERE = os.path.dirname(os.path.abspath(__file__))
manifest = json.load(open(os.path.join(HERE, 'manifest.json'), encoding='utf-8'))
JST = datetime.timezone(datetime.timedelta(hours=9))
LINE = re.compile(r'^(\d\d)-(\d\d) (\d\d):(\d\d):(\d\d)\.(\d{3})\s+(\d+)\s+(\d+) (\w) ([^:]+): ?(.*)$')


def stamp(m):
    mo, d, h, mi, s, ms = (int(x) for x in m.groups()[:6])
    return datetime.datetime(2026, mo, d, h, mi, s, ms * 1000, JST).timestamp()


import glob


def lines():
    seen = set()
    for name in sorted(glob.glob(os.path.join(folder, 'logcat*.txt')), key=lambda f: (len(f), f)):
        if 'logcat-err' in name:
            continue
        for raw in open(name, encoding='utf-8', errors='replace'):
            if raw in seen:
                continue
            seen.add(raw)
            yield raw


turns, current, kills = [], None, []
said = None
for raw in lines():
    m = LINE.match(raw.rstrip('\r\n'))
    if not m:
        continue
    t, tag, text = stamp(m), m.group(10).strip(), m.group(11)
    if re.search(r'Killing \d+:com\.bhengubv\.circleai.*iAwareK', text):
        kills.append((t, text[:110]))
        if current is not None:
            current['killed'] = True
            current = None
    if 'woke on "a recorded question"' in text:
        current = dict(start=t)
        turns.append(current)
    if current is None:
        continue
    x = re.search(r'spoken turn: (\d+) ms from the last word to the first sound: microphone (\d+), ears (-?\d+), brain (\d+), to the voice (\d+), voice (\d+) \((\d+) chars first\); longest silence inside the answer (\d+) ms', text)
    if x and 'turn' not in current:
        current.update(turn=int(x[1]), mic=int(x[2]), ears=int(x[3]), brain=int(x[4]), tovoice=int(x[5]), voice=int(x[6]), gap=int(x[8]))
    x = re.search(r'captured (\d+) bytes in (\d+) ms', text)
    if x and 'captured' not in current:
        current.update(bytes=int(x[1]), captured=int(x[2]))
    x = re.search(r'the recording was heard as: (.*)$', text)
    if x:
        current['heard'] = x[1]
    x = re.search(r'the recording was answered: (.*)$', text)
    if x:
        current['answer'] = x[1]
        said = (raw[:18], m.group(7), m.group(8))
    elif said == (raw[:18], m.group(7), m.group(8)) and tag == 'CircleAI.WakeTurn':
        current['answer'] += ' / ' + text
    x = re.search(r'(\d+) prompt tokens, (\d+) run \((\S+)\) in (\d+) ms; (\d+) tokens in (\d+) ms \(([\d.,]+) tok/s\); (\w+)', text)
    if x and 'prompt' not in current:
        current.update(prompt=int(x[1]), run=int(x[2]), kind=x[3], readms=int(x[4]), toks=int(x[5]), rate=float(x[7].replace(',', '.')), stop=x[8])
    x = re.search(r'CIRCLEAI-TURN .*model=(\S+)', text)
    if x and 'model' not in current:
        current['model'] = x[1]
    x = re.search(r'finish=(\S+)', text)
    if x and 'CIRCLEAI-TURN' in text:
        current['finish'] = x[1]
    if 'nothing was said' in text:
        current['nothing'] = True
    if 'spoken turn: no sound came out' in text:
        current['mute'] = True
    if 'the words moved after' in text:
        current['moved'] = True
    if 'talk: the turn is over' in text:
        current['over'] = t
        current = None

asked = [l.split() for l in open(os.path.join(folder, 'turns.txt'))]
day = datetime.datetime.fromtimestamp(turns[0]['start'], JST) if turns else None


def when(hms):
    h, mi, s = (int(x) for x in hms.split(':'))
    return day.replace(hour=h, minute=mi, second=s, microsecond=0).timestamp()


for t in turns:
    t['id'], t['n'] = '?', 0
    for a in asked:
        if when(a[1]) <= t['start'] + 1:
            t['id'], t['n'] = a[2], int(a[0])
by_id = {t['id']: t for t in turns}


def norm(text):
    return re.sub(r'[^a-z0-9 ]', ' ', text.lower().replace('[1200]', ' ').replace('[2000]', ' ')).split()


def wer(truth, heard):
    a, b = norm(truth), norm(heard or '')
    d = list(range(len(b) + 1))
    for i, x in enumerate(a, 1):
        p, d[0] = d[0], i
        for j, y in enumerate(b, 1):
            p, d[j] = d[j], min(d[j] + 1, d[j - 1] + 1, p + (x != y))
    return d[len(b)] / max(1, len(a))


def key(m, answer):
    if not answer:
        return 'none'
    low = answer.lower()
    for bad in m.get('none') or []:
        if re.search(bad, low):
            return 'FAIL-claim'
    if m.get('all') and not all(re.search(p, low) for p in m['all']):
        return 'fail'
    if m.get('any') and not any(re.search(p, low) for p in m['any']):
        return 'fail'
    if m.get('any') or m.get('all'):
        return 'ok' if not m.get('judge') else 'ok?'
    return 'read'


rows = []
for a in asked:
    m = manifest[a[2]]
    t = by_id.get(a[2], {})
    speech_end = m['speech_end']
    real = None
    if 'turn' in t and 'captured' in t and speech_end:
        real = (t['captured'] - speech_end) + (t['turn'] - t['mic'])
    rows.append(dict(id=a[2], n=int(a[0]), tier=m['tier'], scene=m['scene'], text=m['text'], heard=t.get('heard'), answer=t.get('answer'),
                     clock=t.get('turn'), real=real, ears=t.get('ears'), brain=t.get('brain'), tovoice=t.get('tovoice'), voice=t.get('voice'), gap=t.get('gap'),
                     captured=t.get('captured'), speech=(m['speech_end'] - m['speech_start']) if m['speech_end'] else 0,
                     prompt=t.get('prompt'), run=t.get('run'), kind=t.get('kind'), readms=t.get('readms'), toks=t.get('toks'), rate=t.get('rate'), stop=t.get('stop'),
                     model=t.get('model'), finish=t.get('finish'), wer=round(wer(m['text'], t.get('heard')), 2) if m['speech_end'] else None,
                     key=key(m, t.get('answer')), judge=m.get('judge'), killed=t.get('killed', False), nothing=t.get('nothing', False), moved=t.get('moved', False), mute=t.get('mute', False)))

json.dump(rows, open(os.path.join(folder, 'rows.json'), 'w', encoding='utf-8'), indent=1, ensure_ascii=False)

mode = sys.argv[2] if len(sys.argv) > 2 else 'all'
if mode in ('all', 'words'):
    for r in rows:
        print(f"[{r['id']}] {r['tier']}/{r['scene']} real={r['real']} clock={r['clock']} ears={r['ears']} brain={r['brain']} voice={r['voice']} gap={r['gap']} cap={r['captured']} speech={r['speech']} prompt={r['prompt']} run={r['run']}/{r['kind']} read={r['readms']} toks={r['toks']} {r['rate']} stop={r['stop']} model={r['model']} wer={r['wer']} key={r['key']}" + (' KILLED' if r['killed'] else '') + (' NOTHING' if r['nothing'] else '') + (' MOVED' if r['moved'] else ''))
        print(f"   SAID : {r['text'][:220]}")
        print(f"   HEARD: {(r['heard'] or '-')[:220]}")
        print(f"   SAYS : {(r['answer'] or '-')[:420]}")
print()


def show(name, group):
    done = [r for r in group if r['real'] is not None]
    if not done:
        print(f'{name}: {len(group)} asked, none timed')
        return
    v = sorted(r['real'] for r in done)
    print(f"{name}: {len(group)} asked, {len(done)} timed; median {statistics.median(v):.0f} ms, best {v[0]}, worst {v[-1]}; within 2 s {sum(x <= 2000 for x in v)}, within 3 s {sum(x <= 3000 for x in v)}")


for tier in sorted({r['tier'] for r in rows}):
    show(tier, [r for r in rows if r['tier'] == tier])
print('kills:', [k[1] for k in kills])
print('keys:', dict(collections.Counter(r['key'] for r in rows)))
w = [r['wer'] for r in rows if r['wer'] is not None]
if w:
    print(f'ears: mean word error {statistics.mean(w):.2f}, perfect {sum(x == 0 for x in w)} of {len(w)}')
