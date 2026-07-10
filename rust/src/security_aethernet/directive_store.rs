//! security_aethernet::directive_store — Rust port of
//! `CircleAI.Security.AetherNet/MeshDirectiveStore.cs` and `MeshSecurityGate.cs`.
//!
//! [`MeshDirectiveStore`] is an in-memory record of every active
//! [`SecurityDirective`] the mesh has issued against a node. It implements
//! [`ISecurityDirectiveConsumer`] so it can be plugged in as the directive sink,
//! and exposes two query surfaces:
//!   * [`MeshDirectiveStore::is_blocked`] — fast hot-path check.
//!   * [`MeshDirectiveStore::active_directives`] — full audit detail.
//! Expiry is handled lazily on read (no background timer). Block state observes
//! Avoid + Quarantine; Release lifts both.
//!
//! [`MeshSecurityGate`] is the read-only fast-path query view over the store —
//! the type CircleAI features inject to consult mesh-issued directives before
//! serving a request.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Utc};

use crate::aether::security_layer::{
    ISecurityDirectiveConsumer, SecurityDirective, SecurityDirectiveKind,
};

// ─────────────────────────────────────────────────────────────────────────────
// MeshDirectiveStore
// ─────────────────────────────────────────────────────────────────────────────

/// Thread-safe in-memory registry of security directives received from the mesh.
/// Acts as both the directive sink and the query surface other CircleAI
/// components consult before serving a request.
pub struct MeshDirectiveStore {
    by_node: Mutex<HashMap<String, Vec<SecurityDirective>>>,
    clock: Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>,
}

impl MeshDirectiveStore {
    /// Constructs a store using the system UTC clock.
    pub fn new() -> Self {
        Self {
            by_node: Mutex::new(HashMap::new()),
            clock: Arc::new(Utc::now),
        }
    }

    /// Constructs a store with an explicit clock (for testing).
    pub fn with_clock(clock: Arc<dyn Fn() -> DateTime<Utc> + Send + Sync>) -> Self {
        Self {
            by_node: Mutex::new(HashMap::new()),
            clock,
        }
    }

    /// Returns `(true, reason)` when an unexpired Avoid or Quarantine directive
    /// is active for the node; `reason` carries the most recent block's text.
    /// Expired entries are swept while walking the list.
    pub fn is_blocked(&self, node_id: &str) -> (bool, String) {
        if node_id.trim().is_empty() {
            return (false, String::new());
        }
        let now = (self.clock)();
        let mut map = self.by_node.lock().unwrap();
        let Some(list) = map.get_mut(node_id) else {
            return (false, String::new());
        };

        // Drop expired entries; track the latest block by issued_at. Uses a
        // strict-greater fold so that on an exact `issued_at` tie the first-seen
        // block wins — matching the C# `d.IssuedAt > latestBlock.IssuedAt` scan.
        list.retain(|d| !Self::is_expired(d, now));
        let mut latest_block: Option<&SecurityDirective> = None;
        for d in list.iter().filter(|d| Self::is_block_kind(d.kind)) {
            if latest_block.is_none() || d.issued_at > latest_block.unwrap().issued_at {
                latest_block = Some(d);
            }
        }

        let result = match latest_block {
            Some(d) => (true, d.reason.clone()),
            None => (false, String::new()),
        };
        if list.is_empty() {
            map.remove(node_id);
        }
        result
    }

    /// Lists every unexpired directive for the node — useful for audit/diagnostics.
    pub fn active_directives(&self, node_id: &str) -> Vec<SecurityDirective> {
        if node_id.trim().is_empty() {
            return Vec::new();
        }
        let now = (self.clock)();
        let map = self.by_node.lock().unwrap();
        match map.get(node_id) {
            None => Vec::new(),
            Some(list) => list
                .iter()
                .filter(|d| !Self::is_expired(d, now))
                .cloned()
                .collect(),
        }
    }

    /// Number of nodes with at least one tracked directive.
    pub fn tracked_node_count(&self) -> usize {
        self.by_node.lock().unwrap().len()
    }

    fn is_block_kind(k: SecurityDirectiveKind) -> bool {
        matches!(
            k,
            SecurityDirectiveKind::AvoidNode | SecurityDirectiveKind::QuarantineNode
        )
    }

    fn is_expired(d: &SecurityDirective, now: DateTime<Utc>) -> bool {
        match d.duration {
            Some(duration) => (d.issued_at + duration) <= now,
            None => false,
        }
    }
}

impl Default for MeshDirectiveStore {
    fn default() -> Self {
        Self::new()
    }
}

impl ISecurityDirectiveConsumer for MeshDirectiveStore {
    fn on_directive(&self, directive: &SecurityDirective) {
        if !directive.has_target() {
            return;
        }
        let node_id = directive.target_node_id.clone().unwrap();

        let mut map = self.by_node.lock().unwrap();
        if directive.kind == SecurityDirectiveKind::ReleaseNode {
            // Release lifts every Avoid/Quarantine for the node.
            map.remove(&node_id);
            return;
        }
        map.entry(node_id).or_default().push(directive.clone());
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MeshSecurityGate
// ─────────────────────────────────────────────────────────────────────────────

/// Decision returned from [`MeshSecurityGate::decide`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct GateDecision {
    pub is_blocked: bool,
    pub reason: String,
}

impl GateDecision {
    /// Allow with no reason text.
    pub fn allowed() -> Self {
        Self {
            is_blocked: false,
            reason: String::new(),
        }
    }

    fn blocked(reason: String) -> Self {
        Self {
            is_blocked: true,
            reason,
        }
    }
}

/// Error returned by [`MeshSecurityGate::enforce`] when the mesh has issued a
/// block directive against the requesting id. Mirrors the C#
/// `MeshSecurityBlockedException`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MeshSecurityBlockedError {
    pub blocked_id: String,
    pub reason: String,
}

impl std::fmt::Display for MeshSecurityBlockedError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "Mesh has blocked '{}': {}", self.blocked_id, self.reason)
    }
}

impl std::error::Error for MeshSecurityBlockedError {}

/// Query surface for "is this user/node currently blocked by the mesh?" Backed by
/// a [`MeshDirectiveStore`]. Separating the gate from the store lets consumers
/// hold the query view without the directive-write surface.
pub struct MeshSecurityGate {
    store: Arc<MeshDirectiveStore>,
}

impl MeshSecurityGate {
    pub fn new(store: Arc<MeshDirectiveStore>) -> Self {
        Self { store }
    }

    /// Returns a single-shot decision for the given user/node id. The reason text
    /// comes from the most recent active block directive.
    pub fn decide(&self, user_or_node_id: &str) -> GateDecision {
        if user_or_node_id.trim().is_empty() {
            return GateDecision::allowed();
        }
        let (blocked, reason) = self.store.is_blocked(user_or_node_id);
        if blocked {
            GateDecision::blocked(reason)
        } else {
            GateDecision::allowed()
        }
    }

    /// Returns `Err(MeshSecurityBlockedError)` when a request from a blocked id
    /// would proceed; `Ok(())` otherwise. Use as a one-line guard at the top of a
    /// method.
    pub fn enforce(&self, user_or_node_id: &str) -> Result<(), MeshSecurityBlockedError> {
        let decision = self.decide(user_or_node_id);
        if decision.is_blocked {
            Err(MeshSecurityBlockedError {
                blocked_id: user_or_node_id.to_string(),
                reason: decision.reason,
            })
        } else {
            Ok(())
        }
    }
}
