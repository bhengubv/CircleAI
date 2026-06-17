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

## [3.0.1] — 2026-06-17 — **Naming + metadata cleanup**

### Renamed

- **`CircleAI.MediaServer` → `CircleAI.MediaHub`** (Plex + beatsync media-server contracts).
- **`CircleAI.Guardrails` → `CircleAI.ContentPolicy`** (Sponsio refusal/audit/injection contracts).

Both old names existed only as a workaround for collisions with the v1.2.0 personal-safety / content-production domain packs (`CircleAI.Safety`, `CircleAI.Media`). `CircleAI.MediaServer 3.0.0` and `CircleAI.Guardrails 3.0.0` are on nuget.org and stay there (the API key scope is push-only — no unlist), but nothing newer ships under those IDs. New consumers: `dotnet add package CircleAI.MediaHub`, `dotnet add package CircleAI.ContentPolicy`.

### Fixed

- Description-prefix drift across all 41 csprojs — every `(2.X.0)` historical prefix tag in the `<Description>` field normalized to `(3.0.1)`.

### Versions

All 41 packages bumped to **3.0.1**.

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
