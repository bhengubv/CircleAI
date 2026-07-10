//! networking::selector — Rust port of `ITransportSelector.cs`.
//!
//! Selects the best transport for a payload+context. The documented default
//! cascade (from the C# XML-doc) is:
//!
//! ```text
//! gRPC → WebSocket → HTTP → MQTT → TCP → WiFi → Bluetooth → NearLink → Aether → DTN → LocalStore
//! ```
//!
//!   * [`ITransportSelector`]        — the trait.
//!   * [`CascadeTransportSelector`]  — the default implementation. Filters the
//!     cascade by (a) the injected [`INetworkPolicy`], (b) the transports the
//!     [`NetworkContext`] reports available, then honours `force_transport` and
//!     `mesh_first`. Deterministic and allocation-light.

use std::sync::Arc;

use super::policy::{DefaultNetworkPolicy, INetworkPolicy};
use super::types::{NetworkContext, NetworkPayload, TransportKind};

// ─────────────────────────────────────────────────────────────────────────────
// ITransportSelector
// ─────────────────────────────────────────────────────────────────────────────

/// Selects the best transport for a payload+context.
pub trait ITransportSelector: Send + Sync {
    /// The single best transport for `payload` in `context`.
    fn select_best(&self, payload: &NetworkPayload, context: &NetworkContext) -> TransportKind;

    /// The ordered fallback cascade for `payload` in `context`, best first.
    fn get_cascade(
        &self,
        payload: &NetworkPayload,
        context: &NetworkContext,
    ) -> Vec<TransportKind>;
}

// ─────────────────────────────────────────────────────────────────────────────
// CascadeTransportSelector
// ─────────────────────────────────────────────────────────────────────────────

/// The canonical cascade order, cloud-preferring, exactly as documented on the
/// C# `ITransportSelector`.
pub const DEFAULT_CASCADE: [TransportKind; 11] = [
    TransportKind::Grpc,
    TransportKind::WebSocket,
    TransportKind::Http,
    TransportKind::Mqtt,
    TransportKind::Tcp,
    TransportKind::WiFi,
    TransportKind::Bluetooth,
    TransportKind::NearLink,
    TransportKind::Aether,
    TransportKind::Dtn,
    TransportKind::LocalStore,
];

/// Transports considered "mesh" for the `mesh_first` reordering: the local-radio
/// and Aether family. When a policy sets `mesh_first`, these are floated to the
/// front of the cascade (retaining their relative order), ahead of the cloud
/// transports.
const MESH_TRANSPORTS: [TransportKind; 4] = [
    TransportKind::WiFi,
    TransportKind::Bluetooth,
    TransportKind::NearLink,
    TransportKind::Aether,
];

/// Default [`ITransportSelector`]. Walks [`DEFAULT_CASCADE`], keeping transports
/// that are both policy-permitted and context-available; applies `force_transport`
/// and `mesh_first`. `LocalStore` is always a valid terminal fallback (the
/// offline queue), so it is never filtered out by availability.
pub struct CascadeTransportSelector {
    policy: Arc<dyn INetworkPolicy>,
}

impl CascadeTransportSelector {
    /// Uses the supplied policy.
    pub fn new(policy: Arc<dyn INetworkPolicy>) -> Self {
        Self { policy }
    }

    /// Uses [`DefaultNetworkPolicy`] (everything permitted).
    pub fn with_default_policy() -> Self {
        Self {
            policy: Arc::new(DefaultNetworkPolicy::INSTANCE),
        }
    }

    fn is_mesh(t: TransportKind) -> bool {
        MESH_TRANSPORTS.contains(&t)
    }

    /// `true` when `t` is usable for `payload` in `context`.
    ///
    /// A transport is usable when it is policy-permitted AND either (a) it is
    /// `LocalStore` (always usable — the offline queue), or (b) the context lists
    /// it as available. When the context reports *no* available transports at all
    /// we treat the cascade as unconstrained by availability (only policy gates),
    /// so a caller that does not populate `available_transports` still gets a
    /// sensible cascade rather than only `LocalStore`.
    fn is_usable(&self, t: TransportKind, payload: &NetworkPayload, context: &NetworkContext) -> bool {
        if !self.policy.permits(t, payload) {
            return false;
        }
        if t == TransportKind::LocalStore {
            return true;
        }
        if context.available_transports.is_empty() {
            return true;
        }
        context.available_transports.contains(&t)
    }

    /// Reorders `base` so mesh transports lead (keeping relative order), when the
    /// policy asks for mesh-first.
    fn apply_mesh_first(items: Vec<TransportKind>) -> Vec<TransportKind> {
        let mut mesh: Vec<TransportKind> = Vec::new();
        let mut rest: Vec<TransportKind> = Vec::new();
        for t in items {
            if Self::is_mesh(t) {
                mesh.push(t);
            } else {
                rest.push(t);
            }
        }
        mesh.extend(rest);
        mesh
    }
}

impl ITransportSelector for CascadeTransportSelector {
    fn select_best(&self, payload: &NetworkPayload, context: &NetworkContext) -> TransportKind {
        self.get_cascade(payload, context)
            .into_iter()
            .next()
            // The cascade always contains LocalStore, so this is unreachable in
            // practice; keep an explicit terminal fallback for total safety.
            .unwrap_or(TransportKind::LocalStore)
    }

    fn get_cascade(
        &self,
        payload: &NetworkPayload,
        context: &NetworkContext,
    ) -> Vec<TransportKind> {
        // A forced transport short-circuits everything (still constrained to be
        // policy-permitted; if the policy forbids it we fall through to LocalStore
        // so the payload is never silently dropped).
        if let Some(forced) = self.policy.force_transport() {
            if self.policy.permits(forced, payload) {
                if forced == TransportKind::LocalStore {
                    return vec![TransportKind::LocalStore];
                }
                return vec![forced, TransportKind::LocalStore];
            }
            return vec![TransportKind::LocalStore];
        }

        let mut cascade: Vec<TransportKind> = DEFAULT_CASCADE
            .iter()
            .copied()
            .filter(|&t| self.is_usable(t, payload, context))
            .collect();

        if self.policy.mesh_first() {
            cascade = Self::apply_mesh_first(cascade);
        }

        // Guarantee a terminal fallback even if a policy somehow filtered
        // LocalStore (it never permits-filters it, but be defensive).
        if !cascade.contains(&TransportKind::LocalStore) {
            cascade.push(TransportKind::LocalStore);
        }
        cascade
    }
}
