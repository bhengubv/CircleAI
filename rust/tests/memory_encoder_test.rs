//! memory_encoder_test.rs
//!
//! Verifies CompanionMemoryEncoder end-to-end: a turn handed to the background
//! encoder fills the knowledge graph so associative recall can later reach the
//! episode; attributed beliefs are formed off the hot path (a third party's fact
//! never becomes the user's); the queue drops rather than blocks when full;
//! close drains remaining work; and an extractor failure is captured, not fatal.
//! Mirrors the TS pilot suite tests/memory_encoder.test.ts and the Go suite
//! memory_encoder_test.go 1:1.

use std::sync::Arc;

use circle_ai::brain::BrainError;
use circle_ai::companion::belief::{HeuristicBeliefExtractor, IBeliefExtractor, SelfBeliefStore};
use circle_ai::companion::memory_encoder::CompanionMemoryEncoder;
use circle_ai::memory::extractor::{HeuristicKnowledgeGraphExtractor, IKnowledgeGraphExtractor};
use circle_ai::memory::graph::{HippoRagStore, IHippoRagStore, KnowledgeGraph, KnowledgeTriple};

/// Always errors from `extract_from_turn`.
struct ThrowingExtractor;

impl IKnowledgeGraphExtractor for ThrowingExtractor {
    fn extract_from_turn(
        &self,
        _user_text: &str,
        _assistant_text: &str,
        _source_episode_id: Option<&str>,
    ) -> Result<Vec<KnowledgeTriple>, BrainError> {
        Err(BrainError::new("boom"))
    }
}

fn new_encoder(
    ex: Arc<dyn IKnowledgeGraphExtractor>,
    g: Arc<KnowledgeGraph>,
    bx: Option<Arc<dyn IBeliefExtractor>>,
    beliefs: Option<Arc<SelfBeliefStore>>,
    capacity: usize,
) -> Arc<CompanionMemoryEncoder> {
    CompanionMemoryEncoder::new(ex, g, bx, beliefs, capacity).expect("NewCompanionMemoryEncoder")
}

// ── End-to-end ───────────────────────────────────────────────────────────────

#[test]
fn encodes_a_turn_so_associative_recall_can_reach_the_episode_by_a_content_word() {
    let graph = Arc::new(KnowledgeGraph::new());
    let enc = new_encoder(
        Arc::new(HeuristicKnowledgeGraphExtractor::new()),
        Arc::clone(&graph),
        None,
        None,
        0,
    );

    enc.enqueue("I love hiking in Drakensberg", "Sounds wonderful", "ep-hike");
    enc.close().expect("Close");

    assert!(!graph.all_triples().is_empty(), "graph should have filled from the turn");

    let hippo = HippoRagStore::new(Arc::clone(&graph)).expect("NewHippoRagStore");
    let hits = hippo.multi_hop_recall("drakensberg", 5).expect("MultiHopRecall");
    let episode = hits.iter().find(|h| h.item.id == "ep-hike").expect("recall should reach the episode");
    assert_eq!(episode.item.text, "I love hiking in Drakensberg");
}

#[test]
fn forms_attributed_beliefs_off_the_hot_path_mothers_fact_never_becomes_the_users() {
    let graph = Arc::new(KnowledgeGraph::new());
    let beliefs = Arc::new(SelfBeliefStore::new());
    let enc = new_encoder(
        Arc::new(HeuristicKnowledgeGraphExtractor::new()),
        Arc::clone(&graph),
        Some(Arc::new(HeuristicBeliefExtractor::new())),
        Some(Arc::clone(&beliefs)),
        0,
    );

    enc.enqueue("my mother is diabetic", "Noted", "ep1");
    enc.enqueue("i am vegetarian", "Got it", "ep2");
    enc.close().expect("Close");

    let facts = beliefs.self_facts();
    for f in &facts {
        assert!(!f.object.contains("diabetic"), "mother's condition must never be a user fact");
    }
    assert!(facts.iter().any(|f| f.object == "vegetarian"), "vegetarian should be a user fact");
    assert!(
        beliefs.non_self().iter().any(|b| b.object == "diabetic"),
        "diabetic should still be remembered as an audit fact"
    );
}

// ── Queue behaviour ──────────────────────────────────────────────────────────

#[test]
fn drops_writes_beyond_capacity_rather_than_blocking() {
    let graph = Arc::new(KnowledgeGraph::new());
    let enc = new_encoder(
        Arc::new(HeuristicKnowledgeGraphExtractor::new()),
        Arc::clone(&graph),
        None,
        None,
        2,
    );

    // Enqueued before the drain is released (it drains on Close): the 3rd
    // overflows a capacity-2 queue and is dropped.
    enc.enqueue("alpha", "", "e1");
    enc.enqueue("bravo", "", "e2");
    enc.enqueue("charlie", "", "e3");
    enc.close().expect("Close");

    assert!(graph.get_node("e1").is_some(), "e1 should be present");
    assert!(graph.get_node("e2").is_some(), "e2 should be present");
    assert!(graph.get_node("e3").is_none(), "the overflow write should have been dropped");
}

#[test]
fn ignores_an_enqueue_with_a_blank_episode_id() {
    let graph = Arc::new(KnowledgeGraph::new());
    let enc = new_encoder(
        Arc::new(HeuristicKnowledgeGraphExtractor::new()),
        Arc::clone(&graph),
        None,
        None,
        0,
    );
    enc.enqueue("hello", "", "");
    enc.enqueue("hello", "", "   ");
    enc.close().expect("Close");
    assert_eq!(graph.all_triples().len(), 0, "blank episode ids should be ignored");
}

#[test]
fn captures_an_extractor_failure_without_crashing_the_drain() {
    let graph = Arc::new(KnowledgeGraph::new());
    let enc = new_encoder(Arc::new(ThrowingExtractor), Arc::clone(&graph), None, None, 0);
    enc.enqueue("x", "", "e1");
    enc.close().expect("Close");

    let last = enc.last_error();
    assert!(last.is_some(), "lastError should be set");
    assert_eq!(last.unwrap().message(), "boom");
    // The node was upserted before the extractor ran, so it survives.
    assert!(graph.get_node("e1").is_some(), "e1 node should survive (upserted before the extractor ran)");
}
