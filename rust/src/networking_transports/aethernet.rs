//! networking_transports::aethernet — Rust port of `CircleAI.Networking.AetherNet`
//! (`src/CircleAI.Networking.AetherNet/*.cs`).
//!
//! The Aether-mesh binding of the [`crate::networking::INetworkTransport`]
//! contract. Faithful ports:
//!
//!   * [`AetherPeerKind`]                 — port of the C# enum.
//!   * [`AetherPeer`] / [`AetherHopTelemetry`] / [`AetherPacketSummary`] — the C#
//!     `record`s (immutable value objects).
//!   * [`InMemoryAetherNetRegistry`]      — port of the C# registry: peer table,
//!     hop telemetry, packet log, with the same ordering / aggregation rules.
//!   * [`AetherNetworkTransport`]         — `INetworkTransport` over the mesh. The
//!     C# type bridges to `IAetherContext` and hands routing to the
//!     aether-protocol engine (`SendAsync` is a bridge point). Here routing is
//!     injected behind [`IAetherRouter`] with a working, deterministic
//!     [`InMemoryAetherRouter`] so `send`/receive have real behaviour (no stub).
//!   * [`AetherPeerDiscovery`]            — [`crate::networking::IPeerDiscovery`]
//!     over Aether presence beacons; discovery/announcements come from the shared
//!     [`InMemoryAetherNetRegistry`].
//!   * [`AetherSyncChannel`]              — [`crate::networking::ISyncChannel`]
//!     backed by DTN store-and-forward with a 72h default TTL; per-(owner,domain)
//!     monotonic sequence tracking exactly like the C# `_sequences` dictionary.
//!
//! `Guid.NewGuid().ToString("N")` → `uuid::Uuid::new_v4().simple()`;
//! `DateTimeOffset` → `chrono::DateTime<Utc>`; `ReadOnlyMemory<byte>` → `Vec<u8>`.

use std::collections::{HashMap, VecDeque};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use crate::networking::{
    INetworkTransport, IPeerDiscovery, ISyncChannel, NetworkPayload, PeerInfo, SyncDelta,
    TransportError, TransportKind,
};

// ─────────────────────────────────────────────────────────────────────────────
// AetherPeerKind — port of the C# enum
// ─────────────────────────────────────────────────────────────────────────────

/// The class of device a mesh peer is. 1:1 with the C# `AetherPeerKind`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum AetherPeerKind {
    Phone,
    Tablet,
    Laptop,
    Desktop,
    Edge,
    Vehicle,
    Iot,
}

// ─────────────────────────────────────────────────────────────────────────────
// Value records
// ─────────────────────────────────────────────────────────────────────────────

/// A discovered mesh peer. Port of the C# `AetherPeer` record.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AetherPeer {
    pub peer_id: String,
    pub kind: AetherPeerKind,
    pub friendly_name: Option<String>,
    pub advertised_capabilities: Vec<String>,
}

impl AetherPeer {
    pub fn new(
        peer_id: impl Into<String>,
        kind: AetherPeerKind,
        friendly_name: Option<String>,
        advertised_capabilities: Vec<String>,
    ) -> Self {
        Self {
            peer_id: peer_id.into(),
            kind,
            friendly_name,
            advertised_capabilities,
        }
    }
}

/// One hop-latency observation for a peer. Port of the C# `AetherHopTelemetry`.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct AetherHopTelemetry {
    pub peer_id: String,
    pub hop_count: i32,
    pub round_trip_ms: f64,
    pub at_utc: DateTime<Utc>,
}

impl AetherHopTelemetry {
    pub fn new(
        peer_id: impl Into<String>,
        hop_count: i32,
        round_trip_ms: f64,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            peer_id: peer_id.into(),
            hop_count,
            round_trip_ms,
            at_utc,
        }
    }
}

/// A summary of one packet that traversed the mesh. Port of the C#
/// `AetherPacketSummary`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct AetherPacketSummary {
    pub packet_id: String,
    pub from_peer: String,
    pub to_peer: String,
    pub bytes: i32,
    pub packet_kind: String,
    pub at_utc: DateTime<Utc>,
}

impl AetherPacketSummary {
    pub fn new(
        packet_id: impl Into<String>,
        from_peer: impl Into<String>,
        to_peer: impl Into<String>,
        bytes: i32,
        packet_kind: impl Into<String>,
        at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            packet_id: packet_id.into(),
            from_peer: from_peer.into(),
            to_peer: to_peer.into(),
            bytes,
            packet_kind: packet_kind.into(),
            at_utc,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryAetherNetRegistry — port of the C# registry
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory mesh registry: peer table + hop telemetry + packet log. Port of the
/// C# `InMemoryAetherNetRegistry`.
///
/// Matches the C# ordering/aggregation exactly:
///   * [`peers`](Self::peers) is ordered by `peer_id` (ordinal).
///   * [`recent_packets`](Self::recent_packets) returns the newest `limit`
///     packets, newest-first.
///   * [`avg_round_trip_ms`](Self::avg_round_trip_ms) averages the peer's RTTs,
///     `0.0` when none (`DefaultIfEmpty(0).Average()`).
///   * [`total_bytes_between`](Self::total_bytes_between) sums packet bytes for a
///     directed pair.
#[derive(Default)]
pub struct InMemoryAetherNetRegistry {
    peers: Mutex<HashMap<String, AetherPeer>>,
    telemetry: Mutex<Vec<AetherHopTelemetry>>,
    packets: Mutex<Vec<AetherPacketSummary>>,
}

impl InMemoryAetherNetRegistry {
    pub fn new() -> Self {
        Self::default()
    }

    /// Registers (or replaces) a peer keyed by `peer_id`.
    pub fn register(&self, p: AetherPeer) {
        self.peers.lock().unwrap().insert(p.peer_id.clone(), p);
    }

    /// The peer with `id`, if registered.
    pub fn get_peer(&self, id: &str) -> Option<AetherPeer> {
        self.peers.lock().unwrap().get(id).cloned()
    }

    /// All peers, ordered by `peer_id` (ordinal). Mirrors `Peers`.
    pub fn peers(&self) -> Vec<AetherPeer> {
        let mut v: Vec<AetherPeer> = self.peers.lock().unwrap().values().cloned().collect();
        v.sort_by(|a, b| a.peer_id.cmp(&b.peer_id));
        v
    }

    /// Records a hop-telemetry observation.
    pub fn record_hop(&self, t: AetherHopTelemetry) {
        self.telemetry.lock().unwrap().push(t);
    }

    /// Records a packet summary.
    pub fn record_packet(&self, p: AetherPacketSummary) {
        self.packets.lock().unwrap().push(p);
    }

    /// The newest `limit` packets, newest-first. Mirrors `RecentPackets`.
    pub fn recent_packets(&self, limit: usize) -> Vec<AetherPacketSummary> {
        let guard = self.packets.lock().unwrap();
        let mut v: Vec<AetherPacketSummary> = guard.clone();
        // OrderByDescending(AtUtc) is a stable sort in LINQ; keep insertion order
        // among equal timestamps by using a stable sort on the reversed key.
        v.sort_by(|a, b| b.at_utc.cmp(&a.at_utc));
        v.truncate(limit);
        v
    }

    /// Average round-trip for `peer_id`; `0.0` if none observed. Mirrors
    /// `AvgRoundTripMs` (`DefaultIfEmpty(0).Average()`).
    pub fn avg_round_trip_ms(&self, peer_id: &str) -> f64 {
        let guard = self.telemetry.lock().unwrap();
        let vals: Vec<f64> = guard
            .iter()
            .filter(|t| t.peer_id == peer_id)
            .map(|t| t.round_trip_ms)
            .collect();
        if vals.is_empty() {
            0.0
        } else {
            vals.iter().sum::<f64>() / vals.len() as f64
        }
    }

    /// Total bytes sent from `from_peer` to `to_peer`. Mirrors
    /// `TotalBytesBetween`.
    pub fn total_bytes_between(&self, from_peer: &str, to_peer: &str) -> i32 {
        self.packets
            .lock()
            .unwrap()
            .iter()
            .filter(|p| p.from_peer == from_peer && p.to_peer == to_peer)
            .map(|p| p.bytes)
            .sum()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IAetherRouter — the injected mesh routing dependency
// ─────────────────────────────────────────────────────────────────────────────

/// The mesh routing engine the transport hands payloads to. In C# this is the
/// aether-protocol `RoutingService` + `SignalCipher` engine (the C# `SendAsync`
/// is a bridge point). Injecting it keeps [`AetherNetworkTransport`] pure and
/// deterministic; a working in-memory router ([`InMemoryAetherRouter`]) is
/// provided so nothing is a stub.
pub trait IAetherRouter: Send + Sync {
    /// Route `payload` over the mesh. `emergency` requests SOS flood mode (the C#
    /// "Emergency payloads trigger SOS flood mode" branch).
    fn route(&self, payload: &NetworkPayload, emergency: bool);
}

/// A deterministic in-memory [`IAetherRouter`] that records every routed payload
/// (and whether it was an SOS flood) so tests / hosts can observe mesh traffic.
#[derive(Default)]
pub struct InMemoryAetherRouter {
    routed: Mutex<Vec<(NetworkPayload, bool)>>,
}

impl InMemoryAetherRouter {
    pub fn new() -> Self {
        Self::default()
    }

    /// Every `(payload, was_sos_flood)` pair routed so far, in order.
    pub fn routed(&self) -> Vec<(NetworkPayload, bool)> {
        self.routed.lock().unwrap().clone()
    }

    /// Count of routed payloads.
    pub fn routed_count(&self) -> usize {
        self.routed.lock().unwrap().len()
    }
}

impl IAetherRouter for InMemoryAetherRouter {
    fn route(&self, payload: &NetworkPayload, emergency: bool) {
        self.routed
            .lock()
            .unwrap()
            .push((payload.clone(), emergency));
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AetherNetworkTransport — port of AetherNetworkTransport.cs
// ─────────────────────────────────────────────────────────────────────────────

/// [`INetworkTransport`] backed by the Aether mesh protocol engine. Port of the
/// C# `AetherNetworkTransport`.
///
/// Availability is delegated to the injected mesh context via
/// [`AetherAvailability::is_available`] (the C# `_context.IsAvailable`); routing
/// is delegated to the injected [`IAetherRouter`] (the C# aether-protocol engine).
/// Emergency-priority payloads trigger SOS flood mode. Start is a no-op; stop
/// completes the inbound buffer (as the C# `_inbound.Writer.TryComplete()`).
pub struct AetherNetworkTransport {
    availability: Arc<dyn AetherAvailability>,
    router: Arc<dyn IAetherRouter>,
    /// Unbounded inbound buffer — the analogue of the C#
    /// `Channel.CreateUnbounded<NetworkPayload>()`.
    inbound: Mutex<VecDeque<NetworkPayload>>,
    /// Once stopped, the inbound buffer is "completed": further `receive` is
    /// dropped and `drain` returns the remainder once.
    completed: AtomicBool,
}

/// Availability probe for the Aether runtime. Any [`crate::aether::IAetherContext`]
/// satisfies this via a blanket impl, so the C# `_context.IsAvailable` maps
/// straight through; a plain bool flag can also be injected in tests.
pub trait AetherAvailability: Send + Sync {
    fn is_available(&self) -> bool;
}

impl<T: crate::aether::IAetherContext> AetherAvailability for T {
    fn is_available(&self) -> bool {
        crate::aether::IAetherContext::is_available(self)
    }
}

/// A fixed-availability probe for hosts/tests that just want to assert a state.
pub struct FixedAetherAvailability(pub bool);

impl AetherAvailability for FixedAetherAvailability {
    fn is_available(&self) -> bool {
        self.0
    }
}

impl AetherNetworkTransport {
    /// Builds a transport over the given availability probe and mesh router.
    pub fn new(
        availability: Arc<dyn AetherAvailability>,
        router: Arc<dyn IAetherRouter>,
    ) -> Self {
        Self {
            availability,
            router,
            inbound: Mutex::new(VecDeque::new()),
            completed: AtomicBool::new(false),
        }
    }

    /// Injects `payload` as if it arrived from the mesh (buffered for [`drain`],
    /// matching the C# inbound channel). No-op once the transport is stopped
    /// (the writer is completed).
    pub fn receive(&self, payload: NetworkPayload) {
        if self.completed.load(Ordering::SeqCst) {
            return;
        }
        self.inbound.lock().unwrap().push_back(payload);
    }

    /// Drains every buffered inbound payload in arrival order. Pull side of the C#
    /// `ReceiveAsync` enumerable.
    pub fn drain(&self) -> Vec<NetworkPayload> {
        self.inbound.lock().unwrap().drain(..).collect()
    }
}

impl INetworkTransport for AetherNetworkTransport {
    fn kind(&self) -> TransportKind {
        TransportKind::Aether
    }

    fn is_available(&self) -> bool {
        self.availability.is_available()
    }

    fn start(&self) {
        // C# StartAsync is Task.CompletedTask; make it re-openable after a stop.
        self.completed.store(false, Ordering::SeqCst);
    }

    fn stop(&self) {
        // C# StopAsync completes the inbound writer.
        self.completed.store(true, Ordering::SeqCst);
    }

    fn send(&self, payload: &NetworkPayload) -> Result<(), TransportError> {
        if !self.is_available() {
            return Err(TransportError::NotAvailable(TransportKind::Aether));
        }
        // Emergency payloads trigger SOS flood mode (the C# `_ = payload.Priority`
        // routing decision, made concrete here).
        let emergency = payload.priority == crate::networking::MessagePriority::Emergency;
        self.router.route(payload, emergency);
        Ok(())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AetherPeerDiscovery — port of AetherPeerDiscovery.cs
// ─────────────────────────────────────────────────────────────────────────────

/// [`IPeerDiscovery`] using Aether presence beacons (Hello/HelloAck). Port of the
/// C# `AetherPeerDiscovery`. Discovery reads the shared
/// [`InMemoryAetherNetRegistry`]; each registered [`AetherPeer`] is projected to a
/// [`PeerInfo`]. Announcements are recorded on the registry-side announcement log.
pub struct AetherPeerDiscovery {
    registry: Arc<InMemoryAetherNetRegistry>,
    announced: Mutex<Vec<PeerInfo>>,
}

impl AetherPeerDiscovery {
    pub fn new(registry: Arc<InMemoryAetherNetRegistry>) -> Self {
        Self {
            registry,
            announced: Mutex::new(Vec::new()),
        }
    }

    /// Everything this node has announced, in order.
    pub fn announcements(&self) -> Vec<PeerInfo> {
        self.announced.lock().unwrap().clone()
    }

    /// Projects an [`AetherPeer`] to the transport-agnostic [`PeerInfo`] a mesh
    /// presence beacon would surface.
    fn to_peer_info(p: &AetherPeer) -> PeerInfo {
        PeerInfo::new(
            p.peer_id.clone(),
            p.friendly_name.clone(),
            vec![TransportKind::Aether],
            crate::networking::PeerRole::Peer,
            None,
            Utc::now(),
        )
    }
}

impl IPeerDiscovery for AetherPeerDiscovery {
    fn discover(&self) -> Vec<PeerInfo> {
        self.registry
            .peers()
            .iter()
            .map(Self::to_peer_info)
            .collect()
    }

    fn announce(&self, local_info: PeerInfo) {
        self.announced.lock().unwrap().push(local_info);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// AetherSyncChannel — port of AetherSyncChannel.cs
// ─────────────────────────────────────────────────────────────────────────────

/// Default DTN TTL: 72 hours, matching the aether-protocol DTN spec (the C#
/// comment "TTL = 72 hours by default").
pub const AETHER_DTN_DEFAULT_TTL: Duration = Duration::from_secs(72 * 3600);

/// [`ISyncChannel`] backed by Aether DTN store-and-forward. Port of the C#
/// `AetherSyncChannel`.
///
/// Deltas are handed to the DTN engine (injected [`IAetherRouter`]) as a
/// custody-transfer bundle; per-(owner,domain) monotonic sequence tracking
/// mirrors the C# `_sequences` dictionary. `receive_deltas` drains the local
/// delivery queue fed by the DTN engine.
pub struct AetherSyncChannel {
    router: Arc<dyn IAetherRouter>,
    sequences: Mutex<HashMap<(String, String), i64>>,
    /// Delivery queue per owner, fed by [`AetherSyncChannel::deliver`] (the DTN
    /// delivery queue filtered by `ownerId`).
    delivered: Mutex<HashMap<String, VecDeque<SyncDelta>>>,
}

impl AetherSyncChannel {
    pub fn new(router: Arc<dyn IAetherRouter>) -> Self {
        Self {
            router,
            sequences: Mutex::new(HashMap::new()),
            delivered: Mutex::new(HashMap::new()),
        }
    }

    /// Simulates a DTN bundle arriving for `owner_id` (the "subscribe to Aether DTN
    /// delivery queue filtered by ownerId" path), enqueuing it for the next
    /// [`ISyncChannel::receive_deltas`].
    pub fn deliver(&self, owner_id: &str, delta: SyncDelta) {
        self.delivered
            .lock()
            .unwrap()
            .entry(owner_id.to_string())
            .or_default()
            .push_back(delta);
    }
}

impl ISyncChannel for AetherSyncChannel {
    fn push_delta(&self, delta: &SyncDelta) {
        // Advance the per-(owner,domain) high-water sequence (monotonic).
        {
            let mut seqs = self.sequences.lock().unwrap();
            let key = (delta.owner_id.clone(), delta.domain_key.clone());
            let entry = seqs.entry(key).or_insert(0);
            if delta.sequence > *entry {
                *entry = delta.sequence;
            }
        }
        // Serialise the delta into a custody-transfer DTN bundle and hand to the
        // engine: TTL default 72h, custody required. Priority is Normal (DTN is a
        // background sync path).
        let ttl = delta.ttl.unwrap_or(AETHER_DTN_DEFAULT_TTL);
        let payload = NetworkPayload::create(
            delta.payload.clone(),
            Some(delta.target_device_id.clone()),
            crate::networking::MessagePriority::Normal,
            "application/dtn-bundle",
            Some(ttl),
        );
        self.router.route(&payload, false);
    }

    fn receive_deltas(&self, owner_id: &str) -> Vec<SyncDelta> {
        let mut delivered = self.delivered.lock().unwrap();
        match delivered.get_mut(owner_id) {
            Some(q) => q.drain(..).collect(),
            None => Vec::new(),
        }
    }

    fn get_last_sequence(&self, owner_id: &str, domain_key: &str) -> i64 {
        self.sequences
            .lock()
            .unwrap()
            .get(&(owner_id.to_string(), domain_key.to_string()))
            .copied()
            .unwrap_or(0)
    }
}
