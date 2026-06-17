# Changelog — CircleAI

All notable changes to the CircleAI runtime are documented here. The format
is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased — 2.1.0 in progress]

Tracking the items shipping in the **"Native lift + HNSW"** release.
Code lands on master incrementally; the version bump + tag + GitHub
release + NuGet push happen together once every 2.1.0 item is in.

### Done

- **RT-09b turbovec HNSW backend.** New `IEmbeddingIndex` interface
  in `CircleAI.Embeddings.Local` + `TurboVecEmbeddingIndex` impl
  backed by the vendored turbovec Rust crate via the new
  `turbovecbridge` cdylib. New `HnswEmbeddingStore` honours the
  existing `ICircleEmbeddingStore` contract and routes search through
  turbovec's SIMD-blocked LUT path. Native lib loads on win-x64;
  Linux/Mac/Android/iOS cross-builds queued for the next build-server
  session. 11 new tests pass on net9.0 + net10.0.

### Pending

- RT-01 Tiered KV cache (FP16 / TQ3 / TQ2 per-token mode) — mnnbridge native.
- RT-03 mmap weight loading — mnnbridge native.
- RT-05 Speculative decoding — mnnbridge native.
- RT-07 Predictive warmup pool — managed.
- Catalog additions — Qwen3-Coder / DeepSeek-Coder-V2-Lite / Qwen3-Draft.
- Turbovec cross-build: linux-x64, linux-arm64, osx-x64, osx-arm64, android-arm64, android-x64, ios-arm64.

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
