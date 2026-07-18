# Circle AI

A complete, sovereign AI runtime for .NET. On-device inference (MNN), a
self-hosted OpenAI-compatible server, RAG that ships with a real
SIMD-quantised vector store, and a contract surface that covers vision,
speech, spatial, banking, markets, workflows, code understanding,
DevOps, and the IDE-replacement primitives a future agent shell will
bind to.

Current 3.0 contract line: **`3.0.1`** — on
[nuget.org](https://www.nuget.org/profiles/bhengubv) and
[GHCR](https://github.com/bhengubv/CircleAI/pkgs/container/circleai-inference-server)
as of June 17, 2026. MIT.

> **3.0 doctrine.** If Claude Code / Codex / Cursor get pulled from a
> market, this is the stack a Geek-Network IDE / agent shell binds to
> instead. The cornerstone is **`CircleAI.DevTools`** — `ICodeEditor`,
> `IInlineSuggester`, `IAgentShell`, `IPatchPlanner`, `IRefactorTool`.
> Contracts ship today; implementations land in 3.0.x dot releases.

## Three version tracks (read this first)

CircleAI isn't one monolithic version line. Three coexist:

| Track | Versions | What it covers |
|---|---|---|
| **3.0 contracts** | `3.0.1` | The new sovereign-stack surface — vision, speech, spatial, banking, markets, workflows, devtools, etc. Plus the core hosting trinity (`Core`, `Inference`, `Hosting`, `Hosting.InferenceBridge`, `Inference.Server`, `Maui`, `Skills`, `Embeddings.Local`, `AetherNet`). |
| **Mature foundation** | `1.0.0` – `1.5.0` | Working production code the 3.0 contracts sit on. `Memory`, `Orchestration`, `Agents.Peer`, `Aether`, `Security.AetherNet`, `Networking.AetherNet`. |
| **Companion + adapters** | `1.2.0` | The original 1.x companion stack (`Companion`, `Tools`, `Voice`, `Personality`, `Security`, `Embeddings`) plus ~10 utility packages and ~50 lifestyle adapters. |

All three are real, all three install side-by-side, and a typical
consumer pulls packages from all three. Authoritative version history is
in [CHANGELOG.md](CHANGELOG.md) — every release from 1.6.x through 3.0.1.

---

## Install

**nuget.org (primary, public, no auth):**

```bash
dotnet add package CircleAI.Core
dotnet add package CircleAI.Hosting              # in-process companion service
dotnet add package CircleAI.Inference.Server     # OpenAI-compatible HTTP server
```

**GHCR (container image of the inference server):**

```bash
docker run -d -p 5050:5050 \
  -v ~/.circleai/models:/var/lib/circleai/models \
  ghcr.io/bhengubv/circleai-inference-server:latest
```

Any other package: `dotnet add package CircleAI.<Name>`.

---

## The trinity

Three contracts every consumer binds to:

| Type | Package | Purpose |
|---|---|---|
| `IChatGenerator` | `CircleAI.Inference` | The atomic seam — messages in, tokens out |
| `IInferenceBridge` | `CircleAI.Hosting.InferenceBridge` | Lifecycle + descriptor wrapper around a generator |
| `IAIService` | `CircleAI.Hosting` | The long-lived companion service — RAG, persona, tools, observers |

### Zero-knob consumer

```csharp
using CircleAI.Hosting;

// The SDK probes the device, queries the ModelScope catalog, picks the
// highest-quality bundle that fits + satisfies the capability flags, and
// keeps that decision live as the catalog refreshes.
var ai = new AIService(new AIOptions
{
    SystemPrompt         = "You are B!, the helpful one.",
    RequiredCapabilities = ChatCapability.Default | ChatCapability.Tools,
});

await ai.StartAsync();
Console.WriteLine(await ai.AskAsync("What's the capital of Morocco?"));
```

No `ModelId`. No `ContextSize`. No `MaxConcurrency`. The consumer states
intent; the SDK figures out the rest. Full overrides + injection points
in [CONSUMING.md](CONSUMING.md).

---

## The Neuron — on-device concierge

`CircleAI.Hosting` ships a **Neuron**: a private, on-device second brain
that decides, *per turn*, whether the always-warm generalist answers or a
capability-matched specialist is hot-loaded to answer — grounded in one
shared memory and persona.

- **Concierge router + gate.** Cheap keyword/length heuristics (never a
  model) classify each turn → the generalist, or a specialist for vision,
  long-context, or reasoning. A `NeuronGate` can veto a specialist by
  policy; the generalist floor always answers.
- **Two-slot residency.** The generalist stays warm (the floor); one
  specialist loads hot beside it. A `ResidentSlotManager` reserves RAM
  before loading and, under memory pressure, evicts the **specialist**
  first — the generalist never drops.
- **Host-neutral seam.** `IChatRuntime` / `IPersistableChatRuntime` + the
  `NeuronNode` facade let any harness drive the node without touching
  engine internals, and snapshot the generalist's KV session so a
  conversation survives an OOM or restart.

Resolve it from `AddNeuron(...)`; with no router the path is byte-identical
to a plain `AIService`. The Neuron is `IAIService`-backed, so
`CompanionSession` (identity + memory + proactive) — and the "Hey B" voice
loop — still sit on top unchanged.

The Neuron ships in the **C# reference and all seven sister ports** —
Python, TypeScript, Go, Kotlin, Swift, Rust, and C — each adapted to its
substrate and covered by its own test suite. (The HarmonyOS/ArkTS port is
pending.)

---

## Inference runtime

**Alibaba MNN** (Apache-2.0) on every platform. Models: **Qwen 3+**,
**Kimi-VL**, **DeepSeek**, **GLM** — discovered through the **ModelScope**
catalog at runtime, not pinned in source.

> The SDK has **zero model knowledge**. A new Qwen / Kimi / DeepSeek
> variant lands on ModelScope, the SDK picks it up on next refresh.
> NuGet sleeps — releases are for SDK bugs and new runtime backends,
> not "we support a new model."

Cross-platform MNN native libs ship in-package for **8 RIDs**: `win-x64`,
`linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`, `android-arm`,
`android-arm64`, `ios-arm64`. See [SETUP.md](SETUP.md) for per-platform
setup.

The TurboVec SIMD vector backend in `CircleAI.Embeddings.Local` ships
its own runtimes for **7 RIDs**: `win-x64`, `linux-x64`, `osx-arm64`,
`osx-x64`, `android-arm64`, `android-x64`, `ios-arm64`.

---

## Self-hosted inference server

`dotnet add package CircleAI.Inference.Server`. OpenAI-compatible
ASP.NET Core minimal-API runtime over `CircleAI.Hosting.InferenceBridge`.
Deployment artefacts ship in-package: `Dockerfile`, `systemd` unit, and
a Windows-service install script.

| Method | Path | Notes |
|---|---|---|
| POST | `/v1/chat/completions` | non-stream + SSE stream, OpenAI shape |
| POST | `/v1/embeddings` | single-string or string-array input |
| POST | `/v1/companion/turn` | CircleAI-native (Send / Agent / Stream) |
| GET | `/v1/models` | OpenAI-shaped list of loaded models |
| GET | `/v1/diagnostics` | uptime, loaded models, host profile, backend, counters |
| GET | `/v1/admin/lifecycle` | total VRAM/RAM allocated + per-load state |
| POST | `/v1/admin/models/load` | runtime load via `MnnInferenceBridgeFactory` |
| DELETE | `/v1/admin/models/{id}` | runtime unload |
| GET | `/v1/healthz` | liveness (no auth) |
| GET | `/v1/readyz` | readiness (no auth) |

JWT and API-key auth out of the box. Drop behind a load balancer; any
OpenAI-SDK consumer talks to it unchanged. `"model":"auto"` defers to
`IModelSelector.BestFit(deviceProbe, ChatCapability.Default)`.

Multi-tenant / sharded / gRPC variant: `CircleAI.Inference.Server.Enterprise`
adds `ITenantRouter`, `IBatchScheduler`, `IModelShardPlanner`, and the
RT-12 v2 `ICrossTierOffload` ("phone borrows the server brain").

---

## Track 1 — 3.0 contracts (`3.0.1`)

Lines marked **contracts** are contract-only surfaces with fail-closed
Null implementations — wire your own backend, or wait for the dot
release. Lines marked **implemented** ship working backends today.

### Core + hosting

| Package | Status | What you get |
|---|---|---|
| `CircleAI.Core` | implemented | Primitives + `ModelRegistryService` + `TurboQuantCodec` + capability flags |
| `CircleAI.Inference` | implemented | `IChatGenerator` + `QwenTextGenerator` + `KimiVlGenerator` + MNN P/Invoke |
| `CircleAI.Hosting.InferenceBridge` | implemented | `IInferenceBridge` + `MnnInferenceBridgeFactory` |
| `CircleAI.Hosting` | implemented | `IAIService` + persona + RAG + tool dispatch + observers + `IToolCatalog` + the **Neuron** (concierge router + gate, two-slot residency, `NeuronNode`) |
| `CircleAI.Maui` | implemented | MAUI-side `OnPaused`/`OnResumed` snapshot wiring + Android/iOS adapters |
| `CircleAI.Inference.Server` | implemented | The OpenAI-shape HTTP server (see above) |
| `CircleAI.Inference.Server.Enterprise` | contracts | Multi-tenant + gRPC + batch + sharding + RT-12 v2 |
| `CircleAI.Skills` | implemented | Pluggable capability surface + `SkillPackAutoImporter` (drains 8 default skill packs at host start) |
| `CircleAI.Embeddings.Local` | implemented | `HnswEmbeddingStore` backed by `TurboVecEmbeddingIndex` — SIMD-blocked, 4-bit quantised. Production RAG today |
| `CircleAI.AetherNet` | implemented | Mesh capability discovery + RT-12 cross-tier offload |

### Domain (consolidated)

`CircleAI.Domain` packs nine plug-points into one NuGet ID. What the
roadmap called separate packages (MemPalace, HippoRAG, Swarm,
Identity.LoRA, Domain.Food, Domain.Finance, Domain.FinancialAgent,
Domain.Presentations, Domain.JobSearch) all live as interfaces inside it:

| Interface | Pattern |
|---|---|
| `IMemPalaceStore` | MemPalace-pattern long-term memory |
| `IHippoRagStore` | HippoRAG graph + Personalized PageRank recall (NeurIPS '24 / ICML '25) |
| `ISwarmCoordinator` | MiroFish-pattern multi-device coordination over AetherNet |
| `IPersonalLoRA` | RT-10 on-device LoRA fine-tuning |
| `IFoodEmbeddings` | EPICure ingredient embeddings + substitutes |
| `IFinanceRetrieval` | quant-mind RAG retrieval |
| `IFinancialAgent` | dexter autonomous research |
| `IPresentationGenerator` | presenton slide generator |
| `IJobSearchPipeline` | career-ops CV + cover letter + matching |

### Vision

`CircleAI.Vision` (contracts). 7 interfaces — `IComputerVisionRuntime`,
`IFaceDetector`, `IFaceEmbedder`, `IFaceLivenessDetector`,
`IDocumentVerifier`, `IPlateRecognizer`, `IBluetoothAnomalyDetector`.
Backends land in 2.2.1 when compv / facex / FaceLivenessDetection-SDK /
KYC-Documents-Verif-SDK / ultimateALPR-SDK / Bluehound are vendored.

### Speech

`CircleAI.Speech` (contracts). 4 interfaces — `ISpeechRecognizer`,
`ISpeechSynthesizer`, `IWakeWordDetector`, `IOpticalCharacterRecognizer`.
Powers the B! voice loop. Backends in 2.3.1: FunASR (ASR), ChatTTS,
hey-snips (wake word), PaddleOCR.

### Spatial

`CircleAI.Spatial` (contracts). 4 interfaces — `IGeoTileSource`,
`IRadarReadout`, `ISkyTracker`, `I3DSceneRenderer`. Lets an AI describe
— and pull data about — places, tracks, skies, and rendered surfaces.

### Inputs + tools

| Package | Status | What you get |
|---|---|---|
| `CircleAI.Inputs` | contracts | Web scraper, stealth HTTP, video ingest, MCP scrape, terminal cast |
| `CircleAI.Tools.Catalog` | contracts | Composio-pattern: `IProviderCatalog`, `ICredentialStore`, `IOAuth2FlowDriver`, `IQuotaGuard`, `IToolNamespaceStore` |

### Safety + alignment

| Package | Status | What you get |
|---|---|---|
| `CircleAI.ContentPolicy` | contracts | Sponsio-pattern refusal + audit + injection detection. **Was `CircleAI.Guardrails` through 3.0.0; renamed in 3.0.1.** Interfaces: `IContentFilter`, `IRefusalPolicy`, `IPromptInjectionDetector`, `ISafetyAuditLog` |
| `CircleAI.ModelAlignment` | contracts | OBLITERATUS-pattern targeted abliteration toolkit + audit — `IAlignmentToolkit` (Apply / Revert / List) + `IAlignmentAuditor` (refuses to publish aligned weights) |

### Observation + ops

| Package | Status | What you get |
|---|---|---|
| `CircleAI.Observer` | contracts | Perceive-reason-act loop. `ISensor` + `IObservationToolbox` + `IObservationLoop` (Apache 2.0 pattern-port) |
| `CircleAI.Observability` | contracts | `IMetricSink`, `ITraceSink`, `IDashboardPublisher` (Prometheus / OTel / Grafana / claude-team-dashboard) |
| `CircleAI.Operator` | contracts | Kubernetes operator (kagent pattern). `IModelOperator` + `IDeploymentObserver` |
| `CircleAI.SDD` | contracts | Spec-driven development (spec-kit pattern-port). `ISpecificationStore` + `ISpecificationValidator` + `ISpecToScaffold` |

### Business apps

All 2.8.0-line, all contracts.

| Package | Interfaces |
|---|---|
| `CircleAI.Banking` | `IAccountReader`, `ILedgerWriter`, `IPaymentProcessor` (OBP-API / fineract / hyperswitch) |
| `CircleAI.Markets` | `IMarketDataFeed`, `IInstrumentCatalog`, `IOrderRouter` (OpenBB / StockSharp) |
| `CircleAI.Pipelines` | `IPipelineSource`, `IPipelineSink`, `IPipelineExecutor`, `IDatabaseQueryTool` |
| `CircleAI.Workflows` | `IWorkflowDefinitionStore`, `IWorkflowRunner`, `IWorkflowState` (restate / automatisch / paca) |
| `CircleAI.Visualization` | `IDashboardDefinitionStore`, `IApiDocBuilder`, `ISiteBuilder` |
| `CircleAI.Collaboration` | `IChannelStore`, `IMessageStore`, `IPresence` (mattermost) |
| `CircleAI.CRM` | `IContactStore`, `IDealPipeline`, `IActivityLog` (twenty) |

### DevOps + ops surface

All 2.9.0-line, all contracts.

| Package | Interfaces |
|---|---|
| `CircleAI.BuildFarm` | `IBuildAgentPool`, `IBuildJobRunner`, `IBuildArtifactStore` (OSX-KVM / macos) |
| `CircleAI.DepBot` | `IDependencyAnalyzer`, `IDependencyUpdater` (renovate) |
| `CircleAI.DocAnalytics` | `IDocumentTracker`, `IDocumentInsights` (papermark) |
| `CircleAI.Testing` | `ISnapshotComparer`, `IGoldenStore` (Verify) |
| `CircleAI.Distribution` | `IFileSync`, `IPeerAdvertiser` (FileSync over AetherNet) |
| `CircleAI.MediaHub` | `IMediaLibrary`, `ISyncedPlayback`. **Was `CircleAI.MediaServer` through 3.0.0; renamed in 3.0.1.** |
| `CircleAI.WindowsAutomation` | `IUiAutomationDriver` (mcp-windows-automation) |
| `CircleAI.MicroAgents` | `IMicroAgent`, `IMicroAgentHost` (picoclaw / hermes-desktop-os1) |

### 3.0 strategic cornerstones

| Package | Interfaces |
|---|---|
| `CircleAI.DevTools` | **Cornerstone.** `ICodeEditor`, `IInlineSuggester`, `IAgentShell`, `IPatchPlanner`, `IRefactorTool` |
| `CircleAI.Research` | `IResearchCorpus`, `IPaperRetrieval`, `ICitationGraph` |
| `CircleAI.Games` | `IGameLoop`, `IInputMap`, `ISceneGraph` (flame / Doom.Mobile) |
| `CircleAI.AutonomousBiz` | `ITreasury`, `IRevenueLoop`, `IDecisionLog` (show-me-the-money) |
| `CircleAI.CodeUnderstanding` | `ICodeIndexer`, `ICodeSearch`, `ISymbolGraph` (Understand-Anything) |

---

## Track 2 — Mature foundation (`1.x`)

The working production substrate the 3.0 contracts sit on. Not stale —
shipped, tested, used.

| Package | Version | What you get |
|---|---|---|
| `CircleAI.Memory` | `1.3.0` | Episodic memory + persona + affect (`AffectState`/`AffectVad`) + goal + feedback stores. Hierarchical sleep-cycle consolidator. Multimodal-to-semantic compression. SQLite + JSON backends. RAG context builder + pipeline builder |
| `CircleAI.Orchestration` | `1.4.0` | Host-side loki-mode agent orchestration — `LokiOrchestrator` with quality gates and concurrent dispatch. Spawns Engineering / Operations / Customer / etc. role swarms |
| `CircleAI.Agents.Peer` | `1.4.0` | Peer-agent envelope + `AgentBus` correlation IDs |
| `CircleAI.Aether` | `1.3.0` | Aether-protocol contracts — floats upstream `bhengubv/aether-protocol` |
| `CircleAI.Security.AetherNet` | `1.1.0` | AetherMesh.Security floating adapter — mesh-directive-driven chat refusal wired into `CircleAI.Hosting` |
| `CircleAI.Networking.AetherNet` | `1.0.0` | AetherMesh.Transport floating adapter — `INetworkTransport` over AetherNet |

---

## Track 3 — Companion + adapters (`1.2.0`)

The original 1.x companion stack and its lifestyle adapter family. ~70
packages. Mature, working code; no version bump because the 3.0 ship
added new contract surfaces on top rather than re-stamping the base.

### Companion core

| Package | What you get |
|---|---|
| `CircleAI.Companion` | Long-running session loop with proactive turn generation and mesh-state sync |
| `CircleAI.Tools` | `IToolBridge` for function calling + `HttpToolBridge` (REST → tool definitions) for the TheGeekNetwork API ecosystem (36 APIs, 1600+ endpoints) |
| `CircleAI.Voice` | ONNX TTS / STT pipeline (the only ONNX dependency in the codebase — text inference stays MNN) |
| `CircleAI.Personality` | User-declared persona artefact — structured identity declaration the user owns, edits, and exports |
| `CircleAI.Security` | Cross-cutting security primitives — refusal model, content tagging, audit hooks |
| `CircleAI.Embeddings` | Older on-device text embeddings (BGE / Qwen-Embedding) — the 1.x predecessor to `CircleAI.Embeddings.Local` |

### Utility packages

| Package | What you get |
|---|---|
| `CircleAI.Search` | Vector search over local SQLite — older RAG retrieval primitive |
| `CircleAI.Knowledge` | Markdown-on-disk knowledge store — episodic memory as user-editable `.md` files |
| `CircleAI.Identity` | Cross-device identity — unified persona key that travels with the person, not the device |
| `CircleAI.Sync` | Memory delta sync — episodic memory, affect state, persona deltas across devices |
| `CircleAI.Federation` | Federated learning round model — only deltas leave the device, never raw data |
| `CircleAI.Runtime` | Runtime capability detection — OS/arch/CPU/GPU/RAM/NPU + backend selection (CPU/CUDA/Vulkan/OpenCL/Metal) |
| `CircleAI.Desktop` | Desktop-shell adapter |
| `CircleAI.Web` | Web-shell adapter |
| `CircleAI.Ambient` | Ambient / always-listening adapter |
| `CircleAI.Accessibility` | Assistive technology — WCAG compliance, disability rights, accessibility profile |

### Networking transports (non-AetherNet)

`CircleAI.Networking` defines `INetworkTransport` and `ITransportSelector`
(picks per-message based on connectivity, not consumer choice). Each
transport ships as its own package:

| Package | State |
|---|---|
| `CircleAI.Networking.Http` | production |
| `CircleAI.Networking.WebSocket` | production |
| `CircleAI.Networking.Grpc` | adapter (bring your own gRPC client) |
| `CircleAI.Networking.Tcp` | adapter |
| `CircleAI.Networking.Mqtt` | adapter |
| `CircleAI.Networking.WiFi` | adapter |
| `CircleAI.Networking.Bluetooth` | adapter (BLE — bring `IBleAdapter`) |
| `CircleAI.Networking.NearLink` | adapter (Huawei SLE — bring `INearLinkAdapter`) |
| `CircleAI.Networking.Dtn` | adapter (delay-tolerant) |

### Commerce

| Package | What you get |
|---|---|
| `CircleAI.Commerce` | E-commerce domain assistant — listings, pricing, orders, suppliers, marketplace analytics |
| `CircleAI.Commerce.Accounting` | Bookkeeping, reconciliation, VAT, IFRS reporting, audit prep |
| `CircleAI.Commerce.Finance` | Working capital, cash flow, business lending, treasury |
| `CircleAI.Commerce.Integration.PayFast` | PayFast payment flow, webhooks, refunds, disputes |
| `CircleAI.Commerce.Integration.Xero` | Xero-aware bookkeeping, invoice management, reconciliation |

### Languages + translation

| Package | What you get |
|---|---|
| `CircleAI.Languages` | KnownLanguages registry |
| `CircleAI.Languages.Language` | Per-language adapter base |
| `CircleAI.Languages.Translation` | Translation pipeline |
| `CircleAI.Languages.Language.Afrikaans` | Afrikaans adapter |
| `CircleAI.Languages.Language.Amharic` | Amharic adapter |
| `CircleAI.Languages.Language.Arabic` | Arabic adapter |
| `CircleAI.Languages.Language.Hausa` | Hausa adapter |
| `CircleAI.Languages.Language.Portuguese` | Portuguese adapter |
| `CircleAI.Languages.Language.Sesotho` | Sesotho adapter |
| `CircleAI.Languages.Language.Swahili` | Swahili adapter |
| `CircleAI.Languages.Language.isiZulu` | isiZulu adapter |

### Lifestyle adapters

~50 lightweight packages that wrap `CircleAI.Companion` with
domain-specific system-prompt context, knowledge facets, and lifecycle
hooks. The companion engine loads only the adapters whose required
sensors / UI fit the host device.

Selection: `Accessibility`, `Agriculture`, `Beauty`, `Business`, `Civic`,
`Community`, `Construction`, `Creative`, `Education`, `Elderly`,
`Energy`, `Faith`, `Family`, `Fitness`, `Food`, `Gaming`, `Healthcare`,
`Home`, `Hospitality`, `HR`, `IoT`, `Kids`, `Knowledge`, `Legal`,
`Logistics`, `Media`, `Parenting`, `Personal`, `Personal.Finance`,
`Personal.Health`, `Personal.Mental`, `Pets`, `RealEstate`,
`Relationships`, `Retail`, `Safety`, `Safety.Child`, `Search`, `Simulation`,
`Social`, `Sports`, `Tourism`, `Travel`, `Wearable`, `Wearable.Biosignals`,
… (full list in `src/`).

> **Naming caveat — two collisions handled in 3.0.1:**
>
> - 1.2.0 `CircleAI.Safety` (situational awareness adapter) ≠ 3.0.1 `CircleAI.ContentPolicy` (refusal/audit/injection contracts).
> - 1.2.0 `CircleAI.Media` (content production adapter) ≠ 3.0.1 `CircleAI.MediaHub` (Plex + beatsync media-server contracts).
>
> The old 3.0.0-line IDs (`CircleAI.Guardrails`, `CircleAI.MediaServer`)
> remain reserved on nuget.org permanently (push-only API key — no
> unlist) but nothing newer ships under those names.

---

## 10-language portable kernel (sister surface)

The portable Circle AI kernel (`AffectState` math, `KnownLanguages`
registry, `ICompanionSession` contracts) ships in **10 languages** so
every Aether node can host the companion stack natively — C#, Python,
TypeScript, Go, Kotlin, Swift, Rust, C, Android (Kotlin), HarmonyOS
(ArkTS). Each port lives in its own top-level directory in this
repository (`python/`, `typescript/`, `go/`, …); the cross-language
contract specification is in [docs/CONTRACTS.md](docs/CONTRACTS.md).

The kernel now also carries the **Neuron** — the concierge router + gate,
`ResidentSlotManager`, the `IChatRuntime` seam, and `NeuronNode` — in the
C# reference and the seven sister ports (Python, TypeScript, Go, Kotlin,
Swift, Rust, C), each with its own test suite. The HarmonyOS (ArkTS) port
is the one remaining gap.

---

## License

MIT. See [LICENSE](LICENSE).

---

## Pointers

| Doc | Purpose |
|---|---|
| [CHANGELOG.md](CHANGELOG.md) | Authoritative version history — every release from 1.6.x → 3.0.1 |
| [INTRODUCING.md](INTRODUCING.md) | The 3.0 doctrine in narrative form |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Why ModelScope is the catalog and NuGet sleeps |
| [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) | Full sovereign-stack rationale |
| [CONSUMING.md](CONSUMING.md) | The trinity, the three injection points, worked example |
| [SETUP.md](SETUP.md) | MNN native-runtime setup per platform |
| [docs/DEPLOY.md](docs/DEPLOY.md) | Server deployment guide (Docker / systemd / Windows service) |
| [TODO.md](TODO.md) | Open work, current priorities |
| [docs/CONTRACTS.md](docs/CONTRACTS.md) | Cross-language portable kernel contract specification |
| [docs/BUILD.md](docs/BUILD.md) | Build + test guide for the C# solution and the 10 language ports |
