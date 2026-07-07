//! extractor.rs
//!
//! Knowledge-graph extraction: turn → (subject, predicate, object) triples.
//! Ported from CircleAI.Companion (IKnowledgeGraphExtractor,
//! HeuristicKnowledgeGraphExtractor) — the C# reference — and mirrors the
//! TypeScript pilot (memory/extractor.ts) and the Go port (memory_extractor.go)
//! 1:1.
//!
//! The heuristic extractor is model-free: it links the content words a turn
//! mentions to the memory they came from, two-way, so a later question can reach
//! an older memory across turns. It is the offline counterpart to the LLM-based
//! extractor (same interface, no network) — the graph still fills, just coarsely.

use std::collections::HashSet;

use chrono::Utc;

use super::graph::KnowledgeTriple;
use crate::brain::BrainError;

/// Turns a conversation turn into knowledge-graph triples. `Send + Sync` so a
/// concrete extractor can be shared behind an `Arc` into the encoder.
pub trait IKnowledgeGraphExtractor: Send + Sync {
    fn extract_from_turn(
        &self,
        user_text: &str,
        assistant_text: &str,
        source_episode_id: Option<&str>,
    ) -> Result<Vec<KnowledgeTriple>, BrainError>;
}

const DEFAULT_TRIPLE_CONFIDENCE: f64 = 0.6;

/// Common function words that carry no association — dropped so links form on
/// meaningful words (names, places, symptoms, things).
const KG_STOP_WORDS: &[&str] = &[
    "the", "a", "an", "and", "or", "but", "if", "is", "are", "was", "were", "be", "been", "being",
    "to", "of", "in", "on", "at", "for", "with", "from", "by", "as", "into", "about", "over",
    "under", "my", "your", "our", "their", "his", "her", "its", "this", "that", "these", "those",
    "i", "you", "he", "she", "it", "we", "they", "me", "him", "them", "us", "do", "does", "did",
    "done", "have", "has", "had", "will", "would", "can", "could", "should", "shall", "may",
    "might", "must", "not", "no", "yes", "so", "than", "then", "there", "here", "how", "why",
    "what", "when", "where", "who", "which", "whom", "am", "get", "got", "really", "just", "very",
    "much", "many", "some", "any", "all",
];

/// A model-free extractor: it links a turn's content words to their memory,
/// two-way.
#[derive(Debug, Default, Clone, Copy)]
pub struct HeuristicKnowledgeGraphExtractor;

impl HeuristicKnowledgeGraphExtractor {
    pub fn new() -> Self {
        Self
    }
}

impl IKnowledgeGraphExtractor for HeuristicKnowledgeGraphExtractor {
    /// Produces bidirectional mentions/seenin triples for each content word in
    /// the turn. The memory node is identified by `source_episode_id` when given,
    /// else the user's words — so recall can hand back the memory the words came
    /// from.
    fn extract_from_turn(
        &self,
        user_text: &str,
        assistant_text: &str,
        source_episode_id: Option<&str>,
    ) -> Result<Vec<KnowledgeTriple>, BrainError> {
        let memory: String = match source_episode_id {
            Some(id) if !id.trim().is_empty() => id.to_string(),
            _ => user_text.to_string(),
        };
        if memory.trim().is_empty() {
            return Ok(Vec::new());
        }

        let combined = format!("{user_text} {assistant_text}");
        let words = content_words(&combined);
        let now = Utc::now();
        let source = source_episode_id.map(|s| s.to_string());

        let mut triples = Vec::with_capacity(words.len() * 2);
        for w in words {
            // Two-way so a walk can go word → memory → word → memory across turns.
            triples.push(KnowledgeTriple {
                subject: memory.clone(),
                predicate: "mentions".to_string(),
                object: w.clone(),
                source: source.clone(),
                confidence: DEFAULT_TRIPLE_CONFIDENCE,
                recorded_at_utc: now,
            });
            triples.push(KnowledgeTriple {
                subject: w,
                predicate: "seenin".to_string(),
                object: memory.clone(),
                source: source.clone(),
                confidence: DEFAULT_TRIPLE_CONFIDENCE,
                recorded_at_utc: now,
            });
        }
        Ok(triples)
    }
}

/// Lowercases, splits on separators, drops short/stop words, and dedupes
/// preserving order. Split set mirrors the TS/C#/Go `[ \t\n\r.,?!;:'"()/-]+`.
fn content_words(text: &str) -> Vec<String> {
    let lowered = text.to_lowercase();
    let mut seen: HashSet<String> = HashSet::new();
    let mut result: Vec<String> = Vec::new();
    let stop: HashSet<&str> = KG_STOP_WORDS.iter().copied().collect();
    for raw in lowered.split(is_kg_separator).filter(|s| !s.is_empty()) {
        if raw.chars().count() < 3 {
            continue;
        }
        if stop.contains(raw) {
            continue;
        }
        if seen.contains(raw) {
            continue;
        }
        seen.insert(raw.to_string());
        result.push(raw.to_string());
    }
    result
}

/// The extractor split set: whitespace and `. , ? ! ; : ' " ( ) / -`.
fn is_kg_separator(r: char) -> bool {
    matches!(
        r,
        ' ' | '\t' | '\n' | '\r' | '.' | ',' | '?' | '!' | ';' | ':' | '\'' | '"' | '(' | ')' | '/' | '-'
    )
}
