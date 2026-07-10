//! networking::discovery — Rust port of `IPeerDiscovery.cs`, `IPayloadOptimiser.cs`,
//! and `ISyncChannel.cs` (the networking variant), each with a working in-memory
//! implementation.
//!
//!   * [`IPeerDiscovery`]        — finds nearby peers; announces ours.
//!   * [`InMemoryPeerDiscovery`] — a mutable registry of visible peers.
//!   * [`IPayloadOptimiser`]     — compress/transform payloads for low-bandwidth
//!     transports (BLE / NearLink / LoRa / DTN).
//!   * [`RlePayloadOptimiser`]   — a deterministic, fully-reversible optimiser.
//!   * [`ISyncChannel`]          — the cross-device continuity primitive.
//!   * [`InMemorySyncChannel`]   — an in-process delta log with per-owner+domain
//!     monotonic sequence tracking.

use std::collections::{BTreeMap, HashMap, VecDeque};
use std::sync::{Arc, Mutex};

use super::types::{NetworkPayload, PeerInfo, SyncDelta, TransportKind};

// ─────────────────────────────────────────────────────────────────────────────
// IPeerDiscovery
// ─────────────────────────────────────────────────────────────────────────────

/// Handler fired for each discovered peer.
pub type PeerHandler = Arc<dyn Fn(&PeerInfo) + Send + Sync>;

/// Finds nearby devices via mDNS, BLE beacons, NearLink scan, Aether presence,
/// etc. The C# `DiscoverAsync` async-stream becomes `discover` (pull) + `watch`
/// (push) here; `announce` records the local advertisement.
pub trait IPeerDiscovery: Send + Sync {
    /// A snapshot of the peers currently visible.
    fn discover(&self) -> Vec<PeerInfo>;

    /// Publishes `local_info` as this device's advertisement.
    fn announce(&self, local_info: PeerInfo);
}

struct DiscoveryCore {
    peers: Mutex<Vec<PeerInfo>>,
    announced: Mutex<Vec<PeerInfo>>,
    watchers: Mutex<Vec<(u64, PeerHandler)>>,
    next_id: Mutex<u64>,
}

/// In-process [`IPeerDiscovery`]. Peers are added via
/// [`InMemoryPeerDiscovery::add_peer`] (as if a scan found them), which fans out
/// to watchers; `announce` records what this node advertised.
pub struct InMemoryPeerDiscovery {
    core: Arc<DiscoveryCore>,
}

impl Default for InMemoryPeerDiscovery {
    fn default() -> Self {
        Self::new()
    }
}

/// Watch handle; drop to stop watching.
pub struct DiscoverySubscription {
    core: Arc<DiscoveryCore>,
    id: u64,
}

impl Drop for DiscoverySubscription {
    fn drop(&mut self) {
        self.core
            .watchers
            .lock()
            .unwrap()
            .retain(|(wid, _)| *wid != self.id);
    }
}

impl InMemoryPeerDiscovery {
    pub fn new() -> Self {
        Self {
            core: Arc::new(DiscoveryCore {
                peers: Mutex::new(Vec::new()),
                announced: Mutex::new(Vec::new()),
                watchers: Mutex::new(Vec::new()),
                next_id: Mutex::new(0),
            }),
        }
    }

    /// Simulates a scan discovering `peer`: records it (replacing any existing
    /// entry with the same `node_id`, keeping the freshest) and fans it out to
    /// watchers outside the lock.
    pub fn add_peer(&self, peer: PeerInfo) {
        {
            let mut peers = self.core.peers.lock().unwrap();
            peers.retain(|p| p.node_id != peer.node_id);
            peers.push(peer.clone());
        }
        let snapshot: Vec<PeerHandler> = {
            let guard = self.core.watchers.lock().unwrap();
            guard.iter().map(|(_, h)| Arc::clone(h)).collect()
        };
        for h in snapshot {
            h(&peer);
        }
    }

    /// Removes a discovered peer by id; returns whether it was present.
    pub fn remove_peer(&self, node_id: &str) -> bool {
        let mut peers = self.core.peers.lock().unwrap();
        let before = peers.len();
        peers.retain(|p| p.node_id != node_id);
        peers.len() != before
    }

    /// Registers `handler` for future discoveries. Subscribe synchronously before
    /// scanning; handle detaches on drop.
    pub fn watch(&self, handler: PeerHandler) -> DiscoverySubscription {
        let id = {
            let mut n = self.core.next_id.lock().unwrap();
            let id = *n;
            *n += 1;
            id
        };
        self.core.watchers.lock().unwrap().push((id, handler));
        DiscoverySubscription {
            core: Arc::clone(&self.core),
            id,
        }
    }

    /// Everything this node has announced, in order.
    pub fn announcements(&self) -> Vec<PeerInfo> {
        self.core.announced.lock().unwrap().clone()
    }
}

impl IPeerDiscovery for InMemoryPeerDiscovery {
    fn discover(&self) -> Vec<PeerInfo> {
        self.core.peers.lock().unwrap().clone()
    }

    fn announce(&self, local_info: PeerInfo) {
        self.core.announced.lock().unwrap().push(local_info);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IPayloadOptimiser
// ─────────────────────────────────────────────────────────────────────────────

/// Compresses or transforms payloads for low-bandwidth transports (BLE, NearLink,
/// LoRa, DTN). Port of the C# `IPayloadOptimiser` (its `ValueTask` async becomes a
/// sync call).
pub trait IPayloadOptimiser: Send + Sync {
    /// Returns an optimised copy of `payload` for `target_transport`. For
    /// high-bandwidth transports this may be the payload unchanged.
    fn optimise(&self, payload: &NetworkPayload, target_transport: TransportKind) -> NetworkPayload;

    /// Reverses [`IPayloadOptimiser::optimise`], returning the original payload.
    fn decompress(&self, payload: &NetworkPayload) -> NetworkPayload;
}

/// Metadata key marking a payload as RLE-compressed and recording the original
/// content type so [`RlePayloadOptimiser::decompress`] restores it exactly.
const RLE_ORIGINAL_CONTENT_TYPE: &str = "circleai.rle.original_content_type";
/// Content type stamped on a compressed payload.
const RLE_CONTENT_TYPE: &str = "application/x-circleai-rle";

/// A deterministic, fully-reversible payload optimiser using byte run-length
/// encoding. Only the low-bandwidth transports (BLE / NearLink / DTN) trigger
/// compression; everything else passes through unchanged. `decompress` is exact
/// for any input `optimise` produced, and a no-op for uncompressed payloads.
///
/// Wire format (per run): `[count:u8][byte:u8]`, count in `1..=255`. This trades
/// a worst-case 2× expansion on incompressible data for guaranteed reversibility
/// — appropriate for a deterministic in-memory reference; a real optimiser would
/// pick zstd/brotli, but the *contract* (reversible transform gated on transport)
/// is identical.
#[derive(Debug, Clone, Copy, Default)]
pub struct RlePayloadOptimiser;

impl RlePayloadOptimiser {
    pub fn new() -> Self {
        RlePayloadOptimiser
    }

    /// Transports for which compression is worthwhile.
    fn is_low_bandwidth(t: TransportKind) -> bool {
        matches!(
            t,
            TransportKind::Bluetooth | TransportKind::NearLink | TransportKind::Dtn
        )
    }

    /// RLE-encode `data`.
    fn rle_encode(data: &[u8]) -> Vec<u8> {
        let mut out = Vec::with_capacity(data.len());
        let mut i = 0;
        while i < data.len() {
            let b = data[i];
            let mut run = 1usize;
            while i + run < data.len() && data[i + run] == b && run < 255 {
                run += 1;
            }
            out.push(run as u8);
            out.push(b);
            i += run;
        }
        out
    }

    /// RLE-decode `data`. Malformed (odd-length) input decodes what it can.
    fn rle_decode(data: &[u8]) -> Vec<u8> {
        let mut out = Vec::new();
        let mut i = 0;
        while i + 1 < data.len() {
            let count = data[i] as usize;
            let byte = data[i + 1];
            out.extend(std::iter::repeat(byte).take(count));
            i += 2;
        }
        out
    }
}

impl IPayloadOptimiser for RlePayloadOptimiser {
    fn optimise(&self, payload: &NetworkPayload, target_transport: TransportKind) -> NetworkPayload {
        // Already compressed, or a high-bandwidth transport → pass through.
        if !Self::is_low_bandwidth(target_transport)
            || payload.metadata.contains_key(RLE_ORIGINAL_CONTENT_TYPE)
        {
            return payload.clone();
        }
        let encoded = Self::rle_encode(&payload.data);
        let mut metadata: BTreeMap<String, String> = payload.metadata.clone();
        metadata.insert(
            RLE_ORIGINAL_CONTENT_TYPE.to_string(),
            payload.content_type.clone(),
        );
        NetworkPayload {
            id: payload.id.clone(),
            source_id: payload.source_id.clone(),
            destination_id: payload.destination_id.clone(),
            data: encoded,
            priority: payload.priority,
            ttl: payload.ttl,
            content_type: RLE_CONTENT_TYPE.to_string(),
            metadata,
            created_at: payload.created_at,
        }
    }

    fn decompress(&self, payload: &NetworkPayload) -> NetworkPayload {
        // Not one of ours → return unchanged.
        let Some(original_ct) = payload.metadata.get(RLE_ORIGINAL_CONTENT_TYPE) else {
            return payload.clone();
        };
        let decoded = Self::rle_decode(&payload.data);
        let mut metadata: BTreeMap<String, String> = payload.metadata.clone();
        let restored_ct = original_ct.clone();
        metadata.remove(RLE_ORIGINAL_CONTENT_TYPE);
        NetworkPayload {
            id: payload.id.clone(),
            source_id: payload.source_id.clone(),
            destination_id: payload.destination_id.clone(),
            data: decoded,
            priority: payload.priority,
            ttl: payload.ttl,
            content_type: restored_ct,
            metadata,
            created_at: payload.created_at,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ISyncChannel  (networking variant)
// ─────────────────────────────────────────────────────────────────────────────

/// The cross-device continuity primitive. Pushes memory/state deltas across
/// whatever transport is available (gRPC over 5G, BLE mesh via a neighbour, a DTN
/// bundle arriving 6 hours later) — app code is identical in every case. This is
/// the networking-namespace `ISyncChannel` that carries the richer
/// [`SyncDelta`] (with scheduling hints).
pub trait ISyncChannel: Send + Sync {
    /// Push a delta. The channel selects the transport and handles retries.
    /// Returns when accepted (not necessarily delivered, for DTN/LocalStore).
    fn push_delta(&self, delta: &SyncDelta);

    /// Drain the deltas currently pending for `owner_id`.
    fn receive_deltas(&self, owner_id: &str) -> Vec<SyncDelta>;

    /// The highest sequence seen for `owner_id`+`domain_key`, or `0` if none.
    fn get_last_sequence(&self, owner_id: &str, domain_key: &str) -> i64;
}

struct SyncCore {
    /// Per-owner delivery queue.
    queues: Mutex<HashMap<String, VecDeque<SyncDelta>>>,
    /// Highest sequence seen per (owner, domain).
    last_seq: Mutex<HashMap<(String, String), i64>>,
    /// Complete accepted-delta log, in push order (for inspection/tests).
    log: Mutex<Vec<SyncDelta>>,
}

/// In-process [`ISyncChannel`]. `push_delta` appends to the owner's queue and
/// advances the per-(owner,domain) high-water sequence; `receive_deltas` drains
/// that queue. Deterministic and lock-safe (snapshots are taken under the lock,
/// nothing external is invoked while held).
pub struct InMemorySyncChannel {
    core: Arc<SyncCore>,
}

impl Default for InMemorySyncChannel {
    fn default() -> Self {
        Self::new()
    }
}

impl InMemorySyncChannel {
    pub fn new() -> Self {
        Self {
            core: Arc::new(SyncCore {
                queues: Mutex::new(HashMap::new()),
                last_seq: Mutex::new(HashMap::new()),
                log: Mutex::new(Vec::new()),
            }),
        }
    }

    /// The full push log in order (does not consume the delivery queues).
    pub fn log(&self) -> Vec<SyncDelta> {
        self.core.log.lock().unwrap().clone()
    }

    /// Count of deltas still pending delivery for `owner_id`.
    pub fn pending(&self, owner_id: &str) -> usize {
        self.core
            .queues
            .lock()
            .unwrap()
            .get(owner_id)
            .map(|q| q.len())
            .unwrap_or(0)
    }
}

impl ISyncChannel for InMemorySyncChannel {
    fn push_delta(&self, delta: &SyncDelta) {
        // Advance the high-water sequence for this owner+domain (monotonic: never
        // moves backward).
        {
            let mut seqs = self.core.last_seq.lock().unwrap();
            let key = (delta.owner_id.clone(), delta.domain_key.clone());
            let entry = seqs.entry(key).or_insert(0);
            if delta.sequence > *entry {
                *entry = delta.sequence;
            }
        }
        self.core.log.lock().unwrap().push(delta.clone());
        self.core
            .queues
            .lock()
            .unwrap()
            .entry(delta.owner_id.clone())
            .or_default()
            .push_back(delta.clone());
    }

    fn receive_deltas(&self, owner_id: &str) -> Vec<SyncDelta> {
        let mut queues = self.core.queues.lock().unwrap();
        match queues.get_mut(owner_id) {
            Some(q) => q.drain(..).collect(),
            None => Vec::new(),
        }
    }

    fn get_last_sequence(&self, owner_id: &str, domain_key: &str) -> i64 {
        self.core
            .last_seq
            .lock()
            .unwrap()
            .get(&(owner_id.to_string(), domain_key.to_string()))
            .copied()
            .unwrap_or(0)
    }
}
