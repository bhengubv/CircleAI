//! aether::telemetry — Rust port of `CircleAI.Aether/IAetherTelemetry.cs`.
//!
//! Contract 1 — Telemetry. Aether publishes; BhenguAI subscribes. Aether never
//! calls into BhenguAI. External Aether adopters can implement
//! [`IAetherTelemetry`] without pulling in any AI dependency.
//!
//! The C# `IDisposable` returned by `Subscribe` becomes [`TelemetrySubscription`],
//! a drop-based handle: dropping it (or calling [`TelemetrySubscription::unsubscribe`])
//! removes exactly that observer. `NullAetherTelemetry` is the no-op feed;
//! `InMemoryAetherTelemetry` is a working synchronous fan-out publisher used by
//! tests and by any host that raises Aether events in-process.

use std::sync::{Arc, Mutex};

use super::events::{
    AetherNetworkEvent, AetherNodeEvent, AetherRouteEvent, AetherSecurityEvent,
    AetherTransportEvent,
};

// ─────────────────────────────────────────────────────────────────────────────
// Observer + telemetry traits
// ─────────────────────────────────────────────────────────────────────────────

/// Receives events emitted by Aether. Implement this to react to mesh activity —
/// nodes, transports, routes, security signals, and topology.
pub trait IAetherTelemetryObserver: Send + Sync {
    fn on_node_event(&self, e: &AetherNodeEvent);
    fn on_transport_event(&self, e: &AetherTransportEvent);
    fn on_route_event(&self, e: &AetherRouteEvent);
    fn on_security_event(&self, e: &AetherSecurityEvent);
    fn on_network_event(&self, e: &AetherNetworkEvent);
}

/// The outward-facing telemetry surface of Aether. The AI Security Layer and any
/// other BhenguAI component subscribes here. Aether owns this interface and
/// publishes; consumers subscribe and dispose.
pub trait IAetherTelemetry: Send + Sync {
    /// Subscribe to all Aether telemetry events. Drop the returned handle to
    /// unsubscribe.
    fn subscribe(&self, observer: Arc<dyn IAetherTelemetryObserver>) -> TelemetrySubscription;
}

// ─────────────────────────────────────────────────────────────────────────────
// Subscription handle
// ─────────────────────────────────────────────────────────────────────────────

/// Unsubscribe handle. Dropping it removes the associated observer from its
/// telemetry source. Mirrors the C# `IDisposable` returned by `Subscribe`.
pub struct TelemetrySubscription {
    remover: Option<Box<dyn FnOnce() + Send + Sync>>,
}

impl TelemetrySubscription {
    fn new(remover: impl FnOnce() + Send + Sync + 'static) -> Self {
        Self {
            remover: Some(Box::new(remover)),
        }
    }

    /// Builds a subscription from an arbitrary unsubscribe closure. Used by
    /// adapters (e.g. `AetherNetTelemetryAdapter`) that need to own a downstream
    /// subscription and release it when this handle drops.
    pub fn from_remover(remover: impl FnOnce() + Send + Sync + 'static) -> Self {
        Self::new(remover)
    }

    /// A subscription that does nothing on drop (used by [`NullAetherTelemetry`]).
    pub fn noop() -> Self {
        Self { remover: None }
    }

    /// Explicit unsubscribe (equivalent to dropping; idempotent).
    pub fn unsubscribe(mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}

impl Drop for TelemetrySubscription {
    fn drop(&mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// NullAetherTelemetry
// ─────────────────────────────────────────────────────────────────────────────

/// No-op telemetry — useful for unit tests and environments where Aether is
/// absent. `subscribe` returns a no-op handle; no events are emitted.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullAetherTelemetry;

impl NullAetherTelemetry {
    pub fn new() -> Self {
        Self
    }
}

impl IAetherTelemetry for NullAetherTelemetry {
    fn subscribe(&self, _observer: Arc<dyn IAetherTelemetryObserver>) -> TelemetrySubscription {
        // C# `ArgumentNullException.ThrowIfNull(observer)` is unrepresentable
        // in a `&Arc<T>` signature — the value is guaranteed non-null.
        TelemetrySubscription::noop()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryAetherTelemetry — working synchronous fan-out publisher
// ─────────────────────────────────────────────────────────────────────────────

/// In-process telemetry publisher. Any host component that raises Aether events
/// (or a test) publishes here; every current subscriber's matching callback
/// fires synchronously. Subscribe/unsubscribe/publish are all thread-safe.
///
/// A snapshot of the observer list is taken under the lock and callbacks fire
/// outside it, so a slow observer cannot stall a publish and an observer that
/// re-enters the publisher cannot self-deadlock.
#[derive(Default)]
pub struct InMemoryAetherTelemetry {
    observers: Arc<Mutex<Vec<(u64, Arc<dyn IAetherTelemetryObserver>)>>>,
    next_id: Mutex<u64>,
}

impl InMemoryAetherTelemetry {
    /// Returns an empty publisher.
    pub fn new() -> Self {
        Self {
            observers: Arc::new(Mutex::new(Vec::new())),
            next_id: Mutex::new(0),
        }
    }

    /// Number of currently active subscribers. Useful in tests.
    pub fn subscriber_count(&self) -> usize {
        self.observers.lock().unwrap().len()
    }

    fn snapshot(&self) -> Vec<Arc<dyn IAetherTelemetryObserver>> {
        let guard = self.observers.lock().unwrap();
        guard.iter().map(|(_, o)| Arc::clone(o)).collect()
    }

    /// Publish a node event to all subscribers.
    pub fn publish_node_event(&self, e: &AetherNodeEvent) {
        for o in self.snapshot() {
            o.on_node_event(e);
        }
    }

    /// Publish a transport event to all subscribers.
    pub fn publish_transport_event(&self, e: &AetherTransportEvent) {
        for o in self.snapshot() {
            o.on_transport_event(e);
        }
    }

    /// Publish a route event to all subscribers.
    pub fn publish_route_event(&self, e: &AetherRouteEvent) {
        for o in self.snapshot() {
            o.on_route_event(e);
        }
    }

    /// Publish a security event to all subscribers.
    pub fn publish_security_event(&self, e: &AetherSecurityEvent) {
        for o in self.snapshot() {
            o.on_security_event(e);
        }
    }

    /// Publish a network event to all subscribers.
    pub fn publish_network_event(&self, e: &AetherNetworkEvent) {
        for o in self.snapshot() {
            o.on_network_event(e);
        }
    }
}

impl IAetherTelemetry for InMemoryAetherTelemetry {
    fn subscribe(&self, observer: Arc<dyn IAetherTelemetryObserver>) -> TelemetrySubscription {
        let id = {
            let mut n = self.next_id.lock().unwrap();
            let id = *n;
            *n += 1;
            id
        };
        self.observers.lock().unwrap().push((id, observer));
        let observers = Arc::clone(&self.observers);
        TelemetrySubscription::new(move || {
            observers.lock().unwrap().retain(|(oid, _)| *oid != id);
        })
    }
}
