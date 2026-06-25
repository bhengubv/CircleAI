# Changelog — CircleAI

All notable changes to the CircleAI runtime are documented here. The format
is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [3.3.0] — 2026-06-25 — **HER / JARVIS parity — always-on, world-aware, embodied, learns-you, reasoner**

Closes the gap between the 3.2.0 substrate ("real backends for 24 HER/Jarvis
contracts") and a companion that actually behaves like HER / JARVIS: always
listens, sees what you see, knows your day, controls your home, learns your
voice over years, and reaches you across devices. 28 deliverables across
5 phases (A=always-on, B=world-aware, C=embodied, D=learns-you, E=reasoner)
landed against existing HER/Jarvis interfaces — no new contract surfaces
introduced.

### Added — Phase A · Always-on persona

- **`CircleAI.Voice.KwsWakeWordDetector`** — low-latency ONNX keyword-spotter
  on a sliding 1 s window every 100 ms. Replaces the ASR-based
  `EnergyWakeWordDetector` for production wake-on-"Hey B". Mel-spectrogram
  feature extraction (Hamming + DFT + mel filterbank) or raw-waveform input,
  configurable threshold + cooldown.
- **`CircleAI.Maui.AlwaysOnService`** — `IHostedService` per platform:
  Android sticky `ForegroundService` with `microphone` type +
  `circleai-always-on` notification channel; iOS `AVAudioSession`
  `PlayAndRecord` with `MixWithOthers` + `AllowBluetooth` +
  interruption observer.
- **`CircleAI.Memory.Sync.CompanionConversationSyncBridge`** —
  `ConversationStateDelta` (session id, partial transcript, in-flight turn
  flag, timestamps) cross-device via the existing `SyncableEntry` pipeline.
  Move a call phone → laptop mid-stream.
- **End-to-end episodic pipeline** wired in `CompanionSession`: every
  user/assistant turn is jointly embedded via `ITextEmbedder` before
  `EpisodicMemory.RecordAsync` so `LoadRecentMemoriesAsync` can do
  embedding-based recall.

### Added — Phase B · World-aware

- **`CircleAI.Integration`** — base contracts: `ICalendarConnector`,
  `IEmailConnector`, `INewsSource`, `IWeatherProvider`, `IRoutingProvider`,
  `IHomeAutomationConnector`.
- **`CircleAI.Integration.Calendar`** — Google Calendar v3 (OAuth),
  Microsoft Graph v1.0, generic CalDAV (`REPORT` verb + ICS parser).
- **`CircleAI.Integration.Email`** — IMAP (MailKit), Gmail API v1
  (base64url), Microsoft Graph mail.
- **`CircleAI.Integration.News`** — RSS 2.0 + Atom 1.0 dual parser,
  NewsAPI / GNews, Bluesky AT-proto `searchPosts`, Mastodon public /
  hashtag timeline.
- **`CircleAI.Integration.Geo`** — Open-Meteo current + hourly with full
  WMO weather-code decoder; OSRM driving/bike/foot routing with polyline.
- **`CircleAI.Companion.ProactiveBriefingService`** — `IHostedService`
  with a configurable fire-times schedule that pulls calendar + email +
  news + weather, runs an LLM summary, and dispatches via
  `IBriefingNotifier`. Internal `TimeUntilNextFire` helper.

### Added — Phase C · Embodied

- **`CircleAI.Integration.HomeAssistant.HomeAssistantConnector`** — REST
  bridge over `/api/states` + `/api/services/{domain}/{service}` POST
  with a Long-Lived Access Token. `TurnOnAsync` / `TurnOffAsync`
  convenience surface.
- **`CircleAI.Vision.IVideoCapture` + `CircleAI.Maui.MauiCameraCapture`** —
  platform-conditional camera capture mirroring `MauiAudioCapture`:
  Android Camera2 with `ImageReader` + Y-plane extraction, iOS
  `AVCaptureSession` with `VideoDataOutput` delegate, Windows
  `MediaCapture` with `MediaFrameReader` + SoftwareBitmap → JPEG.
  `BoundedChannel`-backed `FrameQueue` for cross-thread frame hand-off.
- **`CircleAI.Vision.OnnxFaceDetector` / `OnnxFaceEmbedder` /
  `OnnxPlateRecognizer`** — YOLO-family face detection with letterbox +
  NMS, ArcFace 112×112 BGR mean-subtracted 512-D embedder (L2 normalised),
  plate-region detector. SixLabors.ImageSharp for cross-platform decode.
- **`CircleAI.Maui.HealthBoardBridge`** — `IHostedService` that polls
  Android Health Connect (ContentResolver fallback) or iOS HealthKit
  (`HKHealthStore`) and records into `IWearableBoard`.
- **`CircleAI.Maui.LocationBridge`** — Android `LocationManager`
  (GpsProvider preferred) / iOS `CLLocationManager` with
  `RequestWhenInUseAuthorization`, feeds `IChildSafetyBoard.RecordCheckIn`.

### Added — Phase D · Learns you

- **`native/mnn-bridge` 1.4.0** — adds `mnn_llm_train_lora_step` +
  `mnn_llm_save_lora` C ABI surface. Implementation is gated by
  `#ifdef MNN_BUILD_TRAIN`; when MNN is built without the training
  subsystem (the upstream binary release), both functions return the new
  `MNNBRIDGE_ERR_TRAINING_DISABLED` (-12). Bundled `libmnnbridge.dylib`
  for `osx-arm64` (built on the Mac with brew cmake + Xcode 26 / AppleClang
  17, linked against the macOS framework).
- **`CircleAI.Inference.LoRAAdapterManager.TrainStep / SaveAdapter`** —
  managed P/Invoke for the new native surface. `NotSupportedException`
  with the explicit "rebuild MNN with `-DMNN_BUILD_TRAIN=ON`" guidance
  when -12 returns.
- **`CircleAI.Inference.FeedbackTrainingQueue`** — line-delimited JSON
  file-backed queue (`TrainingSample` records) with atomic drain.
- **`CircleAI.Inference.NightlyAdapterTrainer`** — `IHostedService`
  with `NightlyAdapterTrainerOptions` (`MinBatchSize`,
  `MaxSamplesPerRun`, `LearningRate`, `LoRARank`, `AdapterPath`,
  `Interval`, `ShouldFireNow`, `Tokenizer`). Drains the queue, runs
  `TrainStep` per sample, atomically `SaveAdapter` + `Apply`. Char-level
  fallback tokenizer. Re-queues on training-disabled.
- **`CircleAI.Memory.Sync.LoraAdapterSyncBridge`** — base64-encoded
  adapter bytes propagate across devices as `LoraAdapterSnapshot`
  syncable entries.

### Added — Phase E · Reasoner

- **`CircleAI.Companion.ReasoningLoopInnerMonologue`** — real o1 /
  DeepSeek-R1 style inner monologue using
  `IChatGenerator.StreamFragmentsAsync` with `IncludeReasoning = true`.
  Captures `Reasoning`-kind fragments as the thought; `Content`
  fragments form the visible answer. Replaces `TemplateInnerMonologue`.
- **`CircleAI.Companion.SqliteKnowledgeGraph` +
  `LlmKnowledgeGraphExtractor`** — `kg_nodes` + `kg_triples` SQLite
  triple store with indexes; LLM-driven entity / relation extractor with
  JSON-strict prompt + defensive bracket-finding parser. Replaces
  `AdjacencyPersonalKnowledgeGraph`.
- **`CircleAI.Companion.SqliteHippoRagStore`** — real
  Personalised PageRank walk over the personal KG (damping 0.85, 32
  iterations) seeded from query terms. Multi-hop recall returns top-K
  nodes as `MemoryHit` with PR mass as score.
- **`CircleAI.Companion.BayesianWorldModel`** — online-learning Naive
  Bayes classifier over (observations → outcome) pairs with Laplace
  smoothing. Softmax-normalised posteriors. Replaces
  `FrequencyWorldModel`.
- **`CircleAI.Companion.SequencePredictiveEngine`** — variable-order
  (default 3-gram) Markov chain over the user's event timeline with
  back-off weighting and per-event inter-arrival forecasting. Replaces
  the slot-of-week `HistogramPredictiveEngine`.
- **`CircleAI.Voice.OnnxSpeakerIdentity` +
  `CircleAI.Companion.OnnxSpeakerIdentityAdapter`** — ECAPA-TDNN-style
  speaker embedder (log-mel or raw waveform input) with JSON
  enrollment store, running-mean centroid update, cosine-similarity
  match. Replaces the MFCC `EnergyBandVoiceIdentity`.
- **`CircleAI.Voice.OnnxSpeechEmotionDetector` +
  `CircleAI.Companion.OnnxSpeechEmotionSensor`** — wav2vec2-style speech
  emotion ONNX with Russell-circumplex arousal / valence mapping per
  label. Plugs into `IEmotionSensor` via a base64-audio key in the
  fused-signal JSON.
- **`CircleAI.SelfBench`** — new project: `BenchTask` / `BenchResult` /
  `BenchSummary` records, `BuiltInScorers` (exact / substring / regex /
  numeric-tolerance), `BenchRunner`, `AbBenchRunner` with
  `RegressionGateConfig` (mean-score threshold, p95 latency cap,
  critical-task regression cap), `BenchSuiteRegistry` with a built-in
  10-task default suite (math, factual, format, refusal, reasoning).
- **`CircleAI.Companion.SelfBenchSelfImprovementLoop`** — implements
  `ISelfImprovementLoop` by orchestrating SelfBench: baseline vs
  candidate `IAIService`, regression-gated promotion.

### Changed

- Version-aligned every 3.x runtime package from 3.2.0 → 3.3.0 (49
  packages), seven new packages join the runtime tier at 3.3.0
  (`CircleAI.Integration`, `…Calendar`, `…Email`, `…News`, `…Geo`,
  `…HomeAssistant`, `CircleAI.SelfBench`). Touched 1.x packages bumped
  one minor: `CircleAI.Companion` 1.2.0 → 1.3.0,
  `CircleAI.Voice` 1.2.0 → 1.3.0, `CircleAI.Memory` 1.3.0 → 1.4.0.
- `native/mnn-bridge/CMakeLists.txt` — macOS framework search path
  (`-F${MNN_FRAMEWORK_PARENT}`) so `<MNN/...>` includes inside
  MNN.framework headers resolve via the framework name; needed because
  `MNN/expr/Expr.hpp` is referenced from MNN's own `llm/llm.hpp` and
  the bundle's Headers directory has no `MNN/` subfolder.

### Fixed

- `CircleAI.Voice.KwsWakeWordDetector` — `ReadOnlyMemory<byte>.AsSpan(...)`
  doesn't exist; replaced with `.Span.Slice(...)`.
- `CircleAI.Inference.csproj` — added
  `Microsoft.Extensions.Hosting.Abstractions` +
  `Microsoft.Extensions.Logging.Abstractions` (required by
  `NightlyAdapterTrainer : IHostedService`).
- `CircleAI.Maui.MauiCameraCapture` — `Android.Media.ImageFormatType`
  doesn't exist; corrected to `Android.Graphics.ImageFormatType`.

### Build verification

- `CircleAI.Companion` (net9.0) — green; transitively builds
  `Core / Memory / Embeddings / Integration / Domain / Identity /
  Sync / Languages / Voice / Tools / Hosting / Networking / Inference /
  SelfBench`.
- All five `CircleAI.Maui` TFMs — `net9.0`, `net10.0`,
  `net10.0-android`, `net10.0-ios`, `net10.0-maccatalyst`,
  `net10.0-windows10.0.19041.0` — green.
- `CircleAI.Integration.{Calendar,Email,News,Geo,HomeAssistant}` + 
  `CircleAI.Vision` — green.
- Native `libmnnbridge.dylib` (osx-arm64) — built on Mac
  (Xcode 26 / AppleClang 17), linked against MNN.framework, copied
  into `src/CircleAI.Inference/runtimes/osx-arm64/native/` alongside
  the staged `libMNN.dylib` (15.7 MB).

## [3.2.0] — 2026-06-22 — **HER / Jarvis lift — companion substrate**

Seven new packages port working backends from CircleUp + Concierge into
the CircleAI substrate so any consumer can light up a HER- / Jarvis-class
companion experience without re-deriving the protocol scaffolding. Every
new package ships contracts + null + real backends + a fail-soft DI path.

### Added

- **`CircleAI.Companion.Proactive`** — generic proactive scheduler lifted
  from CircleUp's workflow engine (vault coupling stripped). 5-field cron
  expression parser, per-(context, id) last-run tracking,
  `IProactiveTaskSource` / `IProactiveTaskRunner` /
  `IProactiveScheduler` abstractions, in-memory + delegate impls, plus
  an `IHostedService` that ticks every minute and refreshes every five.
- **`CircleAI.Hosting.CloudFallback`** — multi-provider chat fallback
  (lift of Concierge's working OpenAI / Anthropic / Gemini runtimes).
  SSE streaming, fail-soft "not configured" sentinel frame, and a
  composite `CloudFallbackChain` that walks providers in order, skipping
  the unconfigured ones and falling through on mid-stream failure.
- **`CircleAI.Speech.Cloud`** — cloud voice loop. OpenAI Whisper
  recognizer (PCM-16 mono wrapped in a WAV envelope so Whisper accepts
  it), OpenAI TTS synthesizer with `response_format=pcm` honouring the
  `SynthesisResult.AudioPcm16Mono` contract, plus a generic regex
  `KeywordVoiceIntentRouter` lifted from CircleUp (vault-specific
  intents stripped, host-supplied intent list).
- **`CircleAI.Vision.Cloud`** — cloud image generation. New
  `IImageGenerator` contract (CircleAI.Vision is detection-only). DALL-E
  + Stability AI implementations lifted from Concierge, plus an
  `ImageGeneratorFallbackChain`.
- **`CircleAI.Plugins`** — plugin host + marketplace + lifecycle, lifted
  from CircleUp. Per-plugin collectible `AssemblyLoadContext`, JSON
  registry with declarative `workspace.read` / `workspace.write` /
  `events.subscribe` permissions, marketplace catalog, hot-reload via
  `ReloadAsync`. CircleUp's vault-specific event bus generalised to a
  string-keyed `IPluginEvents`.
- **`CircleAI.Hosting.Mcp`** — MCP (Model Context Protocol) JSON-RPC 2.0
  endpoint lifted from CircleUp's `MapMcpApi`. Vault tool surface
  generalised: hosts register `IMcpTool` + `IMcpResourceProvider` via
  DI, the substrate handles `initialize` / `tools/list` / `tools/call` /
  `resources/list` / `resources/read`. Single + batch requests; pure-DI
  `DispatchAsync` entry point makes it testable without ASP.NET Core.
- **`CircleAI.Hosting.Multiplayer`** — SignalR collaboration hub lifted
  from CircleUp's `CollabHub`. "Note" generalised to "document";
  `IOwnerProvider` replaced with `IMultiplayerPeerIdentity`. Per-doc
  rooms with last-writer-wins on the body, live cursor positions, and
  presence. Covers the 95 % case without a CRDT port.

### Changed

- Version-aligned every 3.x package from 3.1.0 → 3.2.0 (50 packages).
- Fixed a latent DI bug in
  `CircleAI.Hosting.CloudFallback.ServiceCollectionExtensions`: the
  options factory was being registered as `Func<IServiceProvider, T>`
  instead of `T`, so `Add*ChatGenerator` calls would have failed at
  resolution time. Unwrapped via `sp => optionsFactory(sp)` for OpenAI,
  Anthropic, Gemini; same pattern applied to `Speech.Cloud` and
  `Vision.Cloud`.

### Tests

- `Circle32ProactiveTests` — 20 tests covering cron parsing,
  refresh / tick / event / RunById paths, multi-tenant context separation.
- `Circle32CloudFallbackTests` — 11 tests covering each provider's
  metadata + fail-soft no-key behaviour + chain skipping / sentinel
  detection / order preservation.
- `Circle32SpeechCloudTests` — 14 tests covering Whisper + TTS
  fail-soft, PCM defaults, regex intent matching, capture extraction,
  trim + implicit-group filtering.
- `Circle32VisionCloudTests` — 13 tests covering each generator's
  metadata + fail-soft + null + fallback chain configuration paths.
- `Circle32PluginsTests` — 18 tests covering events pub/sub + dispose,
  registry round-trip + persistence + permission grant/revoke + uninstall,
  marketplace parsing, permissioned context gating, loader empty-folder
  safety.
- `Circle32McpTests` — 15 tests covering initialize, notifications,
  tools/list, tools/call (success + tool-level error + unknown +
  missing-name), resources/list + read (success + unknown scheme +
  missing uri + not-found), unknown method, malformed request.
- `Circle32MultiplayerTests` — 9 tests covering guest identity defaults
  + colour stability + uniqueness + default value, static rev/peers
  helpers.

All 100 new tests pass on both `net9.0` and `net10.0`.

## [3.1.0] — 2026-06-18 — **Video pillar foundation**

Adds a new contract surface — `CircleAI.Video` — for short-form
text-to-video generation, plus the device-fit gates that let the
BestFit selector surface video-capable models *only* on devices that
can actually run them.

The driving use case is **txtMe Video Mail**: sender video-calls, no
answer, types a text message; the recipient's on-device B! (where
capable) renders the message as a short styled video — public-domain
or original-character voice — and plays it back. On phones that can't
honour the GPU budget, the toggle is hidden entirely; on phones with
a desktop peer reachable over AetherNet, the work is offloaded via
the RT-12 v2 cross-tier path that shipped in 2.7.0.

### Added

- New package **`CircleAI.Video`** (contracts only). Three interfaces:
  - `IVideoGenerator` — text + optional style + optional reference
    frame + optional audio track → short video (`mp4`).
  - `IStyleScript` — rewrite a user message in a chosen style's voice
    using the existing `IChatGenerator`. No new model needed for this
    leg — pure system-prompt work.
  - `IStyleReference` — registry of registered styles (public-domain
    illustrations, original-character renders, genre presets). Drives
    the host's style-picker UI and the generator's grounding lookup.
- Primitives — `StyleId`, `VideoResolution` (P480 / P720 / P1080),
  `StyleReferenceFrame`, `StyleAttribution`, `StyleReference`,
  `AudioTrack`, `VideoGenerationRequest` / `VideoGenerationResult`,
  `StyleScriptRequest` / `StyleScriptResult`.
- Null implementations — `NullVideoGenerator`, `NullStyleScript`,
  `InMemoryStyleReference`. The InMemoryStyleReference is genuinely
  production-suitable; the other two fail closed (empty result,
  pass-through text).
- **`ChatCapability.Video`** flag in `CircleAI.Inference`. Consumers
  declare `RequiredCapabilities |= ChatCapability.Video` and the
  selector finds an entry that satisfies it AND fits the device.
- **`ModelEntry.MinVramGb`** (nullable `double`) in
  `CircleAI.Core.ModelRegistryService`. Video models declare it;
  text-only models leave it null. Selector filters out entries the
  device can't satisfy.
- **`DeviceProbe.VramGb`** (nullable `double`) in `CircleAI.Core`.
  Populated by platform adapters (Metal device query on Apple, NVML
  on CUDA, Vulkan memory props on AMD/Intel, ActivityManager-derived
  on Android). The `Snapshot()` factory grew a new optional
  `vramGbOverride` parameter so wiring a host probe is one line.

### Real backends — shipping in 3.1.x

- **CogVideoX-2B** (THUDM / Zhipu AI) — 2B params, 6-second clips at
  720×480, INT8/FP8 quantisable, lives on ModelScope. Apache-2.0
  compatible. Same Chinese-sovereign family as Qwen/MNN. ONNX → MNN
  conversion is the inflight work.
- **LTX-Video distilled-2B** (Lightricks) — runner-up; faster than
  CogVideoX on the same hardware. Image-to-video mode built-in.

### Why the gate matters

CogVideoX-2B needs ~6 GB VRAM quantised. A phone has 2–8 GB *system*
RAM total. The MinVramGb gate is what makes the difference between
"feature is hidden on this device" and "feature crashes on first
tap." It's the same pattern as `MinRamGb` did for the text models —
just on a different memory axis.

### Versions

All 43 packages bumped to **3.1.0** (the 42 from the 3.0.1 line plus
the new `CircleAI.Video`).

### Tests

13 new contract-surface tests in
`tests/CircleAI.Tests/Circle31ContractTests.cs` covering the three
null implementations, the in-memory style catalogue, the new
capability flag, and the new MinVramGb / VramGb fields on
ModelEntry and DeviceProbe.

---

## [2.0.3] — 2026-06-17

Managed-only point release: tool-catalog contract skeleton (composio
pattern-port, MIT). Lets downstream consumers (circle-concierge,
Observer's IObserverTool wiring) start building `IToolProvider`
implementations against a stable contract while 2.5.0 brings the full
catalog (semantic search + bundled SaaS integrations + optional
Composio adapter).

### Added

- **`CircleAI.Hosting.Tools` namespace.** Contracts + default in-memory
  implementation, fresh Apache 2.0 code (composio architecture is the
  reference; we don't vendor their SDK).
  - `ToolDescriptor` record — Name / Description / Provider / JsonSchema /
    AuthScheme / Tags / Examples.
  - `ToolExecutionResult` record — Success / Result / Error / DurationMs.
  - `IToolCatalog` — Upsert / Get / List / Search / ListByProvider.
  - `IToolProvider` — DiscoverAsync + IsAvailableAsync (vendor / MCP /
    AetherNet / optional Composio adapter).
  - `IToolExecutor` — schema-validated dispatch surface.
  - `InMemoryToolCatalog` — keyword-substring search with
    name-weighted scoring; thread-safe via `ConcurrentDictionary`.
  - `ToolCatalogExtensions.ImportFromAsync` — drain any provider's
    discovery into a catalog (call once at startup).

Semantic search and the bundled clean-room SaaS integrations (Gmail,
Slack, GitHub, Drive, Discord) land with 2.5.0; the optional
`CircleAI.Tools.Composio` adapter ships as a separate companion
package.

### Versions

All 8 packages bumped uniformly to **2.0.3**.

### Tests

7 new (Upsert / Remove / List sort / ListByProvider / Search ranking /
Search topK / ImportFromAsync).

---

## [2.0.2] — 2026-06-17

Managed-only point release: skill-pack auto-import on host start.

### Added

- **`SkillPackSource`** — declarative source record (Name / RepoUrl /
  GitRef / License / SkillSubdir / EstimatedSkillCount /
  IsDefaultEnabled / DefaultTags).
- **`KnownSkillPacks`** — default catalogue of 8 packs:
  - `bhengubv/awesome-agent-skills` (1000+, Apache 2.0)
  - `mukul975/Anthropic-Cybersecurity-Skills` (754, Apache 2.0 — MITRE / NIST / ATLAS / D3FEND / AI RMF)
  - `mukul975/Privacy-Data-Protection-Skills` (282, Apache 2.0 — GDPR / CCPA / EU AI Act / HIPAA / LGPD / PIPL / DPDP)
  - `bhengubv/Claude-BugHunter` (51, Apache 2.0)
  - `bhengubv/last30days-skill` (1, MIT)
  - `bhengubv/eduba-brand` (1, pattern-port — Eduba brand voice + tokens)
  - `bhengubv/career-ops` (14, MIT — default-disabled, awaits 2.0.3 format adapter)
  - `bhengubv/build-your-own-x` (educational corpus — default-disabled, awaits synthesiser)
- **`IPackDownloader`** + `HttpPackDownloader` — GitHub-tarball fetcher
  using `System.Formats.Tar`. Caches under
  `%LOCALAPPDATA%/CircleAI/skill-packs/` with a configurable TTL
  (default 7 days). Tests substitute `FakePackDownloader`.
- **`SkillPackSourcesOptions`** + **`SkillPackAutoImporter`** —
  `ImportEnabledAsync(onError, ct)` walks every default-enabled or
  explicitly-enabled pack, calls `SkillPackLoader.ImportAsync`, returns
  one `SkillPackManifest` per successfully-imported pack. Per-pack
  failure surfaces through `onError`; the rest still import.

### Compatibility

Non-breaking. The auto-importer is opt-in — existing 2.0.1 callers that
don't construct a `SkillPackAutoImporter` see no behaviour change.

### Versions

All 8 packages bumped uniformly to **2.0.2**.

### Tests

5 new tests (12 total in the SkillPack* suite). Full suite stays green
on net9.0 + net10.0.

---

## [2.0.1] — 2026-06-17

Managed-only point release covering the three 2.0.x follow-up items
from the runtime-2.0 roadmap, plus the macOS native dylibs for RT-09b.

### Added

- **(2.0.1) Skill pack harness.** `CircleAI.Skills` gains
  `SkillPackLoader` — Claude Code-format `SKILL.md` parser + importer
  over the existing `ISkillStore`. Compatible with
  `bhengubv/Claude-BugHunter` (51 hunting skills) and
  `bhengubv/awesome-agent-skills` (1000+ community skills). Walks a
  pack directory recursively, parses YAML frontmatter + markdown body,
  stamps a `pack:<name>` tag, returns a `SkillPackManifest`.

- **(2.0.2) Generative UI plug point.** `CircleAI.Hosting.GenerativeUI`
  exposes `IGenerativeUIRenderer` + a strict catalog-validated
  `JsonRenderParser`. Pattern adopted from `bhengubv/json-render`: the
  LLM emits a JSON tree, the parser validates against a `UiCatalog`
  (card / list / button / textBlock / image by default), the host
  renders into native UI (MAUI controls, HTML, terminal, etc.).
  Includes a `DescribeCatalogForPrompt` helper that produces the
  system-prompt constraints.

- **(2.0.3 / RT-12 v1) Mesh capability discovery.**
  `CircleAI.AetherNet` gains `MeshCapabilityAdvertisement` +
  `IMeshCapabilityRegistry` + `InMemoryMeshCapabilityRegistry`. Peers
  publish what they have loaded ("Qwen3-1.7B with 2048 free KV tokens
  on a Phone tier"); the registry queries by model + min-free-KV +
  staleness. v1 is contracts + in-memory; the AetherNet transport
  binding lands with RT-12 v2 actual offload in 2.7.0.

- **(RT-09b cross-build progress)** `turbovecbridge` Rust cdylib now
  ships for `osx-arm64` + `osx-x64` in addition to `win-x64` (3 of 8
  RIDs). Linux + Android + iOS queued for the next build-server pass.

### Changed

- All 8 packages bumped uniformly to **2.0.1**: Core, Inference,
  Hosting, Hosting.InferenceBridge, Inference.Server, Embeddings.Local,
  Skills, AetherNet.

### Tests

23 new tests (7 SkillPackLoader + 8 JsonRenderParser + 8
MeshCapabilityRegistry). Full suite stays green on net9.0 + net10.0.

---

## [3.0.1] — 2026-06-17 — **Naming + metadata cleanup**

### Renamed

- **`CircleAI.MediaServer` → `CircleAI.MediaHub`** (Plex + beatsync media-server contracts).
- **`CircleAI.Guardrails` → `CircleAI.ContentPolicy`** (Sponsio refusal/audit/injection contracts).

Both old names existed only as a workaround for collisions with the v1.2.0 personal-safety / content-production domain packs (`CircleAI.Safety`, `CircleAI.Media`). `CircleAI.MediaServer 3.0.0` and `CircleAI.Guardrails 3.0.0` are on nuget.org and stay there (the API key scope is push-only — no unlist), but nothing newer ships under those IDs. New consumers: `dotnet add package CircleAI.MediaHub`, `dotnet add package CircleAI.ContentPolicy`.

### Fixed

- Description-prefix drift across all 42 csprojs — every `(2.X.0)` historical prefix tag in the `<Description>` field normalized to `(3.0.1)`.

### Versions

All 42 packages bumped to **3.0.1**.

---

## [3.0.0] — 2026-06-17 — **The contingency runtime**

**The strategic 3.0.0 release.** Five new packages close the loop on
the "if Claude Code / Codex / Cursor get banned, CircleAI is what we
have left" doctrine. The cornerstone is **`CircleAI.DevTools`** — the
contract surface a Geek-Network IDE or agent shell binds to.

### Breaking

None. 3.0.0 is purely additive over 2.9.0; all 2.x packages are bumped
to 3.0.0 alongside the new ones. SemVer major bump signals the
strategic-doctrine shift from "mobile runtime" to "complete sovereign
AI stack". Consumers can upgrade 2.9.0 → 3.0.0 with no code changes.

### Added

- New package **`CircleAI.Research`** (the_well + UnifiedFieldTheory + arxiv):
  `IResearchCorpus`, `IPaperRetrieval`, `ICitationGraph`.
- New package **`CircleAI.Games`** (flame + Doom.Mobile):
  `IGameLoop`, `IInputMap`, `ISceneGraph`.
- New package **`CircleAI.AutonomousBiz`** (show-me-the-money pattern):
  `ITreasury`, `IRevenueLoop`, `IDecisionLog`.
- New package **`CircleAI.CodeUnderstanding`** (Understand-Anything pattern):
  `ICodeIndexer`, `ICodeSearch`, `ISymbolGraph`.
- New package **`CircleAI.DevTools`** — **the cornerstone**:
  `ICodeEditor`, `IInlineSuggester`, `IAgentShell`, `IPatchPlanner`, `IRefactorTool`.

### Versions

All 41 packages bumped to **3.0.0**.

### Tests

14 new contract tests. **174 total contract-surface tests across the 2.x → 3.0 line.**

### Doctrine

CircleAI is now a complete sovereign AI runtime: inference, hosting,
networking, vision, speech, on-device specialists, full server-farm
operational tooling, banking, markets, pipelines, workflows, CRM,
build farm, micro-agents, scientific research, autonomous business,
code understanding, and the IDE / agent-shell contract surface itself.

If the West pulls Claude Code, Codex, Cursor — the Geek Network can
ship its own replacement on top of CircleAI 3.0.0 without re-architecting
anything. **Nothing was left on the table.**

---

## [2.9.0] — 2026-06-17

**"DevOps + build-farm — the final 2.x release"**. Eight new packages
close out the 2.x line: build farm, dep-bot, doc analytics, snapshot
testing, distribution, media server, Windows automation, micro-agents.

CircleAI 2.x is now feature-complete as a contingency runtime — if
Western dev tools (Claude Code, Codex, Cursor) get banned, CircleAI
covers the surface from inference through devops, banking, markets,
workflows, and CRM. Real backends will land in the 2.x.1 dot-releases.

### Added

- New package **`CircleAI.BuildFarm`** (OSX-KVM + macos): `IBuildAgentPool`, `IBuildJobRunner`, `IBuildArtifactStore`.
- New package **`CircleAI.DepBot`** (renovate): `IDependencyAnalyzer`, `IDependencyUpdater`.
- New package **`CircleAI.DocAnalytics`** (papermark): `IDocumentTracker`, `IDocumentInsights`.
- New package **`CircleAI.Testing`** (Verify): `ISnapshotComparer`, `IGoldenStore`.
- New package **`CircleAI.Distribution`** (FileSync over AetherNet): `IFileSync`, `IPeerAdvertiser`.
- New package **`CircleAI.MediaServer`** (pms-docker-plex + beatsync): `IMediaLibrary`, `ISyncedPlayback`. Named with "Server" suffix to avoid collision with the v1.2.0 `CircleAI.Media` content-production domain pack.
- New package **`CircleAI.WindowsAutomation`** (mcp-windows-automation): `IUiAutomationDriver`.
- New package **`CircleAI.MicroAgents`** (picoclaw + hermes-desktop-os1): `IMicroAgent`, `IMicroAgentHost`.

### Versions

All 36 packages bumped to **2.9.0**.

### Tests

14 new contract tests (160 total contract-surface tests across the 2.x line).

### 2.x line wrap-up

Across 2.0 → 2.9 we shipped **36 packages** covering: inference,
hosting, networking, skills, vision, speech, domain specialists, tools
catalog, inputs, spatial, observer, guardrails, model-alignment, server
farm, observability, k8s operator, spec-driven dev, banking, markets,
pipelines, workflows, visualization, collaboration, CRM, build farm,
dep-bot, doc analytics, snapshot testing, distribution, media server,
Windows automation, and micro-agents. Nothing was left on the table.

Real backends (`*.csproj` description fields point at 2.x.1) land
opportunistically; the contract surface is stable and downstream
consumers can compile against it today.

---

## [2.8.0] — 2026-06-17

**"Server domain packs"**. Seven new packages — banking, markets,
pipelines, workflows, visualization, collaboration, CRM. Lets a host
build an AI that operates inside its line-of-business stack.

### Added

- New package **`CircleAI.Banking`** (OBP-API + fineract + hyperswitch):
  `IAccountReader`, `ILedgerWriter`, `IPaymentProcessor`.
- New package **`CircleAI.Markets`** (OpenBB + StockSharp):
  `IMarketDataFeed`, `IInstrumentCatalog`, `IOrderRouter`.
- New package **`CircleAI.Pipelines`** (etl + airbyte + mysql-mcp + postgres-mcp):
  `IPipelineSource`, `IPipelineSink`, `IPipelineExecutor`, `IDatabaseQueryTool`.
- New package **`CircleAI.Workflows`** (restate + automatisch + paca):
  `IWorkflowDefinitionStore`, `IWorkflowRunner`, `IWorkflowState`.
- New package **`CircleAI.Visualization`** (superset + scalar + webstudio):
  `IDashboardDefinitionStore`, `IApiDocBuilder`, `ISiteBuilder`.
- New package **`CircleAI.Collaboration`** (mattermost pattern-port):
  `IChannelStore`, `IMessageStore`, `IPresence`.
- New package **`CircleAI.CRM`** (twenty pattern-port):
  `IContactStore`, `IDealPipeline`, `IActivityLog`.

### Versions

All 28 packages bumped to **2.8.0**.

### Tests

14 new contract tests.

---

## [2.7.0] — 2026-06-17

**"Server-farm tier"**. Four new packages bring the runtime to the
"CTO/CIO can deploy this in our datacenter" doctrine — multi-tenant
routing, gRPC + batch + sharding contracts, OTel observability,
k8s operator (kagent), and spec-driven scaffolding.

### Added

- New package **`CircleAI.Inference.Server.Enterprise`**:
  - `ITenantRouter` (per-tenant quotas + node choice)
  - `IBatchScheduler` (coalesce small requests)
  - `IModelShardPlanner` (very-large-model sharding)
  - `ICrossTierOffload` (RT-12 v2 — phone borrows server brain)
  - `ServerTier` enum (SingleNode / Server / ServerFarm)
- New package **`CircleAI.Observability`**:
  - `IMetricSink` (OTel + Prometheus)
  - `ITraceSink` (OTel spans)
  - `IDashboardPublisher` (Grafana + claude-team-dashboard)
- New package **`CircleAI.Operator`** (kagent pattern):
  - `IModelOperator` (reconcile CRDs)
  - `IDeploymentObserver` (lifecycle events)
- New package **`CircleAI.SDD`** (spec-kit pattern-port):
  - `ISpecificationStore`
  - `ISpecificationValidator`
  - `ISpecToScaffold` (codegen)

### Versions

All 21 packages bumped to **2.7.0**.

### Tests

12 new contract tests.

---

## [2.6.0] — 2026-06-17

**"Observer + Guardrails + ModelAlignment"**. Three new packages.

**Observer** is a pattern-port of `bhengubv/Observer` (AGPL upstream
→ rewritten fresh under Apache 2.0 per the
`feedback_no_license_means_pattern_port.md` rule). On-device
perceive-reason-act loop with pluggable sensors and a tool registry.

**Guardrails** is a Sponsio-pattern-adoption — separate from the
existing personal-safety domain pack `CircleAI.Safety`. Naming chosen
to avoid collision with the v1.2.0 `CircleAI.Safety` domain package.

**ModelAlignment** is an OBLITERATUS pattern-port: targeted
abliteration toolkit + audit-publish-gate so abliterated weights
cannot be accidentally shipped upstream.

### Added

- New package **`CircleAI.Observer`** (Apache 2.0 fresh write):
  - `ISensor` (camera / mic / GPS / phone-state)
  - `IObservationToolbox` (in-memory default ships)
  - `IObservationLoop` (perceive → reason → act tick)
- New package **`CircleAI.Guardrails`** (Sponsio pattern-adoption):
  - `IContentFilter` + `IRefusalPolicy` + `IPromptInjectionDetector`
  - `ISafetyAuditLog`
  - All Null impls fail-closed.
- New package **`CircleAI.ModelAlignment`** (OBLITERATUS pattern-port):
  - `IAlignmentToolkit` (apply / revert / list)
  - `IAlignmentAuditor` (asserts ok-to-publish)

### Versions

All 17 packages bumped to **2.6.0**: Core / Inference / Hosting /
Hosting.InferenceBridge / Inference.Server / Embeddings.Local /
Skills / AetherNet / Vision / Speech / Domain / Tools.Catalog /
Inputs / Spatial / **Observer (new)** / **Guardrails (new)** /
**ModelAlignment (new)**.

### Tests

10 new contract tests.

---

## [2.5.0] — 2026-06-17

**"Tools.Catalog full + Inputs + Spatial"**. Three new packages.
Tools.Catalog adds the missing "+1000 tools" surface around the
lightweight `IToolCatalog` shipped in 2.0.3 — provider directory,
credential store, OAuth2 flow driver, quota guard, namespace store.
Inputs covers URL / HTTPS / video / MCP-side scrape / terminal-cast.
Spatial covers map tiles / radar / sky / 3D-scenes.

### Added

- New package **`CircleAI.Tools.Catalog`** (composio pattern-port):
  - `IProviderCatalog` (list / get / semantic search)
  - `ICredentialStore`
  - `IOAuth2FlowDriver`
  - `IQuotaGuard`
  - `IToolNamespaceStore`
- New package **`CircleAI.Inputs`**:
  - `IWebScraper` (ConvertX pattern)
  - `IStealthHttpClient` (Scrapling pattern)
  - `IVideoIngest` (openvid)
  - `IMcpWebScrape`
  - `ITerminalCast` (ASCILINE pattern)
- New package **`CircleAI.Spatial`**:
  - `IGeoTileSource` (deck.gl + cesium)
  - `IRadarReadout` (RADAR)
  - `ISkyTracker` (skylight)
  - `I3DSceneRenderer` (flame + anime)

### Versions

All 14 packages bumped to **2.5.0**: Core / Inference / Hosting /
Hosting.InferenceBridge / Inference.Server / Embeddings.Local /
Skills / AetherNet / Vision / Speech / Domain / **Tools.Catalog (new)** /
**Inputs (new)** / **Spatial (new)**.

### Tests

12 new contract tests.

---

## [2.4.0] — 2026-06-17

**"Domain specialists — contract surface"**. New `CircleAI.Domain` pack
covering 9 specialist plug points: Food (EPICure), Finance (quant-mind),
FinancialAgent (dexter), Presentations (presenton), JobSearch
(career-ops → TheJobCenter), Memory.MemPalace, Memory.HippoRAG (NeurIPS
'24 / ICML '25), Swarm (MiroFish over AetherNet), Identity.LoRA (RT-10).
Null implementations ship out of the box. Real backends land in 2.4.1.

### Added

- New package **`CircleAI.Domain`**:
  - Food — `IFoodEmbeddings` (EPICure)
  - Finance — `IFinanceRetrieval` (quant-mind), `IFinancialAgent` (dexter)
  - Presentations — `IPresentationGenerator` (presenton)
  - Job search — `IJobSearchPipeline` (career-ops; powers TheJobCenter)
  - Memory — `IMemPalaceStore`, `IHippoRagStore`
  - Swarm — `ISwarmCoordinator` (MiroFish over AetherNet)
  - Identity — `IPersonalLoRA` (RT-10, conditional)
  - Null implementations for all 9; fail-safe defaults.

### Versions

All 11 packages bumped to **2.4.0**: Core / Inference / Hosting /
Hosting.InferenceBridge / Inference.Server / Embeddings.Local /
Skills / AetherNet / Vision / Speech / **Domain (new)**.

### Tests

9 new contract tests.

---

## [2.3.0] — 2026-06-17

**"Speech + OCR pack — contract surface"**. New `CircleAI.Speech`
package: `ISpeechRecognizer` (ASR) + `ISpeechSynthesizer` (TTS) +
`IWakeWordDetector` (KWS for "Hey B") + `IOpticalCharacterRecognizer`
(OCR). Null implementations ship out of the box. Real backends —
FunASR + yapsnap + ChatTTS + Hey-Snips-pattern KWS + PaddleOCR — land
in **2.3.1**.

### Added

- New package **`CircleAI.Speech`**:
  - Primitives — `TranscribedSegment`, `TranscriptionResult`,
    `SynthesisResult`, `OcrResult`, `OcrTextBlock`, `WakeWordEvent`.
  - Contracts — `ISpeechRecognizer`, `ISpeechSynthesizer`,
    `IWakeWordDetector`, `IOpticalCharacterRecognizer`.
  - Null implementations — fail-closed defaults; recogniser returns
    empty text, synthesiser returns zero-length buffer, OCR returns
    empty blocks, wake detector subscribes successfully but never
    fires.

### Versions

All 10 packages bumped to **2.3.0**: Core / Inference / Hosting /
Hosting.InferenceBridge / Inference.Server / Embeddings.Local /
Skills / AetherNet / Vision / **Speech (new)**.

### Tests

6 new contract tests.

---

## [2.2.0] — 2026-06-17

**"Vision pack — contract surface"**. New `CircleAI.Vision` package
ships every IFace* / IDocumentVerifier / IPlateRecognizer /
IBluetoothAnomalyDetector interface plus fail-closed Null
implementations. Real backends — compv (CV foundation), facex, the
FaceLivenessDetection-SDK, KYC-Documents-Verif-SDK, ultimateALPR-SDK,
Bluehound — land in **2.2.1** when the C++ SDKs are vendored under
`native/<sdk>/`. Lets the integrators (PhonePin biometric auth, Sdpkt
wallet KYC, TagMe / Panik vehicle features, AetherNet adversary
detection) build against the surface today.

### Added

- **New package: `CircleAI.Vision`**.
  - Primitives — `BoundingBox`, `LandmarkPoint`, `DetectedFace`,
    `FaceEmbedding`, `LivenessResult`, `DocumentField`,
    `DocumentVerificationResult`, `PlateRecognitionResult`,
    `BluetoothAnomaly`.
  - Contracts — `IComputerVisionRuntime`, `IFaceDetector`,
    `IFaceEmbedder`, `IFaceLivenessDetector`, `IDocumentVerifier`,
    `IPlateRecognizer`, `IBluetoothAnomalyDetector`.
  - Null implementations — fail-closed defaults for every contract
    (`Null*` singletons / `new NullFaceEmbedder(dim)` for the
    parameterised case). Liveness + DocumentVerifier return
    `IsLive: false` / `IsValid: false` with explanatory warnings so
    nothing "passes" on absence-of-backend.

### Versions

All 9 packages bumped uniformly to **2.2.0**: Core / Inference /
Hosting / Hosting.InferenceBridge / Inference.Server /
Embeddings.Local / Skills / AetherNet / **Vision (new)**.

### Tests

8 new contract tests; full suite passes on net9.0 + net10.0.

---

## [2.1.0] — 2026-06-17

**"Native lift + HNSW (managed half)"** — ships every item that has
landed without native mnnbridge C++ work. The native-attention items
(RT-01 / RT-03 / RT-05) and RT-14 airllm move to **2.1.1**, which gates
on a focused mnnbridge cross-build session. The catalog additions
(Qwen3-Coder / DeepSeek-Coder-V2-Lite) gate on the recalibrator + bundle
downloads and ship in **2.1.2** when both finish.

### Added

- **RT-07 Predictive warmup pool.** New `CircleAI.Hosting.Warmup`
  namespace:
  - `IRequestPredictor` + `ArrivalForecast` record (probability /
    expected count / confidence).
  - `HistogramRequestPredictor` — per-minute EWMA over rolling 7-day
    window; Poisson-tail probability; confidence rises with sample
    count.
  - `PredictiveWarmupOptions` — Enabled / PollInterval / ForecastWindow
    / WarmupThreshold / MinTimeBetweenWarmups.
  - `PredictiveWarmupController` — background loop polls the predictor
    every 30 s by default; when `ProbabilityOfArrival × Confidence`
    clears the threshold, calls `IAIService.PrewarmAsync`. Throttled
    by `MinTimeBetweenWarmups`. All local-only — no telemetry, no
    upload.
  - `IAIService.PrewarmAsync` — new method with default impl that
    calls `StartAsync`; concrete `AIService` overrides to re-run the
    existing warm-up generation when already started.

- **RT-09b turbovec HNSW backend — 7 of 8 RIDs shipped.** Native
  `turbovecbridge` cdylib now ships for:
  - `win-x64` (MinGW, since 2.0.0)
  - `osx-arm64` (MacInCloud, since 2.0.1)
  - `osx-x64` (MacInCloud, since 2.0.1)
  - `linux-x64` (.201, this release — patched away the
    optional OpenBLAS dep)
  - `ios-arm64` (MacInCloud + Xcode iOS SDK, this release)
  - `android-arm64` (MacInCloud + NDK r26d, this release)
  - `android-x64` (MacInCloud + NDK r26d, this release)
  
  Only `linux-arm64` remains — pending an ARM Linux box / qemu-user
  setup. Ships in **2.1.1**.

### Carries forward

- 2.0.3 Tools.Catalog contract skeleton (`IToolDescriptor` /
  `IToolCatalog` / `IToolProvider` / `IToolExecutor` /
  `InMemoryToolCatalog`) — pattern-port from composio under Apache 2.0.
- 2.0.2 skill-pack auto-import (~2,090 hosted skills across 6
  default-enabled packs).
- 2.0.1 SkillPackLoader + Generative UI plug point + Mesh capability
  discovery v1.
- 2.0.0 RT-04 brownout + RT-08 fallback chain + RT-09 embeddings store.

### Deferred (no scope cuts — just sequencing)

| Item | New target | Reason |
|---|---|---|
| RT-01 Tiered KV cache | 2.1.1 | Needs C++ in `native/mnn-bridge/src/mnnbridge.cpp` |
| RT-03 mmap weight loading | 2.1.1 | Needs C++ patch to `MNN::Express::Module::load` |
| RT-05 Speculative decoding | 2.1.1 | New mnnbridge entrypoint required |
| RT-14 airllm layer-stream | 2.1.1 | Algorithmic port + native MNN extension |
| shard pattern-port (RT-01 alt) | 2.1.1 | Same MNN attention-path work |
| Catalog: Qwen3-Coder / DeepSeek-Coder-V2-Lite / Qwen3-Draft | 2.1.2 | Recalibrator runs + multi-GB bundle downloads |
| Turbovec `linux-arm64` | 2.1.1 | Needs ARM Linux host or qemu-user (sudo blocked on .201) |

### Versions

All 8 packages bumped uniformly to **2.1.0**. Native `turbovecbridge`
cdylib unchanged from 2.0.x (still ABI v1).

### Tests

29 new tests across the 2.0.x → 2.1.0 ladder (12 SkillPack* + 8
JsonRender + 8 MeshCapabilityRegistry + 5 SkillPackAutoImporter + 7
PredictiveWarmup + 7 InMemoryToolCatalog + others). Full suite passes
on net9.0 + net10.0.

---

---

## [2.0.0] — 2026-06-16

The first major bump in the runtime-2.0 programme. 2.0.0 lands the managed
side of three "cheap-phone tier" features: a fallback chain across the
embedded model catalog, brownout hot-swap under memory pressure, and an
on-device embeddings store with built-in RAG primitives. The remaining
runtime-2.0 features (RT-01 tiered KV, RT-03 mmap, RT-05 speculative decode,
RT-10 LoRA) need native mnnbridge cross-builds and ship in **2.1.0** once
those binaries are in.

### Added

- **RT-08 Multi-tier fallback chain.** `ModelEntry` gains `FallbackModelId`
  (chain-head pointer) and `MemoryHintBytes` (RAM estimate for brownout
  sizing). `IModelSelector` exposes `ChainFor(headModelId)` returning
  `[head, smaller, smaller, …]`; `DeviceAwareModelSelector` walks the
  registry transitively with self-/cycle-break. The embedded catalog ships
  the chain stamped across both Qwen3 (14B → 8B → 4B → 1.7B → 0.6B) and
  Qwen2.5 (7B → 3B → 1.5B → 0.5B) families with per-entry `QualityRank`
  and `MemoryHintBytes`.

- **RT-04 Adaptive brownout under pressure.** New `IMemoryPressureSource`
  contract surfaces a coarse 3-level pressure signal (Normal/Trim/Critical)
  with `Current` + `Subscribe(handler)`; `NullMemoryPressureSource` is the
  default no-op and `ManualMemoryPressureSource` is the test-/hosting-driven
  implementation. `AIService` gains a new constructor overload that takes
  the source and a public `BrownoutAsync(BrownoutReason)` method: it
  cancels in-flight generations, disposes the current generator, resolves
  the next entry in the chain, and reloads. `IAIObserver.OnBrownoutAsync`
  fires after a successful swap.

- **RT-09 Embeddings-as-a-Service.** New package
  `CircleAI.Embeddings.Local` ships `ICircleEmbeddingStore` (add / remove
  / search by text or vector / save / load) plus `IEmbeddingEncoder`
  (bring-your-own dense encoder) and the default
  `InMemoryEmbeddingStore` (brute-force cosine, TurboQuant-compressed
  payload at 4 bits/dim ~ 8× shrink vs FP32, durable single-file format).
  HNSW backend is on the 2.1.0 roadmap.

### Changed

- All packages bumped to **2.0.0**:
  CircleAI.Core / CircleAI.Inference / CircleAI.Hosting /
  CircleAI.Hosting.InferenceBridge / CircleAI.Inference.Server, plus the
  new CircleAI.Embeddings.Local 2.0.0.

### Compatibility

- Public-surface additions are non-breaking. Existing callers compile and
  run unchanged. The new `BrownoutAsync` and `ChainFor` default-impl
  patterns guarantee 1.7.0 consumers keep working.
- Native mnnbridge ABI is unchanged from 1.2.0; no new RIDs needed for
  2.0.0.

### Roadmap (2.1.0 — native-backed features)

These items are tracked but require the mnnbridge cross-build that does
not ship in 2.0.0:

- RT-01 Tiered KV cache (FP16 recent / TQ3 mid / TQ2 cold).
- RT-03 mmap weight loading.
- RT-05 Speculative decoding with curated draft models.
- RT-10 On-device LoRA personalisation.
- RT-07 Predictive warmup, RT-12 Mesh-offload v1 — managed work scheduled
  for 2.0.x point releases.

---

## [1.7.0] — 2026-06-13

The first release of the runtime-2.0 programme. 1.7.0 lands three features
that make conversations on cheap phones feel like a flagship-tier app.

### Added

- **RT-02 Live snapshot / restore.** `IChatGenerator.SaveSessionAsync(path)`
  + `LoadSessionAsync(path)` round-trip a compressed KV snapshot to disk.
  MAUI hosting wires `OnPaused` → snapshot automatically. Default
  implementations return `false` so non-MNN generators keep working.
- **RT-06 Cross-session prefix cache.** `GenerationOptions.UsePrefixCache`
  + `PrefixCacheService`: system-prompt prefill is snapshotted on first
  use, reloaded on every subsequent chat with the same `(model_id,
  system_prompt)` SHA-256. First-token latency drops to ~150 ms on a
  warm cache. Stored under `%LOCALAPPDATA%/CircleAI/prefix-cache/` with
  LRU eviction at 500 MB.
- **RT-11 Power-budget API.** New `PowerBudget` enum
  (`None|Low|Normal|High`) on `GenerationOptions.Budget`. The runtime
  maps the budget to a max-tokens cap, preferred KV mode, and (eventually)
  smaller model in the chain. `PowerBudgetPolicy.Resolve` auto-downgrades
  Normal → Low below 15 % battery and reads thermal state. Surfaces to
  the 9 portable ports too (Python, TS, Go, Kotlin, Swift, Rust, C,
  HarmonyOS, Android).

### Changed

- CircleAI.Inference                1.6.0 → **1.7.0**
- CircleAI.Hosting.InferenceBridge  1.3.0 → **1.4.0**
- CircleAI.Inference.Server         1.4.0 → **1.5.0**

### Fixed

- (1.6.0 → 1.7.0 carry-forward) MNN P/Invoke ABI is now 5-arg with a
  function-pointer streaming callback, eliminating the
  `[DisableRuntimeMarshalling]` fatal stack-corruption (`0xC0000005`) seen
  on the first streamed token.
- Per-handle `SemaphoreSlim(1,1)` serialisation in `QwenTextGenerator` and
  `KimiVlGenerator` — `mnnbridge.h` declares concurrent calls UB.
- `mnn_llm_reset_session` is called before every generation so
  back-to-back chats no longer leak state.
- `<think>…</think>` is now routed through `MnnTokenRouter` and surfaced
  as a separate `reasoning_content` field (o1 / DeepSeek-style).
- `DrainRemainder` no-ops after a stop sequence fires, so `<|im_end|>` and
  `<|endoftext|>` never leak into emitted content.

### Compatibility

- All additions are default-impl back-compat; existing callers compile
  and run unchanged.
- Native mnnbridge ABI: 1.2.0 (unchanged).

---

## [1.6.0 and earlier]

See git history.
