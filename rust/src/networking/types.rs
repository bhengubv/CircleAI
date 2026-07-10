//! networking::types — Rust port of the value types in `CircleAI.Networking`.
//!
//! Ports (faithful to the C# records/enums):
//!   * enums  [`TransportKind`], [`ConnectivityState`], [`MessagePriority`],
//!            [`PeerRole`]  (`SyncDeliveryMode` is reused from [`crate::sync`]).
//!   * records [`NetworkPayload`], [`NetworkContext`], [`PeerInfo`],
//!            [`SchedulingHint`], [`SyncDelta`] (the networking variant that
//!            carries an optional [`SchedulingHint`]).
//!
//! The C# records are immutable value objects; the Rust ports are plain structs
//! with the same fields and the same static factory methods
//! (`NetworkPayload::create`, `NetworkContext::offline`). `Guid.NewGuid().ToString("N")`
//! maps to a hyphen-less lowercase `uuid::Uuid::new_v4().simple()`.
//! `TimeSpan? Ttl` → `Option<Duration>`; `ReadOnlyMemory<byte>` → `Vec<u8>`;
//! `IReadOnlyDictionary<string,string>` → `BTreeMap<String,String>` (ordered so
//! metadata round-trips deterministically).

use std::collections::BTreeMap;
use std::time::Duration;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

pub use crate::sync::SyncDeliveryMode;

// ─────────────────────────────────────────────────────────────────────────────
// Enums — 1:1 with `NetworkTypes.cs`
// ─────────────────────────────────────────────────────────────────────────────

/// The concrete transport a payload can travel over. Ordering matches the C#
/// `enum` declaration order (used by the default selector cascade).
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum TransportKind {
    Http,
    WebSocket,
    Grpc,
    Mqtt,
    Tcp,
    Udp,
    /// WiFi Direct / mDNS / LAN — no Aether required.
    WiFi,
    /// Raw BLE GATT — no Aether required.
    Bluetooth,
    /// Huawei SLE / HarmonyOS — no Aether required.
    NearLink,
    /// Full Aether mesh (Signal E2E + AODV + SOS).
    Aether,
    /// 72hr store-and-forward over any transport.
    Dtn,
    /// Offline queue — no live path at all.
    LocalStore,
}

/// Coarse connectivity state of the device.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum ConnectivityState {
    Online,
    LocalOnly,
    MeshOnly,
    Offline,
}

/// Relative urgency of a payload; drives queue ordering and transport choice.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum MessagePriority {
    Low,
    Normal,
    High,
    Urgent,
    Emergency,
}

/// The role a peer plays in the mesh topology.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
pub enum PeerRole {
    Peer,
    Relay,
    Bridge,
    Sink,
}

// ─────────────────────────────────────────────────────────────────────────────
// NetworkPayload — port of `NetworkPayload.cs`
// ─────────────────────────────────────────────────────────────────────────────

/// Immutable envelope for a single message or data unit traversing any transport.
///
/// Transports must not mutate it — create a new payload instead (the C# type is a
/// `record` for exactly this reason; clone + rebuild here).
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NetworkPayload {
    pub id: String,
    pub source_id: Option<String>,
    pub destination_id: Option<String>,
    pub data: Vec<u8>,
    pub priority: MessagePriority,
    /// `None` = no TTL.
    pub ttl: Option<Duration>,
    pub content_type: String,
    pub metadata: BTreeMap<String, String>,
    pub created_at: DateTime<Utc>,
}

impl NetworkPayload {
    /// Full constructor (mirrors the C# positional record constructor). Prefer
    /// [`NetworkPayload::create`] for new payloads — it fills the id/timestamp.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        id: impl Into<String>,
        source_id: Option<String>,
        destination_id: Option<String>,
        data: Vec<u8>,
        priority: MessagePriority,
        ttl: Option<Duration>,
        content_type: impl Into<String>,
        metadata: BTreeMap<String, String>,
        created_at: DateTime<Utc>,
    ) -> Self {
        Self {
            id: id.into(),
            source_id,
            destination_id,
            data,
            priority,
            ttl,
            content_type: content_type.into(),
            metadata,
            created_at,
        }
    }

    /// Factory mirroring the C# `NetworkPayload.Create`. Assigns a fresh
    /// `Guid.NewGuid().ToString("N")`-style id (32 lowercase hex chars, no
    /// dashes), no source, empty metadata, and `CreatedAt = UtcNow`.
    pub fn create(
        data: Vec<u8>,
        destination_id: Option<String>,
        priority: MessagePriority,
        content_type: impl Into<String>,
        ttl: Option<Duration>,
    ) -> Self {
        Self {
            id: Uuid::new_v4().simple().to_string(),
            source_id: None,
            destination_id,
            data,
            priority,
            ttl,
            content_type: content_type.into(),
            metadata: BTreeMap::new(),
            created_at: Utc::now(),
        }
    }

    /// Convenience matching the C# default parameters:
    /// `priority = Normal`, `content_type = "application/octet-stream"`,
    /// `ttl = null`, `destination = null`.
    pub fn of(data: Vec<u8>) -> Self {
        Self::create(
            data,
            None,
            MessagePriority::Normal,
            "application/octet-stream",
            None,
        )
    }

    /// Returns a copy with `source_id` set (transports stamp their origin without
    /// mutating the shared payload — the C# `record with { SourceId = ... }`).
    pub fn with_source(&self, source_id: impl Into<String>) -> Self {
        let mut next = self.clone();
        next.source_id = Some(source_id.into());
        next
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// NetworkContext — port of `NetworkContext.cs`
// ─────────────────────────────────────────────────────────────────────────────

/// Snapshot of current connectivity state.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct NetworkContext {
    pub state: ConnectivityState,
    pub preferred_transport: TransportKind,
    pub available_transports: Vec<TransportKind>,
    pub signal_strength_dbm: Option<i32>,
    pub estimated_bandwidth_bps: Option<i64>,
    pub latency_ms: Option<i64>,
    pub nearby_peer_count: i32,
    pub snapshot_at: DateTime<Utc>,
}

impl NetworkContext {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        state: ConnectivityState,
        preferred_transport: TransportKind,
        available_transports: Vec<TransportKind>,
        signal_strength_dbm: Option<i32>,
        estimated_bandwidth_bps: Option<i64>,
        latency_ms: Option<i64>,
        nearby_peer_count: i32,
        snapshot_at: DateTime<Utc>,
    ) -> Self {
        Self {
            state,
            preferred_transport,
            available_transports,
            signal_strength_dbm,
            estimated_bandwidth_bps,
            latency_ms,
            nearby_peer_count,
            snapshot_at,
        }
    }

    /// The offline snapshot. Mirrors the C# `NetworkContext.Offline` static:
    /// state Offline, preferred transport LocalStore, no live transports, zero
    /// peers, `SnapshotAt = UtcNow`.
    ///
    /// C# exposes this as a `static readonly` field frozen at type-init; because
    /// the timestamp there is a *point in time*, the Rust analogue is a
    /// constructor so each caller gets a fresh `UtcNow` (the timestamp is
    /// documentary only).
    pub fn offline() -> Self {
        Self {
            state: ConnectivityState::Offline,
            preferred_transport: TransportKind::LocalStore,
            available_transports: Vec::new(),
            signal_strength_dbm: None,
            estimated_bandwidth_bps: None,
            latency_ms: None,
            nearby_peer_count: 0,
            snapshot_at: Utc::now(),
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PeerInfo — port of `PeerInfo.cs`
// ─────────────────────────────────────────────────────────────────────────────

/// Describes a discovered peer on any transport.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct PeerInfo {
    pub node_id: String,
    pub display_name: Option<String>,
    pub supported_transports: Vec<TransportKind>,
    pub role: PeerRole,
    pub signal_strength_dbm: Option<i32>,
    pub last_seen: DateTime<Utc>,
}

impl PeerInfo {
    pub fn new(
        node_id: impl Into<String>,
        display_name: Option<String>,
        supported_transports: Vec<TransportKind>,
        role: PeerRole,
        signal_strength_dbm: Option<i32>,
        last_seen: DateTime<Utc>,
    ) -> Self {
        Self {
            node_id: node_id.into(),
            display_name,
            supported_transports,
            role,
            signal_strength_dbm,
            last_seen,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SchedulingHint — port of `SchedulingHint.cs`
// ─────────────────────────────────────────────────────────────────────────────

/// Advisory scheduling information attached to a [`SyncDelta`] by the Circle AI
/// layer. The transport is free to disregard these hints, but honouring them
/// minimises unnecessary wakeups and battery drain on constrained devices.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SchedulingHint {
    /// Device IDs strongly preferred as the first delivery targets. Empty means
    /// "no preference".
    pub preferred_peer_ids: Vec<String>,
    /// Earliest UTC timestamp the transport should attempt delivery. `None` =
    /// forward immediately.
    pub suggested_window_utc: Option<DateTime<Utc>>,
    /// Confidence in `[0.0, 1.0]`. Below 0.5 is a weak advisory; above 0.8 is a
    /// strong advisory.
    pub confidence_score: f32,
}

impl SchedulingHint {
    pub fn new(
        preferred_peer_ids: Vec<String>,
        suggested_window_utc: Option<DateTime<Utc>>,
        confidence_score: f32,
    ) -> Self {
        Self {
            preferred_peer_ids,
            suggested_window_utc,
            confidence_score,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SyncDelta — port of `SyncDelta.cs` (the networking variant)
// ─────────────────────────────────────────────────────────────────────────────

/// An incremental state change that must reach every device owned by `owner_id`.
///
/// This is the networking-namespace variant of the sync primitive: unlike
/// [`crate::sync::SyncDelta`] it carries an optional [`SchedulingHint`] AI-layer
/// routing advisory. Kept as its own type to mirror `CircleAI.Networking.SyncDelta`
/// exactly.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SyncDelta {
    /// Identity whose state this belongs to.
    pub owner_id: String,
    /// Origin device.
    pub source_device_id: String,
    /// `""` = broadcast to all owned devices.
    pub target_device_id: String,
    /// `"memory.episodic"` | `"affect.state"` | `"persona"` | custom.
    pub domain_key: String,
    pub payload: Vec<u8>,
    /// Monotonic per owner+domain.
    pub sequence: i64,
    pub delivery_mode: SyncDeliveryMode,
    pub ttl: Option<Duration>,
    pub created_at: DateTime<Utc>,
    /// Optional AI-layer routing advisory.
    pub scheduling_hint: Option<SchedulingHint>,
}

impl SyncDelta {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        owner_id: impl Into<String>,
        source_device_id: impl Into<String>,
        target_device_id: impl Into<String>,
        domain_key: impl Into<String>,
        payload: Vec<u8>,
        sequence: i64,
        delivery_mode: SyncDeliveryMode,
        ttl: Option<Duration>,
        created_at: DateTime<Utc>,
        scheduling_hint: Option<SchedulingHint>,
    ) -> Self {
        Self {
            owner_id: owner_id.into(),
            source_device_id: source_device_id.into(),
            target_device_id: target_device_id.into(),
            domain_key: domain_key.into(),
            payload,
            sequence,
            delivery_mode,
            ttl,
            created_at,
            scheduling_hint,
        }
    }

    /// `true` when this delta broadcasts to all of the owner's devices.
    pub fn is_broadcast(&self) -> bool {
        self.target_device_id.is_empty()
    }
}
