//! predictive_engine.rs
//!
//! Predictive engine — anticipates upcoming user needs. Port of the C#
//! reference (CircleAI.Companion.HerJarvis `IPredictiveEngine` +
//! `HistogramPredictiveEngine`, and CircleAI.Companion `SequencePredictiveEngine`)
//! 1:1.
//!
//! Two concrete engines:
//!   * [`HistogramPredictiveEngine`] — a time-of-day (day-of-week × hour)
//!     histogram of recurring events; case-insensitive descriptions.
//!   * [`SequencePredictiveEngine`] — a variable-order Markov chain (default
//!     3-gram) over the user's event timeline with back-off, plus per-event
//!     mean inter-arrival forecasting; case-sensitive event ids.
//!
//! Both are in-memory and deterministic.

use std::collections::HashMap;
use std::sync::Mutex;

use chrono::{DateTime, Datelike, Duration, Timelike, Utc};

/// A predicted upcoming need with its expected arrival time and probability.
#[derive(Debug, Clone, PartialEq)]
pub struct AnticipatedNeed {
    pub description: String,
    pub expected_by_utc: DateTime<Utc>,
    pub probability: f64,
}

impl AnticipatedNeed {
    pub fn new(
        description: impl Into<String>,
        expected_by_utc: DateTime<Utc>,
        probability: f64,
    ) -> Self {
        Self {
            description: description.into(),
            expected_by_utc,
            probability,
        }
    }
}

/// Predictive engine contract. `Send + Sync` so a concrete engine can be shared
/// behind an `Arc`.
pub trait IPredictiveEngine: Send + Sync {
    /// Returns the anticipated needs within the next `horizon_minutes`,
    /// most-probable first.
    ///
    /// # Panics
    /// Panics if `horizon_minutes <= 0` (mirrors the C#
    /// `ArgumentOutOfRangeException`).
    fn anticipate(&self, horizon_minutes: i64) -> Vec<AnticipatedNeed>;
}

/// The `DayOfWeek * 24 + hour` histogram slot for a UTC instant. C#
/// `DateTimeOffset.DayOfWeek` is Sunday=0..Saturday=6, matching chrono's
/// `num_days_from_sunday`.
fn slot_of(at_utc: DateTime<Utc>) -> usize {
    let dow = at_utc.weekday().num_days_from_sunday() as usize;
    dow * 24 + at_utc.hour() as usize
}

// =====================================================================
// HistogramPredictiveEngine — time-of-day histogram of recurring events.
// =====================================================================

/// Learns, per event description, a 24×7 histogram of when it occurs, then
/// forecasts which events fall inside the horizon window (sampled every 30
/// minutes) and scores each by `upcoming / total`.
#[derive(Debug, Default)]
pub struct HistogramPredictiveEngine {
    /// description (lower-cased) -> (display, [i64; 24*7])
    inner: Mutex<HashMap<String, (String, Vec<i64>)>>,
}

impl HistogramPredictiveEngine {
    /// Returns an empty engine.
    pub fn new() -> Self {
        Self::default()
    }

    /// Tell the engine: this need occurred at this UTC time.
    ///
    /// # Panics
    /// Panics if `description` is empty or whitespace.
    pub fn observe(&self, description: &str, at_utc: DateTime<Utc>) {
        assert!(!description.trim().is_empty(), "description required");
        let key = description.to_lowercase();
        let slot = slot_of(at_utc);
        let mut inner = self.inner.lock().unwrap();
        let entry = inner
            .entry(key)
            .or_insert_with(|| (description.to_string(), vec![0i64; 24 * 7]));
        entry.1[slot] += 1;
    }
}

impl IPredictiveEngine for HistogramPredictiveEngine {
    fn anticipate(&self, horizon_minutes: i64) -> Vec<AnticipatedNeed> {
        assert!(horizon_minutes > 0, "horizonMinutes out of range");
        let now = Utc::now();
        let mut results: Vec<AnticipatedNeed> = Vec::new();
        {
            let inner = self.inner.lock().unwrap();
            for (_, (display, hist)) in inner.iter() {
                let total: i64 = hist.iter().sum();
                let mut upcoming: i64 = 0;
                let mut m = 0i64;
                while m <= horizon_minutes {
                    let when = now + Duration::minutes(m);
                    upcoming += hist[slot_of(when)];
                    m += 30;
                }
                if total == 0 || upcoming == 0 {
                    continue;
                }
                results.push(AnticipatedNeed::new(
                    display.clone(),
                    now + Duration::minutes(horizon_minutes / 2),
                    upcoming as f64 / total as f64,
                ));
            }
        }
        // Most-probable first. Stable descending sort.
        results.sort_by(|a, b| {
            b.probability
                .partial_cmp(&a.probability)
                .unwrap_or(std::cmp::Ordering::Equal)
        });
        results
    }
}

// =====================================================================
// SequencePredictiveEngine — variable-order Markov chain (n-gram).
// =====================================================================

/// A real online sequence model: a variable-order Markov chain over the user's
/// observed event timeline. On [`anticipate`](IPredictiveEngine::anticipate) it
/// walks from the longest context down to a 1-gram (back-off), weighting longer
/// contexts by `2^k`, aggregates the transition probabilities, then estimates
/// each event's arrival from its mean inter-arrival interval. Events whose mean
/// interval exceeds the horizon are dropped.
#[derive(Debug)]
pub struct SequencePredictiveEngine {
    inner: Mutex<SequenceInner>,
    order: usize,
}

#[derive(Debug, Default)]
struct SequenceInner {
    /// context key ("a|b|c") -> { next event -> count }  (case-sensitive)
    transitions: HashMap<String, HashMap<String, i64>>,
    /// event -> (count, sum_seconds) mean inter-arrival accumulator.
    inter_arrivals: HashMap<String, (i64, f64)>,
    /// full ordered timeline of (event, at_utc).
    history: Vec<(String, DateTime<Utc>)>,
}

impl Default for SequencePredictiveEngine {
    fn default() -> Self {
        Self::new(3)
    }
}

impl SequencePredictiveEngine {
    /// Creates an engine of the given Markov order (1..=6).
    ///
    /// # Panics
    /// Panics if `order` is outside `1..=6` (mirrors the C#
    /// `ArgumentOutOfRangeException`).
    pub fn new(order: usize) -> Self {
        assert!((1..=6).contains(&order), "order out of range");
        Self {
            inner: Mutex::new(SequenceInner::default()),
            order,
        }
    }

    /// Add one event to the user timeline.
    ///
    /// # Panics
    /// Panics if `event` is empty or whitespace.
    pub fn observe(&self, event: &str, at_utc: DateTime<Utc>) {
        assert!(!event.trim().is_empty(), "event required");
        let mut inner = self.inner.lock().unwrap();
        inner.history.push((event.to_string(), at_utc));

        // Build n-gram contexts up to `order`.
        let hist_len = inner.history.len();
        for k in 1..=self.order {
            if hist_len <= k {
                break;
            }
            // context_start = history.Count - 1 - k
            let context_start = hist_len - 1 - k;
            let key = inner.history[context_start..context_start + k]
                .iter()
                .map(|(e, _)| e.as_str())
                .collect::<Vec<_>>()
                .join("|");
            let bucket = inner.transitions.entry(key).or_default();
            *bucket.entry(event.to_string()).or_insert(0) += 1;
        }

        // Track inter-arrival time for this event vs the immediately prior one.
        if hist_len >= 2 {
            let (last_event, last_at) = inner.history[hist_len - 2].clone();
            if last_event == event {
                let gap = (at_utc - last_at).num_milliseconds() as f64 / 1000.0;
                let e = inner
                    .inter_arrivals
                    .entry(event.to_string())
                    .or_insert((0, 0.0));
                e.0 += 1;
                e.1 += gap;
            }
        }
    }
}

impl IPredictiveEngine for SequencePredictiveEngine {
    fn anticipate(&self, horizon_minutes: i64) -> Vec<AnticipatedNeed> {
        assert!(horizon_minutes > 0, "horizonMinutes out of range");
        let inner = self.inner.lock().unwrap();
        if inner.history.is_empty() {
            return Vec::new();
        }

        // Most recent `order` events as the prediction context.
        let context_len = self.order.min(inner.history.len());
        let start = inner.history.len() - context_len;
        let context: Vec<&str> = inner.history[start..]
            .iter()
            .map(|(e, _)| e.as_str())
            .collect();

        // total_score: next event -> aggregated weighted probability.
        let mut total_score: HashMap<String, f64> = HashMap::new();
        // Walk from longest context to shortest (back-off).
        for k in (1..=context.len()).rev() {
            let key = context[context.len() - k..].join("|");
            let Some(bucket) = inner.transitions.get(&key) else {
                continue;
            };
            let total_for_ctx: i64 = bucket.values().sum();
            if total_for_ctx == 0 {
                continue;
            }
            let weight = 2f64.powi(k as i32);
            for (next, count) in bucket {
                let prob = *count as f64 / total_for_ctx as f64;
                *total_score.entry(next.clone()).or_insert(0.0) += weight * prob;
            }
        }

        if total_score.is_empty() {
            return Vec::new();
        }

        let total_weight: f64 = total_score.values().sum();
        let horizon_sec = horizon_minutes as f64 * 60.0;
        let now = Utc::now();

        // Order by descending aggregated score (stable).
        let mut ranked: Vec<(String, f64)> = total_score.into_iter().collect();
        ranked.sort_by(|a, b| {
            b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal)
        });

        let mut anticipated: Vec<AnticipatedNeed> = Vec::new();
        for (ev, raw) in ranked {
            let prob = raw / total_weight;
            if prob <= 0.0 {
                continue;
            }
            // Mean inter-arrival estimates when it'll happen.
            let mean_interval = match inner.inter_arrivals.get(&ev) {
                Some((cnt, sum_sec)) if *cnt > 0 => sum_sec / *cnt as f64,
                _ => horizon_sec * 0.5,
            };
            if mean_interval > horizon_sec {
                continue; // not expected within window
            }
            anticipated.push(AnticipatedNeed::new(
                ev,
                now + Duration::milliseconds((mean_interval * 1000.0) as i64),
                prob,
            ));
        }
        anticipated
    }
}
