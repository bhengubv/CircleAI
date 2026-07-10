//! aether_contracts_test.rs
//!
//! Ports the behavioural contracts in `CircleAI.Aether/*.cs`: the event records'
//! computed properties, `AetherVersion` (.NET `System.Version`) ordering,
//! `StaticAetherContext` / `AetherNetContextAdapter` derivations, the auth-
//! challenge minimum-method policy, the security-layer directive publishing +
//! posture, and the intelligence outputs.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};

use chrono::{Duration, Utc};
use circle_ai::aether::{
    AetherInstallLevel, AetherNetworkEvent, AetherNetworkEventKind, AetherNodeEvent,
    AetherNodeEventKind, AetherNodeHealth, AetherRouteEvent, AetherRouteEventKind,
    AetherSecurityEvent, AetherSecurityEventKind, AetherThreatLevel, AetherTransportEvent,
    AetherTransportEventKind, AetherTransportKind, AetherVersion, AuthChallengeReason, AuthMethod,
    IAISecurityLayer, IAetherContext, IAetherIntelligence, IAetherTelemetry, IAuthChallenge,
    ISecurityDirectiveConsumer, InMemoryAISecurityLayer, InMemoryAetherIntelligence,
    InMemoryAetherTelemetry, NullAetherTelemetry, PolicyAuthChallenge, SecurityDirective,
    SecurityDirectiveKind, StaticAetherContext,
};

// ── Event record computed properties ─────────────────────────────────────────

#[test]
fn node_event_is_exit_only_on_left() {
    let health = AetherNodeHealth::new(1.0, true, Duration::milliseconds(10), 1);
    let joined = AetherNodeEvent::new("n1", AetherNodeEventKind::Joined, health.clone(), Utc::now());
    let left = AetherNodeEvent::new("n1", AetherNodeEventKind::Left, health.clone(), Utc::now());
    assert!(!joined.is_exit());
    assert!(left.is_exit());
    assert!(health.is_valid());
    assert!(!AetherNodeHealth::new(1.5, true, Duration::zero(), 0).is_valid());
}

#[test]
fn transport_event_exceeds_loss() {
    let e = AetherTransportEvent::new(
        "n1",
        AetherTransportEventKind::PacketLoss,
        AetherTransportKind::WiFi,
        Some(Duration::milliseconds(5)),
        Some(0.30),
        Utc::now(),
    );
    assert!(e.exceeds_loss(0.25));
    assert!(!e.exceeds_loss(0.50));
    // None packet-loss never exceeds.
    let none = AetherTransportEvent::new(
        "n1",
        AetherTransportEventKind::Selected,
        AetherTransportKind::LoRa,
        None,
        None,
        Utc::now(),
    );
    assert!(!none.exceeds_loss(0.0));
}

#[test]
fn route_event_hop_count_and_failed() {
    let e = AetherRouteEvent::new(
        "a",
        "c",
        vec!["a".into(), "b".into(), "c".into()],
        AetherRouteEventKind::Failed,
        Some("timeout".into()),
        Utc::now(),
    );
    assert_eq!(e.hop_count(), 3);
    assert!(e.is_failed());
}

#[test]
fn security_event_high_severity() {
    let mk = |lvl| {
        AetherSecurityEvent::new(
            "n1",
            AetherSecurityEventKind::IntrusionSignal,
            lvl,
            "d",
            BTreeMap::new(),
            Utc::now(),
        )
    };
    assert!(!mk(AetherThreatLevel::Medium).is_high_severity());
    assert!(mk(AetherThreatLevel::High).is_high_severity());
    assert!(mk(AetherThreatLevel::Critical).is_high_severity());
}

#[test]
fn network_event_high_congestion() {
    let hi = AetherNetworkEvent::new(AetherNetworkEventKind::CongestionDetected, 5, 3, 0.80, Utc::now());
    let lo = AetherNetworkEvent::new(AetherNetworkEventKind::TopologyChanged, 5, 3, 0.50, Utc::now());
    assert!(hi.is_high_congestion());
    assert!(!lo.is_high_congestion());
}

// ── AetherVersion ordering (System.Version semantics) ────────────────────────

#[test]
fn version_ordering_matches_dotnet() {
    // Two-component build=-1 sorts before three-component build=0.
    assert!(AetherVersion::new(1, 0) < AetherVersion::with_build(1, 0, 0));
    assert!(AetherVersion::new(2, 1) > AetherVersion::new(2, 0));
    assert!(AetherVersion::with_build(3, 0, 5) > AetherVersion::with_build(3, 0, 4));
    assert_eq!(AetherVersion::new(1, 2), AetherVersion::new(1, 2));
    assert!(AetherVersion::full(1, 0, 0, 1) > AetherVersion::with_build(1, 0, 0));
    assert_eq!(AetherVersion::with_build(1, 2, 3).to_string(), "1.2.3");
    assert_eq!(AetherVersion::new(1, 2).to_string(), "1.2");
}

// ── IAetherContext ───────────────────────────────────────────────────────────

#[test]
fn static_context_derivations() {
    // OS-managed, sufficient version, enabled.
    let ctx = StaticAetherContext::new(
        AetherInstallLevel::Os,
        Some(AetherVersion::with_build(3, 0, 0)),
        Some(AetherVersion::with_build(2, 5, 0)),
        true,
    );
    assert!(ctx.is_available());
    assert!(ctx.is_sufficient());
    assert!(ctx.requires_auth()); // OS level
    assert!(ctx.is_enabled());

    // Absent context.
    let absent = StaticAetherContext::absent();
    assert!(!absent.is_available());
    assert!(absent.is_sufficient()); // no minimum required → always sufficient
    assert!(!absent.requires_auth());
}

#[test]
fn context_insufficient_when_runtime_below_minimum() {
    let ctx = StaticAetherContext::new(
        AetherInstallLevel::App,
        Some(AetherVersion::with_build(1, 0, 0)),
        Some(AetherVersion::with_build(2, 0, 0)),
        true,
    );
    assert!(!ctx.is_sufficient());
    assert!(!ctx.requires_auth()); // App level, not OS
}

// ── IAuthChallenge ────────────────────────────────────────────────────────────

#[test]
fn auth_challenge_enforces_os_floor() {
    // A user who can only do Biometric fails the OS floor (Biometric+DeviceAdmin).
    let weak = PolicyAuthChallenge::biometric_only();
    let r = weak.request_os_toggle(true);
    assert!(!r.succeeded);
    assert!(r.failure_reason.is_some());

    // A user who can do the full method passes.
    let strong = PolicyAuthChallenge::always_succeeds();
    let r2 = strong.request_os_toggle(false);
    assert!(r2.succeeded);
    assert_eq!(r2.method_used, AuthMethod::Custom);
}

#[test]
fn auth_challenge_requested_minimum_can_only_raise_bar() {
    // Exactly meeting the floor with BiometricAndDeviceAdmin succeeds for a
    // non-OS reason with no explicit minimum.
    let user = PolicyAuthChallenge::new(AuthMethod::BiometricAndDeviceAdmin);
    let ok = user.challenge(AuthChallengeReason::PrivilegedOperation, None, "prompt");
    assert!(ok.succeeded);

    // Requesting a stronger Custom minimum now fails that same user.
    let fail = user.challenge(
        AuthChallengeReason::PrivilegedOperation,
        Some(AuthMethod::Custom),
        "prompt",
    );
    assert!(!fail.succeeded);
}

#[test]
fn auth_method_strength_ordering() {
    assert!(AuthMethod::Custom > AuthMethod::BiometricAndDeviceAdmin);
    assert!(AuthMethod::BiometricAndDeviceAdmin > AuthMethod::DeviceAdmin);
    assert!(AuthMethod::DeviceAdmin > AuthMethod::Biometric);
    assert_eq!(AuthMethod::Biometric as u8, 1);
    assert_eq!(AuthMethod::Custom as u8, 4);
}

// ── IAISecurityLayer (InMemoryAISecurityLayer) ───────────────────────────────

/// Records every directive it receives.
#[derive(Default)]
struct RecordingConsumer {
    got: Mutex<Vec<SecurityDirective>>,
}
impl ISecurityDirectiveConsumer for RecordingConsumer {
    fn on_directive(&self, directive: &SecurityDirective) {
        self.got.lock().unwrap().push(directive.clone());
    }
}

fn sec_event(node: &str, lvl: AetherThreatLevel) -> AetherSecurityEvent {
    AetherSecurityEvent::new(
        node,
        AetherSecurityEventKind::IntrusionSignal,
        lvl,
        "sig",
        BTreeMap::new(),
        Utc::now(),
    )
}

#[test]
fn security_layer_publishes_directive_on_high_and_critical() {
    let telemetry = InMemoryAetherTelemetry::new();
    let layer = InMemoryAISecurityLayer::new();
    let consumer = Arc::new(RecordingConsumer::default());
    let _sub = layer.subscribe_to_directives(consumer.clone());

    layer.start(&telemetry);
    assert!(layer.is_active());

    // Medium → ElevateMonitoring.
    telemetry.publish_security_event(&sec_event("n1", AetherThreatLevel::Medium));
    // High → AvoidNode.
    telemetry.publish_security_event(&sec_event("n1", AetherThreatLevel::High));
    // Critical → QuarantineNode.
    telemetry.publish_security_event(&sec_event("n1", AetherThreatLevel::Critical));

    let got = consumer.got.lock().unwrap();
    let kinds: Vec<SecurityDirectiveKind> = got.iter().map(|d| d.kind).collect();
    assert_eq!(
        kinds,
        vec![
            SecurityDirectiveKind::ElevateMonitoring,
            SecurityDirectiveKind::AvoidNode,
            SecurityDirectiveKind::QuarantineNode,
        ]
    );
    for d in got.iter() {
        assert_eq!(d.target_node_id.as_deref(), Some("n1"));
        assert!(d.has_target());
    }
}

#[test]
fn security_layer_subscribe_before_publish_is_not_lost() {
    // The observer must be wired synchronously by start(); an event published
    // immediately after start must be seen.
    let telemetry = InMemoryAetherTelemetry::new();
    let layer = InMemoryAISecurityLayer::new();
    layer.start(&telemetry);
    telemetry.publish_security_event(&sec_event("n1", AetherThreatLevel::High));
    let posture = layer.get_posture();
    assert_eq!(posture.overall_threat_level, AetherThreatLevel::High);
    assert_eq!(posture.monitored_node_count, 1);
    assert!(posture.is_active);
}

#[test]
fn security_layer_posture_counts_and_node_exit_clears() {
    let telemetry = InMemoryAetherTelemetry::new();
    let layer = InMemoryAISecurityLayer::new();
    layer.start(&telemetry);

    telemetry.publish_security_event(&sec_event("crit", AetherThreatLevel::Critical));
    telemetry.publish_security_event(&sec_event("med", AetherThreatLevel::Medium));

    let p = layer.get_posture();
    assert_eq!(p.quarantined_node_count, 1);
    assert_eq!(p.monitored_node_count, 1);
    assert_eq!(p.overall_threat_level, AetherThreatLevel::Critical);

    // A node departure clears its tracked threat.
    let health = AetherNodeHealth::new(0.0, false, Duration::zero(), 0);
    telemetry.publish_node_event(&AetherNodeEvent::new(
        "crit",
        AetherNodeEventKind::Left,
        health,
        Utc::now(),
    ));
    let p2 = layer.get_posture();
    assert_eq!(p2.quarantined_node_count, 0);
    assert_eq!(p2.overall_threat_level, AetherThreatLevel::Medium);

    layer.stop();
    assert!(!layer.is_active());
    assert!(!layer.get_posture().is_active);
}

#[test]
fn security_layer_stop_unsubscribes_observer() {
    let telemetry = InMemoryAetherTelemetry::new();
    let layer = InMemoryAISecurityLayer::new();
    layer.start(&telemetry);
    assert_eq!(telemetry.subscriber_count(), 1);
    layer.stop();
    assert_eq!(telemetry.subscriber_count(), 0, "stop drops the subscription");
}

#[test]
fn null_telemetry_subscribe_is_noop() {
    let t = NullAetherTelemetry::new();
    let layer = InMemoryAISecurityLayer::new();
    layer.start(&t); // must not panic
    // Nothing is published; posture is clean.
    assert_eq!(layer.get_posture().overall_threat_level, AetherThreatLevel::None);
}

// ── IAetherIntelligence (InMemoryAetherIntelligence) ─────────────────────────

#[test]
fn intelligence_empty_network_health() {
    let intel = InMemoryAetherIntelligence::new();
    let h = intel.get_network_health();
    assert_eq!(h.overall_score, 1.0);
    assert_eq!(h.trusted_node_count, 0);
    assert!(h.is_valid());
}

#[test]
fn intelligence_assess_unknown_is_zero_confidence() {
    let intel = InMemoryAetherIntelligence::new();
    let a = intel.assess_threat("ghost");
    assert_eq!(a.threat_confidence, 0.0);
    assert_eq!(a.level, AetherThreatLevel::None);
    assert!(a.indicators.is_empty());
}

#[test]
fn intelligence_low_trust_drives_level_and_avoid() {
    let intel = InMemoryAetherIntelligence::new();
    intel.set_trust("good", 0.95, "seed");
    intel.set_trust("bad", 0.20, "attack");

    let assess = intel.assess_threat("bad");
    assert_eq!(assess.level, AetherThreatLevel::Critical);
    assert!(assess.threat_confidence > 0.75);
    assert!(assess.indicators.contains(&"trust-critical".to_string()));

    let advice = intel.get_routing_advice("bad");
    assert!(advice.recommended_path.is_empty(), "no safe path to quarantined node");
    assert!(advice.avoid_nodes.contains(&"bad".to_string()));

    let advice2 = intel.get_routing_advice("good");
    assert_eq!(advice2.recommended_path, vec!["good".to_string()]);
}

#[test]
fn intelligence_stream_drains_updates() {
    let intel = InMemoryAetherIntelligence::new();
    intel.set_trust("n1", 0.9, "a");
    intel.set_trust("n1", 0.5, "b"); // changed → 2 updates
    let first = intel.stream_trust_scores();
    assert_eq!(first.len(), 2);
    assert!(first[1].is_degraded());
    assert!(first[0].has_changed());
    // Second drain is empty.
    assert!(intel.stream_trust_scores().is_empty());
    // A no-op set (same value) publishes nothing.
    intel.set_trust("n1", 0.5, "noop");
    assert!(intel.stream_trust_scores().is_empty());
}
