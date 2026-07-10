//! aethernet::mesh_capability_registry — Rust port of
//! `CircleAI.AetherNet/MeshCapabilityRegistry.cs` (RT-12 v1).
//!
//! Mesh capability discovery: peers broadcast what they have loaded ("I have
//! Qwen3-1.7B-MNN with 2048 tokens of free KV budget on a Tier=Phone device").
//! v1 ships the contracts + an in-memory registry; the AetherNet broadcast
//! transport lands later with RT-12 v2 actual offload.
//!
//! `ValueTask` maps to a direct return (the crate is sync); `TimeSpan?`
//! staleness windows map to `Option<chrono::Duration>`. The registry is
//! thread-safe and, without a transport feeding it, simply stays empty.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Duration, Utc};
use serde::{Deserialize, Serialize};

use crate::device::DeviceTier;

// ─────────────────────────────────────────────────────────────────────────────
// MeshCapabilityAdvertisement
// ─────────────────────────────────────────────────────────────────────────────

/// One peer's advertisement of what it can serve right now. Pure data — no
/// execution state.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct MeshCapabilityAdvertisement {
    /// Stable opaque identifier for the advertising peer.
    pub peer_id: String,
    /// The model the peer has loaded, e.g. `"Qwen3-1.7B-MNN"`.
    pub model_id: String,
    /// How many tokens of KV-cache budget the peer has spare.
    pub free_kv_tokens: i32,
    /// The peer's device tier.
    pub tier: DeviceTier,
    /// The model's configured context window.
    pub context_window_tokens: i32,
    /// When the peer last published this advertisement.
    pub advertised_at_utc: DateTime<Utc>,
    /// Optional round-trip estimate; `None` when unknown.
    pub latency_hint_ms: Option<i32>,
}

impl MeshCapabilityAdvertisement {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        peer_id: impl Into<String>,
        model_id: impl Into<String>,
        free_kv_tokens: i32,
        tier: DeviceTier,
        context_window_tokens: i32,
        advertised_at_utc: DateTime<Utc>,
        latency_hint_ms: Option<i32>,
    ) -> Self {
        Self {
            peer_id: peer_id.into(),
            model_id: model_id.into(),
            free_kv_tokens,
            tier,
            context_window_tokens,
            advertised_at_utc,
            latency_hint_ms,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IMeshCapabilityRegistry
// ─────────────────────────────────────────────────────────────────────────────

/// Holds the latest advertisement per peer + supports filtered query. The
/// AetherNet transport feeds this registry as peers broadcast; without a
/// transport it stays empty.
pub trait IMeshCapabilityRegistry: Send + Sync {
    /// Publish or replace an advertisement. Called by the transport on receipt of
    /// a peer broadcast.
    fn upsert(&self, ad: MeshCapabilityAdvertisement);

    /// Remove a peer (e.g. on explicit disconnect). Idempotent. Returns `true`
    /// when an entry was removed.
    fn remove(&self, peer_id: &str) -> bool;

    /// Return every advertisement currently known. `stale_after` filters out
    /// entries older than that window (`None` = no filtering).
    fn list(&self, stale_after: Option<Duration>) -> Vec<MeshCapabilityAdvertisement>;

    /// Find every peer that has loaded `model_id` with at least `min_free_kv_tokens`
    /// of spare KV budget. Sorted by spare budget descending — the most-capable
    /// peer comes first.
    fn find(
        &self,
        model_id: &str,
        min_free_kv_tokens: i32,
        stale_after: Option<Duration>,
    ) -> Vec<MeshCapabilityAdvertisement>;
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryMeshCapabilityRegistry
// ─────────────────────────────────────────────────────────────────────────────

/// Default [`IMeshCapabilityRegistry`] — in-memory, thread-safe. The AetherNet
/// transport plugs into this; without a transport it just stays empty.
///
/// Keys use ordinal (case-sensitive) peer-id equality, matching the C#
/// `StringComparer.Ordinal`; model-id matching in [`InMemoryMeshCapabilityRegistry::find`]
/// is case-insensitive, matching `StringComparison.OrdinalIgnoreCase`.
pub struct InMemoryMeshCapabilityRegistry {
    entries: Mutex<HashMap<String, MeshCapabilityAdvertisement>>,
    now_utc: Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>,
}

impl InMemoryMeshCapabilityRegistry {
    /// Creates an empty registry using the system UTC clock.
    pub fn new() -> Self {
        Self {
            entries: Mutex::new(HashMap::new()),
            now_utc: Arc::new(Utc::now),
        }
    }

    /// Creates an empty registry with an explicit clock (for tests). Mirrors the
    /// C# `NowUtc` init property.
    pub fn with_clock(now_utc: Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>) -> Self {
        Self {
            entries: Mutex::new(HashMap::new()),
            now_utc,
        }
    }
}

impl Default for InMemoryMeshCapabilityRegistry {
    fn default() -> Self {
        Self::new()
    }
}

impl IMeshCapabilityRegistry for InMemoryMeshCapabilityRegistry {
    fn upsert(&self, ad: MeshCapabilityAdvertisement) {
        // C# guards against a null/blank PeerId; here we drop a blank id rather
        // than store an unqueryable entry.
        if ad.peer_id.trim().is_empty() {
            return;
        }
        self.entries
            .lock()
            .unwrap()
            .insert(ad.peer_id.clone(), ad);
    }

    fn remove(&self, peer_id: &str) -> bool {
        if peer_id.trim().is_empty() {
            return false;
        }
        self.entries.lock().unwrap().remove(peer_id).is_some()
    }

    fn list(&self, stale_after: Option<Duration>) -> Vec<MeshCapabilityAdvertisement> {
        let map = self.entries.lock().unwrap();
        match stale_after {
            None => map.values().cloned().collect(),
            Some(window) => {
                let cutoff = (self.now_utc)() - window;
                map.values()
                    .filter(|a| a.advertised_at_utc >= cutoff)
                    .cloned()
                    .collect()
            }
        }
    }

    fn find(
        &self,
        model_id: &str,
        min_free_kv_tokens: i32,
        stale_after: Option<Duration>,
    ) -> Vec<MeshCapabilityAdvertisement> {
        if model_id.trim().is_empty() {
            return Vec::new();
        }
        let cutoff = stale_after.map(|w| (self.now_utc)() - w);
        let map = self.entries.lock().unwrap();
        let mut hits: Vec<MeshCapabilityAdvertisement> = map
            .values()
            .filter(|a| a.model_id.eq_ignore_ascii_case(model_id))
            .filter(|a| a.free_kv_tokens >= min_free_kv_tokens)
            .filter(|a| cutoff.map(|c| a.advertised_at_utc >= c).unwrap_or(true))
            .cloned()
            .collect();
        // Sort by spare budget descending — most-capable peer first. Stable sort
        // keeps insertion-independent ties deterministic.
        hits.sort_by(|a, b| b.free_kv_tokens.cmp(&a.free_kv_tokens));
        hits
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IMeshCapabilityBroadcaster
// ─────────────────────────────────────────────────────────────────────────────

/// Contract for the broadcaster that publishes OUR advertisement to the mesh. v1
/// ships a no-op default; the AetherNet transport binding (v2) supersedes it.
pub trait IMeshCapabilityBroadcaster: Send + Sync {
    /// Publish our current advertisement to the mesh. v1 may be a no-op when no
    /// transport is registered.
    fn broadcast(&self, ad: &MeshCapabilityAdvertisement);
}

/// Default broadcaster — does nothing. Used when no AetherNet transport is
/// bound. Existing CircleAI deployments work unchanged.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullMeshCapabilityBroadcaster;

impl NullMeshCapabilityBroadcaster {
    pub fn new() -> Self {
        Self
    }
}

impl IMeshCapabilityBroadcaster for NullMeshCapabilityBroadcaster {
    fn broadcast(&self, _ad: &MeshCapabilityAdvertisement) {}
}
