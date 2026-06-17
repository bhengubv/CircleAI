# Changelog — CircleAI

All notable changes to the CircleAI runtime are documented here. The format
is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
