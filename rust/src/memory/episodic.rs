//! episodic.rs
//!
//! Concrete in-memory episodic store for the memory-brain. Ported from
//! CircleAI.Memory (InMemoryEpisodicStore) — the C# reference — and mirrors the
//! TypeScript pilot (memory/stores.ts) and the Go port (memory_stores.go) 1:1.
//!
//! All data is lost when the process exits; a persistent (SQLite) backend is a
//! later slice. The algorithms (cosine similarity, recency fallback, FIFO cap)
//! are identical to the reference. cosine == dot product because both vectors
//! are L2-normalised at write time.
//!
//! Concurrency: the store uses interior mutability (a [`Mutex`]) so it can be
//! shared behind an [`std::sync::Arc`] between the recall path, the session, and
//! the background encoder — matching the reference stores, which are all
//! internally synchronised. This also lets the same value satisfy both the
//! `&mut self` sync [`IEpisodicMemoryStore`] trait and the shared-access
//! [`EpisodicSearch`] recall seam.

use chrono::{DateTime, Utc};
use std::sync::Mutex;

use super::stores::{EpisodicMemoryEntry, IEpisodicMemoryStore};
use crate::brain::BrainError;

/// The shared-access episodic recall seam used by `FusedRecall`. Mirrors the
/// `Search` method of the Go/TS `IEpisodicMemoryStore` interface, taking `&self`
/// so the store can be shared. Tests inject a fake implementation.
pub trait EpisodicSearch: Send + Sync {
    /// Returns the `top_k` entries most similar (cosine) to `query_embedding`.
    /// When `query_embedding` is `None` or empty, falls back to recency
    /// (newest-first). Only entries whose embedding dimension matches the query
    /// take part in the cosine ranking.
    fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<EpisodicMemoryEntry>, BrainError>;
}

/// In-memory [`IEpisodicMemoryStore`]. Capacity is capped (FIFO eviction) to
/// prevent unbounded growth on long-running processes. All methods are safe for
/// concurrent use.
#[derive(Debug)]
pub struct InMemoryEpisodicStore {
    max_entries: usize,
    entries: Mutex<Vec<EpisodicMemoryEntry>>,
}

impl InMemoryEpisodicStore {
    /// Creates a store capped at `max_entries`. When the cap is exceeded the
    /// oldest entries are evicted (FIFO). `max_entries` must be positive.
    pub fn new(max_entries: usize) -> Result<Self, BrainError> {
        if max_entries == 0 {
            return Err(BrainError::new("maxEntries must be positive"));
        }
        Ok(Self {
            max_entries,
            entries: Mutex::new(Vec::new()),
        })
    }

    /// Creates a store with the default cap of 1000.
    pub fn with_default_capacity() -> Self {
        Self {
            max_entries: 1000,
            entries: Mutex::new(Vec::new()),
        }
    }

    /// Appends a new entry, evicting the oldest entries once the cap is exceeded
    /// (FIFO). Shared-access counterpart of the `&mut self` trait method so the
    /// store can be driven behind an `Arc`.
    pub fn add_shared(&self, entry: EpisodicMemoryEntry) -> Result<(), BrainError> {
        let mut entries = self.entries.lock().unwrap();
        entries.push(entry);
        while entries.len() > self.max_entries {
            entries.remove(0);
        }
        Ok(())
    }

    /// Returns the most recent `count` entries, newest-first.
    pub fn get_recent_shared(&self, count: usize) -> Result<Vec<EpisodicMemoryEntry>, BrainError> {
        let entries = self.entries.lock().unwrap();
        let mut snapshot = entries.clone();
        drop(entries);
        sort_by_recency_desc(&mut snapshot);
        Ok(take_entries(snapshot, count))
    }

    /// Returns the number of entries currently stored.
    pub fn count_shared(&self) -> Result<usize, BrainError> {
        Ok(self.entries.lock().unwrap().len())
    }

    /// Removes all entries recorded strictly before `cutoff` and returns the
    /// number removed.
    pub fn prune_older_than_shared(
        &self,
        cutoff: &DateTime<Utc>,
    ) -> Result<usize, BrainError> {
        let mut entries = self.entries.lock().unwrap();
        let before = entries.len();
        entries.retain(|e| e.recorded_at_utc >= *cutoff);
        Ok(before - entries.len())
    }
}

impl EpisodicSearch for InMemoryEpisodicStore {
    fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<EpisodicMemoryEntry>, BrainError> {
        let entries = self.entries.lock().unwrap();
        let snapshot: Vec<EpisodicMemoryEntry> = entries.clone();
        drop(entries);

        let query = match query_embedding {
            Some(q) if !q.is_empty() => q,
            _ => {
                // No embedding — return most recent.
                let mut recent = snapshot;
                sort_by_recency_desc(&mut recent);
                return Ok(take_entries(recent, top_k));
            }
        };

        // Cosine similarity, only against entries whose embedding matches the
        // query dimension. Both vectors are L2-normalised, so cosine == dot.
        let mut candidates: Vec<(EpisodicMemoryEntry, f32)> = snapshot
            .into_iter()
            .filter_map(|e| match &e.embedding {
                Some(emb) if emb.len() == query.len() => {
                    let score = cosine_similarity(query, emb);
                    Some((e, score))
                }
                _ => None,
            })
            .collect();

        // Stable sort by score descending (mirrors Go's sort.SliceStable).
        candidates.sort_by(|a, b| {
            b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal)
        });

        let limit = top_k.min(candidates.len());
        Ok(candidates
            .into_iter()
            .take(limit)
            .map(|(e, _)| e)
            .collect())
    }
}

// The sync `IEpisodicMemoryStore` trait (kept for portability / no-std targets):
// the concrete store also satisfies it. `add`/`prune_older_than` take `&mut self`
// per the trait; they delegate to the shared-access methods.
impl IEpisodicMemoryStore for InMemoryEpisodicStore {
    type Error = BrainError;

    fn add(&mut self, entry: EpisodicMemoryEntry) -> Result<(), Self::Error> {
        self.add_shared(entry)
    }

    fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<EpisodicMemoryEntry>, Self::Error> {
        <Self as EpisodicSearch>::search(self, query_embedding, top_k)
    }

    fn get_recent(&self, count: usize) -> Result<Vec<EpisodicMemoryEntry>, Self::Error> {
        self.get_recent_shared(count)
    }

    fn count(&self) -> Result<usize, Self::Error> {
        self.count_shared()
    }

    fn prune_older_than(&mut self, cutoff: &DateTime<Utc>) -> Result<usize, Self::Error> {
        self.prune_older_than_shared(cutoff)
    }
}

/// Sorts entries newest-first (stable).
fn sort_by_recency_desc(entries: &mut [EpisodicMemoryEntry]) {
    entries.sort_by(|a, b| b.recorded_at_utc.cmp(&a.recorded_at_utc));
}

/// Returns the first `n` entries (or all when fewer exist).
fn take_entries(mut entries: Vec<EpisodicMemoryEntry>, n: usize) -> Vec<EpisodicMemoryEntry> {
    entries.truncate(n);
    entries
}

/// Cosine similarity of two equal-length, L2-normalised vectors (== dot product).
fn cosine_similarity(a: &[f32], b: &[f32]) -> f32 {
    let mut dot = 0.0f32;
    let n = a.len().min(b.len());
    for i in 0..n {
        dot += a[i] * b[i];
    }
    dot
}
