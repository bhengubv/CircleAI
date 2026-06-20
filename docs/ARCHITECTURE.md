# CircleAI — Architecture

Last refreshed: 2026-06-18. Current line: **`3.0.1`**.

CircleAI is a portable, on-device + server AI SDK built around a single
inference seam, an Alibaba-MNN execution backend, and Qwen-family models
hosted on ModelScope. This document is the source of truth for those
decisions and the contracts that hold the rest of the codebase together.

---

## 0. The 3.0 doctrine

The 1.x and 2.x lines built a complete on-device + server AI runtime.
3.0 reframes the same codebase as a **sovereign-stack contingency**:

> *If Claude Code / Codex / Cursor get pulled from a market, CircleAI
> is the substrate a Geek-Network IDE / agent shell binds to instead.
> The cornerstone is `CircleAI.DevTools` — `ICodeEditor`,
> `IInlineSuggester`, `IAgentShell`, `IPatchPlanner`, `IRefactorTool`.
> Contracts ship today; implementations land in 3.0.x dot releases.*

This document captures the architectural decisions that hold across
both framings. The newer 3.0 contract surfaces (vision, speech, spatial,
banking, markets, workflows, devtools, etc.) sit on top of the inference
seam described in § 2 — they extend the codebase without changing the
foundational design.

---

## 1. The Chinese-sovereign stack

| Layer            | Choice                                | Why                                                                 |
|------------------|----------------------------------------|----------------------------------------------------------------------|
| Models           | Qwen 3.x / 3.5 / 3.6 + Kimi VL + DeepSeek + GLM | Open-weight, Apache-2.0 / Tongyi licence, top-tier Chinese-trained  |
| Engine           | Alibaba MNN (Apache-2.0)               | Multi-backend (CPU/CUDA/Vulkan/OpenCL/Metal/Ascend/Cambricon)        |
| Model registry   | ModelScope (primary), HuggingFace (fallback) | Sovereign mirror, ModelScope CDN                                |
| Native runtimes  | Pre-built MNN binaries, fetched on demand | No build-from-source step, no per-arch CI matrix                |
| Speech models    | SenseVoice (ONNX)                       | Chinese-trained ASR / TTS                                            |

There are **zero Western inference runtimes** in this codebase. llama.cpp,
vLLM, ONNX Runtime (LLM path), and TensorRT have all been removed.

If you find one creeping back in, refuse the PR.

---

## 2. The ONE seam: `IChatGenerator` + `IInferenceBridge`

All inference flows through two contracts. There are no parallel paths.

### `CircleAI.Inference.IChatGenerator`

The low-level "give me tokens for these messages" contract, implemented
by `QwenTextGenerator` over `MnnInterop`.

```csharp
public interface IChatGenerator : IDisposable
{
    Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? opts, CancellationToken ct);
    IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? opts, CancellationToken ct);
}
```

### `CircleAI.Hosting.InferenceBridge.IInferenceBridge`

The hosting contract — wraps one or more `IChatGenerator`s and adds
descriptors, capability reporting, and outcome classification.

```csharp
public interface IInferenceBridge
{
    Task<IReadOnlyList<ModelDescriptor>> ListLoadedModelsAsync(CancellationToken ct);
    Task<bool> IsModelLoadedAsync(string modelId, CancellationToken ct);
    Task<InferenceResponse> CompleteAsync(InferenceRequest request, CancellationToken ct);
    IAsyncEnumerable<string> StreamCompletionAsync(InferenceRequest request, CancellationToken ct);
    Task<DeviceCapabilities> GetDeviceCapabilitiesAsync(CancellationToken ct);
}
```

`LocalProcessInferenceBridge` is the in-process reference implementation.
Cross-process bridges (Binder on Android, XPC on macOS, named pipes on
Windows) wrap a `LocalProcessInferenceBridge` running inside a daemon.

---

## 3. Package map

CircleAI ships **132 csprojs** across three version tracks. The README
has the authoritative per-package list with descriptions; this section
captures the structural shape.

### Track 1 — 3.0 contract line (`3.0.1`)

**42 packages.** The trinity + the new sovereign-stack contract
surfaces. Most are contract + null-impl; backends land in dot releases.

```
Core hosting        Core, Inference, Hosting, Hosting.InferenceBridge,
                    Inference.Server, Inference.Server.Enterprise,
                    Maui, Skills, Embeddings.Local, AetherNet
Domain consolidator Domain — 9 plug-points (MemPalace, HippoRAG, Swarm,
                    Identity.LoRA, Food, Finance, FinancialAgent,
                    Presentations, JobSearch) in one NuGet ID
Vision              Vision (7 interfaces — CV runtime, face stack, doc
                    + plate verify, BLE anomaly)
Speech              Speech (4 interfaces — ASR, TTS, wake word, OCR)
Spatial             Spatial (4 interfaces — tile source, radar, sky,
                    3D scene)
Inputs + tools      Inputs, Tools.Catalog
Safety + alignment  ContentPolicy (was Guardrails), ModelAlignment
Observation + ops   Observer, Observability, Operator, SDD
Business apps       Banking, Markets, Pipelines, Workflows,
                    Visualization, Collaboration, CRM
DevOps              BuildFarm, DepBot, DocAnalytics, Testing,
                    Distribution, MediaHub (was MediaServer),
                    WindowsAutomation, MicroAgents
3.0 cornerstones    DevTools, Research, Games, AutonomousBiz,
                    CodeUnderstanding
```

### Track 2 — Mature foundation (`1.0.0` – `1.5.0`)

**6 packages.** Working production substrate the 3.0 contracts sit on.
Not stale — implementations live here.

```
Memory               1.3.0   Episodic + persona + affect + goal +
                             feedback stores. Hierarchical sleep-cycle
                             consolidator. Multimodal compression.
Orchestration        1.4.0   Host-side loki-mode agent orchestration
                             (`LokiOrchestrator`).
Agents.Peer          1.4.0   Peer-agent envelope + AgentBus correlation.
Aether               1.3.0   Aether-protocol contracts. Floats
                             upstream `bhengubv/aether-protocol`.
Security.AetherNet   1.1.0   AetherMesh.Security floating adapter.
Networking.AetherNet 1.0.0   AetherMesh.Transport floating adapter.
```

### Track 3 — Companion + adapters (`1.2.0`)

**84 packages.** Original 1.x companion stack and its lifestyle adapter
family. Working code; no version bump because the 3.0 ship added new
contract surfaces on top rather than re-stamping the base.

```
Companion core    Companion, Tools, Voice, Personality, Security,
                  Embeddings (older predecessor to Embeddings.Local)
Utilities         Search, Knowledge, Identity, Sync, Federation,
                  Runtime, Desktop, Web, Ambient, Accessibility
Networking        Networking + 9 transports (Http, WebSocket, Grpc,
                  Tcp, Mqtt, WiFi, Bluetooth, NearLink, Dtn)
Commerce          Commerce, Commerce.Accounting, Commerce.Finance,
                  Commerce.Integration.PayFast,
                  Commerce.Integration.Xero
Languages         Languages + Languages.Language + Languages.Translation
                  + 8 specific language adapters (Afrikaans, Amharic,
                  Arabic, Hausa, Portuguese, Sesotho, Swahili, isiZulu)
Lifestyle (~50)   Beauty, Faith, Fitness, Healthcare, RealEstate,
                  Tourism, Tradesperson, Elderly, Kids, Parenting,
                  Pets, Sports, … (full list under `src/`)
```

### Cross-language portable kernel

10-language ports (Rust / Go / Python / TypeScript / Kotlin / Swift / C /
Android / HarmonyOS-ArkTS) sit alongside the C# tree under `rust/`,
`go/`, `python/`, `typescript/`, `kotlin/`, `swift/`, `c/`, `android/`,
`harmonyos/`. Each implements the same 8 portable modules
(`models`, `memory`, `identity`, `languages`, `companion`, `inference`,
`tools`, `sync`). The cross-language contract specification lives in
[CONTRACTS.md](CONTRACTS.md).

---

## 4. Where models come from

`CircleAI.Inference.ModelDownloadService.EnsureModelAsync(modelId, uri, sha256, progress, ct)`:

1. Check `{ModelStorageRoot}/{modelId}.gguf` (or `.mnn` for MNN packaging)
2. If present and SHA matches, return its path immediately
3. Otherwise download from the URI, verify SHA-256, atomic-rename
4. URIs live in `CircleAI.Core/Models/embedded_registry.json` — all on
   `modelscope.cn`, with optional fallback URIs on GitHub mirrors

Pin every production-shipped entry with `expected_sha256` so a tampered
mirror cannot smuggle a bad weights file in.

---

## 5. Where native runtimes come from

`CircleAI.Runtime.NativeRuntimes.NativeRuntimeFetcher.EnsureRuntimeAsync(os, arch, backend, …)`:

1. Look up the bundle in `embedded_native_registry.json` for the requested
   tuple (`os`, `arch`, `backend`)
2. Check `{RuntimeCacheRoot}/{mnnVersion}-{os}-{arch}-{backend}/` — if
   `mnnbridge.{ext}` and `MNN.{ext}` both exist, return the install
3. Otherwise download, sniff format (ZIP `PK\x03\x04` or TAR.GZ `0x1F8B`),
   extract atomically to a `.tmp` directory and rename into place
4. Primary URL on `modelscope.cn`, fallback on `github.com/alibaba/MNN/releases`
5. Optional SHA-256 verification when the bundle pins one

Default cache root:
- Windows: `%LOCALAPPDATA%\CircleAI\runtime`
- Linux/macOS: `~/.local/share/CircleAI/runtime` (XDG)
- Container: `/data/runtime` (set via env var)

---

## 6. Capability detection

`CircleAI.Runtime.Capabilities.CapabilityProbe()` is the cross-platform
default. It dispatches to:

| OS detected    | Probe                       | Method                                   |
|----------------|-----------------------------|------------------------------------------|
| Windows        | `WindowsCapabilityProbe`    | Win32 `GlobalMemoryStatusEx` + PowerShell CIM (Win32_VideoController / Get-PnpDevice) |
| Linux          | `LinuxCapabilityProbe`      | `/proc/cpuinfo` / `/proc/meminfo` / `nvidia-smi` / `lspci`                             |
| macOS          | `MacOSCapabilityProbe`      | `sysctl` + `system_profiler SPDisplaysDataType`                                       |
| Android        | `AndroidCapabilityProbe`    | `/proc` (via LinuxProbe) + `getprop ro.soc.model` / `ro.build.version.release`        |
| iOS / HarmonyOS| `UnknownCapabilityProbe`    | host port supplies its own probe                                                      |

Probes NEVER throw. Failure to detect a field yields `Unknown` / `0` / `null`.

The result is a `HostProfile`. The richer-than-`DeviceCapabilities` view
is intentional — `BackendSelector` and `ModelLifecycleManager` need CPU
model, core split, GPU driver, NPU vendor; consumers of
`IInferenceBridge.GetDeviceCapabilitiesAsync` get the flat
`DeviceCapabilities` projection.

---

## 7. Backend selection

`BackendSelector.Select(HostProfile, CapabilityTier requested)` returns
`(BackendKind, CapabilityTier actual, string rationale)`. Routing table:

| Host shape                                  | Backend  | Notes                                       |
|---------------------------------------------|----------|---------------------------------------------|
| Apple Silicon + Apple GPU                   | Metal    | Tier capped by unified RAM                  |
| NVIDIA + VRAM ≥ 4 GiB                       | Cuda     | Tier from VRAM ladder (24/12/8/4 GiB)       |
| Huawei Ascend NPU                           | Ascend   | Tier 3 ceiling                              |
| Cambricon MLU                               | Cambricon| Tier 3 ceiling                              |
| AMD or Intel GPU + VRAM ≥ 4 GiB             | Vulkan   | Cross-vendor compute                        |
| Qualcomm Hexagon NPU / Adreno GPU           | OpenCL   | Tier 1 ceiling                              |
| ARM Mali (MediaTek, Exynos, Tensor)          | Vulkan   | Tier 1 ceiling                              |
| No usable GPU/NPU                            | Cpu      | Tier from system RAM ladder                 |

Always returns a runnable combination. The CPU fallback is mandatory.

---

## 8. The hosted server

`CircleAI.Inference.Server` is an ASP.NET Core minimal-API app exposing:

| Method | Path                          | Auth    | Purpose                                                          |
|--------|-------------------------------|---------|------------------------------------------------------------------|
| POST   | `/v1/chat/completions`        | ✓       | OpenAI-shaped chat completion (non-stream + SSE stream)          |
| POST   | `/v1/embeddings`              | ✓       | OpenAI-shaped embeddings (single string or array)                |
| POST   | `/v1/companion/turn`          | ✓       | CircleAI-native Companion turn (Send / Agent / Stream)           |
| GET    | `/v1/diagnostics`             | ✓       | Uptime, loaded models, host profile, backend selection, counters |
| GET    | `/v1/models`                  | ✓       | OpenAI-shaped list of loaded models                              |
| GET    | `/v1/admin/lifecycle`         | ✓       | Total VRAM/RAM allocated + per-load state                        |
| POST   | `/v1/admin/models/load`       | ✓       | Runtime load — passes to `IModelLifecycleManager`                |
| DELETE | `/v1/admin/models/{modelId}`  | ✓       | Runtime unload                                                   |
| GET    | `/v1/healthz`                 | —       | Liveness probe (200 OK)                                          |
| GET    | `/v1/readyz`                  | —       | Readiness (200 OK iff ≥ 1 model loaded)                          |

Auth is the API-key handler by default (constant-time match against
config-supplied keys), JWT bearer when `Auth:Jwt:Enabled=true`. Admission
control is fixed-cap (`MaxConcurrentRequests`, default 16, rejects with
HTTP 503 + `Retry-After: 1`).

The server is OpenAI-compatible at the wire shape — `openai-python` and
`@openai/sdk` work against it with only a base-URL change.

---

## 9. Lifecycle + observability

`ModelLifecycleManager` is the **sole writer** to the chat-bridge
registry once the server is up:

```
LoadAsync(descriptor) -> LoadResult
   1. AlreadyLoaded fast path                  → idempotent success
   2. GPU backend? VRAM headroom check         → InsufficientVram on miss
   3. RAM headroom check (always)               → InsufficientRam on miss
   4. Reserve state under a lock                → race protection
   5. BridgeFactory(ct).Invoke()                 → rolls back on throw
   6. Registry.Register + emit OperationsTotal
```

Every Load/Unload increments
`CircleAI.Core.Diagnostics.CircleAIDiagnostics.OperationsTotal` with tags
`{component, operation, outcome, model_id, backend, error_type}`. The
existing OpenTelemetry Meter set up by `CircleAI.Core` 1.1.0 picks these
up automatically — Prometheus / Jaeger / Loki exporters see them with
zero extra wiring.

---

## 10. Verification + experimental gates

Every type that isn't yet "wire-proven in production" carries a
`[CircleAIVerificationStatus(VerificationLevel.X)]` attribute:

- **Reference** — shape is correct, behaviour is internally tested but
  hasn't run against the real workload yet
- **WireProven** — exercised end-to-end in at least one production-shaped
  test or staging deployment
- **ProductionDeployed** — running against real traffic in a published
  app or backend

Surfaces that should be opted into deliberately also carry
`[Experimental("CIRCLEAI_*")]`. See `docs/experimental.md` for the IDs
and their stability stories.

---

## 11. What you should NOT do

1. **Don't add a Western inference runtime.** llama.cpp, vLLM, ONNX-LLM,
   TensorRT, MLC-LLM — out. Forever. See § 1.
2. **Don't fork the seam.** Every inference path goes through
   `IChatGenerator` + `IInferenceBridge`. If you want a parallel path,
   you don't; you want a new bridge implementation.
3. **Don't ship native binaries in the package.** The default deployment
   model is runtime fetch from ModelScope. Opt into bundling with
   `<CircleAIBundleMnn>true</CircleAIBundleMnn>` only when air-gapped.
4. **Don't regress the on-device profile.** Server changes must not break
   the in-process `LocalProcessInferenceBridge` path. Verify with the
   existing InferenceBridge test suite before pushing.
5. **Don't decide what the user sees without asking.** Surface decisions,
   pricing tiers, model defaults — these are management calls, not engine
   calls. See the project root `CLAUDE.md` for the wider policy.
