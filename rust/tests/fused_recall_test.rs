//! fused_recall_test.rs
//!
//! Verifies FusedRecall: Reciprocal Rank Fusion order, cross-source
//! reinforcement, cold-start degradation to episodic, the graph confidence gate,
//! empty-query short-circuit, and dedup by normalised text. Mirrors the TS pilot
//! suite tests/fused_recall.test.ts and the Go suite fused_recall_test.go 1:1.

use std::collections::HashMap;
use std::sync::Arc;

use chrono::{TimeZone, Utc};
use circle_ai::brain::BrainError;
use circle_ai::memory::episodic::EpisodicSearch;
use circle_ai::memory::graph::{IHippoRagStore, MemoryHit, MemoryItem};
use circle_ai::memory::recall::{FusedRecall, IRecall};
use circle_ai::memory::EpisodicMemoryEntry;
use uuid::Uuid;

// ── Test doubles ─────────────────────────────────────────────────────────────

fn ep_entry(_id: &str, user_text: &str) -> EpisodicMemoryEntry {
    // The fused-recall suite keys on text, never on id; a fresh v4 id suffices.
    EpisodicMemoryEntry {
        id: Uuid::new_v4(),
        recorded_at_utc: Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap(),
        user_text: user_text.to_string(),
        assistant_text: String::new(),
        app_context: None,
        embedding: None,
        tags: None,
    }
}

/// Returns a fixed, pre-ranked list from `search`.
struct FakeEpisodic {
    hits: Vec<EpisodicMemoryEntry>,
}

impl EpisodicSearch for FakeEpisodic {
    fn search(
        &self,
        _query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<EpisodicMemoryEntry>, BrainError> {
        Ok(self.hits.iter().take(top_k).cloned().collect())
    }
}

/// Returns a fixed, pre-ranked list from `multi_hop_recall`.
struct FakeHippo {
    hits: Vec<MemoryHit>,
}

impl IHippoRagStore for FakeHippo {
    fn backend_id(&self) -> &str {
        "fake-hippo"
    }
    fn index(&self, _item: &MemoryItem) -> Result<(), BrainError> {
        Ok(())
    }
    fn multi_hop_recall(&self, _query: &str, top_k: usize) -> Result<Vec<MemoryHit>, BrainError> {
        Ok(self.hits.iter().take(top_k).cloned().collect())
    }
}

/// Always errors from `multi_hop_recall`.
struct ThrowingHippo;

impl IHippoRagStore for ThrowingHippo {
    fn backend_id(&self) -> &str {
        "boom"
    }
    fn index(&self, _item: &MemoryItem) -> Result<(), BrainError> {
        Ok(())
    }
    fn multi_hop_recall(&self, _query: &str, _top_k: usize) -> Result<Vec<MemoryHit>, BrainError> {
        Err(BrainError::new("graph unavailable"))
    }
}

fn graph_hit(id: &str, text: &str, confidence: Option<&str>) -> MemoryHit {
    let metadata = confidence.map(|c| {
        let mut m = HashMap::new();
        m.insert("confidence".to_string(), c.to_string());
        m
    });
    MemoryHit {
        item: MemoryItem {
            id: id.to_string(),
            text: text.to_string(),
            metadata,
        },
        score: 1.0,
    }
}

fn hit_texts(hits: &[MemoryHit]) -> Vec<String> {
    hits.iter().map(|h| h.item.text.clone()).collect()
}

fn must_recall(
    ep: Arc<dyn EpisodicSearch>,
    g: Option<Arc<dyn IHippoRagStore>>,
) -> FusedRecall {
    FusedRecall::new(ep, g, None).expect("NewFusedRecall")
}

// ── RRF ordering ─────────────────────────────────────────────────────────────

#[test]
fn a_memory_surfaced_by_both_sources_outranks_one_surfaced_by_only_one() {
    let episodic = Arc::new(FakeEpisodic {
        hits: vec![ep_entry("a", "A"), ep_entry("b", "B"), ep_entry("c", "C")],
    });
    let graph = Arc::new(FakeHippo {
        hits: vec![graph_hit("g", "B", None)], // reinforces B
    });
    let recall = must_recall(episodic, Some(graph));

    let hits = recall.recall("q", None, 5).expect("Recall");
    assert_eq!(hit_texts(&hits), vec!["B", "A", "C"]);
}

#[test]
fn cold_start_no_graph_yields_the_episodic_order_unchanged() {
    let episodic = Arc::new(FakeEpisodic {
        hits: vec![ep_entry("a", "A"), ep_entry("b", "B"), ep_entry("c", "C")],
    });
    let recall = must_recall(episodic, None);

    let hits = recall.recall("q", None, 5).expect("Recall");
    assert_eq!(hit_texts(&hits), vec!["A", "B", "C"]);
}

#[test]
fn rrf_respects_top_k() {
    let episodic = Arc::new(FakeEpisodic {
        hits: vec![ep_entry("a", "A"), ep_entry("b", "B"), ep_entry("c", "C")],
    });
    let recall = must_recall(episodic, None);

    let hits = recall.recall("q", None, 2).expect("Recall");
    assert_eq!(hits.len(), 2);
    assert_eq!(hit_texts(&hits), vec!["A", "B"]);
}

// ── Integrity gates ──────────────────────────────────────────────────────────

#[test]
fn drops_graph_hits_below_the_confidence_threshold() {
    let episodic = Arc::new(FakeEpisodic { hits: vec![] });
    let graph = Arc::new(FakeHippo {
        hits: vec![
            graph_hit("low", "LOW", Some("0.2")),
            graph_hit("high", "HIGH", Some("0.9")),
        ],
    });
    let recall = must_recall(episodic, Some(graph));

    let hits = recall.recall("q", None, 5).expect("Recall");
    let texts = hit_texts(&hits);
    assert!(!texts.contains(&"LOW".to_string()), "below-threshold hit must be dropped");
    assert!(texts.contains(&"HIGH".to_string()), "HIGH must be kept");
}

#[test]
fn keeps_graph_hits_that_carry_no_confidence_metadata() {
    let episodic = Arc::new(FakeEpisodic { hits: vec![] });
    let graph = Arc::new(FakeHippo {
        hits: vec![graph_hit("g", "NOCONF", None)],
    });
    let recall = must_recall(episodic, Some(graph));

    let hits = recall.recall("q", None, 5).expect("Recall");
    assert_eq!(hit_texts(&hits), vec!["NOCONF"]);
}

#[test]
fn skips_the_graph_entirely_for_an_empty_query() {
    let episodic = Arc::new(FakeEpisodic {
        hits: vec![ep_entry("a", "A")],
    });
    let graph = Arc::new(FakeHippo {
        hits: vec![graph_hit("g", "GRAPH", None)],
    });
    let recall = must_recall(episodic, Some(graph));

    let hits = recall.recall("   ", None, 5).expect("Recall");
    let texts = hit_texts(&hits);
    assert_eq!(texts, vec!["A"]);
    assert!(!texts.contains(&"GRAPH".to_string()), "graph must be skipped for empty query");
}

#[test]
fn degrades_to_episodic_when_the_graph_errors() {
    let episodic = Arc::new(FakeEpisodic {
        hits: vec![ep_entry("a", "A")],
    });
    let recall = must_recall(episodic, Some(Arc::new(ThrowingHippo)));

    let hits = recall.recall("q", None, 5).expect("Recall");
    assert_eq!(hit_texts(&hits), vec!["A"]);
}

// ── Dedup ────────────────────────────────────────────────────────────────────

#[test]
fn fuses_two_hits_with_the_same_normalised_text_into_one_entry() {
    let episodic = Arc::new(FakeEpisodic {
        hits: vec![ep_entry("a", "Durban  Weather")],
    });
    let graph = Arc::new(FakeHippo {
        hits: vec![graph_hit("g", "durban weather", None)], // same key
    });
    let recall = must_recall(episodic, Some(graph));

    let hits = recall.recall("q", None, 5).expect("Recall");
    assert_eq!(hits.len(), 1);
}
