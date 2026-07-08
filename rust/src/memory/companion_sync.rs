//! companion_sync.rs
//!
//! Port of `CircleAI.Memory/Sync/` — the companion-state cross-device sync layer.
//!
//! Contains, 1:1 with the C# reference:
//!   * [`HybridLogicalClock`] — monotonic, globally-unique 64-bit version stamps
//!     (48-bit physical ms | 10-bit logical | 6-bit node short id).
//!   * [`SyncableEntry`] — the wire unit; opaque JSON payload + SHA-256 content
//!     hash tiebreaker.
//!   * [`SyncEnvelope`] / [`SyncEnvelopeKind`] / [`StateVectorEntry`] /
//!     [`RequestItem`] — the Announce → Request → Push convergence protocol.
//!   * [`ISyncableEntryStore`] + [`InMemorySyncableEntryStore`] — the local view
//!     the engine reads / writes (higher-version-wins, tombstone + content-hash
//!     tiebreak).
//!   * [`ICompanionStateChannel`] + [`InProcessSyncHub`] +
//!     [`InProcessCompanionStateChannel`] — the transport seam and a loopback
//!     implementation for tests / same-device simulation.
//!   * [`ICompanionStateSyncEngine`] + [`CompanionStateSyncEngine`] — the
//!     orchestration loop (WriteLocal, SyncNow, inbound envelope handling).
//!   * [`PersonaStateSyncBridge`], [`LoraAdapterSyncBridge`],
//!     [`CompanionConversationSyncBridge`] — concrete type bridges onto the wire.
//!
//! The C# API is async (`Task`); the traits here are sync (the existing Rust
//! sync surface is sync). Content-hashing reuses the self-contained SHA-256 in
//! [`crate::memory::multimodal::compute_sha256`], which is byte-identical to
//! `System.Security.Cryptography.SHA256`.

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};

use super::multimodal::compute_sha256;
use super::stores::{IPersonaStore, PersonaState};

// ─────────────────────────────────────────────────────────────────────────────
// HybridLogicalClock
// ─────────────────────────────────────────────────────────────────────────────

/// Source of physical time in milliseconds (Unix epoch).
pub type PhysicalNowFn = Arc<dyn Fn() -> i64 + Send + Sync>;

/// Hybrid Logical Clock — produces monotonic, globally-unique version stamps for
/// syncable entries. Thread-safe. 1:1 with the C# `HybridLogicalClock`.
///
/// Version layout (64 bits):
///   * high 48 bits — physical time in milliseconds (Unix epoch)
///   * mid  10 bits — logical counter (resets when physical advances)
///   * low   6 bits — node short id (0..63)
pub struct HybridLogicalClock {
    physical_now_ms: PhysicalNowFn,
    node_short_id: i64,
    inner: Mutex<HlcInner>,
}

struct HlcInner {
    last_physical: i64,
    logical: i64,
}

impl HybridLogicalClock {
    /// Creates a clock with the system wall-clock as the physical time source.
    ///
    /// `node_short_id` must be in `0..=63` — it packs into the low 6 bits of
    /// every version. Each device a user has should pick a stable distinct value.
    ///
    /// # Panics
    /// Panics when `node_short_id` is outside `0..=63` (mirrors the C#
    /// `ArgumentOutOfRangeException`).
    pub fn new(node_short_id: i64) -> Self {
        Self::with_clock(node_short_id, Arc::new(default_now))
    }

    /// Creates a clock with an explicit physical-time source (for deterministic
    /// tests).
    ///
    /// # Panics
    /// Panics when `node_short_id` is outside `0..=63`.
    pub fn with_clock(node_short_id: i64, physical_now_ms: PhysicalNowFn) -> Self {
        assert!(
            (0..=63).contains(&node_short_id),
            "nodeShortId must be in 0..63"
        );
        let last_physical = physical_now_ms();
        Self {
            physical_now_ms,
            node_short_id,
            inner: Mutex::new(HlcInner {
                last_physical,
                logical: 0,
            }),
        }
    }

    /// Produces the next outgoing version (for a write we originated).
    pub fn tick(&self) -> i64 {
        let mut inner = self.inner.lock().unwrap();
        let now = (self.physical_now_ms)();
        if now > inner.last_physical {
            inner.last_physical = now;
            inner.logical = 0;
        } else {
            inner.logical += 1;
            if inner.logical >= 1024 {
                // Logical counter overflowed within the same ms — bump physical.
                inner.last_physical += 1;
                inner.logical = 0;
            }
        }
        Self::compose(inner.last_physical, inner.logical, self.node_short_id)
    }

    /// Updates the clock from a received version (must be called on every inbound
    /// apply so subsequent local ticks remain monotonic w.r.t. peers).
    pub fn observe(&self, incoming: i64) -> i64 {
        let mut inner = self.inner.lock().unwrap();
        let (incoming_physical, _, _) = Self::decompose(incoming);
        let now = (self.physical_now_ms)();
        let max_physical = inner.last_physical.max(incoming_physical).max(now);

        if max_physical == inner.last_physical && max_physical == incoming_physical {
            inner.logical += 1;
        } else if max_physical == inner.last_physical {
            inner.logical += 1;
        } else if max_physical == incoming_physical {
            inner.logical = Self::decompose(incoming).1 + 1;
        } else {
            inner.logical = 0;
        }

        inner.last_physical = max_physical;
        Self::compose(inner.last_physical, inner.logical, self.node_short_id)
    }

    /// Composes the three components into a 64-bit version.
    pub fn compose(physical_ms: i64, logical: i64, node_short_id: i64) -> i64 {
        (physical_ms << 16) | ((logical & 0x3FF) << 6) | (node_short_id & 0x3F)
    }

    /// Decomposes a version into `(physical_ms, logical, node_short_id)`.
    pub fn decompose(version: i64) -> (i64, i64, i64) {
        (version >> 16, (version >> 6) & 0x3FF, version & 0x3F)
    }
}

fn default_now() -> i64 {
    Utc::now().timestamp_millis()
}

// ─────────────────────────────────────────────────────────────────────────────
// SyncableEntry
// ─────────────────────────────────────────────────────────────────────────────

/// A single syncable item — the smallest unit the engine moves between peers.
///
/// `content_hash` is the SHA-256 hex of `payload`, used as the deterministic
/// tiebreaker when two peers write the same `version`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SyncableEntry {
    /// Logical type — e.g. "PersonaState", "CoreMemory", "DailyMemorySummary".
    pub entity_type: String,
    /// Identifier within the type — e.g. a user id, a GUID-N format string.
    pub entity_id: String,
    /// HLC-produced monotonic version stamp.
    pub version: i64,
    /// True when this entry represents a deletion. Payload is empty in that case.
    pub is_tombstone: bool,
    /// SHA-256 hex of `payload` — content tiebreaker when versions collide.
    pub content_hash: String,
    /// Opaque payload — type-specific JSON or any string the adapter chose.
    pub payload: String,
    /// Identifier of the node that authored this version (provenance).
    pub source_node_id: String,
    /// UTC wall-clock when authored — for human-facing display, not ordering.
    pub authored_at: DateTime<Utc>,
}

impl SyncableEntry {
    /// Constructs a syncable entry with all fields explicit.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        entity_type: impl Into<String>,
        entity_id: impl Into<String>,
        version: i64,
        is_tombstone: bool,
        content_hash: impl Into<String>,
        payload: impl Into<String>,
        source_node_id: impl Into<String>,
        authored_at: DateTime<Utc>,
    ) -> Self {
        Self {
            entity_type: entity_type.into(),
            entity_id: entity_id.into(),
            version,
            is_tombstone,
            content_hash: content_hash.into(),
            payload: payload.into(),
            source_node_id: source_node_id.into(),
            authored_at,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SyncEnvelope + protocol payloads
// ─────────────────────────────────────────────────────────────────────────────

/// Kind of sync envelope.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum SyncEnvelopeKind {
    /// Broadcast of the sender's per-entity-type high-watermark versions.
    Announce,
    /// Reply to an Announce asking for entries newer than a known version.
    Request,
    /// Unsolicited or replied delivery of syncable entries.
    Push,
}

/// Per-entity-type high-watermark — used in Announce/Request payloads.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct StateVectorEntry {
    pub entity_type: String,
    pub max_known_version: i64,
}

impl StateVectorEntry {
    pub fn new(entity_type: impl Into<String>, max_known_version: i64) -> Self {
        Self {
            entity_type: entity_type.into(),
            max_known_version,
        }
    }
}

/// Reply-side request item — "send me entries of `entity_type` strictly newer
/// than `since_version`".
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct RequestItem {
    pub entity_type: String,
    pub since_version: i64,
}

impl RequestItem {
    pub fn new(entity_type: impl Into<String>, since_version: i64) -> Self {
        Self {
            entity_type: entity_type.into(),
            since_version,
        }
    }
}

/// A sync envelope — the message unit that crosses the channel.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct SyncEnvelope {
    pub kind: SyncEnvelopeKind,
    pub from_node_id: String,
    pub state_vector: Option<Vec<StateVectorEntry>>,
    pub requests: Option<Vec<RequestItem>>,
    pub entries: Option<Vec<SyncableEntry>>,
}

impl SyncEnvelope {
    /// Constructs an envelope from all four fields (mirrors the C# record ctor).
    pub fn new(
        kind: SyncEnvelopeKind,
        from_node_id: impl Into<String>,
        state_vector: Option<Vec<StateVectorEntry>>,
        requests: Option<Vec<RequestItem>>,
        entries: Option<Vec<SyncableEntry>>,
    ) -> Self {
        Self {
            kind,
            from_node_id: from_node_id.into(),
            state_vector,
            requests,
            entries,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ISyncableEntryStore + InMemorySyncableEntryStore
// ─────────────────────────────────────────────────────────────────────────────

/// The seat the sync engine reads from and writes to.
///
/// Apply rules — implementations MUST enforce these for convergence:
///   * Higher `version` wins.
///   * On tie (same version), higher `content_hash` (string compare) wins.
///   * Tombstones replace any non-tombstone of equal-or-lower version.
pub trait ISyncableEntryStore: Send + Sync {
    /// Applies an incoming entry. Returns `true` when local state was actually
    /// updated (incoming was strictly newer / preferred), `false` when the local
    /// entry was already at or beyond the incoming version.
    fn apply(&self, entry: &SyncableEntry) -> bool;

    /// Returns the current entry for the given `(entity_type, entity_id)`, or
    /// `None` when not known locally. Tombstones ARE returned.
    fn get(&self, entity_type: &str, entity_id: &str) -> Option<SyncableEntry>;

    /// Returns every entry of the given type whose `version` is strictly greater
    /// than `since_version`, ordered ascending by version.
    fn get_since(&self, entity_type: &str, since_version: i64) -> Vec<SyncableEntry>;

    /// Returns the highest known `version` per entity type — the local node's
    /// state vector. Types with no entries are omitted, ordered by `entity_type`.
    fn get_state_vector(&self) -> Vec<StateVectorEntry>;
}

/// In-memory [`ISyncableEntryStore`]. 1:1 with the C# `InMemorySyncableEntryStore`.
#[derive(Default)]
pub struct InMemorySyncableEntryStore {
    inner: Mutex<StoreInner>,
}

#[derive(Default)]
struct StoreInner {
    // Keyed by (type, id).
    entries: HashMap<(String, String), SyncableEntry>,
    max_version_by_type: HashMap<String, i64>,
}

impl InMemorySyncableEntryStore {
    pub fn new() -> Self {
        Self::default()
    }

    /// Apply rule: higher version wins; on tie, tombstone-of-non-tombstone wins,
    /// then higher content hash (ordinal string compare) wins.
    fn should_apply(existing: &SyncableEntry, incoming: &SyncableEntry) -> bool {
        if incoming.version > existing.version {
            return true;
        }
        if incoming.version < existing.version {
            return false;
        }
        // Equal versions — tombstone-of-non-tombstone wins.
        if incoming.is_tombstone && !existing.is_tombstone {
            return true;
        }
        if !incoming.is_tombstone && existing.is_tombstone {
            return false;
        }
        // Same tombstone state, same version — content hash tiebreaker.
        incoming.content_hash > existing.content_hash
    }
}

impl ISyncableEntryStore for InMemorySyncableEntryStore {
    fn apply(&self, entry: &SyncableEntry) -> bool {
        let key = (entry.entity_type.clone(), entry.entity_id.clone());
        let mut inner = self.inner.lock().unwrap();

        let applied = match inner.entries.get(&key) {
            None => {
                inner.entries.insert(key, entry.clone());
                true
            }
            Some(existing) => {
                if Self::should_apply(existing, entry) {
                    inner.entries.insert(key, entry.clone());
                    true
                } else {
                    false
                }
            }
        };

        if applied {
            let current = inner
                .max_version_by_type
                .get(&entry.entity_type)
                .copied()
                .unwrap_or(0);
            if entry.version > current {
                inner
                    .max_version_by_type
                    .insert(entry.entity_type.clone(), entry.version);
            }
        }
        applied
    }

    fn get(&self, entity_type: &str, entity_id: &str) -> Option<SyncableEntry> {
        let inner = self.inner.lock().unwrap();
        inner
            .entries
            .get(&(entity_type.to_string(), entity_id.to_string()))
            .cloned()
    }

    fn get_since(&self, entity_type: &str, since_version: i64) -> Vec<SyncableEntry> {
        let inner = self.inner.lock().unwrap();
        let mut result: Vec<SyncableEntry> = inner
            .entries
            .values()
            .filter(|e| e.entity_type == entity_type && e.version > since_version)
            .cloned()
            .collect();
        result.sort_by(|a, b| a.version.cmp(&b.version));
        result
    }

    fn get_state_vector(&self) -> Vec<StateVectorEntry> {
        let inner = self.inner.lock().unwrap();
        let mut vector: Vec<StateVectorEntry> = inner
            .max_version_by_type
            .iter()
            .map(|(k, v)| StateVectorEntry::new(k.clone(), *v))
            .collect();
        vector.sort_by(|a, b| a.entity_type.cmp(&b.entity_type));
        vector
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ICompanionStateChannel + InProcessSyncHub + InProcessCompanionStateChannel
// ─────────────────────────────────────────────────────────────────────────────

/// Inbound-envelope handler. The `&str` is the receiving channel's local node id.
pub type EnvelopeHandler = Arc<dyn Fn(&SyncEnvelope) + Send + Sync>;

/// Unsubscribe guard — dropping it removes the handler (the Rust analog of the
/// C# `IDisposable` returned by `Subscribe`).
pub struct Subscription {
    channel: Arc<InProcessChannelCore>,
    id: u64,
}

impl Drop for Subscription {
    fn drop(&mut self) {
        let mut handlers = self.channel.handlers.lock().unwrap();
        handlers.retain(|(hid, _)| *hid != self.id);
    }
}

/// Transport that moves [`SyncEnvelope`] messages between peers.
pub trait ICompanionStateChannel: Send + Sync {
    /// Stable identifier of THIS node on this channel. Stamped onto every
    /// envelope as [`SyncEnvelope::from_node_id`].
    fn local_node_id(&self) -> &str;

    /// Sends an envelope to peers. For v0.1 every channel implements broadcast
    /// semantics.
    fn send(&self, envelope: &SyncEnvelope);

    /// Subscribe to inbound envelopes. The returned [`Subscription`] unsubscribes
    /// on drop.
    fn subscribe(&self, handler: EnvelopeHandler) -> Subscription;
}

/// Routes envelopes between every [`InProcessCompanionStateChannel`] that has
/// joined the hub. One hub per simulated "mesh". 1:1 with the C#
/// `InProcessSyncHub`.
#[derive(Default, Clone)]
pub struct InProcessSyncHub {
    channels: Arc<Mutex<HashMap<String, Arc<InProcessChannelCore>>>>,
}

impl InProcessSyncHub {
    pub fn new() -> Self {
        Self::default()
    }

    fn join(&self, channel: Arc<InProcessChannelCore>) {
        self.channels
            .lock()
            .unwrap()
            .insert(channel.local_node_id.clone(), channel);
    }

    fn leave(&self, node_id: &str) {
        self.channels.lock().unwrap().remove(node_id);
    }

    fn broadcast(&self, envelope: &SyncEnvelope, sender_node_id: &str) {
        // Snapshot peers (excluding sender) so a handler that itself sends does
        // not deadlock on the hub lock.
        let peers: Vec<Arc<InProcessChannelCore>> = {
            let channels = self.channels.lock().unwrap();
            channels
                .values()
                .filter(|c| c.local_node_id != sender_node_id)
                .cloned()
                .collect()
        };
        for peer in peers {
            peer.deliver(envelope);
        }
    }

    /// Channels currently on this hub.
    pub fn connected_node_ids(&self) -> Vec<String> {
        self.channels.lock().unwrap().keys().cloned().collect()
    }
}

/// Shared inner state of an in-process channel (the piece [`Subscription`] and
/// the hub hold onto).
pub struct InProcessChannelCore {
    hub: InProcessSyncHub,
    local_node_id: String,
    handlers: Mutex<Vec<(u64, EnvelopeHandler)>>,
    next_id: AtomicU64,
    disposed: AtomicBool,
}

impl InProcessChannelCore {
    fn deliver(&self, envelope: &SyncEnvelope) {
        let snapshot: Vec<EnvelopeHandler> = {
            let handlers = self.handlers.lock().unwrap();
            handlers.iter().map(|(_, h)| Arc::clone(h)).collect()
        };
        for h in snapshot {
            h(envelope);
        }
    }
}

/// In-process [`ICompanionStateChannel`]. Broadcasts via an [`InProcessSyncHub`].
/// 1:1 with the C# `InProcessCompanionStateChannel`.
///
/// Dropping the channel (or calling [`dispose`](Self::dispose)) leaves the hub
/// and clears handlers.
pub struct InProcessCompanionStateChannel {
    core: Arc<InProcessChannelCore>,
}

impl InProcessCompanionStateChannel {
    /// Joins `hub` under `local_node_id`.
    ///
    /// # Panics
    /// Panics when `local_node_id` is blank (mirrors the C# `ArgumentException`).
    pub fn new(hub: &InProcessSyncHub, local_node_id: impl Into<String>) -> Self {
        let local_node_id = local_node_id.into();
        assert!(
            !local_node_id.trim().is_empty(),
            "localNodeId required"
        );
        let core = Arc::new(InProcessChannelCore {
            hub: hub.clone(),
            local_node_id,
            handlers: Mutex::new(Vec::new()),
            next_id: AtomicU64::new(0),
            disposed: AtomicBool::new(false),
        });
        hub.join(Arc::clone(&core));
        Self { core }
    }

    /// Unregisters from the hub and clears handlers. Idempotent.
    pub fn dispose(&self) {
        if self.core.disposed.swap(true, Ordering::SeqCst) {
            return;
        }
        self.core.hub.leave(&self.core.local_node_id);
        self.core.handlers.lock().unwrap().clear();
    }
}

impl Drop for InProcessCompanionStateChannel {
    fn drop(&mut self) {
        self.dispose();
    }
}

impl ICompanionStateChannel for InProcessCompanionStateChannel {
    fn local_node_id(&self) -> &str {
        &self.core.local_node_id
    }

    fn send(&self, envelope: &SyncEnvelope) {
        assert!(
            !self.core.disposed.load(Ordering::SeqCst),
            "channel disposed"
        );
        self.core.hub.broadcast(envelope, &self.core.local_node_id);
    }

    fn subscribe(&self, handler: EnvelopeHandler) -> Subscription {
        assert!(
            !self.core.disposed.load(Ordering::SeqCst),
            "channel disposed"
        );
        let id = self.core.next_id.fetch_add(1, Ordering::SeqCst);
        self.core.handlers.lock().unwrap().push((id, handler));
        Subscription {
            channel: Arc::clone(&self.core),
            id,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// ICompanionStateSyncEngine + CompanionStateSyncEngine
// ─────────────────────────────────────────────────────────────────────────────

/// Source of UTC wall-clock time for `authored_at` stamping.
pub type WallClockFn = Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>;

/// Engine that broadcasts local state vectors, fulfils peer Requests, and applies
/// inbound Push entries.
pub trait ICompanionStateSyncEngine: Send + Sync {
    /// Subscribes the engine to channel envelopes.
    fn start(&self);

    /// Broadcasts the local state vector to all peers immediately.
    fn sync_now(&self);

    /// Applies a locally-authored entry: stamps it with a fresh HLC version,
    /// persists it to the local store, and (if started) broadcasts it via Push.
    /// Returns the resulting entry with its assigned version.
    ///
    /// # Panics
    /// Panics when `entity_type` or `entity_id` is blank (mirrors the C#
    /// `ArgumentException`).
    fn write_local(
        &self,
        entity_type: &str,
        entity_id: &str,
        payload: &str,
        is_tombstone: bool,
    ) -> SyncableEntry;
}

/// The shared core the engine's inbound handler captures. Holds the channel,
/// store, clock, and wall-clock — cloneable via `Arc` so the subscription
/// closure can call back in (the Rust analog of C# capturing `this`).
struct EngineCore {
    channel: Arc<dyn ICompanionStateChannel>,
    store: Arc<dyn ISyncableEntryStore>,
    clock: Arc<HybridLogicalClock>,
    wall_clock: WallClockFn,
}

impl EngineCore {
    fn sync_now(&self) {
        let vector = self.store.get_state_vector();
        self.channel.send(&SyncEnvelope::new(
            SyncEnvelopeKind::Announce,
            self.channel.local_node_id(),
            Some(vector),
            None,
            None,
        ));
    }

    fn handle_envelope(&self, envelope: &SyncEnvelope) {
        match envelope.kind {
            SyncEnvelopeKind::Announce => self.handle_announce(envelope),
            SyncEnvelopeKind::Request => self.handle_request(envelope),
            SyncEnvelopeKind::Push => self.handle_push(envelope),
        }
    }

    fn handle_announce(&self, envelope: &SyncEnvelope) {
        let Some(peer_vector) = envelope.state_vector.as_ref() else {
            return;
        };
        let local = self.store.get_state_vector();
        let local_map: HashMap<&str, i64> = local
            .iter()
            .map(|v| (v.entity_type.as_str(), v.max_known_version))
            .collect();

        let mut requests: Vec<RequestItem> = Vec::new();
        for peer in peer_vector {
            let our_max = local_map
                .get(peer.entity_type.as_str())
                .copied()
                .unwrap_or(0);
            if peer.max_known_version > our_max {
                requests.push(RequestItem::new(peer.entity_type.clone(), our_max));
            }
        }
        if requests.is_empty() {
            return;
        }

        self.channel.send(&SyncEnvelope::new(
            SyncEnvelopeKind::Request,
            self.channel.local_node_id(),
            None,
            Some(requests),
            None,
        ));
    }

    fn handle_request(&self, envelope: &SyncEnvelope) {
        let Some(requests) = envelope.requests.as_ref() else {
            return;
        };
        if requests.is_empty() {
            return;
        }
        let mut collected: Vec<SyncableEntry> = Vec::new();
        for req in requests {
            let newer = self.store.get_since(&req.entity_type, req.since_version);
            collected.extend(newer);
        }
        if collected.is_empty() {
            return;
        }

        self.channel.send(&SyncEnvelope::new(
            SyncEnvelopeKind::Push,
            self.channel.local_node_id(),
            None,
            None,
            Some(collected),
        ));
    }

    fn handle_push(&self, envelope: &SyncEnvelope) {
        let Some(entries) = envelope.entries.as_ref() else {
            return;
        };
        let mut any_applied = false;
        for e in entries {
            self.clock.observe(e.version);
            let applied = self.store.apply(e);
            any_applied |= applied;
        }
        // If anything applied, re-announce so other peers can converge too.
        if any_applied {
            self.sync_now();
        }
    }
}

/// Default [`ICompanionStateSyncEngine`]. 1:1 with the C#
/// `CompanionStateSyncEngine`.
pub struct CompanionStateSyncEngine {
    core: Arc<EngineCore>,
    subscription: Mutex<Option<Subscription>>,
    started: AtomicBool,
}

impl CompanionStateSyncEngine {
    /// Wires the engine over a channel, store, and clock. Uses the system
    /// wall-clock for `authored_at`.
    pub fn new(
        channel: Arc<dyn ICompanionStateChannel>,
        store: Arc<dyn ISyncableEntryStore>,
        clock: Arc<HybridLogicalClock>,
    ) -> Self {
        Self::with_wall_clock(channel, store, clock, Arc::new(Utc::now))
    }

    /// Wires the engine with an explicit wall-clock source (for tests).
    pub fn with_wall_clock(
        channel: Arc<dyn ICompanionStateChannel>,
        store: Arc<dyn ISyncableEntryStore>,
        clock: Arc<HybridLogicalClock>,
        wall_clock: WallClockFn,
    ) -> Self {
        Self {
            core: Arc::new(EngineCore {
                channel,
                store,
                clock,
                wall_clock,
            }),
            subscription: Mutex::new(None),
            started: AtomicBool::new(false),
        }
    }

    /// True once [`start`](ICompanionStateSyncEngine::start) has subscribed the
    /// engine — used to decide whether `write_local` also broadcasts a Push.
    fn is_started(&self) -> bool {
        self.started.load(Ordering::SeqCst)
    }
}

impl ICompanionStateSyncEngine for CompanionStateSyncEngine {
    fn start(&self) {
        let mut sub = self.subscription.lock().unwrap();
        if sub.is_some() {
            return;
        }
        let core = Arc::clone(&self.core);
        let handler: EnvelopeHandler = Arc::new(move |envelope: &SyncEnvelope| {
            core.handle_envelope(envelope);
        });
        *sub = Some(self.core.channel.subscribe(handler));
        self.started.store(true, Ordering::SeqCst);
    }

    fn sync_now(&self) {
        self.core.sync_now();
    }

    fn write_local(
        &self,
        entity_type: &str,
        entity_id: &str,
        payload: &str,
        is_tombstone: bool,
    ) -> SyncableEntry {
        assert!(!entity_type.trim().is_empty(), "entityType required");
        assert!(!entity_id.trim().is_empty(), "entityId required");

        let entry = SyncableEntry::new(
            entity_type,
            entity_id,
            self.core.clock.tick(),
            is_tombstone,
            compute_sha256(payload.as_bytes()),
            payload,
            self.core.channel.local_node_id(),
            (self.core.wall_clock)(),
        );

        self.core.store.apply(&entry);

        if self.is_started() {
            self.core.channel.send(&SyncEnvelope::new(
                SyncEnvelopeKind::Push,
                self.core.channel.local_node_id(),
                None,
                None,
                Some(vec![entry.clone()]),
            ));
        }
        entry
    }
}

impl Drop for CompanionStateSyncEngine {
    fn drop(&mut self) {
        // Dropping the stored Subscription unsubscribes the handler.
        *self.subscription.lock().unwrap() = None;
        self.started.store(false, Ordering::SeqCst);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// PersonaStateSyncBridge
// ─────────────────────────────────────────────────────────────────────────────

/// EntityType used on the wire for PersonaState entries.
pub const PERSONA_STATE_ENTITY_TYPE: &str = "PersonaState";

/// Bridges [`IPersonaStore`] ↔ [`ICompanionStateSyncEngine`]. On
/// [`save`](Self::save) the persona is JSON-serialised and pushed. 1:1 with the
/// C# `PersonaStateSyncBridge`.
pub struct PersonaStateSyncBridge<S: IPersonaStore> {
    store: S,
    engine: Arc<dyn ICompanionStateSyncEngine>,
}

impl<S: IPersonaStore> PersonaStateSyncBridge<S> {
    /// EntityType used on the wire for PersonaState entries.
    pub const ENTITY_TYPE: &'static str = PERSONA_STATE_ENTITY_TYPE;

    pub fn new(store: S, engine: Arc<dyn ICompanionStateSyncEngine>) -> Self {
        Self { store, engine }
    }

    /// Persists `persona` locally AND broadcasts it via sync.
    pub fn save(&mut self, persona: &PersonaState) -> Result<(), S::Error> {
        self.store.save(persona)?;
        let payload = serde_json::to_string(persona).unwrap_or_default();
        self.engine
            .write_local(Self::ENTITY_TYPE, &persona.user_id, &payload, false);
        Ok(())
    }

    /// Decodes a [`SyncableEntry`] back into a [`PersonaState`]. Returns `None`
    /// for tombstones, wrong entity types, or malformed payloads.
    pub fn try_decode(entry: &SyncableEntry) -> Option<PersonaState> {
        if entry.is_tombstone {
            return None;
        }
        if entry.entity_type != PERSONA_STATE_ENTITY_TYPE {
            return None;
        }
        serde_json::from_str(&entry.payload).ok()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// LoraAdapterSyncBridge
// ─────────────────────────────────────────────────────────────────────────────

/// EntityType used on the wire for LoRA adapter snapshots.
pub const LORA_ADAPTER_ENTITY_TYPE: &str = "LoraAdapter";

/// (Phase D4) Payload of a synced LoRA adapter snapshot.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct LoraAdapterSnapshot {
    /// Stable id (typically "personal-{userId}").
    pub adapter_id: String,
    /// Adapter file contents, base64-encoded.
    pub base64_bytes: String,
    /// When training that produced these bytes finished.
    pub trained_at_utc: DateTime<Utc>,
    /// Total training steps so far (monotonic).
    pub step_count: i64,
}

impl LoraAdapterSnapshot {
    pub fn new(
        adapter_id: impl Into<String>,
        base64_bytes: impl Into<String>,
        trained_at_utc: DateTime<Utc>,
        step_count: i64,
    ) -> Self {
        Self {
            adapter_id: adapter_id.into(),
            base64_bytes: base64_bytes.into(),
            trained_at_utc,
            step_count,
        }
    }
}

/// Bridges trained LoRA adapter bytes across the user's devices through the
/// [`ICompanionStateSyncEngine`]. Adapter bytes are base64-encoded into the
/// payload. 1:1 with the C# `LoraAdapterSyncBridge` (byte I/O is injected so the
/// port stays in-memory / filesystem-agnostic).
pub struct LoraAdapterSyncBridge {
    engine: Arc<dyn ICompanionStateSyncEngine>,
}

impl LoraAdapterSyncBridge {
    /// EntityType used on the wire.
    pub const ENTITY_TYPE: &'static str = LORA_ADAPTER_ENTITY_TYPE;

    pub fn new(engine: Arc<dyn ICompanionStateSyncEngine>) -> Self {
        Self { engine }
    }

    /// Publish trained adapter `bytes` to peer devices under `adapter_id`.
    ///
    /// The C# overload reads the bytes from a file path; the byte source is the
    /// caller's concern here (keeps the port filesystem-free). `trained_at` is
    /// injected for deterministic tests.
    ///
    /// # Panics
    /// Panics when `adapter_id` is blank.
    pub fn publish(
        &self,
        adapter_id: &str,
        bytes: &[u8],
        step_count: i64,
        trained_at: DateTime<Utc>,
    ) -> LoraAdapterSnapshot {
        assert!(!adapter_id.trim().is_empty(), "adapterId required");
        let snapshot = LoraAdapterSnapshot::new(
            adapter_id,
            base64_encode(bytes),
            trained_at,
            step_count,
        );
        let payload = serde_json::to_string(&snapshot).unwrap_or_default();
        self.engine
            .write_local(Self::ENTITY_TYPE, adapter_id, &payload, false);
        snapshot
    }

    /// Decode an inbound [`SyncableEntry`] into `(snapshot, decoded_bytes)`.
    /// Returns `None` for tombstones, wrong entity types, or undecodable
    /// payloads. `decoded_bytes` is empty when the snapshot carried no base64.
    ///
    /// The C# variant writes the bytes to a destination path; here the caller
    /// receives the decoded bytes and decides what to do with them.
    pub fn try_decode(entry: &SyncableEntry) -> Option<(LoraAdapterSnapshot, Vec<u8>)> {
        if entry.is_tombstone {
            return None;
        }
        if entry.entity_type != LORA_ADAPTER_ENTITY_TYPE {
            return None;
        }
        let snapshot: LoraAdapterSnapshot = serde_json::from_str(&entry.payload).ok()?;
        if snapshot.base64_bytes.is_empty() {
            return Some((snapshot, Vec::new()));
        }
        let bytes = base64_decode(&snapshot.base64_bytes).unwrap_or_default();
        Some((snapshot, bytes))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionConversationSyncBridge
// ─────────────────────────────────────────────────────────────────────────────

/// EntityType used on the wire for conversation-state entries.
pub const CONVERSATION_STATE_ENTITY_TYPE: &str = "ConversationState";

/// (Phase A2) Wire-format payload of an in-flight conversation turn. The
/// `entity_id` is the `session_id` so multiple sessions converge independently.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct ConversationStateDelta {
    /// Stable identifier the originating device uses for this conversation.
    pub session_id: String,
    /// The latest user utterance for this turn (may be a partial transcript).
    pub user_text: String,
    /// Assistant reply so far — empty until the model starts emitting tokens.
    pub assistant_text: String,
    /// True once the turn finished; false during streaming.
    pub is_turn_complete: bool,
    /// When the originating device started the turn.
    pub started_at_utc: DateTime<Utc>,
    /// When this delta was authored.
    pub updated_at_utc: DateTime<Utc>,
}

impl ConversationStateDelta {
    pub fn new(
        session_id: impl Into<String>,
        user_text: impl Into<String>,
        assistant_text: impl Into<String>,
        is_turn_complete: bool,
        started_at_utc: DateTime<Utc>,
        updated_at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            session_id: session_id.into(),
            user_text: user_text.into(),
            assistant_text: assistant_text.into(),
            is_turn_complete,
            started_at_utc,
            updated_at_utc,
        }
    }
}

/// (Phase A2) Bridges live [`ConversationStateDelta`] snapshots to the
/// [`ICompanionStateSyncEngine`] wire. 1:1 with the C#
/// `CompanionConversationSyncBridge`.
pub struct CompanionConversationSyncBridge {
    engine: Arc<dyn ICompanionStateSyncEngine>,
}

impl CompanionConversationSyncBridge {
    /// EntityType used on the wire for conversation-state entries.
    pub const ENTITY_TYPE: &'static str = CONVERSATION_STATE_ENTITY_TYPE;

    pub fn new(engine: Arc<dyn ICompanionStateSyncEngine>) -> Self {
        Self { engine }
    }

    /// Broadcast a conversation-state snapshot to peer devices.
    ///
    /// # Panics
    /// Panics when `delta.session_id` is blank.
    pub fn publish(&self, delta: &ConversationStateDelta) {
        assert!(!delta.session_id.trim().is_empty(), "SessionId required");
        let payload = serde_json::to_string(delta).unwrap_or_default();
        self.engine
            .write_local(Self::ENTITY_TYPE, &delta.session_id, &payload, false);
    }

    /// Mark the session as ended so peers can clean up shadow state. Uses the
    /// sync-layer tombstone primitive — peers receive an empty payload.
    ///
    /// # Panics
    /// Panics when `session_id` is blank.
    pub fn terminate(&self, session_id: &str) {
        assert!(!session_id.trim().is_empty(), "sessionId required");
        self.engine
            .write_local(Self::ENTITY_TYPE, session_id, "", true);
    }

    /// Decode a sync-layer entry back to a typed delta. Returns `None` for
    /// tombstones, wrong entity types, or malformed payloads.
    pub fn try_decode(entry: &SyncableEntry) -> Option<ConversationStateDelta> {
        if entry.is_tombstone {
            return None;
        }
        if entry.entity_type != CONVERSATION_STATE_ENTITY_TYPE {
            return None;
        }
        serde_json::from_str(&entry.payload).ok()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Base64 (standard alphabet, with padding) — self-contained, matches
// Convert.ToBase64String / Convert.FromBase64String.
// ─────────────────────────────────────────────────────────────────────────────

const B64_ALPHABET: &[u8; 64] =
    b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/// Standard base64 encode with `=` padding.
pub fn base64_encode(input: &[u8]) -> String {
    let mut out = String::with_capacity(input.len().div_ceil(3) * 4);
    for chunk in input.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = *chunk.get(1).unwrap_or(&0) as u32;
        let b2 = *chunk.get(2).unwrap_or(&0) as u32;
        let triple = (b0 << 16) | (b1 << 8) | b2;
        out.push(B64_ALPHABET[((triple >> 18) & 0x3F) as usize] as char);
        out.push(B64_ALPHABET[((triple >> 12) & 0x3F) as usize] as char);
        if chunk.len() > 1 {
            out.push(B64_ALPHABET[((triple >> 6) & 0x3F) as usize] as char);
        } else {
            out.push('=');
        }
        if chunk.len() > 2 {
            out.push(B64_ALPHABET[(triple & 0x3F) as usize] as char);
        } else {
            out.push('=');
        }
    }
    out
}

/// Standard base64 decode. Returns `None` on invalid input.
pub fn base64_decode(input: &str) -> Option<Vec<u8>> {
    fn val(c: u8) -> Option<u32> {
        match c {
            b'A'..=b'Z' => Some((c - b'A') as u32),
            b'a'..=b'z' => Some((c - b'a' + 26) as u32),
            b'0'..=b'9' => Some((c - b'0' + 52) as u32),
            b'+' => Some(62),
            b'/' => Some(63),
            _ => None,
        }
    }

    let bytes: Vec<u8> = input.bytes().filter(|b| !b.is_ascii_whitespace()).collect();
    if bytes.len() % 4 != 0 {
        return None;
    }
    let mut out = Vec::with_capacity(bytes.len() / 4 * 3);
    for chunk in bytes.chunks(4) {
        let c0 = val(chunk[0])?;
        let c1 = val(chunk[1])?;
        let (c2, has2) = if chunk[2] == b'=' {
            (0, false)
        } else {
            (val(chunk[2])?, true)
        };
        let (c3, has3) = if chunk[3] == b'=' {
            (0, false)
        } else {
            (val(chunk[3])?, true)
        };
        let triple = (c0 << 18) | (c1 << 12) | (c2 << 6) | c3;
        out.push(((triple >> 16) & 0xFF) as u8);
        if has2 {
            out.push(((triple >> 8) & 0xFF) as u8);
        }
        if has3 {
            out.push((triple & 0xFF) as u8);
        }
    }
    Some(out)
}
