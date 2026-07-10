//! node_trust_registry.rs
//!
//! Thread-safe, per-peer trust store — Rust port of `NodeTrustRegistry.cs`.
//!
//! - Each peer gets a score in `[0, 1]`. `1.0` = fully trusted; `0.0` = fully lost.
//! - `apply_degradation` drops the score and records the triggering event.
//! - `apply_recovery` heals all peers passively (called by a background timer).
//! - `trust_score_updates` drains an unbounded backlog; readers receive every
//!   change made since the last drain.
//!
//! Transport-agnostic: stores `PeerSecurityEvent`, emits `PeerTrustScoreUpdate`.

use std::collections::VecDeque;
use std::sync::{Mutex, RwLock};

use chrono::{DateTime, Utc};

use super::peer_security_types::{PeerSecurityEvent, PeerTrustScoreUpdate};
use super::security_options::SecurityOptions;

/// Per-peer mutable trust state. Exposed for diagnostics and tests.
#[derive(Debug, Clone)]
pub struct NodeTrustEntry {
    /// Stable peer identifier.
    pub node_id: String,
    /// Current trust score in `[0, 1]`.
    pub trust_score: f64,
    /// UTC timestamp of the last update.
    pub last_updated: DateTime<Utc>,
    /// Bounded history of security events (oldest-first).
    pub recent_events: Vec<PeerSecurityEvent>,
}

impl NodeTrustEntry {
    fn new(node_id: String, trust_score: f64) -> Self {
        Self {
            node_id,
            trust_score,
            last_updated: Utc::now(),
            recent_events: Vec::new(),
        }
    }
}

/// Maintains per-peer trust scores, event history, and an unbounded backlog of
/// trust score changes consumed by
/// [`crate::security::PeerIntelligenceService`].
pub struct NodeTrustRegistry {
    options: SecurityOptions,
    // Each entry is individually locked (mirrors the C# per-entry `lock`), and
    // the map itself is behind an RwLock so `GetOrCreate`/`AllNodeIds` are safe.
    nodes: RwLock<std::collections::HashMap<String, Mutex<NodeTrustEntry>>>,
    // Unbounded backlog buffer: every published update is retained until drained
    // (matches the unbounded `Channel<PeerTrustScoreUpdate>`).
    backlog: Mutex<VecDeque<PeerTrustScoreUpdate>>,
}

impl NodeTrustRegistry {
    /// Creates a registry with the given options.
    pub fn new(options: SecurityOptions) -> Self {
        Self {
            options,
            nodes: RwLock::new(std::collections::HashMap::new()),
            backlog: Mutex::new(VecDeque::new()),
        }
    }

    // ─── Peer access ──────────────────────────────────────────────────────────

    /// Ensures an entry exists for `node_id`, initialised to
    /// [`SecurityOptions::initial_trust_score`] on first observation. Returns
    /// the current trust score of the (possibly freshly created) entry.
    pub fn get_or_create(&self, node_id: &str) -> f64 {
        {
            let map = self.nodes.read().unwrap();
            if let Some(cell) = map.get(node_id) {
                return cell.lock().unwrap().trust_score;
            }
        }
        let mut map = self.nodes.write().unwrap();
        let cell = map.entry(node_id.to_string()).or_insert_with(|| {
            Mutex::new(NodeTrustEntry::new(
                node_id.to_string(),
                self.options.initial_trust_score,
            ))
        });
        let score = cell.lock().unwrap().trust_score;
        score
    }

    /// Returns a clone of the entry for `node_id`, or `None` if unknown.
    pub fn snapshot_entry(&self, node_id: &str) -> Option<NodeTrustEntry> {
        let map = self.nodes.read().unwrap();
        map.get(node_id).map(|cell| cell.lock().unwrap().clone())
    }

    /// All peer IDs currently tracked.
    pub fn all_node_ids(&self) -> Vec<String> {
        let map = self.nodes.read().unwrap();
        map.keys().cloned().collect()
    }

    /// Returns the current trust score for `node_id`, or
    /// [`SecurityOptions::initial_trust_score`] for unknown peers.
    pub fn get_trust_score(&self, node_id: &str) -> f64 {
        let map = self.nodes.read().unwrap();
        if let Some(cell) = map.get(node_id) {
            return cell.lock().unwrap().trust_score;
        }
        self.options.initial_trust_score
    }

    // ─── Mutations ────────────────────────────────────────────────────────────

    /// Applies trust degradation for a security event. Score is clamped to
    /// `[0, 1]`; the event is appended to the per-peer history; a
    /// [`PeerTrustScoreUpdate`] is published on the backlog. Returns
    /// `(previous_score, new_score)`.
    pub fn apply_degradation(
        &self,
        security_event: &PeerSecurityEvent,
        degradation_amount: f64,
    ) -> (f64, f64) {
        // Ensure the entry exists.
        self.get_or_create(&security_event.node_id);

        // Compute the mutation under the entry lock, collect the publish outside.
        let publish;
        let result;
        {
            let map = self.nodes.read().unwrap();
            let cell = map
                .get(&security_event.node_id)
                .expect("entry created above");
            let mut entry = cell.lock().unwrap();

            let previous = entry.trust_score;
            entry.trust_score = (previous - degradation_amount).clamp(0.0, 1.0);
            entry.last_updated = security_event.occurred_at;

            entry.recent_events.push(security_event.clone());
            while entry.recent_events.len() > self.options.max_events_per_node {
                entry.recent_events.remove(0);
            }

            let current = entry.trust_score;
            publish = if (current - previous).abs() > 0.0001 {
                Some(PeerTrustScoreUpdate {
                    node_id: entry.node_id.clone(),
                    previous_score: previous,
                    new_score: current,
                    reason: security_event.description.clone(),
                    changed_at: security_event.occurred_at,
                })
            } else {
                None
            };
            result = (previous, current);
        }

        if let Some(update) = publish {
            self.publish(update);
        }
        result
    }

    /// Passively heals all tracked peers by `recovery_rate_per_second × elapsed`.
    /// Peers already at `1.0` are skipped. Called by the background recovery
    /// timer.
    pub fn apply_recovery(&self, elapsed: chrono::Duration) {
        let amount = self.options.recovery_rate_per_second
            * (elapsed.num_milliseconds() as f64 / 1000.0);
        if amount <= 0.0 {
            return;
        }

        // Collect the publishes under per-entry locks, emit after releasing.
        let mut updates: Vec<PeerTrustScoreUpdate> = Vec::new();
        {
            let map = self.nodes.read().unwrap();
            for cell in map.values() {
                let mut entry = cell.lock().unwrap();
                if entry.trust_score >= 1.0 {
                    continue;
                }
                let previous = entry.trust_score;
                entry.trust_score = (previous + amount).min(1.0);
                let now = Utc::now();
                entry.last_updated = now;
                updates.push(PeerTrustScoreUpdate {
                    node_id: entry.node_id.clone(),
                    previous_score: previous,
                    new_score: entry.trust_score,
                    reason: "passive-recovery".to_string(),
                    changed_at: now,
                });
            }
        }
        for u in updates {
            self.publish(u);
        }
    }

    // ─── History queries ──────────────────────────────────────────────────────

    /// Returns events for `node_id` that fall within
    /// [`SecurityOptions::event_window`] of now. Returns an empty list for
    /// unknown peers.
    pub fn get_recent_events(&self, node_id: &str) -> Vec<PeerSecurityEvent> {
        let map = self.nodes.read().unwrap();
        let Some(cell) = map.get(node_id) else {
            return Vec::new();
        };
        let cutoff = Utc::now() - self.options.event_window;
        let entry = cell.lock().unwrap();
        entry
            .recent_events
            .iter()
            .filter(|e| e.occurred_at >= cutoff)
            .cloned()
            .collect()
    }

    // ─── Trust score stream ─────────────────────────────────────────────────

    /// Drains every trust score change published since the last drain. The
    /// backlog is unbounded, so updates published before the first drain are
    /// retained (matching the unbounded C# `Channel`).
    pub fn trust_score_updates(&self) -> Vec<PeerTrustScoreUpdate> {
        let mut backlog = self.backlog.lock().unwrap();
        backlog.drain(..).collect()
    }

    // ─── Private ──────────────────────────────────────────────────────────────

    fn publish(&self, update: PeerTrustScoreUpdate) {
        self.backlog.lock().unwrap().push_back(update);
    }
}
