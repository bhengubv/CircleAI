//! companion_types_test.rs
//!
//! Tests for:
//!   - InterfaceKind (7 variants)
//!   - CompanionContext, CompanionTurn, CompanionProactiveEvent construction
//!   - FaceAffectMapper::apply (vectors from fixtures/facex_biometric_vectors.json)

use circle_ai::companion::{
    CompanionContext, CompanionProactiveEvent, CompanionTurn, InterfaceKind,
};
use circle_ai::companion::face_affect_mapper;
use circle_ai::memory::AffectState;
use circle_ai::tools::facial_metric::{
    FaceBoundingBox, FaceExpressionClassification, FacialMetricMatrix,
};
use serde::Deserialize;

const EPSILON: f32 = 1e-5_f32;

fn approx_eq(a: f32, b: f32) -> bool {
    (a - b).abs() <= EPSILON
}

// ─────────────────────────────────────────────────────────────────────────────
// InterfaceKind — 7 variants
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_interface_kind_has_seven_variants() {
    let variants = [
        InterfaceKind::Mobile,
        InterfaceKind::Wearable,
        InterfaceKind::Desktop,
        InterfaceKind::Web,
        InterfaceKind::IoT,
        InterfaceKind::Ambient,
        InterfaceKind::Headless,
    ];
    assert_eq!(variants.len(), 7);
}

#[test]
fn test_interface_kind_variants_are_distinct() {
    let variants = [
        InterfaceKind::Mobile,
        InterfaceKind::Wearable,
        InterfaceKind::Desktop,
        InterfaceKind::Web,
        InterfaceKind::IoT,
        InterfaceKind::Ambient,
        InterfaceKind::Headless,
    ];
    for v in &variants {
        assert_eq!(*v, *v);
    }
    assert_ne!(variants[0], variants[6]);
}

#[test]
fn test_interface_kind_clone() {
    let v = InterfaceKind::Mobile;
    let v2 = v;
    assert_eq!(v, v2);
}

#[test]
fn test_interface_kind_debug() {
    assert_eq!(format!("{:?}", InterfaceKind::Mobile), "Mobile");
    assert_eq!(format!("{:?}", InterfaceKind::Headless), "Headless");
    assert_eq!(format!("{:?}", InterfaceKind::IoT), "IoT");
    assert_eq!(format!("{:?}", InterfaceKind::Ambient), "Ambient");
    assert_eq!(format!("{:?}", InterfaceKind::Wearable), "Wearable");
    assert_eq!(format!("{:?}", InterfaceKind::Desktop), "Desktop");
    assert_eq!(format!("{:?}", InterfaceKind::Web), "Web");
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionTurn
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_companion_turn_user() {
    let turn = CompanionTurn::user("Hello, B!");
    assert_eq!(turn.role, "user");
    assert_eq!(turn.content, "Hello, B!");
}

#[test]
fn test_companion_turn_assistant() {
    let turn = CompanionTurn::assistant("Hi! How can I help?");
    assert_eq!(turn.role, "assistant");
    assert_eq!(turn.content, "Hi! How can I help?");
}

#[test]
fn test_companion_turn_new() {
    let turn = CompanionTurn::new("system", "You are B!, a helpful AI.");
    assert_eq!(turn.role, "system");
    assert_eq!(turn.content, "You are B!, a helpful AI.");
}

#[test]
fn test_companion_turn_has_timestamp() {
    let before = chrono::Utc::now();
    let turn = CompanionTurn::user("test");
    let after = chrono::Utc::now();
    assert!(turn.timestamp >= before);
    assert!(turn.timestamp <= after);
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionContext
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_companion_context_construction() {
    let ctx = CompanionContext::new(
        "identity-001",
        "Sipho",
        Some("zu".to_string()),
        InterfaceKind::Mobile,
        "[User preferences]\nKeep responses brief.\n",
        "[Affect state]\nYou are fully engaged.\n",
        vec!["User asked about weather".to_string()],
        vec!["Learn isiZulu".to_string()],
    );

    assert_eq!(ctx.identity_id, "identity-001");
    assert_eq!(ctx.display_name, "Sipho");
    assert_eq!(ctx.preferred_language.as_deref(), Some("zu"));
    assert_eq!(ctx.interface, InterfaceKind::Mobile);
    assert!(ctx.persona_hints.contains("brief"));
    assert!(ctx.affect_summary.contains("engaged"));
    assert_eq!(ctx.recent_memory_snippets.len(), 1);
    assert_eq!(ctx.active_goals.len(), 1);
}

#[test]
fn test_companion_context_no_language() {
    let ctx = CompanionContext::new(
        "id-anon",
        "Guest",
        None,
        InterfaceKind::IoT,
        "",
        "",
        vec![],
        vec![],
    );

    assert!(ctx.preferred_language.is_none());
    assert_eq!(ctx.interface, InterfaceKind::IoT);
    assert!(ctx.recent_memory_snippets.is_empty());
    assert!(ctx.active_goals.is_empty());
}

#[test]
fn test_companion_context_has_timestamp() {
    let before = chrono::Utc::now();
    let ctx = CompanionContext::new(
        "id",
        "User",
        None,
        InterfaceKind::Headless,
        "",
        "",
        vec![],
        vec![],
    );
    let after = chrono::Utc::now();
    assert!(ctx.context_built_at >= before);
    assert!(ctx.context_built_at <= after);
}

// ─────────────────────────────────────────────────────────────────────────────
// CompanionProactiveEvent
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_proactive_event_construction() {
    let event = CompanionProactiveEvent::new(
        "session-001",
        "identity-001",
        InterfaceKind::Wearable,
        "Don't forget your goal: Learn isiZulu!",
        "goal_check_in",
    );

    assert_eq!(event.session_id, "session-001");
    assert_eq!(event.identity_id, "identity-001");
    assert_eq!(event.interface, InterfaceKind::Wearable);
    assert_eq!(event.message, "Don't forget your goal: Learn isiZulu!");
    assert_eq!(event.trigger_name, "goal_check_in");
}

#[test]
fn test_proactive_event_has_timestamp() {
    let before = chrono::Utc::now();
    let event = CompanionProactiveEvent::new(
        "s", "i", InterfaceKind::Ambient, "Hey!", "ping",
    );
    let after = chrono::Utc::now();
    assert!(event.generated_at >= before);
    assert!(event.generated_at <= after);
}

#[test]
fn test_proactive_event_all_interface_kinds() {
    let kinds = [
        InterfaceKind::Mobile,
        InterfaceKind::Wearable,
        InterfaceKind::Desktop,
        InterfaceKind::Web,
        InterfaceKind::IoT,
        InterfaceKind::Ambient,
        InterfaceKind::Headless,
    ];
    for kind in &kinds {
        let event = CompanionProactiveEvent::new("s", "i", *kind, "msg", "trigger");
        assert_eq!(event.interface, *kind);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FaceAffectMapper fixture helpers
// ─────────────────────────────────────────────────────────────────────────────

#[derive(Debug, Deserialize)]
struct AffectFields {
    curiosity: f32,
    engagement: f32,
    uncertainty: f32,
    rapport: f32,
    energy: f32,
}

#[derive(Debug, Deserialize)]
struct AffectMapperVector {
    id: String,
    initial_affect: AffectFields,
    expression: String,
    confidence: f32,
    expected_affect: AffectFields,
    tolerance: f32,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct FacexFixture {
    affect_mapper_vectors: Vec<AffectMapperVector>,
}

fn load_facex_fixture() -> FacexFixture {
    let fixtures_dir = std::path::Path::new(env!("CARGO_MANIFEST_DIR"))
        .parent()
        .unwrap()
        .join("fixtures");
    let path = fixtures_dir.join("facex_biometric_vectors.json");
    let text = std::fs::read_to_string(&path)
        .unwrap_or_else(|e| panic!("Failed to read {:?}: {}", path, e));
    serde_json::from_str(&text).expect("Failed to parse facex_biometric_vectors.json")
}

fn expression_from_str(s: &str) -> FaceExpressionClassification {
    match s {
        "Neutral"   => FaceExpressionClassification::Neutral,
        "Happy"     => FaceExpressionClassification::Happy,
        "Sad"       => FaceExpressionClassification::Sad,
        "Surprised" => FaceExpressionClassification::Surprised,
        "Confused"  => FaceExpressionClassification::Confused,
        "Stressed"  => FaceExpressionClassification::Stressed,
        "Angry"     => FaceExpressionClassification::Angry,
        other       => panic!("Unknown expression in fixture: {}", other),
    }
}

fn make_matrix(expression: FaceExpressionClassification, confidence: f32) -> FacialMetricMatrix {
    FacialMetricMatrix {
        landmarks: [0.0_f32; 136],
        bounding_box: FaceBoundingBox { x: 0.0, y: 0.0, width: 1.0, height: 1.0 },
        expression,
        confidence_score: confidence,
        captured_at: chrono::Utc::now(),
    }
}

fn make_affect(f: &AffectFields) -> AffectState {
    let mut s = AffectState::new("test");
    s.curiosity    = f.curiosity;
    s.engagement   = f.engagement;
    s.uncertainty  = f.uncertainty;
    s.rapport      = f.rapport;
    s.energy       = f.energy;
    s
}

fn assert_affect(id: &str, result: &AffectState, expected: &AffectFields, tol: f32) {
    assert!(
        approx_eq_tol(result.curiosity,   expected.curiosity,   tol), "[{}] curiosity: got {}, expected {}", id, result.curiosity, expected.curiosity);
    assert!(
        approx_eq_tol(result.engagement,  expected.engagement,  tol), "[{}] engagement: got {}, expected {}", id, result.engagement, expected.engagement);
    assert!(
        approx_eq_tol(result.uncertainty, expected.uncertainty, tol), "[{}] uncertainty: got {}, expected {}", id, result.uncertainty, expected.uncertainty);
    assert!(
        approx_eq_tol(result.rapport,     expected.rapport,     tol), "[{}] rapport: got {}, expected {}", id, result.rapport, expected.rapport);
    assert!(
        approx_eq_tol(result.energy,      expected.energy,      tol), "[{}] energy: got {}, expected {}", id, result.energy, expected.energy);
}

fn approx_eq_tol(a: f32, b: f32, tol: f32) -> bool {
    (a - b).abs() <= tol
}

// ─────────────────────────────────────────────────────────────────────────────
// FaceAffectMapper — fixture-driven tests
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_face_affect_mapper_all_vectors() {
    let fixture = load_facex_fixture();
    assert!(
        !fixture.affect_mapper_vectors.is_empty(),
        "affect_mapper_vectors must not be empty"
    );

    for v in &fixture.affect_mapper_vectors {
        let mut affect = make_affect(&v.initial_affect);
        let matrix = make_matrix(expression_from_str(&v.expression), v.confidence);
        face_affect_mapper::apply(&matrix, &mut affect);
        assert_affect(&v.id, &affect, &v.expected_affect, v.tolerance);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// FaceAffectMapper — individual pinned tests
// ─────────────────────────────────────────────────────────────────────────────

#[test]
fn test_happy_from_neutral() {
    // Happy: engagement += 0.03, energy += 0.02
    let mut affect = AffectState::new("u");
    // default: engagement=0.5, energy=0.5
    let matrix = make_matrix(FaceExpressionClassification::Happy, 0.92);
    face_affect_mapper::apply(&matrix, &mut affect);
    assert!(approx_eq(affect.engagement, 0.53), "engagement {}", affect.engagement);
    assert!(approx_eq(affect.energy,     0.52), "energy {}",     affect.energy);
    assert!(approx_eq(affect.curiosity,  0.5),  "curiosity unchanged");
    assert!(approx_eq(affect.uncertainty, 0.2), "uncertainty unchanged");
    assert!(approx_eq(affect.rapport,    0.0),  "rapport unchanged");
}

#[test]
fn test_surprised_increments_curiosity() {
    let mut affect = AffectState::new("u");
    let matrix = make_matrix(FaceExpressionClassification::Surprised, 0.88);
    face_affect_mapper::apply(&matrix, &mut affect);
    assert!(approx_eq(affect.curiosity, 0.54), "curiosity {}", affect.curiosity);
    assert!(approx_eq(affect.engagement, 0.5), "engagement unchanged");
}

#[test]
fn test_confused_increments_uncertainty() {
    let mut affect = AffectState::new("u");
    let matrix = make_matrix(FaceExpressionClassification::Confused, 0.79);
    face_affect_mapper::apply(&matrix, &mut affect);
    assert!(approx_eq(affect.uncertainty, 0.25), "uncertainty {}", affect.uncertainty);
}

#[test]
fn test_stressed_increments_uncertainty_decrements_energy() {
    let mut affect = AffectState::new("u");
    let matrix = make_matrix(FaceExpressionClassification::Stressed, 0.85);
    face_affect_mapper::apply(&matrix, &mut affect);
    assert!(approx_eq(affect.uncertainty, 0.28), "uncertainty {}", affect.uncertainty);
    assert!(approx_eq(affect.energy,      0.45), "energy {}", affect.energy);
}

#[test]
fn test_angry_decrements_engagement_and_rapport() {
    let mut affect = AffectState::new("u");
    affect.rapport = 0.3;
    let matrix = make_matrix(FaceExpressionClassification::Angry, 0.91);
    face_affect_mapper::apply(&matrix, &mut affect);
    assert!(approx_eq(affect.engagement, 0.46), "engagement {}", affect.engagement);
    assert!(approx_eq(affect.rapport,    0.28), "rapport {}", affect.rapport);
}

#[test]
fn test_neutral_expression_no_change() {
    let mut affect = AffectState::new("u");
    let matrix = make_matrix(FaceExpressionClassification::Neutral, 0.95);
    face_affect_mapper::apply(&matrix, &mut affect);
    // All dimensions must remain at defaults
    assert!(approx_eq(affect.curiosity,   0.5));
    assert!(approx_eq(affect.engagement,  0.5));
    assert!(approx_eq(affect.uncertainty, 0.2));
    assert!(approx_eq(affect.rapport,     0.0));
    assert!(approx_eq(affect.energy,      0.5));
}

#[test]
fn test_low_confidence_discarded() {
    let mut affect = AffectState::new("u");
    // confidence 0.49 < MIN_CONFIDENCE 0.5 → no change
    let matrix = make_matrix(FaceExpressionClassification::Stressed, 0.49);
    face_affect_mapper::apply(&matrix, &mut affect);
    assert!(approx_eq(affect.uncertainty, 0.2), "must not change when confidence < 0.5");
    assert!(approx_eq(affect.energy, 0.5), "must not change when confidence < 0.5");
}

#[test]
fn test_clamp_max_engagement() {
    // Happy from near-max engagement: 0.99 + 0.03 > 1.0 → should clamp to 1.0
    let mut affect = AffectState::new("u");
    affect.engagement = 0.99;
    let matrix = make_matrix(FaceExpressionClassification::Happy, 0.95);
    face_affect_mapper::apply(&matrix, &mut affect);
    assert!(approx_eq(affect.engagement, 1.0), "engagement must clamp to 1.0");
    assert!(approx_eq(affect.energy, 0.52), "energy should still apply normally");
}
