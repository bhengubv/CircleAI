# CircleAI Corpus Audit — fork corpus ↔ roadmap, per phase

> Maps the `github.com/bhengubv` fork corpus (433 curated forks) onto the CircleAI
> roadmap, phase by phase. For each subsystem: the **real source forks** (what each
> fork actually *is*, not just its name), **what CircleAI already has** in `src/`,
> **what's still missing**, and **adjacent forks the roadmap didn't name** that are
> directly on-point. The recurring failure mode we hunt is the *"type exists but
> nothing constructs (or feeds) it"* trap — flagged inline as 🪤.
>
> **Method (read-only):** ROADMAP.md + the memory forks index (`github-forks-index.md`)
> + a name/file census of `CircleAI/src` + targeted grep + a few file reads. No build,
> no code changes. Fork descriptions are summarised from the memory index; where the
> index can't tell what a fork is, this says so. Substance judgements come from file
> census + grep, not from running anything.
>
> _Audited 2026-07-23. `src` holds ~165 `CircleAI.*` projects; this audit covers the
> phase-critical ones._

---

## How to read the "CircleAI has" verdicts

| Mark | Meaning |
|---|---|
| ✅ **Engine** | Real constructors + a pipeline that does the work. |
| 🟡 **Partial** | Real pieces exist, but a load-bearing seam is missing or unwired. |
| 🟨 **Scaffold** | The tell-tale triad `Contracts.cs` + `InMemory<X>.cs` + `NullImplementations.cs` — interfaces and placeholders, **nothing that does the work**. This is the trap. |
| ⬜ **Absent** | No project, or the project is empty of substance. |

**Fork-fit tags:** 🎯 = the fork genuinely does the thing · 🧭 = inspiration / shape-only · ⚠️ = **mislabeled** (the fork is not what the roadmap's usage implies).

**The scaffold signature.** These `src` projects are Contracts+InMemory+Null shells with no engine: `CircleAI.Video`, `CircleAI.MediaHub`, `CircleAI.Visualization`, `CircleAI.DocAnalytics`, `CircleAI.Observer`, `CircleAI.AutonomousBiz`, `CircleAI.Operator`, `CircleAI.CRM`, `CircleAI.CodeUnderstanding`, `CircleAI.DevTools`, `CircleAI.BuildFarm`. Domain "companion" projects (`CircleAI.Safety`, `CircleAI.Business`, etc.) are 3-file adapter/context/primitive stubs. Their *presence in the tree is not evidence of a capability.*

---

## Phase-at-a-glance

| Phase / subsystem | Real source fork(s) | CircleAI has | Verdict |
|---|---|---|---|
| **0 · Voice** | (own stack: Whisper/Piper) | `CircleAI.Voice` (23 files) ✅ | Built, **never run on a phone** |
| **0 · Vision** | — (no VLM fork named) | `KimiVlGenerator` in `CircleAI.Inference` ✅, wired to Hosting DI | Runtime built, 🪤 **no model catalogued** |
| **1 · Documents** | `career-ops` 🧭, `presenton` ⚠️ | `CircleAI.Documents` (10 files) ✅ engine | 🟡 engine real, **model→content seam & CV entrypoint unwired** |
| **2 · HTML→video** | `html-video` 🎯 | `CircleAI.Video` 🟨 scaffold; `CircleAI.Media` APNG/stills ✅ | 🪤 **flagship unbuilt — no MP4 encoder anywhere** |
| **2 · TTS ladder** | `LuxTTS` 🎯; `speakr`/`voicebox`/`patter` ⚠️ | `CircleAI.Voice` OnnxTtsEngine+Piper ✅ | Floor built; ladder rungs not integrated |
| **2 · Charts** | `understand-anything` ⚠️ | `CircleAI.Charts` (12 files) ✅ engine | ✅ real (PdfSharp renderer, from scratch) |
| **2 · Presentations** | `presenton` ⚠️ | `CircleAI.Presentations` (5 files) ✅ | ✅ PDF decks (not PPTX) |
| **2 · Stills (ASCII)** | `ASCILINE` ⚠️ | `CircleAI.Media` RasterCanvas/BitmapFont | 🟡 substrate only, no ASCII styliser |
| **2 · Music beds** | `suvmusic` ⚠️ | `CircleAI.Music` ProceduralMusicBedGenerator ✅ | 🟡 procedural (not a model; fork is a *player*) |
| **3 · Reflexes** | `ipblocklist` 🎯, `Watcher` 🧭, `intercept` 🧭, `shizuwall` 🧭 | `CircleAI.Security.Defense` (15 files) ✅ | ✅ always-on defence sentinel built |
| **3 · Antibodies** | `malwoverview`/`findme`/`deepdarkCTI`/`ghost-osint-crm`/`hacktricks`/`neko-master` 🧭 | `CircleAI.Security.Antibodies` (27 files) ✅ framework | 🪤 **corpus empty by design — no ingestion path built** |
| **4 · Business ops** | `gstack` 🧭, `automaton` 🧭, `dexter` ⚠️, `career-ops` 🎯 | `CircleAI.BusinessOps` (9) 🟡, `CircleAI.Workflows` (paca) 🟡 | 🟡 invoices/reminders real; autonomy scaffold |
| **5 · Code from mobile** | `PhoneHarness` ⚠️, `adb-device-manager-2` ⚠️, `dexter` ⚠️ | `CircleAI.CodeAgent` (8 files) ✅ loop+catalog | 🟡 agent loop real; **none of the 3 forks is an on-device coder** |
| **X · Selector ladder** | (own) | `CircleAI.Runtime` (18), `CircleAI.Inference` (29) ✅ | ✅ DeviceAwareModelSelector, CapabilityProbe |
| **X · Mesh offload** | (AetherNet) | `CircleAI.Mesh` (8) ✅ client; `CircleAI.Aether` contracts | 🟡 offload engine real; **radio transport stubbed (external)** |
| **X · Cast to TV** | `awesome-smart-tv` 🧭 | `CircleAI.Cast` (14 files) ✅ DLNA/UPnP | ✅ real (not derived from that fork) |

---

## Phase 0 — Close the two open claims

### Voice on the phone
- **Roadmap source:** none named (own Whisper + Piper stack).
- **CircleAI has** — ✅ `CircleAI.Voice` (23 files): `VoiceLoop`, `VoicePipeline`, `WhisperNetTranscriber`/`WhisperInterop`, `OnnxTtsEngine` + `PiperVoiceConfig`, `KwsWakeWordDetector`/`EnergyWakeWordDetector`, `EnergyVadDetector`, `NativeEspeakPhonemizer`, `OnnxSpeakerIdentity`, `OnnxSpeechEmotionDetector`. This is a genuinely built wake→ASR→TTS loop.
- **Missing:** the **Android voice head** (`-p:ItVoiceOnAndroid=true`) and an on-device run. Matches roadmap "🟡 built, never run on a phone." Not a type-trap — the engine is real; it's unproven on hardware.
- **Adjacent (unnamed) forks:** `ten-vad` (low-footprint ONNX VAD — a cleaner rung than `EnergyVadDetector`), `jarvis` (on-device VAD+STT+TTS reference), `google-ai-edge-gallery` (on-device Gemma with audio).

### Vision has a model
- **Roadmap source:** none named.
- **CircleAI has** — ✅ runtime: `KimiVlGenerator` + `VisionInput` live in **`CircleAI.Inference`** (not `CircleAI.Vision`), and grep confirms it's wired into `CircleAI.Hosting` DI (`AIService`, `AIOptions`, `ServiceCollectionExtensions`) and the `MnnTokenRouter`. So the VLM constructor is real and reachable.
- 🪤 **Trap (already flagged by roadmap):** **no vision model is catalogued** — no `ModelModality.Vision` SHA-256 entry, so `KimiVlGenerator` has nothing to load. Type + constructor exist; the *model that makes it run* does not.
- **Note — `CircleAI.Vision` is a different subsystem:** its 7 files are `OnnxFaceDetector`, `OnnxFaceEmbedder`, `OnnxPlateRecognizer`, `IVideoCapture` — i.e. **face/ALPR CV**, not a VLM. Don't conflate "Eyes = see images (VLM)" with `CircleAI.Vision` (biometrics/plates).
- **Adjacent (unnamed) forks:** for the VLM model itself — `vlmrun-hub` (VLM schemas for structured image/doc extraction), `google-ai-edge-gallery` (Gemma on-device, image input). For the face/ALPR side of `CircleAI.Vision` — `compv`, `ultimateALPR-SDK`, `CompreFace`, `face-api.js` (all in corpus, all on-device CV).

---

## Phase 1 — Documents (CVs first)

**Roadmap sources:** `career-ops`, `presenton`.

| Fork | What it actually is | Fit |
|---|---|---|
| `career-ops` | Multi-agent job-search system: AI ranks listings **and auto-generates tailored CVs**. | 🎯 on-point for the CV vertical (and Phase 4 ops). |
| `presenton` | Self-hosted AI **presentation** generator, editable **PPTX** export, via Ollama/OpenAI/Gemini/Anthropic. | ⚠️ a *slides* tool, cloud-LLM driven — inspiration only for docs. |

- **CircleAI has** — ✅ `CircleAI.Documents` (10 files): `PdfSharpDocumentEngine` + `IDocumentEngine`, `CvDocument`, `CoverLetter`, `Invoice`, templates (`SingleColumnCvTemplate`, `ClassicCoverLetterTemplate`, `ClassicInvoiceTemplate`), `EmbeddedFontResolver`. A real, offline, **template→PDF** engine on PDFsharp (licence-correct per memory rules).
- 🪤 **Trap — the content-from-model seam is not wired.** Grep for `IDocumentEngine`/`CvDocument`/`GenerateCv`/`ICvGenerator`/`BuildCv` returns **only references inside `CircleAI.Documents` itself** — no external caller, no orchestrator that asks the model to write tailored bullets and hands them to a template, and no CV-generation entrypoint. The *deterministic half* (template→PDF) is built and construct-ready; the *"model writes the content"* half (the actual roadmap deliverable) has no evident binding. Consistent with the Phase 1 boxes all being unchecked.
- **Missing:** model→template binding; a `GenerateCv(role, profile)` entrypoint; proof on the Huawei (a real PDF). Invoices exist as a template here **and** as records in `CircleAI.BusinessOps` — the LedgerAPI/BidBaas/SDPKT tie-in (roadmap) is not evident in `src`.
- **Adjacent (unnamed) forks, directly on-point:** `ai-resume-analyzer` (ATS scoring + JD→candidate matching — exactly "tailor bullets to a target role"), `free-resume-maker` (ATS resume builder + PDF export), `docling` (PDF/DOCX/PPTX parsing → unified doc model, for ingesting an existing CV), `PDFMathTranslate` (layout-preserving PDF transforms).

---

## Phase 2 — Media generation

**Roadmap sources:** `html-video`, `ASCILINE`, `understand-anything`, `presenton`, `suvmusic`, TTS cluster (`speakr`, `voicebox`, `patter`, `LUXTTS`).

### HTML → video / stills (the flagship bet)
| Fork | What it actually is | Fit |
|---|---|---|
| `html-video` | Local coding-agent pipeline: prompt/article/repo → single-file **animated HTML** → rendered to **MP4** (optional AI soundtrack). | 🎯 exactly the roadmap bet. |

- **CircleAI has** — for **stills/animation only**: `CircleAI.Media` (12 files) has `ManagedMediaRenderer`, `RasterCanvas`, `AnimatedPngEncoder` (APNG), `ImageCodecs`, `BitmapFont`, `MediaTemplates`, `MediaSpec`. Real programmatic raster output.
- 🪤 **Trap — the video half does not exist.** `CircleAI.Video` is 🟨 scaffold (`Contracts` + `NullImplementations` + `Primitives`). **No MP4/H.264 encoder or muxer exists anywhere in `src`** — the `mp4|h264|ffmpeg|mux|webm` grep only hits `CircleAI.Cast` (DLNA *serving* of existing media to a TV), APNG format strings in `CircleAI.Media`, and a generic `CommandRunner`. The `html-video` approach is **not ported**; MAUI's HTML-render → MP4 pipeline is unbuilt. This is the single biggest Phase 2 gap.
- **Adjacent (unnamed) forks for actual video:** `OpenMontage` (agentic video production — corpus retrieval, timeline edit, TTS narration, renders via Remotion), `OpenCut` (video editor with **headless batch rendering** + MCP), `MoneyPrinterV2` (Shorts automation), `aimangastudio` (LLM→storyboard→PNG/PDF export).

### TTS ladder
| Fork | What it actually is | Fit |
|---|---|---|
| `LuxTTS` | Lightweight zipvoice TTS, 48 kHz voice cloning, 150× realtime, <1 GB VRAM. | 🎯 a real on-device TTS **model** — the one genuine rung. |
| `speakr` | Self-hosted **transcription/note-taking** (WhisperX diarization, chat over recordings). | ⚠️ this is **STT/RAG**, not TTS. |
| `voicebox` | Local voice **studio**: cloning + TTS + STT + dictation. | 🧭 a bundle, not a drop-in engine. |
| `patter` | SDK that gives an **AI agent a phone number** (agent loop + STT/TTS + telephony). | ⚠️ a **telephony** SDK, not a TTS rung. |

- **CircleAI has** — ✅ the **floor**: `OnnxTtsEngine` + `PiperVoiceConfig` + `NativeEspeakPhonemizer` in `CircleAI.Voice`. Piper is the established rung.
- **Missing:** any *higher* rung integrated. Of the four "TTS cluster" names, **only LuxTTS is actually a TTS model** — the roadmap's own instruction ("evaluate LUXTTS/voicebox/patter/speakr as rungs above Piper") is built on three mislabeled forks.
- **Adjacent (unnamed) real-TTS forks:** `fish-speech` (expressive TTS + voice cloning), `tiny-tts` (~3.4 MB ONNX, CPU/edge — a true *floor-below-Piper* rung), `Amphion` (TTS + singing + voice conversion toolkit), `alexandria-audiobook`/`audiblez` (TTS pipelines).

### Data → charts
| Fork | What it actually is | Fit |
|---|---|---|
| `understand-anything` | Turns a **codebase/docs into a queryable knowledge graph** (code intelligence). | ⚠️ **not a charting tool** — belongs in Phase 5 / CodeUnderstanding. |

- **CircleAI has** — ✅ `CircleAI.Charts` (12 files): `PdfSharpChartRenderer`, `BarChartDrawer`, `LineChartDrawer`, `PieChartDrawer`, `AxisChart`, `Legend`, `ChartSpec`/`ChartSpecFactory`/`ChartStyle`. A genuine from-scratch chart renderer — **not derived from the named fork.** `CircleAI.Visualization` beside it is 🟨 scaffold; the real one is `Charts`.
- **Adjacent (unnamed) forks:** `mermaid` (LLMs commonly emit mermaid for generated diagrams), `json-render` (LLM→schema-constrained JSON→PDF/component render).

### Presentations
- `presenton` (see Phase 1) exports **PPTX via cloud LLMs**. **CircleAI has** ✅ `CircleAI.Presentations` (5 files): `PdfSharpDeckEngine`, `IDeckEngine`, `Deck`, `LandscapeSlideTemplate`, `SampleDeck` — offline **PDF decks**, no PPTX. Fork is inspiration only; editable-PPTX export is not ported (and isn't offline-friendly).

### ASCII / stylised stills
- `ASCILINE` is a real-time **ASCII/pixel video-streaming** engine (low-bandwidth text frames instead of codecs) — ⚠️ it's a *mesh streaming codec*, a loose fit for "stylised stills." **CircleAI has** the substrate (`RasterCanvas`, `BitmapFont` in `CircleAI.Media`) but 🟡 **no ASCII-art styliser** ports ASCILINE's actual behaviour.

### Music beds
| Fork | What it actually is | Fit |
|---|---|---|
| `suvmusic` | Android hi-fi **music streaming/playback** app (YouTube/local sources, EQ, Listen-Together rooms). | ⚠️ a **player**, not a generator. |

- **CircleAI has** — 🟡 `CircleAI.Music` (12 files): `ProceduralMusicBedGenerator`, `IMusicBedGenerator`, `MusicBedGeneratorResolver`, `MusicTheory`, `WavWriter`, `Mood`, `MusicalKey`. Real, but **procedural/algorithmic** — not the "on-device small model" the roadmap describes. Doubly off: the named fork isn't a generator, and what's built isn't a model.
- **Adjacent (unnamed) real music-gen forks:** `InspireMusic` (autoregressive-transformer text-to-music), `Amphion` (music/song/audio generation). These are the actual sources for a *model-based* rung above the procedural floor.

---

## Phase 3 — Immune system (built-in security)

### Reflexes (defensive, always-on)
| Fork | What it actually is | Fit |
|---|---|---|
| `ipblocklist` | Aggregated inbound/outbound IP blocklists (2-hourly, public-DNS exclusions). | 🎯 a real **feed** the defence layer consumes. |
| `Watcher` | Django/React AI cyber-threat-intel platform (digests, threat monitoring). | 🧭 shape for the monitor/dashboard. |
| `intercept` | Web SIGINT for SDR (POCSAG/ADS-B/AIS, Wi-Fi/BT scan, LoRa Meshtastic). | 🧭 RF/mesh awareness; server-flavoured. |
| `shizuwall` (ShizuWall) | Android **per-app firewall** without VPN (Shizuku/ADB/root), offline-only. | 🧭 on-device network-access-control shape. |

- **CircleAI has** — ✅ `CircleAI.Security.Defense` (15 files): `AlwaysOnDefenseSentinel`, `BlocklistThreatMonitor` + `BlocklistIndicatorSource`/`BlocklistParser`/`Ipv4Cidr` (the `ipblocklist` shape, **wired**), `ConnectionRateAnomalyDetector`, `NetworkObservation`, `IThreatMonitor`/`IThreatSink`, `WatchdogThreatSink`, `SosEscalation` (the Panik/Nope tie-in). This is a genuinely built always-on reflex layer. `CircleAI.Security` (15 files, peer/mesh trust: `ThreatDetector`, `AISecurityLayerService`, `NodeTrustRegistry`) backs it on the AetherNet side.
- **Missing / verify:** the actual blocklist *dataset* delivery (does a real `ipblocklist` snapshot ship or download offline?) — `BlocklistIndicatorSource` exists but the data path on-device isn't proven here.
- **Adjacent (unnamed) forks:** `Bluehound` (BLE recon + RSSI/flood anomaly — matches the mesh threat surface), `fingerprint-suite` (anti-fingerprinting), `neko-master` (network-traffic monitor — the roadmap files it under antibodies but it's really a *reflex*/monitor).

### Antibodies (gated by authorized-use boundary)
| Fork | What it actually is (index) | Fit |
|---|---|---|
| `malwoverview` | Malware-triage CLI (VirusTotal/Hybrid Analysis lookups). | 🧭 shape for file-threat awareness. |
| `findme` | Username OSINT across 400+ platforms. | 🧭 shape for identity-exposure awareness. |
| `deepdarkCTI` | Curated deep/dark-web CTI feed list (IoCs). | 🧭 shape for the indicator corpus. |
| `ghost-osint-crm` (GHOST-osint-crm) | Self-hosted OSINT investigation CRM (entity graphs, geo map, WiGLE). | 🧭 investigation shape (server app). |
| `hacktricks` | The HackTricks pentest wiki as a local mdBook. | 🧭 knowledge base only. |
| `neko-master` | Dockerized real-time network-traffic monitor. | 🧭 monitor (really a reflex). |

- **CircleAI has** — ✅ framework: `CircleAI.Security.Antibodies` (27 files). The **authorized-use boundary is implemented**: `IAuthorizedUseGate`, `ExplicitConsentAuthorizedUseGate`, `AuthorizedUseConsent`/`Request`, `IAuthorizedUseConsentStore`, `NullAuthorizedUseGate`. Awareness assessors exist: `FileThreatAwareness`, `NetworkThreatAwareness`, `BreachExposureAwareness`, `IdentityIndicator`, `ThreatIndicator`, `IndicatorNormalizer`. `DefensiveAntibodySystem` is the front door.
- 🪤 **Trap — the corpus is empty by design and nothing feeds it.** The default is `EmptyIndicatorCorpus` ("holds nothing… **completely inert** … denies every request"). The README is explicit: the six forks above were studied **for the *shape* of the knowledge only** — "No network… nothing bundled loose." A host must load a dataset into `InMemoryIndicatorCorpus`, and **no ingestion path from any of the named forks is built.** So every awareness assessor returns "no known threat." This is intentional (Principle 8 / "nothing loose"), but it means the antibody capability is a *framework with no knowledge* until someone builds a curated, authorized, offline indicator pack. The written authorized-use boundary the roadmap requires **does exist in code** (the gate); the *data* does not.
- **Adjacent (unnamed) forks:** `blackbird` (OSINT profiling w/ PDF export), `Argus`, `GhostTrack` (IP/phone/username recon), `capa` (binary capability→ATT&CK — a real file-threat engine), `Ciphey` (cipher/encoding ID).

---

## Phase 4 — Business operations (the operator)

**Roadmap sources:** `gstack`, `automaton`, `dexter`, `career-ops`.

| Fork | What it actually is | Fit |
|---|---|---|
| `gstack` | Claude-Code slash-command "software factory" — 23-role virtual eng team. | 🧭 dev-ops orchestration shape (not business ops). |
| `automaton` | Continuously-running self-replicating "sovereign" agent that provisions compute + pays via crypto wallet. | 🧭 autonomy shape (aggressive; crypto-funded). |
| `dexter` | Autonomous **financial-research** agent (market data, filings). | ⚠️ finance *research*, not invoicing/ops. |
| `career-ops` | Multi-agent job-search + CV generation. | 🎯 the jobs/ops-automation piece. |

- **CircleAI has** — 🟡 mixed:
  - `CircleAI.BusinessOps` (9 files): `BusinessStore`, `Clients`, `Invoices`, `Money`, `Reminders`, `Services`, `CrmBridge` — real invoicing/clients/reminders scaffolding, **but** `SampleData` + `NullImplementations` present (watch the memory "no fake data in fallbacks" rule).
  - `CircleAI.Workflows` (13 files): a port of the **`paca`** fork (PM platform) — `PacaAgents/Boards/Projects/Mcp/Realtime/Skills/...`. Substantial but it's PM-workflow contracts, not the invoicing/follow-up loop.
  - 🟨 `CircleAI.AutonomousBiz`, `CircleAI.Operator`, `CircleAI.CRM` are **scaffold-only** (Contracts+InMemory+Null). The "automated operations engine" (roadmap) and the `automaton`-style autonomy are **not built** — the types exist, nothing runs them.
- **Missing:** the actual operate-the-business loop (invoice → follow-up → schedule), and any tie between `BusinessOps.Invoices` and `CircleAI.Documents.Invoice` (two separate invoice notions today).
- **Adjacent (unnamed) forks, strongly on-point:** `midday` (all-in-one AI business assistant — time tracking, **invoicing**, receipt-matching "Magic Inbox", financial assistant — the closest single fork to this phase), `storecraft` (agentic e-commerce backend), `Taskosaur`/`paca` (already partly ported), `show-me-the-money` (agent skills that operate a business), `restate` (durable execution for reliable agents), `n8n-workflows` (4,343 automation flows).

---

## Phase 5 — Code from mobile

**Roadmap sources:** `PhonesHarness` (index: `PhoneHarness`), `adb-device-manager-2`, `dexter`.

| Fork | What it actually is | Fit |
|---|---|---|
| `PhoneHarness` | **Evaluation** harness/benchmark for phone AI agents (verifiable side-effects on Android emulators). | ⚠️ a *benchmark*, not a coding agent. |
| `adb-device-manager-2` (Adb-Device-Manager-2) | Desktop Flutter/Kotlin tool bridging Android↔Windows over **ADB**. | ⚠️ device tooling, not on-device inference. |
| `dexter` | Autonomous finance-research agent. | ⚠️ wrong domain (finance, cloud data). |

- **CircleAI has** — ✅ `CircleAI.CodeAgent` (8 files): `CodeAgentLoop`, `CodingCapabilityPlanner`, `CodingModelCatalog`, `CodingModelRequirements`, `CommandRunner`, `AgentAction`, `ServiceCollectionExtensions`. A real agent loop with a capability planner + model catalog — hardware-tiered exactly as the roadmap wants (a P30 reports *Unavailable*, a Pixel gets a real model). 🟨 `CircleAI.CodeUnderstanding`, `CircleAI.DevTools`, `CircleAI.BuildFarm` beside it are scaffold-only.
- **Missing:** the roadmap's three named sources give it **nothing** — none is an on-device coding-model runtime. The mesh path for weak phones is **blocked on RT-12** (see cross-cutting).
- 🪤 **Note:** the roadmap's Phase 5 fork list is entirely mismatched to the phase. The real sources are unnamed:
- **Adjacent (unnamed) forks that ARE on-device coders:** `openclaw-android` (**runs a coding-agent CLI on Android in Termux** — the literal "coder on the phone"), `talkcody` (local-first coding agent, any model, on-machine storage), `picoclaw` (<10 MB Go agent), `claude-code`/CLAURST (Rust terminal coding agent). For the code-intelligence side: `repowise`, `GitNexus`, `Understand-Anything`, `describer`.

---

## Cross-cutting workstreams

### Selector ladder (extend to every modality)
- ✅ **Built and real.** `CircleAI.Runtime` (18 files): `BackendSelector`, `CapabilityProbe` + per-OS probes (`AndroidCapabilityProbe`, `Linux/MacOS/Windows`), `HostProfile`, `CapabilityTier`, `NativeRuntimeFetcher/Registry`. `CircleAI.Inference` (29 files): `DeviceAwareModelSelector`, `SpeechModelSelector`, `ContextWindowBudgetManager`, `PowerBudget`, `ModelDownloadGate`. The `PlanFor` per-device answer exists for chat/speech/vision. **Gap:** documents/media/coding don't yet report through the same selector (they have no model to select — see the model-catalogue traps in Phases 0/2).

### Mesh inference-offload (AetherNet RT-12)
- 🟡 **Offload engine is real; the transport under it is the stub.** `CircleAI.Mesh` (8 files): `MeshOffloadClient` is a **fully implemented** request/reply-correlation + inbound-serve + advert-ingest engine (read in full), plus `MeshOffloadRouter`, `MeshOffloadWire`, `AetherMeshCapabilityBroadcaster`. **But** it rides an injected `INetworkTransport` and *explicitly does not open sockets or discover peers* — "Zero-infrastructure BLE / Wi-Fi Direct discovery is **AetherNet's responsibility** (aether-protocol repo), not this package's." `CircleAI.Aether` (10 files) is pure interfaces/events — the seam. So the roadmap's "transport stubbed" is precise, but the stub lives **outside CircleAI** (the AetherNet broadcast transport); CircleAI's own offload logic is done. Phase 5's weak-phone path and Principle 1's "scale via mesh" both wait on that external transport.
- **Adjacent (unnamed) forks:** `exo` (cluster LLMs across your own devices — topology-aware tensor/pipeline parallelism), `OpenDAN` (distributed AI compute), `Second-Me` (decentralized-identity peer net).

### Output surfaces beyond the phone (cast to TV)
- ✅ **Built.** `CircleAI.Cast` (14 files): `DlnaCastEngine`, `DlnaCastDiscovery`, `DlnaCastSession`, `SsdpClient`, `UpnpControlPoint`, `TcpMediaHost` — a real DLNA/UPnP casting stack. The roadmap named `awesome-smart-tv` (a *resource list* for Tizen/webOS/Android TV/tvOS), 🧭 which is reference-only; the actual implementation is standards-based DLNA, not derived from that fork.

---

## Consolidated trap register (type exists / nothing constructs or feeds it)

| # | Where | The trap | Evidence |
|---|---|---|---|
| 1 | **Phase 0 Vision** | `KimiVlGenerator` is built + DI-wired, but **no vision model is catalogued** → nothing to load. | grep: no `ModelModality.Vision`; roadmap concurs. |
| 2 | **Phase 1 Documents** | Template→PDF engine is real, but the **model→content seam and CV entrypoint are unwired** — no external caller of `IDocumentEngine`/`CvDocument`, no `GenerateCv`. | grep: all refs internal to `CircleAI.Documents`. |
| 3 | **Phase 2 Video** | The flagship HTML→video bet: **no MP4/H.264 encoder exists**; `CircleAI.Video` is scaffold-only. | census + `mp4/h264/ffmpeg/mux` grep hits are Cast/APNG only. |
| 4 | **Phase 2 Music** | `IMusicBedGenerator` resolves to a **procedural** generator, not the "on-device small model" claimed; the named fork (`suvmusic`) is a *player*. | census: `ProceduralMusicBedGenerator`; index: SuvMusic = streaming app. |
| 5 | **Phase 3 Antibodies** | Full awareness framework + authorized-use gate, but the indicator corpus is **`EmptyIndicatorCorpus` — inert, and no ingestion path from the six threat forks is built**. | README + `DefensiveAntibodySystem.cs`: "completely inert… denies every request." |
| 6 | **Phase 4 Autonomy** | `CircleAI.AutonomousBiz` / `Operator` / `CRM` are **scaffold-only**; the "automated operations engine" isn't built. | census: Contracts+InMemory+Null triad. |
| 7 | **Phase 5 sources** | All three roadmap-named forks are **mismatched** (a benchmark, an ADB bridge, a finance agent); none is an on-device coder. The real sources (`openclaw-android`, `talkcody`, `picoclaw`) are unnamed. | index descriptions. |
| 8 | **Broad surface** | ~165 `CircleAI.*` projects; a large share are scaffold shells whose *presence overstates capability* (the claimed-vs-delivered pattern). | `Video/MediaHub/Visualization/Observer/Operator/CRM/CodeUnderstanding/DevTools/BuildFarm/AutonomousBiz/DocAnalytics`. |

## Fork-labelling corrections (roadmap ↔ reality)

- **TTS cluster mostly isn't TTS:** `speakr` = transcription/STT, `patter` = telephony agent SDK, `voicebox` = studio bundle. Only **`LuxTTS`** is a TTS model. Real rungs to evaluate instead: `LuxTTS`, `tiny-tts`, `fish-speech`, `Amphion`.
- **`understand-anything`** is code→knowledge-graph, **not** data→charts. Move it to Phase 5 thinking; `CircleAI.Charts` is a from-scratch renderer owing it nothing.
- **`presenton`/`career-ops`** are cloud/multi-agent references; CircleAI reimplemented offline (`PdfSharpDeckEngine`, own CV templates).
- **`suvmusic`** is a streaming player, not a generator; **`dexter`** (Phases 4 & 5) is a finance-research agent, off-domain for both.
- **`awesome-smart-tv`** is a link list; the real cast stack is DLNA/UPnP in `CircleAI.Cast`.
