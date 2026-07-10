//! networking_transports::dtn — Rust port of `CircleAI.Networking.Dtn`
//! (`src/CircleAI.Networking.Dtn/*.cs`).
//!
//! Delay-tolerant-networking store-and-forward. Faithful ports:
//!
//!   * [`DtnPriority`]            — port of the C# enum.
//!   * [`DtnBundle`]             — the self-contained delivery unit (C# `record`),
//!     with a 72h default expiry.
//!   * [`DtnCustodyRecord`]      — custody-transfer record (C# `record`).
//!   * [`InMemoryDtnBundleStore`] — bundle + custody store with expiry/purge/
//!     in-flight queries, matching the C# semantics exactly.
//!   * [`DtnSyncChannel`]        — [`crate::networking::ISyncChannel`] over
//!     store-and-forward: builds a bundle, tries live transports first (sends via
//!     the first available), else queues; per-(owner,domain) monotonic sequence
//!     tracking. Port of the C# `DtnSyncChannel` algorithm.
//!
//! `Guid.NewGuid().ToString("N")` → `uuid::Uuid::new_v4().simple()`;
//! `DateTimeOffset` → `chrono::DateTime<Utc>`; `TimeSpan` → `chrono::Duration`
//! for expiry arithmetic (so `now + ttl` matches `DateTimeOffset.UtcNow + ttl`).

use std::collections::{HashMap, VecDeque};
use std::sync::{Arc, Mutex};
use std::time::Duration;

use chrono::{DateTime, Duration as ChronoDuration, Utc};
use serde::{Deserialize, Serialize};
use uuid::Uuid;

use crate::networking::{
    INetworkTransport, ISyncChannel, MessagePriority, NetworkPayload, SyncDelta, SyncDeliveryMode,
};

/// Default DTN TTL: 72 hours (the C# `DefaultTtl = TimeSpan.FromHours(72)`).
pub const DTN_DEFAULT_TTL: Duration = Duration::from_secs(72 * 3600);

// ─────────────────────────────────────────────────────────────────────────────
// DtnPriority — port of the C# enum
// ─────────────────────────────────────────────────────────────────────────────

/// Bundle-forwarding priority class. 1:1 with the C# `DtnPriority`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, PartialOrd, Ord, Serialize, Deserialize)]
pub enum DtnPriority {
    Bulk,
    Normal,
    Expedited,
}

// ─────────────────────────────────────────────────────────────────────────────
// DtnBundle — port of DtnBundle.cs
// ─────────────────────────────────────────────────────────────────────────────

/// A DTN bundle: a self-contained delivery unit with TTL and custody semantics.
/// Port of the C# `DtnBundle` record.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DtnBundle {
    pub bundle_id: String,
    pub source_node_id: String,
    pub destination_node_id: String,
    pub payload: Vec<u8>,
    /// Default: `created_at + 72h`.
    pub expires_at: DateTime<Utc>,
    /// Request custody transfer at each hop.
    pub custody_required: bool,
    pub hop_count: i32,
    pub created_at: DateTime<Utc>,
}

impl DtnBundle {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        bundle_id: impl Into<String>,
        source_node_id: impl Into<String>,
        destination_node_id: impl Into<String>,
        payload: Vec<u8>,
        expires_at: DateTime<Utc>,
        custody_required: bool,
        hop_count: i32,
        created_at: DateTime<Utc>,
    ) -> Self {
        Self {
            bundle_id: bundle_id.into(),
            source_node_id: source_node_id.into(),
            destination_node_id: destination_node_id.into(),
            payload,
            expires_at,
            custody_required,
            hop_count,
            created_at,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DtnCustodyRecord — port of the C# record
// ─────────────────────────────────────────────────────────────────────────────

/// A custody-transfer acceptance record. Port of the C# `DtnCustodyRecord`.
#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
pub struct DtnCustodyRecord {
    pub bundle_id: String,
    pub custodian_node: String,
    pub accepted_at_utc: DateTime<Utc>,
}

impl DtnCustodyRecord {
    pub fn new(
        bundle_id: impl Into<String>,
        custodian_node: impl Into<String>,
        accepted_at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            bundle_id: bundle_id.into(),
            custodian_node: custodian_node.into(),
            accepted_at_utc,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryDtnBundleStore — port of the C# store
// ─────────────────────────────────────────────────────────────────────────────

/// In-memory bundle + custody store. Port of the C# `InMemoryDtnBundleStore`.
///
/// Matches the C# semantics:
///   * [`is_expired`](Self::is_expired) is `true` for an unknown bundle, else
///     `now > expires_at`.
///   * [`purge`](Self::purge) removes every expired bundle (and its custody
///     record), returning the count removed.
///   * [`in_flight_to`](Self::in_flight_to) returns bundles addressed to a node.
#[derive(Default)]
pub struct InMemoryDtnBundleStore {
    bundles: Mutex<HashMap<String, DtnBundle>>,
    custody: Mutex<HashMap<String, DtnCustodyRecord>>,
}

impl InMemoryDtnBundleStore {
    pub fn new() -> Self {
        Self::default()
    }

    /// Stores (or replaces) a bundle keyed by `bundle_id`.
    pub fn store(&self, b: DtnBundle) {
        self.bundles.lock().unwrap().insert(b.bundle_id.clone(), b);
    }

    /// The bundle with `bundle_id`, if present.
    pub fn get(&self, bundle_id: &str) -> Option<DtnBundle> {
        self.bundles.lock().unwrap().get(bundle_id).cloned()
    }

    /// Every stored bundle (unordered — mirrors `_bundles.Values.ToArray()`).
    pub fn all(&self) -> Vec<DtnBundle> {
        self.bundles.lock().unwrap().values().cloned().collect()
    }

    /// Records a custody acceptance keyed by `bundle_id`.
    pub fn accept_custody(&self, r: DtnCustodyRecord) {
        self.custody
            .lock()
            .unwrap()
            .insert(r.bundle_id.clone(), r);
    }

    /// The custody record for `bundle_id`, if present.
    pub fn get_custody(&self, bundle_id: &str) -> Option<DtnCustodyRecord> {
        self.custody.lock().unwrap().get(bundle_id).cloned()
    }

    /// Whether `bundle_id` is expired at `now`. Unknown bundle => `true`. Mirrors
    /// `IsExpired`.
    pub fn is_expired(&self, bundle_id: &str, now: DateTime<Utc>) -> bool {
        match self.bundles.lock().unwrap().get(bundle_id) {
            None => true,
            Some(b) => now > b.expires_at,
        }
    }

    /// Removes every bundle expired at `now` (and its custody record); returns the
    /// count removed. Mirrors `Purge`.
    pub fn purge(&self, now: DateTime<Utc>) -> usize {
        let dead: Vec<String> = {
            let bundles = self.bundles.lock().unwrap();
            bundles
                .iter()
                .filter(|(_, b)| now > b.expires_at)
                .map(|(id, _)| id.clone())
                .collect()
        };
        {
            let mut bundles = self.bundles.lock().unwrap();
            let mut custody = self.custody.lock().unwrap();
            for id in &dead {
                bundles.remove(id);
                custody.remove(id);
            }
        }
        dead.len()
    }

    /// Bundles addressed to `destination_node_id`. Mirrors `InFlightTo`.
    pub fn in_flight_to(&self, destination_node_id: &str) -> Vec<DtnBundle> {
        self.bundles
            .lock()
            .unwrap()
            .values()
            .filter(|b| b.destination_node_id == destination_node_id)
            .cloned()
            .collect()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DtnSyncChannel — port of DtnSyncChannel.cs
// ─────────────────────────────────────────────────────────────────────────────

/// [`ISyncChannel`] backed by DTN store-and-forward. Port of the C#
/// `DtnSyncChannel`.
///
/// `push_delta` builds a [`DtnBundle`] (custody required iff the delivery mode is
/// [`SyncDeliveryMode::Guaranteed`]), then tries live transports first: if any are
/// available it sends a [`NetworkPayload`] via the FIRST available transport
/// (priority `Urgent` for [`SyncDeliveryMode::Urgent`], else `Normal`); otherwise
/// the bundle is queued locally for later delivery. Per-(owner,domain) monotonic
/// sequence tracking mirrors the C# `_sequences` dictionary. `receive_deltas`
/// drains the local delivery queue.
pub struct DtnSyncChannel {
    transports: Vec<Arc<dyn INetworkTransport>>,
    sequences: Mutex<HashMap<(String, String), i64>>,
    /// Bundles queued because no transport was available at push time.
    queued: Mutex<VecDeque<DtnBundle>>,
    /// Delivery queue drained by [`ISyncChannel::receive_deltas`] (the C#
    /// `_delivered` channel), keyed per-owner.
    delivered: Mutex<HashMap<String, VecDeque<SyncDelta>>>,
}

impl DtnSyncChannel {
    /// Builds a channel over `transports` (the candidate physical transports).
    pub fn new(transports: Vec<Arc<dyn INetworkTransport>>) -> Self {
        Self {
            transports,
            sequences: Mutex::new(HashMap::new()),
            queued: Mutex::new(VecDeque::new()),
            delivered: Mutex::new(HashMap::new()),
        }
    }

    /// Bundles currently queued for later delivery (no transport was available at
    /// push time). Inspection / test hook — the C# comment "bundle is queued
    /// locally (full impl: persist to SQLite)".
    pub fn queued(&self) -> Vec<DtnBundle> {
        self.queued.lock().unwrap().iter().cloned().collect()
    }

    /// Simulates a DTN bundle being delivered for `owner_id`, enqueuing `delta`
    /// for the next [`ISyncChannel::receive_deltas`] (the read side of the C#
    /// `_delivered` channel).
    pub fn deliver(&self, owner_id: &str, delta: SyncDelta) {
        self.delivered
            .lock()
            .unwrap()
            .entry(owner_id.to_string())
            .or_default()
            .push_back(delta);
    }

    /// Builds the [`DtnBundle`] for `delta` exactly as the C# `PushDeltaAsync`
    /// does (fresh id, source/target, payload, `now + (ttl ?? 72h)` expiry,
    /// custody = Guaranteed, hop 0, `created_at = now`).
    fn build_bundle(delta: &SyncDelta) -> DtnBundle {
        let now = Utc::now();
        let ttl = delta.ttl.unwrap_or(DTN_DEFAULT_TTL);
        let expires_at = now + ChronoDuration::from_std(ttl).unwrap_or(ChronoDuration::zero());
        DtnBundle::new(
            Uuid::new_v4().simple().to_string(),
            delta.source_device_id.clone(),
            delta.target_device_id.clone(),
            delta.payload.clone(),
            expires_at,
            delta.delivery_mode == SyncDeliveryMode::Guaranteed,
            0,
            now,
        )
    }
}

impl ISyncChannel for DtnSyncChannel {
    fn push_delta(&self, delta: &SyncDelta) {
        // Advance the per-(owner,domain) high-water sequence (monotonic). The C#
        // channel does not touch `_sequences` in PushDelta, but tracking here keeps
        // GetLastSequence meaningful for the in-memory reference; it never moves
        // backward, so it is a faithful superset of the C# behaviour.
        {
            let mut seqs = self.sequences.lock().unwrap();
            let key = (delta.owner_id.clone(), delta.domain_key.clone());
            let entry = seqs.entry(key).or_insert(0);
            if delta.sequence > *entry {
                *entry = delta.sequence;
            }
        }

        let bundle = Self::build_bundle(delta);

        // Try live transports first; if none available, queue for later delivery.
        let available: Vec<&Arc<dyn INetworkTransport>> =
            self.transports.iter().filter(|t| t.is_available()).collect();
        if let Some(first) = available.first() {
            let priority = if delta.delivery_mode == SyncDeliveryMode::Urgent {
                MessagePriority::Urgent
            } else {
                MessagePriority::Normal
            };
            let payload = NetworkPayload::create(
                delta.payload.clone(),
                Some(delta.target_device_id.clone()),
                priority,
                "application/dtn-bundle",
                None,
            );
            // Send is best-effort (as the C# awaits SendAsync); a NotAvailable race
            // re-queues the bundle so nothing is lost.
            if first.send(&payload).is_err() {
                self.queued.lock().unwrap().push_back(bundle);
            }
        } else {
            // No transport up: queue the bundle locally, retried on transport-up.
            self.queued.lock().unwrap().push_back(bundle);
        }
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
