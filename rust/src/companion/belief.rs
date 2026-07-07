//! belief.rs
//!
//! Memory integrity: attribution + belief revision. Ported from
//! CircleAI.Companion (PersonalBelief, HeuristicBeliefExtractor, SelfBeliefStore)
//! — the C# reference — and mirrors the TypeScript pilot (companion/belief.ts)
//! and the Go port (companion_belief.go) 1:1.
//!
//! Every belief carries WHOSE fact it is — the user's own (Self), someone else's
//! (Other), or a general fact (World). The highest-harm rule in the whole system:
//! a fact about a third party ("my mother is diabetic") must never be recorded as
//! a fact about the user. Only Self beliefs become user facts; a newer self-belief
//! on the same predicate supersedes the older one; a correction retracts a belief.

use std::collections::HashSet;
use std::sync::Mutex;

use chrono::{DateTime, Utc};

use crate::brain::BrainError;

/// Whose fact a belief is about.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Attribution {
    /// A fact about the user themselves.
    Self_,
    /// A fact about a third party.
    Other,
    /// A general fact.
    World,
}

impl Attribution {
    /// The canonical name of the attribution.
    pub fn name(&self) -> &'static str {
        match self {
            Attribution::Self_ => "Self",
            Attribution::Other => "Other",
            Attribution::World => "World",
        }
    }
}

impl std::fmt::Display for Attribution {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(self.name())
    }
}

/// A single attributed belief, with provenance and confidence.
#[derive(Debug, Clone, PartialEq)]
pub struct PersonalBelief {
    pub attribution: Attribution,
    pub subject: String,
    pub predicate: String,
    pub object: String,
    pub confidence: f64,
    pub source: Option<String>,
    pub recorded_at_utc: DateTime<Utc>,
}

/// Turns a sentence into attributed beliefs. `Send + Sync` so a concrete
/// extractor can be shared behind an `Arc` into the encoder.
pub trait IBeliefExtractor: Send + Sync {
    fn extract(
        &self,
        text: &str,
        source: Option<&str>,
    ) -> Result<Vec<PersonalBelief>, BrainError>;
}

const BELIEF_RELATIONS: &[&str] = &[
    "mother", "father", "mom", "mum", "dad", "sister", "brother", "wife", "husband", "son",
    "daughter", "aunt", "uncle", "grandmother", "grandfather", "granny", "grandpa", "gran", "nan",
    "friend", "colleague", "boss", "neighbour", "neighbor", "cousin", "partner", "girlfriend",
    "boyfriend",
];

const BELIEF_POSSESSIVE: &[&str] = &["my", "her", "his", "their", "our"];

const BELIEF_STOP: &[&str] = &[
    "the", "a", "an", "is", "are", "was", "were", "be", "been", "am", "to", "of", "in", "on", "at",
    "and", "or", "but", "with", "has", "have", "had", "that", "this", "it", "as", "for", "really",
    "very", "just", "now",
];

/// A model-free belief extractor with attribution discipline. Coarse by design —
/// the model-based extractor is far more precise — but it never collapses "my
/// mother" into "me". Attribution is decided by the sentence's leading subject.
#[derive(Debug, Default, Clone, Copy)]
pub struct HeuristicBeliefExtractor;

impl HeuristicBeliefExtractor {
    pub fn new() -> Self {
        Self
    }
}

impl IBeliefExtractor for HeuristicBeliefExtractor {
    /// Returns at most one attributed belief for the sentence. The split set (no
    /// apostrophe, no hyphen) mirrors the TS/C#/Go reference so "i'm" stays one
    /// token.
    fn extract(
        &self,
        text: &str,
        source: Option<&str>,
    ) -> Result<Vec<PersonalBelief>, BrainError> {
        if text.trim().is_empty() {
            return Ok(Vec::new());
        }

        let lowered = text.to_lowercase();
        let tokens: Vec<&str> = lowered.split(is_belief_separator).filter(|s| !s.is_empty()).collect();
        if tokens.is_empty() {
            return Ok(Vec::new());
        }

        let relations: HashSet<&str> = BELIEF_RELATIONS.iter().copied().collect();
        let possessive: HashSet<&str> = BELIEF_POSSESSIVE.iter().copied().collect();
        let stop: HashSet<&str> = BELIEF_STOP.iter().copied().collect();

        let attribution: Attribution;
        let subject: String;
        let mut skip: HashSet<usize> = HashSet::new();

        if tokens.len() >= 2 && possessive.contains(tokens[0]) && relations.contains(tokens[1]) {
            // "my mother ..." → someone else
            attribution = Attribution::Other;
            subject = tokens[1].to_string();
            skip.insert(0);
            skip.insert(1);
        } else if relations.contains(tokens[0]) {
            attribution = Attribution::Other;
            subject = tokens[0].to_string();
            skip.insert(0);
        } else if tokens[0] == "i"
            || tokens[0] == "i'm"
            || tokens[0] == "im"
            || tokens[0] == "me"
            || tokens[0] == "my"
        {
            // "I ..." or "my <non-relation> ..." → the user
            attribution = Attribution::Self_;
            subject = "user".to_string();
            skip.insert(0);
        } else {
            attribution = Attribution::World;
            subject = tokens[0].to_string();
        }

        let mut object_parts: Vec<&str> = Vec::new();
        for (i, t) in tokens.iter().enumerate() {
            if skip.contains(&i) {
                continue;
            }
            if t.chars().count() < 3 {
                continue;
            }
            if stop.contains(t) {
                continue;
            }
            if relations.contains(t) {
                continue;
            }
            object_parts.push(t);
        }
        let obj = object_parts.join(" ");
        if obj.trim().is_empty() {
            return Ok(Vec::new());
        }

        Ok(vec![PersonalBelief {
            attribution,
            subject,
            predicate: "isAbout".to_string(),
            object: obj,
            confidence: 0.6,
            source: source.map(|s| s.to_string()),
            recorded_at_utc: Utc::now(),
        }])
    }
}

/// The belief split set: whitespace and `. , ? ! ; : " ( )`. Note: NO
/// apostrophe, so "i'm" survives as a single token.
fn is_belief_separator(r: char) -> bool {
    matches!(
        r,
        ' ' | '\t' | '\n' | '\r' | '.' | ',' | '?' | '!' | ';' | ':' | '"' | '(' | ')'
    )
}

/// Holds the user's own facts, with attribution filtering, revision, and
/// correction. Thread-safe: the encoder writes from its background drain while
/// the session reads facts for the prompt.
#[derive(Debug, Default)]
pub struct SelfBeliefStore {
    inner: Mutex<SelfBeliefInner>,
}

#[derive(Debug, Default)]
struct SelfBeliefInner {
    /// Self-attributed facts only.
    self_facts: Vec<PersonalBelief>,
    /// Other/world — remembered, never a user fact.
    audit: Vec<PersonalBelief>,
}

impl SelfBeliefStore {
    /// Returns an empty store.
    pub fn new() -> Self {
        Self::default()
    }

    /// Records a belief. Only Self beliefs become user facts; the rest are
    /// audited. A newer Self belief on the same (subject, predicate) supersedes
    /// the older one.
    pub fn record(&self, belief: PersonalBelief) -> Result<(), BrainError> {
        let mut inner = self.inner.lock().unwrap();
        if belief.attribution != Attribution::Self_ {
            inner.audit.push(belief);
            return Ok(());
        }
        // Supersede an existing self-belief on the same (subject, predicate): a
        // functional fact holds one current value. The prior value drops out.
        inner.self_facts.retain(|b| {
            !(b.subject.eq_ignore_ascii_case(&belief.subject)
                && b.predicate.eq_ignore_ascii_case(&belief.predicate))
        });
        inner.self_facts.push(belief);
        Ok(())
    }

    /// Returns the user's own current facts.
    pub fn self_facts(&self) -> Vec<PersonalBelief> {
        self.inner.lock().unwrap().self_facts.clone()
    }

    /// Returns beliefs remembered but never treated as user facts (audit trail).
    pub fn non_self(&self) -> Vec<PersonalBelief> {
        self.inner.lock().unwrap().audit.clone()
    }

    /// Drops any user fact whose object contains the given text
    /// (case-insensitive) and returns the number removed.
    pub fn retract(&self, object_contains: &str) -> usize {
        if object_contains.trim().is_empty() {
            return 0;
        }
        let needle = object_contains.to_lowercase();
        let mut inner = self.inner.lock().unwrap();
        let before = inner.self_facts.len();
        inner
            .self_facts
            .retain(|b| !b.object.to_lowercase().contains(&needle));
        before - inner.self_facts.len()
    }

    /// Returns the distinct source turns behind the user's facts.
    pub fn provenance(&self) -> Vec<String> {
        let inner = self.inner.lock().unwrap();
        let mut seen: HashSet<String> = HashSet::new();
        let mut out: Vec<String> = Vec::new();
        for b in &inner.self_facts {
            if let Some(src) = &b.source {
                if seen.insert(src.clone()) {
                    out.push(src.clone());
                }
            }
        }
        out
    }
}
