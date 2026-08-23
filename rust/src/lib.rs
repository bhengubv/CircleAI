//! Circle AI portable core — Rust port.
//!
//! All modules are public; consumers pick what they need.

#![allow(dead_code)]
#![allow(clippy::type_complexity)]
#![allow(clippy::excessive_precision)]

pub mod aether;
pub mod aethernet;
pub mod agents;
pub mod banking;
pub mod brain;
pub mod business;
pub mod catalog;
pub mod commerce;
pub mod commerce_accounting;
pub mod commerce_finance;
pub mod commerce_payfast;
pub mod commerce_xero;
pub mod companion;
pub mod content_policy;
pub mod crm;
pub mod device;
pub mod education;
pub mod elderly;
pub mod family;
pub mod healthcare;
pub mod home;
pub mod hr;
pub mod iot;
pub mod legal;
pub mod logistics;
pub mod markets;
pub mod parenting;
pub mod pets;
pub mod personal_finance;
pub mod personal_health;
pub mod personal_mental;
pub mod real_estate;
pub mod retail;
pub mod embeddings;
pub mod embeddings_local;
pub mod hosting;
pub mod hosting_cloud_fallback;
pub mod hosting_inference_bridge;
pub mod hosting_mcp;
pub mod hosting_multiplayer;
pub mod identity;
pub mod inference;
pub mod inference_server;
pub mod languages;
pub mod memory;
pub mod model_alignment;
pub mod model_runtime;
pub mod models;
pub mod models_v15;
pub mod networking;
pub mod networking_transports;
pub mod proactive;
pub mod prompt;
pub mod registry;
pub mod safety;
pub mod safety_child;
pub mod security;
pub mod security_aethernet;
pub mod selector;
pub mod sync;
pub mod sync_service;
pub mod tools;

// Domain boards C: lifestyle / civic / misc (ports of CircleAI.<Domain>).
pub mod accessibility;
pub mod agriculture;
pub mod ambient;
pub mod beauty;
pub mod civic;
pub mod community;
pub mod construction;
pub mod creative;
pub mod energy;
pub mod faith;
pub mod fitness;
pub mod food;
pub mod games;
pub mod gaming;
pub mod hospitality;
pub mod kids;
pub mod relationships;
pub mod social;
pub mod sports;
pub mod tourism;
pub mod travel;
pub mod wearable;
pub mod wearable_biosignals;

// CircleAI.Personality — user-DECLARED persona artefact + provider/resolver/prompt.
pub mod personality;
// CircleAI.Federation — federated-learning round bookkeeping + averaging.
pub mod federation;
// CircleAI.Skills — persistent skill store + SKILL.md pack loader/importer.
pub mod skills;
// CircleAI.Distribution — file-sync contracts + 77 ubiquity rails.
pub mod distribution;
// CircleAI.Vision — face / document / plate / BLE contracts + ONNX backends.
pub mod vision;
// CircleAI.Telephony — carrier-agnostic voice-loop surface + DSP + orchestration.
pub mod telephony;
// CircleAI.Workflows — durable-workflow contracts + the paca project/agent runtime.
pub mod workflows;
// CircleAI.Speech — ASR/TTS/wake-word/OCR contracts + VAD/EOT/AEC/NR/codec DSP.
pub mod speech;
// CircleAI.Runtime — deterministic hardware -> MNN-backend routing + host-capability probe seam.
pub mod runtime;
// CircleAI.Orchestration — semaphore-bounded agent swarm + quality gate + incident/security bridges.
pub mod orchestration;
// CircleAI.Tools.Catalog — provider directory / credential store / OAuth2 driver / quota guard / namespaces.
pub mod tools_catalog;
// CircleAI.Simulation — simple entity-relationship graph + offline network-health forecaster.
pub mod simulation;
// CircleAI.Knowledge — markdown-on-disk knowledge notes + episodic store (real fs I/O).
pub mod knowledge;
// CircleAI.Speech.Cloud — regex-based voice intent router.
pub mod speech_cloud;
// CircleAI.Voice — on-device capture/VAD/transcribe/wake-word pipeline over injected seams.
pub mod voice;
pub mod voice_piper;
pub mod voice_wav;
pub mod voice_text;
pub mod voice_xsampa;
// CircleAI.Embeddings.Local.HnswEmbeddingStore — index-backed store over the IEmbeddingIndex seam.
pub mod embeddings_local_hnsw;
// CircleAI.Hosting.VoiceOptions — voice-pipeline config DTO.
pub mod hosting_voice;
// CircleAI.Media — audio/video/image asset catalogue + in-memory library.
pub mod media;
// CircleAI.DocAnalytics — document view tracker + on-demand insights.
pub mod doc_analytics;
// CircleAI.Integration — deterministic in-memory reference connectors
// (calendar / email / news / weather / routing / home-automation).
pub mod integration;

// Convenience re-exports so downstream crates can write `circle_ai::AffectState`.
pub use companion::{
    AnticipatedNeed, BayesianWorldModel, BeliefTrackerTheoryOfMind, CausalPrediction,
    CompanionContext, CompanionProactiveEvent, CompanionTurn, FrequencyWorldModel,
    HistogramPredictiveEngine, IInnerMonologue, IPredictiveEngine, ITheoryOfMind, IWorldModel,
    InterfaceKind, OtherMindEstimate, ReasoningLoopInnerMonologue, SelfReflection,
    SequencePredictiveEngine, TemplateInnerMonologue,
};

// HER/Jarvis companion cognition + proactive companion surface (flat access).
pub use companion::{
    AbBenchRunner, AbVerdict, AcquiredSkill, AdjacencyPersonalKnowledgeGraph, AgentToAgentMessage,
    BenchSummary, BenchTask, BioSignal, CapabilityEntry, ChannelBioSignalStream,
    ChannelFusedPerception, CodeGenJob, CompanionSessionFactory, ConfidenceBand,
    DelegationCredential, DemoStoreSkillAcquisition, EmotionFrame, EnergyBandVoiceIdentity,
    EpisodeRecord, EwaContinuousLearner, ExternalCapabilityRegistry, FineTuneJobStatus,
    FirstTokenBudget, FusedPercept, HeartbeatAlwaysOnPresence, HistoricalCalibratedConfidence,
    HmacCryptoDelegation, IAbBenchRunner, IAgentPeerNetwork, IAlwaysOnPresence, IBenchModel,
    IBenchSuiteRegistry, IBioSignalStream, IBriefingNotifier, ICalibratedConfidence,
    ICodeGenerationLoop, ICompanionSessionFactory, IContinuousLearner, ICryptoDelegation,
    IEmotionSensor, IEpisodicMemory, IFederatedFineTuner, IFirstTokenOptimizer, IFusedPerception,
    IGoalPursuer, IIdentitySync, ILiveWorldKnowledge, IPersonalKnowledgeGraph, IPhysicalActuator,
    ISelfImprovementLoop, ISkillAcquisition, IVoiceIdentity, IVoiceListener,
    InMemoryBenchSuiteRegistry, InMemoryFederatedFineTuner, InMemoryGoalPursuer, JsonIdentitySync,
    KeywordEmotionSensor, LongHorizonGoal, MailboxAgentPeerNetwork, PhysicalCommand,
    PhysicalCommandResult, ProactiveBriefingOptions, ProactiveBriefingService,
    RegistryPhysicalActuator, RegressionGateConfig, SelfBenchSelfImprovementLoop,
    SelfImprovementVerdict, SlidingP50FirstTokenOptimizer, SyntaxCheckingCodeGenerationLoop,
    TfEpisodicMemory, TopicLiveWorldKnowledge, TrackingSelfImprovementLoop, VoiceCompanionListener,
    WorldFact,
};

// Proactive scheduling substrate (flat access).
pub use proactive::{
    CronExpression, DelegateProactiveTaskRunner, IProactiveScheduler, IProactiveTaskRunner,
    IProactiveTaskSource, InMemoryProactiveTaskSource, NullProactiveTaskRunner,
    NullProactiveTaskSource, ProactiveScheduler, ProactiveTask, ProactiveTaskLoadError,
    ProactiveTaskRunResult, ProactiveTrigger,
};
pub use identity::{CircleIdentity, IdentityTier, RegisteredDevice};
pub use inference::{ChatMessage as InferenceChatMessage, GenerationOptions, IChatGenerator, PowerBudget};

// CircleAI.Inference runtime gaps (flat access).
pub use inference::capability::{ChatCapability, VisionInput};
pub use inference::chat_generator::{
    build_qwen_chat_prompt, default_stop_sequences, extract_system_prompt, ChatResponse,
    DeterministicChatGenerator, FinishReason,
};
pub use inference::context_budget::{BudgetError, ContextWindowBudgetManager};
pub use inference::download_service::{
    strip_sha_algorithm_prefix, BundleFileSpec, DownloadServiceError, IContentFetcher,
    IFileStore, IModelDownloadService, InMemoryContentFetcher, InMemoryFileStore,
    ModelDownloadService,
};
pub use inference::feedback_queue::{
    FeedbackQueueError, IFeedbackTrainingQueue, InMemoryFeedbackTrainingQueue, TrainingSample,
};
pub use inference::kv_compression::{
    IKvCompressionHandle, InMemoryKvCompressionHandle, KvCompressionApplyResult, KvCompressionMode,
    MnnKvCompression, PowerBudgetPolicy, Resolution as PowerBudgetResolution,
};
pub use inference::layer_streaming::{
    discover_layer_shards, ILayerStreamingRunner, LayerActivations, LayerStreamingError,
    LayerStreamingOrchestrator, LayerStreamingPlan, LayerWeightShard, NullLayerStreamingRunner,
};
pub use inference::nightly_trainer::{
    char_tokenizer, ILoRAAdapterManager, InMemoryLoRAAdapterManager, NightlyAdapterTrainer,
    NightlyAdapterTrainerOptions, RunOnceResult, TrainStepError,
};
pub use inference::prefix_cache::PrefixCacheService;
pub use languages::{DetectionResult, KnownLanguages, LanguageTag, ScriptNormalisationResult, WritingSystem};
pub use memory::{
    AffectState, AffectVad, EpisodicMemoryEntry, FeedbackPolarity, FeedbackSignal, Goal,
    GoalPriority, GoalStatus, InMemoryGoalStore, PersonaState,
};

// Companion-state cross-device sync layer + memory pipeline runtime (flat access).
pub use memory::{
    CompanionConversationSyncBridge, CompanionRuntime, CompanionRuntimeOptions,
    CompanionStateSyncEngine, ConversationStateDelta, HybridLogicalClock, ICompanionStateChannel,
    ICompanionStateSyncEngine, ISyncableEntryStore, InMemorySyncableEntryStore,
    InProcessCompanionStateChannel, InProcessSyncHub, LoraAdapterSnapshot, LoraAdapterSyncBridge,
    PersonaStateSyncBridge, RequestItem, StateVectorEntry, SyncEnvelope, SyncEnvelopeKind,
    SyncableEntry,
};
// CircleAI.Security — local immune system + transport-agnostic peer security
// pipeline (flat access).
pub use security::{
    confidence_band, hash_redacted, redact_evidence, serialize_redacted, to_redacted_json,
    AnomalyDispatchOutcome, AnomalyDispatchResult, AnomalySignal, DefaultAnomalyEventDispatcher,
    DefaultSecurityWatchdog, DirectivePublisher, DirectiveSubscription, IAnomalyEventDispatcher,
    IPeerDirectiveConsumer, IPeerIntelligence, IPeerSecurityEventFeed, IPeerSecurityLayer,
    ISecurityWatchdog, KeyRingError, NodeTrustEntry, NodeTrustRegistry, PeerDirective,
    PeerDirectiveKind, PeerIntelligenceService, PeerNetworkHealthReport, PeerRoutingAdvice,
    PeerSecurityEvent, PeerSecurityEventKind, PeerSecurityPosture, PeerThreatAssessment,
    PeerThreatLevel, PeerTrustScoreUpdate, SecurityCheckpoint, SecurityLayerService,
    SecurityOptions, SecurityResponse, SecurityResponseKind, ThreatDetector, ThreatVector,
    UhidKeyRing, RECOVERY_INTERVAL_SECONDS,
};
pub use runtime::{
    ArchitectureKind, BackendKind, BackendSelection, BackendSelector, CapabilityProbe,
    CapabilityTier, GpuInfo, GpuVendor, HostProfile, IBackendSelector, NpuInfo, NpuVendor,
    OperatingSystemKind, UnknownCapabilityProbe,
    // The host-capability probe trait — flat-aliased to avoid colliding with
    // `inference_server::ICapabilityProbe` (a distinct inference-bridge trait).
    ICapabilityProbe as IHostCapabilityProbe,
};
pub use orchestration::{
    AgentHandler, AgentPriority, AgentRole, AgentStatus, AgentSwarmConfig, AgentTask,
    IAgentDispatcher, IncidentTrigger, LocalAgentDispatcher, LokiOrchestrator, QualityGateResult,
    SecurityOrchestrationBridge, SwarmResult,
};
pub use agents::{
    AgentBus, AgentCapability, AgentInvocationError, AgentMessage, AgentMessageKind,
    CapabilityHandlerFn, IAgentPeerProtocol, InMemoryAgentPeerProtocol, PeerAgent, SignerFn,
};
pub use tools_catalog::{
    AesGcmCredentialStore, AuthKind, CatalogError, ClientIdResolver, CredentialBundle,
    ICredentialCipher, ICredentialStore, IOAuth2FlowDriver, IProviderCatalog, IQuotaGuard,
    IToolNamespaceStore, InMemoryProviderCatalog, InMemoryToolNamespaceStore, NullCredentialStore,
    NullOAuth2FlowDriver, NullProviderCatalog, NullQuotaGuard, NullToolNamespaceStore,
    OAuth2Descriptor, OAuth2FlowDriver, ProviderDescriptor, QuotaPolicy, SlidingWindowQuotaGuard,
    ToolNamespace, TokenExchangeFn, XorObfuscationCipher,
};
pub use models::{ChatMessage, DownloadProgress};
pub use sync::{
    SyncDeliveryMode, SyncDelta, SyncDomainKeys, SyncReconciliation, VersionVector,
};
pub use sync_service::{IMemorySyncService, MemorySyncError, MemorySyncService};

// CircleAI.Personality — declared persona artefact + storage/resolution/prompt (flat access).
pub use personality::{
    DeclaredWinsResolver, FormalityRange, IPersonaConflictResolver, IPersonaProvider,
    JsonPersonaProvider, LearnedWinsResolver, Persona, PersonaPromptBuilder, PersonaProviderError,
    PrivacyLevel,
};

// CircleAI.Federation — federated-learning rounds + averaging (flat access).
pub use federation::{
    federated_averaging, DefaultFederationDeltaDispatcher, DeltaDispatchOutcome, FederationError,
    FederationRound, IFederationAggregator, IFederationDeltaDispatcher, IFederationParticipant,
    InMemoryFederationAggregator, ModelDelta, RoundStatus,
};

// CircleAI.Networking — the transport ABSTRACTION the 10 concrete transports
// implement (flat access). `SyncDeliveryMode` is reused from `sync` (same type),
// so it is not re-exported here; the networking `SyncDelta` (which carries a
// `SchedulingHint`) is re-exported as `NetworkSyncDelta` to avoid clashing with
// the `sync::SyncDelta` already re-exported above.
pub use networking::{
    BuiltPolicy, CascadeTransportSelector, ChannelSubscription, ConnectivityState, ContextHandler,
    DefaultNetworkPolicy, DiscoverySubscription, IConnectivityMonitor, IMeshNetwork,
    IMessageChannel, INetworkPolicy, INetworkTransport, IPayloadOptimiser, IPeerDiscovery,
    ISyncChannel as INetworkSyncChannel, ITransportSelector, InMemoryMeshNetwork,
    InMemoryMessageBus, InMemoryMessageChannel, InMemoryNetworkTransport, InMemoryPeerDiscovery,
    InMemorySyncChannel, ManualConnectivityMonitor, MessageChannelError, MessagePriority,
    NetworkContext, NetworkPayload, NetworkPolicyBuilder, PayloadHandler, PeerHandler, PeerInfo,
    PeerRole, RlePayloadOptimiser, SchedulingHint, SyncDelta as NetworkSyncDelta, TransportError,
    TransportKind, TransportSubscription, WatchSubscription, DEFAULT_CASCADE,
};

// CircleAI.Networking.{AetherNet,Bluetooth,Dtn,Grpc,Http} — concrete transports
// implementing the networking core `INetworkTransport` (flat access). Wave
// "Networking transports A". Each ports one C# transport package; the socket /
// native / mesh / cloud dependency is injected behind a trait with a working
// in-memory implementation. Wave "Networking transports B" adds
// Mqtt/NearLink/Tcp/WebSocket/WiFi under the same discipline.
pub use networking_transports::{
    // AetherNet
    AetherAvailability, AetherHopTelemetry, AetherNetworkTransport, AetherPacketSummary,
    AetherPeer, AetherPeerDiscovery, AetherPeerKind, AetherSyncChannel, FixedAetherAvailability,
    IAetherRouter, InMemoryAetherNetRegistry, InMemoryAetherRouter, AETHER_DTN_DEFAULT_TTL,
    // Bluetooth
    BluetoothCapabilityProfile, BluetoothCapabilityProfiles, BluetoothConnectionState,
    BluetoothEndpointDescriptor, BluetoothNetworkTransport, BluetoothThroughputSample,
    IBleGattAdapter, InMemoryBleGattAdapter, InMemoryBluetoothTransportRegistry, InboundSink,
    // Dtn
    DtnBundle, DtnCustodyRecord, DtnPriority, DtnSyncChannel, InMemoryDtnBundleStore,
    DTN_DEFAULT_TTL,
    // Grpc
    GrpcCallSummary, GrpcChannelDescriptor, GrpcChannelState, GrpcNetworkTransport,
    GrpcRetryPolicies, GrpcRetryPolicy, IGrpcChannel, InMemoryGrpcCallMetrics, InMemoryGrpcChannel,
    GRPC_SEND_NOT_SUPPORTED,
    // Http
    HttpCacheKey, HttpEndpointDescriptor, HttpNetworkTransport, HttpPostRequest, HttpPostResult,
    HttpRequestSummary, HttpSendError, HttpStatusFamily, IHttpMessageSender,
    InMemoryHttpMessageSender, InMemoryHttpRequestMetrics,
    // Mqtt (Wave B)
    IMqttClient, InMemoryMqttBroker, InMemoryMqttClient, MqttClientDescriptor, MqttInboundSink,
    MqttNetworkTransport, MqttPublish, MqttQos, MqttRetainedMessage, MqttTopicDescriptor,
    // NearLink (Wave B)
    INearLinkAdapter, InMemoryNearLinkAdapter, InMemoryNearLinkRegistry, NearLinkDevice,
    NearLinkInboundSink, NearLinkPairingState, NearLinkPowerProfile, NearLinkSession,
    NearLinkThroughputSample, NearLinkTransport,
    // Tcp (Wave B)
    ITcpConnection, InMemoryTcpConnection, InMemoryTcpConnectionRegistry, TcpConnectionState,
    TcpEndpointDescriptor, TcpInboundSink, TcpKnownPorts, TcpNetworkTransport, TcpThroughputSample,
    // WebSocket (Wave B)
    IWebSocket, InMemoryWebSocket, InMemoryWebSocketSessionRegistry, WebSocketEndpointDescriptor,
    WebSocketFrameSummary, WebSocketInboundSink, WebSocketLinkState, WebSocketMessageType,
    WebSocketTransport,
    // WiFi (Wave B)
    IWiFiDatagramSocket, InMemoryWiFiDatagramSocket, WiFiDatagram, WiFiInboundSink,
    WiFiNetworkTransport, WiFiPeerDiscovery, BEACON_MAGIC, BROADCAST_ADDR, DATA_PORT,
    DISCOVERY_PORT,
};
pub use tools::{ToolDefinition, ToolInvocation, ToolParameter, ToolResult};

// CircleAI.Core model-management runtime (flat access). `DownloadProgress` is
// re-exported as `SourceDownloadProgress` to avoid clashing with the existing
// `models::DownloadProgress`.
pub use model_runtime::{
    outcomes, BundleFileEntry, BundleFileInfo, CircleAIAuditEntry, CircleAIAuditQuery,
    CircleAIAuditing, CircleEngine, ContentProvider, DotNetRandom,
    DownloadProgress as SourceDownloadProgress, DownloadProgressReport, HuggingFaceSource,
    ICircleAIAuditLog, ICircleAITenantContext, ICircleModule, IEmbeddingService, IModelDownloader,
    IModelLoader, IModelManager, IModelSource, InMemoryContentProvider, InMemoryNativeLoader,
    InteropError, LocalModelLoader, LocalModelManager, LoggerAuditLog, ModelDownloader,
    ModelEntry as ModelRuntimeEntry, ModelInfo as LoaderModelInfo, ModelLoaderError,
    ModelScopeSource,
    NativeModelLoader, NoTenantError, NoopAuditLog, NullTenantContext, PlatformInterop,
    SafeModelHandle, ShardCodecError, ShardCompressedFrame, ShardKvCodec, SingleTenantContext,
    SourceDownloadHelper, SourceError,
};

// CircleAI.Embeddings (flat access).
pub use embeddings::{
    BackendFactory, EmbedderError, HashingEmbeddingBackend, IEmbeddingBackend, ITextEmbedder,
    TextEmbedder,
};

// CircleAI.Embeddings.Local (flat access).
pub use embeddings_local::{
    EmbeddingDocument, EmbeddingIndexHit, EmbeddingSearchHit, EmbeddingStoreError,
    ICircleEmbeddingStore, IEmbeddingEncoder, IEmbeddingIndex, InMemoryEmbeddingStore,
};

// CircleAI.Embeddings.Local.HnswEmbeddingStore (flat access; index-backed store).
pub use embeddings_local_hnsw::HnswEmbeddingStore;

// CircleAI.Simulation (flat access). The simple entity-relationship graph is
// re-exported as `SimKnowledgeGraph` to avoid colliding with the HippoRAG
// `memory::KnowledgeGraph`.
pub use simulation::{
    EpisodicGraphExtractor, GraphEdge, GraphNode, IGraphBuilder, ISimulationEngine,
    KnowledgeGraph as SimKnowledgeGraph, LocalSimulationEngine, NetworkHealthSimulator,
    ScenarioKind, SimulationOutcome, SimulationResult, SimulationScenario,
};

// CircleAI.Knowledge (flat access). `KnowledgeNote` here is the markdown note
// type (distinct from the HippoRAG `memory::KnowledgeNode`).
pub use knowledge::{
    FileSystemKnowledgeStore, IKnowledgeStore, KnowledgeError, KnowledgeNote,
    MarkdownEpisodicMemoryStore,
};

// CircleAI.Speech.Cloud voice intent router (flat access).
pub use speech_cloud::{
    IVoiceIntentRouter, KeywordVoiceIntentRouter, NullVoiceIntentRouter, VoiceIntent,
    VoiceIntentMatch,
};

// CircleAI.Hosting.VoiceOptions (flat access).
pub use hosting_voice::VoiceOptions;

// CircleAI.Voice: NOT flat-exported — its `AudioFormat` / `VadSegment` /
// `TranscriptionResult` / `IWakeWordDetector` / `IVoiceActivityDetector` types
// deliberately shadow the richer `CircleAI.Speech` surface, so they are reached
// path-qualified via `circle_ai::voice::*`.

// CircleAI.Hosting runtime (flat access). The endpoint/observer/service surface
// lives under `hosting::` and is re-exported flat here.
pub use hosting::{
    AIApiClient, AIChatEvent, AIHttpClient, AIService, AIStreamEvent, AIToolEvent,
    AetherAIObserver, ArrivalForecast, BackgroundInferenceWorker, BrownoutReason, CronJob,
    CronJobState, CronScheduleError, CronScheduleParser, DeliveryTarget,
    DeviceContext as HostDeviceContext, FallbackAIService, FixedRamProbe, HostAIOptions,
    HistogramRequestPredictor, HostingError, HttpLoopbackEndpoint, HttpRequest, HttpResponse,
    IAIEndpoint, IAIObserver, IAIService, IAffectStore, IButlerTransport, IGenerativeUIRenderer,
    IGoalStore, IHostChatGenerator, IHostEpisodicStore, IHostFeedbackStore, IHostPersonaStore,
    IHostToolBridge, IMemoryPressureSource, IProactiveReasoningService, IPushNotificationSender,
    IRamProbe, IRequestPredictor, IScheduledTaskStore, IThermalSampler, IThermalThrottleService,
    IToolCatalog, IToolExecutor, IToolProvider, ITriggerCondition, ICircleAetherTransport,
    IdleTrigger, InMemoryAffectStore as HostInMemoryAffectStore,
    InMemoryGoalStore as HostInMemoryGoalStore, InMemoryScheduledTaskStore,
    InMemoryToolCatalog, InProcessEndpoint, InProcessLoopbackTransport, JobCompletedEventArgs,
    JsonRenderParser, ManualMemoryPressureSource, ManualThermalSampler, MemoryPressureLevel,
    NullAIObserver, NullMemoryPressureSource, PredictiveWarmupController, PredictiveWarmupOptions,
    PressureSubscription, ProactiveContext, ProactiveMessageEventArgs, ProactiveReasoningService,
    PublishedMessage, PushAIObserver, RecordingButlerTransport, RecordingCircleAetherTransport,
    RecordingGenerativeUIRenderer, RecordingPushNotificationSender, RenderParseError,
    ScheduleTrigger, ScheduledAIService, SentPush, ThermalState, ThermalThrottleService,
    ToolDescriptor as HostToolDescriptor, ToolExecutionResult, UiCatalogEntry, UiCatalogs,
    UiComponent,
};

// CircleAI.Hosting.CloudFallback (flat access).
pub use hosting_cloud_fallback::{
    ALL_BRAINS_FAILED_FRAME, BackupBrainOrchestrator, BackupBrainPolicy, BrainHealth, BrainStatus,
    CloudChatMessage, CloudFallbackChain, FakeCloudGenerator, GeneratorEntry, ICloudChatGenerator,
    IConfigurableChatGenerator, NO_GENERATOR_FRAME,
};

// CircleAI.Hosting.InferenceBridge (flat access). `ModelDescriptor` /
// `ModelFormat` re-exported with a `Bridge` prefix to avoid clashing with the
// model-runtime types.
pub use hosting_inference_bridge::{
    BridgeChatResponse, BridgeGenerationOptions, DeviceCapabilities, FakeBridgeGenerator,
    FixedCapabilityProbe, IBridgeChatGenerator, ICapabilityProbe, IInferenceBridge,
    InferenceFragment, InferenceFragmentKind, InferenceRequest, InferenceResponse, InferenceStatus,
    LocalProcessInferenceBridge, MockInferenceBridge, ModelDescriptor as BridgeModelDescriptor,
    ModelFormat as BridgeModelFormat,
};

// CircleAI.Hosting.Mcp (flat access).
pub use hosting_mcp::{
    FnMcpTool, IMcpResourceProvider, IMcpTool, InMemoryResourceProvider, McpRegistry, McpResource,
    McpResourceContent, McpServerInfo, McpToolError,
};

// CircleAI.Hosting.Multiplayer (flat access).
pub use hosting_multiplayer::{
    colour_for, EditOutcome, GuestPeerIdentity, HubBroadcast, IMultiplayerPeerIdentity,
    MultiplayerHub, PeerState,
};

// CircleAI.Aether — the five one-way BhenguAI ↔ Aether mesh contracts (flat
// access). The aether `DirectiveSubscription` is re-exported as
// `AetherDirectiveSubscription` to avoid clashing with the peer-security
// `security::DirectiveSubscription`.
pub use aether::{
    AetherInstallLevel, AetherNetworkEvent, AetherNetworkEventKind, AetherNodeEvent,
    AetherNodeEventKind, AetherNodeHealth, AetherRouteEvent, AetherRouteEventKind,
    AetherSecurityEvent, AetherSecurityEventKind, AetherThreatLevel, AetherTransportEvent,
    AetherTransportEventKind, AetherTransportKind, AetherVersion, AuthChallengeReason,
    AuthChallengeResult, AuthMethod, IAISecurityLayer, IAetherContext, IAetherIntelligence,
    IAetherTelemetry, IAetherTelemetryObserver, IAuthChallenge, ISecurityDirectiveConsumer,
    InMemoryAISecurityLayer, InMemoryAetherIntelligence, InMemoryAetherTelemetry,
    NetworkHealthReport, NullAetherTelemetry, PolicyAuthChallenge, RoutingAdvice,
    SecurityDirective, SecurityDirectiveKind, SecurityPosture, StaticAetherContext,
    TelemetrySubscription, ThreatAssessment, TrustScoreUpdate,
};
pub use aether::security_layer::DirectiveSubscription as AetherDirectiveSubscription;

// CircleAI.AetherNet — mesh capability discovery + CircleAI ↔ AetherNet adapters
// (flat access). Mesh-side `AetherNet*` boundary types stay under
// `aethernet::mesh_extensibility::` to keep the flat namespace focused.
pub use aethernet::{
    AetherNetCompanionStateChannel, AetherNetContextAdapter, AetherNetDirectiveSink,
    AetherNetInboundDirectiveBridge, AetherNetTelemetryAdapter, AiNetworkHealthReport,
    AiRouteSuggestion, AiThreatLevel, CircleAiAetherNetAiProvider, IAetherNetAiProvider,
    IAetherNetTelemetry, IMeshCapabilityBroadcaster, IMeshCapabilityRegistry,
    IMeshSecurityDirectiveConsumer, IMessagingService, InMemoryMeshCapabilityRegistry,
    InMemoryMeshTelemetry, InMemoryMessagingService, MeshCapabilityAdvertisement, MeshMessage,
    MeshPacket, MeshSecurityDirective, MeshSecurityDirectiveKind, MessageStatus,
    NullMeshCapabilityBroadcaster, RecordingMeshDirectiveConsumer, CURRENT_PROTOCOL_VERSION,
};

// CircleAI.Security.AetherNet — AetherNet-specific security bindings (flat
// access).
pub use security_aethernet::{
    to_security_directive_kind, AetherIntelligenceAdapter, AetherSecurityBridge, GateDecision,
    MeshDirectiveStore, MeshGatedCompanionSession, MeshGatedError, MeshSecurityBlockedError,
    MeshSecurityGate,
};

// CircleAI.ContentPolicy — safety guardrails: content filter, refusal policy,
// prompt-injection detector, safety audit log (flat access).
pub use content_policy::{
    CommonKeywordRules, IContentFilter, IPromptInjectionDetector, IRefusalPolicy, ISafetyAuditLog,
    KeywordContentFilter, KeywordPromptInjectionDetector, KeywordRule, NullContentFilter,
    NullPromptInjectionDetector, NullRefusalPolicy, NullSafetyAuditLog, SafetyAuditEntry,
    SafetyFinding, SafetyVerdict, ThresholdRefusalPolicy,
};

// CircleAI.ModelAlignment — targeted-abliteration toolkit + publish auditor
// (flat access).
pub use model_alignment::{
    AlignmentError, AlignmentProfile, AlignmentResult, IAlignmentAuditor, IAlignmentToolkit,
    InMemoryAlignmentToolkit, NullAlignmentAuditor, NullAlignmentToolkit,
    RefuseAlignedPublishAuditor,
};

// CircleAI.Safety — personal-safety domain pack: incident/hazard/contact board
// + domain context + companion adapter (flat access).
pub use safety::{
    EmergencyContact, Hazard, ISafetyBoard, Incident, IncidentSeverity, InMemorySafetyBoard,
    SafetyCompanionAdapter, SafetyDomainContext,
};

// CircleAI.Safety.Child — child-safeguarding domain pack: trusted-adult ring /
// geofence / check-in board + domain context + companion adapter (flat access).
pub use safety_child::{
    haversine_meters, CheckIn, Geofence, IChildSafetyBoard, InMemoryChildSafetyBoard,
    SafetyChildCompanionAdapter, SafetyChildDomainContext, TrustedAdult,
};

// ── Domain boards A: health / finance / legal / edu / commerce ───────────────
// Each of the following ports one `CircleAI.<Domain>` project: an `I<Domain>Board`
// trait + a handful of record types + a deterministic in-memory board, plus (where
// the C# has them) a static `<Domain>DomainContext` and an `ICompanionSession`
// domain adapter. Sync-only; `decimal` money → `f64`.

// CircleAI.Healthcare (flat access).
pub use healthcare::{
    HealthAppointment, HealthcareCompanionAdapter, HealthcareDomainContext, IHealthcareBoard,
    InMemoryHealthcareBoard, Patient, Prescription,
};

// CircleAI.Banking (flat access). No domain context/adapter in the C#.
pub use banking::{
    Account, IAccountReader, ILedgerWriter, IPaymentProcessor, InMemoryAccountReader, InMemoryBank,
    InMemoryLedgerWriter, InMemoryPaymentProcessor, LedgerEntry, NullAccountReader,
    NullLedgerWriter, NullPaymentProcessor, PaymentRequest, PaymentResult,
};

// CircleAI.Legal (flat access).
pub use legal::{
    Clause, Contract, ILegalBoard, InMemoryLegalBoard, LegalCompanionAdapter, LegalDeadline,
    LegalDomainContext, Matter,
};

// CircleAI.Education (flat access).
pub use education::{
    Course, EducationCompanionAdapter, EducationDomainContext, IEducationBoard,
    InMemoryEducationBoard, Lesson, StudentRecord,
};

// CircleAI.Commerce (flat access).
pub use commerce::{
    CommerceCompanionAdapter, CommerceCustomer, CommerceDomainContext, CommerceLineItem,
    CommerceOrder, ICommerceBoard, InMemoryCommerceBoard,
};

// CircleAI.Commerce.Accounting (flat access).
pub use commerce_accounting::{
    AccountingEntry, CommerceAccountingCompanionAdapter, CommerceAccountingDomainContext,
    IAccountingBoard, InMemoryAccountingBoard, Period, TaxRate,
};

// CircleAI.Commerce.Finance (flat access).
pub use commerce_finance::{
    CommerceFinanceCompanionAdapter, CommerceFinanceDomainContext, FinancePayment, IInvoiceBoard,
    InMemoryInvoiceBoard, Invoice, InvoiceLine,
};

// CircleAI.Commerce.Integration.PayFast (flat access).
pub use commerce_payfast::{
    CommerceIntegrationPayFastCompanionAdapter, CommerceIntegrationPayFastDomainContext,
    IPayFastBoard, InMemoryPayFastBoard, PayFastConfig, PayFastItnPayload,
};

// CircleAI.Commerce.Integration.Xero (flat access).
pub use commerce_xero::{
    CommerceIntegrationXeroCompanionAdapter, CommerceIntegrationXeroDomainContext, IXeroBoard,
    InMemoryXeroBoard, XeroTenant, XeroTokens, XeroWebhookEvent,
};

// CircleAI.Personal.Finance (flat access). `Account` clashes with the banking
// `Account`, so it is re-exported as `PersonalFinanceAccount`
// (`personal_finance::Account` remains the canonical path).
pub use personal_finance::{
    Account as PersonalFinanceAccount, BudgetLine, FinanceTransaction, IPersonalFinanceBoard,
    InMemoryPersonalFinanceBoard, MonthSummary, PersonalFinanceCompanionAdapter,
    PersonalFinanceDomainContext,
};

// CircleAI.Personal.Health (flat access).
pub use personal_health::{
    Allergy, IPersonalHealthBoard, InMemoryPersonalHealthBoard, Medication,
    PersonalHealthCompanionAdapter, PersonalHealthDomainContext, VitalKind, VitalReading,
};

// CircleAI.Personal.Mental (flat access).
pub use personal_mental::{
    CopingStrategy, IMentalHealthBoard, InMemoryMentalHealthBoard, JournalEntry, Mood, MoodLog,
    PersonalMentalCompanionAdapter, PersonalMentalDomainContext,
};

// ── Domain boards B: people / home / logistics ───────────────────────────────
// Each of the following ports one `CircleAI.<Domain>` project: an `I<Domain>Board`
// trait (or, for CRM/Markets, a small family of contracts) + a handful of record
// types + a deterministic in-memory board. CRM/Markets additionally carry the
// fail-closed `Null*` backends. Sync-only; `decimal` money → `f64`; the C#
// `DateTime`/`DateTimeOffset` fields → `chrono::DateTime<Utc>`; `TimeSpan` →
// `chrono::Duration`.

// CircleAI.CRM (flat access).
pub use crm::{
    Activity, Company, Contact, Deal, IActivityLog, IContactStore, IDealPipeline,
    InMemoryActivityLog, InMemoryContactStore, InMemoryDealPipeline, NullActivityLog,
    NullContactStore, NullDealPipeline,
};

// CircleAI.HR (flat access).
pub use hr::{
    Employee, IHRBoard, InMemoryHRBoard, LeaveRequest, PerformanceReview,
};

// CircleAI.Business (flat access).
pub use business::{
    BusinessUnit, IBusinessBoard, InMemoryBusinessBoard, KpiSample, QuarterTarget,
};

// CircleAI.Retail (flat access).
pub use retail::{
    IRetailBoard, InMemoryRetailBoard, Product, Sale, StockLevel,
};

// CircleAI.Markets (flat access). `OrderSide`/`OrderType` + subscribe surface.
pub use markets::{
    IInstrumentCatalog, IMarketDataFeed, IOrderRouter, InMemoryInstrumentCatalog,
    InMemoryMarketDataFeed, InMemoryOrderRouter, Instrument, NullInstrumentCatalog,
    NullMarketDataFeed, NullOrderRouter, OrderRequest, OrderResult, OrderSide, OrderType, Quote,
    QuoteHandler, QuoteSubscription,
};

// CircleAI.Logistics (flat access).
pub use logistics::{
    ILogisticsBoard, InMemoryLogisticsBoard, RouteLeg, RoutePlan, Shipment, Vehicle,
};

// CircleAI.RealEstate (flat access).
pub use real_estate::{
    IRealEstateBoard, InMemoryRealEstateBoard, Listing, Property, PropertyKind, Valuation, Viewing,
};

// CircleAI.Home (flat access).
pub use home::{
    HomeDevice, IHomeBoard, InMemoryHomeBoard, MaintenanceTask, Room,
};

// CircleAI.IoT (flat access).
pub use iot::{
    IIoTBoard, InMemoryIoTBoard, IoTCommand, IoTDevice, IoTTelemetry,
};

// CircleAI.Family (flat access).
pub use family::{
    FamilyEvent, FamilyMember, IFamilyBoard, InMemoryFamilyBoard, SharedExpense,
};

// CircleAI.Parenting (flat access). `DayOfWeek` is a faithful port of
// `System.DayOfWeek`.
pub use parenting::{
    Child, DayOfWeek, IParentingBoard, InMemoryParentingBoard, Milestone, Routine, RoutineEntry,
};

// CircleAI.Pets (flat access).
pub use pets::{
    IPetsBoard, InMemoryPetsBoard, Pet, Vaccination, VetAppointment, WeightSample,
};

// CircleAI.Elderly (flat access). The elderly `CheckIn` is re-exported as
// `ElderlyCheckIn` to avoid clashing with `safety_child::CheckIn`.
pub use elderly::{
    CarePlan, CheckIn as ElderlyCheckIn, IElderlyCareBoard, InMemoryElderlyCareBoard, MedReminder,
};

// ── Domain boards C: lifestyle / civic / misc ────────────────────────────────
// Each ports one `CircleAI.<Domain>` project: an `I<Domain>Board` trait (or, for
// Games, a small family of runtime contracts) + record/enum types + a
// deterministic in-memory board. Games additionally carries `Null*` backends.
// Sync-only; `decimal` money → `f64`; `DateTime`/`DateTimeOffset` →
// `chrono::DateTime<Utc>`; `TimeSpan` → `chrono::Duration`.

// CircleAI.Sports (flat access). `Activity` → `SportsActivity` (clashes with
// `crm::Activity`).
pub use sports::{
    Activity as SportsActivity, DistanceKind, ISportsBoard, InMemorySportsBoard, PersonalBest,
    TrainingSession,
};

// CircleAI.Fitness (flat access).
pub use fitness::{
    ExerciseSet, FitnessGoal, IFitnessBoard, InMemoryFitnessBoard, Workout,
};

// CircleAI.Food (flat access).
pub use food::{
    IFoodBoard, InMemoryFoodBoard, MealLog, PantryItem, Recipe,
};

// CircleAI.Agriculture (flat access).
pub use agriculture::{
    Crop, Field, IFarmBoard, InMemoryFarmBoard, YieldRecord,
};

// CircleAI.Beauty (flat access).
pub use beauty::{
    Appointment, IBeautyBoard, InMemoryBeautyBoard, SkinProfile, Treatment,
};

// CircleAI.Gaming (flat access).
pub use gaming::{
    AchievementUnlock, GameTitle, IGamingBoard, InMemoryGamingBoard, PlaySession,
};

// CircleAI.Games (flat access). Game-runtime contracts + real + `Null*` backends.
pub use games::{
    GameSubscription, GameTick, IGameLoop, IInputMap, ISceneGraph, InMemoryInputMap,
    InMemorySceneGraph, InputEvent, InputHandler, NullGameLoop, NullInputMap, NullSceneGraph,
    SceneNode, TickHandler, TimerGameLoop,
};

// CircleAI.Hospitality (flat access).
pub use hospitality::{
    FrontDeskNote, GuestReservation, HotelRoom, IHospitalityBoard, InMemoryHospitalityBoard,
};

// CircleAI.Tourism (flat access).
pub use tourism::{
    Attraction, ITourismBoard, InMemoryTourismBoard, Itinerary, ItineraryItem, TourismBooking,
};

// CircleAI.Travel (flat access).
pub use travel::{
    Flight, HotelStay, ITravelBoard, InMemoryTravelBoard, TravelTrip,
};

// CircleAI.Civic (flat access).
pub use civic::{
    CivicEvent, CivicIssue, ICivicBoard, InMemoryCivicBoard, Representative,
};

// CircleAI.Community (flat access).
pub use community::{
    Announcement, CommunityGroup, ICommunityBoard, InMemoryCommunityBoard, VolunteerOpportunity,
};

// CircleAI.Social (flat access).
pub use social::{
    Follow, ISocialBoard, InMemorySocialBoard, Reaction, SocialPost,
};

// CircleAI.Relationships (flat access).
pub use relationships::{
    ContactEvent, IRelationshipsBoard, ImportantDate, InMemoryRelationshipsBoard, PersonContact,
};

// CircleAI.Faith (flat access).
pub use faith::{
    FaithService, IFaithBoard, InMemoryFaithBoard, PrayerRequest, ScriptureReference,
};

// CircleAI.Construction (flat access). `Project` → `ConstructionProject`.
pub use construction::{
    ConstructionTask, CostEntry, IConstructionBoard, InMemoryConstructionBoard,
    Project as ConstructionProject,
};

// CircleAI.Energy (flat access).
pub use energy::{
    EnergyTariff, IEnergyBoard, InMemoryEnergyBoard, MeterReading, Outage,
};

// CircleAI.Creative (flat access).
pub use creative::{
    CreativeWork, Critique, ICreativeBoard, InMemoryCreativeBoard, Inspiration,
};

// CircleAI.Kids (flat access).
pub use kids::{
    AgeAppropriateness, DailyTime, IKidsBoard, InMemoryKidsBoard, KidsContent, TimeLog,
};

// CircleAI.Wearable (flat access).
pub use wearable::{
    IWearableBoard, InMemoryWearableBoard, WearableDevice, WearableKind, WearableSample,
    WearableTelemetryKind,
};

// CircleAI.Accessibility (flat access).
pub use accessibility::{
    AccessibilityNeed, AdaptationHint, IAccessibilityBoard, InMemoryAccessibilityBoard,
    UserAccessibilityProfile,
};

// CircleAI.Ambient (flat access).
pub use ambient::{
    AmbientPreference, AmbientReading, IAmbientBoard, InMemoryAmbientBoard,
};

// CircleAI.Wearable.Biosignals (flat access).
pub use wearable_biosignals::{
    BiosignalAffectMapper, BiosignalAggregator, BiosignalKind, BiosignalSample, BiosignalSnapshot,
    BiosignalStats, IBiosignalSource, NullBiosignalSource, RecordedBiosignalSource,
};

// CircleAI.Skills — persistent skill store + SKILL.md pack loader/importer (flat
// access). `generate_slug`, `parse_skill_file`, `build_tarball_url`,
// `sanitize_pack_name` are the free functions ported from C# static helpers.
pub use skills::{
    build_tarball_url, generate_slug, parse_skill_file, sanitize_pack_name, FileSkillStore,
    ISkillStore, IPackDownloader, InMemorySkillStore, KnownSkillPacks, LocalCachePackDownloader,
    ParsedSkill, SkillContextBuilder, SkillDetail, SkillDraft, SkillError, SkillPackAutoImporter,
    SkillPackLoader, SkillPackManifest, SkillPackSource, SkillPackSourcesOptions, SkillSource,
    SkillSummary,
};

// CircleAI.Distribution — file-sync contracts + the 77 ubiquity rails (flat
// access). The ubiquity surface lived in the sibling C# namespace
// `CircleAI.Distribution.Ubiquity`; it is inlined in `distribution.rs` and
// re-exported flat here.
pub use distribution::{
    // Contracts + null impls
    DistributionError, FileMetadata, IFileSync, IPeerAdvertiser, NullFileSync, NullPeerAdvertiser,
    Peer,
    // Distribution rails
    AppStorePackage, DefaultCarrierPreloadCatalog, DefaultLinuxRepoFanout,
    DefaultOemPreloadCatalog, DefaultPwaFallback, DefaultSideloadChannel, DeltaUpdate,
    IAppStoreSubmitter, ICarrierPreloadCatalog, ILinuxRepoFanout, IOemPreloadCatalog, IPwaFallback,
    ISideloadChannel, ISignedDeltaUpdater,
    // Onboarding rails
    DefaultAiPersonalityWizard, HouseholdMember, IAiPersonalityWizard, IFamilyOnboarding,
    INoManualFirstRun, IPersonalDataImport, IPhonePinBiometricOnboarding, IVoiceLedSetup,
    OnboardingSession, PersonalityChoice,
    // Trust rails
    DefaultBugBountyChannel, DefaultComplianceCertifications, DefaultPrivacyRegulationCompliance,
    DefaultThirdPartySecurityAuditPublisher, DefaultVerifiablePrivacyProof, IBugBountyChannel,
    IComplianceCertifications, IPerCallTransparency, IPrivacyRegulationCompliance,
    IThirdPartySecurityAuditPublisher, IVerifiablePrivacyProof, TransparencyReceipt,
    // Pricing rails
    DefaultCarrierRevenueShare, DefaultPluginMarketplaceRevenueShare, DefaultPricingMatrix,
    ICarrierRevenueShare, IPluginMarketplaceRevenueShare, IPricingMatrix, PricingTier,
    // Localisation rails
    DefaultCrossBorderCorridors, DefaultCulturalGreetings, DefaultCulturalNameRecogniser,
    DefaultCurrencyFormatter, DefaultIndigenousKnowledgeProtocols, DefaultPhoneNumberFormatter,
    DefaultSaServiceConnectors, ICrossBorderCorridors, ICulturalGreetings, ICulturalNameRecogniser,
    ICurrencyFormatter, IIndigenousKnowledgeProtocols, IPhoneNumberFormatter, ISaServiceConnectors,
    // Hardware rails
    DefaultKaiOsSupport, DefaultLowCpuOptimization, DefaultLowRamPhoneSupport,
    DefaultOfflineQueuedOperation, DefaultSmsFallback, DefaultUssdFallback, IKaiOsSupport,
    ILowCpuOptimization, ILowRamPhoneSupport, IOfflineQueuedOperation, ISmsFallback, IUssdFallback,
    SmsSent,
    // Services rails
    DefaultAccountingConnectorRegistry, DefaultBankingConnectorRegistry,
    DefaultCalendarConnectorRegistry, DefaultCrmConnectorRegistry, DefaultEmailConnectorRegistry,
    DefaultTelegramIntegration, DefaultWhatsAppIntegration, IAccountingConnectorRegistry,
    IBankingConnectorRegistry, ICalendarConnectorRegistry, ICrmConnectorRegistry,
    IEmailConnectorRegistry, ITelegramIntegration, IWhatsAppIntegration, TelegramOut, WhatsAppOut,
    // Regulator rails
    DefaultGlobalRegulatorEngagement, DefaultIcasaApprovalStatus, DefaultLawfulInterceptCompliance,
    DefaultSarbSandboxStatus, DefaultTaxInvoiceRegistry, IGlobalRegulatorEngagement,
    IIcasaApprovalStatus, ILawfulInterceptCompliance, ISarbSandboxStatus, ITaxInvoiceRegistry,
    // Recovery rails
    DefaultAccountCompromiseRecovery, DefaultDataPortabilityExport, DefaultInheritanceProtocol,
    DefaultLostDeviceFlow, DefaultVerifiableWipe, IAccountCompromiseRecovery,
    IDataPortabilityExport, IInheritanceProtocol, ILostDeviceFlow, IVerifiableWipe,
    // Failure-mode rails
    DefaultAbusiveEnvironmentMode, DefaultBrainUnreachableMode, DefaultImpairedUserMode,
    DefaultNoInternetCacheTarget, DefaultPublicDisasterMode, DefaultStorageFullDegradationPolicy,
    IAbusiveEnvironmentMode, IBrainUnreachableMode, IImpairedUserMode, INoInternetCacheTarget,
    IPublicDisasterMode, IStorageFullDegradationPolicy,
    // Cost rails
    DefaultFreeTierCostCapping, DefaultLocalFirstRouting, DefaultPerCallCostCeiling,
    DefaultSustainablePerUserCostMath, IFreeTierCostCapping, ILocalFirstRouting,
    IPerCallCostCeiling, ISustainablePerUserCostMath,
    // Network-effect rails
    DefaultCrossProviderFederation, DefaultFamilyAiSharing, DefaultGroupNetworkEffects,
    DefaultReferralProgramme, DefaultUserGrowthFlywheel, ICrossProviderFederation, IFamilyAiSharing,
    IGroupNetworkEffects, IReferralProgramme, IUserGrowthFlywheel,
    // Cultural rails
    DefaultChildProtectionMode, DefaultIndigenousDataSovereignty, DefaultPublicTransparency,
    DefaultQuietMode, DefaultReligiousAccommodation, DefaultThirdPartyHarmLiability,
    IChildProtectionMode, IIndigenousDataSovereignty, IPublicTransparency, IQuietMode,
    IReligiousAccommodation, IThirdPartyHarmLiability, LinkedEvidence, QuietWindow,
    // Missing defaults (real in-memory impls)
    DefaultAppStoreSubmitter, DefaultFamilyOnboarding, DefaultNoManualFirstRun,
    DefaultPerCallTransparency, DefaultPersonalDataImport, DefaultPhonePinBiometricOnboarding,
    DefaultSignedDeltaUpdater, DefaultVoiceLedSetup,
};

// CircleAI.Vision — face / document / plate / BLE contracts + ONNX backends
// (flat access). The ONNX session + image decode are injected traits
// ([`IOnnxSession`] / [`IImageSource`]); the letterbox + YOLO postprocess + NMS +
// L2-normalise geometry is ported verbatim as free functions. `IFileSync`-style
// null backends carry the fail-closed defaults.
pub use vision::{
    // Primitives
    BluetoothAnomaly, BoundingBox, DetectedFace, DocumentField, DocumentVerificationResult,
    FaceEmbedding, LandmarkPoint, LivenessResult, PlateRecognitionResult,
    // Video capture
    IVideoCapture, NullVideoCapture, VideoFrame, VideoPixelFormat,
    // Contracts + null impls
    AnomalyHandler, AnomalySubscription, IBluetoothAnomalyDetector, IComputerVisionRuntime,
    IDocumentVerifier, IFaceDetector, IFaceEmbedder, IFaceLivenessDetector, IPlateRecognizer,
    NullBluetoothAnomalyDetector, NullComputerVisionRuntime, NullDocumentVerifier,
    NullFaceDetector, NullFaceEmbedder, NullFaceLivenessDetector, NullPlateRecognizer,
    // ONNX backends + injected traits + ported geometry
    clamp_region, iou, l2_normalise, letterbox_resize, non_max_suppression, postprocess_yolo,
    to_tensor_rgb_normalised, IImageSource, IOnnxSession, OnnxFaceDetector,
    OnnxFaceDetectorOptions, OnnxFaceEmbedder, OnnxFaceEmbedderOptions, OnnxPlateRecognizer,
    OnnxPlateRecognizerOptions, RgbImage, VisionError,
};

// CircleAI.Telephony — carrier-agnostic voice-loop surface (flat access). The
// carrier/session network + HTTP boundary is injected behind traits
// (`ITelephonyCarrier` / `ICallSession` / `IHttpJsonClient`); the pure DSP
// (`DtmfToneGenerator`, `AnsweringMachineDetector`, `HoldMusicMixer`,
// `StereoCallRecorder`) and orchestration state machines are ported verbatim.
// `AudioFrame`/`ToolDefinition`/`ToolInvocation`/`ToolResult` are re-exported with
// a `Telephony`/`VoiceLoop` prefix to avoid clashing with the identically-named
// `tools::`/`networking_transports::` items already re-exported above.
pub use telephony::{
    AgentHealthRow, AmdOptions, AmdVerdict, AnsweringMachineDetector,
    AudioFrame as TelephonyAudioFrame, BargeInController, BargeInOptions, BargeInState,
    BargeInTransition, BriefingSynthesiser, CallAgent, CallCostBreakdown, CallCostCalculator,
    CallDirection, CallInfo, CallMediaFormat, CallPricing, CallSnapshot, CallStatus,
    CarrierFallback, CircuitBreakerToolRegistry, CommonGuardrails, ConsultAnswer, ConsultEscalator,
    ConsultRequest, DashboardSnapshot, DashboardSummary, DefaultAgentHandoffOrchestrator,
    DefaultSpeculativeGenerator, DefaultToolCallRegistry, DefaultWarmTransferOrchestrator,
    DtmfEvent, DtmfToneGenerator, EvalRunResult, EvalSession, EvalTurn, EvalTurnHandler,
    EvalTurnResult, FirstMessagePreambleOptions, GuardrailAction, GuardrailResult, GuardrailRule,
    Guardrails, HandoffResult, HoldMusicMixer, HttpMcpToolImporter, HttpWebhookConsultChannel,
    IConsultChannel, IDashboardDataSource, IDtmfSendable, IFalseInterruptionTracker,
    IHttpJsonClient, IInboundCallDispatcher, ILocalDevTunnel, IProvisionedNumberStore,
    IToolCallRegistry, IToolProgressSink, ITelephonyCarrier, ICallSession, InMemoryHttpJsonClient,
    InMemoryFalseInterruptionTracker, InMemoryProvisionedNumberStore, InMemorySpeechLifecycleBus,
    InterruptionStats, IvrLoopDetector, IvrLoopVerdict, IvrRound, JudgeCompletion, JudgeDimension,
    JudgeVerdict, LatencySnapshot, LatencyStage, LatencyTracker, LiveCallRow, LlmJudge,
    LocalToolHandler, McpServerConfig, McpToolDescriptor, NullInboundCallDispatcher,
    NullLocalDevTunnel, NullTelephonyCarrier, OutboundDialOptions, PhoneNumberProvisioner,
    PromptVariableProvider, PromptVariableResolver, ProvisionError, ProvisionedNumber,
    ReassuranceFillerOptions, ReassuranceVocabulary, RecentCallRow, RecordingToolProgressSink,
    ResponseGenerator, SentenceChunker, SpanKind, SpanOutcome,
    SpeechEventKind, SpeechLifecycleEvent, StaticLocalDevTunnel, StereoCallRecorder,
    StreamingToolHandler, StreamingToolRunner, TelephonyError, TelephonySubscription,
    TestCallSession, ToolBreakerState, ToolCallPolicy, ToolDefinition as VoiceLoopToolDefinition,
    ToolInvocation as VoiceLoopToolInvocation, ToolProgressUpdate, ToolResult as VoiceLoopToolResult_,
    TransferMode, VoiceLoopAsTool, VoiceLoopRunner, VoiceLoopSpan, VoiceLoopTelemetry,
    VoiceLoopToolRequest, VoiceLoopToolResult, WarmTransferRequest, WarmTransferResult,
};

// CircleAI.Workflows — durable-workflow contracts + the paca project/agent/board/
// doc/plugin/realtime/MCP/deploy/auth runtime (flat access). The
// `WorkflowExecution`/durable-runner surface is trait-only; the paca stores are
// deterministic in-memory. `HmacJwtAuthenticator`/`PacaApiKeyAuthenticator` port a
// self-contained SHA-256 + HMAC-SHA256 (no crypto dep, like the Distribution
// hashing submodule); the CSPRNG is the injected `ISecureRandom` boundary.
pub use workflows::{
    AgentCapabilities, AgentConversation, AgentGitIdentity, AgentLimits, AgentLlmConfig,
    AgentMcpConfig, AgentProfile, AgentSystemPrompts, AgentTemplates, AgentTriggers, AllowAllPermissionCheck,
    BoardView, CheckpointPayload, compare_semver, ConversationError, ConversationPermissions,
    ConversationState, ConversationStep, ConversationStepSink, CounterSecureRandom, DocActivity,
    DocLink, DocNode, DocVersion, HmacJwtAuthenticator, IConversationExecutor,
    IPermissionCheck, IPluginRuntimeHost, IRealtimeBroadcaster, ISecureRandom,
    IWorkflowDefinitionStore, IWorkflowRunner, IWorkflowState, InMemoryPacaMemberStore,
    InMemoryPacaStore, InstalledPlugin, JwtPair, JwtPayload, McpTransportKind, MemberKind,
    NullWorkflowDefinitionStore, NullWorkflowRunner, NullWorkflowState, PacaApiKeyAuthenticator,
    PacaApiKeyRecord, PacaBoard, PacaConversationRuntime, PacaCoreMcpTools, PacaDeployArtifact,
    PacaDeployMode, PacaDeployOverrides, PacaDeployer, PacaDocService, PacaMcpHandler, PacaMcpServer,
    PacaMcpTool, PacaPluginRegistry, PacaProject, PacaRealtimeHub, PacaSkill, PacaSkillInstaller,
    PacaSkillLibrary, PacaSprint, PacaTask, PluginExtensionPoint, PluginManifest,
    PluginRegistryError, PluginResourceLimits, ProjectMember, QueryInvalidation, RealtimePacaEvent,
    SkillTemplates as WorkflowSkillTemplates, SprintState, StatusColumn, TaskBoardMetadata,
    WorkflowDefinition, WorkflowError, WorkflowExecution, WorkflowPhase,
};

// CircleAI.Speech — ASR/TTS/wake-word/OCR contracts + the (3.3.0) real DSP
// backends (VAD/EOT/AEC/NR) + G.711 ↔ PCM-16 codec (flat access). The async
// contracts carry an associated `Error`; the DSP traits are synchronous. The
// native/ONNX model runners are the injected boundary; every shipped wrapper
// falls back to the pure backend when no runner is wired. `AudioCodec` is
// re-exported from the `audio_format` submodule.
pub use speech::{
    audio_format::{AudioCodec, AudioFormatConverter},
    DeepFilterNetNoiseReducer, EndOfTurnResult, EnergyVoiceActivityDetector, IEchoCanceller,
    IEchoCancellerModelRunner, IEndOfTurnDetector, INoiseReducer, INoiseReducerModelRunner,
    IOpticalCharacterRecognizer, ISpeechRecognizer, ISpeechSynthesizer, ITurnModelRunner,
    IVadModelRunner, IVoiceActivityDetector, IWakeWordDetector, KrispNoiseReducer,
    NlmsEchoCanceller, NullEchoCanceller, NullEndOfTurnDetector, NullNoiseReducer,
    NullOpticalCharacterRecognizer, NullSpeechRecognizer, NullSpeechSynthesizer,
    NullVoiceActivityDetector, NullWakeWordDetector, OcrResult, OcrTextBlock,
    RuleBasedEndOfTurnDetector, SileroVoiceActivityDetector, SmartTurnDetector, SpectralSubtractionNoiseReducer,
    SpeechError, SynthesisResult, TranscribedSegment, TranscriptionResult, VadFrameResult,
    WakeWordEvent, WebRtcEchoCanceller,
};
