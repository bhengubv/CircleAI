# Reads a p30-soak.ps1 folder: every turn, then the same numbers in five-minute stretches, then anything that went wrong.
import re, sys, statistics, datetime, collections
folder = sys.argv[1]
window = int(sys.argv[2]) if len(sys.argv) > 2 else 5
JST = datetime.timezone(datetime.timedelta(hours=9))
LINE = re.compile(r'^(\d\d)-(\d\d) (\d\d):(\d\d):(\d\d)\.(\d{3})\s+(\d+)\s+(\d+) (\w) ([^:]+): ?(.*)$')

def stamp(m):
    mo, d, h, mi, s, ms = (int(x) for x in m.groups()[:6])
    return datetime.datetime(2026, mo, d, h, mi, s, ms * 1000, JST).timestamp()

turns, current, trouble = [], None, []
ours = set()
for raw in open(folder + r'\logcat.txt', encoding='utf-8', errors='replace'):
    m = LINE.match(raw.rstrip('\r\n'))
    if not m: continue
    t, pid, level, tag, text = stamp(m), m.group(7), m.group(9), m.group(10).strip(), m.group(11)
    if tag.startswith('CircleAI'): ours.add(pid)
    if re.search(r'Fatal signal|Abort message|FATAL EXCEPTION|ANR in .*circleai|Process .*circleai.* has died|lowmemorykiller.*circleai|Killing .*circleai|Force finishing .*circleai', text):
        trouble.append((t, tag, text[:170]))
    if pid in ours and level in 'EF': trouble.append((t, tag, text[:170]))
    if 'woke on "a recorded question"' in text:
        current = dict(start=t, pid=pid)
        turns.append(current)
    if current is None: continue
    x = re.search(r'spoken turn: (\d+) ms from the last word to the first sound: microphone (\d+), ears (-?\d+), brain (\d+), to the voice (\d+), voice (\d+) \((\d+) chars first\); longest silence inside the answer (\d+) ms', text)
    if x and 'turn' not in current:
        current.update(turn=int(x[1]), mic=int(x[2]), ears=int(x[3]), brain=int(x[4]), tovoice=int(x[5]), voice=int(x[6]), firstchars=int(x[7]), gap=int(x[8]))
    x = re.search(r'captured (\d+) bytes in (\d+) ms', text)
    if x and 'captured' not in current: current.update(captured=int(x[2]), closed=t)
    x = re.search(r'^heard: (\d+) chars', text)
    if x: current['heard'] = int(x[1])
    x = re.search(r'^answer: (\d+) chars', text)
    if x: current['answer'] = int(x[1])
    x = re.search(r'(\d+) prompt tokens, (\d+) run \((\S+)\) in (\d+) ms; (\d+) tokens in (\d+) ms \(([\d.,]+) tok/s\); (\w+)', text)
    if x and 'prompt' not in current:
        current.update(prompt=int(x[1]), run=int(x[2]), kind=x[3], readms=int(x[4]), toks=int(x[5]), writems=int(x[6]), rate=float(x[7].replace(',', '.')), stop=x[8])
    x = re.search(r'CIRCLEAI-TURN .*model=(\S+)', text)
    if x and 'model' not in current: current.update(model=x[1])
    if 'talk: the turn is over' in text:
        current['over'] = t
        current = None
    elif 'CircleAI.Soak' in tag and text.startswith('talk: '):
        current['failed'] = text[:120]
        current = None

asked = [l.split() for l in open(folder + r'\turns.txt')]
day = datetime.datetime.fromtimestamp(turns[0]['start'], JST) if turns else None
def when(hms):
    h, mi, s = (int(x) for x in hms.split(':'))
    return day.replace(hour=h, minute=mi, second=s, microsecond=0).timestamp()
for t in turns:
    t['clip'], t['n'] = '?', 0
    for a in asked:
        if when(a[1]) <= t['start'] + 1:
            t['clip'], t['n'] = a[2], int(a[0])
woke = {t['n'] for t in turns}
print('asked and never woke:', [a[0] + ' ' + a[2] for a in asked if int(a[0]) not in woke])

clocks = []
try:
    for l in open(folder + r'\clocks.txt'):
        p = l.split()
        if len(p) == 3: clocks.append((float(p[0]), int(p[1]) // 1000, int(p[2]) // 1000))
except OSError:
    pass

probes, block = [], None
for l in open(folder + r'\probe.txt', encoding='utf-8', errors='replace'):
    l = l.strip()
    if l.startswith('=== '):
        block = dict(n=int(l.split()[1]))
        probes.append(block)
        continue
    if block is None: continue
    x = re.match(r'pids (\d+)? ?(\d+)?', l)
    if x: block['pids'] = (x[1], x[2])
    x = re.search(r'Temperature\{mValue=([\d.]+).*mName=(\w+)', l)
    if x: block[x[2]] = float(x[1])
    x = re.search(r'CoolingDevice\{mValue=(\d+).*mName=(\w+)', l)
    if x: block['cool_' + x[2]] = int(x[1])
    x = re.match(r'mem (\d+) VmRSS: (\d+) kB VmSwap: (\d+) kB', l)
    if x: block.setdefault('mem', []).append((int(x[2]) // 1024, int(x[3]) // 1024))
    x = re.match(r'stat (\d+) (\d+) (\d+) (\d+)', l)
    if x: block.setdefault('faults', []).append(int(x[2]))
    x = re.match(r'MemAvailable: (\d+)', l)
    if x: block['avail'] = int(x[1]) // 1024
    x = re.match(r'SwapFree: (\d+)', l)
    if x: block['swapfree'] = int(x[1]) // 1024
    x = re.search(r'Display Power: state=(\w+)', l)
    if x: block['display'] = x[1]
    x = re.search(r'Thermal Status: (\d+)', l)
    if x: block['status'] = int(x[1])
    x = re.match(r'level: (\d+)', l)
    if x: block['level'] = int(x[1])
    x = re.match(r'temperature: (\d+)', l)
    if x: block['batt'] = int(x[1]) / 10
byturn = {p['n']: p for p in probes}

def clock_after(t):
    if 'closed' not in t or not clocks: return None, None
    big = [c[2] for c in clocks if t['closed'] <= c[0] <= t['closed'] + 3]
    little = [c[1] for c in clocks if t['closed'] <= c[0] <= t['closed'] + 3]
    return (statistics.median(big) if big else None), (statistics.median(little) if little else None)

print(' n  min clip     turn ears brain tovo voice gap | prompt run kind      read toks tok/s ans heard | big  ltl | c1 c0 batt shell cool rss/swap svc, brain  avail swapfree')
start = turns[0]['start'] if turns else 0
for t in turns:
    p = byturn.get(t['n'], {})
    b, l = clock_after(t)
    t['big'], t['little'] = b, l
    t['minute'] = (t['start'] - start) / 60
    mem = p.get('mem', [])
    print(f"{t['n']:3} {t['minute']:4.1f} {t['clip']:8} {t.get('turn','-'):>4} {t.get('ears','-'):>4} {t.get('brain','-'):>5} {t.get('tovoice','-'):>4} {t.get('voice','-'):>4} {t.get('gap','-'):>4} | "
          f"{t.get('prompt','-'):>5} {t.get('run','-'):>3} {t.get('kind','-'):9} {t.get('readms','-'):>5} {t.get('toks','-'):>3} {t.get('rate','-'):>5} {t.get('answer','-'):>3} {t.get('heard','-'):>3} | "
          f"{b} {l} | {p.get('cluster1','-')} {p.get('cluster0','-')} {p.get('batt','-')} {p.get('shell_frame','-')} {p.get('cool_cluster1','-')}/{p.get('cool_cluster0','-')} {mem} {p.get('avail','-')} {p.get('swapfree','-')}" + (f"  FAILED {t['failed']}" if 'failed' in t else ''))

def line(name, rows):
    ms = [r['turn'] for r in rows if 'turn' in r]
    if not ms: return f'{name}: none'
    q = sorted(ms)
    return f"{name}: {len(ms)} turns, median {statistics.median(ms):.0f}, best {q[0]}, worst {q[-1]}, at or under 2000: {sum(m <= 2000 for m in ms)}, at or under 2500: {sum(m <= 2500 for m in ms)}"

brain = [t for t in turns if t['clip'] != 'q-kenya']
table = [t for t in turns if t['clip'] == 'q-kenya']
print()
print(line('brain turns', brain))
print(line('table turns', table))
for clip in sorted({t['clip'] for t in turns}):
    print(line('  ' + clip, [t for t in turns if t['clip'] == clip]))
print()
print('stretch        brain: n  median  worst  ears brain tovo voice  gaps  write-t/s  big  ltl   c1  batt  shell')

def med(key, source):
    values = [r[key] for r in source if r.get(key) is not None]
    return statistics.median(values) if values else float('nan')

last = max(t['minute'] for t in turns) if turns else 0
w = 0
while w <= last:
    rows = [t for t in brain if w <= t['minute'] < w + window and 'turn' in t]
    if rows:
        ps = [byturn.get(r['n'], {}) for r in rows]
        print(f"{w:3}-{w+window:3} min  {len(rows):8}  {med('turn', rows):6.0f} {max(r['turn'] for r in rows):6} {med('ears', rows):6.0f} {med('brain', rows):5.0f} {med('tovoice', rows):4.0f} {med('voice', rows):4.0f} {sum(r['gap'] > 0 for r in rows):3}/{max(r['gap'] for r in rows):4} {med('rate', rows):7.2f} {med('big', rows):6.0f} {med('little', rows):5.0f} {med('cluster1', ps):5.1f} {med('batt', ps):5.1f} {med('shell_frame', ps):5.1f}")
    w += window
print()
print('process ids seen:', dict(collections.Counter(p.get('pids') for p in probes)))
print('display states:', dict(collections.Counter(p.get('display') for p in probes)))
print('thermal status:', dict(collections.Counter(p.get('status') for p in probes)), ' cooling cluster1:', dict(collections.Counter(p.get('cool_cluster1') for p in probes)), ' cluster0:', dict(collections.Counter(p.get('cool_cluster0') for p in probes)))
print('turns with no spoken line:', [str(t['n']) + ' ' + t['clip'] for t in turns if 'turn' not in t])
print('what the ears heard, chars by clip:', {c: dict(collections.Counter(t.get('heard') for t in turns if t['clip'] == c)) for c in sorted({t['clip'] for t in turns})})
print('answer lengths by clip:', {c: dict(sorted(collections.Counter(t.get('answer') for t in turns if t['clip'] == c).items(), key=lambda kv: str(kv[0]))) for c in sorted({t['clip'] for t in turns})})
print('models:', dict(collections.Counter(t.get('model') for t in turns)), ' read kinds:', dict(collections.Counter(t.get('kind') for t in turns)))
print('captured ms by clip:', {c: (min(t['captured'] for t in turns if t['clip'] == c and 'captured' in t), max(t['captured'] for t in turns if t['clip'] == c and 'captured' in t)) for c in sorted({t['clip'] for t in turns if 'captured' in t})})
print(f'trouble lines: {len(trouble)}')
seen = collections.Counter(re.sub(r'\d+', '#', x[2])[:120] for x in trouble)
for text, count in seen.most_common(14):
    print(f'  {count}x {text}')
