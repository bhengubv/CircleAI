//! security_aethernet — AetherNet-specific security bindings (Rust port of
//! `src/CircleAI.Security.AetherNet/*.cs`).
//!
//! Wires the Aether contract surface ([`crate::aether`]) to the
//! transport-agnostic peer-security pipeline ([`crate::security`]):
//!
//!   [`mapper`]                     — Aether ↔ Peer enum/type translation.
//!   [`AetherSecurityBridge`]       — `IAISecurityLayer` over `SecurityLayerService`,
//!                                    fed by an Aether telemetry feed.
//!   [`AetherIntelligenceAdapter`]  — `IAetherIntelligence` over `PeerIntelligenceService`.
//!   [`MeshDirectiveStore`]         — `ISecurityDirectiveConsumer` sink + query surface.
//!   [`MeshSecurityGate`]           — read-only "is this id blocked?" view.
//!   [`MeshGatedCompanionSession`]  — decorator that gates chat on mesh blocks.
//!
//! All in-memory and deterministic; the reasoning lives in `SecurityLayerService`
//! / `PeerIntelligenceService`, so these types are pure translation + policy
//! plumbing.

pub mod directive_store;
pub mod gated_session;
pub mod intelligence_adapter;
pub mod mapper;
pub mod security_bridge;

// ── Re-exports (module-flat) ─────────────────────────────────────────────────

pub use directive_store::{
    GateDecision, MeshDirectiveStore, MeshSecurityBlockedError, MeshSecurityGate,
};
pub use gated_session::{MeshGatedCompanionSession, MeshGatedError};
pub use intelligence_adapter::AetherIntelligenceAdapter;
pub use mapper::{
    to_aether_threat_level, to_peer_event_kind, to_peer_threat_level, to_security_directive_kind,
};
pub use security_bridge::AetherSecurityBridge;
