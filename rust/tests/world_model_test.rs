//! world_model_test.rs
//!
//! Verifies the companion world model: FrequencyWorldModel (co-occurrence
//! argmax) and BayesianWorldModel (online Naive Bayes with Laplace smoothing +
//! softmax). Mirrors the C# reference behaviour of IWorldModel.PredictAsync.

use circle_ai::companion::world_model::{
    BayesianWorldModel, FrequencyWorldModel, IWorldModel,
};

// ── FrequencyWorldModel ────────────────────────────────────────────────────

#[test]
fn frequency_unknown_when_no_evidence_matches() {
    let m = FrequencyWorldModel::new();
    let p = m.predict("{\"weather\":\"rain\"}");
    assert_eq!(p.outcome, "unknown");
    assert_eq!(p.probability, 0.5);
    assert!(p.supporting_factors.is_empty());
}

#[test]
fn frequency_predicts_the_most_frequent_outcome() {
    let m = FrequencyWorldModel::new();
    // Observation key is "weather=rain" (name=value form).
    m.observe(["weather=rain"], "carry-umbrella");
    m.observe(["weather=rain"], "carry-umbrella");
    m.observe(["weather=rain"], "wear-hat");

    let p = m.predict("{\"weather\":\"rain\"}");
    assert_eq!(p.outcome, "carry-umbrella");
    // 2 of 3 total tallies.
    assert!((p.probability - 2.0 / 3.0).abs() < 1e-9);
    assert_eq!(p.supporting_factors, vec!["weather=rain".to_string()]);
}

#[test]
fn frequency_observation_matching_is_case_insensitive() {
    let m = FrequencyWorldModel::new();
    m.observe(["Weather=Rain"], "carry-umbrella");
    // Scenario yields "weather=rain" — different casing must still match, as the
    // C# uses OrdinalIgnoreCase on the observation key.
    let p = m.predict("{\"weather\":\"rain\"}");
    assert_eq!(p.outcome, "carry-umbrella");
    assert!((p.probability - 1.0).abs() < 1e-9);
}

#[test]
fn frequency_aggregates_multiple_observations() {
    let m = FrequencyWorldModel::new();
    m.observe(["time=morning"], "make-coffee");
    m.observe(["location=kitchen"], "make-coffee");
    m.observe(["location=kitchen"], "wash-dishes");

    let p = m.predict("{\"time\":\"morning\",\"location\":\"kitchen\"}");
    // make-coffee scores 1 (morning) + 1 (kitchen) = 2; wash-dishes 1 → total 3.
    assert_eq!(p.outcome, "make-coffee");
    assert!((p.probability - 2.0 / 3.0).abs() < 1e-9);
    assert_eq!(p.supporting_factors.len(), 2);
}

#[test]
#[should_panic(expected = "outcome required")]
fn frequency_observe_rejects_blank_outcome() {
    let m = FrequencyWorldModel::new();
    m.observe(["a=b"], "   ");
}

#[test]
fn frequency_ignores_malformed_or_non_object_json() {
    let m = FrequencyWorldModel::new();
    m.observe(["x=1"], "y");
    let bad = m.predict("not json");
    assert_eq!(bad.outcome, "unknown");
    let arr = m.predict("[1,2,3]");
    assert_eq!(arr.outcome, "unknown");
}

// ── BayesianWorldModel ─────────────────────────────────────────────────────

#[test]
fn bayes_unknown_when_untrained() {
    let m = BayesianWorldModel::default();
    let p = m.predict("{\"a\":\"1\"}");
    assert_eq!(p.outcome, "unknown");
    assert_eq!(p.probability, 0.5);
    assert!(p.supporting_factors.is_empty());
}

#[test]
fn bayes_learns_and_predicts_dominant_class() {
    let m = BayesianWorldModel::default();
    for _ in 0..5 {
        m.observe(["sky=grey", "pressure=low"], "rain");
    }
    for _ in 0..5 {
        m.observe(["sky=blue", "pressure=high"], "sun");
    }

    let p = m.predict("{\"sky\":\"grey\",\"pressure\":\"low\"}");
    assert_eq!(p.outcome, "rain");
    // Posterior should be well above the two-class prior of 0.5.
    assert!(p.probability > 0.8, "expected confident rain, got {}", p.probability);
    // supporting_factors echoes the extracted observations.
    assert_eq!(p.supporting_factors.len(), 2);
}

#[test]
fn bayes_probability_is_normalised_between_zero_and_one() {
    let m = BayesianWorldModel::new(1.0);
    m.observe(["k=v"], "a");
    m.observe(["k=v"], "b");
    let p = m.predict("{\"k\":\"v\"}");
    assert!(p.probability >= 0.0 && p.probability <= 1.0);
}

#[test]
fn bayes_case_insensitive_observation_lookup() {
    let m = BayesianWorldModel::default();
    for _ in 0..3 {
        m.observe(["Mood=Happy"], "smile");
    }
    let p = m.predict("{\"mood\":\"happy\"}");
    assert_eq!(p.outcome, "smile");
}

#[test]
#[should_panic(expected = "laplaceAlpha out of range")]
fn bayes_rejects_non_positive_alpha() {
    let _ = BayesianWorldModel::new(0.0);
}

#[test]
#[should_panic(expected = "outcome required")]
fn bayes_observe_rejects_blank_outcome() {
    let m = BayesianWorldModel::default();
    m.observe(["a=b"], "");
}
