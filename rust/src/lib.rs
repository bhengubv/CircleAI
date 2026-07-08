//! Circle AI portable core — Rust port.
//!
//! All modules are public; consumers pick what they need.

#![allow(dead_code)]
#![allow(clippy::type_complexity)]
#![allow(clippy::excessive_precision)]

pub mod agents;
pub mod brain;
pub mod catalog;
pub mod companion;
pub mod device;
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
pub mod model_runtime;
pub mod models;
pub mod models_v15;
pub mod proactive;
pub mod prompt;
pub mod registry;
pub mod security;
pub mod selector;
pub mod sync;
pub mod sync_service;
pub mod tools;

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
pub use security::{AnomalySignal, ThreatVector};
pub use models::{ChatMessage, DownloadProgress};
pub use sync::{
    SyncDeliveryMode, SyncDelta, SyncDomainKeys, SyncReconciliation, VersionVector,
};
pub use sync_service::{IMemorySyncService, MemorySyncError, MemorySyncService};
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
