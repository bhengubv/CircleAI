//! companion — InterfaceKind, CompanionContext, CompanionTurn, CompanionProactiveEvent,
//! ICompanionSession trait, FaceAffectMapper, and FaceCompanionBridge.

pub mod belief;
pub mod face_affect_mapper;
pub mod face_companion_bridge;
pub mod memory_encoder;
pub mod session;
pub mod types;

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
