//! memory — AffectState, PersonaState, EpisodicMemoryEntry, FeedbackSignal, Goal,
//! and their async/sync store traits.

pub mod affect_state;
pub mod affect_vad;
pub mod consolidation;
pub mod episodic;
pub mod extractor;
pub mod goal;
pub mod graph;
pub mod llm_extractor;
pub mod recall;
pub mod stores;

// Re-export everything that the top-level lib.rs and existing tests expect at
// the `circle_ai::memory::` path.
pub use affect_state::AffectState;
pub use affect_vad::AffectVad;
pub use goal::{Goal, GoalPriority, GoalStatus};
pub use stores::{
    AffectStore, EpisodicMemoryEntry, EpisodicMemoryStore, FeedbackPolarity, FeedbackSignal,
    FeedbackStore, GoalStore, IAffectStore, IEpisodicMemoryStore, IFeedbackStore, IGoalStore,
    IPersonaStore, PersonaState, PersonaStore,
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
