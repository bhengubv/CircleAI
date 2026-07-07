//! knowledge_graph_test.rs
//!
//! Verifies KnowledgeGraph (triples + nodes) and HippoRagStore (Personalised
//! PageRank multi-hop recall) — including the three precision guarantees:
//! no-seed→empty, seeds excluded from results, confidence-weighting. Mirrors the
//! TS pilot suite tests/knowledge_graph.test.ts and the Go suite
//! knowledge_graph_test.go 1:1.

use std::collections::HashMap;
use std::sync::Arc;

use circle_ai::memory::graph::{
    HippoRagStore, IHippoRagStore, KnowledgeGraph, KnowledgeNode, MemoryHit, MemoryItem,
};

fn must_triple(kg: &KnowledgeGraph, s: &str, p: &str, o: &str, src: Option<&str>, conf: f64) {
    kg.add_triple(s, p, o, src, conf).unwrap_or_else(|e| panic!("AddTriple({s},{p},{o}): {e}"));
}

fn must_hippo(kg: Arc<KnowledgeGraph>) -> HippoRagStore {
    HippoRagStore::new(kg).expect("NewHippoRagStore")
}

fn hit_ids(hits: &[MemoryHit]) -> HashMap<String, bool> {
    hits.iter().map(|h| (h.item.id.clone(), true)).collect()
}

fn find_hit<'a>(hits: &'a [MemoryHit], id: &str) -> Option<&'a MemoryHit> {
    hits.iter().find(|h| h.item.id == id)
}

// ── KnowledgeGraph ───────────────────────────────────────────────────────────

#[test]
fn stores_and_returns_triples() {
    let kg = KnowledgeGraph::new();
    must_triple(&kg, "a", "rel", "b", Some("ep1"), 1.0);
    let all = kg.all_triples();
    assert_eq!(all.len(), 1);
    assert_eq!(all[0].subject, "a");
    assert_eq!(all[0].object, "b");
    assert_eq!(all[0].confidence, 1.0);
}

#[test]
fn replaces_a_triple_with_same_spo() {
    let kg = KnowledgeGraph::new();
    must_triple(&kg, "a", "rel", "b", Some("ep1"), 0.5);
    must_triple(&kg, "a", "rel", "b", Some("ep2"), 0.9);
    let all = kg.all_triples();
    assert_eq!(all.len(), 1);
    assert_eq!(all[0].confidence, 0.9);
    assert_eq!(all[0].source.as_deref(), Some("ep2"));
}

#[test]
fn upserts_and_fetches_nodes() {
    let kg = KnowledgeGraph::new();
    kg.upsert_node(KnowledgeNode::new("heart", "organ", "the heart")).expect("UpsertNode");
    let n = kg.get_node("heart");
    assert!(n.is_some());
    assert_eq!(n.unwrap().name, "the heart");
    assert!(kg.get_node("missing").is_none());
}

#[test]
fn rejects_out_of_range_confidence() {
    let kg = KnowledgeGraph::new();
    assert!(kg.add_triple("a", "r", "b", None, 1.5).is_err());
}

// ── HippoRagStore::multi_hop_recall ──────────────────────────────────────────

#[test]
fn reaches_associated_nodes_across_hops_and_excludes_the_seed() {
    // chest → heart → father_cardiac_event
    let kg = Arc::new(KnowledgeGraph::new());
    must_triple(&kg, "chest", "relates", "heart", Some("ep1"), 1.0);
    must_triple(&kg, "heart", "relates", "father_cardiac_event", Some("ep2"), 1.0);
    let hippo = must_hippo(Arc::clone(&kg));

    let hits = hippo.multi_hop_recall("chest tightness", 5).expect("MultiHopRecall");
    let ids = hit_ids(&hits);
    assert!(!ids.contains_key("chest"), "seed node must be excluded");
    assert!(ids.contains_key("heart"), "one-hop node should be recalled");
    assert!(ids.contains_key("father_cardiac_event"), "two-hop node should be recalled");

    let heart = find_hit(&hits, "heart").expect("heart present");
    let father = find_hit(&hits, "father_cardiac_event").expect("father present");
    assert!(heart.score >= father.score, "one hop should carry >= mass: heart={} father={}", heart.score, father.score);
}

#[test]
fn returns_empty_when_no_query_term_touches_the_graph() {
    let kg = Arc::new(KnowledgeGraph::new());
    must_triple(&kg, "chest", "relates", "heart", Some("ep1"), 1.0);
    let hippo = must_hippo(Arc::clone(&kg));

    let hits = hippo.multi_hop_recall("banana apple", 5).expect("MultiHopRecall");
    assert_eq!(hits.len(), 0);
}

#[test]
fn returns_empty_on_an_empty_graph() {
    let hippo = must_hippo(Arc::new(KnowledgeGraph::new()));
    let hits = hippo.multi_hop_recall("anything", 5).expect("MultiHopRecall");
    assert_eq!(hits.len(), 0);
}

#[test]
fn confidence_weights_edge_spread_stated_fact_outranks_a_guess() {
    // root → alpha (stated, 1.0) and root → beta (guessed, 0.1)
    let kg = Arc::new(KnowledgeGraph::new());
    must_triple(&kg, "root", "r", "alpha", Some("ep1"), 1.0);
    must_triple(&kg, "root", "r", "beta", Some("ep2"), 0.1);
    let hippo = must_hippo(Arc::clone(&kg));

    let hits = hippo.multi_hop_recall("root", 5).expect("MultiHopRecall");
    let ids = hit_ids(&hits);
    assert!(!ids.contains_key("root"), "seed excluded");
    assert!(hits.len() >= 2, "expected at least 2 hits, got {}", hits.len());
    assert_eq!(hits[0].item.id, "alpha");
    assert_eq!(hits[1].item.id, "beta");
    assert!(hits[0].score > hits[1].score, "alpha should outrank beta: {} vs {}", hits[0].score, hits[1].score);
}

#[test]
fn uses_the_node_name_as_recall_text_when_a_node_is_present() {
    let kg = Arc::new(KnowledgeGraph::new());
    must_triple(&kg, "chest", "relates", "heart", Some("ep1"), 1.0);
    kg.upsert_node(KnowledgeNode::new("heart", "organ", "the heart")).expect("UpsertNode");
    let hippo = must_hippo(Arc::clone(&kg));

    let hits = hippo.multi_hop_recall("chest", 5).expect("MultiHopRecall");
    let heart = find_hit(&hits, "heart").expect("heart present");
    assert_eq!(heart.item.text, "the heart");
}

#[test]
fn index_registers_the_item_and_its_metadata_as_graph_triples() {
    let kg = Arc::new(KnowledgeGraph::new());
    let hippo = must_hippo(Arc::clone(&kg));
    let mut meta = HashMap::new();
    meta.insert("topic".to_string(), "durban".to_string());
    hippo.index(&MemoryItem::with_metadata("note1", "durban weather", meta)).expect("Index");

    let triples = kg.read_triples("note1").expect("ReadTriples");
    let preds: std::collections::HashSet<String> =
        triples.iter().map(|t| t.predicate.clone()).collect();
    assert!(preds.contains("memory_text"));
    assert!(preds.contains("topic"));
    assert_eq!(preds.len(), 2, "predicates: got {preds:?} want {{memory_text, topic}}");
}

#[test]
fn recalls_a_memory_node_reached_from_a_query_term_seed_reverse_edge() {
    let kg = Arc::new(KnowledgeGraph::new());
    must_triple(&kg, "durban", "seenin", "note1", Some("ep1"), 1.0);
    kg.upsert_node(KnowledgeNode::new("note1", "memory", "durban weather")).expect("UpsertNode");
    let hippo = must_hippo(Arc::clone(&kg));

    let hits = hippo.multi_hop_recall("durban", 5).expect("MultiHopRecall");
    let ids = hit_ids(&hits);
    assert!(!ids.contains_key("durban"), "seed excluded");
    assert!(ids.contains_key("note1"), "note1 should be recalled");
    let note = find_hit(&hits, "note1").expect("note1 present");
    assert_eq!(note.item.text, "durban weather");
}
