# Voice provenance — 51 languages verified on the Huawei P30 Lite

Recorded because a licence decision made once, in a hurry, becomes invisible
later. Every NC entry is a deliberate call under the "run with what we can get"
agreement, not an oversight.

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

## Non-commercial — blocks "CircleAI remains open source"
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

## Permissive replacements available, not yet taken
- 14+ African languages have CC-BY-SA VITS builds at `multilingual-tts/*-OpenBible-*`
  (Hausa, Swahili, Yoruba, Luganda, Kikuyu, Chichewa, Shona, Oromo, Ewe, Twi,
  Ndebele, Lingala, Igbo). They ship as .pth and need ONNX export; register is
  liturgical (Bible readings).
- Hindi: Kokoro (Apache-2.0) replaces the NC pratham voice once the Kokoro
  Android path is wired.
