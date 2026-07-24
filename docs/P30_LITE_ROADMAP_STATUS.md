# Roadmap × Huawei P30 Lite — 67-item on-device status

Per-item result for the reference device: **Huawei P30 Lite** (MAR-LX1M, 3.6 GB RAM, EMUI, **no GMS**). Honest and graded.

**Ground truth about method (stated plainly):** the tests are written and run against **C# as the reference**, and the other-language work is **ported from that C#**. The P30 runs the **C#/.NET MAUI** app — so the C# reference is exactly what executes on this phone; the Go/Rust/Python/TypeScript/Kotlin/Swift/C ports run on *their* runtimes (desktop/Mac) and are validated there, not on the P30. That's why the port rows read "not a P30 run" — it's a different target, not a failure.

**Legend**
- ✅ **Proven** — ran on the P30 and produced real, verified output (pulled off the device).
- ◐ **Exercised / verdict** — runs on the P30 as part of the app, but the specific heavy path (or inference) wasn't independently demonstrated yet.
- ⬚ **Not a P30 run** — a port to another language, or a desktop/Mac test / audit / build step. The C# it mirrors runs on the P30; the item itself targets another runtime.
- 🚫 **Exception** — deliberately not done.

Evidence for ✅ rows: `adb exec-out run-as com.bhengubv.itsample cat files/capability-report.txt` + the pulled PDFs/WAV/PNG, plus the on-device coding run. Companion: [HARDWARE_FINDINGS_HUAWEI_P30.md](HARDWARE_FINDINGS_HUAWEI_P30.md).

---

## Ports, parity & cross-language tests (#1–#24)

| # | Item | P30 | Note |
|---|------|:---:|---|
| 1 | Personality + Federation → Go & Rust | ⬚ | Port; the C# reference runs on the P30 |
| 2 | Per-language core gaps (telephony/speech/skills) | ⬚ | Multi-language port |
| 3 | Cross-cutting holes across ports | ⬚ | Port work |
| 4 | Complete C port | ⬚ | C runtime, verified on Mac |
| 5 | Kotlin gap closure | ⬚ | Kotlin/JVM |
| 6 | Swift track | ⬚ | Swift, verified on Mac |
| 7 | HarmonyOS full port | 🚫 | Deliberate exception |
| 8 | Reconcile + code-parity report | ⬚ | Cross-language audit |
| 9 | Unify safety-phrase hash (FNV-1a) | ⬚ | C# runs on P30; ports validated on their runtimes |
| 10 | Federation delta dispatcher | ⬚ | C# runs on P30; ports elsewhere |
| 11 | Re-verify all builds | ⬚ | Build step (desktop) |
| 12 | Surface-parity audit | ⬚ | Audit |
| 13 | Close audit gaps (7 ports) | ⬚ | Port work |
| 14 | Test: C# reference suite | ⬚ | Desktop test of the C# that *does* run on the P30 |
| 15 | Test: Go | ⬚ | Port test |
| 16 | Test: Rust | ⬚ | Port test |
| 17 | Test: Python | ⬚ | Port test |
| 18 | Test: TypeScript | ⬚ | Port test |
| 19 | Test: Kotlin | ⬚ | Port test |
| 20 | Test: Swift | ⬚ | Port test (Mac) |
| 21 | Test: C | ⬚ | Port test (Mac) |
| 22 | Full-green report | ⬚ | Meta |
| 23 | Port cascade (15 pkgs) | ⬚ | Port work |
| 24 | Re-verify port suites | ⬚ | Port test |

## Neuron engine (#25–#33) — the C# runtime that IS the app on the P30

| # | Item | P30 | Note |
|---|------|:---:|---|
| 25 | IChatRuntime seam | ✅ | The runtime the app drives; chat runs on the phone |
| 26 | Concierge router | ✅ | Routing exercised on device (the Sweep) |
| 27 | ResidentSlotManager (RAM admission) | ✅ | Drove the OOM fix; now picks a model that fits **free** RAM (0.6B) and loads |
| 28 | Two-slot residency | ◐ | Brownout exercised (Sweep); full specialist hot-swap not demonstrated |
| 29 | Router option + Save/Load | ◐ | Present + compiled; not independently demonstrated on device |
| 30 | NeuronNode facade + DI | ✅ | The app's brain *is* this; runs on the phone |
| 31 | Voice into the Neuron | ◐ | Voice-flagged head compiles; wake→ASR→TTS loop not run |
| 32 | Neuron tests + standalone host | ◐ | Host runs on device; the tests are desktop |
| 33 | Build + regression gate | ⬚ | Meta |

## Neuron ports (#34–#40)

| # | Item | P30 | Note |
|---|------|:---:|---|
| 34 | Neuron → Python | ⬚ | Port |
| 35 | Neuron → Go | ⬚ | Port |
| 36 | Neuron → Rust | ⬚ | Port |
| 37 | Neuron → TypeScript | ⬚ | Port |
| 38 | Neuron → Kotlin | ⬚ | Port |
| 39 | Neuron → Swift | ⬚ | Port (Mac) |
| 40 | Neuron → C | ⬚ | Port (Mac) |

## Selection / enrichment gaps (#41–#48)

| # | Item | P30 | Note |
|---|------|:---:|---|
| 41 | Fit-vs-function verdict in BestFit | ✅ | The per-modality verdicts in the on-device report are this code |
| 42 | Actionable native-load error | ✅ | Surfaced on the phone (the OOM/load path) — drove the fix |
| 43 | Collapse model-storage dir | ◐ | Models land in one dir on device (`files/.config/CircleAI/Models`) |
| 44 | Enrichment on caller-owned system turns | ◐ | Chat runs on device |
| 45 | Reconcile the two registries | ✅ | The embedded registry loads on device; the selector reads it |
| 46 | Populate skill store from capabilities | ◐ | "what can you do?" exercised in the Sweep |
| 47 | Speech ladder foundation | ✅ | ASR/TTS verdicts on device |
| 48 | Compile + test gaps | ⬚ | Meta |

## Capabilities (#50–#68) — the device-facing features

| # | Item | P30 | Evidence |
|---|------|:---:|---|
| 50 | Voice on the phone | ◐ | Voice head compiles; loop not run. Selector: ASR/TTS `Good` |
| 51 | Vision model | ◐ | Pipeline **proven** on device: select → download (311 MB) → load SmolVLM-256M. But **inference blocked on this phone**: SmolVLM loads yet MNN's image path fails (`code -6` — its SigLIP arch isn't what the bridge's Qwen-VL/Kimi-VL vision generation supports); the Qwen-VL VLMs the bridge *does* support need ~2.4 GB+ and don't fit the ~1 GB free. Would run on a 4 GB+ phone. |
| 52 | Document engine → PDF | ✅ | CV PDF on device (64,172 B) |
| 53 | CV generator | ✅ | Rendered in the on-device suite |
| 54 | Corpus audit | ⬚ | Documentation deliverable |
| 55 | HTML → video / stills | ◐ | PNG **still** on device (`89504E47`); H.264/MP4 is a seam |
| 56 | TTS ladder | ◐ | Selector climbs to `Piper-en_US-lessac-high` on device; synthesis not run |
| 57 | Charts | ✅ | Chart PDF on device (42,757 B) |
| 58 | Presentations | ✅ | Deck PDF on device (63,388 B) |
| 59 | Music beds | ✅ | WAV on device (705,644 B, `RIFF/WAVE`) |
| 60 | Security — defensive | ✅ | Blocklist matched `malware.example` on device |
| 61 | Security — antibodies | ✅ | Deny-by-default gate on device |
| 62 | Business ops | ✅ | Invoice `INV-2026-0001`, `R 11 500.00` on device |
| 63 | Code from mobile | ✅ | 1.5B wrote a correct `is_prime()` on the phone |
| 64 | Cast to smart TV | ◐ | DLNA discovery ran on device (0 offline); no live cast |
| 65 | Cover letter + invoice | ✅ | Both PDFs on device |
| 66 | Report kind | ✅ | Report PDF on device (60,389 B) |
| 67 | Selector ladder → modalities | ✅ | Per-modality verdicts on device |
| 68 | Mesh hand-off router | ✅ | Capability advertise + list on device |

---

## Tally (67 items)

| P30 result | Count | Which |
|---|---:|---|
| ✅ Proven on device | **21** | 25,26,27,30,41,42,45,47,52,53,57,58,59,60,61,62,63,65,66,67,68 |
| ◐ Exercised / verdict | **12** | 28,29,31,32,43,44,46,50,51,55,56,64 |
| ⬚ Not a P30 run (port / test / meta) | **33** | 1–6, 8–24, 33, 34–40, 48, 54 |
| 🚫 Exception | **1** | 7 |

**Read it as:** on the P30 itself, **21 items ran with verified output** and **12 more run as part of the live app** (verdicts or compiled-but-not-exercised). **33 are ports or cross-language tests** — validated against the C# reference and on their own runtimes, not this phone. **1** is the agreed exception.

The three ◐ that convert to ✅ with more device work: **voice loop**, **vision inference** (SmolVLM), **TTS synthesis**. Coding already crossed that line.

*Snapshot 2026-07 on MAR-LX1M. Updated as the ◐ inference paths are run on the device.*
