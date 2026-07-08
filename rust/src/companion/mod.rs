//! companion — InterfaceKind, CompanionContext, CompanionTurn, CompanionProactiveEvent,
//! ICompanionSession trait, FaceAffectMapper, and FaceCompanionBridge.

pub mod belief;
pub mod briefing;
pub mod capability_registry;
pub mod face_affect_mapper;
pub mod face_companion_bridge;
pub mod her_jarvis;
pub mod inner_monologue;
pub mod memory_encoder;
pub mod predictive_engine;
pub mod self_improvement;
pub mod session;
pub mod session_factory;
pub mod theory_of_mind;
pub mod types;
pub mod voice_listener;
pub mod world_model;

pub use face_companion_bridge::CONFUSION_THRESHOLD;
pub use types::{
    CompanionContext, CompanionProactiveEvent, CompanionTurn, ICompanionSession, InterfaceKind,
};

// Memory-brain companion concretes (in-memory port of the C#/TS/Go reference).
pub use belief::{
    Attribution, HeuristicBeliefExtractor, IBeliefExtractor, PersonalBelief, SelfBeliefStore,
};
pub use memory_encoder::CompanionMemoryEncoder;
pub use session::{CompanionSession, CompanionSessionOptions, EmbedderFn};

// HER/Jarvis companion reasoning core (in-memory port of the C# reference).
pub use inner_monologue::{
    IInnerMonologue, ReasoningLoopInnerMonologue, SelfReflection, TemplateInnerMonologue,
};
pub use predictive_engine::{
    AnticipatedNeed, HistogramPredictiveEngine, IPredictiveEngine, SequencePredictiveEngine,
};
pub use theory_of_mind::{BeliefTrackerTheoryOfMind, ITheoryOfMind, OtherMindEstimate};
pub use world_model::{
    BayesianWorldModel, CausalPrediction, FrequencyWorldModel, IWorldModel,
};

// HER/Jarvis remaining contracts + real impls (the four already-ported families
// — world model, theory of mind, inner monologue, predictive engine — are
// re-exported above from their own modules and also from `her_jarvis`).
pub use her_jarvis::{
    AcquiredSkill, AdjacencyPersonalKnowledgeGraph, AgentToAgentMessage, BioSignal,
    ChannelBioSignalStream, ChannelFusedPerception, CodeGenJob, ConfidenceBand,
    DemoStoreSkillAcquisition, DelegationCredential, DeviceHandler, EmotionFrame,
    EnergyBandVoiceIdentity, EpisodeRecord, EwaContinuousLearner, FineTuneJobStatus,
    FirstTokenBudget, FusedPercept, HeartbeatAlwaysOnPresence, HistoricalCalibratedConfidence,
    HmacCryptoDelegation, IAgentPeerNetwork, IAlwaysOnPresence, IBioSignalStream,
    ICalibratedConfidence, ICodeGenerationLoop, IContinuousLearner, ICryptoDelegation,
    IEmotionSensor, IEpisodicMemory, IFederatedFineTuner, IFirstTokenOptimizer, IFusedPerception,
    IGoalPursuer, IIdentitySync, ILiveWorldKnowledge, IPersonalKnowledgeGraph, IPhysicalActuator,
    ISelfImprovementLoop, ISkillAcquisition, IVoiceIdentity, InMemoryFederatedFineTuner,
    InMemoryGoalPursuer, JsonIdentitySync, KeywordEmotionSensor, KnowledgeNode as HjKnowledgeNode,
    KnowledgeRelation as HjKnowledgeRelation, LongHorizonGoal, MailboxAgentPeerNetwork,
    PhysicalCommand, PhysicalCommandResult, RegistryPhysicalActuator, SelfImprovementVerdict,
    SlidingP50FirstTokenOptimizer, SyntaxCheckingCodeGenerationLoop, TfEpisodicMemory,
    TopicLiveWorldKnowledge, TrackingSelfImprovementLoop, WorldFact,
};

// SelfBench-backed self-improvement loop.
pub use self_improvement::{
    AbBenchRunner, AbVerdict, BenchSummary, BenchTask, IAbBenchRunner, IBenchModel,
    IBenchSuiteRegistry, InMemoryBenchSuiteRegistry, RegressionGateConfig,
    SelfBenchSelfImprovementLoop,
};

// External-capability registry.
pub use capability_registry::{CapabilityEntry, ExternalCapabilityRegistry};

// Proactive briefing service.
pub use briefing::{
    CalendarEvent, EmailHeader, IBriefingNotifier, ICalendarConnector, IEmailConnector,
    INewsSource, IWeatherProvider, NewsItem, ProactiveBriefingOptions, ProactiveBriefingService,
    WeatherNow,
};

// Companion session factory.
pub use session_factory::{
    CompanionSessionFactory, ICompanionSessionFactory, IIdentityNameResolver, ResolvedIdentity,
};

// Voice listener bridge.
pub use voice_listener::{
    IVoiceListener, IVoicePipeline, ResponseReadyEventArgs, TranscriptionResult,
    UtteranceDetectedEventArgs, VoiceCompanionListener,
};
