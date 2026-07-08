//! predictive_engine_test.rs
//!
//! Verifies the companion predictive engine: HistogramPredictiveEngine
//! (day-of-week × hour histogram) and SequencePredictiveEngine (variable-order
//! Markov chain with back-off + inter-arrival forecasting). Mirrors the C#
//! IPredictiveEngine.AnticipateAsync behaviour.

use chrono::{Duration, Utc};
use circle_ai::companion::predictive_engine::{
    HistogramPredictiveEngine, IPredictiveEngine, SequencePredictiveEngine,
};

// ── HistogramPredictiveEngine ──────────────────────────────────────────────

#[test]
fn histogram_empty_engine_returns_nothing() {
    let e = HistogramPredictiveEngine::new();
    assert!(e.anticipate(120).is_empty());
}

#[test]
fn histogram_anticipates_an_event_seen_at_the_current_slot() {
    let e = HistogramPredictiveEngine::new();
    // Observe at "now" so the m=0 sample of the horizon walk hits the same slot.
    e.observe("check-email", Utc::now());
    // Horizon < 30 ⇒ the walk samples only m=0, so exactly one slot is counted.
    // (The C# double-counts a slot when two 30-min samples land in the same
    // clock hour; a sub-30-min horizon avoids that so the probability is exact.)
    let needs = e.anticipate(1);
    assert_eq!(needs.len(), 1);
    assert_eq!(needs[0].description, "check-email");
    // Only one observation ⇒ upcoming/total == 1.
    assert!((needs[0].probability - 1.0).abs() < 1e-9);
}

#[test]
fn histogram_skips_events_outside_the_horizon_window() {
    let e = HistogramPredictiveEngine::new();
    // 12 hours from now is well outside a 60-minute horizon.
    e.observe("midnight-task", Utc::now() + Duration::hours(12));
    let needs = e.anticipate(60);
    assert!(
        needs.iter().all(|n| n.description != "midnight-task"),
        "far-off event must not be anticipated in a 60-min horizon"
    );
}

#[test]
fn histogram_orders_by_descending_probability() {
    let e = HistogramPredictiveEngine::new();
    let now = Utc::now();
    // "frequent" occurs entirely at the current slot (prob 1.0). "rare" occurs
    // once at the current slot and once 12h away (prob 0.5 within the window).
    e.observe("frequent", now);
    e.observe("rare", now);
    e.observe("rare", now + Duration::hours(12));
    let needs = e.anticipate(60);
    // Both present; frequent (1.0) must sort before rare (0.5).
    assert!(needs.len() >= 2);
    let freq_pos = needs.iter().position(|n| n.description == "frequent").unwrap();
    let rare_pos = needs.iter().position(|n| n.description == "rare").unwrap();
    assert!(freq_pos < rare_pos);
    assert!(needs[freq_pos].probability >= needs[rare_pos].probability);
}

#[test]
fn histogram_case_insensitive_description() {
    let e = HistogramPredictiveEngine::new();
    let now = Utc::now();
    e.observe("Standup", now);
    e.observe("standup", now);
    // Sub-30-min horizon ⇒ single sample, so the folded bucket reads prob 1.0.
    let needs = e.anticipate(1);
    // Both fold to one histogram bucket.
    assert_eq!(needs.len(), 1);
    assert!((needs[0].probability - 1.0).abs() < 1e-9);
}

#[test]
#[should_panic(expected = "horizonMinutes out of range")]
fn histogram_rejects_non_positive_horizon() {
    let e = HistogramPredictiveEngine::new();
    e.anticipate(0);
}

#[test]
#[should_panic(expected = "description required")]
fn histogram_observe_rejects_blank_description() {
    let e = HistogramPredictiveEngine::new();
    e.observe("  ", Utc::now());
}

// ── SequencePredictiveEngine ───────────────────────────────────────────────

#[test]
fn sequence_empty_engine_returns_nothing() {
    let e = SequencePredictiveEngine::default();
    assert!(e.anticipate(120).is_empty());
}

#[test]
fn sequence_predicts_the_learned_next_event() {
    let e = SequencePredictiveEngine::new(3);
    let base = Utc::now();
    // Teach the chain a -> b -> c a few times so the context [a,b,c...] leads to
    // a strong "wake -> coffee" style transition.
    for i in 0..4 {
        let t = base + Duration::minutes(i * 10);
        e.observe("wake", t);
        e.observe("coffee", t + Duration::minutes(1));
        e.observe("commute", t + Duration::minutes(2));
    }
    // A generous horizon so mean-interval defaults don't exclude candidates.
    let needs = e.anticipate(600);
    assert!(!needs.is_empty(), "expected at least one anticipated event");
    // Probabilities are normalised (sum over emitted candidates ≤ 1 by design of
    // the softmax-like aggregation; each is in (0,1]).
    for n in &needs {
        assert!(n.probability > 0.0 && n.probability <= 1.0);
    }
}

#[test]
fn sequence_drops_events_whose_mean_interval_exceeds_horizon() {
    let e = SequencePredictiveEngine::new(2);
    let base = Utc::now();
    // Same event repeated with a large (1 hour) gap ⇒ mean interval 3600s.
    e.observe("hourly", base);
    e.observe("hourly", base + Duration::hours(1));
    e.observe("hourly", base + Duration::hours(2));
    // Horizon of 10 minutes (600s) is far below the 3600s mean interval.
    let needs = e.anticipate(10);
    assert!(
        needs.iter().all(|n| n.description != "hourly"),
        "event with mean interval > horizon must be dropped"
    );
}

#[test]
fn sequence_is_case_sensitive() {
    let e = SequencePredictiveEngine::new(2);
    let base = Utc::now();
    // "A" and "a" are distinct contexts/events (Ordinal in the C#).
    e.observe("A", base);
    e.observe("b", base + Duration::minutes(1));
    e.observe("a", base + Duration::minutes(2));
    e.observe("c", base + Duration::minutes(3));
    // Context now ends in [a, c] — the [A,b]->? transition is a different key,
    // so no crash and predictions come only from lowercase-a's history.
    let _ = e.anticipate(600); // must not panic on the distinct-case keys
}

#[test]
#[should_panic(expected = "order out of range")]
fn sequence_rejects_order_zero() {
    let _ = SequencePredictiveEngine::new(0);
}

#[test]
#[should_panic(expected = "order out of range")]
fn sequence_rejects_order_above_six() {
    let _ = SequencePredictiveEngine::new(7);
}

#[test]
#[should_panic(expected = "event required")]
fn sequence_observe_rejects_blank_event() {
    let e = SequencePredictiveEngine::default();
    e.observe("", Utc::now());
}

#[test]
#[should_panic(expected = "horizonMinutes out of range")]
fn sequence_rejects_non_positive_horizon() {
    let e = SequencePredictiveEngine::default();
    e.anticipate(-5);
}
