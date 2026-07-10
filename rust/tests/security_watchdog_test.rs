//! security_watchdog_test.rs
//!
//! Ports `ISecurityWatchdog.cs` (`DefaultSecurityWatchdog`),
//! `IAnomalyEventDispatcher.cs`, `SecurityResponse.cs`, and
//! `SecurityCheckpoint.cs` graduated-response behaviour.

use std::sync::Arc;

use circle_ai::security::{
    confidence_band, AnomalyDispatchOutcome, AnomalySignal, DefaultAnomalyEventDispatcher,
    DefaultSecurityWatchdog, IAnomalyEventDispatcher, ISecurityWatchdog, SecurityCheckpoint,
    SecurityResponse, SecurityResponseKind, ThreatVector,
};

// ── DefaultSecurityWatchdog graduated policy ────────────────────────────────

#[test]
fn low_confidence_is_no_action() {
    let wd = DefaultSecurityWatchdog::new();
    let sig = AnomalySignal::create(ThreatVector::MemoryAnomaly, 0.2, "mod", "low", None);
    let resp = wd.on_anomaly_detected(&sig, None);
    assert_eq!(resp.kind, SecurityResponseKind::NoAction);
    assert_eq!(resp.signal_id, sig.id);
    assert!(resp.applied_actions.is_empty());
    assert!(resp.restored_checkpoint.is_none());
}

#[test]
fn mid_confidence_rotates_keys() {
    let wd = DefaultSecurityWatchdog::new();
    let sig = AnomalySignal::create(ThreatVector::MemoryAnomaly, 0.45, "mod", "mid", None);
    let resp = wd.on_anomaly_detected(&sig, None);
    assert_eq!(resp.kind, SecurityResponseKind::KeyRotation);
    assert!(resp.applied_actions.is_empty());
}

#[test]
fn boundary_030_is_key_rotation_not_no_action() {
    // Confidence == 0.30 is NOT < 0.30, so it escalates to rotation.
    let wd = DefaultSecurityWatchdog::new();
    let sig = AnomalySignal::create(ThreatVector::MemoryAnomaly, 0.30, "mod", "edge", None);
    let resp = wd.on_anomaly_detected(&sig, None);
    assert_eq!(resp.kind, SecurityResponseKind::KeyRotation);
}

#[test]
fn boundary_060_is_key_rotation_not_composite() {
    // Confidence == 0.60 is NOT > 0.60, so it stays key-rotation.
    let wd = DefaultSecurityWatchdog::new();
    let sig = AnomalySignal::create(ThreatVector::ControlFlowDrift, 0.60, "mod", "edge", None);
    let resp = wd.on_anomaly_detected(&sig, None);
    assert_eq!(resp.kind, SecurityResponseKind::KeyRotation);
}

#[test]
fn high_confidence_is_composite_rotation_plus_mesh() {
    let wd = DefaultSecurityWatchdog::new();
    let sig = AnomalySignal::create(ThreatVector::MemoryAnomaly, 0.92, "mod", "high", None);
    let resp = wd.on_anomaly_detected(&sig, None);
    assert_eq!(resp.kind, SecurityResponseKind::Composite);
    assert_eq!(
        resp.applied_actions,
        vec![
            SecurityResponseKind::KeyRotation,
            SecurityResponseKind::MeshIsolationSignal
        ]
    );
    // MemoryAnomaly is NOT high-severity -> no rollback even with checkpoint.
    assert!(resp.restored_checkpoint.is_none());
}

#[test]
fn high_confidence_high_severity_with_verified_checkpoint_adds_rollback() {
    let wd = DefaultSecurityWatchdog::new();
    let cp = SecurityCheckpoint::create("uhid-1", "CircleAI.Memory", b"trusted-state".to_vec());
    let sig = AnomalySignal::create(ThreatVector::StateCorruption, 0.95, "CircleAI.Memory", "hi", None);
    let resp = wd.on_anomaly_detected(&sig, Some(&cp));
    assert_eq!(resp.kind, SecurityResponseKind::Composite);
    assert!(resp
        .applied_actions
        .contains(&SecurityResponseKind::StateRollback));
    assert!(resp.restored_checkpoint.is_some());
    assert_eq!(resp.restored_checkpoint.unwrap().id, cp.id);
}

#[test]
fn high_severity_but_tampered_checkpoint_skips_rollback() {
    let wd = DefaultSecurityWatchdog::new();
    let mut cp = SecurityCheckpoint::create("uhid-1", "CircleAI.Memory", b"good".to_vec());
    // Tamper with the payload so verify() fails.
    cp.payload = b"evil".to_vec();
    assert!(!cp.verify());
    let sig = AnomalySignal::create(ThreatVector::NetworkPivot, 0.99, "mod", "hi", None);
    let resp = wd.on_anomaly_detected(&sig, Some(&cp));
    assert_eq!(resp.kind, SecurityResponseKind::Composite);
    assert!(!resp
        .applied_actions
        .contains(&SecurityResponseKind::StateRollback));
    assert!(resp.restored_checkpoint.is_none());
}

#[test]
fn stream_signals_drains_every_processed_signal() {
    let wd = DefaultSecurityWatchdog::new();
    let s1 = AnomalySignal::create(ThreatVector::MemoryAnomaly, 0.1, "m", "a", None);
    let s2 = AnomalySignal::create(ThreatVector::MemoryAnomaly, 0.9, "m", "b", None);
    wd.on_anomaly_detected(&s1, None);
    wd.on_anomaly_detected(&s2, None);
    let drained = wd.stream_signals();
    assert_eq!(drained.len(), 2);
    assert_eq!(drained[0].id, s1.id);
    assert_eq!(drained[1].id, s2.id);
    assert!(wd.stream_signals().is_empty(), "second drain empty");
}

#[test]
fn component_name_matches_reference() {
    assert_eq!(DefaultSecurityWatchdog::new().component_name(), "DefaultSecurityWatchdog");
}

#[test]
fn confidence_band_matches_reference_bands() {
    assert_eq!(confidence_band(0.0), "low");
    assert_eq!(confidence_band(0.29), "low");
    assert_eq!(confidence_band(0.30), "mid");
    assert_eq!(confidence_band(0.59), "mid");
    assert_eq!(confidence_band(0.60), "high");
    assert_eq!(confidence_band(1.0), "high");
}

// ── SecurityResponse factories ──────────────────────────────────────────────

#[test]
fn rollback_factory_records_checkpoint_and_description() {
    let cp = SecurityCheckpoint::create("uhid", "ModX", b"p".to_vec());
    let id = uuid_like();
    let resp = SecurityResponse::for_rollback(id, cp.clone());
    assert_eq!(resp.kind, SecurityResponseKind::StateRollback);
    assert_eq!(resp.restored_checkpoint.as_ref().unwrap().id, cp.id);
    assert!(resp.description.contains(&cp.id.to_string()));
    assert!(resp.description.contains("ModX"));
}

fn uuid_like() -> uuid::Uuid {
    uuid::Uuid::new_v4()
}

// ── DefaultAnomalyEventDispatcher ───────────────────────────────────────────

#[test]
fn dispatcher_forwards_qualifying_signal() {
    let wd: Arc<dyn ISecurityWatchdog> = Arc::new(DefaultSecurityWatchdog::new());
    let disp = DefaultAnomalyEventDispatcher::new(Arc::clone(&wd));
    let sig = AnomalySignal::create(ThreatVector::MemoryAnomaly, 0.9, "m", "x", None);
    let r = disp.verify_and_dispatch(&sig, None, false);
    assert_eq!(r.outcome, AnomalyDispatchOutcome::Dispatched);
    assert!(r.response.is_some());
    assert_eq!(r.response.unwrap().kind, SecurityResponseKind::Composite);
}

#[test]
fn dispatcher_drops_below_threshold() {
    let wd: Arc<dyn ISecurityWatchdog> = Arc::new(DefaultSecurityWatchdog::new());
    let disp = DefaultAnomalyEventDispatcher::new(Arc::clone(&wd)); // default min 0.30
    let sig = AnomalySignal::create(ThreatVector::MemoryAnomaly, 0.2, "m", "low", None);
    let r = disp.verify_and_dispatch(&sig, None, false);
    assert_eq!(r.outcome, AnomalyDispatchOutcome::BelowThreshold);
    assert!(r.response.is_none());
    // Watchdog never saw it.
    assert!(wd.stream_signals().is_empty());
}

#[test]
fn dispatcher_dedupes_by_id() {
    let wd: Arc<dyn ISecurityWatchdog> = Arc::new(DefaultSecurityWatchdog::new());
    let disp = DefaultAnomalyEventDispatcher::new(Arc::clone(&wd));
    let sig = AnomalySignal::create(ThreatVector::MemoryAnomaly, 0.9, "m", "x", None);
    let r1 = disp.verify_and_dispatch(&sig, None, false);
    let r2 = disp.verify_and_dispatch(&sig, None, false);
    assert_eq!(r1.outcome, AnomalyDispatchOutcome::Dispatched);
    assert_eq!(r2.outcome, AnomalyDispatchOutcome::Duplicate);
    assert!(r2.response.is_none());
    assert_eq!(wd.stream_signals().len(), 1, "watchdog invoked once");
}

#[test]
fn dispatcher_reports_cancellation() {
    let wd: Arc<dyn ISecurityWatchdog> = Arc::new(DefaultSecurityWatchdog::new());
    let disp = DefaultAnomalyEventDispatcher::new(Arc::clone(&wd));
    let sig = AnomalySignal::create(ThreatVector::MemoryAnomaly, 0.9, "m", "x", None);
    let r = disp.verify_and_dispatch(&sig, None, true);
    assert_eq!(r.outcome, AnomalyDispatchOutcome::Cancelled);
}

#[test]
fn dispatcher_custom_threshold_is_clamped() {
    let wd: Arc<dyn ISecurityWatchdog> = Arc::new(DefaultSecurityWatchdog::new());
    // Above-1 threshold clamps to 1.0, so 0.99 falls below.
    let disp = DefaultAnomalyEventDispatcher::with_minimum_confidence(Arc::clone(&wd), 2.0);
    assert_eq!(disp.minimum_confidence(), 1.0);
    let sig = AnomalySignal::create(ThreatVector::MemoryAnomaly, 0.99, "m", "x", None);
    assert_eq!(
        disp.verify_and_dispatch(&sig, None, false).outcome,
        AnomalyDispatchOutcome::BelowThreshold
    );
}
