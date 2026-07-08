//! feedback_analyser_test.rs
//!
//! Exercises FeedbackAnalyser (persona-adaptation deltas from a window of
//! signals) and the InMemoryFeedbackStore. Mirrors the TS suite
//! tests/feedback_analyser.test.ts and the C# FeedbackAnalyser rules 1:1.

use chrono::{TimeZone, Utc};
use circle_ai::memory::feedback_analyser::{FeedbackAnalyser, InMemoryFeedbackStore};
use circle_ai::memory::stores::IFeedbackStore;
use circle_ai::memory::{FeedbackPolarity, FeedbackSignal};
use uuid::Uuid;

// FP32 deltas — must equal the C# `float` literals exactly.
const VERBOSITY_DOWN: f32 = -0.1;
const VERBOSITY_UP: f32 = 0.05;

/// Builds a signal with the given polarity and timestamp (monotonic default so
/// window ordering is deterministic per call).
fn make(polarity: FeedbackPolarity, at: chrono::DateTime<Utc>, user: &str) -> FeedbackSignal {
    FeedbackSignal {
        id: Uuid::new_v4(),
        recorded_at_utc: at,
        episode_id: None,
        user_text: user.to_string(),
        assistant_text: "response".to_string(),
        polarity,
        corrected_text: None,
        comment: None,
    }
}

/// Deterministic monotonic timestamps (seq seconds past a fixed epoch).
fn ts(seq: i64) -> chrono::DateTime<Utc> {
    Utc.timestamp_opt(1_700_000_000 + seq, 0).unwrap()
}

// ══════════════════════════════════════════════════════════════════════════
// FeedbackAnalyser
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn rejects_a_window_size_below_1() {
    assert!(FeedbackAnalyser::new(0).is_err());
}

#[test]
fn returns_zero_deltas_for_an_empty_signal_set() {
    let a = FeedbackAnalyser::default().analyse(Vec::<FeedbackSignal>::new());
    assert_eq!(a.verbosity_delta, 0.0);
    assert_eq!(a.formality_delta, 0.0);
    assert!(a.preferred_topics.is_empty());
}

#[test]
fn drops_verbosity_by_negative_when_over_70pct_negative() {
    let analyser = FeedbackAnalyser::default();
    // 8 negative + 2 positive = 80% negative.
    let mut signals = Vec::new();
    let mut seq = 0;
    for _ in 0..8 {
        signals.push(make(FeedbackPolarity::Negative, ts(seq), "user"));
        seq += 1;
    }
    for _ in 0..2 {
        signals.push(make(FeedbackPolarity::Positive, ts(seq), "user"));
        seq += 1;
    }

    let a = analyser.analyse(signals);
    assert_eq!(a.verbosity_delta, VERBOSITY_DOWN);
    assert_eq!(a.formality_delta, 0.0);
    assert!(a.preferred_topics.is_empty());
}

#[test]
fn raises_verbosity_by_up_when_over_70pct_positive() {
    let analyser = FeedbackAnalyser::default();
    let mut signals = Vec::new();
    let mut seq = 0;
    for _ in 0..8 {
        signals.push(make(FeedbackPolarity::Positive, ts(seq), "user"));
        seq += 1;
    }
    for _ in 0..2 {
        signals.push(make(FeedbackPolarity::Negative, ts(seq), "user"));
        seq += 1;
    }

    let a = analyser.analyse(signals);
    assert_eq!(a.verbosity_delta, VERBOSITY_UP);
}

#[test]
fn leaves_verbosity_at_0_for_a_balanced_window() {
    let analyser = FeedbackAnalyser::default();
    let mut signals = Vec::new();
    let mut seq = 0;
    for _ in 0..5 {
        signals.push(make(FeedbackPolarity::Positive, ts(seq), "user"));
        seq += 1;
    }
    for _ in 0..5 {
        signals.push(make(FeedbackPolarity::Negative, ts(seq), "user"));
        seq += 1;
    }

    assert_eq!(analyser.analyse(signals).verbosity_delta, 0.0);
}

#[test]
fn treats_exactly_70pct_as_not_crossing_the_threshold() {
    let analyser = FeedbackAnalyser::new(10).unwrap();
    // Exactly 7/10 negative — 0.70 is not > 0.70.
    let mut signals = Vec::new();
    let mut seq = 0;
    for _ in 0..7 {
        signals.push(make(FeedbackPolarity::Negative, ts(seq), "user"));
        seq += 1;
    }
    for _ in 0..3 {
        signals.push(make(FeedbackPolarity::Positive, ts(seq), "user"));
        seq += 1;
    }

    assert_eq!(analyser.analyse(signals).verbosity_delta, 0.0);
}

#[test]
fn only_considers_the_most_recent_window_size_signals() {
    let analyser = FeedbackAnalyser::new(3).unwrap();
    // Older bulk is positive; the 3 newest are negative → window is 100% negative.
    let mut signals = Vec::new();
    for i in 0..10 {
        signals.push(make(FeedbackPolarity::Positive, ts(1000 + i), "user"));
    }
    for i in 0..3 {
        signals.push(make(FeedbackPolarity::Negative, ts(9_000_000 + i), "user"));
    }

    let a = analyser.analyse(signals);
    assert_eq!(a.verbosity_delta, VERBOSITY_DOWN);
}

#[test]
fn ignores_correction_signals_in_the_ratio() {
    let analyser = FeedbackAnalyser::default();
    // 8 negative + 2 correction = 8/10 = 80% negative → down.
    let mut signals = Vec::new();
    let mut seq = 0;
    for _ in 0..8 {
        signals.push(make(FeedbackPolarity::Negative, ts(seq), "user"));
        seq += 1;
    }
    for _ in 0..2 {
        signals.push(make(FeedbackPolarity::Correction, ts(seq), "user"));
        seq += 1;
    }
    assert_eq!(analyser.analyse(signals).verbosity_delta, VERBOSITY_DOWN);
}

// ══════════════════════════════════════════════════════════════════════════
// InMemoryFeedbackStore (mirrors CircleAI.Tests.InMemoryFeedbackStoreTests)
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn add_increments_the_count() {
    let store = InMemoryFeedbackStore::with_default_capacity();
    store
        .add_shared(make(FeedbackPolarity::Positive, ts(0), "user"))
        .unwrap();
    assert_eq!(store.count_shared().unwrap(), 1);
}

#[test]
fn get_recent_on_an_empty_store_returns_empty() {
    let store = InMemoryFeedbackStore::with_default_capacity();
    assert!(store.get_recent_shared(10).unwrap().is_empty());
}

#[test]
fn get_recent_returns_newest_first() {
    let store = InMemoryFeedbackStore::with_default_capacity();
    store
        .add_shared(make(FeedbackPolarity::Positive, ts(0), "old"))
        .unwrap();
    store
        .add_shared(make(FeedbackPolarity::Negative, ts(600), "new"))
        .unwrap();

    let result = store.get_recent_shared(10).unwrap();
    assert_eq!(result.len(), 2);
    assert_eq!(result[0].user_text, "new");
}

#[test]
fn positive_ratio_returns_none_with_no_signals() {
    let store = InMemoryFeedbackStore::with_default_capacity();
    assert_eq!(store.positive_ratio_shared().unwrap(), None);
}

#[test]
fn positive_ratio_returns_1_when_all_positive() {
    let store = InMemoryFeedbackStore::with_default_capacity();
    store
        .add_shared(make(FeedbackPolarity::Positive, ts(0), "user"))
        .unwrap();
    store
        .add_shared(make(FeedbackPolarity::Positive, ts(1), "user"))
        .unwrap();
    assert_eq!(store.positive_ratio_shared().unwrap(), Some(1.0));
}

#[test]
fn positive_ratio_returns_the_right_fraction_for_mixed_signals() {
    let store = InMemoryFeedbackStore::with_default_capacity();
    store
        .add_shared(make(FeedbackPolarity::Positive, ts(0), "user"))
        .unwrap();
    store
        .add_shared(make(FeedbackPolarity::Positive, ts(1), "user"))
        .unwrap();
    store
        .add_shared(make(FeedbackPolarity::Negative, ts(2), "user"))
        .unwrap();
    let ratio = store.positive_ratio_shared().unwrap().unwrap();
    assert!(ratio > 0.66 && ratio < 0.68); // 2/3
}

#[test]
fn evicts_the_oldest_when_max_signals_is_exceeded_fifo() {
    let store = InMemoryFeedbackStore::new(3).unwrap();
    for i in 0..5 {
        store
            .add_shared(make(FeedbackPolarity::Positive, ts(i), &format!("u{i}")))
            .unwrap();
    }
    assert_eq!(store.count_shared().unwrap(), 3);
}

#[test]
fn rejects_a_non_positive_max_signals() {
    assert!(InMemoryFeedbackStore::new(0).is_err());
}

#[test]
fn implements_the_sync_ifeedbackstore_trait() {
    // Drive it through the &mut self trait to prove the impl is wired.
    let mut store = InMemoryFeedbackStore::with_default_capacity();
    IFeedbackStore::add(&mut store, make(FeedbackPolarity::Positive, ts(0), "user")).unwrap();
    assert_eq!(IFeedbackStore::count(&store).unwrap(), 1);
    assert_eq!(IFeedbackStore::positive_ratio(&store).unwrap(), Some(1.0));
    assert_eq!(IFeedbackStore::get_recent(&store, 10).unwrap().len(), 1);
}
