//! recall.rs
//!
//! Fused associative recall (Reciprocal Rank Fusion). Ported from
//! CircleAI.Companion (IRecall, FusedRecall) — the C# reference — and mirrors the
//! TypeScript pilot (memory/recall.ts) and the Go port (memory_recall.go) 1:1.
//!
//! Fuses two memory systems with incomparable score spaces — episodic cosine
//! similarity and graph association (Personalised PageRank) — into one ranked
//! context. RRF combines ranked lists by *position*, so it needs no shared score
//! scale: each source contributes 1 / (k + rank).
//!
//! Cold-start is automatic: a new user has an empty graph, so only episodic
//! contributes and the fused order equals the episodic order — no special case.

use std::collections::HashMap;
use std::sync::Arc;

use super::episodic::EpisodicSearch;
use super::graph::{IHippoRagStore, MemoryHit, MemoryItem};
use super::stores::EpisodicMemoryEntry;
use crate::brain::BrainError;

/// Unified memory recall — the most relevant memories for a turn.
pub trait IRecall: Send + Sync {
    /// Returns the `top_k` most relevant memories for the current turn. `query`
    /// drives graph association; `query_embedding` drives episodic cosine
    /// similarity (may be `None` → episodic recency fallback).
    fn recall(
        &self,
        query: &str,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<MemoryHit>, BrainError>;
}

/// Tuning for [`FusedRecall`].
#[derive(Debug, Clone, Copy)]
pub struct FusedRecallOptions {
    /// Number of candidates pulled from each source before fusion. Default 20.
    pub candidate_pool_size: usize,
    /// The RRF damping constant k. Default 60 (the standard value).
    pub rrf_k: usize,
    /// Drops graph hits whose backing confidence (metadata key `"confidence"`)
    /// is below it. Applied only when a hit actually carries a confidence value.
    /// Default 0.4.
    pub graph_confidence_threshold: f64,
}

impl Default for FusedRecallOptions {
    fn default() -> Self {
        Self {
            candidate_pool_size: 20,
            rrf_k: 60,
            graph_confidence_threshold: 0.4,
        }
    }
}

/// Reciprocal-Rank-Fusion recall over episodic similarity + graph association.
pub struct FusedRecall {
    episodic: Arc<dyn EpisodicSearch>,
    graph: Option<Arc<dyn IHippoRagStore>>,
    opts: FusedRecallOptions,
}

impl FusedRecall {
    /// Creates a [`FusedRecall`]. `graph` may be `None` (cold-start / pure
    /// episodic). `opts` of `None` uses defaults; any zero numeric field falls
    /// back to its default (mirrors the Go merge semantics).
    pub fn new(
        episodic: Arc<dyn EpisodicSearch>,
        graph: Option<Arc<dyn IHippoRagStore>>,
        opts: Option<FusedRecallOptions>,
    ) -> Result<Self, BrainError> {
        let mut merged = FusedRecallOptions::default();
        if let Some(o) = opts {
            if o.candidate_pool_size != 0 {
                merged.candidate_pool_size = o.candidate_pool_size;
            }
            if o.rrf_k != 0 {
                merged.rrf_k = o.rrf_k;
            }
            if o.graph_confidence_threshold != 0.0 {
                merged.graph_confidence_threshold = o.graph_confidence_threshold;
            }
        }
        Ok(Self {
            episodic,
            graph,
            opts: merged,
        })
    }
}

impl IRecall for FusedRecall {
    /// Runs episodic similarity (or recency), best-effort graph association, and
    /// fuses them by Reciprocal Rank Fusion.
    fn recall(
        &self,
        query: &str,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<MemoryHit>, BrainError> {
        if top_k == 0 {
            return Err(BrainError::new("topK must be positive"));
        }

        let pool = self.opts.candidate_pool_size;

        // Fast path: episodic similarity (or recency when the embedding is nil).
        let episodic = self.episodic.search(query_embedding, pool)?;

        // Slow path: graph association. Optional and best-effort — a missing,
        // empty, or failing graph degrades to pure episodic, never propagates the
        // error. An empty query cannot seed a graph walk, so skip it.
        let mut graph: Vec<MemoryHit> = Vec::new();
        if let Some(g) = &self.graph {
            if !query.trim().is_empty() {
                if let Ok(hits) = g.multi_hop_recall(query, pool) {
                    graph = hits;
                }
            }
        }

        // Reciprocal Rank Fusion: accumulate 1 / (k + rank) per candidate across
        // both ranked lists, keyed by normalised text so a memory surfaced by
        // both sources reinforces rather than duplicates.
        let k = self.opts.rrf_k as f64;

        struct FusedEntry {
            item: MemoryItem,
            score: f64,
        }
        let mut fused: HashMap<String, FusedEntry> = HashMap::new();
        // Insertion order, for stable output among equal scores.
        let mut order: Vec<String> = Vec::new();

        let mut accumulate = |item: MemoryItem, one_based_rank: usize| {
            let key = normalise_key(&item.text);
            if key.is_empty() {
                return;
            }
            let contribution = 1.0 / (k + one_based_rank as f64);
            match fused.get_mut(&key) {
                Some(existing) => existing.score += contribution,
                None => {
                    order.push(key.clone());
                    fused.insert(key, FusedEntry { item, score: contribution });
                }
            }
        };

        for (i, e) in episodic.into_iter().enumerate() {
            accumulate(adapt_episodic(e), i + 1);
        }
        for (i, h) in graph.into_iter().enumerate() {
            if is_below_confidence(&h, self.opts.graph_confidence_threshold) {
                continue;
            }
            accumulate(h.item, i + 1);
        }

        // Rank position for stable tie-breaking, mirroring the pilot's Map order.
        let pos: HashMap<String, usize> = order
            .iter()
            .enumerate()
            .map(|(i, key)| (key.clone(), i))
            .collect();

        let mut result: Vec<MemoryHit> = fused
            .into_values()
            .map(|e| MemoryHit {
                item: e.item,
                score: e.score,
            })
            .collect();
        result.sort_by(|a, b| {
            if a.score != b.score {
                b.score.partial_cmp(&a.score).unwrap_or(std::cmp::Ordering::Equal)
            } else {
                let pa = pos.get(&normalise_key(&a.item.text)).copied().unwrap_or(usize::MAX);
                let pb = pos.get(&normalise_key(&b.item.text)).copied().unwrap_or(usize::MAX);
                pa.cmp(&pb)
            }
        });
        result.truncate(top_k);
        Ok(result)
    }
}

/// Reports whether a graph hit carries a confidence value below the threshold. A
/// hit with no confidence metadata is never below (gate no-op).
fn is_below_confidence(hit: &MemoryHit, threshold: f64) -> bool {
    let meta = match &hit.item.metadata {
        Some(m) => m,
        None => return false,
    };
    let raw = match meta.get("confidence") {
        Some(r) => r,
        None => return false,
    };
    match raw.parse::<f64>() {
        Ok(c) => c < threshold,
        Err(_) => false,
    }
}

/// Maps an episodic entry into the shared [`MemoryItem`] currency, keyed by the
/// user's text and stamped with episodic provenance metadata.
fn adapt_episodic(e: EpisodicMemoryEntry) -> MemoryItem {
    let mut meta: HashMap<String, String> = HashMap::new();
    meta.insert("source".to_string(), "episodic".to_string());
    meta.insert(
        "recordedAt".to_string(),
        e.recorded_at_utc
            .to_rfc3339_opts(chrono::SecondsFormat::Nanos, true),
    );
    if !e.assistant_text.is_empty() {
        meta.insert("assistantText".to_string(), e.assistant_text.clone());
    }
    if let Some(app) = &e.app_context {
        if !app.is_empty() {
            meta.insert("appContext".to_string(), app.clone());
        }
    }
    MemoryItem {
        id: e.id.to_string(),
        text: e.user_text,
        metadata: Some(meta),
    }
}

/// Lowercases and collapses internal whitespace so equivalent texts fuse to one
/// key.
fn normalise_key(text: &str) -> String {
    let trimmed = text.trim();
    if trimmed.is_empty() {
        return String::new();
    }
    let mut out = String::with_capacity(trimmed.len());
    let mut prev_space = false;
    for ch in trimmed.chars() {
        if ch.is_whitespace() {
            if !prev_space {
                out.push(' ');
                prev_space = true;
            }
        } else {
            out.extend(ch.to_lowercase());
            prev_space = false;
        }
    }
    out
}
