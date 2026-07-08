//! llm_extractor.rs
//!
//! LLM-backed knowledge-graph extraction: turn → (subject, predicate, object)
//! triples. Ported from CircleAI.Companion (LlmKnowledgeGraphExtractor) — the C#
//! reference — and mirrors the TypeScript pilot (memory/llm_extractor.ts) 1:1.
//!
//! Uses an on-device [`IChatGenerator`] to ask an LLM to extract triples from a
//! single conversation turn. The extraction prompt asks for strict-JSON output;
//! the parser is defensive against the model emitting extra prose or fences.
//! Every failure mode — a blank turn, a failing generator, unparseable output —
//! degrades to an empty list rather than propagating an error, matching the
//! reference's graceful-degradation contract.

use chrono::Utc;

use super::graph::KnowledgeTriple;
use crate::brain::BrainError;
use crate::inference::IChatGenerator;
use crate::memory::extractor::IKnowledgeGraphExtractor;
use crate::models::ChatMessage;

/// Confidence used when the model omits (or malforms) the "c" field.
const DEFAULT_CONFIDENCE: f64 = 0.75;

/// The verbatim extraction system prompt (identical to the C#/TS reference).
const SYSTEM_PROMPT: &str = "You are a knowledge-graph extractor. Read the conversation turn between USER and ASSISTANT. \
Identify entities (people, places, things, concepts) and facts. \
Output a single JSON array of triples like [{\"s\":\"Subject\",\"p\":\"predicate\",\"o\":\"object\",\"c\":0.0-1.0}, ...]. \
Only output the JSON — no prose, no markdown fences.";

/// Model-backed extractor: asks an LLM for triples and parses its JSON reply.
/// Generic over the concrete chat generator `G` (reuses the existing
/// [`IChatGenerator`] trait), so tests can inject a fake generator. The generic
/// generator's error type never surfaces: a failing call degrades to an empty
/// triple list, so [`IKnowledgeGraphExtractor::extract_from_turn`] always
/// returns `Ok`.
pub struct LlmKnowledgeGraphExtractor<G: IChatGenerator> {
    ai: G,
}

impl<G: IChatGenerator> LlmKnowledgeGraphExtractor<G> {
    /// Creates an extractor over the given chat generator.
    pub fn new(ai: G) -> Self {
        Self { ai }
    }

    /// Borrows the underlying chat generator (useful for tests that inspect the
    /// messages it was handed).
    pub fn generator(&self) -> &G {
        &self.ai
    }
}

impl<G> IKnowledgeGraphExtractor for LlmKnowledgeGraphExtractor<G>
where
    G: IChatGenerator + Send + Sync,
{
    fn extract_from_turn(
        &self,
        user_text: &str,
        assistant_text: &str,
        source_episode_id: Option<&str>,
    ) -> Result<Vec<KnowledgeTriple>, BrainError> {
        if is_blank(user_text) && is_blank(assistant_text) {
            return Ok(Vec::new());
        }

        let user_msg = format!("USER:\n{user_text}\nASSISTANT:\n{assistant_text}\n");

        let messages = [
            ChatMessage::system(SYSTEM_PROMPT),
            ChatMessage::user(user_msg),
        ];

        let reply = match self.ai.generate(&messages, None) {
            Ok(r) => r,
            // LLM call failed — degrade gracefully, no triples this turn.
            Err(_) => return Ok(Vec::new()),
        };

        Ok(parse_triples(&reply, source_episode_id))
    }
}

/// Parses the model's reply into triples. Finds the first `[` and last `]`,
/// JSON-parses the slice, and reads s/p/o/c from each object. Any structural
/// problem yields an empty list rather than an error.
pub fn parse_triples(raw: &str, source_episode_id: Option<&str>) -> Vec<KnowledgeTriple> {
    if is_blank(raw) {
        return Vec::new();
    }
    let first_bracket = match raw.find('[') {
        Some(i) => i,
        None => return Vec::new(),
    };
    let last_bracket = match raw.rfind(']') {
        Some(i) => i,
        None => return Vec::new(),
    };
    if last_bracket <= first_bracket {
        return Vec::new();
    }
    let json_slice = &raw[first_bracket..=last_bracket];

    let parsed: serde_json::Value = match serde_json::from_str(json_slice) {
        Ok(v) => v,
        // Malformed JSON — return nothing.
        Err(_) => return Vec::new(),
    };

    let array = match parsed.as_array() {
        Some(a) => a,
        None => return Vec::new(),
    };

    let now = Utc::now();
    let source = source_episode_id.map(|s| s.to_string());
    let mut hits: Vec<KnowledgeTriple> = Vec::with_capacity(array.len());
    for entry in array {
        let obj = match entry.as_object() {
            Some(o) => o,
            None => continue, // skip non-object array entries (numbers, strings, null, nested arrays)
        };
        let s = obj.get("s").and_then(|v| v.as_str());
        let p = obj.get("p").and_then(|v| v.as_str());
        let o = obj.get("o").and_then(|v| v.as_str());
        // `c` only counts when it is a finite JSON number; otherwise default.
        let c = match obj.get("c").and_then(|v| v.as_f64()) {
            Some(n) if n.is_finite() => clamp(n, 0.0, 1.0),
            _ => DEFAULT_CONFIDENCE,
        };
        if is_blank_opt(s) || is_blank_opt(p) || is_blank_opt(o) {
            continue;
        }
        hits.push(KnowledgeTriple {
            subject: s.unwrap().to_string(),
            predicate: p.unwrap().to_string(),
            object: o.unwrap().to_string(),
            source: source.clone(),
            confidence: c,
            recorded_at_utc: now,
        });
    }
    hits
}

fn is_blank(s: &str) -> bool {
    s.trim().is_empty()
}

fn is_blank_opt(s: Option<&str>) -> bool {
    match s {
        None => true,
        Some(v) => v.trim().is_empty(),
    }
}

fn clamp(x: f64, lo: f64, hi: f64) -> f64 {
    x.max(lo).min(hi)
}
