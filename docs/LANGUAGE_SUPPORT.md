# CircleAI voice — African-language TTS support

Honest and evidence-tiered. A language **counts** only when it is (1) **permissive**
(licence read — the one pre-filter) AND (2) **proven on the P30 Lite** (the phone
actually spoke it). Everything else is labelled by exactly how far it has been taken.
Voices are **runtime-sourced by catalogue** (content-hash / AetherNet swarm), so a
device pulls only the languages its user asks for — nothing is bundled.

## ✅ Proven on the P30 (the phone spoke it, WAV pulled)

| Language | Voice | Format | Licence | Evidence |
|---|---|---|---|---|
| **English** (en) | Piper `en_US-lessac-high` | Piper `.onnx` + out-of-process espeak G2P | MIT model; espeak GPL isolated in a separate app | real 2.6 s WAV, 22 050 Hz, 71% peak |
| **Twi / Akan** (tw) | kasanoma (michsethowusu) | Piper `.onnx` + out-of-process espeak (`lfn` proxy) | open-source (to firm with author) | real **2.56 s WAV, 16 kHz, 52% peak, 9 s on-device** (2026-07-25) |
| **Chichewa** (ny) | kasanoma (michsethowusu) | Piper `.onnx` + out-of-process espeak (`lfn` proxy) | open-source (to firm with author) | real **2.59 s WAV, 16 kHz, 74% peak, 8 s on-device** (2026-07-25) |

## 🟡 Ready — permissive, phone-format, staged for the P30 now

**kasanoma** (github.com/michsethowusu/kasanoma) ships **Piper `.onnx`** voices — the
*identical* format to the proven English one, so our OnnxTtsEngine + out-of-process
espeak run them with **zero new engine code** (each carries its own espeak G2P proxy):

| Language | espeak proxy | Size | Status |
|---|---|---|---|
| **Twi / Akan** | `lfn` | 58 MB | downloaded + hash verified; **P30 test in flight** |
| **Chichewa** | (per config) | 58 MB | on GitHub releases, next |
| **Makhuwa** | (per config) | 58 MB | on GitHub releases, next |

Licence: author states "open-source" but ships no explicit file — **to firm with the author.**

## 🔵 Permissive + on-device, different runtime (NeuTTS-Nano GGUF)

**`AfriSpeech/afri10-tts-local`** — **10 African languages** as GGUF (needs a NeuTTS /
llama.cpp-style loader, not our ONNX engine): Kabiye, Twi, Bassa (Cameroon), Mauritian
Creole, Nyaneka, Gun, Swahili (+3). Licence: *NeuTTS Open License 1.0* — commercial terms to confirm.

## 🟢 Reachable via espeak + any Piper voice (espeak natively covers the language)

Afrikaans (af), Swahili (sw), Amharic (am), Setswana (tn), Oromo (om), Arabic (ar).

## 🔴 Blocked by licence — do NOT use

**MMS** (`facebook/mms-tts-*`) covers **all 11 SA official languages + ~1,100 more**, small
VITS, phone-runnable — but **CC-BY-NC**. It would have completed SA coverage in a day; the
licence is the only wall, and it's a hard one for a product in a revenue ecosystem.

## ⚪ Leads still being hunted (rule nothing out until tested on the P30)

- **BibleTTS** (CC-BY-SA): Yoruba, Hausa, Ewe, Lingala, Kikuyu, Chichewa, Twi.
- **CMU African Voices**: ~30 African languages.
- michsethowusu: `akiti-tts`, `fast-ghana-voice`, `nano-twi` — more offline ONNX voices.
- `NWU-MuST/za_lex`: SA pronunciation lexicons — a G2P resource aimed at the Nguni/Sotho gap.

## The honest gap

The **SA Nguni/Sotho languages — isiZulu, isiXhosa, Sepedi, Sesotho, Setswana, Tshivenda,
Xitsonga, siSwati, isiNdebele** — have **no confirmed permissive, phone-runnable voice yet**
(kasanoma covers Twi/Chichewa/Makhuwa, not these; MMS covers them but is NC). Closing this
is the priority — through the leads above or a permissive source not yet found. **We do not
claim these languages until the P30 speaks them.**

*Living document — grows as each candidate is proven on the phone.*
