//! kg_extractor_test.rs
//!
//! Verifies HeuristicKnowledgeGraphExtractor: bidirectional mentions/seenin
//! triples on content words, stop-word + short-word filtering, dedup, and the
//! memory-id fallback to userText when no episode id is given. Mirrors the TS
//! pilot suite tests/kg_extractor.test.ts and the Go suite kg_extractor_test.go
//! 1:1.

use circle_ai::memory::extractor::{HeuristicKnowledgeGraphExtractor, IKnowledgeGraphExtractor};
use circle_ai::memory::graph::KnowledgeTriple;

fn must_extract(
    ex: &HeuristicKnowledgeGraphExtractor,
    user: &str,
    assistant: &str,
    src: Option<&str>,
) -> Vec<KnowledgeTriple> {
    ex.extract_from_turn(user, assistant, src).expect("ExtractFromTurn")
}

fn mentions_objects(triples: &[KnowledgeTriple]) -> Vec<String> {
    triples
        .iter()
        .filter(|t| t.predicate == "mentions")
        .map(|t| t.object.clone())
        .collect()
}

#[test]
fn emits_a_two_way_link_per_content_word_keyed_by_the_episode_id() {
    let ex = HeuristicKnowledgeGraphExtractor::new();
    let triples = must_extract(&ex, "Durban weather is sunny", "", Some("ep1"));
    // content words: durban, weather, sunny ("is" is a short stop word)
    assert_eq!(triples.len(), 6);
    let has = |s: &str, p: &str, o: &str| {
        triples.iter().any(|t| t.subject == s && t.predicate == p && t.object == o)
    };
    assert!(has("ep1", "mentions", "durban"));
    assert!(has("durban", "seenin", "ep1"));
    assert!(has("ep1", "mentions", "weather"));
    assert!(has("ep1", "mentions", "sunny"));
}

#[test]
fn drops_stop_words_and_words_shorter_than_3_chars() {
    let ex = HeuristicKnowledgeGraphExtractor::new();
    let triples = must_extract(&ex, "I am at the shop", "", Some("ep2"));
    let objects = mentions_objects(&triples);
    // "i","am","at","the" are all stop/short; only "shop" survives.
    assert_eq!(objects, vec!["shop"]);
}

#[test]
fn dedupes_a_repeated_word() {
    let ex = HeuristicKnowledgeGraphExtractor::new();
    let triples = must_extract(&ex, "test test test", "", Some("ep3"));
    // one mentions + one seenin for "test"
    assert_eq!(triples.len(), 2);
}

#[test]
fn includes_assistant_side_content_words() {
    let ex = HeuristicKnowledgeGraphExtractor::new();
    let triples = must_extract(&ex, "tell me about", "Johannesburg traffic", Some("ep4"));
    let mut objects = mentions_objects(&triples);
    objects.sort();
    assert_eq!(objects, vec!["johannesburg", "tell", "traffic"]);
}

#[test]
fn falls_back_to_user_text_as_the_memory_id_when_no_episode_id_is_given() {
    let ex = HeuristicKnowledgeGraphExtractor::new();
    let triples = must_extract(&ex, "hello world", "", None);
    let found = triples
        .iter()
        .any(|t| t.subject == "hello world" && t.predicate == "mentions");
    assert!(found, "expected memory id to fall back to userText");
}

#[test]
fn returns_nothing_for_an_empty_turn() {
    let ex = HeuristicKnowledgeGraphExtractor::new();
    let triples = must_extract(&ex, "", "", None);
    assert_eq!(triples.len(), 0);
}

#[test]
fn tags_every_triple_with_the_source_episode_id_and_default_confidence() {
    let ex = HeuristicKnowledgeGraphExtractor::new();
    let triples = must_extract(&ex, "coffee", "", Some("ep5"));
    assert!(!triples.is_empty());
    for t in &triples {
        assert_eq!(t.source.as_deref(), Some("ep5"));
        assert_eq!(t.confidence, 0.6);
    }
}
