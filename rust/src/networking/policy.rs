//! networking::policy — Rust port of `INetworkPolicy.cs`, `DefaultNetworkPolicy.cs`,
//! and `NetworkPolicyBuilder.cs`.
//!
//! Policy rules applied *before* choosing a transport, e.g. "WiFi-only",
//! "mesh-first", "no cloud when roaming".
//!
//!   * [`INetworkPolicy`]        — the trait (`C# interface INetworkPolicy`).
//!   * [`DefaultNetworkPolicy`]  — permissive singleton: everything allowed,
//!                                 offline queue on.
//!   * [`NetworkPolicyBuilder`]  — fluent builder producing a [`BuiltPolicy`].

use std::collections::HashSet;

use super::types::{NetworkPayload, TransportKind};

// ─────────────────────────────────────────────────────────────────────────────
// INetworkPolicy
// ─────────────────────────────────────────────────────────────────────────────

/// Policy rules applied before choosing a transport.
pub trait INetworkPolicy: Send + Sync {
    /// Whether `transport` may carry `payload` under this policy.
    fn permits(&self, transport: TransportKind, payload: &NetworkPayload) -> bool;

    /// When `Some`, forces every payload onto this one transport.
    fn force_transport(&self) -> Option<TransportKind>;

    /// Prefer mesh transports ahead of cloud ones in the selection cascade.
    fn mesh_first(&self) -> bool;

    /// Whether payloads should be queued while offline rather than dropped.
    fn offline_queue_enabled(&self) -> bool;

    /// Whether cloud transports (HTTP/WS/gRPC/MQTT) are permitted at all.
    fn allow_cloud_transports(&self) -> bool;
}

// ─────────────────────────────────────────────────────────────────────────────
// DefaultNetworkPolicy
// ─────────────────────────────────────────────────────────────────────────────

/// Permissive default: all transports allowed, offline queue on. Port of the C#
/// `DefaultNetworkPolicy` sealed singleton (`DefaultNetworkPolicy.Instance`).
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct DefaultNetworkPolicy;

impl DefaultNetworkPolicy {
    /// The shared instance (mirrors `DefaultNetworkPolicy.Instance`). The type is
    /// zero-sized, so this is free.
    pub const INSTANCE: DefaultNetworkPolicy = DefaultNetworkPolicy;

    pub fn new() -> Self {
        DefaultNetworkPolicy
    }
}

impl INetworkPolicy for DefaultNetworkPolicy {
    fn permits(&self, _transport: TransportKind, _payload: &NetworkPayload) -> bool {
        true
    }
    fn force_transport(&self) -> Option<TransportKind> {
        None
    }
    fn mesh_first(&self) -> bool {
        false
    }
    fn offline_queue_enabled(&self) -> bool {
        true
    }
    fn allow_cloud_transports(&self) -> bool {
        true
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// NetworkPolicyBuilder → BuiltPolicy
// ─────────────────────────────────────────────────────────────────────────────

/// Fluent builder for an [`INetworkPolicy`]. Port of `NetworkPolicyBuilder`.
///
/// ```
/// # use circle_ai::networking::{NetworkPolicyBuilder, TransportKind, INetworkPolicy, NetworkPayload};
/// let policy = NetworkPolicyBuilder::new()
///     .mesh_first()
///     .no_cloud()
///     .allow(&[TransportKind::Aether, TransportKind::Bluetooth])
///     .build();
/// assert!(policy.mesh_first());
/// assert!(!policy.allow_cloud_transports());
/// ```
#[derive(Debug, Clone, Default)]
pub struct NetworkPolicyBuilder {
    allowed: HashSet<TransportKind>,
    mesh_first: bool,
    no_cloud: bool,
    queue_enabled: bool,
    force: Option<TransportKind>,
}

impl NetworkPolicyBuilder {
    /// A fresh builder. `queue_enabled` defaults to `true` (matching the C#
    /// field initialiser `_queueEnabled = true`).
    pub fn new() -> Self {
        Self {
            allowed: HashSet::new(),
            mesh_first: false,
            no_cloud: false,
            queue_enabled: true,
            force: None,
        }
    }

    /// Prefer mesh transports first.
    pub fn mesh_first(mut self) -> Self {
        self.mesh_first = true;
        self
    }

    /// Forbid cloud transports (HTTP/WebSocket/gRPC/MQTT).
    pub fn no_cloud(mut self) -> Self {
        self.no_cloud = true;
        self
    }

    /// Turn off the offline queue.
    pub fn disable_queue(mut self) -> Self {
        self.queue_enabled = false;
        self
    }

    /// Force every payload onto `t`.
    pub fn force(mut self, t: TransportKind) -> Self {
        self.force = Some(t);
        self
    }

    /// Add transports to the allow-list. Called with no kinds it is a no-op; with
    /// any kinds, only listed transports (that also pass the no-cloud filter) are
    /// permitted. Mirrors the C# `Allow(params TransportKind[])`.
    pub fn allow(mut self, kinds: &[TransportKind]) -> Self {
        for &k in kinds {
            self.allowed.insert(k);
        }
        self
    }

    /// Build the immutable policy. When the allow-list is empty, all transports
    /// are permitted (subject to the no-cloud filter) — matching the C#
    /// `allowed.Count > 0 ? ... : null`.
    pub fn build(self) -> BuiltPolicy {
        let allowed = if self.allowed.is_empty() {
            None
        } else {
            Some(self.allowed)
        };
        BuiltPolicy {
            allowed,
            mesh_first: self.mesh_first,
            no_cloud: self.no_cloud,
            queue_enabled: self.queue_enabled,
            force: self.force,
        }
    }
}

/// The concrete [`INetworkPolicy`] produced by [`NetworkPolicyBuilder::build`].
/// Port of the private `NetworkPolicyBuilder.Policy` sealed class.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct BuiltPolicy {
    allowed: Option<HashSet<TransportKind>>,
    mesh_first: bool,
    no_cloud: bool,
    queue_enabled: bool,
    force: Option<TransportKind>,
}

impl BuiltPolicy {
    /// A cloud transport is one of HTTP / WebSocket / gRPC / MQTT — the exact set
    /// the C# `Permits` short-circuits on when `no_cloud` is set.
    fn is_cloud(t: TransportKind) -> bool {
        matches!(
            t,
            TransportKind::Http
                | TransportKind::WebSocket
                | TransportKind::Grpc
                | TransportKind::Mqtt
        )
    }
}

impl INetworkPolicy for BuiltPolicy {
    fn permits(&self, transport: TransportKind, _payload: &NetworkPayload) -> bool {
        if self.no_cloud && Self::is_cloud(transport) {
            return false;
        }
        match &self.allowed {
            None => true,
            Some(set) => set.contains(&transport),
        }
    }
    fn force_transport(&self) -> Option<TransportKind> {
        self.force
    }
    fn mesh_first(&self) -> bool {
        self.mesh_first
    }
    fn offline_queue_enabled(&self) -> bool {
        self.queue_enabled
    }
    fn allow_cloud_transports(&self) -> bool {
        !self.no_cloud
    }
}
