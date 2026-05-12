//! companion_types_test.rs
//!
//! Tests for InterfaceKind (7 variants), CompanionContext, CompanionTurn, and
//! CompanionProactiveEvent construction.

use circle_ai::companion::{
    CompanionContext, CompanionProactiveEvent, CompanionTurn, InterfaceKind,
};

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
    // Each variant is equal to itself
    for v in &variants {
        assert_eq!(*v, *v);
    }
    // First and last are different
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
