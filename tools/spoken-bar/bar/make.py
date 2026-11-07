# The recordings for the bar. Windows' own voices say the lines; then they are brought down to what the P30's microphone really
# hears (a voice reads 0,002 to 0,016 RMS through it, our old clips read 0,107), laid on the phone's own room tone, and for some
# lines mixed with a room of people, a television, a fan, a road or music made here. Nothing of ours runs on this PC.
import json, os, re, subprocess, sys, wave
import numpy as np

sys.path.insert(0, os.path.dirname(__file__))
import bank

HERE = os.path.dirname(os.path.abspath(__file__))
RAW = os.path.join(HERE, 'raw')
OUT = os.path.join(HERE, 'clips')
ROOM = os.path.join(HERE, 'room-tone.wav')
RATE = 16000
DEFAULT_LEVEL = float(sys.argv[1]) if len(sys.argv) > 1 else 0.008
os.makedirs(RAW, exist_ok=True)
os.makedirs(OUT, exist_ok=True)
rng = np.random.default_rng(11)


def read(path):
    data = open(path, 'rb').read()
    at = data.find(b'data')
    if data[:4] == b'RIFF' and at > 0:
        size = int.from_bytes(data[at + 4:at + 8], 'little')
        data = data[at + 8:at + 8 + size]
    data = data[:len(data) // 2 * 2]
    return np.frombuffer(data, dtype='<i2').astype(np.float32) / 32768


def write(path, x):
    pcm = np.clip(x * 32768, -32768, 32767).astype('<i2')
    with wave.open(path, 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm.tobytes())


def blocks(x, ms=20):
    n = RATE * ms // 1000
    b = len(x) // n * n
    return np.sqrt((x[:b].reshape(-1, n) ** 2).mean(axis=1)), n


def trim(x):
    r, n = blocks(x)
    on = np.where(r > r.max() * 0.03)[0]
    return x[on[0] * n:(on[-1] + 1) * n]


def active_rms(x):
    r, _ = blocks(x, 100)
    return float(np.median(r[r > r.max() * 0.2]))


def room_tone():
    # The longest quiet stretch of the phone's own capture: hiss and hum, no words.
    x = read(ROOM)
    r, n = blocks(x, 100)
    quiet = r < np.median(r) * 1.2
    best, start, run = (0, 0), 0, 0
    for i, q in enumerate(quiet):
        run = run + 1 if q else 0
        if run == 1:
            start = i
        if run > best[0]:
            best = (run, start)
    tone = x[best[1] * n:(best[1] + best[0]) * n]
    print(f'room tone: {len(tone) / RATE:.1f} s at rms {np.sqrt((tone ** 2).mean()):.5f}')
    return tone


TONE = room_tone()


def bed(length):
    reps = length // len(TONE) + 2
    x = np.tile(TONE, reps)
    start = int(rng.integers(0, len(TONE)))
    return x[start:start + length].copy()


def speed(x, factor):
    idx = np.arange(0, len(x) - 1, factor)
    return np.interp(idx, np.arange(len(x)), x).astype(np.float32)


def reverb(x, wet):
    n = int(RATE * 0.35)
    ir = rng.standard_normal(n).astype(np.float32) * np.exp(-np.arange(n) / (RATE * 0.07)).astype(np.float32)
    ir /= np.sqrt((ir ** 2).sum())
    return ((1 - wet) * x + wet * np.convolve(x, ir)[:len(x)]).astype(np.float32)


def rms(x):
    return float(np.sqrt((x ** 2).mean()))


def fit(x, length):
    return np.tile(x, length // len(x) + 1)[:length]


def babble(length, others):
    out = np.zeros(length, np.float32)
    for _ in range(6):
        v = others[int(rng.integers(0, len(others)))]
        v = speed(v, float(rng.uniform(0.9, 1.1)))
        out += np.roll(fit(v, length), int(rng.integers(0, length)))
    return out


def fan(length):
    w = rng.standard_normal(length).astype(np.float32)
    y = np.zeros(length, np.float32)
    a = 0.0
    for i in range(0, length):
        a = 0.97 * a + w[i]
        y[i] = a
    return y


def road(length):
    w = rng.standard_normal(length).astype(np.float32)
    y = np.zeros(length, np.float32)
    a = b = 0.0
    for i in range(length):
        a = 0.995 * a + w[i]
        b = 0.99 * b + a
        y[i] = b
    return y


def music(length):
    t = np.arange(length) / RATE
    y = np.zeros(length, np.float32)
    chords = [(220, 277, 330), (196, 247, 294), (175, 220, 262), (165, 208, 247)]
    for i, chord in enumerate(chords * (length // (RATE * 8) + 1)):
        a, b = i * 2 * RATE, min((i + 1) * 2 * RATE, length)
        if a >= length:
            break
        s = t[a:b]
        for f in chord:
            for h, g in ((1, 1.0), (2, 0.4), (3, 0.2)):
                y[a:b] += g * np.sin(2 * np.pi * f * h * s)
        y[a:b] *= np.exp(-(s - s[0]) * 1.2)
    return y


# ---- what has to be said, line by line, each split at its pauses
turns = []
for sc in bank.SCENES:
    for i, tn in enumerate(sc['turns']):
        turns.append((sc, i, tn))
ladder = []
for tag, level in bank.LEVELS:
    for key, line, answer in bank.LADDER_LINES:
        ladder.append(dict(id=f'{tag}{key}', text=line, how=dict(level=level), any=[answer], level=level))

voices = ['Microsoft Zira Desktop', 'Microsoft David Desktop']
jobs = {}
n = 0
items = []
for sc, i, tn in turns:
    n += 1
    tn = dict(tn)
    tn['scene'], tn['tier'], tn['first'] = sc['name'], sc['tier'], i == 0
    tn['fresh'] = sc['each_fresh'] or (sc['fresh'] and i == 0)
    tn['voice'] = tn.get('how', {}).get('voice', voices[(n // 3) % 2])
    items.append(tn)
for tn in ladder:
    tn['scene'], tn['tier'], tn['first'], tn['fresh'], tn['voice'] = 'ladder', 'ears', True, True, voices[0]
    items.append(tn)

for tn in items:
    how = tn.get('how', {})
    if how.get('room_only') or how.get('door'):
        continue
    said = tn.get('say', tn['text'])
    parts = re.split(r'\[(\d+)\]', said)
    tn['parts'] = parts
    for j, part in enumerate(parts[0::2]):
        name = f"{tn['id']}-{j}"
        jobs[name] = dict(name=name, text=part.strip(), voice=tn['voice'], rate=how.get('rate', 0))

todo = [j for j in jobs.values() if not os.path.exists(os.path.join(RAW, j['name'] + '.wav'))]
print(f'{len(items)} lines, {len(jobs)} pieces, {len(todo)} to say')
if todo:
    job_file = os.path.join(HERE, 'jobs.json')
    json.dump(todo, open(job_file, 'w', encoding='utf-8'))
    script = os.path.join(HERE, 'say.ps1')
    subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', script, job_file, RAW], check=True)

clean = {name: trim(read(os.path.join(RAW, name + '.wav'))) for name in jobs}
others = [x for name, x in clean.items() if len(x) > RATE * 3]
print(f'{len(others)} long lines to make a crowd of')

manifest = {}
for tn in items:
    how = tn.get('how', {})
    level = how.get('level', DEFAULT_LEVEL)
    lead = int(RATE * 1.0)
    tail = int(RATE * how.get('tail', 1800) / 1000)

    if how.get('room_only'):
        length = RATE * 7
        x = bed(length)
        start = end = 0
    elif how.get('door'):
        length = RATE * 5
        x = bed(length)
        burst = rng.standard_normal(int(RATE * 0.25)).astype(np.float32) * np.exp(-np.arange(int(RATE * 0.25)) / (RATE * 0.05)).astype(np.float32)
        x[RATE:RATE + len(burst)] += burst * 0.03
        start = end = 0
    else:
        parts = tn['parts']
        pieces = []
        for j, part in enumerate(parts):
            if j % 2 == 0:
                pieces.append(clean[f"{tn['id']}-{j // 2}"])
            else:
                pieces.append(np.zeros(int(RATE * int(part) / 1000), np.float32))
        speech = np.concatenate(pieces)
        if 'pitch' in how:
            speech = speed(speech, how['pitch'])
        if 'reverb' in how:
            speech = reverb(speech, how['reverb'])
        speech = speech * (level / active_rms(speech))
        length = lead + len(speech) + tail
        x = bed(length)
        x[lead:lead + len(speech)] += speech
        start, end = lead, lead + len(speech)
        noise = how.get('noise')
        if noise:
            made = {'babble': lambda: babble(length, others), 'tv': lambda: fit(others[3], length),
                    'fan': lambda: fan(length), 'road': lambda: road(length), 'music': lambda: music(length)}[noise]()
            made = made - float(made.mean())
            x += made * (level / 10 ** (how['snr'] / 20) / max(rms(made), 1e-9))
    write(os.path.join(OUT, tn['id'] + '.wav'), x)
    manifest[tn['id']] = {k: tn.get(k) for k in ('id', 'text', 'scene', 'tier', 'first', 'fresh', 'any', 'all', 'none', 'judge', 'claim', 'how', 'voice')}
    manifest[tn['id']].update(speech_start=round(start * 1000 / RATE), speech_end=round(end * 1000 / RATE), total=round(length * 1000 / RATE), level=level)

json.dump(manifest, open(os.path.join(HERE, 'manifest.json'), 'w', encoding='utf-8'), indent=1)
total = sum(m['total'] for m in manifest.values()) / 1000
longest = max(manifest.values(), key=lambda m: m['speech_end'] - m['speech_start'])
print(f"{len(manifest)} clips, {total / 60:.1f} minutes of audio; longest speech {(longest['speech_end'] - longest['speech_start']) / 1000:.1f} s ({longest['id']})")
print('by tier:', {k: sum(1 for m in manifest.values() if m['tier'] == k) for k in sorted({m['tier'] for m in manifest.values()})})
