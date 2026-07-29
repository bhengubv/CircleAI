# CircleAI voice — language coverage (verified, source-by-source)

The definitive record of what CircleAI can voice, and from where. Earned by an
exhaustive, verified search — Western hubs, Asia (ModelScope), African labs,
GitHub **topic tags**, Zenodo/Zindi/Kaggle/GitLab, and the primary academic
sources — searching plainly, not through a HuggingFace-shaped keyhole.

Two honest bars: **✅ voice** = a permissive voice model exists and drops into the
pipeline; **🔧 build-ready** = no finished voice has been released openly, but
*every free ingredient to make one is in hand* (phonemizer + data + engine).

## The engine ladder (why format is no longer a wall)

CircleAI's TTS is multi-engine, not Piper-only:

1. **espeak-ng** (out-of-process, GPL-isolated) — rules floor, ~100 languages.
2. **VITS family via sherpa-onnx** (Apache) — Piper, mimic3, coqui, Kokoro, Matcha
   all run here. One runtime, many `.onnx` voice formats.
3. **Neural-codec / cloning** (VoxCPM, NeuTTS, Qwen3-TTS — all Apache) — the
   personalisation rung, device-gated.

`SpeechModelSelector` now selects **by language** (`BestFor(probe, Tts, "zu")`) —
the code seam that lets any voice below be served for the tongue asked for.

## 🇿🇦 South Africa — full coverage (all 11 official languages)

Three have a ready permissive `.onnx` voice. The other eight are build-ready:
their finished acoustic voice hasn't been released openly by anyone (verified via
the exact `isizulu` / `isixhosa` GitHub topic tags — the most direct check there
is), but the hard, language-specific parts are all free and gathered.

| Language | Status | Source(s) gathered |
|---|---|---|
| **English** | ✅ voice (proven on P30) | Piper `en_US-lessac` (MIT) — stocked in registry |
| **Afrikaans** | ✅ permissive `.onnx` voice | mimic3 `af_ZA` (CC-BY-SA) · espeak `af` · phonemeza G2P |
| **Setswana** | ✅ permissive `.onnx` voice | mimic3 `tn_ZA` (CC-BY-SA) · espeak `tn` |
| **isiXhosa** | 🔧 build-ready *(best data)* | **phonemeza** G2P · **OpenSLR-32** (CC-BY-SA, TTS-grade) · NCHLT (CC-BY) · coqui/Piper |
| **Sesotho** | 🔧 build-ready | **OpenSLR-32** (CC-BY-SA, TTS-grade) · NCHLT · coqui |
| **isiZulu** | 🔧 build-ready | **phonemeza** G2P (98.7% acc.) · NCHLT (CC-BY) · coqui |
| **Sepedi** (N. Sotho) | 🔧 build-ready | NCHLT (CC-BY) · coqui |
| **Xitsonga** | 🔧 build-ready | NCHLT (CC-BY) · coqui |
| **Tshivenda** | 🔧 build-ready | NCHLT (CC-BY) · coqui |
| **siSwati** | 🔧 build-ready | NCHLT (CC-BY) · coqui |
| **isiNdebele** | 🔧 build-ready | NCHLT (CC-BY) · coqui |

**Cross-cutting SA pieces (the finds that make the eight buildable):**
- **`CubicMonk19/phonemeza`** — free grapheme-to-phoneme for **isiZulu (98.7%),
  isiXhosa, Afrikaans** → X-SAMPA. The language-specific front-end (clicks, tone,
  agglutination) that breaks generic engines. Trained `.pt` bundles; built on
  **NCHLT-inlang** pronunciation dictionaries (CC-BY-3.0).
- **NCHLT Speech** (SADiLaR / CTexT, CC-BY-3.0) — read speech for **all 11**.
- **OpenSLR-32** (Google/NWU, CC-BY-SA) — TTS-grade, single-speaker: af, **st, tn, xh**.
- **espeak-ng** — the floor (native af, tn; proxy elsewhere).

**The path for the eight — build our own (the only sovereign option):**
Train CC-BY-SA voices from the gathered free pieces (phonemeza + NCHLT/OpenSLR-32 +
coqui/Piper), export ONNX, serve on sherpa-onnx. **Owned outright, permanent, no
external kill switch** — and free: free data, free tools, free GPU (Colab). It is
the only path that survives someone else changing their mind.

**Rejected as load-bearing dependencies** (recorded, not used):
- **`guymandude/South-African-TTS-11-Vits`** — a finished all-11 VITS exists but is
  access-gated + unlicensed. Building SA on it is a single point of failure held by
  one person: licence pulled or repo deleted → SA coverage dies overnight. Not ours
  → not usable.
- **Qfrency / CSIR** — finished commercial voices, but licence-on-request: a cost we
  don't have and a switch they control.

## Beyond South Africa (the reach)

- **Ready `.onnx` now** (sherpa-onnx bundle, permissive): ~46 languages — European
  + English/Chinese, plus **Swahili**, **Hausa** (mimic3), **Twi** & **Chichewa**
  (kasanoma, proven on P30).
- **Convert + stock** (CC-BY-SA VITS): **OpenBible** — Yoruba, Igbo, Shona, Lingala,
  Oromo, Luganda, Twi.
- **India** (permissive): **AI4Bharat / IndicF5** (MIT, 11) · **Indic Parler-TTS**
  (Apache, 21) · Bhashini.
- **Kinyarwanda** — DigitalUmuganda (CC-BY-SA) · **Somali** — Kokoro fine-tune.
- **Cloning / personalisation** (Apache): VoxCPM (~30 langs), NeuTTS Air (on-device),
  Qwen3-TTS (GGUF).
- **Coverage ceiling reference** (NOT usable — CC-BY-NC): MMS, 1,143 languages.

## Corrections banked (so they don't recur)

- MMS does **not** cover the SA-Nguni/Sotho (only Xitsonga of the 11), and it's NC.
- OpenBible/EveryVoice (41 models) does **not** include isiZulu or isiXhosa — its
  "Ndebele" is the Zimbabwean cousin (`nd`), not SA `nbl`.
- Swivuriso (3,000 h, 7 SA languages, CC-BY-4.0) **prohibits TTS/voice-synthesis use.**
- Search method: plain nouns, every platform, GitHub topic-tags — never a
  jargon-loaded query that pre-deletes the results. See
  [[feedback_search_open_web_first]].

*Living record — a 🔧 becomes ✅ the moment that language's voice is stood up and
the P30 speaks it.*
