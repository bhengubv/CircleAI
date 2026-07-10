//! aether::security_layer — Rust port of `CircleAI.Aether/IAISecurityLayer.cs`.
//!
//! Contract 4 — Security Layer. BhenguAI reasons over Aether telemetry and
//! publishes [`SecurityDirective`]s. Aether's policy engine consumes those
//! directives via [`ISecurityDirectiveConsumer`] — Aether never calls into
//! BhenguAI directly. The boundary is strictly one-way, and adoption of a
//! directive is always the policy engine's decision.
//!
//! The C# surface is `Task`-based; the Rust port is sync. [`IAISecurityLayer`]'s
//! lifecycle wires the layer to an [`IAetherTelemetry`] feed.
//! [`InMemoryAISecurityLayer`] is the self-contained working implementation:
//! it subscribes to the telemetry feed, tracks per-node threat from security
//! events, publishes directives when a node crosses High/Critical, and reports
//! an aggregate [`SecurityPosture`].

use std::collections::HashMap;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Duration, Utc};
use serde::{Deserialize, Serialize};

use super::events::{
    AetherNetworkEvent, AetherNodeEvent, AetherRouteEvent, AetherSecurityEvent, AetherThreatLevel,
    AetherTransportEvent,
};
use super::telemetry::{IAetherTelemetry, IAetherTelemetryObserver, TelemetrySubscription};

// ─────────────────────────────────────────────────────────────────────────────
// SecurityDirectiveKind
// ─────────────────────────────────────────────────────────────────────────────

/// The action BhenguAI is recommending to Aether's policy engine. Ordinals
/// follow the C# declaration order.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash, Serialize, Deserialize)]
#[repr(u8)]
pub enum SecurityDirectiveKind {
    /// Adjust the recorded trust score for a node.
    UpdateNodeTrust = 0,
    /// Exclude the node from routing decisions (soft block).
    AvoidNode = 1,
    /// Hard block — no traffic to or from the node until released.
    QuarantineNode = 2,
    /// Lift an AvoidNode or QuarantineNode directive.
    ReleaseNode = 3,
    /// Request that the user re-authenticates before a sensitive operation.
    RequestReauth = 4,
    /// Increase telemetry verbosity for the target node.
    ElevateMonitoring = 5,
}

// ─────────────────────────────────────────────────────────────────────────────
// SecurityDirective
// ─────────────────────────────────────────────────────────────────────────────

/// An instruction published by the AI Security Layer to Aether's policy engine.
/// Aether is never required to honour a directive — adoption is a per-deployment
/// policy decision.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SecurityDirective {
    pub kind: SecurityDirectiveKind,
    pub target_node_id: Option<String>,
    pub trust_score_override: Option<f64>,
    pub threat_level: AetherThreatLevel,
    pub reason: String,
    pub duration: Option<Duration>,
    pub issued_at: DateTime<Utc>,
}

impl SecurityDirective {
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        kind: SecurityDirectiveKind,
        target_node_id: Option<String>,
        trust_score_override: Option<f64>,
        threat_level: AetherThreatLevel,
        reason: impl Into<String>,
        duration: Option<Duration>,
        issued_at: DateTime<Utc>,
    ) -> Self {
        Self {
            kind,
            target_node_id,
            trust_score_override,
            threat_level,
            reason: reason.into(),
            duration,
            issued_at,
        }
    }

    /// True when the directive targets a specific node.
    pub fn has_target(&self) -> bool {
        self.target_node_id
            .as_deref()
            .map(|s| !s.trim().is_empty())
            .unwrap_or(false)
    }

    /// True when `duration` is `None` — the directive has no automatic expiry.
    pub fn is_permanent(&self) -> bool {
        self.duration.is_none()
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// SecurityPosture
// ─────────────────────────────────────────────────────────────────────────────

/// Point-in-time summary of the AI Security Layer's current posture.
#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
pub struct SecurityPosture {
    pub overall_threat_level: AetherThreatLevel,
    pub quarantined_node_count: i32,
    pub monitored_node_count: i32,
    pub is_active: bool,
    pub assessed_at: DateTime<Utc>,
}

impl SecurityPosture {
    pub fn new(
        overall_threat_level: AetherThreatLevel,
        quarantined_node_count: i32,
        monitored_node_count: i32,
        is_active: bool,
        assessed_at: DateTime<Utc>,
    ) -> Self {
        Self {
            overall_threat_level,
            quarantined_node_count,
            monitored_node_count,
            is_active,
            assessed_at,
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Consumer + layer traits
// ─────────────────────────────────────────────────────────────────────────────

/// Receives security directives from the AI Security Layer. Implement this on
/// Aether's policy engine to participate in AI-guided security decisions.
pub trait ISecurityDirectiveConsumer: Send + Sync {
    /// Called each time BhenguAI issues a security directive. Implementations
    /// decide whether and how to honour it.
    fn on_directive(&self, directive: &SecurityDirective);
}

/// The AI Security Layer contract. BhenguAI implements this by subscribing to an
/// [`IAetherTelemetry`] feed and producing [`SecurityDirective`] outputs consumed
/// by Aether's policy engine via [`ISecurityDirectiveConsumer`].
pub trait IAISecurityLayer: Send + Sync {
    /// Wire the security layer to an Aether telemetry feed and begin processing
    /// events.
    fn start(&self, telemetry: &dyn IAetherTelemetry);

    /// Stop processing and release all telemetry subscriptions.
    fn stop(&self);

    /// Subscribe a policy engine to receive security directives. Drop the
    /// returned handle to unsubscribe.
    fn subscribe_to_directives(
        &self,
        consumer: Arc<dyn ISecurityDirectiveConsumer>,
    ) -> DirectiveSubscription;

    /// Returns the current security posture snapshot.
    fn get_posture(&self) -> SecurityPosture;
}

// ─────────────────────────────────────────────────────────────────────────────
// DirectiveSubscription — drop-based unsubscribe handle
// ─────────────────────────────────────────────────────────────────────────────

/// Unsubscribe handle for [`IAISecurityLayer::subscribe_to_directives`]. Dropping
/// it removes the associated consumer. Mirrors the C# `IDisposable`.
pub struct DirectiveSubscription {
    remover: Option<Box<dyn FnOnce() + Send + Sync>>,
}

impl DirectiveSubscription {
    fn new(remover: impl FnOnce() + Send + Sync + 'static) -> Self {
        Self {
            remover: Some(Box::new(remover)),
        }
    }

    /// Builds a subscription from an arbitrary unsubscribe closure. Used by
    /// bridges (e.g. `AetherSecurityBridge`) that own a downstream peer
    /// subscription and release it when this handle drops.
    pub fn from_remover(remover: impl FnOnce() + Send + Sync + 'static) -> Self {
        Self::new(remover)
    }

    /// Explicit unsubscribe (equivalent to dropping; idempotent).
    pub fn unsubscribe(mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}

impl Drop for DirectiveSubscription {
    fn drop(&mut self) {
        if let Some(r) = self.remover.take() {
            r();
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DirectivePublisher — fan-out to consumers
// ─────────────────────────────────────────────────────────────────────────────

/// Fan-out publisher for [`SecurityDirective`]s. A snapshot is taken under the
/// lock; callbacks fire outside it, so a slow consumer can't stall a publish and
/// a consumer that re-enters the publisher can't self-deadlock.
#[derive(Default)]
struct DirectivePublisher {
    consumers: Arc<Mutex<Vec<(u64, Arc<dyn ISecurityDirectiveConsumer>)>>>,
    next_id: Mutex<u64>,
}

impl DirectivePublisher {
    fn new() -> Self {
        Self {
            consumers: Arc::new(Mutex::new(Vec::new())),
            next_id: Mutex::new(0),
        }
    }

    fn subscribe(&self, consumer: Arc<dyn ISecurityDirectiveConsumer>) -> DirectiveSubscription {
        let id = {
            let mut n = self.next_id.lock().unwrap();
            let id = *n;
            *n += 1;
            id
        };
        self.consumers.lock().unwrap().push((id, consumer));
        let consumers = Arc::clone(&self.consumers);
        DirectiveSubscription::new(move || {
            consumers.lock().unwrap().retain(|(cid, _)| *cid != id);
        })
    }

    fn publish(&self, directive: &SecurityDirective) {
        let snapshot: Vec<Arc<dyn ISecurityDirectiveConsumer>> = {
            let guard = self.consumers.lock().unwrap();
            guard.iter().map(|(_, c)| Arc::clone(c)).collect()
        };
        for c in snapshot {
            c.on_directive(directive);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryAISecurityLayer — self-contained working implementation
// ─────────────────────────────────────────────────────────────────────────────

/// Per-node worst-observed threat, tracked from security events.
#[derive(Debug, Clone, Copy)]
struct NodeThreat {
    level: AetherThreatLevel,
}

/// A complete, self-contained [`IAISecurityLayer`]. Subscribes to an
/// [`IAetherTelemetry`] feed; each [`AetherSecurityEvent`] raises the node's
/// tracked threat to the worst level seen. Crossing into `High` emits an
/// `AvoidNode` directive; `Critical` emits `QuarantineNode`. A departing node
/// (`AetherNodeEvent::is_exit`) clears its tracked threat. Posture aggregates
/// the current node set.
///
/// The shared state lives behind an [`Arc`] so the telemetry observer (also
/// holding that [`Arc`]) and the public query surface see the same data.
pub struct InMemoryAISecurityLayer {
    shared: Arc<LayerShared>,
    subscription: Mutex<Option<TelemetrySubscription>>,
}

struct LayerShared {
    threats: Mutex<HashMap<String, NodeThreat>>,
    publisher: DirectivePublisher,
    active: AtomicBool,
}

impl LayerShared {
    /// Applies one security event: raises the node's worst-seen threat and, on a
    /// transition into High/Critical, emits the matching directive.
    fn apply_security_event(&self, e: &AetherSecurityEvent) {
        let (previous, current) = {
            let mut map = self.threats.lock().unwrap();
            let entry = map.entry(e.node_id.clone()).or_insert(NodeThreat {
                level: AetherThreatLevel::None,
            });
            let previous = entry.level;
            if e.threat_level > entry.level {
                entry.level = e.threat_level;
            }
            (previous, entry.level)
        };

        // Emit at most one directive, on the transition into a blocking band.
        if current >= AetherThreatLevel::Critical && previous < AetherThreatLevel::Critical {
            self.publisher.publish(&SecurityDirective::new(
                SecurityDirectiveKind::QuarantineNode,
                Some(e.node_id.clone()),
                None,
                current,
                e.description.clone(),
                None,
                e.occurred_at,
            ));
        } else if current >= AetherThreatLevel::High && previous < AetherThreatLevel::High {
            self.publisher.publish(&SecurityDirective::new(
                SecurityDirectiveKind::AvoidNode,
                Some(e.node_id.clone()),
                None,
                current,
                e.description.clone(),
                None,
                e.occurred_at,
            ));
        } else if current >= AetherThreatLevel::Medium && previous < AetherThreatLevel::Medium {
            self.publisher.publish(&SecurityDirective::new(
                SecurityDirectiveKind::ElevateMonitoring,
                Some(e.node_id.clone()),
                None,
                current,
                e.description.clone(),
                None,
                e.occurred_at,
            ));
        }
    }

    fn clear_node(&self, node_id: &str) {
        self.threats.lock().unwrap().remove(node_id);
    }
}

impl InMemoryAISecurityLayer {
    /// Creates an idle layer. Call [`IAISecurityLayer::start`] to wire it to a
    /// telemetry feed.
    pub fn new() -> Self {
        Self {
            shared: Arc::new(LayerShared {
                threats: Mutex::new(HashMap::new()),
                publisher: DirectivePublisher::new(),
                active: AtomicBool::new(false),
            }),
            subscription: Mutex::new(None),
        }
    }

    /// Whether the layer is currently active.
    pub fn is_active(&self) -> bool {
        self.shared.active.load(Ordering::SeqCst)
    }
}

impl Default for InMemoryAISecurityLayer {
    fn default() -> Self {
        Self::new()
    }
}

/// Bridges telemetry callbacks into the shared layer state.
struct LayerObserver {
    shared: Arc<LayerShared>,
}

impl IAetherTelemetryObserver for LayerObserver {
    fn on_node_event(&self, e: &AetherNodeEvent) {
        if e.is_exit() {
            self.shared.clear_node(&e.node_id);
        }
    }
    fn on_transport_event(&self, _e: &AetherTransportEvent) {}
    fn on_route_event(&self, _e: &AetherRouteEvent) {}
    fn on_security_event(&self, e: &AetherSecurityEvent) {
        self.shared.apply_security_event(e);
    }
    fn on_network_event(&self, _e: &AetherNetworkEvent) {}
}

impl IAISecurityLayer for InMemoryAISecurityLayer {
    fn start(&self, telemetry: &dyn IAetherTelemetry) {
        // Subscribe SYNCHRONOUSLY before marking active, so no security event
        // published immediately after start can be missed.
        let observer: Arc<dyn IAetherTelemetryObserver> = Arc::new(LayerObserver {
            shared: Arc::clone(&self.shared),
        });
        let sub = telemetry.subscribe(observer);
        *self.subscription.lock().unwrap() = Some(sub);
        self.shared.active.store(true, Ordering::SeqCst);
    }

    fn stop(&self) {
        // Drop the subscription (unhooks the observer), then mark inactive.
        *self.subscription.lock().unwrap() = None;
        self.shared.active.store(false, Ordering::SeqCst);
    }

    fn subscribe_to_directives(
        &self,
        consumer: Arc<dyn ISecurityDirectiveConsumer>,
    ) -> DirectiveSubscription {
        self.shared.publisher.subscribe(consumer)
    }

    fn get_posture(&self) -> SecurityPosture {
        let map = self.shared.threats.lock().unwrap();
        let mut worst = AetherThreatLevel::None;
        let mut quarantined = 0i32;
        let mut monitored = 0i32;
        for t in map.values() {
            if t.level > worst {
                worst = t.level;
            }
            match t.level {
                AetherThreatLevel::Critical => quarantined += 1,
                AetherThreatLevel::High | AetherThreatLevel::Medium => monitored += 1,
                _ => {}
            }
        }
        SecurityPosture::new(
            worst,
            quarantined,
            monitored,
            self.shared.active.load(Ordering::SeqCst),
            Utc::now(),
        )
    }
}
