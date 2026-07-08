//! theory_of_mind.rs
//!
//! Theory of mind — estimating another party's mental state. Port of the C#
//! reference (CircleAI.Companion.HerJarvis `ITheoryOfMind` +
//! `BeliefTrackerTheoryOfMind`) 1:1.
//!
//! [`BeliefTrackerTheoryOfMind`] does bag-of-belief inference with confidence
//! decay: it scans the interaction history for belief verbs (`thinks`,
//! `believes`, `wants`, `fears`, `hopes`) and the clause that follows each,
//! weights `believe*` claims higher, decays later matches, and serialises the
//! accumulated (`verb:claim` → weight) map to JSON. In-memory, deterministic.

use std::collections::BTreeMap;

use serde_json::Value;

/// An estimate of another party's likely belief, with a confidence in `[0,1]`.
#[derive(Debug, Clone, PartialEq)]
pub struct OtherMindEstimate {
    pub target_identifier: String,
    pub likely_belief_json: String,
    pub confidence: f64,
}

impl OtherMindEstimate {
    pub fn new(
        target_identifier: impl Into<String>,
        likely_belief_json: impl Into<String>,
        confidence: f64,
    ) -> Self {
        Self {
            target_identifier: target_identifier.into(),
            likely_belief_json: likely_belief_json.into(),
            confidence,
        }
    }
}

/// Theory-of-mind contract. `Send + Sync` so a concrete estimator can be shared
/// behind an `Arc`.
pub trait ITheoryOfMind: Send + Sync {
    /// Estimates the target's likely belief from the interaction history JSON.
    ///
    /// # Panics
    /// Panics if `target` is empty or whitespace (mirrors the C#
    /// `ArgumentException`).
    fn estimate(&self, target: &str, interaction_history_json: &str) -> OtherMindEstimate;
}

/// The belief verbs, each as (root, allows-trailing-s). Mirrors the C# regex
/// alternation `thinks?|believes?|wants?|fears?|hopes?` (the `s?` is optional,
/// and there is a `\b` only before the root — never after — so "thinking"
/// matches "think").
const BELIEF_VERBS: [&str; 5] = ["think", "believe", "want", "fear", "hope"];

/// One matched belief verb and its trailing clause.
struct BeliefMatch {
    verb: String,
    claim: String,
}

/// Scans `text` for `\b(think|believe|want|fear|hope)(s?)\s+([^.;!?]+)` with
/// case-insensitive matching, left-to-right, non-overlapping — the exact
/// behaviour of .NET's `Regex.Matches`.
fn scan_beliefs(text: &str) -> Vec<BeliefMatch> {
    let chars: Vec<char> = text.chars().collect();
    let lower: Vec<char> = text.to_lowercase().chars().collect();
    // to_lowercase can change length for a few code points; guard by realigning
    // on the simpler ASCII-lowered form, which suffices for these ASCII verbs.
    let lower: Vec<char> = if lower.len() == chars.len() {
        lower
    } else {
        chars.iter().map(|c| c.to_ascii_lowercase()).collect()
    };

    let mut matches = Vec::new();
    let n = chars.len();
    let mut i = 0usize;
    while i < n {
        // A word boundary before position i: i==0, or the previous char is a
        // non-word char (regex `\w` = [A-Za-z0-9_]).
        let boundary = i == 0 || !is_word_char(chars[i - 1]);
        if boundary {
            if let Some((verb, after)) = try_match_verb(&lower, i) {
                // Require `\s+` after the (optionally s-suffixed) verb.
                let mut j = after;
                let mut saw_space = false;
                while j < n && chars[j].is_whitespace() {
                    j += 1;
                    saw_space = true;
                }
                if saw_space && j < n {
                    // Capture group 2: `[^.;!?]+` — one or more non-terminator
                    // chars. Must be non-empty for the overall match to occur.
                    let start = j;
                    while j < n && !matches!(chars[j], '.' | ';' | '!' | '?') {
                        j += 1;
                    }
                    if j > start {
                        let claim: String = chars[start..j].iter().collect();
                        matches.push(BeliefMatch {
                            verb,
                            claim: claim.trim().to_string(),
                        });
                        // Continue scanning after the consumed clause.
                        i = j;
                        continue;
                    }
                }
            }
        }
        i += 1;
    }
    matches
}

/// Attempts to match a belief verb (plus optional trailing `s`) at `pos` in the
/// lower-cased char slice. Returns the lower-cased matched verb text (root, or
/// root+"s") and the index just past it.
fn try_match_verb(lower: &[char], pos: usize) -> Option<(String, usize)> {
    for root in BELIEF_VERBS {
        let root_chars: Vec<char> = root.chars().collect();
        let end = pos + root_chars.len();
        if end <= lower.len() && lower[pos..end] == root_chars[..] {
            // Optional trailing 's'.
            if end < lower.len() && lower[end] == 's' {
                return Some((format!("{root}s"), end + 1));
            }
            return Some((root.to_string(), end));
        }
    }
    None
}

fn is_word_char(c: char) -> bool {
    c.is_ascii_alphanumeric() || c == '_'
}

/// Bag-of-belief theory of mind with confidence decay.
#[derive(Debug, Default, Clone, Copy)]
pub struct BeliefTrackerTheoryOfMind;

impl BeliefTrackerTheoryOfMind {
    /// Returns a new estimator.
    pub fn new() -> Self {
        Self
    }
}

impl ITheoryOfMind for BeliefTrackerTheoryOfMind {
    fn estimate(&self, target: &str, interaction_history_json: &str) -> OtherMindEstimate {
        assert!(!target.trim().is_empty(), "target required");
        // A BTreeMap keeps a stable, sorted key order for the JSON — the C#
        // `Dictionary` order is insertion-based, but the accumulated values are
        // identical and callers parse the JSON rather than string-match it.
        let mut beliefs: BTreeMap<String, f64> = BTreeMap::new();
        for (idx, m) in scan_beliefs(interaction_history_json).into_iter().enumerate() {
            let verb = m.verb.to_lowercase();
            let decay = 1.0 / (1.0 + idx as f64 * 0.1);
            let weight = if verb.starts_with("believ") { 1.0 } else { 0.7 };
            let key = format!("{verb}:{}", m.claim);
            *beliefs.entry(key).or_insert(0.0) += weight * decay;
        }
        let json = Value::Object(
            beliefs
                .iter()
                .map(|(k, v)| (k.clone(), json_number(*v)))
                .collect(),
        )
        .to_string();
        let conf = if beliefs.is_empty() {
            0.0
        } else {
            (beliefs.values().sum::<f64>() / 5.0).min(1.0)
        };
        OtherMindEstimate::new(target, json, conf)
    }
}

/// Serialises a weight as a JSON number, preserving integral values as integers
/// (so `1.0` renders `1.0`-style like `serde_json` does for `f64`).
fn json_number(v: f64) -> Value {
    serde_json::Number::from_f64(v)
        .map(Value::Number)
        .unwrap_or(Value::Null)
}
