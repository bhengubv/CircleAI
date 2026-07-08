//! prefix_cache.rs
//!
//! RT-06: cross-session prefix cache. Ported from
//! `CircleAI.Inference/PrefixCacheService.cs`.
//!
//! Snapshot the model's warm KV state once per (modelId, systemPrompt) pair,
//! reload it on the next chat with the same pair, and skip the prefill entirely.
//!
//! The C# service owns only the *indexing* — the on-disk format is MNN's. Per
//! the no-real-IO porting brief, this Rust port keys the same way (byte-exact
//! [`PrefixCacheService::key_for`]) but stores snapshot payloads + their
//! last-used ordinal in memory. Eviction keeps the cache under a 500 MB cap by
//! dropping least-recently-used entries first — the same LRU-by-mtime policy the
//! C# `EvictIfNeededAsync` runs, with a monotonically increasing "touch"
//! ordinal standing in for the file mtime.

use std::collections::HashMap;

use crate::memory::multimodal::compute_sha256;

const CAP_BYTES: u64 = 500 * 1024 * 1024; // 500 MB

/// One cached warm-session entry.
#[derive(Debug, Clone)]
struct Entry {
    /// Opaque snapshot payload (the KV snapshot bytes MNN would have written).
    payload: Vec<u8>,
    /// Monotonic last-used ordinal; higher = more recently used.
    touched: u64,
}

/// Manages a cache of "warm" model sessions keyed by the hash of (modelId,
/// systemPrompt). Generators that opt in via `GenerationOptions.use_prefix_cache`
/// consult this service before resetting the model handle for a new
/// conversation. Thread-unsafe by itself — wrap in a `Mutex`/`RwLock` when
/// shared across threads (the C# service serialises via a semaphore).
#[derive(Debug, Default)]
pub struct PrefixCacheService {
    entries: HashMap<String, Entry>,
    clock: u64,
}

impl PrefixCacheService {
    /// Constructs an empty cache service.
    pub fn new() -> Self {
        Self {
            entries: HashMap::new(),
            clock: 0,
        }
    }

    /// Compute the cache key for a (modelId, systemPrompt) pair. Returns `None`
    /// when `model_id` is blank or `system_prompt` is `None`/empty — there is
    /// nothing to cache without a system prompt to key against.
    ///
    /// Byte-exact with the C#: `sha256(model_id)[..16] + "_" + sha256(system)[..16]`.
    pub fn key_for(model_id: &str, system_prompt: Option<&str>) -> Option<String> {
        if model_id.trim().is_empty() {
            return None;
        }
        let system = match system_prompt {
            Some(s) if !s.is_empty() => s,
            _ => return None,
        };

        let model_hash = compute_sha256(model_id.as_bytes());
        let system_hash = compute_sha256(system.as_bytes());
        // First 16 hex chars per component — collision-free at any single
        // device's cache scale, much shorter on disk.
        Some(format!("{}_{}", &model_hash[..16], &system_hash[..16]))
    }

    /// `true` when a cached entry exists for `key`.
    pub fn has_entry(&self, key: &str) -> bool {
        self.entries.contains_key(key)
    }

    /// Number of currently cached entries. Diagnostics only.
    pub fn len(&self) -> usize {
        self.entries.len()
    }

    /// `true` when the cache holds no entries.
    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// Store a warm-session snapshot for `key`, then bound the cache. Mirrors
    /// the C# `SaveSession` → `EvictIfNeededAsync` sequence: the write always
    /// happens, then eviction keeps the total under the cap.
    pub fn save(&mut self, key: &str, payload: impl Into<Vec<u8>>) {
        self.clock += 1;
        let touched = self.clock;
        self.entries.insert(
            key.to_string(),
            Entry {
                payload: payload.into(),
                touched,
            },
        );
        self.evict_if_needed();
    }

    /// Load a previously-saved snapshot, touching it so LRU eviction treats it
    /// as recently used (mirrors the C# `Touch` after a successful load).
    /// Returns `None` when the key is absent.
    pub fn load(&mut self, key: &str) -> Option<Vec<u8>> {
        self.clock += 1;
        let now = self.clock;
        match self.entries.get_mut(key) {
            Some(e) => {
                e.touched = now;
                Some(e.payload.clone())
            }
            None => None,
        }
    }

    /// Touch the entry so LRU eviction treats it as recently used. No-op when
    /// the key is absent. Mirrors the C# `Touch(key)`.
    pub fn touch(&mut self, key: &str) {
        self.clock += 1;
        let now = self.clock;
        if let Some(e) = self.entries.get_mut(key) {
            e.touched = now;
        }
    }

    /// Total bytes currently held across all cached snapshots.
    pub fn total_bytes(&self) -> u64 {
        self.entries.values().map(|e| e.payload.len() as u64).sum()
    }

    /// Evict least-recently-used entries until the cache is under the 500 MB
    /// cap. Called after every `save`. Best-effort and idempotent.
    pub fn evict_if_needed(&mut self) {
        let mut total = self.total_bytes();
        if total <= CAP_BYTES {
            return;
        }
        // Order keys oldest-first (lowest touch ordinal), evicting until under.
        let mut ordered: Vec<(String, u64, u64)> = self
            .entries
            .iter()
            .map(|(k, e)| (k.clone(), e.touched, e.payload.len() as u64))
            .collect();
        ordered.sort_by_key(|(_, touched, _)| *touched);

        for (key, _touched, size) in ordered {
            if total <= CAP_BYTES {
                break;
            }
            self.entries.remove(&key);
            total -= size;
        }
    }
}
