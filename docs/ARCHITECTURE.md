# CircleAI — Architecture

Last refreshed: 2026-06-05.

CircleAI is a portable, on-device + server AI SDK built around a single
inference seam, an Alibaba-MNN execution backend, and Qwen-family models
hosted on ModelScope. This document is the source of truth for those
decisions and the contracts that hold the rest of the codebase together.

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

```
CircleAI.Core               primitives — ComponentBase, Diagnostics (Activity+Meter),
                            audit sink, tenant context, [CircleAIVerificationStatus] attribute
CircleAI.Inference          IChatGenerator, MnnInterop, QwenTextGenerator,
                            ModelDownloadService, ModelDescriptor types
CircleAI.Embeddings         ITextEmbedder, MnnEmbeddingBackend
CircleAI.Hosting            high-level IAIService composition, voice options
CircleAI.Hosting.InferenceBridge
                            IInferenceBridge, DeviceCapabilities,
                            LocalProcessInferenceBridge
CircleAI.Runtime            CapabilityProbe (Windows/Linux/macOS/Android),
                            BackendSelector (CPU/CUDA/Vulkan/Metal/Ascend/…),
                            NativeRuntimeFetcher (on-demand MNN bundles
                            from ModelScope/Alibaba)
CircleAI.Inference.Server   OpenAI-compatible ASP.NET Core minimal API:
                            /v1/chat/completions (SSE), /v1/embeddings,
                            /v1/companion/*, /v1/diagnostics, /v1/admin/*
CircleAI.Companion          ICompanionSession + state composer
CircleAI.Memory             Episodic memory, affect state, RAG context builder
CircleAI.Personality        Persona provider (file-backed)
CircleAI.Knowledge          File-system knowledge store + RAG retrieval
CircleAI.Security           AnomalySignal, ISecurityWatchdog, RedactedEvidenceJsonConverter,
                            IAnomalyEventDispatcher (verify+dedup+invoke)
CircleAI.Federation         In-memory federation aggregator, FederationRound,
                            IFederationDeltaDispatcher
CircleAI.Simulation         Knowledge-graph extractor, network-health simulator
CircleAI.Agents.Peer        Mesh agent peer protocol + AgentBus
CircleAI.Wearable.Biosignals BiosignalAffectMapper / Aggregator / NullSource
```

10-language ports (Rust / Go / Python / TypeScript / Kotlin / Swift / C /
Android / HarmonyOS-ArkTS) sit alongside under `rust/`, `go/`, … each
implementing the same 8 portable modules. See `docs/CONTRACTS.md`.

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
