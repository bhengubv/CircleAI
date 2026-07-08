//! theory_of_mind_test.rs
//!
//! Verifies the companion theory of mind: BeliefTrackerTheoryOfMind, which does
//! bag-of-belief inference with confidence decay over an interaction history.
//! Mirrors the C# ITheoryOfMind.EstimateAsync behaviour (belief-verb scanning,
//! believe-weighting, decay, JSON serialisation, confidence).

use circle_ai::companion::theory_of_mind::{
    BeliefTrackerTheoryOfMind, ITheoryOfMind,
};
use serde_json::Value;

fn beliefs_of(estimate: &circle_ai::companion::theory_of_mind::OtherMindEstimate) -> Value {
    serde_json::from_str(&estimate.likely_belief_json).expect("valid belief json")
}

#[test]
fn no_belief_verbs_yields_empty_map_and_zero_confidence() {
    let tom = BeliefTrackerTheoryOfMind::new();
    let e = tom.estimate("alice", "the weather report for today");
    assert_eq!(e.target_identifier, "alice");
    assert_eq!(e.confidence, 0.0);
    let map = beliefs_of(&e);
    assert!(map.as_object().unwrap().is_empty());
}

#[test]
fn extracts_a_single_belief_clause() {
    let tom = BeliefTrackerTheoryOfMind::new();
    let e = tom.estimate("bob", "bob thinks the meeting is cancelled.");
    let map = beliefs_of(&e);
    let obj = map.as_object().unwrap();
    assert_eq!(obj.len(), 1);
    // Key is "verb:claim"; claim is trimmed and stops at the period.
    let (k, v) = obj.iter().next().unwrap();
    assert_eq!(k, "thinks:the meeting is cancelled");
    // idx 0 ⇒ decay 1.0, non-believe verb ⇒ weight 0.7.
    assert!((v.as_f64().unwrap() - 0.7).abs() < 1e-9);
}

#[test]
fn believe_verbs_weigh_more_than_other_verbs() {
    let tom = BeliefTrackerTheoryOfMind::new();
    // A single believe-clause at idx 0 ⇒ weight 1.0 * decay 1.0 = 1.0.
    let e = tom.estimate("carol", "carol believes the plan will work.");
    let map = beliefs_of(&e);
    let (_, v) = map.as_object().unwrap().iter().next().unwrap();
    assert!((v.as_f64().unwrap() - 1.0).abs() < 1e-9);
}

#[test]
fn later_matches_decay() {
    let tom = BeliefTrackerTheoryOfMind::new();
    // Two non-believe clauses. First idx 0 (decay 1.0), second idx 1 (decay 1/1.1).
    let e = tom.estimate(
        "dave",
        "dave wants a raise; dave fears the layoffs.",
    );
    let map = beliefs_of(&e);
    let obj = map.as_object().unwrap();
    assert_eq!(obj.len(), 2);
    let wants = obj.get("wants:a raise").and_then(|v| v.as_f64()).unwrap();
    let fears = obj.get("fears:the layoffs").and_then(|v| v.as_f64()).unwrap();
    // wants = 0.7 * 1.0 ; fears = 0.7 * (1/1.1) → strictly smaller.
    assert!((wants - 0.7).abs() < 1e-9);
    assert!((fears - 0.7 / 1.1).abs() < 1e-9);
    assert!(fears < wants);
}

#[test]
fn confidence_is_sum_over_five_capped_at_one() {
    let tom = BeliefTrackerTheoryOfMind::new();
    let e = tom.estimate("erin", "erin thinks it is fine.");
    // One clause weight 0.7 ⇒ conf 0.7/5 = 0.14.
    assert!((e.confidence - 0.7 / 5.0).abs() < 1e-9);
}

#[test]
fn confidence_saturates_at_one() {
    let tom = BeliefTrackerTheoryOfMind::new();
    // Many distinct believe clauses drive the summed weight well past 5.
    let mut history = String::new();
    for i in 0..12 {
        history.push_str(&format!("frank believes claim number {i} is true. "));
    }
    let e = tom.estimate("frank", &history);
    assert!(e.confidence <= 1.0);
    assert!((e.confidence - 1.0).abs() < 1e-9, "expected saturation, got {}", e.confidence);
}

#[test]
fn matching_is_case_insensitive() {
    let tom = BeliefTrackerTheoryOfMind::new();
    let e = tom.estimate("gina", "Gina THINKS the door is open.");
    let map = beliefs_of(&e);
    let obj = map.as_object().unwrap();
    assert_eq!(obj.len(), 1);
    // Verb is lower-cased in the key.
    assert!(obj.contains_key("thinks:the door is open"));
}

#[test]
fn stops_the_claim_at_terminators() {
    let tom = BeliefTrackerTheoryOfMind::new();
    // The claim capture is [^.;!?]+ — it must not swallow the exclamation.
    let e = tom.estimate("hank", "hank hopes we win! then we celebrate.");
    let map = beliefs_of(&e);
    assert!(map.as_object().unwrap().contains_key("hopes:we win"));
}

#[test]
#[should_panic(expected = "target required")]
fn rejects_blank_target() {
    let tom = BeliefTrackerTheoryOfMind::new();
    tom.estimate("   ", "anything");
}
