# The answers as read by hand, one line each, and what the numbers say. R right, P partly, N an honest "I can't", W wrong.
# For a wrong answer, the one thing that sank it. Where two runs answered, the later one counts.
import collections, datetime, glob, json, os, re, statistics

HERE = os.path.dirname(os.path.abspath(__file__))
rows = json.load(open(os.path.join(HERE, 'all-rows.json'), encoding='utf-8'))

R, P, N = 'R', 'P', 'N'
score = dict(
    # Alexa
    a01=R, a02=R, a03='W shortcut', a04=R, a05=R, a06=R, a07='W ears', a08='W ears', a09=R, a10='W cannot act', a11='W cannot act', a12='W cannot act',
    a13='W cannot act', a14=N, a15=N, a16='W brain', a17=R, a18='W brain', a19='W tool text', a20=R, a21=R, a22=R, a23=R, a24='W brain', a25=R,
    a26='W brain', a27=R, a28='W forgot',
    # reasoning, honesty, instructions, making things
    c01='W brain', c02='W brain', c03='W brain', c04=R, c05=R, c06=P, c07=P, c08='W notes', c09=R, c10='W mic cut', c11=R, c12=R, c13=P, c14=R,
    c15='W ears', c16='W brain', c17=P, c18='W mute', c19=P, c20=R, c21=R, c22=R, c23='W ears', c24=R,
    # Her
    h01=P, h02=P, h03='W notes', h04=P, h05=R, h06=P, h07=R, h08=R, h09=R, h10=P, h11='W brain',
    # Jarvis
    j01=R, j02='W forgot', j03='W forgot', j04='W notes', j05='W forgot', j06='W forgot', j07=P, j08='W forgot', j09='W forgot', j10='W forgot',
    j11=P, j12=R, j13=R, j14='W brain', j15='W forgot', j16='W forgot', j17=R, j18=R, j19=R, j20=R, j21='W ears', j22='W tool text', j23='W forgot',
    j24=R, j25=P, j26='W forgot', j27=P, j28=R, j29='W brain', j30='W brain', j31='W brain', j32=R, j33=R,
    # length
    b01='W ears', b02=R, b03=P, b04=R, b05=R, b06='W mic cut', b07='W mic cut',
    # the room
    e01=R, e02=R, e03='W mic cut', e04='W mic cut', e05='W brain', e06=R, e07=R, e08=R, e09=R, e10=R, e11=R, e12=P, e13=R, e14=R, e15=R, e16=R,
    e17='W notes', e18=R, e19=R,
)
items = [r for r in rows if r['id'] in score]
assert len(items) == 122 and len(score) == 122, (len(items), len(score))
names = dict(alexa='Alexa everyday', complexity='Reasoning and making', her='Her: company', jarvis='Jarvis: holding the thread', length='Talking for longer', ears='Messy speech and noise')

print('RIGHT / PARTLY / HONEST NO / WRONG')
total = collections.Counter()
for tier in ['alexa', 'jarvis', 'her', 'complexity', 'length', 'ears']:
    group = [r for r in items if r['tier'] == tier]
    c = collections.Counter(score[r['id']][0] for r in group)
    total.update(c)
    print(f"{names[tier]:28} {len(group):3} asked: {c['R']:3} R {c['P']:3} P {c['N']:2} N {c['W']:3} W   right {100 * c['R'] / len(group):.0f}%")
print(f"{'ALL':28} {sum(total.values()):3} asked: {total['R']:3} R {total['P']:3} P {total['N']:2} N {total['W']:3} W   right {100 * total['R'] / sum(total.values()):.0f}%")
print()
print('WHAT SANK THE WRONG ONES')
for cause, n in collections.Counter(v[2:] for v in score.values() if v.startswith('W')).most_common():
    print(f'{n:3} {cause}')
print()


def show(name, group):
    v = sorted(r['real'] for r in group)
    if not v:
        print(f'{name}: none')
        return
    print(f"{name}: {len(v)} turns, median {statistics.median(v) / 1000:.2f} s, best {v[0] / 1000:.2f}, worst {v[-1] / 1000:.1f}; within 2 s {sum(x <= 2000 for x in v)} ({100 * sum(x <= 2000 for x in v) / len(v):.0f}%), within 3 s {sum(x <= 3000 for x in v)} ({100 * sum(x <= 3000 for x in v) / len(v):.0f}%), over 5 s {sum(x > 5000 for x in v)}")


def noisy(r):
    return r['id'] in ('e10', 'e11', 'e12', 'e13', 'e14', 'e15')


timed = [r for r in items if r['real'] is not None and r['captured'] and r['captured'] >= r['speech'] + 1000 - 50 and r['real'] > 0]
cut = [r['id'] for r in items if r['real'] is not None and r not in timed]
print('SPEED, last word to first sound, real clock')
print('cut off before the end of the words, so not timed:', cut)
show('all that spoke', timed)
quiet = [r for r in timed if not noisy(r)]
table = [r for r in quiet if not r['prompt']]
brain = [r for r in quiet if r['prompt']]
heavy = [r for r in brain if (r['run'] or 0) >= 60]
light = [r for r in brain if (r['run'] or 0) < 60]
show('from a table, quiet room', table)
show('through the brain, quiet room', brain)
show('  brain, plain reading', light)
show('  brain, 60+ tokens to read first', heavy)
show('in noise', [r for r in timed if noisy(r)])
for name, a, b in [('under 2 s of talking', 0, 2000), ('2 to 5 s', 2000, 5000), ('over 5 s', 5000, 99999)]:
    show('  brain, light, ' + name, [r for r in light if a <= r['speech'] < b])
print('slow ones:', [(r['id'], round(r['real'] / 1000, 1), r['run'], r['prompt']) for r in sorted(heavy, key=lambda r: -r['real'])])
g = [r['gap'] for r in timed if r['gap'] is not None]
print(f'gaps inside answers: {sum(x > 300 for x in g)} of {len(g)} over 300 ms, {sum(x > 700 for x in g)} over 700 ms, worst {max(g)} ms')
print('writing speed, tokens a second, median', statistics.median([r['rate'] for r in timed if r['rate']]))
print('answers cut by the 80-token limit:', [r['id'] for r in items if r['stop'] == 'Length'])
print()
print('noise: how long after the recording ended the microphone closed')
man = json.load(open(os.path.join(HERE, 'manifest.json'), encoding='utf-8'))
for i in ['e10', 'e11', 'e13', 'e14', 'e15', 'e12']:
    r = next(x for x in items if x['id'] == i)
    print(i, man[i]['how'].get('noise'), man[i]['how'].get('snr'), 'dB; clip ends', man[i]['total'], 'mic closed', r['captured'], 'after the last word by', r['captured'] - man[i]['speech_end'])

# The time the phone cannot hear its name, and the wait before it listens, from the run's own log.
S = os.path.join(HERE, '..', 'bar-rest', 'logcat1.txt')
line = re.compile(r'^11-07 (\d\d):(\d\d):(\d\d)\.(\d{3})')


def at(s):
    m = line.match(s)
    return int(m[1]) * 3600 + int(m[2]) * 60 + int(m[3]) + int(m[4]) / 1000 if m else None


over = deaf = None
deafs, opens, woke = [], [], None
for s in open(S, encoding='utf-8', errors='replace'):
    if 'CircleAI.Soak: talk: the turn is over' in s:
        over = at(s)
    elif 'CircleAI.ScreenWake: listening for:' in s and over:
        deafs.append(at(s) - over)
        over = None
    elif 'woke on "a recorded question"' in s:
        woke = at(s)
        over = None
    else:
        m = re.search(r'captured \d+ bytes in (\d+) ms', s)
        if m and woke:
            opens.append(at(s) - int(m[1]) / 1000 - woke)
            woke = None
print(f'deaf after a turn: median {statistics.median(deafs):.1f} s, worst {max(deafs):.1f} s over {len(deafs)}')
print(f'name to open microphone: median {statistics.median(opens):.1f} s over {len(opens)}')
