//! aether::events — Rust port of `CircleAI.Aether/Events/*.cs`.
//!
//! Aether publishes; BhenguAI subscribes. These are the protocol-layer event
//! records Aether emits, plus the two shared enums that flow through the whole
//! contract surface: [`AetherThreatLevel`] and [`AetherInstallLevel`].
//!
//! `TimeSpan?` maps to `Option<chrono::Duration>`; `DateTimeOffset` maps to
//! `DateTime<Utc>`; `IReadOnlyList<string>` / `IReadOnlyDictionary<string,string>`
//! map to `Vec<String>` / `BTreeMap<String,String>`. All records are value
//! types (`Clone + PartialEq`) matching the C# `sealed record`.

use std::collections::BTreeMap;

use chrono::{DateTime, Duration, Utc};
use serde::{Deserialize, Serialize};

// ─────────────────────────────────────────────────────────────────────────────
// Shared enums
// ─────────────────────────────────────────────────────────────────────────────

/// Protocol-level threat severity as assessed by Aether itself, before any AI
/// reasoning is applied. Ordinals follow the C# declaration order.
#[derive(Debug, Clone, Copy, PartialEq, Eq, PartialOrd, Ord, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum AetherThreatLevel {
    None = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}

/// Indicates where Aether is installed and who manages it. Mirrors
/// `AetherInstallLevel` in `IAetherContext.cs`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum AetherInstallLevel {
    /// Aether is not present on this device.
    None = 0,
    /// Aether was installed at app level — bundled or downloaded at first launch.
    App = 1,
    /// Aether is a system service managed by the OS. Requires biometric +
    /// device admin auth to toggle.
    Os = 2,
}

// ─────────────────────────────────────────────────────────────────────────────
// Node events
// ─────────────────────────────────────────────────────────────────────────────

/// Kinds of node lifecycle transitions Aether can emit.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum AetherNodeEventKind {
    Joined = 0,
    Left = 1,
    HealthChanged = 2,
}

/// Point-in-time health snapshot for a single mesh node.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AetherNodeHealth {
    /// 0.0 (untrusted) to 1.0 (fully trusted).
    pub trust_score: f64,
    pub is_reachable: bool,
    pub latency: Duration,
    pub hop_count: i32,
}

impl AetherNodeHealth {
    pub fn new(trust_score: f64, is_reachable: bool, latency: Duration, hop_count: i32) -> Self {
        Self {
            trust_score,
            is_reachable,
            latency,
            hop_count,
        }
    }

    /// Returns true when `trust_score` is within the valid 0–1 range.
    pub fn is_valid(&self) -> bool {
        (0.0..=1.0).contains(&self.trust_score)
    }
}

/// Emitted by Aether whenever a node joins, leaves, or changes health.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AetherNodeEvent {
    pub node_id: String,
    pub kind: AetherNodeEventKind,
    pub health: AetherNodeHealth,
    pub occurred_at: DateTime<Utc>,
}

impl AetherNodeEvent {
    pub fn new(
        node_id: impl Into<String>,
        kind: AetherNodeEventKind,
        health: AetherNodeHealth,
        occurred_at: DateTime<Utc>,
    ) -> Self {
        Self {
            node_id: node_id.into(),
            kind,
            health,
            occurred_at,
        }
    }

    /// True when this is a departure event.
    pub fn is_exit(&self) -> bool {
        self.kind == AetherNodeEventKind::Left
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Transport events
// ─────────────────────────────────────────────────────────────────────────────

/// Physical or logical transport medium Aether is using.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum AetherTransportKind {
    WiFi = 0,
    Bluetooth = 1,
    LoRa = 2,
    Nfc = 3,
    Cellular = 4,
    Ethernet = 5,
    Unknown = 6,
}

/// Kinds of transport-layer observations Aether can emit.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum AetherTransportEventKind {
    Selected = 0,
    Changed = 1,
    LatencyMeasured = 2,
    PacketLoss = 3,
}

/// Emitted when Aether selects, changes, or measures quality on a transport.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AetherTransportEvent {
    pub node_id: String,
    pub kind: AetherTransportEventKind,
    pub transport: AetherTransportKind,
    pub latency: Option<Duration>,
    pub packet_loss_rate: Option<f64>,
    pub occurred_at: DateTime<Utc>,
}

impl AetherTransportEvent {
    pub fn new(
        node_id: impl Into<String>,
        kind: AetherTransportEventKind,
        transport: AetherTransportKind,
        latency: Option<Duration>,
        packet_loss_rate: Option<f64>,
        occurred_at: DateTime<Utc>,
    ) -> Self {
        Self {
            node_id: node_id.into(),
            kind,
            transport,
            latency,
            packet_loss_rate,
            occurred_at,
        }
    }

    /// True when `packet_loss_rate` is set and exceeds `threshold` (0.0–1.0).
    pub fn exceeds_loss(&self, threshold: f64) -> bool {
        matches!(self.packet_loss_rate, Some(r) if r > threshold)
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Route events
// ─────────────────────────────────────────────────────────────────────────────

/// Kinds of routing changes Aether can emit.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum AetherRouteEventKind {
    Discovered = 0,
    Changed = 1,
    Failed = 2,
}

/// Emitted when Aether discovers, updates, or loses a route between two nodes.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AetherRouteEvent {
    pub source_node_id: String,
    pub destination_node_id: String,
    pub path: Vec<String>,
    pub kind: AetherRouteEventKind,
    pub failure_reason: Option<String>,
    pub occurred_at: DateTime<Utc>,
}

impl AetherRouteEvent {
    pub fn new(
        source_node_id: impl Into<String>,
        destination_node_id: impl Into<String>,
        path: Vec<String>,
        kind: AetherRouteEventKind,
        failure_reason: Option<String>,
        occurred_at: DateTime<Utc>,
    ) -> Self {
        Self {
            source_node_id: source_node_id.into(),
            destination_node_id: destination_node_id.into(),
            path,
            kind,
            failure_reason,
            occurred_at,
        }
    }

    /// Number of hops in this route, including source and destination.
    pub fn hop_count(&self) -> usize {
        self.path.len()
    }

    /// True when this event represents a routing failure.
    pub fn is_failed(&self) -> bool {
        self.kind == AetherRouteEventKind::Failed
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Security events
// ─────────────────────────────────────────────────────────────────────────────

/// Categories of security-relevant observations Aether can detect at the
/// protocol layer, without requiring AI.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum AetherSecurityEventKind {
    NodeAuthAttempt = 0,
    RoutingAnomaly = 1,
    NodeBehaviourChange = 2,
    EncryptionEvent = 3,
    IntrusionSignal = 4,
    PrivilegeAttempt = 5,
}

/// Emitted by Aether when a security-relevant event occurs at the protocol
/// layer. Primary feed for the AI Security Layer.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AetherSecurityEvent {
    pub node_id: String,
    pub kind: AetherSecurityEventKind,
    pub threat_level: AetherThreatLevel,
    pub description: String,
    pub metadata: BTreeMap<String, String>,
    pub occurred_at: DateTime<Utc>,
}

impl AetherSecurityEvent {
    pub fn new(
        node_id: impl Into<String>,
        kind: AetherSecurityEventKind,
        threat_level: AetherThreatLevel,
        description: impl Into<String>,
        metadata: BTreeMap<String, String>,
        occurred_at: DateTime<Utc>,
    ) -> Self {
        Self {
            node_id: node_id.into(),
            kind,
            threat_level,
            description: description.into(),
            metadata,
            occurred_at,
        }
    }

    /// True when `threat_level` is High or Critical.
    pub fn is_high_severity(&self) -> bool {
        matches!(
            self.threat_level,
            AetherThreatLevel::High | AetherThreatLevel::Critical
        )
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Network events
// ─────────────────────────────────────────────────────────────────────────────

/// Mesh-wide topology and congestion observations.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum AetherNetworkEventKind {
    TopologyChanged = 0,
    CongestionDetected = 1,
    PartitionDetected = 2,
}

/// Emitted when the mesh topology or overall network health changes.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AetherNetworkEvent {
    pub kind: AetherNetworkEventKind,
    pub node_count: i32,
    pub active_route_count: i32,
    pub congestion_level: f64,
    pub occurred_at: DateTime<Utc>,
}

impl AetherNetworkEvent {
    pub fn new(
        kind: AetherNetworkEventKind,
        node_count: i32,
        active_route_count: i32,
        congestion_level: f64,
        occurred_at: DateTime<Utc>,
    ) -> Self {
        Self {
            kind,
            node_count,
            active_route_count,
            congestion_level,
            occurred_at,
        }
    }

    /// True when `congestion_level` exceeds 0.75 — a useful default alert
    /// threshold.
    pub fn is_high_congestion(&self) -> bool {
        self.congestion_level > 0.75
    }
}
