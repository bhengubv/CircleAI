"""Emit the C test's expected-value tables from the fixtures.

The C port has no JSON reader and will not vendor one, so its expectations are
LITERALS. Generating them here rather than typing them by hand is the difference
between a transcription and a transcription error.
"""
import io
import json
import os
import sys

FIX = sys.argv[1]
OUT = sys.argv[2]


def cstr(s):
    out = []
    for b in s.encode('utf-8'):
        if b == 0x22:
            out.append('\\"')
        elif b == 0x5C:
            out.append('\\\\')
        elif b == 0x0A:
            out.append('\\n')
        elif b == 0x0D:
            out.append('\\r')
        elif b == 0x09:
            out.append('\\t')
        elif 0x20 <= b < 0x7F:
            out.append(chr(b))
        else:
            # Close and reopen the literal so a following hex digit cannot be
            # swallowed into the escape.
            out.append('\\x%02x" "' % b)
    return '"' + ''.join(out) + '"'


def read(n):
    with io.open(os.path.join(FIX, n), encoding='utf-8') as f:
        return json.load(f)


L = []
w = L.append

w('/* voice_text_expected.h — GENERATED from the fixtures; see test_voice_text.c. */')
w('')

sp = read('voice_sentence_splitter.json')
w('#define SPLITTER_MAX_CHARS %d' % sp['maxCharsPerSegment'])
w('')
for i, c in enumerate(sp['cases']):
    if not c['segments']:
        continue
    w('static const seg_expect SPLIT_%d[] = {' % i)
    for s in c['segments']:
        w('    { %s, %d },' % (cstr(s['text']), s['trailingPauseMs']))
    w('};')
w('')
w('static const splitter_case SPLITTER_CASES[] = {')
for i, c in enumerate(sp['cases']):
    arr = ('SPLIT_%d' % i) if c['segments'] else 'NULL'
    w('    { %s, %s, %s, %d },' % (cstr(c['name']), cstr(c['text']), arr, len(c['segments'])))
w('};')
w('')

ls = read('voice_language_spans.json')
for i, c in enumerate(ls['split']):
    if not c['spans']:
        continue
    w('static const span_expect SPANS_%d[] = {' % i)
    for s in c['spans']:
        w('    { %s, %d },' % (cstr(s['text']), 1 if s['isForeign'] else 0))
    w('};')
w('')
w('static const spans_case SPANS_CASES[] = {')
for i, c in enumerate(ls['split']):
    arr = ('SPANS_%d' % i) if c['spans'] else 'NULL'
    w('    { %s, %s, %d },' % (cstr(c['text']), arr, len(c['spans'])))
w('};')
w('')
w('static const pair_case SPOKEN_CASES[] = {')
for c in ls['toSpokenForm']:
    w('    { %s, %s },' % (cstr(c['input']), cstr(c['output'])))
w('};')
w('')
w('static const flag_case FOREIGN_CASES[] = {')
for c in ls['isForeignWord']:
    w('    { %s, %d },' % (cstr(c['word']), 1 if c['foreign'] else 0))
w('};')
w('')

gz = read('voice_geez_romanizer.json')
w('static const flag_case ETHIOPIC_CASES[] = {')
for c in gz['isEthiopic']:
    w('    { %s, %d },' % (cstr(c['text']), 1 if c['ethiopic'] else 0))
w('};')
w('')
w('static const pair_case ROMANIZE_CASES[] = {')
for c in gz['romanize']:
    w('    { %s, %s },' % (cstr(c['input']), cstr(c['output'])))
w('};')
w('')

ts = read('voice_tone_shaper.json')
w('#define TONE_WAVEFORM_TOLERANCE %.17g' % ts['waveformTolerance'])
w('#define TONE_COEFFICIENT_TOLERANCE %.17g' % ts['coefficientTolerance'])
w('#define TONE_SAMPLE_RATE %d' % ts['waveform']['sampleRate'])
w('#define TONE_SILENCE_COUNT %d' % len(ts['silenceStaysSilent']))
w('')
w('static const settings_expect TONE_SETTINGS = { %s, %s, %s, %s, %s, %s };' % (
    '%.17g' % ts['settings']['lowShelfHz'], '%.17g' % ts['settings']['lowShelfDb'],
    '%.17g' % ts['settings']['presenceHz'], '%.17g' % ts['settings']['presenceDb'],
    '%.17g' % ts['settings']['presenceQ'], '%.17g' % ts['settings']['lowShelfSlope']))
w('')
w('static const coeff_case COEFF_CASES[] = {')
for c in ts['coefficients']:
    w('    { %d,' % c['sampleRate'])
    w('      { %s },' % ', '.join('%.17g' % v for v in c['lowShelf']['b']))
    w('      { %s },' % ', '.join('%.17g' % v for v in c['lowShelf']['a']))
    w('      { %s },' % ', '.join('%.17g' % v for v in c['peaking']['b']))
    w('      { %s } },' % ', '.join('%.17g' % v for v in c['peaking']['a']))
w('};')
w('')
w('static const double TONE_INPUT[] = {')
for i in range(0, len(ts['waveform']['input']), 4):
    w('    ' + ', '.join('%.17g' % v for v in ts['waveform']['input'][i:i + 4]) + ',')
w('};')
w('')
w('static const double TONE_OUTPUT[] = {')
for i in range(0, len(ts['waveform']['output']), 4):
    w('    ' + ', '.join('%.17g' % v for v in ts['waveform']['output'][i:i + 4]) + ',')
w('};')
w('')

nc = read('voice_nchlt_phonemizer.json')
for key, name in (('dict', 'NCHLT_DICT'), ('rules', 'NCHLT_RULES'),
                  ('phoneMap', 'NCHLT_PHONE_MAP'), ('graphMap', 'NCHLT_GRAPH_MAP'),
                  ('gnulls', 'NCHLT_GNULLS')):
    w('static const char *const %s =' % name)
    parts = nc[key].split('\n')
    for k, line in enumerate(parts):
        tail = ' "\\n"' if k + 1 < len(parts) else ''
        w('    %s%s' % (cstr(line), tail))
    w('    ;')
    w('')

for i, c in enumerate(nc['cases']):
    if c['phones']:
        w('static const char *const NPH_%d[] = { %s };'
          % (i, ', '.join(cstr(x) for x in c['phones'])))
    if c['unknownGraphemes']:
        w('static const char *const NUN_%d[] = { %s };'
          % (i, ', '.join(cstr(x) for x in c['unknownGraphemes'])))
w('')
w('static const nchlt_case NCHLT_CASES[] = {')
for i, c in enumerate(nc['cases']):
    ph = ('NPH_%d' % i) if c['phones'] else 'NULL'
    un = ('NUN_%d' % i) if c['unknownGraphemes'] else 'NULL'
    w('    { %s, %s, %s, %d, %d, %s, %d },'
      % (cstr(c['name']), cstr(c['text']), ph, len(c['phones']),
         c['rulePredictedWords'], un, len(c['unknownGraphemes'])))
w('};')
w('')
for i, c in enumerate(nc['predictWord']):
    if c['phones']:
        w('static const char *const PRED_%d[] = { %s };'
          % (i, ', '.join(cstr(x) for x in c['phones'])))
w('')
w('static const predict_case PREDICT_CASES[] = {')
for i, c in enumerate(nc['predictWord']):
    ph = ('PRED_%d' % i) if c['phones'] else 'NULL'
    w('    { %s, %s, %d },' % (cstr(c['word']), ph, len(c['phones'])))
w('};')

with io.open(OUT, 'w', encoding='utf-8', newline='\n') as f:
    f.write('\n'.join(L) + '\n')
print('wrote %s (%d lines)' % (OUT, len(L)))
