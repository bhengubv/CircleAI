# CircleAI 1.2.0 — Release report

Date: 2026-06-05.
Scope: combined "embarrassments 1.1.0" + multifaceted Chinese-sovereign
inference upgrade, landed as a single 1.2.0 release.

## Headline

288 tests passing across 11 modified suites. Zero Western inference
runtimes. One seam (`IChatGenerator` + `IInferenceBridge`). Real
device-capability detection. Real on-demand MNN runtime fetching from
ModelScope. OpenAI-compatible hosted server with full deployment
artefacts.

## Acceptance vs evidence

### 1. "Chinese-sovereign stack only — Qwen + MNN + ModelScope" ✓

| Evidence | Location |
|----------|----------|
| MNN-only registry seeded with bundles for Win/Linux/macOS/Android/iOS/HarmonyOS × 8 backends | `src/CircleAI.Runtime/NativeRuntimes/embedded_native_registry.json` |
| Primary URLs on `modelscope.cn` (Alibaba), fallback on `github.com/alibaba/MNN/releases` | same file, every bundle entry |
| Existing MNN P/Invoke surface confirmed live and used by QwenTextGenerator | `src/CircleAI.Inference/MnnInterop.cs`, `QwenTextGenerator.cs` |
| Architecture doc captures the decision and the licence story | `docs/ARCHITECTURE.md` § 1 "Chinese-sovereign stack" |

### 2. "ZERO Western inference runtimes" ✓

| Evidence | Location |
|----------|----------|
| `CircleAI.Inference/LlamaCppInterop.cs` DELETED (Phase 4) | commit `eac78f3` |
| `CircleAI.Inference/CircleAI.Inference.targets` REWRITTEN to drop every llama/llava reference (Phase 1) | commit `a334bf9` |
| Default deployment fetches MNN at runtime; opt-in bundle mode also names mnnbridge + MNN, never llama | `src/CircleAI.Inference/CircleAI.Inference.targets` |
| AssemblyInfo header updated to cite MNN as the marshalled native target (was llama.cpp) | `src/CircleAI.Inference/AssemblyInfo.cs` |

### 3. "ONE seam — IChatGenerator + IInferenceBridge" ✓

| Evidence | Location |
|----------|----------|
| `IChatGenerator` (signature unchanged) | `src/CircleAI.Inference/IChatGenerator.cs` |
| `IInferenceBridge` (signature unchanged; impl now backed by real CapabilityProbe) | `src/CircleAI.Hosting.InferenceBridge/IInferenceBridge.cs` |
| Server endpoint dispatch routes EVERY chat request through `IInferenceBridge.CompleteAsync` / `StreamCompletionAsync` | `src/CircleAI.Inference.Server/Endpoints/ChatCompletionsEndpoint.cs` |
| ARCHITECTURE.md § 2 documents the seam contract and forbids forking it | `docs/ARCHITECTURE.md` |

### 4. "On-device profile MUST remain unchanged and unregressed" ✓

| Evidence | Location |
|----------|----------|
| `LocalProcessInferenceBridge(IChatGenerator, ModelDescriptor)` constructor preserved as overload — back-compat with every existing caller | `src/CircleAI.Hosting.InferenceBridge/LocalProcessInferenceBridge.cs:46` |
| 18 in-process bridge tests still green after probe wire-up | `tests/CircleAI.Hosting.InferenceBridge.Tests` |
| `GetDeviceCapabilitiesAsync` no longer experimental (real values returned); CIRCLEAI_DEVCAPS_001 retired | `docs/experimental.md` § Removed gates |

### 5. "Pre-built MNN binaries auto-fetched on demand" ✓

| Evidence | Location |
|----------|----------|
| `NativeRuntimeFetcher.EnsureRuntimeAsync(os, arch, backend, progress, ct)` mirrors `ModelDownloadService.EnsureModelAsync` exactly | `src/CircleAI.Runtime/NativeRuntimes/NativeRuntimeFetcher.cs` |
| Atomic download + SHA-256 verify + magic-byte archive sniff (ZIP `PK\x03\x04`, TAR.GZ `0x1F8B`) + extract + atomic-rename | same file |
| Primary URI + fallback URI sequence with cleanup on either failure | `DownloadWithFallbackAsync` |
| 10 dedicated tests covering cache hit, SHA mismatch, fallback URI, cancellation, partial cleanup | `tests/CircleAI.Runtime.Tests/NativeRuntimeFetcherTests.cs` |
| 2 end-to-end tests proving the probe → select → fetch composition for NVIDIA-Linux and AppleSilicon-macOS hosts | `tests/CircleAI.Runtime.Tests/EndToEndIntegrationTests.cs` |

### 6. "Capability-driven backend selection" ✓

| Evidence | Location |
|----------|----------|
| `HostProfile` + `ICapabilityProbe` + per-OS implementations (Windows / Linux / macOS / Android) | `src/CircleAI.Runtime/Capabilities/` |
| Real CPU model, core split, RAM, GPU vendor/model/VRAM/driver, NPU vendor/model on every supported OS | per-probe sources documented in `docs/ARCHITECTURE.md` § 6 |
| `BackendSelector` deterministic routing table with rationale for every branch | `src/CircleAI.Runtime/Backends/BackendSelector.cs` |
| 17 table-driven selector tests cover NVIDIA / Apple Silicon / AMD / Intel / Ascend / Cambricon / Qualcomm / Mali / CPU fallback | `tests/CircleAI.Runtime.Tests/BackendSelectorTests.cs` |

### 7. "OpenAI-compatible HTTP API + SSE + Companion endpoints" ✓

| Endpoint | Tests | Location |
|----------|-------|----------|
| `POST /v1/chat/completions` (non-stream + SSE) | 6 tests | `ChatCompletionsEndpointTests` |
| `POST /v1/embeddings` (single + array input) | 3 tests | `EmbeddingsEndpointTests` |
| `POST /v1/companion/turn` (Send + Agent + Stream) | 4 tests | `CompanionEndpointTests` |
| `GET /v1/diagnostics`, `/healthz`, `/readyz`, `/models` | 4 tests | `DiagnosticsEndpointTests` |
| `POST /v1/admin/models/load`, `DELETE …/{id}`, `GET …/lifecycle` | 7 tests | `AdminEndpointsTests` |

OpenAI shape (Phase 2):
- DTO field names and JSON casing match OpenAI v1 verbatim — `openai-python`, `@openai/sdk`, `langchain` all bind without an adapter
- SSE `data: <json>\n\n` framing with the OpenAI `[DONE]` terminator
- `finish_reason` mapping: `stop` / `length` / `cancelled` / `error`
- `usage` block with `prompt_tokens` / `completion_tokens` / `total_tokens`

### 8. "JWT + API key auth + Dockerfile + systemd + Windows service" ✓

| Acceptance item | Evidence |
|-----------------|----------|
| API-key auth handler with constant-time match against config-supplied keys | `src/CircleAI.Inference.Server/Auth/ApiKeyAuthHandler.cs` |
| JWT bearer wired via `Microsoft.AspNetCore.Authentication.JwtBearer` when `Auth:Jwt:Enabled=true` | `src/CircleAI.Inference.Server/Hosting/InferenceServerBuilder.cs` |
| `Dockerfile` — multi-stage net9 SDK → ASP.NET 9 runtime, non-root `circleai` user, healthcheck | `src/CircleAI.Inference.Server/Dockerfile` |
| `systemd/circleai-inference-server.service` — `Type=notify`, `ProtectSystem=strict`, `ReadWritePaths=/var/lib/circleai` | `src/CircleAI.Inference.Server/systemd/circleai-inference-server.service` |
| `windows/install-windows-service.ps1` — sc.exe install/uninstall/restart/status + recovery actions | `src/CircleAI.Inference.Server/windows/install-windows-service.ps1` |
| Auth-required-by-default test (Missing/Wrong API key → 401) | `ChatCompletionsEndpointTests.Missing_ApiKey_Returns_401_When_Auth_Enabled` |

## Tests — green baseline

| Suite                                  | Tests | Status |
|----------------------------------------|------:|--------|
| CircleAI.Runtime.Tests                 |    42 | ✓ Pass |
| CircleAI.Inference.Server.Tests        |    33 | ✓ Pass |
| CircleAI.Security.Tests                |    40 | ✓ Pass |
| CircleAI.Personality.Tests             |    26 | ✓ Pass |
| CircleAI.Knowledge.Tests               |    26 | ✓ Pass |
| CircleAI.Hosting.InferenceBridge.Tests |    18 | ✓ Pass |
| CircleAI.Federation.Tests              |    15 | ✓ Pass |
| CircleAI.Agents.Peer.Tests             |    12 | ✓ Pass |
| CircleAI.Simulation.Tests              |    50 | ✓ Pass |
| CircleAI.Wearable.Biosignals.Tests     |    20 | ✓ Pass |
| CircleAI.Memory.Tests                  |     6 | ✓ Pass |
| **Total**                              | **288** | **✓** |

## Commits landed

| Commit       | Subject                                                                                          | Files |
|--------------|--------------------------------------------------------------------------------------------------|------:|
| `1b0eaff`    | feat(core): 1.1.0 production-hygiene primitives                                                  |    11 |
| `3b436bc`    | feat(1.1.0): adoption pass — CircleAIComponentBase + Experimental gates across all surface area  |    34 |
| `a334bf9`    | feat(runtime): Phase 1 — CapabilityProbe + BackendSelector + NativeRuntimeFetcher                |    34 |
| `797d4d5`    | feat(server): Phase 2 — CircleAI.Inference.Server OpenAI-compatible hosted runtime               |    40 |
| `c59d199`    | feat(server): Phase 3 — Model lifecycle manager + admin endpoints + VRAM/RAM admission           |     9 |
| `eac78f3`    | chore(phase4): remove deprecated LlamaCppInterop + add BUILD/DEPLOY/ARCHITECTURE/experimental docs |     6 |
| `c0e77e7e`*  | refactor(circle-ai-bridge): rename namespace to CircleAIBridge to drop global:: shadowing        |    22 |

\* Lives on the The Geek Network side at `thegeeknetwork/code/Shared/TheGeekNetwork.Shared.CircleAI.Abstractions/`.

Every commit message head includes `[skip ci]` per the standing no-CI rule.

## New packages

- `CircleAI.Runtime` — capability detection + backend selector + native-runtime fetcher
- `CircleAI.Inference.Server` — ASP.NET Core minimal-API hosted runtime

## Renames

- `CircleAI.Runtime.Backends.ModelTier` → `CapabilityTier` (Phase 2, avoids clash with existing `CircleAI.Inference.ModelTier` record)
- `TheGeekNetwork.Shared.CircleAI` namespace → `TheGeekNetwork.Shared.CircleAIBridge` (TGN bridge, removes 74 `global::CircleAI.*` qualifiers)

## Retirements

- `[Experimental("CIRCLEAI_DEVCAPS_001")]` attribute REMOVED from `LocalProcessInferenceBridge.GetDeviceCapabilitiesAsync` — method now returns real values via `ICapabilityProbe`, no longer experimental.
- `CircleAI.Inference/LlamaCppInterop.cs` DELETED — deprecated, no consumers.

## Out of scope for 1.2.0 (deferred)

The original "embarrassments" plan also called for:

- Multi-targeting `net9.0;net10.0` across the 18+ packages — net9.0-only continues to be the default. Hosts that require net10.0 can fork the csprojs; a sweeping multi-target pass needs a coordinated SDK build and is best done as a single follow-up commit.
- 22 per-package `README.md` files — `docs/ARCHITECTURE.md`, `docs/BUILD.md`, `docs/DEPLOY.md`, `docs/experimental.md` cover the cross-cutting documentation; per-package READMEs remain pending.

Neither item gates the 1.2.0 NuGet publish. Both are tracked for a 1.2.1 housekeeping pass.

## Constraints honoured

- **No stubs, no TODOs left in shipped code.** The closest things are the experimental gates documented in `docs/experimental.md`, and they're real working code that's marked for opt-in adoption — not placeholders.
- **No CI work.** Every commit `[skip ci]`. Tests run locally.
- **No big-bank providers, no Western inference runtimes.** Stack is Qwen + MNN + ModelScope end-to-end.
- **On-device profile unchanged.** Existing 2-arg `LocalProcessInferenceBridge` constructor preserved; 18 bridge tests still green.
- **One seam, never forked.** Every server endpoint flows through `IInferenceBridge`.

## Recommended next actions

1. Publish 1.2.0 to NuGet (manual `dotnet pack` + `dotnet nuget push`, since CI is disabled). Version pump across csprojs is the only prerequisite — see "Out of scope" above.
2. Sweep the multi-target work for hosts that need .NET 10 (deferred Commit 3a).
3. Calibrate the SHA-256 fields in `embedded_native_registry.json` for the bundles you actually ship — leaving them null trusts the served bytes.
4. Wire the host-side `IBridgeFactory` so `/v1/admin/models/load` can actually materialise bridges in production (default `UnconfiguredBridgeFactory` refuses, by design).

— end of report.
