//! Circle AI portable core — Rust port.
//!
//! All modules are public; consumers pick what they need.

#![allow(dead_code)]
#![allow(clippy::type_complexity)]
#![allow(clippy::excessive_precision)]

pub mod agents;
pub mod catalog;
pub mod companion;
pub mod device;
pub mod hosting;
pub mod identity;
pub mod inference;
pub mod languages;
pub mod memory;
pub mod models;
pub mod models_v15;
pub mod prompt;
pub mod registry;
pub mod security;
pub mod selector;
pub mod sync;
pub mod tools;

// Convenience re-exports so downstream crates can write `circle_ai::AffectState`.
pub use companion::{
    CompanionContext, CompanionProactiveEvent, CompanionTurn, InterfaceKind,
};
pub use identity::{CircleIdentity, IdentityTier, RegisteredDevice};
pub use inference::{ChatMessage as InferenceChatMessage, GenerationOptions};
pub use languages::{DetectionResult, KnownLanguages, LanguageTag, ScriptNormalisationResult, WritingSystem};
pub use memory::{
    AffectState, AffectVad, EpisodicMemoryEntry, FeedbackPolarity, FeedbackSignal, Goal,
    GoalPriority, GoalStatus, PersonaState,
};
pub use security::{AnomalySignal, ThreatVector};
pub use models::{ChatMessage, DownloadProgress};
pub use sync::{SyncDeliveryMode, SyncDelta, SyncDomainKeys};
pub use tools::{ToolDefinition, ToolInvocation, ToolParameter, ToolResult};
