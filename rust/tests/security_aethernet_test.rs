//! security_aethernet_test.rs
//!
//! Ports `CircleAI.Security.AetherNet/*.cs`: the Aether ↔ Peer mapper, the
//! `AetherSecurityBridge` (IAISecurityLayer over SecurityLayerService fed by an
//! Aether telemetry feed), the `AetherIntelligenceAdapter`, the `MeshDirectiveStore`
//! (directive sink + block query + lazy expiry + Release semantics) and its
//! `MeshSecurityGate`, and the `MeshGatedCompanionSession` decorator.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};

use chrono::{DateTime, Duration, Utc};
use circle_ai::aether::{
    AetherSecurityEvent, AetherSecurityEventKind, AetherThreatLevel, IAISecurityLayer,
    IAetherIntelligence, IAetherTelemetry, ISecurityDirectiveConsumer, InMemoryAetherTelemetry,
    SecurityDirective, SecurityDirectiveKind,
};
use circle_ai::security::{
    DirectivePublisher, NodeTrustRegistry, PeerIntelligenceService, SecurityLayerService,
    SecurityOptions,
};
use circle_ai::security_aethernet::{
    to_peer_event_kind, to_security_directive_kind, AetherIntelligenceAdapter, AetherSecurityBridge,
    MeshDirectiveStore, MeshSecurityGate,
};
use circle_ai::security::{PeerDirectiveKind, PeerSecurityEventKind};

// ── Mapper ────────────────────────────────────────────────────────────────────

#[test]
fn mapper_event_kind_and_directive_kind() {
    assert_eq!(
        to_peer_event_kind(AetherSecurityEventKind::NodeAuthAttempt),
        PeerSecurityEventKind::AuthAttempt
    );
    assert_eq!(
        to_peer_event_kind(AetherSecurityEventKind::PrivilegeAttempt),
        PeerSecurityEventKind::PrivilegeAttempt
    );
    assert_eq!(
        to_security_directive_kind(PeerDirectiveKind::QuarantineNode),
        SecurityDirectiveKind::QuarantineNode
    );
    // Peer set has no UpdateNodeTrust; ElevateMonitoring is the C# default arm.
    assert_eq!(
        to_security_directive_kind(PeerDirectiveKind::ElevateMonitoring),
        SecurityDirectiveKind::ElevateMonitoring
    );
}

// ── AetherSecurityBridge ─────────────────────────────────────────────────────

fn build_layer() -> Arc<SecurityLayerService> {
    let opts = SecurityOptions::default();
    let registry = Arc::new(NodeTrustRegistry::new(opts.clone()));
    let publisher = Arc::new(DirectivePublisher::new());
    Arc::new(SecurityLayerService::new(registry, opts, publisher))
}

#[derive(Default)]
struct RecordingConsumer {
    got: Mutex<Vec<SecurityDirective>>,
}
impl ISecurityDirectiveConsumer for RecordingConsumer {
    fn on_directive(&self, d: &SecurityDirective) {
        self.got.lock().unwrap().push(d.clone());
    }
}

fn crit_event(node: &str) -> AetherSecurityEvent {
    AetherSecurityEvent::new(
        node,
        AetherSecurityEventKind::IntrusionSignal,
        AetherThreatLevel::Critical,
        "intrusion",
        BTreeMap::new(),
        Utc::now(),
    )
}

#[test]
fn bridge_feeds_events_and_issues_translated_directives() {
    let layer = build_layer();
    let bridge = AetherSecurityBridge::new(layer.clone());
    let consumer = Arc::new(RecordingConsumer::default());
    let _sub = bridge.subscribe_to_directives(consumer.clone());

    let telemetry = InMemoryAetherTelemetry::new();
    bridge.start(&telemetry);

    // A stream of Critical intrusion events degrades the node's trust across the
    // quarantine threshold, which makes the peer layer issue a directive that the
    // bridge translates back to the Aether SecurityDirective shape.
    for _ in 0..12 {
        telemetry.publish_security_event(&crit_event("badnode"));
    }

    let got = consumer.got.lock().unwrap();
    assert!(!got.is_empty(), "at least one directive should be issued");
    // The most severe directive issued targets badnode with a mapped Aether kind.
    let last = got.last().unwrap();
    assert_eq!(last.target_node_id.as_deref(), Some("badnode"));
    assert!(matches!(
        last.kind,
        SecurityDirectiveKind::AvoidNode
            | SecurityDirectiveKind::QuarantineNode
            | SecurityDirectiveKind::ElevateMonitoring
    ));
    // Trust-score override is carried through from the peer directive.
    assert!(last.trust_score_override.is_some());
}

#[test]
fn bridge_posture_reflects_layer_state() {
    let layer = build_layer();
    let bridge = AetherSecurityBridge::new(layer.clone());
    let telemetry = InMemoryAetherTelemetry::new();
    bridge.start(&telemetry);

    let posture = bridge.get_posture();
    assert!(posture.is_active);
    // No events yet → clean.
    assert_eq!(posture.overall_threat_level, AetherThreatLevel::None);

    bridge.stop();
    assert!(!bridge.get_posture().is_active);
}

#[test]
fn bridge_start_subscribes_synchronously() {
    let layer = build_layer();
    let bridge = AetherSecurityBridge::new(layer.clone());
    let telemetry = InMemoryAetherTelemetry::new();
    bridge.start(&telemetry);
    assert_eq!(telemetry.subscriber_count(), 1);
    bridge.stop();
    assert_eq!(telemetry.subscriber_count(), 0);
}

// ── AetherIntelligenceAdapter ────────────────────────────────────────────────

#[test]
fn intelligence_adapter_maps_peer_results() {
    let opts = SecurityOptions::default();
    let registry = Arc::new(NodeTrustRegistry::new(opts.clone()));
    // Degrade a node so the intelligence surface has something to report.
    let ev = circle_ai::security::PeerSecurityEvent::new(
        "n1",
        PeerSecurityEventKind::IntrusionSignal,
        circle_ai::security::PeerThreatLevel::Critical,
        "hit",
        "aether",
        Utc::now(),
    );
    registry.apply_degradation(&ev, 0.9); // n1 → ~0.1
    let inner = Arc::new(PeerIntelligenceService::new(registry, opts));
    let adapter = AetherIntelligenceAdapter::new(inner);

    let health = adapter.get_network_health();
    assert_eq!(health.trusted_node_count + health.suspicious_node_count >= 1, true);

    let assess = adapter.assess_threat("n1");
    assert_eq!(assess.node_id, "n1");
    assert_eq!(assess.level, AetherThreatLevel::Critical);

    let advice = adapter.get_routing_advice("n1");
    assert_eq!(advice.destination_node_id, "n1");
    assert!(advice.avoid_nodes.contains(&"n1".to_string()));

    // The streaming drain surfaces the degradation update.
    let updates = adapter.stream_trust_scores();
    assert!(updates.iter().any(|u| u.node_id == "n1"));
}

// ── MeshDirectiveStore + MeshSecurityGate ────────────────────────────────────

fn avoid_directive(node: &str, reason: &str, at: DateTime<Utc>, dur: Option<Duration>) -> SecurityDirective {
    SecurityDirective::new(
        SecurityDirectiveKind::AvoidNode,
        Some(node.into()),
        None,
        AetherThreatLevel::High,
        reason,
        dur,
        at,
    )
}

#[test]
fn store_blocks_on_avoid_and_quarantine() {
    let store = MeshDirectiveStore::new();
    store.on_directive(&avoid_directive("n1", "sketchy", Utc::now(), None));
    let (blocked, reason) = store.is_blocked("n1");
    assert!(blocked);
    assert_eq!(reason, "sketchy");
    assert_eq!(store.tracked_node_count(), 1);
    // Unknown node is not blocked.
    assert!(!store.is_blocked("other").0);
}

#[test]
fn store_release_lifts_block() {
    let store = MeshDirectiveStore::new();
    store.on_directive(&avoid_directive("n1", "sketchy", Utc::now(), None));
    assert!(store.is_blocked("n1").0);

    let release = SecurityDirective::new(
        SecurityDirectiveKind::ReleaseNode,
        Some("n1".into()),
        None,
        AetherThreatLevel::None,
        "cleared",
        None,
        Utc::now(),
    );
    store.on_directive(&release);
    assert!(!store.is_blocked("n1").0);
    assert_eq!(store.tracked_node_count(), 0);
}

#[test]
fn store_targetless_directive_is_ignored() {
    let store = MeshDirectiveStore::new();
    let no_target = SecurityDirective::new(
        SecurityDirectiveKind::ElevateMonitoring,
        None,
        None,
        AetherThreatLevel::Medium,
        "global",
        None,
        Utc::now(),
    );
    store.on_directive(&no_target);
    assert_eq!(store.tracked_node_count(), 0);
}

#[test]
fn store_expires_directives_lazily() {
    // Fixed clock in the future so a short-duration directive is already expired.
    let base = Utc::now();
    let future = base + Duration::seconds(120);
    let store = MeshDirectiveStore::with_clock(Arc::new(move || future));
    // Issued at base with a 60s duration → expired by `future`.
    store.on_directive(&avoid_directive("n1", "temp", base, Some(Duration::seconds(60))));
    let (blocked, _) = store.is_blocked("n1");
    assert!(!blocked, "expired directive should not block");
    // Lazy sweep removed the empty node entry.
    assert_eq!(store.tracked_node_count(), 0);
    assert!(store.active_directives("n1").is_empty());
}

#[test]
fn store_latest_block_reason_wins() {
    let store = MeshDirectiveStore::new();
    let t0 = Utc::now();
    store.on_directive(&avoid_directive("n1", "first", t0, None));
    store.on_directive(&avoid_directive("n1", "second", t0 + Duration::seconds(5), None));
    let (_, reason) = store.is_blocked("n1");
    assert_eq!(reason, "second", "most recent block reason wins");
    assert_eq!(store.active_directives("n1").len(), 2);
}

#[test]
fn gate_decide_and_enforce() {
    let store = Arc::new(MeshDirectiveStore::new());
    store.on_directive(&avoid_directive("blocked", "nope", Utc::now(), None));
    let gate = MeshSecurityGate::new(store.clone());

    let d = gate.decide("blocked");
    assert!(d.is_blocked);
    assert_eq!(d.reason, "nope");

    let ok = gate.decide("clean");
    assert!(!ok.is_blocked);

    // enforce returns Err for a blocked id, Ok otherwise.
    let err = gate.enforce("blocked").unwrap_err();
    assert_eq!(err.blocked_id, "blocked");
    assert!(gate.enforce("clean").is_ok());
    // Blank id is always allowed.
    assert!(gate.enforce("  ").is_ok());
}
