//! biometric_matcher_test.rs
//!
//! Cross-language test vectors for BiometricMatcher::cosine_similarity and
//! BiometricMatcher::is_match, loaded from fixtures/facex_biometric_vectors.json.
//! All numeric comparisons use 1e-4 tolerance unless the fixture specifies 1e-5.

use circle_ai::identity::{BiometricMatcher, BiometricProfile};
use serde::Deserialize;

const EPSILON_5: f64 = 1e-5;
const EPSILON_4: f64 = 1e-4;

// ─────────────────────────────────────────────────────────────────────────────
// Fixture deserialization
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Deserialize)]
struct CosineSimilarityVector {
    id: String,
    a: Vec<f32>,
    b: Vec<f32>,
    expected_similarity: f64,
    tolerance: f64,
    #[serde(default)]
    expected_is_match_at_threshold_0_85: Option<bool>,
}

#[derive(Debug, Deserialize)]
struct Fixture {
    // The shared fixture JSON uses snake_case keys (cosine_similarity_vectors);
    // no rename_all so the field maps directly.
    cosine_similarity_vectors: Vec<CosineSimilarityVector>,
}

fn load_fixture() -> Fixture {
    let fixtures_dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("fixtures");
    let path = fixtures_dir.join("facex_biometric_vectors.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("Failed to read {:?}: {}", path, e));
    serde_json::from_str(&text).expect("Failed to parse facex_biometric_vectors.json")
}

// ─────────────────────────────────────────────────────────────────────────────
// Fixture-driven tests
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_cosine_similarity_all_vectors() {
    let fixture = load_fixture();
    assert!(
        !fixture.cosine_similarity_vectors.is_empty(),
        "Expected cosine_similarity_vectors to be non-empty"
    );

    for v in &fixture.cosine_similarity_vectors {
        let sim = BiometricMatcher::cosine_similarity(&v.a, &v.b);
        let diff = (sim - v.expected_similarity).abs();
        assert!(
            diff <= v.tolerance,
            "[{}] cosine_similarity: got {:.8}, expected {:.8}, diff={:.2e} > tol={:.2e}",
            v.id,
            sim,
            v.expected_similarity,
            diff,
            v.tolerance
        );

        if let Some(expected_match) = v.expected_is_match_at_threshold_0_85 {
            let profile = BiometricProfile {
                identity_id: "test".to_string(),
                embedding_vector: v.b.clone(),
                match_threshold: 0.85,
                enrolled_at: chrono::Utc::now(),
                last_match_at: None,
            };
            let result = BiometricMatcher::is_match(&v.a, &profile);
            assert_eq!(
                result, expected_match,
                "[{}] is_match at threshold 0.85: got {}, expected {}",
                v.id, result, expected_match
            );
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Individual pinned tests (canonical values)
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_identical_unit_vectors_2d() {
    let a = [0.6_f32, 0.8_f32];
    let b = [0.6_f32, 0.8_f32];
    let sim = BiometricMatcher::cosine_similarity(&a, &b);
    assert!(
        (sim - 1.0_f64).abs() <= EPSILON_5,
        "identical vectors: expected 1.0, got {sim}"
    );
}

#[test]
fn test_orthogonal_vectors_2d() {
    let a = [1.0_f32, 0.0_f32];
    let b = [0.0_f32, 1.0_f32];
    let sim = BiometricMatcher::cosine_similarity(&a, &b);
    assert!(
        sim.abs() <= EPSILON_5,
        "orthogonal vectors: expected 0.0, got {sim}"
    );
}

#[test]
fn test_opposite_vectors_2d() {
    let a = [1.0_f32, 0.0_f32];
    let b = [-1.0_f32, 0.0_f32];
    let sim = BiometricMatcher::cosine_similarity(&a, &b);
    assert!(
        (sim - (-1.0_f64)).abs() <= EPSILON_5,
        "opposite vectors: expected -1.0, got {sim}"
    );
}

#[test]
fn test_same_face_high_similarity_4d() {
    // From fixture: same_face_high_similarity_4d — expected ~0.999794, tol 1e-4
    let a = [0.5257_f32, 0.7236_f32, 0.2425_f32, 0.3780_f32];
    let b = [0.5133_f32, 0.7340_f32, 0.2511_f32, 0.3692_f32];
    let sim = BiometricMatcher::cosine_similarity(&a, &b);
    assert!(
        (sim - 0.999794_f64).abs() <= EPSILON_4,
        "same_face_4d: expected ~0.999794, got {sim}"
    );
}

#[test]
fn test_same_face_is_match_at_threshold_085() {
    let a = [0.5257_f32, 0.7236_f32, 0.2425_f32, 0.3780_f32];
    let b = [0.5133_f32, 0.7340_f32, 0.2511_f32, 0.3692_f32];
    let profile = BiometricProfile {
        identity_id: "same-face".to_string(),
        embedding_vector: b.to_vec(),
        match_threshold: 0.85,
        enrolled_at: chrono::Utc::now(),
        last_match_at: None,
    };
    assert!(
        BiometricMatcher::is_match(&a, &profile),
        "Same-face vectors must match at threshold 0.85"
    );
}

#[test]
fn test_different_face_is_not_match_at_threshold_085() {
    let a = [0.5257_f32, 0.7236_f32, 0.2425_f32, 0.3780_f32];
    let b = [-0.3015_f32, 0.6547_f32, 0.5893_f32, -0.3812_f32];
    let profile = BiometricProfile {
        identity_id: "diff-face".to_string(),
        embedding_vector: b.to_vec(),
        match_threshold: 0.85,
        enrolled_at: chrono::Utc::now(),
        last_match_at: None,
    };
    assert!(
        !BiometricMatcher::is_match(&a, &profile),
        "Different-face vectors must NOT match at threshold 0.85"
    );
}

#[test]
fn test_is_match_at_threshold_uses_gte_semantics() {
    // Identical vectors → similarity == 1.0, threshold == 0.85 → must match
    let v = [0.7071_f32, 0.7071_f32];
    let profile = BiometricProfile {
        identity_id: "exact".to_string(),
        embedding_vector: v.to_vec(),
        match_threshold: 0.85,
        enrolled_at: chrono::Utc::now(),
        last_match_at: None,
    };
    assert!(
        BiometricMatcher::is_match(&v, &profile),
        "Exact match at threshold (>=) must return true"
    );
}

#[test]
fn test_zero_vector_returns_zero_similarity() {
    // Near-zero vector: all components tiny
    let a = [1e-12_f32, 1e-12_f32];
    let b = [0.6_f32, 0.8_f32];
    let sim = BiometricMatcher::cosine_similarity(&a, &b);
    // magnitude of a is ~1.4e-12, below the 1e-10 floor → should return 0.0
    assert_eq!(sim, 0.0, "near-zero magnitude must return 0.0 similarity");
}

#[test]
fn test_embedding_dimension() {
    let v = vec![0.1_f32, 0.2, 0.3, 0.4];
    let profile = BiometricProfile::new("id-001", v);
    assert_eq!(profile.embedding_dimension(), 4);
}
