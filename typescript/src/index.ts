// src/index.ts
// Barrel export for the @bhengubv/circle-ai package.
// Re-exports the original 9 portable modules + the 1.5.0 parity additions.

// Original 9 portable modules
export * from "./models/index.js";
export * from "./memory/index.js";
export * from "./identity/index.js";
export * from "./languages/index.js";
export * from "./companion/index.js";
export * from "./inference/index.js";
export * from "./tools/index.js";
export * from "./sync/index.js";
export * from "./security/index.js";

// 1.5.0 parity additions
export * from "./device/index.js";
export * from "./prompt/index.js";
export * from "./catalog/index.js";
export * from "./registry/index.js";
export * from "./selector/index.js";
export * from "./agents/peer/index.js";
export * from "./hosting/index.js";

// Disambiguate names that both ./companion and ./hosting export via `export *`.
// The hosting versions are the canonical butler-runtime contracts, so they win
// at the package root; the companion-local variants remain reachable via the
// ./companion subpath. (companion.IAIService is a narrow briefing subset;
// companion.ProactiveMessageHandler is a companion-event handler.)
export type {
  IAIService,
  ProactiveMessageHandler,
} from "./hosting/index.js";

// Companion reasoning core — HER/Jarvis contracts + deterministic in-memory impls
export * from "./companion/reasoning/index.js";

// Companion.Proactive project — scheduling primitives, cron, scheduler, driver
export * from "./proactive/index.js";

// Memory in-memory stores (episodic, persona, feedback, goal)
export * from "./memory/stores.js";

// CircleAI.Memory.Sync — companion-state sync engine (HLC, envelopes, bridges)
export * from "./memory/sync/index.js";

// CircleAI.Memory.Runtime — CompanionRuntime host orchestrator
export * from "./memory/runtime/index.js";

// CircleAI.Core model-management runtime — loaders, managers, downloaders,
// sources, SafeModelHandle/PlatformInterop, CircleEngine, ShardKvCodec,
// auditing + multi-tenant contracts.
export * from "./core/index.js";

// CircleAI.Embeddings — ITextEmbedder + TextEmbedder (backend-injected).
export * from "./embeddings/index.js";

// CircleAI.Embeddings.Local — encoder/store/index contracts + InMemoryEmbeddingStore.
export * from "./embeddings/local/index.js";

// CircleAI.Inference.Server + .Enterprise — OpenAI-compatible in-memory server:
// bridge contracts, OpenAI DTOs, API-key auth, model registry + lifecycle,
// companion session resolver, native-runtime status, bridge factory, in-memory
// endpoint handlers, and the enterprise tenant/batch/shard/offload tier.
export * from "./inference/server/index.js";

// CircleAI.Aether — the one-way Aether↔BhenguAI boundary: telemetry events +
// surface, presence/capability (IAetherContext), intelligence output
// (IAetherIntelligence), the AI security-layer contract (IAISecurityLayer +
// SecurityDirective), and the auth-challenge contract (IAuthChallenge), plus
// deterministic in-memory implementations of each.
export * from "./aether/index.js";

// CircleAI.AetherNet — mesh capability discovery (RT-12 v1): advertisement
// record, IMeshCapabilityRegistry + in-memory impl, and the broadcaster contract
// (null + registry-backed loopback).
export * from "./aethernet/index.js";

// CircleAI.Security.AetherNet — AetherNet-specific security bindings: enum
// mapper, MeshDirectiveStore + MeshSecurityGate, the AetherSecurityBridge
// (IAISecurityLayer over SecurityLayerService), AetherIntelligenceAdapter
// (IAetherIntelligence over PeerIntelligenceService), and the
// MeshGatedCompanionSession decorator.
export * from "./security/aethernet/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// CircleAI.Integration — external-integration layer (Phase B/C). Shared
// contracts (ICalendarConnector / IEmailConnector / INewsSource / IWeatherProvider
// / IRoutingProvider / IHomeAutomationConnector + CalendarEvent / EmailMessage /
// NewsItem / WeatherSample / RouteEstimate / HaEntity) over an injected IHttpClient
// transport (no real network), plus faithful connector ports:
//   .Calendar        — CalDav / Google / MsGraph calendar connectors (+ ICS parser).
//   .Email           — Gmail / MsGraph (HTTP) + Imap (over an injected IImapTransport).
//   .Geo             — OpenMeteoWeatherProvider + OsrmRoutingProvider.
//   .HomeAssistant   — HomeAssistantConnector (+ turn_on/off convenience).
//   .News            — Bluesky / Mastodon / NewsApi / Rss sources (+ RSS/Atom reader).
// ─────────────────────────────────────────────────────────────────────────────
export * from "./integration/index.js";
export * from "./integration/calendar/index.js";
export * from "./integration/email/index.js";
export * from "./integration/geo/index.js";
export * from "./integration/homeassistant/index.js";
export * from "./integration/news/index.js";

// Disambiguate names that both ./companion (briefing subsets) and ./integration
// (the canonical full CircleAI.Integration contracts) export via `export *`.
// The full integration contracts win at the package root; the narrow briefing
// variants remain reachable via the ./companion subpath. Mirrors the existing
// IAIService / Account disambiguation convention above.
export type {
  ICalendarConnector,
  IEmailConnector,
  INewsSource,
  IWeatherProvider,
} from "./integration/index.js";

// ./hosting also exports an `HttpResponse` (its SSE-capable server response) via
// `export *`, which collides with the integration transport's `HttpResponse`.
// Keep hosting's as the canonical package-root binding (pre-existing public
// surface); the integration `HttpResponse` stays reachable via ./integration.
export type { HttpResponse } from "./hosting/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// CircleAI.Voice — the on-device voice stack: AudioFormat, wake-word detection
// (Null / energy-ASR / ONNX-KWS), voice-activity detection (Null / energy-RMS),
// speech-to-text (Null / Whisper over an injected whisper.cpp context), text-to-
// speech (Null / ONNX VITS), speaker identity + speech-emotion recognition (ONNX),
// and the composed VoicePipeline. The ONNX runtime and whisper.cpp native deps
// are injected behind IOnnxSession / IWhisperContext (deterministic, no native lib),
// mirroring the embeddings backend-injection convention.
//
// NOTE: CircleAI.Voice's `TranscriptionResult` and `TranscribedEventArgs` collide
// by name with the narrow briefing-subset variants that CircleAI.Companion.HerJarvis
// already exports at the package root (its VoiceCompanionListener bridge). Under
// `export *` TypeScript elides the ambiguous names, so the full CircleAI.Voice
// definitions — the authoritative ones — are re-asserted below as the canonical
// package-root binding; the companion subsets remain reachable via the ./companion
// subpath. Mirrors the existing IAIService / Account / Activity disambiguation.
export * from "./voice/index.js";
export type { TranscriptionResult, TranscribedEventArgs } from "./voice/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// CircleAI.Speech — the on-device speech DSP + ASR/TTS/OCR contract surface. A
// DISTINCT C# project from CircleAI.Voice: its VAD / wake-word / transcription
// contracts are frame-at-a-time and would collide by identifier with the
// stream-based CircleAI.Voice ones, so the port keeps them in the ./speech
// module under disambiguated names (SpeechTranscriptionResult,
// ISpeechVoiceActivityDetector, ISpeechWakeWordDetector,
// NullSpeechVoiceActivityDetector, NullSpeechWakeWordDetector, …). The
// genuinely-new surface — AudioFormatConverter (μ-law/a-law/PCM-16 + resample),
// echo cancellation (NLMS / WebRTC shell), end-of-turn detection (rule-based /
// SmartTurn) and noise reduction (spectral-subtraction / Krisp / DeepFilterNet)
// — carries its own non-colliding names. Faithful port of CircleAI.Speech.
export * from "./speech/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// CircleAI.Tools.Catalog — the composio-pattern provider directory + credential
// vault + OAuth2 flow + quota guard + tool-namespace store: AuthKind,
// ProviderDescriptor / OAuth2Descriptor / CredentialBundle / QuotaPolicy /
// ToolNamespace, the five contracts (IProviderCatalog / ICredentialStore /
// IOAuth2FlowDriver / IQuotaGuard / IToolNamespaceStore), the injectable
// IAeadCipher crypto seam + WebCrypto AES-256-GCM default, the in-memory impls
// (substring/tag provider search, AES-GCM encrypt-at-rest credential store,
// authorize-URL OAuth2 driver + host token-exchange delegate, sliding-window
// quota guard, namespace store) and fail-closed Null* defaults. Lives in
// ./tools_catalog because the ./catalog module is the unrelated MODEL catalog.
export * from "./tools_catalog/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// CircleAI.Telephony — the carrier-agnostic voice-agent stack: call primitives,
// sentence chunking, DTMF tone generation, barge-in, answering-machine + IVR-
// loop detection, warm transfer, tool-calling (+ circuit breaker + streaming
// progress + MCP import), guardrails, LLM-as-judge, eval sessions, latency +
// false-interruption tracking, reassurance fillers, speculative generation,
// prompt-variable resolution, stereo call recording, hold-music mixing, phone-
// number provisioning, voice-loop-as-a-tool, agent handoff, consult escalation,
// dashboard data, speech-lifecycle events, first-message preamble, an in-memory
// test call session, and OTel-style telemetry. Carrier / MCP / consult HTTP
// boundaries are injected behind IHttpClient; native tracing behind
// IActivitySource; the carrier behind ITelephonyCarrier (Null* fail-soft).
//
// NOTE: a handful of telephony names collide with identifiers already exported
// at the package root by sibling barrels — the HTTP-seam types + `isSuccess-
// StatusCode` (from ./integration, byte-identical shapes reused deliberately),
// `throwIfAborted` (from ./voice), `ILogger` (from ./plugins), and the tool-
// calling records `ToolDefinition` / `ToolInvocation` / `ToolResult` (from
// ./tools, a DISTINCT shape). Two `export *` sources exporting the same name is
// a hard TS error, so each collision is disambiguated explicitly below: the
// pre-existing owner keeps the bare name, and telephony's variant is re-asserted
// under a `Telephony*` / `telephony*` alias so it stays reachable at the root.
// Mirrors the TranscriptionResult / IAIService disambiguation convention above.
export * from "./telephony/index.js";

// Pin the pre-existing owners as the bare-name winners (resolves the ambiguity).
// `HttpResponse` is deliberately omitted — it is already canonically pinned to
// ./hosting above; telephony's variant is reachable via the alias below.
export type { HttpMethod, HttpRequest, IHttpClient } from "./integration/index.js";
export { isSuccessStatusCode } from "./integration/index.js";
export { throwIfAborted } from "./voice/index.js";
export type { ILogger } from "./plugins/index.js";
export type { ToolDefinition, ToolInvocation, ToolResult } from "./tools/index.js";

// CircleAI.Tools' `IToolBridge` (the local-LLM → TheGeekNetwork bridge, HTTP /
// Composio) collides by name with the pre-existing CircleAI.Hosting `IToolBridge`
// (the hosting-layer tool-catalog seam) already exported at the package root via
// `export *`. Two `export *` sources exporting the same name is a hard TS error,
// so the pre-existing hosting owner keeps the bare name and the CircleAI.Tools
// variant is re-asserted under an alias. The full CircleAI.Tools bridge remains
// reachable at the root as `ITgnToolBridge`, and directly via the ./tools subpath.
export type { IToolBridge } from "./hosting/index.js";
export type { IToolBridge as ITgnToolBridge } from "./tools/index.js";

// Re-assert telephony's colliding variants under disambiguated aliases.
export type {
  HttpMethod as TelephonyHttpMethod,
  HttpRequest as TelephonyHttpRequest,
  HttpResponse as TelephonyHttpResponse,
  IHttpClient as ITelephonyHttpClient,
  ILogger as ITelephonyLogger,
  ToolDefinition as TelephonyToolDefinition,
  ToolInvocation as TelephonyToolInvocation,
  ToolResult as TelephonyToolResult,
} from "./telephony/index.js";
export { isSuccessStatusCode as telephonyIsSuccessStatusCode } from "./telephony/index.js";
export { throwIfAborted as telephonyThrowIfAborted } from "./telephony/index.js";
export { toolDefinition as telephonyToolDefinition } from "./telephony/index.js";
export { toolInvocation as telephonyToolInvocation } from "./telephony/index.js";
export { toolResult as telephonyToolResult } from "./telephony/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// Domain boards A — health / finance / legal / edu / commerce
// Each is an I<Domain>Board + record types + a deterministic in-memory impl,
// plus the vertical's static <Domain>DomainContext. Faithful ports of the
// CircleAI.<Domain> C# projects.
// ─────────────────────────────────────────────────────────────────────────────

// CircleAI.Healthcare — IHealthcareBoard (patients, appointments, prescriptions).
export * from "./healthcare/index.js";

// CircleAI.Banking — IAccountReader / ILedgerWriter / IPaymentProcessor, the
// shared InMemoryBank (double-entry payments), and fail-closed Null* defaults.
export * from "./banking/index.js";

// CircleAI.Legal — ILegalBoard (matters, contracts, deadlines, clause library).
export * from "./legal/index.js";

// CircleAI.Education — IEducationBoard (courses, lessons, students, avg progress).
export * from "./education/index.js";

// CircleAI.Commerce — ICommerceBoard (customers, orders, line items, LTV).
export * from "./commerce/index.js";

// CircleAI.Commerce.Accounting — IAccountingBoard (double-entry postings, tax,
// per-period sums, net profit).
export * from "./commerce/accounting/index.js";

// CircleAI.Commerce.Finance — IInvoiceBoard (invoices with taxed lines, payments,
// overdue tracking, outstanding balance).
export * from "./commerce/finance/index.js";

// CircleAI.Commerce.Integration.PayFast — IPayFastBoard (real MD5 signature
// builder + WebUtility.UrlEncode parity, ITN verification, webhook recorder).
export * from "./commerce/integration/payfast/index.js";

// CircleAI.Commerce.Integration.Xero — IXeroBoard (token storage, tenant
// tracking, webhook recorder).
export * from "./commerce/integration/xero/index.js";

// CircleAI.Personal.Finance — IPersonalFinanceBoard (accounts, transactions,
// budgets, monthly summary). NOTE: its `Account`/`account` collide by name with
// CircleAI.Banking's; under `export *` TypeScript elides the ambiguous name from
// the package root, so the banking `Account`/`account` win at root and the
// personal-finance variants remain reachable via the ./personal/finance subpath.
export * from "./personal/finance/index.js";

// CircleAI.Personal.Health — IPersonalHealthBoard (VitalKind, vitals, allergies,
// medications).
export * from "./personal/health/index.js";

// CircleAI.Personal.Mental — IMentalHealthBoard (Mood, mood logs, journal,
// coping strategies, 7-day trend).
export * from "./personal/mental/index.js";

// Re-assert the banking Account/account as the canonical package-root binding
// (the ambiguous `export *` above otherwise elides them). The personal-finance
// Account/account stay available via `@bhengubv/circle-ai/.../personal/finance`.
export { account, type Account } from "./banking/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// Domain boards B — people / home / logistics
// Each is an I<Domain>Board (or the CRM/Markets contract triple) + record types
// (+ any enums) + a deterministic in-memory impl, plus the vertical's static
// <Domain>DomainContext where the C# project defines one. Faithful ports of the
// CircleAI.<Domain> C# projects. (The C# *CompanionAdapter / IoTCompanionPipeline
// LLM/voice wrappers are intentionally not ported — same convention as boards A.)
// ─────────────────────────────────────────────────────────────────────────────

// CircleAI.CRM — IContactStore / IDealPipeline / IActivityLog (substring contact
// search, stage-indexed deals, per-contact activity log) + fail-closed Null*.
export * from "./crm/index.js";

// CircleAI.HR — IHRBoard (employees, leave requests + decisions, performance
// reviews with per-employee average rating).
export * from "./hr/index.js";

// CircleAI.Business — IBusinessBoard (business-unit hierarchy, KPI samples +
// latest lookup, quarterly targets + achievement ratio).
export * from "./business/index.js";

// CircleAI.Retail — IRetailBoard (products, stock, sales, same-day revenue,
// top-sellers-since ranking).
export * from "./retail/index.js";

// CircleAI.Markets — IMarketDataFeed / IInstrumentCatalog / IOrderRouter +
// OrderSide/OrderType enums (pub/sub quotes, case-insensitive catalog, rule-based
// order router) + fail-closed Null*.
export * from "./markets/index.js";

// CircleAI.Logistics — ILogisticsBoard (shipments, vehicles, route legs, and a
// distance × cost-per-km route planner).
export * from "./logistics/index.js";

// CircleAI.RealEstate — IRealEstateBoard + PropertyKind (properties, listings,
// valuations, viewings, suburb-average comparable).
export * from "./realestate/index.js";

// CircleAI.Home — IHomeBoard (rooms, smart-home devices with toggle, maintenance
// tasks with an upcoming-by-date query).
export * from "./home/index.js";

// CircleAI.IoT — IIoTBoard (device registry, telemetry + latest/history, command
// log). NOTE: no DomainContext in the C# project.
export * from "./iot/index.js";

// CircleAI.Family — IFamilyBoard (members, shared events per-member, shared
// expenses with per-payer / per-category rollups).
export * from "./family/index.js";

// CircleAI.Parenting — IParentingBoard + DayOfWeek (children, milestones, per-day
// routines, age calculation).
export * from "./parenting/index.js";

// CircleAI.Pets — IPetsBoard (pets, vaccinations, weight history, vet
// appointments with an upcoming query).
export * from "./pets/index.js";

// CircleAI.Elderly — IElderlyCareBoard (per-resident care plans, medication
// reminders, check-ins with a missed-check-in test).
export * from "./elderly/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// Domain boards C — lifestyle / civic / misc
// Each is an I<Domain>Board (or, for Games, the game-runtime contract triple) +
// record types (+ any enums) + a deterministic in-memory impl, plus the
// vertical's static <Domain>DomainContext where the C# project defines one.
// Faithful ports of the CircleAI.<Domain> C# projects. (The C# *CompanionAdapter
// LLM/voice wrappers are intentionally not ported — same convention as boards A/B.)
// ─────────────────────────────────────────────────────────────────────────────

// CircleAI.Sports — ISportsBoard + DistanceKind (activities, personal bests,
// training sessions, weekly-volume + best rollups).
export * from "./sports/index.js";

// CircleAI.Fitness — IFitnessBoard (workouts, goals, exercise sets, weekly
// workouts + calorie rollups).
export * from "./fitness/index.js";

// CircleAI.Food — IFoodBoard (recipes, ingredient search, meal logs, pantry with
// usage + expiry).
export * from "./food/index.js";

// CircleAI.Agriculture — IFarmBoard (fields, crops, yields, average-yield-of-
// variety rollup).
export * from "./agriculture/index.js";

// CircleAI.Beauty — IBeautyBoard (treatments, appointments, skin profiles,
// concern-based treatment recommender).
export * from "./beauty/index.js";

// CircleAI.Gaming — IGamingBoard (titles, play sessions, achievement unlocks,
// total-play-time + most-played rollups).
export * from "./gaming/index.js";

// CircleAI.Games — IGameLoop / IInputMap / ISceneGraph + game-runtime records
// (timer-driven loop, in-memory input map + scene graph) and fail-closed Null*.
export * from "./games/index.js";

// CircleAI.Hospitality — IHospitalityBoard (rooms, reservations, front-desk
// notes, availability + checkout).
export * from "./hospitality/index.js";

// CircleAI.Tourism — ITourismBoard (attractions, itineraries, bookings).
export * from "./tourism/index.js";

// CircleAI.Travel — ITravelBoard (flights, hotel stays, trips, trip-cost +
// upcoming rollups).
export * from "./travel/index.js";

// CircleAI.Civic — ICivicBoard (reported issues, representatives, civic events).
export * from "./civic/index.js";

// CircleAI.Community — ICommunityBoard (groups, announcements, volunteer
// opportunities).
export * from "./community/index.js";

// CircleAI.Social — ISocialBoard (posts, reactions, follows, follow-graph feed).
export * from "./social/index.js";

// CircleAI.Relationships — IRelationshipsBoard (contacts, important dates,
// last-contact tracker).
export * from "./relationships/index.js";

// CircleAI.Faith — IFaithBoard (services, prayer requests, scripture references).
export * from "./faith/index.js";

// CircleAI.Construction — IConstructionBoard (projects, tasks, cost entries,
// spend / remaining-budget rollups).
export * from "./construction/index.js";

// CircleAI.Energy — IEnergyBoard (meter readings, tariffs, outages, consumption
// + cost rollups).
export * from "./energy/index.js";

// CircleAI.Creative — ICreativeBoard (works, inspirations, critiques,
// average-critique-score rollup).
export * from "./creative/index.js";

// CircleAI.Kids — IKidsBoard + AgeAppropriateness (age-banded content, daily time
// limits, time logs, screen/reading-limit checks).
export * from "./kids/index.js";

// CircleAI.Wearable — IWearableBoard + WearableKind / WearableTelemetryKind
// (devices, telemetry samples, latest/average rollups, WearableContext snapshot).
export * from "./wearable/index.js";

// CircleAI.Wearable.Biosignals — IBiosignalSource + BiosignalKind (streaming
// sources: null + recorded), BiosignalAggregator (windowed snapshot), and the
// deterministic BiosignalAffectMapper over AffectState.
export * from "./wearable/biosignals/index.js";

// CircleAI.Accessibility — IAccessibilityBoard: AccessibilityNeed /
// UserAccessibilityProfile / AdaptationHint (profiles → derived adaptation hints).
export * from "./accessibility/index.js";

// CircleAI.Ambient — IAmbientBoard (environmental readings, comfort preferences,
// comfort test).
export * from "./ambient/index.js";

// Re-assert CRM's Activity/activity as the canonical package-root binding (the
// ambiguous `export *` above otherwise elides them). CircleAI.Sports also exports
// `Activity`/`activity` (an endurance-activity record); under `export *`
// TypeScript elides the ambiguous name from the package root, so we re-export the
// CRM (foundational contact-activity-log) variant here — mirroring the banking
// `Account`/`account` disambiguation. The Sports `Activity`/`activity` stay
// reachable via the `@bhengubv/circle-ai/.../sports` subpath.
export { activity, type Activity } from "./crm/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// Serving / agents / runtime — the CircleAI.* infrastructure boards. Each is a
// small contract-interface + record types (+ enums) + a deterministic in-memory
// implementation (and, where the C# ships one, a fail-closed Null* default).
// Faithful ports of the corresponding CircleAI.<Module> C# projects.
// ─────────────────────────────────────────────────────────────────────────────

// CircleAI.Orchestration — Loki agent-swarm: AgentTask / AgentRole / AgentPriority
// / AgentStatus, IAgentDispatcher + LocalAgentDispatcher, LokiOrchestrator
// (bounded-concurrency swarm + quality gate), IncidentTrigger, and the
// SecurityOrchestrationBridge (ISecurityWatchdog decorator).
export * from "./orchestration/index.js";

// CircleAI.Operator — Kubernetes-operator model deployment: ModelLifecyclePhase,
// IModelOperator / IDeploymentObserver, the lifecycle-driving
// InMemoryModelOperator, and Null* defaults.
export * from "./operator/index.js";

// CircleAI.Pipelines — data-pipeline source/sink/executor + a SELECT-only
// in-memory database query tool (IPipelineSource / IPipelineSink /
// IPipelineExecutor / IDatabaseQueryTool) with in-memory + Null* impls.
export * from "./pipelines/index.js";

// CircleAI.MicroAgents — IMicroAgent / IMicroAgentHost, a FuncMicroAgent lambda
// wrapper, the InMemoryMicroAgentHost registry+router, capability search, and
// an invocation log.
export * from "./microagents/index.js";

// CircleAI.Federation — federated learning: ModelDelta / FederationRound,
// IFederationParticipant / IFederationAggregator / IFederationDeltaDispatcher,
// sample-size-weighted FederatedAveraging over little-endian float payloads, and
// the InMemoryFederationAggregator.
export * from "./federation/index.js";

// CircleAI.Distribution — the four ubiquity rails in scope: IAppStoreSubmitter,
// ISignedDeltaUpdater (HMAC-verified), IOemPreloadCatalog, ICarrierPreloadCatalog
// (+ their default implementations).
export * from "./distribution/index.js";

// CircleAI.BuildFarm — BuildAgentKind / BuildJobPhase, IBuildAgentPool /
// IBuildJobRunner / IBuildArtifactStore with in-memory (acquire/release, job
// state machine) + Null* impls.
export * from "./buildfarm/index.js";

// CircleAI.Collaboration — IChannelStore / IMessageStore / IPresence with
// in-memory (team-indexed channels, per-channel newest-first messages, presence)
// + Null* impls.
export * from "./collaboration/index.js";

// CircleAI.AutonomousBiz — ITreasury / IRevenueLoop / IDecisionLog: a fan-out
// revenue pub/sub, a currency-matched running-balance treasury, an append-only
// decision log, + Null* impls.
export * from "./autonomousbiz/index.js";

// CircleAI.Workflows — durable-workflow contracts (WorkflowPhase,
// IWorkflowDefinitionStore / IWorkflowRunner / IWorkflowState with in-memory +
// Null* impls) plus the full PACA surface ported from paca: conversation state
// machine (PacaConversationRuntime), projects/tasks (InMemoryPacaStore),
// sprintboards (PacaBoard), auth (HmacJwtAuthenticator / PacaApiKeyAuthenticator),
// agents-as-members + presets (InMemoryPacaMemberStore / AgentTemplates), living
// docs (PacaDocService), plugins (PacaPluginRegistry), MCP server (PacaMcpServer),
// realtime fan-out (PacaRealtimeHub), skills (PacaSkillLibrary / installer), and
// single-command deploy (PacaDeployer).
export * from "./workflows/index.js";

// CircleAI.Realtime — carrier-agnostic streaming realtime AI contracts
// (RealtimeSessionConfig, IRealtimeSession, the RealtimeEvent union), the
// in-process LoopbackRealtimeService, and fail-closed Null* defaults.
export * from "./realtime/index.js";

// CircleAI.Realtime.Cloud — the 5 vendor connectors (OpenAiRealtimeService,
// GeminiLiveService, NovaSonicService, ElevenLabsConvService, UltravoxService)
// behind an injected WebSocket/HTTP transport seam, plus the shared
// RealtimeWebSocketSession that demuxes vendor JSON envelopes.
export * from "./realtime-cloud/index.js";

// `TranscriptFinalEvent` is exported by BOTH CircleAI.Realtime and
// CircleAI.Telephony (its speech-lifecycle event). Under `export *` TypeScript
// elides the ambiguous name; the realtime variant is the canonical package-root
// binding (re-asserted here), matching the runtime/skills/inference precedent
// above. The telephony `TranscriptFinalEvent` stays reachable via the
// `@bhengubv/circle-ai/.../telephony` subpath.
export type { TranscriptFinalEvent } from "./realtime/index.js";

// CircleAI.Vision — the on-device vision stack's ONNX-backed components: face
// detection (OnnxFaceDetector), license-plate recognition (OnnxPlateRecognizer)
// and face embedding (OnnxFaceEmbedder), each behind an injected ONNX-runtime
// (IOnnxSession) + image-codec (ImageDecoder) seam, plus the shared BoundingBox
// / DetectedFace / FaceEmbedding primitives and the letterbox + NMS pipeline.
// (The ONNX seam types DenseTensor / IOnnxSession / OnnxSessionFactory are NOT
// re-exported here — voice already owns those names at the package root; use the
// ./vision subpath for the vision seam.)
export * from "./vision/index.js";

// CircleAI.Plugins — plugin contract surface: IPlugin / IPluginContext /
// IPluginEvents (+ the thread-safe PluginEvents bus + PluginEventNames), the
// default PluginContext, and the permission-gated PermissionedPluginContext.
export * from "./plugins/index.js";

// CircleAI.Runtime — native-runtime + backend selection. NOTE: its `BackendKind`
// / `CapabilityTier` enums collide by name with the CircleAI.Inference bridge
// enums already exported at the package root. Under `export *` TypeScript elides
// the ambiguous names, so the inference bridge variants (pre-existing public
// surface) remain the canonical package-root binding, re-asserted below. The
// CircleAI.Runtime `BackendKind` / `CapabilityTier` stay reachable via the
// `@bhengubv/circle-ai/.../runtime` subpath. (The two `BackendKind` enums are
// value-identical; the `CapabilityTier` enums describe related device/model
// bands.)
export * from "./runtime/index.js";
export { BackendKind, CapabilityTier } from "./inference/server/index.js";

// CircleAI.Skills — the canonical B!-skill store: SkillSource / SkillSummary /
// SkillDetail / SkillDraft, ISkillStore + InMemorySkillStore, SkillPackSource /
// KnownSkillPacks, and the IPackDownloader strategy. NOTE: several of these
// names (`SkillSource`, `SkillDetail`, `SkillSummary`, `SkillDraft`,
// `ISkillStore`, `InMemorySkillStore`) collide with the narrower
// CircleAI.Hosting skills-enrichment seam already exported at the package root.
// Under `export *` TypeScript elides the ambiguous names, so the hosting seam
// (pre-existing public surface) stays the canonical package-root binding,
// re-asserted below. The full CircleAI.Skills variants — including the
// non-colliding SkillPackSource / KnownSkillPacks / IPackDownloader /
// InMemoryPackDownloader / SkillPackSourcesOptions — remain reachable via the
// `@bhengubv/circle-ai/.../skills` subpath.
export * from "./skills/index.js";
export { SkillSource, InMemorySkillStore } from "./hosting/index.js";
export type { ISkillStore, SkillSummary, SkillDetail, SkillDraft } from "./hosting/index.js";

// ─────────────────────────────────────────────────────────────────────────────
// Knowledge / perception / dev-tools — the CircleAI.* research/perception/tooling
// boards. Each is a small contract-interface surface + record types (+ enums) +
// deterministic in-memory implementations (and fail-safe Null* defaults where the
// C# ships them). Faithful ports of the corresponding CircleAI.<Module> C# projects.
// ─────────────────────────────────────────────────────────────────────────────

// CircleAI.Knowledge — markdown-on-disk knowledge notes: YamlFrontmatter (flat
// key→value reader/writer), KnowledgeNote (+ ToFileText / ParseFile round-trip),
// IKnowledgeStore + FileSystemKnowledgeStore (one .md per note, atomic
// write-then-rename, per-Guid mutex), and MarkdownEpisodicMemoryStore
// (IEpisodicMemoryStore over an IKnowledgeStore).
export * from "./knowledge/index.js";

// CircleAI.Search — search-relevance helpers: SearchTokenisation / SearchScoring
// (term-frequency + simple relevance) and VectorMath / SimdOps (single-precision
// cosine similarity; the C# SIMD fast-path is an unobservable perf detail).
export * from "./search/index.js";

// CircleAI.Personality — the user-DECLARED persona artefact (distinct from the
// learned CircleAI.Memory.PersonaState): Persona / FormalityRange / PrivacyLevel,
// IPersonaProvider + JsonPersonaProvider (one {userId}.persona.json per user),
// IPersonaConflictResolver + DeclaredWinsResolver / LearnedWinsResolver, and the
// PersonaPromptBuilder (JSON-quoted, prompt-injection-hardened system-hint block).
export * from "./personality/index.js";

// CircleAI.Research — research corpora: IResearchCorpus / IPaperRetrieval /
// ICitationGraph over ResearchPaper / Citation records, deterministic in-memory
// implementations, and fail-safe Null* defaults.
export * from "./research/index.js";

// CircleAI.Domain — domain-specialist plug points: IFoodEmbeddings /
// IFinanceRetrieval / IFinancialAgent / IHippoRagStore / IMemPalaceStore /
// IJobSearchPipeline / IPresentationGenerator / ISwarmCoordinator / IPersonalLoRA,
// with in-memory / template / multi-pass impls and Null* defaults.
export * from "./domain/index.js";

// CircleAI.Observer — the perceive-reason-act observation loop: ISensor /
// IObservationToolbox / IObservationLoop over ObservationTick / SensorReading /
// ObservationTool / ObserverDecision, with in-memory impls, a SensorRecorder, and
// Null* defaults. NOTE: its `Disposable` (a `{ dispose(): void }` subscription
// handle) collides by name with the operator/autonomousbiz/plugins `Disposable`
// already canonicalised at the package root; under `export *` TypeScript elides
// the ambiguous name, so the operator variant (re-asserted below) stays canonical
// and the observer variant remains reachable via the ./observer subpath.
export * from "./observer/index.js";

// CircleAI.Observability — metric sink, trace sink, dashboard publisher:
// IMetricSink / ITraceSink / IDashboardPublisher over MetricSample / TraceSpan,
// with in-memory impls and Null* defaults.
export * from "./observability/index.js";

// CircleAI.Spatial — spatial / geo contract surface: IGeoTileSource /
// IRadarReadout / ISkyTracker / I3DSceneRenderer + record types, with
// deterministic in-memory impls and Null* defaults.
export * from "./spatial/index.js";

// CircleAI.Visualization — dashboard-definition store, API-doc builder,
// static-site builder: IDashboardDefinitionStore / IApiDocBuilder / ISiteBuilder
// + record types, with in-memory impls and Null* defaults.
export * from "./visualization/index.js";

// CircleAI.Inputs — input-adapter contracts: IWebScraper / IStealthHttpClient /
// IVideoIngest / IMcpWebScrape / ITerminalCast + record types, over injected
// transports (no real network) with in-memory / recorded impls and Null* defaults.
export * from "./inputs/index.js";

// CircleAI.CodeUnderstanding — code-understanding contracts: ICodeIndexer /
// ICodeSearch / ISymbolGraph + record types, with a filesystem indexer, in-memory
// impls, and Null* defaults.
export * from "./code-understanding/index.js";

// CircleAI.DevTools — the dev-tools replacement surface: ICodeEditor /
// IInlineSuggester / IAgentShell / IPatchPlanner / IRefactorTool + record types,
// with filesystem/regex/token-context impls and Null* defaults.
export * from "./dev-tools/index.js";

// CircleAI.DepBot — dependency analyzer + updater: IDependencyAnalyzer /
// IDependencyUpdater + record types, with deterministic in-memory impls and Null*
// defaults.
export * from "./depbot/index.js";

// CircleAI.SDD — Spec-Driven Development contracts: ISpecificationStore /
// ISpecificationValidator / ISpecToScaffold + record types, with in-memory impls
// and Null* defaults.
export * from "./sdd/index.js";

// CircleAI.DocAnalytics — document-analytics contracts: IDocumentTracker /
// IDocumentInsights + record types, with deterministic in-memory impls and Null*
// defaults.
export * from "./doc-analytics/index.js";

// CircleAI.Simulation — offline network-health simulation over a knowledge graph
// extracted from episodic memory: GraphNode / GraphEdge / KnowledgeGraph,
// ScenarioKind / SimulationScenario, SimulationOutcome / SimulationResult,
// IGraphBuilder + EpisodicGraphExtractor, ISimulationEngine + LocalSimulationEngine
// + MiroFishAdapter, the NetworkHealthSimulator facade, and the
// ThreatPropagationScenario (Security ↔ Simulation) bridge.
export * from "./simulation/index.js";

// Re-assert a single canonical `Disposable` at the package root. CircleAI.Operator,
// CircleAI.AutonomousBiz, CircleAI.Plugins, and CircleAI.Observer each export a
// value-identical `Disposable` (a `{ dispose(): void }` handle); under `export *`
// TypeScript elides the ambiguous name, so we re-export the operator variant here.
// The others stay reachable via their subpaths.
export type { Disposable } from "./operator/index.js";
