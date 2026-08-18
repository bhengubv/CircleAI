# Voice session state — 2026-08-13

Written to stop re-deriving settled facts. Everything below is measured on the
P30 Lite (`UTKDU19919000815`) unless marked otherwise.

## Decisions (settled, do not reopen)

| | |
|---|---|
| English voice | **Piper-en_US-lessac-medium** — espeak G2P, 22 kHz, WER **0.00** |
| SA languages | **Vits-11ZA**, speaker **128** (was 129) |
| Tone | `ToneShaper.Warm` — low shelf +4 dB @320 Hz, dip −4 dB @3.2 kHz |
| Voice residency | **ONE at a time.** Both resident → OEM low-memory killer → no answer at all |

Baseline that anchors every WER here: commercial TTS scores 0.07 on the same
whisper-tiny. 0.17 (Vits on English) is bad; 0.00 (lessac) is better than
commercial. Vits-11ZA on English is grapheme-driven with no pronunciation
model — structurally wrong, not tunable.

## Shipped in code (built, on device)

- Transcription 6 834 → ~1 300 ms (encoder window sized to audio, processor reused)
- Whisper/brain/TTS model loads moved off the turn (`PrepareAsync`, `ItSessionHost`)
- First clause may break at a comma → first sound sooner
- int8 SA voice (static QDQ, 122 → 32 MB, 2.6×) — catalogued as `Vits-11ZA-int8`
- Per-language routing in `ItSpeaker` (`Family`, `FamilyFor`, `TryLoadEnglishAsync`)
- Both voices named in `FirstRun.Plan` from `ItSpeaker`'s own constants
- Instrumentation: `stt:`, `tts:`, `spoke:`, `TURN:`, `CIRCLEAI-KV`, `CIRCLEAI-SYSTEM`,
  `CIRCLEAI-ENRICH`, `sideload:`, `voice   :`, `speaking:`

## MMS voices — TWO ARE BROKEN AS WIRED (measured, new)

Coverage of each language's own script against its own vocab:

```
  Igbo      100.0%     Lingala   100.0%     Nepali   95.3% (missing danda ।)
  Amharic    20.0%  <- drops 20 of 25 characters
  Tigrinya   20.0%  <- drops 20 of 25 characters
```

CAUSE: their vocabs are plain Latin.
```
  amh: 29 symbols ->  '_abcdefghijklmnopqrstuwxyz
  tir: 28 symbols ->  -abcdefghijklmnopqrstuwxyz
```
MMS romanizes non-Latin scripts with **uroman** before phonemizing. Nothing in
this app does that, so Ethiopic input is 80% silently dropped — shorter audio, no
error, every acoustic metric still passes. THIRD instance today of "right model,
wrong alphabet" (Vits-11ZA/espeak, Kokoro/espeak, now MMS/uroman).

Fix is uroman before the phoneme map for any MMS voice whose vocab is Latin-only.
Detect it: if the vocab has no character of the text's script, romanize first.

(Igbo's vocab also contains Hebrew letters — contamination in the published
config, harmless because Igbo text maps via the Latin subset, but it means these
configs were not reviewed.)

## SA language coverage (measured)

9 of 11 at 100%. Sepedi `š` and Tshivenda `ḓ ṱ` are absent from the vocabulary
and get folded to `s`/`d t` — audible, not the true sound. Needs a native ear.

## OPEN — in priority order

1. **Setup appears to stall after picking a language: taps do nothing.**
   Reported by the user, reproduced in the log — no `turn:`/`stt:` line existed
   anywhere in the fresh-install run.

   PROVEN, after the fact: the circle itself is fine. One tap on the restored
   install gave `turn: mic=0 ms | listened=6130 ms | 0,0 s of audio`.

   The tap path has three earlier exits (`HomeActivity` ~line 808):
   ```
   if (_setup is not null) return;                      // silent, by design
   if (_ready.Stage == NeedsSetup) { StartSetup(); return; }
   if (!_ready.CanTalk) { SpeakNext(); return; }         // speaks a tour line
   TalkOnce();
   ```
   THREE HYPOTHESES TRIED AND EACH DISPROVED BY READING FURTHER — do not repeat
   them: (a) `StopHandsFreeAsync` hangs — no, a turn runs fine; (b) the
   permission caption never resets — no, `OnRequestPermissionsResult` routes 1003
   to `CheckReadyAsync`; (c) setup never re-checks readiness — no, line 760 is
   `await CheckReadyAsync()`.

   WHAT IS STILL UNKNOWN: which of the three exits the user's taps actually took.
   The `tts: 12/18 chars` pairs at 05:04:30 and 05:08:43 are either the setup
   tour or `SpeakNext()`, and the log cannot tell them apart.

   NEXT STEP IS A MEASUREMENT, NOT A READ: add one log line to each of the three
   early exits, reproduce, and the log will say which. Guessing has cost three
   rounds already.
2. Wake bundle re-initialises 4× per run; KWS came up `+ 0 effect(s)` where
   working runs show `+ 2 effect(s)`. Unexplained, possibly unrelated.
3. **`CircleNeuronService` notification shows a developer string** to the user:
   "no brain configured — set CircleNeuronService.OptionsFactory before Start".
   The APP's brain is fine (`brain warm in 13519 ms`) — this is the resident
   background service, a separate thing, and it is user-visible.
4. **Home screen claims ready regardless of setup state** — leaving the setup
   screen and returning shows "Say Hey B" whether or not setup finished.
5. **Turn-1 prefill still ~2.5–3.5 s.** `UsePrefixCache` is on but libMNN's
   `setPrefixCacheFile` returns false, and warm-up/turn system prompts differ
   (`af04d19a` vs `da697a1c`) because query-dependent skill context sits in the
   system message.
6. **int8 SA voice must be re-pushed** — the wipe took it; it is the one file not
   downloadable (unpublished). Source: scratchpad `vits-11za-int8.onnx`,
   sha256 `2bcc377d2fc080d869c5a5472a0d263dbc04b56a0475a46612a3cc546060e871`.
7. **Japanese / Korean / Chinese — voices are AVAILABLE. Fetch, catalogue, route.**

   All three exist as ready-made ONNX. "No Japanese voice exists" was wrong: it
   was true of Piper's `voices.json` (53 locales, no `ja_*`) and of
   `facebook/mms-tts-*` (has `kor`, no `jpn`, no `cmn`) — two catalogues, not the
   world. Kokoro covers it, and `KokoroTtsEngine.cs` already ships in
   CircleAI.Voice.

   | lang | source | file | size |
   |---|---|---|---|
   | ja | `onnx-community/Kokoro-82M-v1.0-ONNX` | `onnx/model_quantized.onnx` + `voices/jf_alpha.bin` | 92 MB + 522 KB |
   | zh | same, or Piper `zh_CN-huayan-medium` | `voices/zf_xiaobei.bin` / `zh/zh_CN/huayan/medium/*.onnx` | 522 KB / 63 MB |
   | ko | Piper `ko_KR-kss-medium` | `ko/ko_KR/kss/medium/ko_KR-kss-medium.onnx` | 63 MB |

   Kokoro is Apache-2.0; Piper voices MIT. One Kokoro model serves ja AND zh —
   the .bin voice files are speaker embeddings, not separate models.

   **KOREAN IS DONE.** `Piper-ko_KR-kss-medium` downloaded, hashed and catalogued
   in BOTH registries. Measured with the harness: WER **0.00** on take 3
   (`안녕하세요 프랑스의 수도는 파리입니다` verbatim), mean 0.40 over 5 — the
   spread is whisper-tiny's Korean and a 4-word reference where each slip costs
   25%, not the voice. Files in scratchpad `cjk/`.

   **ALL THREE NOW SPEAK. Measured, desktop:**
   ```
                    espeak      misaki      transcript (misaki)
     Japanese       CER 3.06    CER 0.12    こんにちはフランスの人はパリです
     Chinese        CER 0.80    CER 0.40    鸣豪法國的首都市巴黎
     Korean              —      WER 0.00    안녕하세요 프랑스의 수도는 파리입니다
   ```
   `dropped=none` for both Kokoro cases — every misaki phoneme is in the
   115-symbol vocab. Residual error is whisper-tiny homophones (是→市, 首都→人),
   not synthesis. Working scripts: `kokoro_misaki.py`, `ko_test.py`.

   MISAKI SETUP THAT COST TIME: `pip install misaki[ja,zh]` is not enough. It
   pulls the FULL `unidic` package, whose dictionary is a separate ~1 GB download
   that never happens, and it SHADOWS `unidic-lite`. MeCab then fails with
   "no such file or directory: unidic/dicdir/mecabrc". Fix:
   `pip install unidic-lite && pip uninstall -y unidic`.

   **STILL TO DO for ja/zh: catalogue + Android G2P.** Kokoro synthesises both
   (9.85 s and 3.80 s of clean audio, sane levels). espeak is the wrong front end
   for logographic script — on 首都 it SWITCHES TO ENGLISH and pronounces the word
   "chinese" aloud:
   ```
   (en)tʃˈaɪniːz(ja)      <- literally in the IPA espeak returned
   ```
   and its Mandarin IPA carries numeric tone marks (`niɜχˈɑu2`) absent from
   Kokoro's 115-symbol vocab. Kokoro expects **misaki** phonemes — `misaki[ja]`
   (fugashi/unidic) and `misaki[zh]` (pypinyin). Same class of bug as feeding
   Vits-11ZA espeak IPA when it wanted characters: right model, wrong alphabet.
   `pip install misaki[ja,zh]` was started and had not finished.

   ON ANDROID this needs an out-of-process G2P like the espeak app, since misaki
   is Python. That is the real design question for ja/zh, not the voice.

   STEPS (same as Vits-11ZA-int8 and lessac, both done today):
   fetch → sha256 → entry in `registry.json` AND `Models/embedded_registry.json`
   → route in `ItSpeaker` → measure WER with the harness in
   scratchpad `rank.py` / `english.py`.

   ALSO REQUIRED, and not fixed by voices: recognition. whisper-tiny called
   Korean "Thai" and returned empty for Japanese, and `LanguageGuess.Detect` only
   separates Nguni/Sotho. Route on the TRANSCRIPT by Unicode script — Hangul,
   Kana, Han are unambiguous — not on whisper's audio language id.

## Not to repeat

- espeak G2P runs out-of-process (GPL). It gets killed under memory pressure.
- Vits-11ZA map has `<PAD>` only — **no `_`/`^`/`$`**, so the app sends plain
  character ids, no padding, no BOS/EOS. Encoding it any other way yields
  gibberish that still measures fine.
- VITS samples noise per run: never rank voices on a single take.
- Build the **Android** csproj after editing shared sample code, or the phone
  runs the old plan.
