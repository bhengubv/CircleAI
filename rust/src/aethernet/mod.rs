//! aethernet — CircleAI ↔ AetherNet binding layer (Rust port of
//! `src/CircleAI.AetherNet/*.cs`).
//!
//! Two families:
//!
//! 1. **Mesh capability discovery** (RT-12 v1) — [`IMeshCapabilityRegistry`] /
//!    [`InMemoryMeshCapabilityRegistry`] hold each peer's
//!    [`MeshCapabilityAdvertisement`]; [`IMeshCapabilityBroadcaster`] /
//!    [`NullMeshCapabilityBroadcaster`] publish ours.
//!
//! 2. **CircleAI ↔ AetherNet adapters** — bridge the CircleAI Aether contracts
//!    onto the mesh runtime boundary ([`mesh_extensibility`]):
//!    [`AetherNetContextAdapter`], [`AetherNetTelemetryAdapter`],
//!    [`AetherNetDirectiveSink`] (outbound), [`AetherNetInboundDirectiveBridge`]
//!    (inbound), and [`CircleAiAetherNetAiProvider`] (the mesh AI seat). Event
//!    projection lives in [`event_translator`]. The companion-state sync
//!    transport is [`AetherNetCompanionStateChannel`] over an
//!    [`IMessagingService`] seam.
//!
//! The mesh runtime (the external `AetherNet.*` packages) is injected behind the
//! traits in [`mesh_extensibility`]; in-memory implementations
//! ([`InMemoryMeshTelemetry`], [`RecordingMeshDirectiveConsumer`],
//! [`InMemoryMessagingService`]) make the whole layer testable without a live
//! mesh.

pub mod adapters;
pub mod companion_state_channel;
pub mod event_translator;
pub mod mesh_capability_registry;
pub mod mesh_extensibility;

// ── Re-exports (module-flat) ─────────────────────────────────────────────────

pub use adapters::{
    AetherNetContextAdapter, AetherNetDirectiveSink, AetherNetInboundDirectiveBridge,
    AetherNetTelemetryAdapter, CircleAiAetherNetAiProvider,
};
pub use companion_state_channel::{
    AetherNetCompanionStateChannel, IMessagingService, InMemoryMessagingService, MeshChannelSubscription,
    MeshMessage, MeshMessageHandler, MessageStatus, SYNC_MESSAGE_TYPE,
};
pub use mesh_capability_registry::{
    IMeshCapabilityBroadcaster, IMeshCapabilityRegistry, InMemoryMeshCapabilityRegistry,
    MeshCapabilityAdvertisement, NullMeshCapabilityBroadcaster,
};
pub use mesh_extensibility::{
    AetherNetNetworkEvent, AetherNetNetworkEventKind, AetherNetNodeEvent, AetherNetNodeEventKind,
    AetherNetNodeHealth, AetherNetRouteEvent, AetherNetRouteEventKind, AetherNetSecurityEvent,
    AetherNetSecurityEventKind, AetherNetThreatLevel, AetherNetTransportEvent,
    AetherNetTransportEventKind, AetherNetTransportKind, AiNetworkHealthReport, AiRouteSuggestion,
    AiThreatLevel, IAetherNetAiProvider, IAetherNetTelemetry, IAetherNetTelemetryObserver,
    IMeshSecurityDirectiveConsumer, InMemoryMeshTelemetry, MeshPacket, MeshSecurityDirective,
    MeshSecurityDirectiveKind, MeshTelemetrySubscription, RecordingMeshDirectiveConsumer,
    CURRENT_PROTOCOL_VERSION,
};
