# Voice provenance — 74 languages verified on the Huawei P30 Lite

Recorded so provenance stays visible, not because anything here is a problem.

CircleAI is free AI for people on old hardware who cannot pay for anything else.
It is developed independently, earns no money, is a core component of nothing,
and does not distribute any model — every voice is fetched at runtime from its
own source. CC-BY-NC permits exactly this use; it is the case that licence exists
for. The NC entries below are therefore a note on where each voice came from, not
a liability.

(An earlier version of this file claimed NC "blocks calling CircleAI open
source". That was wrong. It reasoned as though CircleAI inherited the commercial
character of apps that call it — the same error as saying the Linux kernel makes
commercial use of itself because a shop runs on it. Nothing NC lives in this
repository; the repository is open source.)

## Permissive — safe for an open-source CircleAI
| voice | licence | source |
|---|---|---|
| es_ES davefx, es_MX ald | CC0 / Unlicense | rhasspy/piper-voices |
| es_MX claude | Apache-2.0 | rhasspy/piper-voices |
| pt_BR faber, pt_PT tugao | CC0 | rhasspy/piper-voices |
| nl_NL alex, nl_BE nathalie | CC0 | rhasspy/piper-voices |
| ru_RU denis | CC0 | rhasspy/piper-voices |
| zh_CN chaowen | CC0 | rhasspy/piper-voices (unused — pinyin type) |
| ur_PK fasih | MIT | rhasspy/piper-voices |
| fr_FR siwis | CC-BY-4.0 | rhasspy/piper-voices |
| bn_BD google | CC-BY-SA-4.0 | rhasspy/piper-voices (openSLR 37) |
| zh (Mandarin) | **MIT** | csukuangfj/vits-melo-tts-zh_en (MyShell MeloTTS) |
| Kokoro (Hindi, Vietnamese, Japanese) | Apache-2.0 | onnx-community/Kokoro-82M-v1.0-ONNX |

## Non-commercial licences — permitted for this use, fetched at runtime
| voices | licence | source |
|---|---|---|
| 20 African continental (swh yor hau lug kik kin ewe lgg nyn aka bam bem fon ful nya orm run sag sna som) | CC-BY-NC-4.0 | MMS |
| 11 South African (afr eng nbl nso sot ssw tsn tso ven xho zul) | CC-BY-NC-4.0 | guymandude SA-11 |
| tgl, tha, mya, tpi | CC-BY-NC-4.0 | willwade/mms-tts-multilingual-models-onnx |
| jav, vie | CC-BY-NC-4.0 | willwade/mms-tts-multilingual-models-onnx |
| hi_IN pratham (in use) | CC-BY-NC-SA-4.0 | rhasspy/piper-voices — Kokoro is the permissive replacement, written not yet deployed |

## Unknown
| voice | note |
|---|---|
| id_ID news_tts | MODEL_CARD describes a different voice (Malayalam/arjun) — provenance unverified |
| yue (Cantonese) | csukuangfj/vits-cantonese-hf-xiaomaiiwn — no licence declared upstream |

## Permissive alternatives, if a future use ever needs them
- 14+ African languages have CC-BY-SA VITS builds published at
  `multilingual-tts/*-OpenBible-*` (Hausa, Swahili, Yoruba, Luganda, Kikuyu,
  Chichewa, Shona, Oromo, Ewe, Twi, Ndebele, Lingala, Igbo). They ship as .pth,
  so using them means exporting the published checkpoint to ONNX — the same step
  already done for Igbo, Lingala and Nepali. Register is liturgical.
- Hindi: Kokoro (Apache-2.0) replaces the NC pratham voice once the Kokoro
  Android path is wired.

## Late additions — the ones I twice said were impossible
| voice | licence | source | note |
|---|---|---|---|
| zh Mandarin | MIT | csukuangfj/vits-melo-tts-zh_en | 195,828-entry lexicon; tones as a parallel channel |
| yue Cantonese | undeclared upstream | csukuangfj/vits-cantonese-hf-xiaomaiiwn | 13,937-entry lexicon; tone inline in phonemes |
| jp Japanese | undeclared upstream | csukuangfj/vits-hf-zh-jp-zomehwh | 34,977-entry lexicon WITH pitch accent |
| ibo Igbo | CC-BY-SA-4.0 | multilingual-tts/VITS-OpenBible-Igbo | exported .pth to ONNX; liturgical register |

I called Mandarin, Cantonese and Japanese "front-end problems needing a G2P we
cannot ship" — pypinyin, jieba, MeCab are Python and Python does not run on the
phone. All three were wrong. The sherpa-onnx builds ship the mapping as a plain
lexicon.txt beside the model, and a lookup table runs anywhere. Japanese even
carries pitch accent: 日本 -> n i UP Q p o DOWN N.

Two known defects, recorded rather than left to be discovered:
- Japanese heteronyms: one lexicon serves zh AND jp, so a single character in
  both can take the Chinese reading (私 -> s r, not w a t a sh i). Multi-character
  words dominate real text and longest-match handles them. new_heteronym.fst
  exists upstream for this.
- The Japanese speakers are Umamusume anime voices — Japanese actors, but
  stylised and high-pitched. Correct language, wrong register for an assistant.
  804 speakers are available; changing it is one number.

## Wave two — 19 more, verified on the P30
| voice | licence | source |
|---|---|---|
| Arabic (MSA), Persian/Dari, Sundanese, Telugu, Marathi, Tamil, Gujarati, Kannada, Malayalam, Punjabi, Malagasy, Kanuri, Guarani, Moore, Haitian Creole, Quechua | CC-BY-NC-4.0 | willwade/mms-tts-multilingual-models-onnx |
| Sinhala | **MIT** | chan4lk/piper-tts-sinhala |
| Lingala, Nepali | **CC-BY-SA-4.0** | multilingual-tts OpenBible, exported .pth -> ONNX |

Known limitations, recorded rather than left to be found:
- Arabic is MSA only. No dialect model exists that is both permissively licensed
  and phone-sized: the Egyptian candidate (mohammedaly22/VoiceTut-TTS, Apache-2.0)
  is 2.3 GB of OmniVoice weights, and kimbolingo/arabic-piper-tts is the right
  format and size but declares no licence at all. MSA is the written register;
  comprehension of it tracks schooling, which is backwards for this project's
  users. No Egyptian dialect model exists that is both licensed and phone-sized,
  so Arabic stays MSA-only until one is published.
- Guarani approximates g-tilde; Kanuri's vocabulary lacks 'z'.

## Blocked
- ~~Amharic, Tigrinya~~ — SOLVED. GeezRomanizer transliterates Ethiopic to Latin
  before the grapheme path. Amharic 3.2s -> 11.5s, Tigrinya 2.1s -> 6.7s, and all
  43 previously-lost characters now reach the model. Both CC-BY-NC (MMS), exported
  from facebook/mms-tts-amh and -tir.
- **Pashto, Wolof, Tshiluba, Kikongo** — no model anywhere. Not in Meta's full
  1,140-model MMS set, not in OpenBible, nothing phone-sized and licensed on
  HuggingFace. Wolof exists (galsenai/wolof-tts) but is 1.67 GB of Parler-TTS
  weights with no declared licence. Nothing usable exists for these four yet.

## How voices get here
CircleAI **hunts and reuses**. Every voice above is someone else's published
model, sourced and made to run on the phone. Where a checkpoint ships as .pth we
export it to ONNX; where a script needs transliteration or a lexicon we write
that in C#. We do not train or fine-tune models — when nothing usable is
published for a language, it is simply not available yet, and it stays on the
list until someone publishes one.
