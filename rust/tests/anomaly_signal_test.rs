//! anomaly_signal_test.rs
//!
//! Cross-language confidence-clamp vectors for AnomalySignal::create.
//! Mirrors `fixtures/anomaly_signal_schema.json` `clamp_vectors`.

use std::collections::HashMap;

use circle_ai::security::{AnomalySignal, ThreatVector};

const EPSILON: f32 = 1e-5_f32;

fn approx_eq(a: f32, b: f32) -> bool {
    (a - b).abs() < EPSILON
}

// ─────────────────────────────────────────────────────────────────────────────
// Clamp vectors — must match fixtures/anomaly_signal_schema.json
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn clamp_above_max() {
    let s = AnomalySignal::create(
        ThreatVector::MemoryAnomaly,
        1.5,
        "Circle.AI.Test",
        "above_max",
        None,
    );
    assert!(
        approx_eq(s.confidence, 1.0),
        "above_max: got {}, expected 1.0",
        s.confidence
    );
}

#[test]
fn clamp_below_min() {
    let s = AnomalySignal::create(
        ThreatVector::MemoryAnomaly,
        -0.3,
        "Circle.AI.Test",
        "below_min",
        None,
    );
    assert!(
        approx_eq(s.confidence, 0.0),
        "below_min: got {}, expected 0.0",
        s.confidence
    );
}

#[test]
fn clamp_at_max() {
    let s = AnomalySignal::create(
        ThreatVector::MemoryAnomaly,
        1.0,
        "Circle.AI.Test",
        "at_max",
        None,
    );
    assert!(
        approx_eq(s.confidence, 1.0),
        "at_max: got {}, expected 1.0",
        s.confidence
    );
}

#[test]
fn clamp_at_min() {
    let s = AnomalySignal::create(
        ThreatVector::MemoryAnomaly,
        0.0,
        "Circle.AI.Test",
        "at_min",
        None,
    );
    assert!(
        approx_eq(s.confidence, 0.0),
        "at_min: got {}, expected 0.0",
        s.confidence
    );
}

#[test]
fn clamp_nominal() {
    let s = AnomalySignal::create(
        ThreatVector::MemoryAnomaly,
        0.7,
        "Circle.AI.Test",
        "nominal",
        None,
    );
    assert!(
        approx_eq(s.confidence, 0.7),
        "nominal: got {}, expected 0.7",
        s.confidence
    );
}

// ─────────────────────────────────────────────────────────────────────────────
// Factory-contract checks
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn create_stamps_unique_id_and_timestamp() {
    let a = AnomalySignal::create(
        ThreatVector::NetworkPivot,
        0.5,
        "Circle.AI.Test",
        "unique id",
        None,
    );
    let b = AnomalySignal::create(
        ThreatVector::NetworkPivot,
        0.5,
        "Circle.AI.Test",
        "unique id",
        None,
    );
    assert_ne!(a.id, b.id, "factory must stamp a fresh UUID per signal");
    assert!(b.detected_at >= a.detected_at, "timestamps monotonic in test");
}

#[test]
fn evidence_defaults_to_empty_when_none() {
    let s = AnomalySignal::create(
        ThreatVector::StateCorruption,
        0.5,
        "Circle.AI.Test",
        "no evidence",
        None,
    );
    assert!(s.evidence.is_empty());
}

#[test]
fn evidence_round_trips_when_provided() {
    let mut ev = HashMap::new();
    ev.insert("hash".to_string(), "deadbeef".to_string());
    ev.insert("module".to_string(), "biometric".to_string());

    let s = AnomalySignal::create(
        ThreatVector::BiometricSpoofAttempt,
        0.9,
        "Circle.AI.Identity",
        "spoof attempt",
        Some(ev),
    );
    assert_eq!(s.evidence.len(), 2);
    assert_eq!(s.evidence.get("hash").map(String::as_str), Some("deadbeef"));
    assert_eq!(s.evidence.get("module").map(String::as_str), Some("biometric"));
}

#[test]
fn threat_vector_ordinals_match_fixture() {
    // Stable across language ports — see fixtures/anomaly_signal_schema.json.
    assert_eq!(ThreatVector::MemoryAnomaly as u8, 0);
    assert_eq!(ThreatVector::ControlFlowDrift as u8, 1);
    assert_eq!(ThreatVector::PrivilegeEscalation as u8, 2);
    assert_eq!(ThreatVector::BiometricSpoofAttempt as u8, 3);
    assert_eq!(ThreatVector::NetworkPivot as u8, 4);
    assert_eq!(ThreatVector::StateCorruption as u8, 5);
    assert_eq!(ThreatVector::AgentPatchRejected as u8, 6);
    assert_eq!(ThreatVector::Unknown as u8, 7);
}
