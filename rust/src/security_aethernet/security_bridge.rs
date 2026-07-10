//! security_aethernet::security_bridge — Rust port of
//! `CircleAI.Security.AetherNet/AetherSecurityBridge.cs`.
//!
//! Bridges an Aether telemetry feed ([`IAetherTelemetry`] /
//! [`IAetherTelemetryObserver`]) into the transport-agnostic
//! [`SecurityLayerService`] and implements [`IAISecurityLayer`] so it drops in
//! as a replacement for the old Aether-coupled layer.
//!
//! Responsibilities (pure translation — the [`SecurityLayerService`] does all
//! the reasoning):
//!   1. `start` subscribes to the telemetry feed and starts the layer.
//!   2. Each [`AetherSecurityEvent`] becomes a [`PeerSecurityEvent`] handed to
//!      the layer (transport id `"aether"`); a departing node calls
//!      `handle_peer_left`.
//!   3. Adapts an Aether [`ISecurityDirectiveConsumer`] ↔ a peer
//!      [`IPeerDirectiveConsumer`].
//!   4. Maps [`PeerSecurityPosture`] → [`SecurityPosture`].

use std::sync::{Arc, Mutex};

use crate::aether::events::{
    AetherNetworkEvent, AetherNodeEvent, AetherRouteEvent, AetherSecurityEvent,
    AetherTransportEvent,
};
use crate::aether::security_layer::{
    DirectiveSubscription, IAISecurityLayer, ISecurityDirectiveConsumer, SecurityDirective,
    SecurityPosture,
};
use crate::aether::telemetry::{
    IAetherTelemetry, IAetherTelemetryObserver, TelemetrySubscription,
};
use crate::security::{
    DirectiveSubscription as PeerDirectiveSubscription, IPeerDirectiveConsumer, IPeerSecurityLayer,
    PeerDirective, PeerSecurityEvent, SecurityLayerService,
};

use super::mapper::{to_aether_threat_level, to_peer_event_kind, to_peer_threat_level};

/// Connects an Aether mesh telemetry feed to the transport-agnostic
/// [`SecurityLayerService`]. Implements [`IAISecurityLayer`].
pub struct AetherSecurityBridge {
    layer: Arc<SecurityLayerService>,
    telemetry_subscription: Mutex<Option<TelemetrySubscription>>,
}

impl AetherSecurityBridge {
    /// Initialises the bridge over an existing transport-agnostic security layer.
    /// The layer must be constructed but need not be started yet.
    pub fn new(layer: Arc<SecurityLayerService>) -> Self {
        Self {
            layer,
            telemetry_subscription: Mutex::new(None),
        }
    }
}

impl IAISecurityLayer for AetherSecurityBridge {
    fn start(&self, telemetry: &dyn IAetherTelemetry) {
        // Subscribe SYNCHRONOUSLY before starting the layer so no security event
        // published immediately after start is lost.
        let observer: Arc<dyn IAetherTelemetryObserver> = Arc::new(Observer {
            layer: Arc::clone(&self.layer),
        });
        let sub = telemetry.subscribe(observer);
        *self.telemetry_subscription.lock().unwrap() = Some(sub);
        self.layer.start();
    }

    fn stop(&self) {
        // Drop the telemetry subscription (unhooks the observer), then stop.
        *self.telemetry_subscription.lock().unwrap() = None;
        self.layer.stop();
    }

    fn subscribe_to_directives(
        &self,
        consumer: Arc<dyn ISecurityDirectiveConsumer>,
    ) -> DirectiveSubscription {
        // Wrap the Aether consumer in an adapter that translates PeerDirective →
        // SecurityDirective, then subscribe it to the peer layer. Keep the peer
        // subscription alive for the lifetime of the returned Aether handle.
        let adapter: Arc<dyn IPeerDirectiveConsumer> = Arc::new(DirectiveAdapter { consumer });
        let peer_sub = self.layer.subscribe_to_directives(adapter);
        wrap_peer_subscription(peer_sub)
    }

    fn get_posture(&self) -> SecurityPosture {
        let p = self.layer.get_posture();
        SecurityPosture::new(
            to_aether_threat_level(p.overall_threat_level),
            p.quarantined_peer_count,
            p.monitored_peer_count,
            p.is_active,
            p.generated_at,
        )
    }
}

/// Wraps a peer directive subscription in an Aether [`DirectiveSubscription`] so
/// the outer handle's lifetime governs the inner one.
fn wrap_peer_subscription(peer_sub: PeerDirectiveSubscription) -> DirectiveSubscription {
    let holder = Arc::new(Mutex::new(Some(peer_sub)));
    DirectiveSubscription::from_remover(move || {
        holder.lock().unwrap().take();
    })
}

// ─── Telemetry observer ──────────────────────────────────────────────────────

/// Translates Aether telemetry into peer events for the security layer.
struct Observer {
    layer: Arc<SecurityLayerService>,
}

impl IAetherTelemetryObserver for Observer {
    fn on_security_event(&self, e: &AetherSecurityEvent) {
        let peer = PeerSecurityEvent::new(
            e.node_id.clone(),
            to_peer_event_kind(e.kind),
            to_peer_threat_level(e.threat_level),
            e.description.clone(),
            "aether",
            e.occurred_at,
        );
        self.layer.handle_peer_event(&peer);
    }

    fn on_node_event(&self, e: &AetherNodeEvent) {
        if e.is_exit() {
            self.layer.handle_peer_left(&e.node_id);
        }
    }

    // Not relevant to security scoring — ignore.
    fn on_transport_event(&self, _e: &AetherTransportEvent) {}
    fn on_route_event(&self, _e: &AetherRouteEvent) {}
    fn on_network_event(&self, _e: &AetherNetworkEvent) {}
}

// ─── Directive adapter ───────────────────────────────────────────────────────

/// Adapts an Aether [`ISecurityDirectiveConsumer`] so it can receive
/// [`PeerDirective`]s, translating them back to [`SecurityDirective`] before
/// delivery.
struct DirectiveAdapter {
    consumer: Arc<dyn ISecurityDirectiveConsumer>,
}

impl IPeerDirectiveConsumer for DirectiveAdapter {
    fn on_directive(&self, directive: &PeerDirective) {
        let aether = SecurityDirective::new(
            super::mapper::to_security_directive_kind(directive.kind),
            Some(directive.target_node_id.clone()),
            Some(directive.trust_score),
            to_aether_threat_level(directive.threat_level),
            directive.reason.clone(),
            directive.duration,
            directive.issued_at,
        );
        self.consumer.on_directive(&aether);
    }
}
