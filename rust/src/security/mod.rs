//! security — CircleAI runtime immune system + transport-agnostic peer
//! security intelligence.
//!
//! Full Rust port of `src/CircleAI.Security/*.cs`. Two cooperating halves:
//!
//! 1. **Local runtime immune system** — `AnomalySignal` / `ThreatVector` /
//!    `SecurityCheckpoint` / `ISecurityWatchdog` (+ `DefaultSecurityWatchdog`)
//!    / `IAnomalyEventDispatcher` (+ `DefaultAnomalyEventDispatcher`) /
//!    `SecurityResponse` / `UhidKeyRing`. Detection sites report anomalies; the
//!    watchdog decides key rotation, mesh isolation, or state rollback.
//!
//! 2. **Transport-agnostic peer-security pipeline** — `PeerSecurityEvent` and
//!    friends, `NodeTrustRegistry`, `SecurityLayerService` (`IPeerSecurityLayer`),
//!    `PeerIntelligenceService` (`IPeerIntelligence`), `DirectivePublisher`,
//!    `ThreatDetector`. Any transport translates its native events into
//!    `PeerSecurityEvent`; the layer degrades trust and issues `PeerDirective`s.
//!
//! Deterministic and in-memory. External/native crypto is behind the crate's
//! vetted SHA-256 core; the C# ECDSA `UhidKeyRing` is realised as a verifiable
//! HMAC-SHA256 ring (matching the companion `HmacCryptoDelegation` port).

pub mod anomaly_event_dispatcher;
pub mod anomaly_signal;
pub mod directive_publisher;
pub(crate) mod hashing;
pub mod node_trust_registry;
pub mod peer_intelligence_service;
pub mod peer_security_types;
pub mod redacted_evidence;
pub mod security_checkpoint;
pub mod security_layer_service;
pub mod security_options;
pub mod security_response;
pub mod security_watchdog;
pub mod threat_detector;
pub mod threat_vector;
pub mod uhid_key_ring;

// ── Re-exports (module-flat) ────────────────────────────────────────────────

pub use anomaly_event_dispatcher::{
    AnomalyDispatchOutcome, AnomalyDispatchResult, DefaultAnomalyEventDispatcher,
    IAnomalyEventDispatcher,
};
pub use anomaly_signal::AnomalySignal;
pub use directive_publisher::{DirectivePublisher, DirectiveSubscription};
pub use node_trust_registry::{NodeTrustEntry, NodeTrustRegistry};
pub use peer_intelligence_service::PeerIntelligenceService;
pub use peer_security_types::{
    IPeerDirectiveConsumer, IPeerIntelligence, IPeerSecurityEventFeed, IPeerSecurityLayer,
    PeerDirective, PeerDirectiveKind, PeerNetworkHealthReport, PeerRoutingAdvice,
    PeerSecurityEvent, PeerSecurityEventKind, PeerSecurityPosture, PeerThreatAssessment,
    PeerThreatLevel, PeerTrustScoreUpdate,
};
pub use redacted_evidence::{hash_redacted, redact_evidence, serialize_redacted, to_redacted_json};
pub use security_checkpoint::SecurityCheckpoint;
pub use security_layer_service::{SecurityLayerService, RECOVERY_INTERVAL_SECONDS};
pub use security_options::SecurityOptions;
pub use security_response::{SecurityResponse, SecurityResponseKind};
pub use security_watchdog::{
    confidence_band, DefaultSecurityWatchdog, ISecurityWatchdog,
};
pub use threat_detector::ThreatDetector;
pub use threat_vector::ThreatVector;
pub use uhid_key_ring::{KeyRingError, UhidKeyRing};
