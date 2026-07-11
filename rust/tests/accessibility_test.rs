//! accessibility_test.rs
//!
//! Ports the behaviour of `CircleAI.Accessibility`: profile store + ordered
//! adaptation-hint derivation (contrast → motion → aria → text-scale → needs).

use circle_ai::accessibility::{
    AccessibilityNeed, IAccessibilityBoard, InMemoryAccessibilityBoard, UserAccessibilityProfile,
};

#[test]
fn hints_derived_in_order_with_formatting() {
    let board = InMemoryAccessibilityBoard::new();
    board.set_profile(UserAccessibilityProfile::new(
        "u1",
        vec![AccessibilityNeed::Visual, AccessibilityNeed::Motor],
        1.5,
        true,  // high contrast
        true,  // reduced motion
        true,  // screen reader
    ));

    let hints = board.hints_for("u1");
    // Expected order and values.
    let pairs: Vec<(String, String)> = hints.iter().map(|h| (h.kind.clone(), h.value.clone())).collect();
    assert_eq!(
        pairs,
        vec![
            ("contrast".to_string(), "high".to_string()),
            ("motion".to_string(), "reduced".to_string()),
            ("aria".to_string(), "verbose".to_string()),
            ("text-scale".to_string(), "1.50".to_string()), // F2 formatting
            ("need".to_string(), "Visual".to_string()),
            ("need".to_string(), "Motor".to_string()),
        ]
    );
}

#[test]
fn no_hints_when_flags_off_and_scale_one() {
    let board = InMemoryAccessibilityBoard::new();
    board.set_profile(UserAccessibilityProfile::new("u2", vec![], 1.0, false, false, false));
    assert!(board.hints_for("u2").is_empty());
}

#[test]
fn missing_profile_yields_no_hints() {
    let board = InMemoryAccessibilityBoard::new();
    assert!(board.get_profile("nobody").is_none());
    assert!(board.hints_for("nobody").is_empty());
}
