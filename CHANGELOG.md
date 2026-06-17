# Changelog — CircleAI

All notable changes to the CircleAI runtime are documented here. The format
is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

## [Unreleased — 2.1.0 in progress]

Tracking the items still in flight for the **"Native lift + HNSW"** release.

### Done

- **RT-09b turbovec HNSW backend (managed + win-x64 + osx-* native).**
  See `[2.0.1]` above for the partial native ship.
- **RT-07 Predictive warmup pool.** `CircleAI.Hosting.Warmup` —
  `IRequestPredictor` + `HistogramRequestPredictor` (per-minute EWMA
  over rolling 7-day window) + `PredictiveWarmupOptions` +
  `PredictiveWarmupController`. Background loop polls the predictor at
  a configurable interval (default 30 s); when forecast
  `ProbabilityOfArrival × Confidence` clears the threshold (default
  0.5), calls the new `IAIService.PrewarmAsync` (default-impl on
  interface; concrete override in `AIService` re-runs the existing
  warm-up generation). Throttled by `MinTimeBetweenWarmups` (default
  5 min). All local-only — no telemetry, no upload. 7 new tests pass
  on net9.0 + net10.0.

### Pending

- RT-01 Tiered KV cache (FP16 / TQ3 / TQ2 per-token mode) — **needs
  C++ source work in mnnbridge.cpp**; cross-build comes after.
- RT-03 mmap weight loading — **needs C++ source work**.
- RT-05 Speculative decoding — **needs C++ source work**.
- RT-07 Predictive warmup pool — managed.
- Catalog additions — Qwen3-Coder / DeepSeek-Coder-V2-Lite / Qwen3-Draft.
- Turbovec cross-build: linux-x64, linux-arm64, android-arm64, android-x64, ios-arm64.

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
