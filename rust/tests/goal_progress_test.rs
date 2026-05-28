//! goal_progress_test.rs
//!
//! 7 cross-language test vectors for Goal::advance_progress,
//! loaded from fixtures/goal_progress.json.
//! Progress is clamped to [0.0, 1.0].

use circle_ai::memory::{Goal, GoalPriority};
use serde::Deserialize;

const EPSILON: f32 = 1e-6_f32;

// ─────────────────────────────────────────────────────────────────────────────
// Fixture deserialization
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Deserialize)]
struct ProgressVector {
    id: String,
    initial_progress: f32,
    delta: f32,
    expected_progress: f32,
}

#[derive(Debug, Deserialize)]
struct Fixture {
    vectors: Vec<ProgressVector>,
}

fn load_fixture() -> Fixture {
    let fixtures_dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("fixtures");
    let path = fixtures_dir.join("goal_progress.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("Failed to read {:?}: {}", path, e));
    serde_json::from_str(&text).expect("Failed to parse goal_progress.json")
}

fn make_goal(progress: f32) -> Goal {
    let mut g = Goal::new("g1", "u1", "Test goal", "A test goal description", GoalPriority::Normal);
    g.progress = progress;
    g
}

// ─────────────────────────────────────────────────────────────────────────────
// Fixture-driven test
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_all_progress_vectors() {
    let fixture = load_fixture();
    assert_eq!(
        fixture.vectors.len(),
        7,
        "Expected 7 progress vectors, found {}",
        fixture.vectors.len()
    );

    for v in &fixture.vectors {
        let goal = make_goal(v.initial_progress);
        let advanced = goal.advance_progress(v.delta);
        let diff = (advanced.progress - v.expected_progress).abs();
        assert!(
            diff <= EPSILON,
            "[{}] progress: got {}, expected {}, diff={}",
            v.id,
            advanced.progress,
            v.expected_progress,
            diff
        );
        // Original goal must be unchanged (advance_progress returns a clone)
        assert_eq!(
            goal.progress, v.initial_progress,
            "[{}] original goal progress mutated",
            v.id
        );
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Individual pinned tests
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_zero_initial_zero_delta() {
    let g = make_goal(0.0);
    assert!((g.advance_progress(0.0).progress - 0.0).abs() <= EPSILON);
}

#[test]
fn test_partial_advance_from_zero() {
    let g = make_goal(0.0);
    let result = g.advance_progress(0.3);
    assert!((result.progress - 0.3).abs() <= EPSILON, "got {}", result.progress);
}

#[test]
fn test_clamp_at_max() {
    let g = make_goal(0.9);
    let result = g.advance_progress(0.5);
    assert!(
        (result.progress - 1.0).abs() <= EPSILON,
        "expected 1.0, got {}",
        result.progress
    );
}

#[test]
fn test_clamp_at_min() {
    let g = make_goal(0.1);
    let result = g.advance_progress(-0.5);
    assert!(
        (result.progress - 0.0).abs() <= EPSILON,
        "expected 0.0, got {}",
        result.progress
    );
}

#[test]
fn test_zero_delta_mid_progress() {
    let g = make_goal(0.5);
    let result = g.advance_progress(0.0);
    assert!((result.progress - 0.5).abs() <= EPSILON, "got {}", result.progress);
}

#[test]
fn test_advance_to_exactly_full() {
    let g = make_goal(0.75);
    let result = g.advance_progress(0.25);
    assert!(
        (result.progress - 1.0).abs() <= EPSILON,
        "expected 1.0, got {}",
        result.progress
    );
}

#[test]
fn test_negative_delta_no_floor_hit() {
    let g = make_goal(0.6);
    let result = g.advance_progress(-0.2);
    assert!(
        (result.progress - 0.4).abs() <= EPSILON,
        "expected 0.4, got {}",
        result.progress
    );
}

#[test]
fn test_advance_progress_does_not_mutate_original() {
    let g = make_goal(0.3);
    let _advanced = g.advance_progress(0.4);
    assert!(
        (g.progress - 0.3).abs() <= EPSILON,
        "original goal should not be mutated, got {}",
        g.progress
    );
}
