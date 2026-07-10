//! aether — CircleAI ↔ Aether mesh contract surface (Rust port of
//! `src/CircleAI.Aether/*.cs`).
//!
//! Five one-way contracts between BhenguAI and the Aether mesh runtime:
//!
//! 1. **Telemetry** — Aether publishes, BhenguAI subscribes ([`IAetherTelemetry`]
//!    / [`IAetherTelemetryObserver`], with [`NullAetherTelemetry`] and the
//!    working [`InMemoryAetherTelemetry`] fan-out).
//! 2. **Presence & Capability** — [`IAetherContext`] answers "is Aether here,
//!    and at what level?" ([`StaticAetherContext`], [`AetherVersion`]).
//! 3. **Intelligence Output** — [`IAetherIntelligence`] is what BhenguAI
//!    produces; flows upward only ([`InMemoryAetherIntelligence`]).
//! 4. **Security Layer** — [`IAISecurityLayer`] publishes [`SecurityDirective`]s
//!    consumed by an [`ISecurityDirectiveConsumer`]
//!    ([`InMemoryAISecurityLayer`]).
//! 5. **Auth Challenge** — [`IAuthChallenge`] gates OS-level operations behind
//!    Biometric + DeviceAdmin ([`PolicyAuthChallenge`]).
//!
//! All contracts are sync (matching the crate convention); every `Task<T>`
//! became a direct return and every `IAsyncEnumerable<T>` a drain. All types are
//! deterministic and in-memory.

pub mod auth_challenge;
pub mod context;
pub mod events;
pub mod intelligence;
pub mod security_layer;
pub mod telemetry;

// ── Re-exports (module-flat) ─────────────────────────────────────────────────

pub use auth_challenge::{
    AuthChallengeReason, AuthChallengeResult, AuthMethod, IAuthChallenge, PolicyAuthChallenge,
};
pub use context::{AetherVersion, IAetherContext, StaticAetherContext};
pub use events::{
    AetherInstallLevel, AetherNetworkEvent, AetherNetworkEventKind, AetherNodeEvent,
    AetherNodeEventKind, AetherNodeHealth, AetherRouteEvent, AetherRouteEventKind,
    AetherSecurityEvent, AetherSecurityEventKind, AetherThreatLevel, AetherTransportEvent,
    AetherTransportEventKind, AetherTransportKind,
};
pub use intelligence::{
    IAetherIntelligence, InMemoryAetherIntelligence, NetworkHealthReport, RoutingAdvice,
    ThreatAssessment, TrustScoreUpdate,
};
pub use security_layer::{
    DirectiveSubscription, IAISecurityLayer, ISecurityDirectiveConsumer, InMemoryAISecurityLayer,
    SecurityDirective, SecurityDirectiveKind, SecurityPosture,
};
pub use telemetry::{
    IAetherTelemetry, IAetherTelemetryObserver, InMemoryAetherTelemetry, NullAetherTelemetry,
    TelemetrySubscription,
};
