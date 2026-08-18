# open-jtalk — Japanese G2P on the device

Turns Japanese text into phonemes on the phone. Built because Japanese cannot be
phonemised from a lookup table: 聞き取れて is read by segmenting the sentence,
identifying 聞く as a verb and applying its conjugation. That is morphology, and
it needs a dictionary and an analyser.

Licence: **modified BSD** (Open JTalk, hts_engine, MeCab's BSD option, NAIST
dictionary). Unlike espeak-ng's GPL-3.0 this links directly into the app, so it
needs no second package to stay licence-clean.

## What is here

| path | what |
|---|---|
| `upstream/` | vendored [r9y9/open_jtalk](https://github.com/r9y9/open_jtalk) branch `1.11` — the CMake fork pyopenjtalk uses. Unmodified; changes belong in a `.patch` beside this file. |
| `openjtalk_g2p.c` | our wrapper: `open` / `g2p` / `labels` / `close` |
| `CMakeLists.txt` | wraps `upstream/src` and links one `libopenjtalk_g2p.so` |
| `ojt_test.c` | on-device check — a desktop build proves nothing about bionic mapping a 100 MB dictionary |
| `dic/` | compiled NAIST dictionary, **not** committed — see below |

Only the analysis chain is built (`text2mecab → mecab → njd_* → jpcommon`).
`hts_engine` is deliberately absent: the acoustic half is a VITS ONNX graph, so
all we want from Open JTalk is readings.

## Build

```
cmake -B build/arm64-v8a -S . -G Ninja ^
  -DCMAKE_MAKE_PROGRAM=<ninja> ^
  -DCMAKE_TOOLCHAIN_FILE=%NDK%/build/cmake/android.toolchain.cmake ^
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-24 ^
  -DANDROID_STL=c++_static -DCMAKE_BUILD_TYPE=Release ^
  -DCMAKE_POLICY_VERSION_MINIMUM=3.5
cmake --build build/arm64-v8a
```

Built with NDK 28.0.13004108, clang 19. Output: **arm64-v8a 10.7 MB,
armeabi-v7a 8.2 MB**.

### Four things that cost time

1. **`-DCMAKE_POLICY_VERSION_MINIMUM=3.5` is required on CMake 4.x.** Upstream
   declares `cmake_minimum_required(VERSION 2.8.12...3.31)` and CMake 4 removed
   compatibility below 3.5. The version range does not rescue it.
2. **Quote that flag in PowerShell.** Unquoted, `3.5` is coerced to a double and
   rendered with the machine's locale — on this machine `3,5` — which CMake
   truncates to `3` and rejects as invalid. The error names the value, not the
   cause.
3. **The Open JTalk headers are order-dependent — do not sort them.**
   `mecab2njd.h` declares `mecab2njd(NJD *, …)` without including `njd.h`, and
   `njd2jpcommon.h` names `JPCommon` and `NJD` without including either.
   Alphabetical order fails with `unknown type name 'NJD'`.
4. **Build ABIs serially.** Upstream's `configure_file()` writes the generated
   `mecab/src/config.h` into the *source* tree, not the build directory, so two
   ABIs configuring at once race on one file and the loser links against the
   other's assumptions — visible only as a wrong reading on one architecture.

`iconv` is correctly *not* found on Android; `MECAB_UTF8_USE_ONLY` compiles that
path out.

## The dictionary

`open_jtalk_dic_utf_8-1.11` — 22.6 MB compressed, **104 MB unpacked**
(`sys.dic` alone is 100.6 MB). Too big for the APK: ship it through the model
registry as a downloadable bundle.

Get it from the **GitHub release**, not SourceForge:

```
https://github.com/r9y9/open_jtalk/releases/download/v1.11.1/open_jtalk_dic_utf_8-1.11.tar.gz
```

Both SourceForge URLs return **HTTP 200 with 136 KB of HTML** instead of the
archive. A status check passes; only a size check catches it.

## Verified on device

Huawei P30 Lite (Kirin 710, arm64), `adb shell` against the real dictionary:

```
これはテスト文ですこの機械が日本語をちゃんと聞き取れているかどうかを計ります
  k o r e w a t e s U t o b u N d e s U k o n o k i k a i g a
  n i h o N g o o ch a N t o k I k i t o r e t e i r u k a d o o k a
  o h a k a r i m a s U                                      (74 phonemes)

1234円です
  s e N n i hy a k u s a N j u u y o e N d e s U             (23 phonemes)
```

Correct on the details that matter: は → `wa` as a topic particle, 文 → `b u N`
(not the 分 homophone), を → `o`, long vowel in `d o o k a`, devoiced vowels as
capital `I`/`U`, moraic n as `N`, `。` as `pau`, and 1234円 read 千二百三十四円
with 四 as `yo` rather than `yon` before the counter. **れ is present** — the
character the previous lexicon silently dropped.

## The matching voice: JSUT VITS

`jsut-vits/` holds `espnet/kan-bayashi_jsut_vits_prosody` — 355 MB PyTorch
checkpoint plus `config.yaml`. **No ONNX export of any Japanese VITS exists**
(HuggingFace returns nothing for `japanese+vits+onnx`, `openjtalk+onnx`, or
`jsut+onnx`; sherpa-onnx ships 0 Japanese voices across 642 TTS assets), so this
has to be exported rather than downloaded.

It pairs with this library exactly, which is why it was chosen: its config says
`g2p: pyopenjtalk_prosody`, and that G2P derives its tokens from Open JTalk
**full-context labels** — the output of `openjtalk_labels()`. No adapter needed.

**Token vocabulary — 47 entries, index order matters:**

```
<blank> <unk> a o i [ # u ] e k n t r s N m _ sh d g ^ $ w cl h y b j
ts ch z p f ky ry gy hy ny by my py v dy ? ty <sos/eos>
```

Phonemes are Open JTalk's. The other seven are prosody:

| sym | meaning | from the label |
|---|---|---|
| `^` | utterance start | first label |
| `$` | end, declarative | last label |
| `?` | end, interrogative | last label |
| `_` | pause | `pau` phoneme |
| `#` | accent-phrase boundary | `F1` / `F2` change |
| `[` | pitch rise | accent position rising |
| `]` | pitch fall | accent position falling |

Two gotchas for the C# tokeniser:

- **Devoiced vowels must be lowercased.** Our G2P emits `I`/`U` (see the `k I k i`
  above) but the vocabulary has only `a o i u e`. `pyopenjtalk_prosody` lowercases
  them; not doing so sends every devoiced vowel to `<unk>`.
- **`cl`** is the geminate consonant (っ) and is a real token — not a marker to skip.

### Exported and measured

`jsut_vits_prosody.onnx` — **137.6 MB**, opset 17, exported by
`export_jsut.py`. Inputs `text[1,T] text_lengths[1] noise_scale noise_scale_dur
alpha`, output `wav[1,S]` at 22 050 Hz.

Scored by `validate_jsut.py`, which runs the real chain (Open JTalk labels →
prosody tokens → ONNX) and transcribes the result with the same whisper the
product listens with:

```
                                  VITS-jp (old)   JSUT VITS
  ReazonSpeech, clean read             0.42          0.11
  ReazonSpeech, conversational          —            0.00
  self-authored toy sentence           0.12          0.18
```

`unk=0` on all three — every symbol maps. The residual errors at 0.11 are
homophones only (文→分, 機械→機会, 計ります→測ります): identical pronunciation, so
that is whisper choosing spellings, not the voice mispronouncing. The toy
sentence regressing 0.12→0.18 is the one the old zh-dominant model happened to
fit, and is the least meaningful of the three.

Notes for anyone re-running the export:

- `pip install espnet` does **not** pull `torchaudio`, but espnet2's beamformer
  code imports it on the way to `tts_inference`. Install it or nothing loads.
- torch 2.12's `torch.onnx.export` defaults to the dynamo path, which needs
  `onnxscript`. `dynamo=False` uses the legacy tracer and is sufficient — the
  graph is static.
- The generator returns `(1, T)`. Marking axis 0 dynamic instead of axis 1 bakes
  the probe's length into the graph and yields a model that only speaks one
  duration.
- `espnet_onnx`'s route additionally wants `espnet_model_zoo`; the manual trace
  does not, which is why it is the fallback that actually ran.

### Still to do

Wire `OpenJTalkProsodyTokeniser` + this ONNX into `OnnxTtsEngine` behind the
`VITS-jp` id, ship `libopenjtalk_g2p.so` in the APK, register the dictionary as
a downloadable bundle, and re-measure **on the P30** — every number above is
desktop.

## Not done yet

The phoneme *set* is Open JTalk's. Our current `VITS-jp`
(`vits-hf-zh-jp-zomehwh`) has a 51-token vocabulary from a Chinese-dominant
model, so these phonemes do not map onto its token ids. **A voice trained on
Open JTalk phonemes is still needed** — ESPnet's JSUT/JVS VITS models are
phonemised with pyopenjtalk and are the natural pairing. Until that lands this
library is a correct front end with no matching mouth.
