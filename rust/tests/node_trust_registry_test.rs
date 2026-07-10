//! node_trust_registry_test.rs
//!
//! Ports `NodeTrustRegistry.cs` behaviour: score init, clamped degradation,
//! bounded event history, passive recovery, windowed queries, and the
//! trust-score update backlog.

use chrono::{Duration, Utc};
use circle_ai::security::{
    NodeTrustRegistry, PeerSecurityEvent, PeerSecurityEventKind, PeerThreatLevel, SecurityOptions,
};

fn ev(node: &str, desc: &str) -> PeerSecurityEvent {
    PeerSecurityEvent::new(
        node,
        PeerSecurityEventKind::AuthAttempt,
        PeerThreatLevel::Medium,
        desc,
        "test",
        Utc::now(),
    )
}

#[test]
fn unknown_node_returns_initial_trust() {
    let reg = NodeTrustRegistry::new(SecurityOptions::default());
    assert_eq!(reg.get_trust_score("ghost"), 1.0);
}

#[test]
fn get_or_create_seeds_initial_score() {
    let mut opts = SecurityOptions::default();
    opts.initial_trust_score = 0.8;
    let reg = NodeTrustRegistry::new(opts);
    assert_eq!(reg.get_or_create("n1"), 0.8);
    assert!(reg.all_node_ids().contains(&"n1".to_string()));
}

#[test]
fn apply_degradation_drops_and_clamps() {
    let reg = NodeTrustRegistry::new(SecurityOptions::default());
    let (prev, cur) = reg.apply_degradation(&ev("n1", "hit"), 0.3);
    assert_eq!(prev, 1.0);
    assert!((cur - 0.7).abs() < 1e-9);

    // Over-degrade to clamp at 0.
    let (_, cur2) = reg.apply_degradation(&ev("n1", "hit2"), 5.0);
    assert_eq!(cur2, 0.0);
}

#[test]
fn degradation_publishes_trust_update() {
    let reg = NodeTrustRegistry::new(SecurityOptions::default());
    reg.apply_degradation(&ev("n1", "reason-x"), 0.25);
    let updates = reg.trust_score_updates();
    assert_eq!(updates.len(), 1);
    let u = &updates[0];
    assert_eq!(u.node_id, "n1");
    assert_eq!(u.previous_score, 1.0);
    assert!((u.new_score - 0.75).abs() < 1e-9);
    assert_eq!(u.reason, "reason-x");
}

#[test]
fn backlog_is_unbounded_and_retained_until_drained() {
    let reg = NodeTrustRegistry::new(SecurityOptions::default());
    // Three distinct-magnitude degradations before any drain.
    reg.apply_degradation(&ev("n1", "a"), 0.1);
    reg.apply_degradation(&ev("n1", "b"), 0.1);
    reg.apply_degradation(&ev("n2", "c"), 0.1);
    let updates = reg.trust_score_updates();
    assert_eq!(updates.len(), 3, "all pre-drain writes retained");
    // Second drain is empty.
    assert!(reg.trust_score_updates().is_empty());
}

#[test]
fn negligible_change_does_not_publish() {
    let reg = NodeTrustRegistry::new(SecurityOptions::default());
    // Degradation below the 0.0001 threshold → no publish.
    reg.apply_degradation(&ev("n1", "tiny"), 0.00001);
    assert!(reg.trust_score_updates().is_empty());
}

#[test]
fn event_history_is_bounded() {
    let mut opts = SecurityOptions::default();
    opts.max_events_per_node = 3;
    let reg = NodeTrustRegistry::new(opts);
    for i in 0..10 {
        reg.apply_degradation(&ev("n1", &format!("e{i}")), 0.001);
    }
    let entry = reg.snapshot_entry("n1").unwrap();
    assert_eq!(entry.recent_events.len(), 3, "oldest dropped first");
    // The retained ones are the last three.
    assert_eq!(entry.recent_events[0].description, "e7");
    assert_eq!(entry.recent_events[2].description, "e9");
}

#[test]
fn recovery_heals_but_caps_at_one() {
    let mut opts = SecurityOptions::default();
    opts.recovery_rate_per_second = 0.1;
    let reg = NodeTrustRegistry::new(opts);
    reg.apply_degradation(&ev("n1", "hit"), 0.5); // -> 0.5
    reg.trust_score_updates(); // drain

    reg.apply_recovery(Duration::seconds(2)); // +0.2 -> 0.7
    assert!((reg.get_trust_score("n1") - 0.7).abs() < 1e-9);

    reg.apply_recovery(Duration::seconds(100)); // caps at 1.0
    assert_eq!(reg.get_trust_score("n1"), 1.0);
}

#[test]
fn recovery_skips_full_trust_nodes() {
    let mut opts = SecurityOptions::default();
    opts.recovery_rate_per_second = 0.1;
    let reg = NodeTrustRegistry::new(opts);
    reg.get_or_create("n1"); // at 1.0
    reg.apply_recovery(Duration::seconds(5));
    // No update published because node is already at 1.0.
    assert!(reg.trust_score_updates().is_empty());
}

#[test]
fn recovery_publishes_passive_recovery_reason() {
    let mut opts = SecurityOptions::default();
    opts.recovery_rate_per_second = 0.05;
    let reg = NodeTrustRegistry::new(opts);
    reg.apply_degradation(&ev("n1", "hit"), 0.5);
    reg.trust_score_updates(); // drain
    reg.apply_recovery(Duration::seconds(1));
    let updates = reg.trust_score_updates();
    assert_eq!(updates.len(), 1);
    assert_eq!(updates[0].reason, "passive-recovery");
}

#[test]
fn get_recent_events_windows_out_stale() {
    let mut opts = SecurityOptions::default();
    opts.event_window = Duration::minutes(5);
    opts.max_events_per_node = 100;
    let reg = NodeTrustRegistry::new(opts);

    let stale = PeerSecurityEvent::new(
        "n1",
        PeerSecurityEventKind::AuthAttempt,
        PeerThreatLevel::Medium,
        "stale",
        "test",
        Utc::now() - Duration::minutes(10),
    );
    reg.apply_degradation(&stale, 0.01);
    reg.apply_degradation(&ev("n1", "fresh"), 0.01);

    let recent = reg.get_recent_events("n1");
    assert_eq!(recent.len(), 1);
    assert_eq!(recent[0].description, "fresh");
}

#[test]
fn recent_events_empty_for_unknown_node() {
    let reg = NodeTrustRegistry::new(SecurityOptions::default());
    assert!(reg.get_recent_events("nobody").is_empty());
}
