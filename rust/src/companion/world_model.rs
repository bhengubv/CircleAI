//! world_model.rs
//!
//! World model + causal reasoning. Port of the C# reference
//! (CircleAI.Companion.HerJarvis `IWorldModel` + `FrequencyWorldModel`, and
//! CircleAI.Companion `BayesianWorldModel`) 1:1.
//!
//! `IWorldModel` learns `P(outcome | observation)` from registered evidence and
//! predicts a causal outcome for a scenario supplied as a JSON object. Two
//! concrete engines:
//!   * [`FrequencyWorldModel`] — plain co-occurrence tally + argmax.
//!   * [`BayesianWorldModel`] — online-learning Naive Bayes with Laplace
//!     smoothing and a softmax over log-posteriors.
//!
//! Both are in-memory and deterministic. Observation keys are matched
//! case-insensitively (the C# uses `StringComparer.OrdinalIgnoreCase`).

use std::collections::HashMap;
use std::sync::Mutex;

use serde_json::Value;

/// A predicted causal outcome with its probability and the observations that
/// supported it.
#[derive(Debug, Clone, PartialEq)]
pub struct CausalPrediction {
    pub outcome: String,
    pub probability: f64,
    pub supporting_factors: Vec<String>,
}

impl CausalPrediction {
    pub fn new(
        outcome: impl Into<String>,
        probability: f64,
        supporting_factors: Vec<String>,
    ) -> Self {
        Self {
            outcome: outcome.into(),
            probability,
            supporting_factors,
        }
    }
}

/// World model + causal reasoning contract. `Send + Sync` so a concrete model
/// can be shared behind an `Arc`.
pub trait IWorldModel: Send + Sync {
    /// Predicts the most likely causal outcome for the given scenario JSON.
    fn predict(&self, scenario_json: &str) -> CausalPrediction;
}

/// Extract observations from a scenario JSON object as `name=value` strings.
///
/// Mirrors the C# `ExtractObservations`: parse the root object and, for every
/// property, emit `PropertyName + "=" + value.ToString()`. `JsonElement.ToString()`
/// returns the raw string content for JSON strings (no surrounding quotes) and
/// the compact JSON text for every other kind. A parse failure or a non-object
/// root yields an empty list.
fn extract_observations(scenario_json: &str) -> Vec<String> {
    if scenario_json.trim().is_empty() {
        return Vec::new();
    }
    let root: Value = match serde_json::from_str(scenario_json) {
        Ok(v) => v,
        Err(_) => return Vec::new(),
    };
    let obj = match root.as_object() {
        Some(o) => o,
        None => return Vec::new(),
    };
    let mut hits = Vec::with_capacity(obj.len());
    for (name, value) in obj {
        hits.push(format!("{}={}", name, value_to_string(value)));
    }
    hits
}

/// Reproduces `System.Text.Json.JsonElement.ToString()`:
/// a JSON string renders as its unquoted content; everything else renders as
/// its compact JSON text.
fn value_to_string(value: &Value) -> String {
    match value {
        Value::String(s) => s.clone(),
        other => other.to_string(),
    }
}

// =====================================================================
// FrequencyWorldModel — learn P(outcome|observation) from evidence.
// =====================================================================

/// Co-occurrence world model: for each observation it tallies how often each
/// outcome was seen, then predicts the outcome with the highest summed tally
/// across the scenario's observations.
#[derive(Debug, Default)]
pub struct FrequencyWorldModel {
    /// observation (lower-cased) -> { outcome (lower-cased) -> (display, count) }
    inner: Mutex<HashMap<String, HashMap<String, (String, i64)>>>,
}

impl FrequencyWorldModel {
    /// Returns an empty model.
    pub fn new() -> Self {
        Self::default()
    }

    /// Tell the model: when these observations happen, this outcome was seen.
    ///
    /// # Panics
    /// Panics if `outcome` is empty or whitespace (mirrors the C#
    /// `ArgumentException`).
    pub fn observe<I, S>(&self, observations: I, outcome: &str)
    where
        I: IntoIterator<Item = S>,
        S: AsRef<str>,
    {
        assert!(
            !outcome.trim().is_empty(),
            "outcome required"
        );
        let out_key = outcome.to_lowercase();
        let mut inner = self.inner.lock().unwrap();
        for obs in observations {
            let obs = obs.as_ref();
            let bucket = inner.entry(obs.to_lowercase()).or_default();
            let entry = bucket
                .entry(out_key.clone())
                .or_insert_with(|| (outcome.to_string(), 0));
            entry.1 += 1;
        }
    }
}

impl IWorldModel for FrequencyWorldModel {
    fn predict(&self, scenario_json: &str) -> CausalPrediction {
        let observations = extract_observations(scenario_json);
        // tally: outcome-key -> (display, count)
        let mut tally: HashMap<String, (String, i64)> = HashMap::new();
        let mut supporters: Vec<String> = Vec::new();
        {
            let inner = self.inner.lock().unwrap();
            for obs in &observations {
                let Some(bucket) = inner.get(&obs.to_lowercase()) else {
                    continue;
                };
                supporters.push(obs.clone());
                for (out_key, (display, count)) in bucket {
                    let e = tally
                        .entry(out_key.clone())
                        .or_insert_with(|| (display.clone(), 0));
                    e.1 += *count;
                }
            }
        }
        if tally.is_empty() {
            return CausalPrediction::new("unknown", 0.5, supporters);
        }
        let total: i64 = tally.values().map(|(_, c)| *c).sum();
        // Argmax by count. (HashMap iteration order is unspecified in both C#
        // and Rust, so a tie between two equally-frequent outcomes is not a
        // stable choice in either — the winner is decided by count.)
        let top = tally
            .values()
            .cloned()
            .fold(None::<(String, i64)>, |acc, (display, count)| match acc {
                Some((_, best)) if best >= count => acc,
                _ => Some((display, count)),
            })
            .unwrap();
        CausalPrediction::new(top.0, top.1 as f64 / total as f64, supporters)
    }
}

// =====================================================================
// BayesianWorldModel — online Naive Bayes with Laplace smoothing.
// =====================================================================

/// A real probabilistic graphical model: an online-learning Naive Bayes
/// classifier over (observations → outcome) pairs. At predict time it scores,
/// for every seen outcome,
/// `P(outcome | obs) ∝ P(outcome) · ∏ P(obs_i | outcome)` with Laplace
/// smoothing, then softmaxes the log-posteriors to a normalised probability.
#[derive(Debug)]
pub struct BayesianWorldModel {
    inner: Mutex<BayesianInner>,
    /// Laplace smoothing strength.
    alpha: f64,
}

#[derive(Debug, Default)]
struct BayesianInner {
    /// outcome-key -> (display, count)
    outcome_counts: HashMap<String, (String, i64)>,
    /// outcome-key -> { observation-key -> count }
    cond_counts: HashMap<String, HashMap<String, i64>>,
    /// distinct observation vocabulary (lower-cased).
    vocab: std::collections::HashSet<String>,
    total_observations: i64,
}

impl Default for BayesianWorldModel {
    fn default() -> Self {
        Self::new(1.0)
    }
}

impl BayesianWorldModel {
    /// Creates a model with the given Laplace smoothing strength.
    ///
    /// # Panics
    /// Panics if `laplace_alpha <= 0` (mirrors the C#
    /// `ArgumentOutOfRangeException`).
    pub fn new(laplace_alpha: f64) -> Self {
        assert!(laplace_alpha > 0.0, "laplaceAlpha out of range");
        Self {
            inner: Mutex::new(BayesianInner::default()),
            alpha: laplace_alpha,
        }
    }

    /// Update the model with one (observations → outcome) example.
    ///
    /// # Panics
    /// Panics if `outcome` is empty or whitespace.
    pub fn observe<I, S>(&self, observations: I, outcome: &str)
    where
        I: IntoIterator<Item = S>,
        S: AsRef<str>,
    {
        assert!(!outcome.trim().is_empty(), "outcome required");
        let out_key = outcome.to_lowercase();
        let mut inner = self.inner.lock().unwrap();
        {
            let e = inner
                .outcome_counts
                .entry(out_key.clone())
                .or_insert_with(|| (outcome.to_string(), 0));
            e.1 += 1;
        }
        inner.total_observations += 1;
        for obs in observations {
            let obs = obs.as_ref();
            if obs.trim().is_empty() {
                continue;
            }
            let obs_key = obs.to_lowercase();
            let cond = inner.cond_counts.entry(out_key.clone()).or_default();
            *cond.entry(obs_key.clone()).or_insert(0) += 1;
            inner.vocab.insert(obs_key);
        }
    }
}

impl IWorldModel for BayesianWorldModel {
    fn predict(&self, scenario_json: &str) -> CausalPrediction {
        let observations = extract_observations(scenario_json);
        let inner = self.inner.lock().unwrap();
        if observations.is_empty() || inner.outcome_counts.is_empty() {
            return CausalPrediction::new("unknown", 0.5, Vec::new());
        }

        let vocab_size = inner.vocab.len().max(1) as f64;
        let total_ex = inner.total_observations.max(1) as f64;
        let num_outcomes = inner.outcome_counts.len() as f64;

        // Lower-cased observations for likelihood lookup.
        let obs_keys: Vec<String> = observations.iter().map(|o| o.to_lowercase()).collect();

        let mut scored: Vec<(String, f64)> = Vec::with_capacity(inner.outcome_counts.len());
        for (out_key, (display, outcome_count)) in &inner.outcome_counts {
            // Log P(outcome) — Laplace-smoothed prior.
            let log_prior =
                (( *outcome_count as f64 + self.alpha) / (total_ex + self.alpha * num_outcomes)).ln();

            let empty = HashMap::new();
            let cond = inner.cond_counts.get(out_key).unwrap_or(&empty);
            let total_for_outcome: i64 = cond.values().sum();
            let mut log_likelihood = 0.0;
            for obs in &obs_keys {
                let n = *cond.get(obs).unwrap_or(&0);
                let p = (n as f64 + self.alpha)
                    / (total_for_outcome as f64 + self.alpha * vocab_size);
                log_likelihood += p.ln();
            }
            scored.push((display.clone(), log_prior + log_likelihood));
        }

        // Softmax over log-posteriors for a normalised probability.
        let max_log_post = scored
            .iter()
            .map(|(_, lp)| *lp)
            .fold(f64::NEG_INFINITY, f64::max);
        let exp_sum: f64 = scored
            .iter()
            .map(|(_, lp)| (lp - max_log_post).exp())
            .sum();
        // Argmax by log-posterior (stable → earliest max on ties).
        let top = scored
            .iter()
            .cloned()
            .fold(None::<(String, f64)>, |acc, (o, lp)| match acc {
                Some((_, best)) if best >= lp => acc,
                _ => Some((o, lp)),
            })
            .unwrap();
        let prob = (top.1 - max_log_post).exp() / exp_sum;
        CausalPrediction::new(top.0, prob, observations)
    }
}
