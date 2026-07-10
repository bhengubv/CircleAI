//! aethernet_mesh_test.rs
//!
//! Ports `CircleAI.AetherNet/*.cs`: the mesh capability registry (upsert/remove/
//! list/find + staleness + case-insensitive model match + budget-descending
//! sort), the null broadcaster, the event translator's fold rules, and the
//! adapter family (context / telemetry / directive sink / inbound bridge / AI
//! provider) against the in-memory mesh boundary.

use std::sync::{Arc, Mutex};

use chrono::{Duration, Utc};
use circle_ai::aethernet::mesh_extensibility::{
    AetherNetNodeEvent, AetherNetNodeEventKind, AetherNetNodeHealth, AetherNetSecurityEvent,
    AetherNetSecurityEventKind, AetherNetThreatLevel, AetherNetTransportEvent,
    AetherNetTransportEventKind, AetherNetTransportKind,
};
use circle_ai::aethernet::{
    AetherNetContextAdapter, AetherNetDirectiveSink, AetherNetInboundDirectiveBridge,
    AetherNetTelemetryAdapter, AiThreatLevel, CircleAiAetherNetAiProvider, IAetherNetAiProvider,
    IAetherNetTelemetry, IMeshCapabilityBroadcaster, IMeshCapabilityRegistry,
    IMeshSecurityDirectiveConsumer, InMemoryMeshCapabilityRegistry, InMemoryMeshTelemetry,
    MeshPacket, MeshSecurityDirective, MeshSecurityDirectiveKind, NullMeshCapabilityBroadcaster,
    RecordingMeshDirectiveConsumer, CURRENT_PROTOCOL_VERSION,
};
use circle_ai::aether::{
    AetherInstallLevel, AetherThreatLevel, AetherVersion, IAetherContext, IAetherTelemetry,
    IAetherTelemetryObserver, ISecurityDirectiveConsumer, InMemoryAetherIntelligence,
    SecurityDirective, SecurityDirectiveKind,
};
use circle_ai::aethernet::MeshCapabilityAdvertisement;
use circle_ai::device::DeviceTier;

// ── Mesh capability registry ─────────────────────────────────────────────────

fn ad(peer: &str, model: &str, free_kv: i32, at: chrono::DateTime<Utc>) -> MeshCapabilityAdvertisement {
    MeshCapabilityAdvertisement::new(peer, model, free_kv, DeviceTier::Phone, 4096, at, None)
}

#[test]
fn registry_upsert_replaces_and_lists() {
    let reg = InMemoryMeshCapabilityRegistry::new();
    reg.upsert(ad("p1", "Qwen3-1.7B-MNN", 100, Utc::now()));
    reg.upsert(ad("p1", "Qwen3-1.7B-MNN", 250, Utc::now())); // replace
    let all = reg.list(None);
    assert_eq!(all.len(), 1);
    assert_eq!(all[0].free_kv_tokens, 250);
}

#[test]
fn registry_remove_is_idempotent() {
    let reg = InMemoryMeshCapabilityRegistry::new();
    reg.upsert(ad("p1", "m", 10, Utc::now()));
    assert!(reg.remove("p1"));
    assert!(!reg.remove("p1")); // already gone
    assert!(reg.list(None).is_empty());
}

#[test]
fn registry_find_is_case_insensitive_and_sorted_by_budget() {
    let reg = InMemoryMeshCapabilityRegistry::new();
    reg.upsert(ad("p1", "Qwen3-1.7B-MNN", 100, Utc::now()));
    reg.upsert(ad("p2", "qwen3-1.7b-mnn", 500, Utc::now())); // different casing
    reg.upsert(ad("p3", "Qwen3-1.7B-MNN", 300, Utc::now()));
    reg.upsert(ad("p4", "OtherModel", 999, Utc::now()));

    let hits = reg.find("QWEN3-1.7B-MNN", 0, None);
    let ids: Vec<&str> = hits.iter().map(|a| a.peer_id.as_str()).collect();
    // p4 excluded (different model); rest sorted by free_kv descending.
    assert_eq!(ids, vec!["p2", "p3", "p1"]);
}

#[test]
fn registry_find_filters_min_budget() {
    let reg = InMemoryMeshCapabilityRegistry::new();
    reg.upsert(ad("p1", "m", 50, Utc::now()));
    reg.upsert(ad("p2", "m", 200, Utc::now()));
    let hits = reg.find("m", 100, None);
    assert_eq!(hits.len(), 1);
    assert_eq!(hits[0].peer_id, "p2");
}

#[test]
fn registry_staleness_window_uses_clock() {
    let now = Utc::now();
    let clock_now = now;
    let reg = InMemoryMeshCapabilityRegistry::with_clock(Arc::new(move || clock_now));
    reg.upsert(ad("fresh", "m", 10, now - Duration::seconds(10)));
    reg.upsert(ad("stale", "m", 10, now - Duration::seconds(120)));

    // 60s window keeps only the fresh one, in both list and find.
    let listed = reg.list(Some(Duration::seconds(60)));
    assert_eq!(listed.len(), 1);
    assert_eq!(listed[0].peer_id, "fresh");
    let found = reg.find("m", 0, Some(Duration::seconds(60)));
    assert_eq!(found.len(), 1);
    assert_eq!(found[0].peer_id, "fresh");
    // No window → both.
    assert_eq!(reg.list(None).len(), 2);
}

#[test]
fn null_broadcaster_is_noop() {
    let b = NullMeshCapabilityBroadcaster::new();
    b.broadcast(&ad("p1", "m", 10, Utc::now())); // must not panic
}

// ── EventTranslator (via the telemetry adapter) ──────────────────────────────

/// Captures translated CircleAI events.
#[derive(Default)]
struct CaptureObserver {
    transports: Mutex<Vec<circle_ai::aether::AetherTransportEvent>>,
    securities: Mutex<Vec<circle_ai::aether::AetherSecurityEvent>>,
    nodes: Mutex<Vec<circle_ai::aether::AetherNodeEvent>>,
}
impl IAetherTelemetryObserver for CaptureObserver {
    fn on_node_event(&self, e: &circle_ai::aether::AetherNodeEvent) {
        self.nodes.lock().unwrap().push(e.clone());
    }
    fn on_transport_event(&self, e: &circle_ai::aether::AetherTransportEvent) {
        self.transports.lock().unwrap().push(e.clone());
    }
    fn on_route_event(&self, _e: &circle_ai::aether::AetherRouteEvent) {}
    fn on_security_event(&self, e: &circle_ai::aether::AetherSecurityEvent) {
        self.securities.lock().unwrap().push(e.clone());
    }
    fn on_network_event(&self, _e: &circle_ai::aether::AetherNetworkEvent) {}
}

#[test]
fn telemetry_adapter_translates_and_folds_transport() {
    let mesh = Arc::new(InMemoryMeshTelemetry::new());
    let adapter = AetherNetTelemetryAdapter::new(mesh.clone() as Arc<dyn IAetherNetTelemetry>);
    let obs = Arc::new(CaptureObserver::default());
    let _sub = adapter.subscribe(obs.clone());

    // NearLink folds to Unknown; HttpRelay → Cellular; WiFiDirect → WiFi.
    for t in [
        AetherNetTransportKind::NearLink,
        AetherNetTransportKind::HttpRelay,
        AetherNetTransportKind::WiFiDirect,
    ] {
        mesh.publish_transport_event(&AetherNetTransportEvent {
            node_id: "n1".into(),
            kind: AetherNetTransportEventKind::Selected,
            transport: t,
            latency: None,
            packet_loss_rate: None,
            occurred_at: Utc::now(),
        });
    }
    let got = obs.transports.lock().unwrap();
    use circle_ai::aether::AetherTransportKind as K;
    assert_eq!(got[0].transport, K::Unknown);
    assert_eq!(got[1].transport, K::Cellular);
    assert_eq!(got[2].transport, K::WiFi);
}

#[test]
fn telemetry_adapter_translates_security_and_threat_level() {
    let mesh = Arc::new(InMemoryMeshTelemetry::new());
    let adapter = AetherNetTelemetryAdapter::new(mesh.clone() as Arc<dyn IAetherNetTelemetry>);
    let obs = Arc::new(CaptureObserver::default());
    let _sub = adapter.subscribe(obs.clone());

    mesh.publish_security_event(&AetherNetSecurityEvent {
        node_id: "n1".into(),
        kind: AetherNetSecurityEventKind::IntrusionSignal,
        threat_level: AetherNetThreatLevel::Critical,
        description: "sig".into(),
        metadata: Default::default(),
        occurred_at: Utc::now(),
    });
    let got = obs.securities.lock().unwrap();
    assert_eq!(got.len(), 1);
    assert_eq!(got[0].threat_level, AetherThreatLevel::Critical);
    assert_eq!(got[0].kind, circle_ai::aether::AetherSecurityEventKind::IntrusionSignal);
}

#[test]
fn telemetry_adapter_unsubscribe_stops_delivery() {
    let mesh = Arc::new(InMemoryMeshTelemetry::new());
    let adapter = AetherNetTelemetryAdapter::new(mesh.clone() as Arc<dyn IAetherNetTelemetry>);
    let obs = Arc::new(CaptureObserver::default());
    let sub = adapter.subscribe(obs.clone());
    assert_eq!(mesh.subscriber_count(), 1);
    drop(sub); // dropping the CircleAI handle drops the mesh subscription
    assert_eq!(mesh.subscriber_count(), 0);
    // A node event after unsubscribe is not delivered.
    mesh.publish_node_event(&AetherNetNodeEvent {
        node_id: "n1".into(),
        kind: AetherNetNodeEventKind::Joined,
        health: AetherNetNodeHealth {
            trust_score: 1.0,
            is_reachable: true,
            latency: Duration::zero(),
            hop_count: 1,
        },
        occurred_at: Utc::now(),
    });
    assert!(obs.nodes.lock().unwrap().is_empty());
}

// ── Context adapter ──────────────────────────────────────────────────────────

#[test]
fn context_adapter_reports_app_level_and_protocol_version() {
    let ctx = AetherNetContextAdapter::new(None, true);
    assert_eq!(ctx.install_level(), AetherInstallLevel::App);
    assert!(ctx.is_available()); // always true for the in-process runtime
    assert!(!ctx.requires_auth()); // App, not OS
    assert!(ctx.is_enabled());
    assert_eq!(
        ctx.runtime_version(),
        Some(AetherVersion::full(CURRENT_PROTOCOL_VERSION, 0, 0, 0))
    );
    assert!(ctx.is_sufficient()); // no minimum
}

#[test]
fn context_adapter_insufficient_when_minimum_exceeds_protocol() {
    let ctx = AetherNetContextAdapter::new(
        Some(AetherVersion::full(CURRENT_PROTOCOL_VERSION + 1, 0, 0, 0)),
        true,
    );
    assert!(!ctx.is_sufficient());
}

// ── Directive sink (CircleAI → mesh) ─────────────────────────────────────────

#[test]
fn directive_sink_translates_and_forwards_to_mesh() {
    let mesh_consumer = Arc::new(RecordingMeshDirectiveConsumer::new());
    let sink = AetherNetDirectiveSink::new(
        mesh_consumer.clone() as Arc<dyn IMeshSecurityDirectiveConsumer>,
    );
    let d = SecurityDirective::new(
        SecurityDirectiveKind::QuarantineNode,
        Some("n1".into()),
        Some(0.1),
        AetherThreatLevel::Critical,
        "bad actor",
        Some(Duration::minutes(10)),
        Utc::now(),
    );
    sink.on_directive(&d);
    let got = mesh_consumer.received();
    assert_eq!(got.len(), 1);
    assert_eq!(got[0].kind, MeshSecurityDirectiveKind::QuarantineNode);
    assert_eq!(got[0].threat_level, AetherNetThreatLevel::Critical);
    assert_eq!(got[0].target_node_id.as_deref(), Some("n1"));
    assert_eq!(got[0].reason, "bad actor");
}

// ── Inbound bridge (mesh → CircleAI) ─────────────────────────────────────────

#[derive(Default)]
struct CircleSink {
    got: Mutex<Vec<SecurityDirective>>,
}
impl ISecurityDirectiveConsumer for CircleSink {
    fn on_directive(&self, d: &SecurityDirective) {
        self.got.lock().unwrap().push(d.clone());
    }
}

#[test]
fn inbound_bridge_translates_mesh_directive_to_circle() {
    let circle = Arc::new(CircleSink::default());
    let bridge = AetherNetInboundDirectiveBridge::new(circle.clone() as Arc<dyn ISecurityDirectiveConsumer>);
    let mesh_d = MeshSecurityDirective {
        kind: MeshSecurityDirectiveKind::RequestReauth,
        target_node_id: Some("n2".into()),
        trust_score_override: Some(0.4),
        threat_level: AetherNetThreatLevel::High,
        reason: "reauth".into(),
        duration: None,
        issued_at: Utc::now(),
    };
    bridge.on_directive(&mesh_d);
    let got = circle.got.lock().unwrap();
    assert_eq!(got.len(), 1);
    assert_eq!(got[0].kind, SecurityDirectiveKind::RequestReauth);
    assert_eq!(got[0].threat_level, AetherThreatLevel::High);
    assert!(got[0].is_permanent());
}

// ── AI provider ──────────────────────────────────────────────────────────────

#[test]
fn ai_provider_folds_critical_to_high_and_maps_routes() {
    let intel = Arc::new(InMemoryAetherIntelligence::new());
    intel.set_trust("dest", 0.95, "seed");
    intel.set_trust("attacker", 0.10, "attack"); // Critical band
    let provider = CircleAiAetherNetAiProvider::new(
        intel.clone() as Arc<dyn circle_ai::aether::IAetherIntelligence>,
    );

    assert!(provider.is_available());

    // Threat: attacker is Critical in CircleAI; the mesh seat has no Critical, so
    // it must fold to High.
    let level = provider.assess_threat(&MeshPacket { source_uhid: "attacker".into() });
    assert_eq!(level, AiThreatLevel::High);
    // Empty source → None.
    assert_eq!(
        provider.assess_threat(&MeshPacket { source_uhid: "  ".into() }),
        AiThreatLevel::None
    );

    // Route suggestion for a trusted destination is the direct path.
    let routes = provider.suggest_routes("dest", 1024);
    assert_eq!(routes.len(), 1);
    assert_eq!(routes[0].path, vec!["dest".to_string()]);

    // No route when destination is quarantined (empty recommended path).
    let none = provider.suggest_routes("attacker", 1024);
    assert!(none.is_empty());

    // Transport biases are empty (CircleAI does not model them yet).
    assert!(provider.get_transport_biases(1024).is_empty());

    // Network health passes through.
    let health = provider.get_network_health();
    assert!(health.overall_score > 0.0 && health.overall_score <= 1.0);
}
