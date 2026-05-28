//! memory — AffectState, PersonaState, EpisodicMemoryEntry, FeedbackSignal, Goal,
//! and their async/sync store traits.

pub mod affect_state;
pub mod goal;
pub mod stores;

// Re-export everything that the top-level lib.rs and existing tests expect at
// the `circle_ai::memory::` path.
pub use affect_state::AffectState;
pub use goal::{Goal, GoalPriority, GoalStatus};
pub use stores::{
    AffectStore, EpisodicMemoryEntry, EpisodicMemoryStore, FeedbackPolarity, FeedbackSignal,
    FeedbackStore, GoalStore, IAffectStore, IEpisodicMemoryStore, IFeedbackStore, IGoalStore,
    IPersonaStore, PersonaState, PersonaStore,
};
