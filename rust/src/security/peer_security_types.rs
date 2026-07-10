//! peer_security_types.rs
//!
//! Transport-agnostic security primitives — Rust port of `PeerSecurityTypes.cs`.
//!
//! These types are deliberately free of any transport dependency (Aether, WiFi,
//! BLE, NearLink, HTTP, etc.). Every transport adapter translates its own event
//! vocabulary into these types before feeding the security layer.
//!
//! Type map:
//!   `PeerSecurityEventKind`   — what happened (transport-neutral event category)
//!   `PeerThreatLevel`         — how severe (None → Critical)
//!   `PeerSecurityEvent`       — one security incident from any transport
//!   `PeerDirectiveKind`       — what the security layer recommends
//!   `PeerDirective`           — a directive issued to all `IPeerDirectiveConsumer`s
//!   `PeerTrustScoreUpdate`    — one change notification emitted by `NodeTrustRegistry`
//!   `PeerSecurityPosture`     — aggregate snapshot of security state
//!   `PeerNetworkHealthReport` — aggregate health across all observed peers
//!   `PeerThreatAssessment`    — per-node threat confidence + indicators
//!   `PeerRoutingAdvice`       — trust-aware path recommendation
//!
//! Traits:
//!   `IPeerDirectiveConsumer`  — receives `PeerDirective`s from any security layer
//!   `IPeerSecurityLayer`      — lifecycle + query surface for the layer
//!   `IPeerIntelligence`       — read-only intelligence queries
//!   `IPeerSecurityEventFeed`  — transport adapters register an event source

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

// ── Enumerations ─────────────────────────────────────────────────────────────

/// Transport-neutral classification of a peer security event.
///
/// Ordinals follow the C# declaration order (0-based) and are stable across
/// language ports.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum PeerSecurityEventKind {
    /// Authentication attempt (login, handshake, re-auth).
    AuthAttempt = 0,
    /// Anomalous routing behaviour detected (loop, black-hole, etc.).
    RoutingAnomaly = 1,
    /// Peer behaviour changed unexpectedly (rate, pattern, protocol).
    BehaviourChange = 2,
    /// Encryption negotiation event (downgrade, cipher mismatch).
    EncryptionEvent = 3,
    /// Active intrusion probe or exploitation attempt.
    IntrusionSignal = 4,
    /// Privilege escalation or capability violation attempt.
    PrivilegeAttempt = 5,
    /// Unusual connection pattern (port scan, rapid reconnect).
    ConnectionAnomaly = 6,
    /// Suspected data exfiltration (volume, destination anomaly).
    DataExfiltration = 7,
    /// Denial-of-service signal (flooding, resource exhaustion).
    DenialOfService = 8,
    /// Catch-all for events that do not map to a specific category.
    Unknown = 9,
}

/// Severity level for a peer security event or threat assessment.
/// Values match the intuitive ordering: `None` is safest, `Critical` is worst.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum PeerThreatLevel {
    /// No threat — event carries no security significance.
    None = 0,
    /// Low-level anomaly — monitor but no action required.
    Low = 1,
    /// Notable anomaly — elevated monitoring recommended.
    Medium = 2,
    /// Significant threat — routing around the peer recommended.
    High = 3,
    /// Active or confirmed attack — quarantine the peer.
    Critical = 4,
}

/// The action recommended by the security layer for a given peer.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum PeerDirectiveKind {
    /// Increase observation cadence; no traffic restriction yet.
    ElevateMonitoring = 0,
    /// Exclude the peer from routing; still accept inbound connections.
    AvoidNode = 1,
    /// Hard-block the peer — no traffic to or from it.
    QuarantineNode = 2,
    /// Lift a previous directive; the peer has recovered sufficient trust.
    /// Not issued automatically — requires explicit operator action.
    ReleaseNode = 3,
}

// ── Records ───────────────────────────────────────────────────────────────────

/// One security incident observed on any transport.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PeerSecurityEvent {
    /// Stable identifier of the peer that generated the event.
    pub node_id: String,
    /// Transport-neutral event category.
    pub kind: PeerSecurityEventKind,
    /// Assessed severity at the time of observation.
    pub threat_level: PeerThreatLevel,
    /// Human-readable description of the event.
    pub description: String,
    /// Identifier for the transport that produced the event
    /// (e.g. `"aether"`, `"wifi"`, `"ble"`, `"nearlink"`, `"http"`).
    pub transport_id: String,
    /// UTC timestamp of the event.
    pub occurred_at: DateTime<Utc>,
}

impl PeerSecurityEvent {
    /// Creates a security event.
    pub fn new(
        node_id: impl Into<String>,
        kind: PeerSecurityEventKind,
        threat_level: PeerThreatLevel,
        description: impl Into<String>,
        transport_id: impl Into<String>,
        occurred_at: DateTime<Utc>,
    ) -> Self {
        Self {
            node_id: node_id.into(),
            kind,
            threat_level,
            description: description.into(),
            transport_id: transport_id.into(),
            occurred_at,
        }
    }
}

/// A security directive issued to all registered [`IPeerDirectiveConsumer`]
/// subscribers when a peer's trust crosses a threshold.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PeerDirective {
    /// The recommended action.
    pub kind: PeerDirectiveKind,
    /// The peer to which the directive applies.
    pub target_node_id: String,
    /// Current trust score of the peer at time of issue.
    pub trust_score: f64,
    /// Threat level at time of issue.
    pub threat_level: PeerThreatLevel,
    /// Human-readable explanation for the directive.
    pub reason: String,
    /// Optional duration after which the directive should be re-evaluated.
    /// `None` means permanent until an explicit [`PeerDirectiveKind::ReleaseNode`].
    pub duration: Option<chrono::Duration>,
    /// UTC timestamp of issue.
    pub issued_at: DateTime<Utc>,
}

/// Notification emitted by `NodeTrustRegistry` whenever a node's trust score
/// changes.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PeerTrustScoreUpdate {
    /// The peer whose score changed.
    pub node_id: String,
    /// Score before this change.
    pub previous_score: f64,
    /// Score after this change.
    pub new_score: f64,
    /// Short description of the cause (event description or `"passive-recovery"`).
    pub reason: String,
    /// UTC timestamp of the change.
    pub changed_at: DateTime<Utc>,
}

/// Snapshot of the overall security posture across all observed peers.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PeerSecurityPosture {
    /// Worst-case threat level in the current peer set.
    pub overall_threat_level: PeerThreatLevel,
    /// Number of peers at or below the quarantine threshold.
    pub quarantined_peer_count: i32,
    /// Number of peers elevated beyond monitoring threshold but not yet quarantined.
    pub monitored_peer_count: i32,
    /// Whether the security layer is currently running.
    pub is_active: bool,
    /// UTC timestamp of this snapshot.
    pub generated_at: DateTime<Utc>,
}

/// Aggregate network health across all observed peers.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PeerNetworkHealthReport {
    /// Average trust score `[0.0, 1.0]` across all peers.
    pub overall_score: f64,
    /// Peers above the avoid-node threshold.
    pub trusted_peer_count: i32,
    /// Peers at or below the elevate-monitoring threshold.
    pub suspicious_peer_count: i32,
    /// Human-readable health summary.
    pub summary: String,
    /// UTC timestamp of this report.
    pub generated_at: DateTime<Utc>,
}

/// Per-peer threat assessment: confidence score, threat level, and detected
/// indicators.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PeerThreatAssessment {
    /// The assessed peer.
    pub node_id: String,
    /// Likelihood that the peer is a genuine threat `[0.0, 1.0]`.
    /// Derived from trust deficit + indicator count.
    pub confidence: f64,
    /// Classified severity.
    pub threat_level: PeerThreatLevel,
    /// Human-readable indicator tags (e.g. `"repeated-auth-attempts"`).
    pub indicators: Vec<String>,
    /// UTC timestamp of this assessment.
    pub assessed_at: DateTime<Utc>,
}

/// Trust-aware routing recommendation for reaching a destination peer.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct PeerRoutingAdvice {
    /// The target peer.
    pub destination_node_id: String,
    /// Ordered list of peer IDs forming the recommended path.
    /// Empty when no safe path is available.
    pub recommended_path: Vec<String>,
    /// Peers that should be excluded from routing.
    pub avoid_node_ids: Vec<String>,
    /// Confidence in the recommendation `[0.0, 1.0]`.
    pub confidence: f64,
    /// Human-readable explanation.
    pub reasoning: String,
    /// UTC timestamp of this advice.
    pub generated_at: DateTime<Utc>,
}

// ── Traits ────────────────────────────────────────────────────────────────────

/// Receives security directives from any [`IPeerSecurityLayer`] implementation.
pub trait IPeerDirectiveConsumer: Send + Sync {
    /// Called when the security layer issues a directive for a peer.
    fn on_directive(&self, directive: &PeerDirective);
}

/// Transport-agnostic security layer lifecycle and posture surface.
///
/// The C# surface is `Task`-based; the Rust port is sync (the recovery loop is
/// driven explicitly via [`IPeerSecurityLayer::tick_recovery`], matching the
/// deterministic in-memory port convention).
pub trait IPeerSecurityLayer: Send + Sync {
    /// Starts the background trust-recovery bookkeeping (marks the layer active).
    fn start(&self);

    /// Stops recovery bookkeeping and releases resources (marks inactive).
    fn stop(&self);

    /// Feed a security event from any transport into the security layer.
    /// The layer will degrade the peer's trust score and issue directives as
    /// needed.
    fn handle_peer_event(&self, e: &PeerSecurityEvent);

    /// Subscribe to receive directives. Drop the returned handle to unsubscribe.
    fn subscribe_to_directives(
        &self,
        consumer: std::sync::Arc<dyn IPeerDirectiveConsumer>,
    ) -> crate::security::DirectiveSubscription;

    /// Returns a snapshot of the current security posture.
    fn get_posture(&self) -> PeerSecurityPosture;
}

/// Transport-agnostic intelligence queries over accumulated trust data.
pub trait IPeerIntelligence: Send + Sync {
    /// Returns aggregate network health across all observed peers.
    fn get_network_health(&self) -> PeerNetworkHealthReport;

    /// Returns a threat assessment for a specific peer.
    fn assess_threat(&self, node_id: &str) -> PeerThreatAssessment;

    /// Returns trust-aware routing advice toward a destination peer.
    fn get_routing_advice(&self, destination_node_id: &str) -> PeerRoutingAdvice;

    /// Drains every trust score change observed since the last drain.
    ///
    /// The C# reference exposes an unbounded `IAsyncEnumerable`; the sync port
    /// drains the unbounded backlog buffer (writes made before this call are
    /// retained, matching the unbounded `Channel`).
    fn stream_trust_scores(&self) -> Vec<PeerTrustScoreUpdate>;
}

/// Implemented by transport adapters to register an event source with the
/// security layer. The security layer calls [`IPeerSecurityEventFeed::pump`]
/// to begin feeding events.
pub trait IPeerSecurityEventFeed: Send + Sync {
    /// Human-readable identifier for this transport (e.g. `"wifi"`, `"ble"`,
    /// `"aether"`).
    fn transport_id(&self) -> &str;

    /// Feeds all currently-available events into `handler`.
    ///
    /// The C# `StartAsync(handler, ct)` pumps until cancellation; the sync port
    /// pumps the currently-buffered batch and returns the count delivered.
    fn pump(&self, handler: &mut dyn FnMut(PeerSecurityEvent)) -> usize;
}
