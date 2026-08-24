# Voice provenance — 71 languages verified on the Huawei P30 Lite

Recorded so provenance stays visible, not because anything here is a problem.

**71 distinct languages.** The figure "74" appears in older notes and counts the
three pairs where two regional voices serve one language — Spanish (Spain and
Mexico), Portuguese (Brazil and Portugal), Dutch (Netherlands and Flemish
Belgium). Both numbers describe the same set of voices.

CircleAI is free AI for people on old hardware who cannot pay for anything else.
It is developed independently, earns no money, and is a core component of
nothing. CC-BY-NC permits exactly this use; it is the case that licence exists
for. The NC entries below are therefore a note on where each voice came from, not
a liability.

## We now host copies — 2026-07-31

Until this date CircleAI distributed no model: every voice was fetched at runtime
from whichever stranger's repository published it. That is no longer true, and
the change is deliberate.

All 66 voices now live in one bucket we control,
[thegeekco/circleai-voices](https://huggingface.co/thegeekco/circleai-voices)
(135 files, 6.91 GB, public). The catalogue points there and nowhere else.

Two reasons. First, a speaker of Bemba should not lose their voice because
someone else's repository went away. Second, the hours spent working out which
model, which ONNX input layout, and which front-end each language needs should
not have to be spent by the next person — one address is the whole point.

This makes us a redistributor, which the previous wording explicitly said we were
not. CC-BY-NC and CC-BY-SA both permit redistribution and both require
attribution, so the bucket carries a README crediting every upstream author and
naming the licence for each voice individually. The obligation is met by that
file; if it is ever removed, the redistribution is no longer compliant.

Six voices in the bucket exist in no other public place — the 11-language South
African model, plus Igbo, Lingala, Amharic, Tigrinya and Nepali. For those, the
bucket is not a mirror, it is the only copy.

(An earlier version of this file claimed NC "blocks calling CircleAI open
source". That was wrong. It reasoned as though CircleAI inherited the commercial
character of apps that call it — the same error as saying the Linux kernel makes
commercial use of itself because a shop runs on it. Nothing NC lives in this
repository; the repository is open source.)

## A second store we control — 2026-08-23

The bucket above needs a credential that exists on no machine here, and the cost
of that was measured rather than guessed: **45 of the small files the catalogue
named had quietly stopped existing.** Those languages downloaded a 114 MB model
and then failed on 2 KB of settings. A store nobody can write to cannot be kept
correct, and a voice proven on a Tuesday sat unpublished for weeks.

So voices may now also live in
[bhengubv/circleai-voices](https://github.com/bhengubv/circleai-voices) as
release assets, addressed `<tag>/<asset>` under `ModelSource.GitHubRelease`.
That is the account's canonical storage and we hold its token, so a voice ships
the day it is proven.

**The rule did not change — it was about CONTROL, not about a company.** What is
still forbidden is pointing the catalogue at whichever stranger's repository
first published a voice; that is the guarantee this document exists to give, and
`ModelModalityTests` enforces both halves: a voice must come from one of our two
stores, and a GitHub-hosted one must sit under `bhengubv/`.

That repository carries the same per-voice attribution README the bucket does.
The obligation is met by that file; if it is removed, the redistribution is no
longer compliant.

| voice | licence | upstream |
|---|---|---|
| ne_NP google medium (Nepali) | CC-BY-SA-4.0 | rhasspy/piper-voices, openSLR 43, 18 speakers |
| ko_KR kss medium (Korean) | CC-BY-NC-SA-4.0 | rhasspy/piper-voices, Korean Single Speaker corpus |
| ig_IB soro medium (Igbo) | CC-BY-NC-4.0 | Shinzmann/soro-tts-ibo, WaxalNLP — re-exported to ONNX by us |
| af_ZA google-nwu low (Afrikaans) | CC-BY-SA-4.0 | MycroftAI/mimic3-voices, openSLR 32 (Google/NWU) via sherpa-onnx |
| jsut_vits_prosody (Japanese) | CC-BY-4.0 | espnet/kan-bayashi_jsut_vits_prosody, JSUT corpus (U. Tokyo) — re-exported to ONNX by us |
| open_jtalk_dic_utf_8-1.11 (Japanese phonemiser) | BSD 3-clause | Open JTalk / NAIST, 2009 — dictionary only, no engine code redistributed |

Afrikaans is the last of the four. `Vits-11ZA` measured **at its own noise
floor** for it — cer 0.76 against a 0.76 floor, unmoved by every encoding, every
one of the eleven language ids, and both recognisers. The replacement,
`vits-mimic3-af_ZA-google-nwu_low`, measures **CER 0.48 against a 1.17 floor**:
`Goeie môre` comes back "Goeiemor", `Die son skyn vandag mooi` as "Disone schijn
van dach moui".

Two details that are not guesses. Mimic3 ships a TRAINING config, not a Piper
sidecar — `phoneme_to_id` is null and the vocabulary is in `tokens.txt` beside
it, 60 tokens opening `_ ^ $`. And its scales are **0.333/0.333**, not Piper's
0.667/0.8; the bundle's own config says so.

`Vits-11ZA` keeps its other ten languages and simply no longer claims `af`. It
is NOT ranked below the new voice, because `BestFor` with no language also sorts
on QualityRank — bumping the Afrikaans voice to 8 made it the global default TTS
ahead of English, which a test caught.

Igbo replaces `MMS-ibo`, which was not an MMS voice either. That bundle was
`multilingual-tts/VITS-OpenBible-Igbo`, and re-exporting it correctly — with its
own vocabulary and its duration noise finally wired to `scales` — proved the
CHECKPOINT is broken, not our pipeline: it answers "ndewo enyi m kedu ka i mere
taa" with fluent, well-formed Igbo that has nothing to do with the input, in 8
seconds for six words. The Hebrew letters in its character set, long suspected as
the fault, are upstream's own and were never the problem.

The replacement is honest rather than good: **CER 0.56 against its own 0.76 noise
floor.** It follows its input where the old one did not, and `enyi` survives
verbatim in a longer sentence, but it is far behind the European voices here. It
ships because wrong words spoken confidently are worse than a weak voice, and
because it is the best free Igbo voice that exists — Igbo is absent from MMS,
from Piper's catalogue, and from all 642 assets in sherpa-onnx's tts-models.

Japanese was the last language in the catalogue whose files existed nowhere. The
144 MB `JSUT-VITS` export and its 103 MB dictionary were built by hand months ago,
measured, and never published — the registry pinned SHAs for two things that
returned 404. Both are now in the release, byte-identical to what was measured.

**The tag is not a directory.** The first spelling put it in the bundle file name
— `voices-v1/sys.dic` — which builds a correct URL and then unpacks the dictionary
into a folder `OpenJTalkPhonemizer` does not search. Nothing fails: 103 MB
downloads, its SHA verifies, and Japanese silently has no phonemiser. Release
assets are FLAT, so the tag now rides on the repo as `owner/name@tag` and the file
name stays the on-disk layout. `GitHubReleaseLayoutTests` holds that line, and the
phonemiser additionally walks one level down for `sys.dic` so the next rename is
survivable rather than silent.

Japanese is also the one voice here that is neither grapheme-driven nor
espeak-driven. It needs a MORPHOLOGICAL ANALYSER: Japanese is written without
spaces and its pitch accent is not recoverable from the characters, so the text
goes through Open JTalk's dictionary to full-context labels, and the accent fields
in those become the bracket tokens the model was trained on. Stripping the
brackets still produces confident speech with flat, wrong prosody — which a
recogniser will happily transcribe, which is why the audit measures them.

Scoring it needed the same correction the Ethiopic voices did, for a different
reason. Japanese has no single correct spelling of a spoken sentence: the
recogniser heard `これはテスト文です` exactly right and wrote `これは手スト分です`
— /te/ as 手 rather than テ, /bun/ as 分 rather than 文. That is CER 0.22 for a
perfect reading. Scored as phonemes, through the analyser that is already open,
it is **0.00**; on longer sentences 0.01–0.12, with `unk=0`.

Nepali replaces `MMS-npi`, which was never an MMS voice: Meta's own list of 1077
TTS languages contains no Nepali, no Igbo and no Lingala. That bundle carried a
vocabulary with 19 Hebrew letters sorted into the middle of the Devanagari, so it
produced fluent speech that was not the input. Korean moves here from a
half-migrated entry that built a bucket URL for an upstream path and 404'd on
every file.

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
| en_US lessac (medium, high) | Blizzard 2013 Lessac dataset licence — see [licence URL](https://www.cstr.ed.ac.uk/projects/blizzard/2013/lessac_blizzard2013/license.html) | rhasspy/piper-voices — the two English voices that predate this work; bespoke research terms, not an SPDX identifier |

## Non-commercial licences — permitted for this use, credited in the bucket README
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
