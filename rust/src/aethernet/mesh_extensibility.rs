//! aethernet::mesh_extensibility — the AetherNet mesh-runtime boundary.
//!
//! The C# `CircleAI.AetherNet` adapters bind against the external
//! `AetherNet.Extensibility` / `AetherNet.Protocol` / `AetherNet.Constants`
//! packages (the mesh runtime, shipped from a separate repo). Per the port
//! rules, that native/socket dependency is injected behind an interface: this
//! module reproduces the mesh-side vocabulary the adapters touch — event
//! records, enums, the `SecurityDirective` shape, the AI-provider seat, and the
//! telemetry publisher — as Rust traits and value types.
//!
//! Only the surface the adapters actually consume is modelled; the mesh's
//! routing/crypto internals stay out of scope. An in-memory
//! [`InMemoryMeshTelemetry`] and recording sinks make the whole bridge testable
//! without a live mesh.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Duration, Utc};

// ─────────────────────────────────────────────────────────────────────────────
// Mesh protocol constants (AetherNet.Constants.ProtocolConstants)
// ─────────────────────────────────────────────────────────────────────────────

/// The AetherNet current protocol version, mirroring
/// `AetherNet.Constants.ProtocolConstants.CurrentProtocolVersion`. The C#
/// adapter builds `new Version(CurrentProtocolVersion, 0, 0, 0)` from it.
pub const CURRENT_PROTOCOL_VERSION: i32 = 3;

// ─────────────────────────────────────────────────────────────────────────────
// Mesh-side enums (AetherNet.Extensibility.*)
// ─────────────────────────────────────────────────────────────────────────────

/// Mesh-side threat level (`AetherNet.Extensibility.AetherNetThreatLevel`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AetherNetThreatLevel {
    None,
    Low,
    Medium,
    High,
    Critical,
}

/// Mesh-side node event kind.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AetherNetNodeEventKind {
    Joined,
    Left,
    HealthChanged,
}

/// Mesh-side transport event kind.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AetherNetTransportEventKind {
    Selected,
    Changed,
    LatencyMeasured,
    PacketLoss,
}

/// Mesh-side transport kind. Richer than CircleAI's OS-classification enum
/// (adds WiFiDirect, NearLink, HttpRelay).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AetherNetTransportKind {
    Bluetooth,
    WiFi,
    WiFiDirect,
    LoRa,
    Nfc,
    NearLink,
    HttpRelay,
}

/// Mesh-side route event kind.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AetherNetRouteEventKind {
    Discovered,
    Changed,
    Failed,
}

/// Mesh-side security event kind.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AetherNetSecurityEventKind {
    NodeAuthAttempt,
    RoutingAnomaly,
    NodeBehaviourChange,
    EncryptionEvent,
    IntrusionSignal,
    PrivilegeAttempt,
}

/// Mesh-side network event kind.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AetherNetNetworkEventKind {
    TopologyChanged,
    CongestionDetected,
    PartitionDetected,
}

/// Mesh-side security-directive kind (`AetherNet.Extensibility.SecurityDirectiveKind`).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum MeshSecurityDirectiveKind {
    UpdateNodeTrust,
    AvoidNode,
    QuarantineNode,
    ReleaseNode,
    RequestReauth,
    ElevateMonitoring,
}

/// Mesh-side AI threat level (`AetherNet.Protocol.AiThreatLevel`). Note: only
/// four values — no `Critical` (Critical folds to High at the AI seat).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum AiThreatLevel {
    None,
    Low,
    Medium,
    High,
}

// ─────────────────────────────────────────────────────────────────────────────
// Mesh-side event records (AetherNet.Extensibility.Events.*)
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Clone, PartialEq)]
pub struct AetherNetNodeHealth {
    pub trust_score: f64,
    pub is_reachable: bool,
    pub latency: Duration,
    pub hop_count: i32,
}

#[derive(Debug, Clone, PartialEq)]
pub struct AetherNetNodeEvent {
    pub node_id: String,
    pub kind: AetherNetNodeEventKind,
    pub health: AetherNetNodeHealth,
    pub occurred_at: DateTime<Utc>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct AetherNetTransportEvent {
    pub node_id: String,
    pub kind: AetherNetTransportEventKind,
    pub transport: AetherNetTransportKind,
    pub latency: Option<Duration>,
    pub packet_loss_rate: Option<f64>,
    pub occurred_at: DateTime<Utc>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct AetherNetRouteEvent {
    pub source_node_id: String,
    pub destination_node_id: String,
    pub path: Vec<String>,
    pub kind: AetherNetRouteEventKind,
    pub failure_reason: Option<String>,
    pub occurred_at: DateTime<Utc>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct AetherNetSecurityEvent {
    pub node_id: String,
    pub kind: AetherNetSecurityEventKind,
    pub threat_level: AetherNetThreatLevel,
    pub description: String,
    pub metadata: BTreeMap<String, String>,
    pub occurred_at: DateTime<Utc>,
}

#[derive(Debug, Clone, PartialEq)]
pub struct AetherNetNetworkEvent {
    pub kind: AetherNetNetworkEventKind,
    pub node_count: i32,
    pub active_route_count: i32,
    pub congestion_level: f64,
    pub occurred_at: DateTime<Utc>,
}

// ─────────────────────────────────────────────────────────────────────────────
// Mesh-side SecurityDirective (AetherNet.Extensibility.SecurityDirective)
// ─────────────────────────────────────────────────────────────────────────────

/// The mesh runtime's directive shape. Field order matches the C# positional
/// record `(Kind, TargetNodeId, TrustScoreOverride, ThreatLevel, Reason,
/// Duration, IssuedAt)`.
#[derive(Debug, Clone, PartialEq)]
pub struct MeshSecurityDirective {
    pub kind: MeshSecurityDirectiveKind,
    pub target_node_id: Option<String>,
    pub trust_score_override: Option<f64>,
    pub threat_level: AetherNetThreatLevel,
    pub reason: String,
    pub duration: Option<Duration>,
    pub issued_at: DateTime<Utc>,
}

// ─────────────────────────────────────────────────────────────────────────────
// Mesh-side AI provider result types (AetherNet.Protocol.*)
// ─────────────────────────────────────────────────────────────────────────────

/// `AetherNet.Protocol.AiRouteSuggestion`.
#[derive(Debug, Clone, PartialEq)]
pub struct AiRouteSuggestion {
    pub path: Vec<String>,
    pub confidence: f64,
}

/// `AetherNet.Protocol.AiNetworkHealthReport`.
#[derive(Debug, Clone, PartialEq)]
pub struct AiNetworkHealthReport {
    pub overall_score: f64,
    pub trusted_node_count: i32,
    pub suspicious_node_count: i32,
    pub summary: String,
    pub generated_at: DateTime<Utc>,
}

/// `AetherNet.Protocol.MeshPacket` — only the field the AI seat reads.
#[derive(Debug, Clone, PartialEq)]
pub struct MeshPacket {
    pub source_uhid: String,
}

// ─────────────────────────────────────────────────────────────────────────────
// Mesh-side traits (the injected boundary)
// ─────────────────────────────────────────────────────────────────────────────

/// `AetherNet.Extensibility.ISecurityDirectiveConsumer` — the mesh policy engine
/// that receives directives.
pub trait IMeshSecurityDirectiveConsumer: Send + Sync {
    fn on_directive(&self, directive: &MeshSecurityDirective);
}

/// `AetherNet.Extensibility.IAetherNetTelemetryObserver` — receives mesh events.
pub trait IAetherNetTelemetryObserver: Send + Sync {
    fn on_node_event(&self, e: &AetherNetNodeEvent);
    fn on_transport_event(&self, e: &AetherNetTransportEvent);
    fn on_route_event(&self, e: &AetherNetRouteEvent);
    fn on_security_event(&self, e: &AetherNetSecurityEvent);
    fn on_network_event(&self, e: &AetherNetNetworkEvent);
}

/// Unsubscribe handle returned by [`IAetherNetTelemetry::subscribe`].
pub struct MeshTelemetrySubscription {
    remover: Option<Box<dyn FnOnce() + Send + Sync>>,
}

impl MeshTelemetrySubscription {
    fn new(remover: impl FnOnce() + Send + Sync + 'static) -> Self {
        Self {
            remover: Some(Box::new(remover)),
        }
    }

    pub fn unsubscribe(mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}

impl Drop for MeshTelemetrySubscription {
    fn drop(&mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}

/// `AetherNet.Extensibility.IAetherNetTelemetry` — the mesh telemetry publisher.
pub trait IAetherNetTelemetry: Send + Sync {
    fn subscribe(&self, observer: Arc<dyn IAetherNetTelemetryObserver>)
        -> MeshTelemetrySubscription;
}

/// `AetherNet.Extensibility.IAetherNetAiProvider` — the AI seat the mesh's
/// routing layer consults.
pub trait IAetherNetAiProvider: Send + Sync {
    /// Whether the provider can produce advice right now.
    fn is_available(&self) -> bool;

    /// Suggest routes toward `destination_uhid` for a `payload_bytes` message.
    fn suggest_routes(&self, destination_uhid: &str, payload_bytes: i32) -> Vec<AiRouteSuggestion>;

    /// Per-transport biases keyed by transport id. Empty tells the mesh to use
    /// its built-in selector.
    fn get_transport_biases(&self, payload_bytes: i32) -> BTreeMap<String, f64>;

    /// Assess the threat of an inbound packet.
    fn assess_threat(&self, packet: &MeshPacket) -> AiThreatLevel;

    /// Aggregate network health as seen by the AI provider.
    fn get_network_health(&self) -> AiNetworkHealthReport;
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryMeshTelemetry — a working mesh telemetry publisher for tests/hosts
// ─────────────────────────────────────────────────────────────────────────────

/// In-process implementation of [`IAetherNetTelemetry`]. A host raising mesh
/// events (or a test) publishes here; every current subscriber's matching
/// callback fires synchronously. Snapshot-under-lock, fire-outside-lock.
#[derive(Default)]
pub struct InMemoryMeshTelemetry {
    observers: Arc<Mutex<Vec<(u64, Arc<dyn IAetherNetTelemetryObserver>)>>>,
    next_id: Mutex<u64>,
}

impl InMemoryMeshTelemetry {
    pub fn new() -> Self {
        Self {
            observers: Arc::new(Mutex::new(Vec::new())),
            next_id: Mutex::new(0),
        }
    }

    pub fn subscriber_count(&self) -> usize {
        self.observers.lock().unwrap().len()
    }

    fn snapshot(&self) -> Vec<Arc<dyn IAetherNetTelemetryObserver>> {
        let guard = self.observers.lock().unwrap();
        guard.iter().map(|(_, o)| Arc::clone(o)).collect()
    }

    pub fn publish_node_event(&self, e: &AetherNetNodeEvent) {
        for o in self.snapshot() {
            o.on_node_event(e);
        }
    }
    pub fn publish_transport_event(&self, e: &AetherNetTransportEvent) {
        for o in self.snapshot() {
            o.on_transport_event(e);
        }
    }
    pub fn publish_route_event(&self, e: &AetherNetRouteEvent) {
        for o in self.snapshot() {
            o.on_route_event(e);
        }
    }
    pub fn publish_security_event(&self, e: &AetherNetSecurityEvent) {
        for o in self.snapshot() {
            o.on_security_event(e);
        }
    }
    pub fn publish_network_event(&self, e: &AetherNetNetworkEvent) {
        for o in self.snapshot() {
            o.on_network_event(e);
        }
    }
}

impl IAetherNetTelemetry for InMemoryMeshTelemetry {
    fn subscribe(
        &self,
        observer: Arc<dyn IAetherNetTelemetryObserver>,
    ) -> MeshTelemetrySubscription {
        let id = {
            let mut n = self.next_id.lock().unwrap();
            let id = *n;
            *n += 1;
            id
        };
        self.observers.lock().unwrap().push((id, observer));
        let observers = Arc::clone(&self.observers);
        MeshTelemetrySubscription::new(move || {
            observers.lock().unwrap().retain(|(oid, _)| *oid != id);
        })
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// RecordingMeshDirectiveConsumer — a working mesh policy-engine sink
// ─────────────────────────────────────────────────────────────────────────────

/// Records every [`MeshSecurityDirective`] the mesh receives. Stands in for a
/// mesh policy engine in tests and lets a host inspect what CircleAI forwarded.
#[derive(Default)]
pub struct RecordingMeshDirectiveConsumer {
    received: Mutex<Vec<MeshSecurityDirective>>,
}

impl RecordingMeshDirectiveConsumer {
    pub fn new() -> Self {
        Self {
            received: Mutex::new(Vec::new()),
        }
    }

    /// Snapshot of every directive received so far.
    pub fn received(&self) -> Vec<MeshSecurityDirective> {
        self.received.lock().unwrap().clone()
    }

    pub fn count(&self) -> usize {
        self.received.lock().unwrap().len()
    }
}

impl IMeshSecurityDirectiveConsumer for RecordingMeshDirectiveConsumer {
    fn on_directive(&self, directive: &MeshSecurityDirective) {
        self.received.lock().unwrap().push(directive.clone());
    }
}
