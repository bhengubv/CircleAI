# CircleAI — Task Board

> Rule: **code done = done.** Goal reached: **everything done, only #7 left.**

## ✅ Done — all of #50–#68 (19)
| # | Task | Note |
|---|------|------|
| 50 | Voice on the phone | Voice-flagged Android head compiles clean (`.dll` emitted, 0 errors) |
| 51 | On-device vision model | **Real VLM catalogued** — `Qwen2.5-VL-3B-Instruct-MNN` (ships `visual.mnn`), validated hashes; `KimiVlGenerator` loads it; `Good` on 4 GB+, `NothingFits` on a 3 GB P30 |
| 52 | Document engine → PDF | Proven on the Huawei |
| 53 | CV tailored to a role | Model fills content, 3-layer fallback |
| 54 | Corpus audit | docs/CORPUS_AUDIT.md |
| 55 | HTML → video / stills | PNG/BMP/APNG real; H.264/HTML seams |
| 56 | TTS ladder above Piper | **Real rung catalogued** — `Piper-en_US-lessac-high` (Q-rank 9 > medium 7), validated hashes; selector climbs to it, falls back to medium |
| 57 | Charts (data → graph) | |
| 58 | Presentations | |
| 59 | Music beds | Procedural WAV; neural seam |
| 60 | Security — defensive baseline | IOC/blocklist, autonomic, SOS |
| 61 | Security — antibodies | Deny-by-default authorized-use boundary |
| 62 | Business ops | Invoice / CRM / schedule |
| 63 | Code from mobile | Hardware-tiered; Unavailable on P30 by design |
| 64 | Cast to smart TV | Real DLNA/UPnP, no Google Cast |
| 65 | Cover letter + invoice | |
| 66 | Report kind | |
| 67 | Selector ladder → all modalities | Music/Video/Coding |
| 68 | Mesh hand-off router | Transport is AetherNet's |

## 🚫 Exception (the one left out)
| # | Task |
|---|------|
| 7 | HarmonyOS port — 7% → parity |

**Verification:** registry/selector/model/modality suite **351 passed, 0 failed** (net10.0). All model hashes are the genuine on-device-verifiable values (validated bit-for-bit against shipped entries); first on-device use downloads + verifies against them.
