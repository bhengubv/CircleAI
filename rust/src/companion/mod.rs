//! companion — InterfaceKind, CompanionContext, CompanionTurn, CompanionProactiveEvent,
//! ICompanionSession trait, FaceAffectMapper, and FaceCompanionBridge.

pub mod face_affect_mapper;
pub mod face_companion_bridge;
pub mod types;

pub use face_companion_bridge::CONFUSION_THRESHOLD;
pub use types::{
    CompanionContext, CompanionProactiveEvent, CompanionTurn, ICompanionSession, InterfaceKind,
};
