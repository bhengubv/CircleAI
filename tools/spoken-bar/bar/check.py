import json, os, re, runpy, sys, io, contextlib, datetime

HERE = os.path.dirname(os.path.abspath(__file__))
buf = io.StringIO()
with contextlib.redirect_stdout(buf):
    g = runpy.run_path(os.path.join(HERE, 'score.py'))
score, items = g['score'], g['items']

fast = lambda r: r['real'] is not None and 0 < r['real'] <= 2000 and r['captured'] and r['captured'] >= r['speech'] + 950
print('right and within 2 s, all:', sum(1 for r in items if score[r['id']] == 'R' and fast(r)), 'of', len(items))
for tier in ['alexa', 'jarvis', 'her', 'complexity', 'length', 'ears']:
    group = [r for r in items if r['tier'] == tier]
    hit = [r['id'] for r in group if score[r['id']] == 'R' and fast(r)]
    print(f' {tier}: {len(hit)} of {len(group)} {hit}')
    t = sorted(r['real'] for r in group if r['real'] is not None and r['real'] > 0)
    if t:
        print(f"   median {t[len(t) // 2] / 1000:.1f} s")
print('right turns: table', sum(1 for r in items if score[r['id']] == 'R' and not r['prompt']))

# notes per turn, by turn window, across the two long runs
S = os.path.join(HERE, '..')
for folder in ['bar-main', 'bar-rest']:
    name = os.path.join(S, folder, 'logcat1.txt')
    notes, cur = {}, None
    asked = [l.split() for l in open(os.path.join(S, folder, 'turns.txt'))]
    times = [(a[1], a[2]) for a in asked]
    for s in open(name, encoding='utf-8', errors='replace'):
        m = re.match(r'^11-07 (\d\d:\d\d:\d\d)', s)
        if not m:
            continue
        x = re.search(r'notes for this turn: (\d+) characters', s)
        if x and int(x[1]) > 0:
            now = m[1]
            who = [c for t, c in times if t <= now]
            if who:
                notes[who[-1]] = max(notes.get(who[-1], 0), int(x[1]))
    print(folder, 'turns with notes:', len(notes), 'of', len(asked))
    heavy = [r for r in items if (r['run'] or 0) >= 60 and r['source'] == folder]
    print('  heavy turns and their notes:', [(r['id'], r['run'], notes.get(r['id'], 0)) for r in heavy])
    print('  notes on light turns:', sorted((k, v) for k, v in notes.items() if k not in [r['id'] for r in heavy])[:12])

# the long ramble: what the turn did
name = os.path.join(S, 'bar-rest', 'logcat1.txt')
sent = {a[2]: a[1] for a in (l.split() for l in open(os.path.join(S, 'bar-rest', 'turns.txt')))}
start = sent['b06']
after = sent['b07']
for s in open(name, encoding='utf-8', errors='replace'):
    m = re.match(r'^11-07 (\d\d:\d\d:\d\d)', s)
    if m and start <= m[1] <= after and re.search(r'CircleAI\.(WakeTurn|Voice: spoken|Voice: tts: \d|Soak: talk|Asks: ask .* (accepted|cancel)|Turn: render)', s) and 'kws' not in s:
        print(s[6:150].rstrip())
