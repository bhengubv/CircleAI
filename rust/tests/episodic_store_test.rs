//! episodic_store_test.rs
//!
//! Verifies InMemoryEpisodicStore: cosine similarity search, recency fallback,
//! FIFO capacity eviction, prune, and count. Mirrors the TS pilot suite
//! tests/episodic_store.test.ts and the Go suite episodic_store_test.go 1:1.

use chrono::{TimeZone, Utc};
use circle_ai::memory::episodic::{EpisodicSearch, InMemoryEpisodicStore};
use circle_ai::memory::EpisodicMemoryEntry;
use uuid::Uuid;

/// Maps a short test label to a stable UUID so entries can be identified by label
/// (the TS/Go suites use string ids like 'x'/'a'). Deterministic without the
/// uuid `v5` feature: a simple FNV-1a hash of the label seeds the 16 bytes.
fn label_id(label: &str) -> Uuid {
    let seed = format!("episodic-test:{label}");
    let mut bytes = [0u8; 16];
    let mut hash: u64 = 0xcbf29ce484222325;
    for (i, b) in seed.bytes().enumerate() {
        hash ^= b as u64;
        hash = hash.wrapping_mul(0x100000001b3);
        bytes[i % 16] ^= (hash >> ((i % 8) * 8)) as u8;
    }
    // Fold the final hash across all bytes so short labels still differ.
    for (i, byte) in bytes.iter_mut().enumerate() {
        *byte ^= (hash >> ((i % 8) * 8)) as u8;
    }
    Uuid::from_bytes(bytes)
}

struct EntryOpts<'a> {
    id: &'a str,
    user_text: &'a str,
    embedding: Option<Vec<f32>>,
    recorded: Option<chrono::DateTime<Utc>>,
}

impl<'a> Default for EntryOpts<'a> {
    fn default() -> Self {
        Self {
            id: "",
            user_text: "",
            embedding: None,
            recorded: None,
        }
    }
}

fn mk_entry(o: EntryOpts) -> EpisodicMemoryEntry {
    let rec = o
        .recorded
        .unwrap_or_else(|| Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap());
    let ut = if o.user_text.is_empty() { "u" } else { o.user_text };
    EpisodicMemoryEntry {
        id: label_id(o.id),
        recorded_at_utc: rec,
        user_text: ut.to_string(),
        assistant_text: "a".to_string(),
        app_context: None,
        embedding: o.embedding,
        tags: None,
    }
}

fn must_add(store: &InMemoryEpisodicStore, e: EpisodicMemoryEntry) {
    store.add_shared(e).expect("Add");
}

// ── Cosine search ────────────────────────────────────────────────────────────

#[test]
fn cosine_search_ranks_the_nearest_embedding_first() {
    let store = InMemoryEpisodicStore::with_default_capacity();
    must_add(&store, mk_entry(EntryOpts { id: "x", user_text: "x-axis", embedding: Some(vec![1.0, 0.0]), ..Default::default() }));
    must_add(&store, mk_entry(EntryOpts { id: "y", user_text: "y-axis", embedding: Some(vec![0.0, 1.0]), ..Default::default() }));

    let hits = store.search(Some(&[1.0, 0.0]), 2).expect("Search");
    assert_eq!(hits.len(), 2);
    assert_eq!(hits[0].id, label_id("x"));
    assert_eq!(hits[1].id, label_id("y"));
}

#[test]
fn cosine_search_respects_top_k() {
    let store = InMemoryEpisodicStore::with_default_capacity();
    must_add(&store, mk_entry(EntryOpts { id: "a", embedding: Some(vec![1.0, 0.0]), ..Default::default() }));
    must_add(&store, mk_entry(EntryOpts { id: "b", embedding: Some(vec![0.9, 0.1]), ..Default::default() }));
    must_add(&store, mk_entry(EntryOpts { id: "c", embedding: Some(vec![0.0, 1.0]), ..Default::default() }));

    let hits = store.search(Some(&[1.0, 0.0]), 1).expect("Search");
    assert_eq!(hits.len(), 1);
    assert_eq!(hits[0].id, label_id("a"));
}

#[test]
fn cosine_search_ignores_dimension_mismatch() {
    let store = InMemoryEpisodicStore::with_default_capacity();
    must_add(&store, mk_entry(EntryOpts { id: "ok", embedding: Some(vec![1.0, 0.0]), ..Default::default() }));
    must_add(&store, mk_entry(EntryOpts { id: "wrongdim", embedding: Some(vec![1.0, 0.0, 0.0]), ..Default::default() }));

    let hits = store.search(Some(&[1.0, 0.0]), 5).expect("Search");
    assert_eq!(hits.len(), 1);
    assert_eq!(hits[0].id, label_id("ok"));
}

// ── Recency fallback ─────────────────────────────────────────────────────────

#[test]
fn recency_returns_newest_first_when_embedding_is_none() {
    let old = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    let recent = Utc.with_ymd_and_hms(2026, 6, 1, 0, 0, 0).unwrap();
    let store = InMemoryEpisodicStore::with_default_capacity();
    must_add(&store, mk_entry(EntryOpts { id: "old", recorded: Some(old), ..Default::default() }));
    must_add(&store, mk_entry(EntryOpts { id: "new", recorded: Some(recent), ..Default::default() }));

    let hits = store.search(None, 5).expect("Search");
    assert_eq!(hits[0].id, label_id("new"));
    assert_eq!(hits[1].id, label_id("old"));
}

#[test]
fn recency_treats_empty_embedding_as_none() {
    let old = Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap();
    let recent = Utc.with_ymd_and_hms(2026, 6, 1, 0, 0, 0).unwrap();
    let store = InMemoryEpisodicStore::with_default_capacity();
    must_add(&store, mk_entry(EntryOpts { id: "old", recorded: Some(old), ..Default::default() }));
    must_add(&store, mk_entry(EntryOpts { id: "new", recorded: Some(recent), ..Default::default() }));

    let empty: [f32; 0] = [];
    let hits = store.search(Some(&empty), 1).expect("Search");
    assert_eq!(hits[0].id, label_id("new"));
}

// ── Capacity & maintenance ───────────────────────────────────────────────────

#[test]
fn capacity_evicts_oldest_beyond_max_entries_fifo() {
    let store = InMemoryEpisodicStore::new(2).expect("New");
    must_add(&store, mk_entry(EntryOpts { id: "a", ..Default::default() }));
    must_add(&store, mk_entry(EntryOpts { id: "b", ..Default::default() }));
    must_add(&store, mk_entry(EntryOpts { id: "c", ..Default::default() }));

    assert_eq!(store.count_shared().unwrap(), 2);
    let recent = store.get_recent_shared(10).expect("GetRecent");
    let mut ids: Vec<String> = recent.iter().map(|e| e.id.to_string()).collect();
    ids.sort();
    let mut want = vec![label_id("b").to_string(), label_id("c").to_string()];
    want.sort();
    assert_eq!(ids, want, "a should be evicted");
}

#[test]
fn prune_removes_entries_older_than_cutoff_and_returns_count() {
    let store = InMemoryEpisodicStore::with_default_capacity();
    must_add(&store, mk_entry(EntryOpts { id: "old", recorded: Some(Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap()), ..Default::default() }));
    must_add(&store, mk_entry(EntryOpts { id: "new", recorded: Some(Utc.with_ymd_and_hms(2026, 6, 1, 0, 0, 0).unwrap()), ..Default::default() }));

    let cutoff = Utc.with_ymd_and_hms(2026, 3, 1, 0, 0, 0).unwrap();
    let removed = store.prune_older_than_shared(&cutoff).expect("PruneOlderThan");
    assert_eq!(removed, 1);
    assert_eq!(store.count_shared().unwrap(), 1);
    let remaining = store.get_recent_shared(10).unwrap();
    assert_eq!(remaining[0].id, label_id("new"));
}

#[test]
fn rejects_non_positive_max_entries() {
    assert!(InMemoryEpisodicStore::new(0).is_err());
}
