//! affect_state_test.rs
//!
//! 12 cross-language test vectors loaded from fixtures/affect_state.json.
//! All numeric comparisons use epsilon 1e-5 (f32).

use circle_ai::memory::AffectState;
use serde::Deserialize;
use std::collections::HashMap;

const EPSILON: f32 = 1e-5_f32;

fn approx_eq(a: f32, b: f32) -> bool {
    (a - b).abs() < EPSILON
}

// ─────────────────────────────────────────────────────────────────────────────
// Fixture deserialization helpers
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Deserialize)]
struct AffectStateFields {
    curiosity: f32,
    engagement: f32,
    uncertainty: f32,
    rapport: f32,
    energy: f32,
}

#[derive(Debug, Deserialize)]
struct Vector {
    id: String,
    input: AffectStateFields,
    operation: String,
    #[serde(rename = "operationParam")]
    operation_param: HashMap<String, serde_json::Value>,
    expected: AffectStateFields,
}

#[derive(Debug, Deserialize)]
struct Fixture {
    vectors: Vec<Vector>,
}

fn load_fixture() -> Fixture {
    let fixtures_dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("fixtures");
    let path = fixtures_dir.join("affect_state.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("Failed to read {:?}: {}", path, e));
    serde_json::from_str(&text).expect("Failed to parse affect_state.json")
}

fn make_state(f: &AffectStateFields) -> AffectState {
    let mut s = AffectState::new("test_user");
    s.curiosity = f.curiosity;
    s.engagement = f.engagement;
    s.uncertainty = f.uncertainty;
    s.rapport = f.rapport;
    s.energy = f.energy;
    s
}

fn assert_fields(id: &str, result: &AffectState, expected: &AffectStateFields) {
    assert!(
        approx_eq(result.curiosity, expected.curiosity),
        "[{}] curiosity: got {}, expected {}",
        id,
        result.curiosity,
        expected.curiosity
    );
    assert!(
        approx_eq(result.engagement, expected.engagement),
        "[{}] engagement: got {}, expected {}",
        id,
        result.engagement,
        expected.engagement
    );
    assert!(
        approx_eq(result.uncertainty, expected.uncertainty),
        "[{}] uncertainty: got {}, expected {}",
        id,
        result.uncertainty,
        expected.uncertainty
    );
    assert!(
        approx_eq(result.rapport, expected.rapport),
        "[{}] rapport: got {}, expected {}",
        id,
        result.rapport,
        expected.rapport
    );
    assert!(
        approx_eq(result.energy, expected.energy),
        "[{}] energy: got {}, expected {}",
        id,
        result.energy,
        expected.energy
    );
}

fn apply_operation(state: &mut AffectState, op: &str, param: &HashMap<String, serde_json::Value>) {
    let count = param
        .get("count")
        .and_then(|v| v.as_u64())
        .unwrap_or(1) as usize;

    let hours = param
        .get("hours")
        .and_then(|v| v.as_f64())
        .map(|h| h as f32);

    match op {
        "positive_signal" => {
            for _ in 0..count {
                state.apply_positive_signal();
            }
        }
        "negative_signal" => {
            for _ in 0..count {
                state.apply_negative_signal();
            }
        }
        "positive_then_negative" => {
            state.apply_positive_signal();
            state.apply_negative_signal();
        }
        "negative_then_positive" => {
            state.apply_negative_signal();
            state.apply_positive_signal();
        }
        "idle_decay" => {
            let h = hours.expect("idle_decay requires 'hours' param");
            state.apply_idle_decay(h);
        }
        other => panic!("Unknown operation: {}", other),
    }
}

#[test]
fn test_all_vectors() {
    let fixture = load_fixture();
    assert_eq!(
        fixture.vectors.len(),
        12,
        "Expected 12 test vectors, found {}",
        fixture.vectors.len()
    );

    for v in &fixture.vectors {
        let mut state = make_state(&v.input);
        apply_operation(&mut state, &v.operation, &v.operation_param);
        assert_fields(&v.id, &state, &v.expected);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Individual named tests for clarity in test output
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_positive_signal_once() {
    let mut s = AffectState::new("u1");
    s.apply_positive_signal();
    assert!(approx_eq(s.engagement, 0.52));
    assert!(approx_eq(s.rapport, 0.01));
    assert!(approx_eq(s.uncertainty, 0.18));
    assert!(approx_eq(s.curiosity, 0.5));
    assert!(approx_eq(s.energy, 0.5));
}

#[test]
fn test_negative_signal_once() {
    let mut s = AffectState::new("u1");
    s.apply_negative_signal();
    assert!(approx_eq(s.engagement, 0.47));
    assert!(approx_eq(s.uncertainty, 0.23));
    assert!(approx_eq(s.rapport, 0.0));
    assert!(approx_eq(s.energy, 0.5));
}

#[test]
fn test_idle_decay_1h() {
    let mut s = AffectState::new("u1");
    s.engagement = 0.8;
    s.energy = 0.7;
    s.apply_idle_decay(1.0);
    // decay = min(0.3, 1*0.02) = 0.02
    // engagement = 0.8 + (0.5 - 0.8) * 0.02 = 0.8 - 0.006 = 0.794
    // energy = 0.7 + (0.5 - 0.7) * 0.02 = 0.7 - 0.004 = 0.696
    assert!(approx_eq(s.engagement, 0.794));
    assert!(approx_eq(s.energy, 0.696));
    // These must not change
    assert!(approx_eq(s.curiosity, 0.5));
    assert!(approx_eq(s.rapport, 0.0));
    assert!(approx_eq(s.uncertainty, 0.2));
}

#[test]
fn test_idle_decay_24h_capped() {
    let mut s = AffectState::new("u1");
    s.engagement = 0.8;
    s.energy = 0.7;
    s.apply_idle_decay(24.0);
    // decay = min(0.3, 24*0.02) = 0.3 (capped)
    // engagement = 0.8 + (0.5 - 0.8) * 0.3 = 0.8 - 0.09 = 0.71
    // energy = 0.7 + (0.5 - 0.7) * 0.3 = 0.7 - 0.06 = 0.64
    assert!(approx_eq(s.engagement, 0.71));
    assert!(approx_eq(s.energy, 0.64));
}

#[test]
fn test_clamp_max_positive() {
    let mut s = AffectState::new("u1");
    s.engagement = 0.99;
    s.rapport = 0.99;
    s.uncertainty = 0.01;
    s.apply_positive_signal();
    assert!(approx_eq(s.engagement, 1.0));
    assert!(approx_eq(s.rapport, 1.0));
    assert!(approx_eq(s.uncertainty, 0.0));
}

#[test]
fn test_clamp_min_negative() {
    let mut s = AffectState::new("u1");
    s.engagement = 0.01;
    s.uncertainty = 0.98;
    s.apply_negative_signal();
    assert!(approx_eq(s.engagement, 0.0));
    assert!(approx_eq(s.uncertainty, 1.0));
}

#[test]
fn test_idle_decay_neutral_no_change() {
    let mut s = AffectState::new("u1");
    // Default state: engagement=0.5, energy=0.5
    let before_engagement = s.engagement;
    let before_energy = s.energy;
    s.apply_idle_decay(8.0);
    assert!(approx_eq(s.engagement, before_engagement));
    assert!(approx_eq(s.energy, before_energy));
}

#[test]
fn test_to_system_prompt_hint_empty() {
    // Default state: no dimension crosses thresholds
    let s = AffectState::new("u1");
    assert_eq!(s.to_system_prompt_hint(), "");
}

#[test]
fn test_to_system_prompt_hint_high_curiosity() {
    let mut s = AffectState::new("u1");
    s.curiosity = 0.75;
    let hint = s.to_system_prompt_hint();
    assert!(hint.contains("[Affect state]"));
    assert!(hint.contains("deeply curious"));
}

#[test]
fn test_to_system_prompt_hint_low_engagement() {
    let mut s = AffectState::new("u1");
    s.engagement = 0.25;
    let hint = s.to_system_prompt_hint();
    assert!(hint.contains("brief and to the point"));
}

#[test]
fn test_to_system_prompt_hint_high_rapport() {
    let mut s = AffectState::new("u1");
    s.rapport = 0.8;
    let hint = s.to_system_prompt_hint();
    assert!(hint.contains("warm, familiar tone"));
}
