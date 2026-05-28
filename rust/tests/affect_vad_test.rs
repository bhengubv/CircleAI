//! affect_vad_test.rs
//!
//! Cross-language derivation vectors for AffectVad::from_state. Mirrors
//! `fixtures/affect_vad_derivation.json` — all comparisons use epsilon 1e-5.

use circle_ai::memory::{AffectState, AffectVad};
use serde::Deserialize;

const EPSILON: f32 = 1e-5_f32;

fn approx_eq(a: f32, b: f32) -> bool {
    (a - b).abs() < EPSILON
}

fn make_state(curiosity: f32, engagement: f32, uncertainty: f32, rapport: f32, energy: f32) -> AffectState {
    let mut s = AffectState::new("test_user");
    s.curiosity = curiosity;
    s.engagement = engagement;
    s.uncertainty = uncertainty;
    s.rapport = rapport;
    s.energy = energy;
    s
}

fn assert_vad(id: &str, vad: &AffectVad, expected_v: f32, expected_a: f32, expected_d: f32) {
    assert!(
        approx_eq(vad.valence, expected_v),
        "[{}] valence: got {}, expected {}",
        id,
        vad.valence,
        expected_v
    );
    assert!(
        approx_eq(vad.arousal, expected_a),
        "[{}] arousal: got {}, expected {}",
        id,
        vad.arousal,
        expected_a
    );
    assert!(
        approx_eq(vad.dominance, expected_d),
        "[{}] dominance: got {}, expected {}",
        id,
        vad.dominance,
        expected_d
    );
}

// ─────────────────────────────────────────────────────────────────────────────
// Hand-written named tests — one per fixture vector
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn default_state() {
    let state = make_state(0.5, 0.5, 0.2, 0.0, 0.5);
    let vad = AffectVad::from_state(&state);
    assert_vad("default_state", &vad, 0.43333333, 0.425, 0.65);
}

#[test]
fn all_max() {
    let state = make_state(1.0, 1.0, 0.0, 1.0, 1.0);
    let vad = AffectVad::from_state(&state);
    assert_vad("all_max", &vad, 1.0, 0.75, 1.0);
}

#[test]
fn all_min_high_uncertainty() {
    let state = make_state(0.0, 0.0, 1.0, 0.0, 0.0);
    let vad = AffectVad::from_state(&state);
    assert_vad("all_min_high_uncertainty", &vad, 0.0, 0.25, 0.0);
}

#[test]
fn high_engagement_warm() {
    let state = make_state(0.6, 0.9, 0.1, 0.8, 0.7);
    let vad = AffectVad::from_state(&state);
    assert_vad("high_engagement_warm", &vad, 0.86666667, 0.525, 0.9);
}

#[test]
fn stressed_low_energy() {
    let state = make_state(0.3, 0.2, 0.8, 0.0, 0.2);
    let vad = AffectVad::from_state(&state);
    assert_vad("stressed_low_energy", &vad, 0.13333333, 0.375, 0.2);
}

#[test]
fn energetic_curious() {
    let state = make_state(0.9, 0.6, 0.3, 0.4, 0.9);
    let vad = AffectVad::from_state(&state);
    assert_vad("energetic_curious", &vad, 0.56666667, 0.75, 0.65);
}

// ─────────────────────────────────────────────────────────────────────────────
// Extension method parity
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn to_vad_matches_from_state() {
    let state = make_state(0.7, 0.6, 0.3, 0.4, 0.5);
    let direct = AffectVad::from_state(&state);
    let ext = state.to_vad();
    assert!(approx_eq(direct.valence, ext.valence));
    assert!(approx_eq(direct.arousal, ext.arousal));
    assert!(approx_eq(direct.dominance, ext.dominance));
}

// ─────────────────────────────────────────────────────────────────────────────
// Fixture-driven coverage — reads the same JSON as every other language port
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Deserialize)]
struct VadFields {
    valence: f32,
    arousal: f32,
    dominance: f32,
}

#[derive(Debug, Deserialize)]
struct StateFields {
    curiosity: f32,
    engagement: f32,
    uncertainty: f32,
    rapport: f32,
    energy: f32,
}

#[derive(Debug, Deserialize)]
struct Vector {
    id: String,
    input: StateFields,
    expected: VadFields,
}

#[derive(Debug, Deserialize)]
struct Fixture {
    epsilon: f32,
    vectors: Vec<Vector>,
}

fn load_fixture() -> Fixture {
    let fixtures_dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .expect("rust/ has a parent")
        .join("fixtures");
    let path = fixtures_dir.join("affect_vad_derivation.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("Failed to read {:?}: {}", path, e));
    serde_json::from_str(&text).expect("Failed to parse affect_vad_derivation.json")
}

#[test]
fn all_fixture_vectors_match_derivation() {
    let fixture = load_fixture();
    assert_eq!(
        fixture.vectors.len(),
        6,
        "Expected 6 fixture vectors, found {}",
        fixture.vectors.len()
    );

    let eps = fixture.epsilon;

    for v in &fixture.vectors {
        let state = make_state(
            v.input.curiosity,
            v.input.engagement,
            v.input.uncertainty,
            v.input.rapport,
            v.input.energy,
        );
        let vad = AffectVad::from_state(&state);

        assert!(
            (vad.valence - v.expected.valence).abs() <= eps,
            "[{}] valence: got {}, expected {}",
            v.id,
            vad.valence,
            v.expected.valence
        );
        assert!(
            (vad.arousal - v.expected.arousal).abs() <= eps,
            "[{}] arousal: got {}, expected {}",
            v.id,
            vad.arousal,
            v.expected.arousal
        );
        assert!(
            (vad.dominance - v.expected.dominance).abs() <= eps,
            "[{}] dominance: got {}, expected {}",
            v.id,
            vad.dominance,
            v.expected.dominance
        );
    }
}
