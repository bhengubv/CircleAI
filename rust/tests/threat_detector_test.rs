//! threat_detector_test.rs
//!
//! Ports the C# `ThreatDetector` degradation-weight and indicator-detection
//! behaviour. Values must match `ThreatDetector.cs` exactly.

use chrono::{Duration, Utc};
use circle_ai::security::{
    PeerSecurityEvent, PeerSecurityEventKind, PeerThreatLevel, ThreatDetector,
};

const EPS: f64 = 1e-9;

fn ev(kind: PeerSecurityEventKind, level: PeerThreatLevel) -> PeerSecurityEvent {
    PeerSecurityEvent::new("node-1", kind, level, "desc", "test", Utc::now())
}

fn ev_at(
    kind: PeerSecurityEventKind,
    level: PeerThreatLevel,
    at: chrono::DateTime<Utc>,
) -> PeerSecurityEvent {
    PeerSecurityEvent::new("node-1", kind, level, "desc", "test", at)
}

// ── Degradation: base_weight × threat_multiplier ────────────────────────────

#[test]
fn none_level_yields_zero_degradation() {
    for kind in [
        PeerSecurityEventKind::AuthAttempt,
        PeerSecurityEventKind::IntrusionSignal,
        PeerSecurityEventKind::DataExfiltration,
    ] {
        let d = ThreatDetector::compute_degradation(&ev(kind, PeerThreatLevel::None));
        assert!(d.abs() < EPS, "None must be 0 for {kind:?}, got {d}");
    }
}

#[test]
fn auth_attempt_medium_is_base_times_one() {
    // 0.05 * 1.0
    let d = ThreatDetector::compute_degradation(&ev(
        PeerSecurityEventKind::AuthAttempt,
        PeerThreatLevel::Medium,
    ));
    assert!((d - 0.05).abs() < EPS, "got {d}");
}

#[test]
fn intrusion_critical_is_base_times_three() {
    // 0.15 * 3.0
    let d = ThreatDetector::compute_degradation(&ev(
        PeerSecurityEventKind::IntrusionSignal,
        PeerThreatLevel::Critical,
    ));
    assert!((d - 0.45).abs() < EPS, "got {d}");
}

#[test]
fn low_level_halves_the_weight() {
    // RoutingAnomaly 0.10 * Low 0.5 = 0.05
    let d = ThreatDetector::compute_degradation(&ev(
        PeerSecurityEventKind::RoutingAnomaly,
        PeerThreatLevel::Low,
    ));
    assert!((d - 0.05).abs() < EPS, "got {d}");
}

#[test]
fn high_level_doubles_the_weight() {
    // DataExfiltration 0.14 * High 2.0 = 0.28
    let d = ThreatDetector::compute_degradation(&ev(
        PeerSecurityEventKind::DataExfiltration,
        PeerThreatLevel::High,
    ));
    assert!((d - 0.28).abs() < EPS, "got {d}");
}

#[test]
fn all_base_weights_match_reference() {
    let cases = [
        (PeerSecurityEventKind::AuthAttempt, 0.05),
        (PeerSecurityEventKind::RoutingAnomaly, 0.10),
        (PeerSecurityEventKind::BehaviourChange, 0.08),
        (PeerSecurityEventKind::EncryptionEvent, 0.06),
        (PeerSecurityEventKind::IntrusionSignal, 0.15),
        (PeerSecurityEventKind::PrivilegeAttempt, 0.12),
        (PeerSecurityEventKind::ConnectionAnomaly, 0.07),
        (PeerSecurityEventKind::DataExfiltration, 0.14),
        (PeerSecurityEventKind::DenialOfService, 0.13),
        (PeerSecurityEventKind::Unknown, 0.05),
    ];
    for (kind, base) in cases {
        // Medium multiplier is 1.0, so degradation == base weight.
        let d = ThreatDetector::compute_degradation(&ev(kind, PeerThreatLevel::Medium));
        assert!((d - base).abs() < EPS, "{kind:?}: got {d}, want {base}");
    }
}

// ── Indicators ──────────────────────────────────────────────────────────────

#[test]
fn empty_events_yield_no_indicators() {
    let events: Vec<PeerSecurityEvent> = Vec::new();
    let ind = ThreatDetector::detect_indicators(events.iter(), Duration::minutes(5));
    assert!(ind.is_empty());
}

#[test]
fn three_auth_attempts_flag_brute_force() {
    let events = vec![
        ev(PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Low),
        ev(PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Low),
        ev(PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Low),
    ];
    let ind = ThreatDetector::detect_indicators(events.iter(), Duration::minutes(5));
    assert!(ind.contains(&"repeated-auth-attempts".to_string()));
    // Only one kind → no multi-vector.
    assert!(!ind.contains(&"multi-vector-activity".to_string()));
}

#[test]
fn two_auth_attempts_do_not_flag_brute_force() {
    let events = vec![
        ev(PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Low),
        ev(PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Low),
    ];
    let ind = ThreatDetector::detect_indicators(events.iter(), Duration::minutes(5));
    assert!(!ind.contains(&"repeated-auth-attempts".to_string()));
}

#[test]
fn intrusion_and_high_severity_flags() {
    let events = vec![ev(
        PeerSecurityEventKind::IntrusionSignal,
        PeerThreatLevel::High,
    )];
    let ind = ThreatDetector::detect_indicators(events.iter(), Duration::minutes(5));
    assert!(ind.contains(&"intrusion-signal-detected".to_string()));
    assert!(ind.contains(&"high-severity-event".to_string()));
}

#[test]
fn three_distinct_kinds_flag_multi_vector() {
    let events = vec![
        ev(PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Low),
        ev(PeerSecurityEventKind::RoutingAnomaly, PeerThreatLevel::Low),
        ev(PeerSecurityEventKind::EncryptionEvent, PeerThreatLevel::Low),
    ];
    let ind = ThreatDetector::detect_indicators(events.iter(), Duration::minutes(5));
    assert!(ind.contains(&"multi-vector-activity".to_string()));
}

#[test]
fn privilege_and_exfiltration_flags() {
    let events = vec![
        ev(PeerSecurityEventKind::PrivilegeAttempt, PeerThreatLevel::Medium),
        ev(PeerSecurityEventKind::DataExfiltration, PeerThreatLevel::Medium),
    ];
    let ind = ThreatDetector::detect_indicators(events.iter(), Duration::minutes(5));
    assert!(ind.contains(&"privilege-escalation-attempt".to_string()));
    assert!(ind.contains(&"data-exfiltration-signal".to_string()));
}

#[test]
fn events_outside_window_are_ignored() {
    // Event happened 10 minutes ago; window is 5 minutes.
    let old = Utc::now() - Duration::minutes(10);
    let events = vec![
        ev_at(PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Low, old),
        ev_at(PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Low, old),
        ev_at(PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Low, old),
    ];
    let ind = ThreatDetector::detect_indicators(events.iter(), Duration::minutes(5));
    assert!(ind.is_empty(), "stale events must be ignored: {ind:?}");
}

#[test]
fn indicator_ordering_matches_reference() {
    // A multi-signal window should emit indicators in the C# declaration order:
    // repeated-auth, intrusion, high-severity, multi-vector, privilege, exfil.
    let events = vec![
        ev(PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Low),
        ev(PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Low),
        ev(PeerSecurityEventKind::AuthAttempt, PeerThreatLevel::Low),
        ev(PeerSecurityEventKind::IntrusionSignal, PeerThreatLevel::Critical),
        ev(PeerSecurityEventKind::PrivilegeAttempt, PeerThreatLevel::High),
        ev(PeerSecurityEventKind::DataExfiltration, PeerThreatLevel::High),
    ];
    let ind = ThreatDetector::detect_indicators(events.iter(), Duration::minutes(5));
    assert_eq!(
        ind,
        vec![
            "repeated-auth-attempts".to_string(),
            "intrusion-signal-detected".to_string(),
            "high-severity-event".to_string(),
            "multi-vector-activity".to_string(),
            "privilege-escalation-attempt".to_string(),
            "data-exfiltration-signal".to_string(),
        ]
    );
}
