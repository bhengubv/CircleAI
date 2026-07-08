//! rag_test.rs
//!
//! Exercises RagContextBuilder + RagPipelineBuilder. Mirrors the TS suite
//! tests/rag.test.ts and CircleAI.Tests.RagContextBuilderTests plus the fluent
//! builder surface and the embedder ranking path.

use std::sync::Arc;

use chrono::{TimeZone, Utc};
use circle_ai::brain::BrainError;
use circle_ai::memory::episodic::InMemoryEpisodicStore;
use circle_ai::memory::rag::{
    ITextEmbedder, RagContextBuilder, RagEpisodicStore, RagPipelineBuilder,
};
use circle_ai::memory::EpisodicMemoryEntry;
use uuid::Uuid;

// ── Helpers ──────────────────────────────────────────────────────────────────

struct EpisodicOpts<'a> {
    user_text: &'a str,
    assistant_text: &'a str,
    app_context: Option<&'a str>,
    embedding: Option<Vec<f32>>,
    recorded: Option<chrono::DateTime<Utc>>,
}

impl Default for EpisodicOpts<'_> {
    fn default() -> Self {
        Self {
            user_text: "u",
            assistant_text: "a",
            app_context: None,
            embedding: None,
            recorded: None,
        }
    }
}

fn episodic(o: EpisodicOpts) -> EpisodicMemoryEntry {
    EpisodicMemoryEntry {
        id: Uuid::new_v4(),
        recorded_at_utc: o
            .recorded
            .unwrap_or_else(|| Utc.with_ymd_and_hms(2026, 6, 1, 12, 34, 0).unwrap()),
        user_text: o.user_text.to_string(),
        assistant_text: o.assistant_text.to_string(),
        app_context: o.app_context.map(|s| s.to_string()),
        embedding: o.embedding,
        tags: None,
    }
}

fn count_occurrences(text: &str, token: &str) -> usize {
    text.matches(token).count()
}

/// Store that always fails — used to test resilience (mirrors the TS
/// ThrowingEpisodicStore).
struct ThrowingEpisodicStore;
impl RagEpisodicStore for ThrowingEpisodicStore {
    fn search(
        &self,
        _query_embedding: Option<&[f32]>,
        _top_k: usize,
    ) -> Result<Vec<EpisodicMemoryEntry>, BrainError> {
        Err(BrainError::new("store failure"))
    }
}

/// Embedder that maps any query to a fixed vector.
struct FixedEmbedder(Vec<f32>);
impl ITextEmbedder for FixedEmbedder {
    fn generate(&self, _text: &str) -> Result<Vec<f32>, BrainError> {
        Ok(self.0.clone())
    }
}

/// Embedder that always fails.
struct FailingEmbedder;
impl ITextEmbedder for FailingEmbedder {
    fn generate(&self, _text: &str) -> Result<Vec<f32>, BrainError> {
        Err(BrainError::new("embedder offline"))
    }
}

fn add(store: &InMemoryEpisodicStore, e: EpisodicMemoryEntry) {
    store.add_shared(e).unwrap();
}

// ══════════════════════════════════════════════════════════════════════════
// Empty / missing query
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn empty_query_returns_empty() {
    let store = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let b = RagContextBuilder::with_store(store);
    assert_eq!(b.build_context(""), "");
}

#[test]
fn whitespace_query_returns_empty() {
    let store = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let b = RagContextBuilder::with_store(store);
    assert_eq!(b.build_context("   "), "");
}

// ══════════════════════════════════════════════════════════════════════════
// Empty store
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn empty_store_returns_empty() {
    let store = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let b = RagContextBuilder::with_store(store);
    assert_eq!(b.build_context("hello"), "");
}

// ══════════════════════════════════════════════════════════════════════════
// Non-empty store — recency fallback (no embedder)
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn returns_a_formatted_block_with_the_header_and_both_texts() {
    let store = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    add(
        &store,
        episodic(EpisodicOpts {
            user_text: "What is SDPKT?",
            assistant_text: "SDPKT is the TGN wallet.",
            recorded: Some(Utc.with_ymd_and_hms(2026, 6, 1, 11, 0, 0).unwrap()),
            ..Default::default()
        }),
    );

    let b = RagContextBuilder::new(store, None, 3, 300);
    let result = b.build_context("tell me about the wallet");

    assert_ne!(result, "");
    assert!(result.contains("What is SDPKT?"));
    assert!(result.contains("SDPKT is the TGN wallet."));
    assert!(result.contains("[Relevant past exchanges"));
}

#[test]
fn formats_the_utc_timestamp_and_labels_user_and_b() {
    let store = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    add(
        &store,
        episodic(EpisodicOpts {
            user_text: "q",
            assistant_text: "r",
            recorded: Some(Utc.with_ymd_and_hms(2026, 6, 1, 9, 5, 0).unwrap()),
            ..Default::default()
        }),
    );
    let b = RagContextBuilder::new(store, None, 1, 300);
    let result = b.build_context("anything");
    assert!(result.contains("[2026-06-01 09:05 UTC]"));
    assert!(result.contains("User: q"));
    assert!(result.contains("B!: r"));
}

#[test]
fn respects_top_k() {
    let store = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    for i in 0..10 {
        add(
            &store,
            episodic(EpisodicOpts {
                user_text: &format!("question {i}"),
                assistant_text: &format!("answer {i}"),
                ..Default::default()
            }),
        );
    }

    let b = RagContextBuilder::new(store, None, 2, 300);
    let result = b.build_context("any question");
    assert_eq!(count_occurrences(&result, "• ["), 2);
}

#[test]
fn includes_the_app_context_when_set() {
    let store = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    add(
        &store,
        episodic(EpisodicOpts {
            user_text: "bid query",
            assistant_text: "bid answer",
            app_context: Some("tgn.bidbaas"),
            ..Default::default()
        }),
    );
    let b = RagContextBuilder::new(store, None, 3, 300);
    let result = b.build_context("bidding");
    assert!(result.contains("tgn.bidbaas"));
}

#[test]
fn truncates_long_texts_to_half_the_budget_with_an_ellipsis() {
    let store = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let long_text = "x".repeat(500);
    add(
        &store,
        episodic(EpisodicOpts {
            user_text: &long_text,
            assistant_text: "a",
            ..Default::default()
        }),
    );
    // max_chars_per_entry 100 → half 50 → truncate to 49 chars + "…"
    let b = RagContextBuilder::new(store, None, 1, 100);
    let result = b.build_context("q");
    assert!(result.contains(&(("x".repeat(49)) + "…")));
    assert!(!result.contains(&"x".repeat(51)));
}

// ══════════════════════════════════════════════════════════════════════════
// Embedder ranking path
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn ranks_by_the_embedding_when_an_embedder_is_supplied() {
    let store = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    add(
        &store,
        episodic(EpisodicOpts {
            user_text: "near",
            assistant_text: "n",
            embedding: Some(vec![1.0, 0.0]),
            ..Default::default()
        }),
    );
    add(
        &store,
        episodic(EpisodicOpts {
            user_text: "far",
            assistant_text: "f",
            embedding: Some(vec![0.0, 1.0]),
            ..Default::default()
        }),
    );

    // Embedder maps any query to the x-axis, so "near" should rank first.
    let embedder: Arc<dyn ITextEmbedder> = Arc::new(FixedEmbedder(vec![1.0, 0.0]));
    let b = RagContextBuilder::new(store, Some(embedder), 1, 300);
    let result = b.build_context("anything");
    assert!(result.contains("near"));
    assert!(!result.contains("far"));
}

#[test]
fn falls_back_to_recency_when_the_embedder_fails() {
    let store = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    add(
        &store,
        episodic(EpisodicOpts {
            user_text: "only",
            assistant_text: "entry",
            recorded: Some(Utc.with_ymd_and_hms(2026, 6, 1, 0, 0, 0).unwrap()),
            ..Default::default()
        }),
    );
    let embedder: Arc<dyn ITextEmbedder> = Arc::new(FailingEmbedder);
    let b = RagContextBuilder::new(store, Some(embedder), 3, 300);
    let result = b.build_context("q");
    assert!(result.contains("only"));
}

// ══════════════════════════════════════════════════════════════════════════
// Resilience — store fails
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn returns_empty_when_the_store_fails() {
    let store: Arc<dyn RagEpisodicStore> = Arc::new(ThrowingEpisodicStore);
    let b = RagContextBuilder::with_store(store);
    assert_eq!(b.build_context("query"), "");
}

// ══════════════════════════════════════════════════════════════════════════
// RagPipelineBuilder
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn builds_from_an_in_memory_store_and_produces_a_working_builder() {
    let store = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    add(
        &store,
        episodic(EpisodicOpts {
            user_text: "hi",
            assistant_text: "hello",
            ..Default::default()
        }),
    );
    let rag = RagPipelineBuilder::create()
        .with_store(store)
        .with_top_k(2)
        .unwrap()
        .with_max_chars_per_entry(500)
        .unwrap()
        .build()
        .unwrap();
    let ctx = rag.build_context("greeting");
    assert!(ctx.contains("hi"));
}

#[test]
fn with_in_memory_store_wires_a_fresh_store() {
    let rag = RagPipelineBuilder::create()
        .with_in_memory_store()
        .build()
        .unwrap();
    assert_eq!(rag.build_context("nothing stored"), "");
}

#[test]
fn build_without_a_store_fails() {
    // Map to the error string first — RagContextBuilder is not Debug, so
    // unwrap_err() would not compile.
    let msg = RagPipelineBuilder::create()
        .build()
        .err()
        .map(|e| e.to_string());
    assert!(msg
        .as_deref()
        .unwrap_or("")
        .to_lowercase()
        .contains("episodic memory store is required"));
}

#[test]
fn with_top_k_rejects_values_below_1() {
    assert!(RagPipelineBuilder::create().with_top_k(0).is_err());
}

#[test]
fn with_max_chars_per_entry_rejects_values_below_50() {
    assert!(RagPipelineBuilder::create()
        .with_max_chars_per_entry(49)
        .is_err());
}

#[test]
fn with_embedder_wires_the_semantic_ranking_seam() {
    let store = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    add(
        &store,
        episodic(EpisodicOpts {
            user_text: "near",
            assistant_text: "n",
            embedding: Some(vec![1.0, 0.0]),
            ..Default::default()
        }),
    );
    add(
        &store,
        episodic(EpisodicOpts {
            user_text: "far",
            assistant_text: "f",
            embedding: Some(vec![0.0, 1.0]),
            ..Default::default()
        }),
    );
    let embedder: Arc<dyn ITextEmbedder> = Arc::new(FixedEmbedder(vec![1.0, 0.0]));
    let rag = RagPipelineBuilder::create()
        .with_store(store)
        .with_embedder(embedder)
        .with_top_k(1)
        .unwrap()
        .build()
        .unwrap();
    let ctx = rag.build_context("q");
    assert!(ctx.contains("near"));
}
