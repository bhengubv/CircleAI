//! memory — AffectState, PersonaState, EpisodicMemoryEntry, FeedbackSignal, Goal,
//! and their async/sync store traits.

pub mod affect_state;
pub mod affect_vad;
pub mod companion_sync;
pub mod compression;
pub mod consolidation;
pub mod episodic;
pub mod extractor;
pub mod feedback_analyser;
pub mod goal;
pub mod graph;
pub mod llm_extractor;
pub mod multimodal;
pub mod rag;
pub mod recall;
pub mod runtime;
pub mod stores;

// Re-export everything that the top-level lib.rs and existing tests expect at
// the `circle_ai::memory::` path.
pub use affect_state::AffectState;
pub use affect_vad::AffectVad;
pub use goal::{Goal, GoalPriority, GoalStatus};
pub use stores::{
    AffectStore, EpisodicMemoryEntry, EpisodicMemoryStore, FeedbackPolarity, FeedbackSignal,
    FeedbackStore, GoalStore, IAffectStore, IEpisodicMemoryStore, IFeedbackStore, IGoalStore,
    InMemoryGoalStore, IPersonaStore, PersonaState, PersonaStore,
};

// Memory-brain concretes (in-memory port of the C#/TS/Go reference).
pub use episodic::{EpisodicSearch, InMemoryEpisodicStore};
pub use extractor::{HeuristicKnowledgeGraphExtractor, IKnowledgeGraphExtractor};
pub use graph::{
    HippoRagStore, IHippoRagStore, KnowledgeGraph, KnowledgeNode, KnowledgeTriple, MemoryHit,
    MemoryItem,
};
pub use recall::{FusedRecall, FusedRecallOptions, IRecall};

// LLM-backed knowledge-graph extractor (in-memory port of the C#/TS reference).
pub use llm_extractor::{parse_triples, LlmKnowledgeGraphExtractor};

// Feedback analyser + in-memory feedback store (in-memory port of the C#/TS reference).
pub use feedback_analyser::{FeedbackAnalyser, InMemoryFeedbackStore, PersonaAdaptation};

// RAG context assembly (in-memory port of the C#/TS reference).
pub use rag::{ITextEmbedder, RagContextBuilder, RagEpisodicStore, RagPipelineBuilder};

// Multimodal semantic memory (in-memory port of the C#/TS reference).
pub use multimodal::{
    compute_sha256, CaptionResult, HeuristicMultimodalCaptioner, IMultimodalCaptioner,
    IMultimodalMemoryStore, InMemoryMultimodalMemoryStore, IngestOptions, IngestionResult,
    MediaModality, MultimodalMemoryEntry, MultimodalMemoryIngester,
};

// TurboQuant compression + compressed store decorators (byte-identical wire
// format across every SDK language — in-memory port of the C#/TS reference).
pub use compression::{
    BetaCodebook, BetaLloydMaxCodebook, BitPacker, CompressedEpisodicMemoryStore,
    CompressedMultimodalMemoryStore, EmbeddingPayloadCodec, OrthogonalRotation, TurboQuantCodec,
    TurboQuantPayload, COMPRESSED_TAG_KEY, MAGIC, ROTATION_SEED,
};

// Companion-state cross-device sync layer (in-memory port of the C#
// `CircleAI.Memory/Sync/` reference).
pub use companion_sync::{
    base64_decode, base64_encode, CompanionConversationSyncBridge, CompanionStateSyncEngine,
    ConversationStateDelta, EnvelopeHandler, HybridLogicalClock, ICompanionStateChannel,
    ICompanionStateSyncEngine, ISyncableEntryStore, InMemorySyncableEntryStore,
    InProcessCompanionStateChannel, InProcessSyncHub, LoraAdapterSnapshot, LoraAdapterSyncBridge,
    PersonaStateSyncBridge, RequestItem, StateVectorEntry, Subscription, SyncEnvelope,
    SyncEnvelopeKind, SyncableEntry, CONVERSATION_STATE_ENTITY_TYPE, LORA_ADAPTER_ENTITY_TYPE,
    PERSONA_STATE_ENTITY_TYPE,
};

// Memory pipeline runtime (in-memory port of the C# `CircleAI.Memory/Runtime/`).
pub use runtime::{CompanionRuntime, CompanionRuntimeOptions};

// Hierarchical memory consolidation — the "sleep cycle" engine (in-memory port
// of the C#/TS reference).
pub use consolidation::{
    add_days, cosine_full, create_core_memory, create_daily_summary, create_persona_delta,
    create_semantic_cluster, day_key_of, monday_of, month_first_day_of, ClockFn, ConsolidationOutcome,
    CoreMemory, CoreMemoryInit, CoreMemoryKind, DailyMemorySummary, DailyMemorySummaryInit,
    EpisodicConsolidationSource, HeuristicSummarizer, ICoreMemoryStore, IDailyMemoryStore,
    IMemoryConsolidator, IMemorySummarizer, IPersonaDeltaStore, ISemanticMemoryStore,
    InMemoryCoreMemoryStore, InMemoryDailyMemoryStore, InMemoryPersonaDeltaStore,
    InMemoryPersonaStore, InMemorySemanticMemoryStore, MemoryConsolidationOptions,
    MemoryConsolidator, PersonaConsolidationStore, PersonaDeltaSnapshot, PersonaDeltaSnapshotInit,
    SemanticMemoryCluster, SemanticMemoryClusterInit, SleepKind,
};
