//! multimodal.rs
//!
//! Compressed semantic memory for media artefacts (image / audio / video /
//! document). Ported from CircleAI.Memory.Multimodal (C#) and mirrors the
//! TypeScript reference (memory/multimodal.ts) 1:1:
//!   - `MediaModality`, `MultimodalMemoryEntry`
//!   - `IMultimodalCaptioner` + `CaptionResult` + `HeuristicMultimodalCaptioner`
//!   - `IMultimodalMemoryStore` + `InMemoryMultimodalMemoryStore`
//!   - `MultimodalMemoryIngester` (+ `IngestionResult`)
//!
//! The whole point: we DO NOT store the pixels / audio samples / video frames —
//! we store the caption, the embedding, and a SHA-256 of the original so the host
//! can reference it back if it kept the file elsewhere. Raw bytes never leave the
//! captioner; the store only ever holds the semantic record.

use chrono::{DateTime, Utc};
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::sync::Mutex;
use uuid::Uuid;

use crate::brain::BrainError;

// ─────────────────────────────────────────────────────────────────────────────
// MediaModality — CircleAI.Memory.Multimodal.MediaModality
// ─────────────────────────────────────────────────────────────────────────────

/// Modality of a multimodal memory entry. Drives how the ingester routes the raw
/// bytes to the captioner and which side-channel metadata is captured.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum MediaModality {
    /// Still image — JPEG, PNG, HEIC, WebP, AVIF.
    Image,
    /// Audio clip — Opus, WAV, MP3, M4A.
    Audio,
    /// Video — MP4, MOV, WebM. Captioned via key-frame extraction by the host.
    Video,
    /// Text document — PDF, DOCX, plain text snippet larger than a single message.
    TextDocument,
}

// ─────────────────────────────────────────────────────────────────────────────
// MultimodalMemoryEntry — CircleAI.Memory.Multimodal.MultimodalMemoryEntry
// ─────────────────────────────────────────────────────────────────────────────

/// One semantically-compressed media memory. The caption + embedding capture the
/// meaning; raw bytes are never retained by the memory layer.
///
/// `reference_count` is mutable (incremented on dedup hits); everything else is
/// effectively write-once, matching the C# `init`/`set` split.
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct MultimodalMemoryEntry {
    /// Stable identifier (UUID v4).
    pub id: Uuid,
    /// UTC timestamp the memory was recorded.
    pub recorded_at_utc: DateTime<Utc>,
    /// Which kind of media this came from.
    pub modality: MediaModality,
    /// Caption — the semantic content.
    pub caption: String,
    /// Embedding of the caption (and, for richer captioners, the joint embedding).
    pub embedding: Option<Vec<f32>>,
    /// SHA-256 of the original bytes, hex-lower.
    pub source_sha256: String,
    /// Original MIME type (e.g. `image/jpeg`). Captured for diagnostics.
    pub source_mime_type: Option<String>,
    /// Size in bytes of the original artefact.
    pub source_byte_count: i64,
    /// Optional URI of the original artefact if the host retained it elsewhere.
    pub source_uri: Option<String>,
    /// Image / video width in pixels, when applicable.
    pub width_px: Option<i32>,
    /// Image / video height in pixels, when applicable.
    pub height_px: Option<i32>,
    /// Audio / video duration in milliseconds, when applicable.
    pub duration_ms: Option<i64>,
    /// How many times this artefact has been re-presented to the ingester.
    /// Incremented on every dedup hit instead of creating a new entry. Mutable.
    pub reference_count: i32,
    /// Optional tags (e.g. location, person, topic).
    pub tags: Option<HashMap<String, String>>,
}

impl Default for MultimodalMemoryEntry {
    /// Fills the same defaults the C# record's initialisers do: fresh UUID id,
    /// `recorded_at_utc = now`, `reference_count = 1`, `modality = Image`.
    fn default() -> Self {
        Self {
            id: Uuid::new_v4(),
            recorded_at_utc: Utc::now(),
            modality: MediaModality::Image,
            caption: String::new(),
            embedding: None,
            source_sha256: String::new(),
            source_mime_type: None,
            source_byte_count: 0,
            source_uri: None,
            width_px: None,
            height_px: None,
            duration_ms: None,
            reference_count: 1,
            tags: None,
        }
    }
}

impl MultimodalMemoryEntry {
    /// Builds an entry from a SHA-256, applying the C# defaults for every other
    /// field. Callers override fields by mutating the returned value.
    pub fn with_hash(source_sha256: impl Into<String>) -> Self {
        Self {
            source_sha256: source_sha256.into(),
            ..Default::default()
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// IMultimodalCaptioner + CaptionResult + HeuristicMultimodalCaptioner
// ─────────────────────────────────────────────────────────────────────────────

/// Output of a single captioning call.
#[derive(Debug, Clone, Default)]
pub struct CaptionResult {
    /// Human-readable semantic description of the artefact. Must not be empty.
    pub caption: String,
    /// Embedding of the artefact. `None` when the captioner has no embedding backend.
    pub embedding: Option<Vec<f32>>,
    /// Image / video width when known.
    pub width_px: Option<i32>,
    /// Image / video height when known.
    pub height_px: Option<i32>,
    /// Audio / video duration when known.
    pub duration_ms: Option<i64>,
}

/// Converts raw media bytes into a semantic representation.
pub trait IMultimodalCaptioner: Send + Sync {
    /// True when this captioner can handle the given modality + mime. The
    /// ingester picks among multiple captioners using this predicate.
    fn can_caption(&self, modality: MediaModality, mime_type: Option<&str>) -> bool;

    /// Produces a [`CaptionResult`] for the given source bytes. Implementations
    /// must not retain the bytes after the call returns.
    fn caption(
        &self,
        modality: MediaModality,
        source_bytes: &[u8],
        mime_type: Option<&str>,
    ) -> Result<CaptionResult, BrainError>;
}

/// Default [`IMultimodalCaptioner`]. Returns a descriptive shell caption — never
/// fabricates semantic content. Always available, zero model dependency, zero
/// token cost.
#[derive(Debug, Default, Clone)]
pub struct HeuristicMultimodalCaptioner;

impl IMultimodalCaptioner for HeuristicMultimodalCaptioner {
    fn can_caption(&self, _modality: MediaModality, _mime_type: Option<&str>) -> bool {
        true
    }

    fn caption(
        &self,
        modality: MediaModality,
        source_bytes: &[u8],
        mime_type: Option<&str>,
    ) -> Result<CaptionResult, BrainError> {
        let detected = detect_mime(source_bytes, mime_type);
        let len = source_bytes.len();
        let caption = match modality {
            MediaModality::Image => {
                format!("[Image — no captioner wired. {detected}, {len} bytes.]")
            }
            MediaModality::Audio => {
                format!("[Audio — no captioner wired. {detected}, {len} bytes.]")
            }
            MediaModality::Video => {
                format!("[Video — no captioner wired. {detected}, {len} bytes.]")
            }
            MediaModality::TextDocument => {
                format!("[Document — no captioner wired. {detected}, {len} bytes.]")
            }
        };
        Ok(CaptionResult {
            caption,
            embedding: None,
            width_px: None,
            height_px: None,
            duration_ms: None,
        })
    }
}

/// Detects the MIME type from the declared value or the leading magic bytes.
fn detect_mime(bytes: &[u8], declared: Option<&str>) -> String {
    if let Some(d) = declared {
        if !d.trim().is_empty() {
            return d.to_string();
        }
    }
    if bytes.len() >= 4 {
        if bytes[0] == 0xff && bytes[1] == 0xd8 {
            return "image/jpeg".to_string();
        }
        if bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4e && bytes[3] == 0x47 {
            return "image/png".to_string();
        }
        if bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 {
            return "image/gif".to_string();
        }
        if bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46 {
            return "audio/wav".to_string();
        }
        if bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 && bytes[3] == 0x46 {
            return "application/pdf".to_string();
        }
    }
    "application/octet-stream".to_string()
}

// ─────────────────────────────────────────────────────────────────────────────
// IMultimodalMemoryStore + InMemoryMultimodalMemoryStore
// ─────────────────────────────────────────────────────────────────────────────

/// Persistent store of compressed multimodal memories.
pub trait IMultimodalMemoryStore: Send + Sync {
    /// Adds an entry. Duplicate SHA-256 hits should be handled via `get_by_hash`.
    fn add(&self, entry: MultimodalMemoryEntry) -> Result<(), BrainError>;
    /// Returns the entry with the given hash, or `None` if unknown.
    fn get_by_hash(&self, source_sha256: &str) -> Result<Option<MultimodalMemoryEntry>, BrainError>;
    /// Increments reference_count for the matching entry. No-op when unknown.
    fn reinforce(&self, source_sha256: &str) -> Result<(), BrainError>;
    /// Top-`top_k` entries most similar (cosine) to `query_embedding`. When the
    /// query is `None`, falls back to most-recent.
    fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<MultimodalMemoryEntry>, BrainError>;
    /// Returns the most recent `count` entries.
    fn get_recent(&self, count: usize) -> Result<Vec<MultimodalMemoryEntry>, BrainError>;
    /// Removes entries older than `cutoff`. Returns count removed.
    fn prune_older_than(&self, cutoff: &DateTime<Utc>) -> Result<usize, BrainError>;
    /// Total entries currently stored.
    fn count(&self) -> Result<usize, BrainError>;
}

/// In-memory [`IMultimodalMemoryStore`]. Keyed by SHA-256 (case-insensitive,
/// matching the C# `OrdinalIgnoreCase` dictionary). Interior mutability lets it
/// be shared behind an [`std::sync::Arc`].
#[derive(Debug, Default)]
pub struct InMemoryMultimodalMemoryStore {
    by_hash: Mutex<HashMap<String, MultimodalMemoryEntry>>,
}

impl InMemoryMultimodalMemoryStore {
    /// Creates an empty store.
    pub fn new() -> Self {
        Self::default()
    }
}

impl IMultimodalMemoryStore for InMemoryMultimodalMemoryStore {
    fn add(&self, entry: MultimodalMemoryEntry) -> Result<(), BrainError> {
        if entry.source_sha256.trim().is_empty() {
            return Err(BrainError::new("SourceSha256 is required."));
        }
        let key = key_of(&entry.source_sha256);
        self.by_hash.lock().unwrap().insert(key, entry);
        Ok(())
    }

    fn get_by_hash(&self, source_sha256: &str) -> Result<Option<MultimodalMemoryEntry>, BrainError> {
        Ok(self
            .by_hash
            .lock()
            .unwrap()
            .get(&key_of(source_sha256))
            .cloned())
    }

    fn reinforce(&self, source_sha256: &str) -> Result<(), BrainError> {
        let mut map = self.by_hash.lock().unwrap();
        if let Some(e) = map.get_mut(&key_of(source_sha256)) {
            e.reference_count += 1;
        }
        Ok(())
    }

    fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<MultimodalMemoryEntry>, BrainError> {
        let map = self.by_hash.lock().unwrap();
        let snapshot: Vec<MultimodalMemoryEntry> = map.values().cloned().collect();
        drop(map);

        match query_embedding {
            None => {
                let mut recent = snapshot;
                recent.sort_by(|a, b| b.recorded_at_utc.cmp(&a.recorded_at_utc));
                recent.truncate(top_k);
                Ok(recent)
            }
            Some(q) => {
                let mut scored: Vec<(MultimodalMemoryEntry, f32)> = snapshot
                    .into_iter()
                    .filter_map(|e| match &e.embedding {
                        Some(emb) if !emb.is_empty() => {
                            let score = cosine_score(q, emb);
                            Some((e, score))
                        }
                        _ => None,
                    })
                    .collect();
                scored.sort_by(|a, b| {
                    b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal)
                });
                scored.truncate(top_k);
                Ok(scored.into_iter().map(|(e, _)| e).collect())
            }
        }
    }

    fn get_recent(&self, count: usize) -> Result<Vec<MultimodalMemoryEntry>, BrainError> {
        let map = self.by_hash.lock().unwrap();
        let mut recent: Vec<MultimodalMemoryEntry> = map.values().cloned().collect();
        drop(map);
        recent.sort_by(|a, b| b.recorded_at_utc.cmp(&a.recorded_at_utc));
        recent.truncate(count);
        Ok(recent)
    }

    fn prune_older_than(&self, cutoff: &DateTime<Utc>) -> Result<usize, BrainError> {
        let mut map = self.by_hash.lock().unwrap();
        let before = map.len();
        map.retain(|_, e| e.recorded_at_utc >= *cutoff);
        Ok(before - map.len())
    }

    fn count(&self) -> Result<usize, BrainError> {
        Ok(self.by_hash.lock().unwrap().len())
    }
}

/// Lower-cases the SHA to reproduce the C# case-insensitive hash lookups.
fn key_of(sha: &str) -> String {
    sha.to_lowercase()
}

/// Cosine similarity — matches the C# store's internal `CosineSimilarity.Score`.
pub(crate) fn cosine_score(a: &[f32], b: &[f32]) -> f32 {
    if a.len() != b.len() {
        return 0.0;
    }
    let mut dot = 0.0f32;
    let mut mag_a = 0.0f32;
    let mut mag_b = 0.0f32;
    for i in 0..a.len() {
        dot += a[i] * b[i];
        mag_a += a[i] * a[i];
        mag_b += b[i] * b[i];
    }
    let denom = mag_a.sqrt() * mag_b.sqrt();
    if denom < f32::EPSILON {
        0.0
    } else {
        dot / denom
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// MultimodalMemoryIngester — CircleAI.Memory.Multimodal.MultimodalMemoryIngester
// ─────────────────────────────────────────────────────────────────────────────

/// Outcome of a [`MultimodalMemoryIngester::ingest`] call.
#[derive(Debug, Clone)]
pub struct IngestionResult {
    /// The stored (or reinforced) entry.
    pub entry: MultimodalMemoryEntry,
    /// True when the artefact matched an existing hash and was reinforced.
    pub was_deduplicated: bool,
}

/// Optional per-call inputs for [`MultimodalMemoryIngester::ingest`].
#[derive(Debug, Clone, Default)]
pub struct IngestOptions {
    /// Optional MIME type for the source.
    pub mime_type: Option<String>,
    /// Optional URI of the original (host-retained).
    pub source_uri: Option<String>,
    /// Optional caller-supplied tags.
    pub tags: Option<HashMap<String, String>>,
}

/// Ingests raw media bytes into compressed semantic memory.
///
///   1. Hashes the source (SHA-256, hex-lower).
///   2. Dedupes — if the hash is known, reinforces the existing entry and
///      returns it (no re-captioning, no duplicate storage).
///   3. Picks a captioner via `can_caption()`.
///   4. Asks the captioner for a [`CaptionResult`].
///   5. Persists a [`MultimodalMemoryEntry`] to the store.
///
/// Raw bytes are never persisted. The hash is the only durable handle the memory
/// layer keeps for the original artefact.
pub struct MultimodalMemoryIngester {
    captioners: Vec<Box<dyn IMultimodalCaptioner>>,
    store: Box<dyn IMultimodalMemoryStore>,
}

impl MultimodalMemoryIngester {
    /// Captioners are tried in order — the first one whose `can_caption()`
    /// returns true wins. The host typically registers richer captioners first
    /// and the heuristic fallback last. At least one captioner is required.
    pub fn new(
        captioners: Vec<Box<dyn IMultimodalCaptioner>>,
        store: Box<dyn IMultimodalMemoryStore>,
    ) -> Result<Self, BrainError> {
        if captioners.is_empty() {
            return Err(BrainError::new("At least one captioner is required."));
        }
        Ok(Self { captioners, store })
    }

    /// Ingests an artefact. When the SHA-256 matches an existing entry the stored
    /// record is reinforced rather than re-captioned, and the result's
    /// `was_deduplicated` is true.
    pub fn ingest(
        &self,
        modality: MediaModality,
        source_bytes: &[u8],
        options: IngestOptions,
    ) -> Result<IngestionResult, BrainError> {
        if source_bytes.is_empty() {
            return Err(BrainError::new("Source bytes are empty."));
        }

        let mime = options.mime_type.as_deref();

        let hash = compute_sha256(source_bytes);
        if self.store.get_by_hash(&hash)?.is_some() {
            // Reinforce first, then re-read so the returned entry reflects the
            // incremented reference_count. (The C#/TS reference mutates the stored
            // entry in place and returns the same reference; because our store
            // hands back a clone, we re-fetch to observe the bump.)
            self.store.reinforce(&hash)?;
            let existing = self
                .store
                .get_by_hash(&hash)?
                .expect("entry present immediately after a successful reinforce");
            return Ok(IngestionResult {
                entry: existing,
                was_deduplicated: true,
            });
        }

        let captioner = self.pick_captioner(modality, mime);
        let caption = captioner.caption(modality, source_bytes, mime)?;

        let entry = MultimodalMemoryEntry {
            id: Uuid::new_v4(),
            recorded_at_utc: Utc::now(),
            modality,
            caption: caption.caption,
            embedding: caption.embedding,
            source_sha256: hash,
            source_mime_type: options.mime_type.clone(),
            source_byte_count: source_bytes.len() as i64,
            source_uri: options.source_uri.clone(),
            width_px: caption.width_px,
            height_px: caption.height_px,
            duration_ms: caption.duration_ms,
            reference_count: 1,
            tags: options.tags.clone(),
        };

        self.store.add(entry.clone())?;
        Ok(IngestionResult {
            entry,
            was_deduplicated: false,
        })
    }

    fn pick_captioner(
        &self,
        modality: MediaModality,
        mime: Option<&str>,
    ) -> &dyn IMultimodalCaptioner {
        for c in &self.captioners {
            if c.can_caption(modality, mime) {
                return c.as_ref();
            }
        }
        // The last registered captioner should accept everything.
        self.captioners
            .last()
            .expect("at least one captioner (checked in new)")
            .as_ref()
    }
}

/// Computes the hex-lower SHA-256 of `bytes`.
pub fn compute_sha256(bytes: &[u8]) -> String {
    let digest = sha256(bytes);
    let mut hex = String::with_capacity(64);
    for b in digest {
        hex.push_str(&format!("{b:02x}"));
    }
    hex
}

// ─────────────────────────────────────────────────────────────────────────────
// Minimal SHA-256 (FIPS 180-4). Self-contained — no external crate. Matches
// System.Security.Cryptography.SHA256 / node:crypto byte-for-byte.
// ─────────────────────────────────────────────────────────────────────────────

const SHA256_K: [u32; 64] = [
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
    0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
    0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
    0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
    0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
];

fn sha256(data: &[u8]) -> [u8; 32] {
    let mut h: [u32; 8] = [
        0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab,
        0x5be0cd19,
    ];

    // Pre-processing (padding).
    let bit_len = (data.len() as u64).wrapping_mul(8);
    let mut msg = data.to_vec();
    msg.push(0x80);
    while msg.len() % 64 != 56 {
        msg.push(0);
    }
    msg.extend_from_slice(&bit_len.to_be_bytes());

    for chunk in msg.chunks_exact(64) {
        let mut w = [0u32; 64];
        for (i, word) in w.iter_mut().enumerate().take(16) {
            let j = i * 4;
            *word = u32::from_be_bytes([chunk[j], chunk[j + 1], chunk[j + 2], chunk[j + 3]]);
        }
        for i in 16..64 {
            let s0 = w[i - 15].rotate_right(7) ^ w[i - 15].rotate_right(18) ^ (w[i - 15] >> 3);
            let s1 = w[i - 2].rotate_right(17) ^ w[i - 2].rotate_right(19) ^ (w[i - 2] >> 10);
            w[i] = w[i - 16]
                .wrapping_add(s0)
                .wrapping_add(w[i - 7])
                .wrapping_add(s1);
        }

        let mut a = h[0];
        let mut b = h[1];
        let mut c = h[2];
        let mut d = h[3];
        let mut e = h[4];
        let mut f = h[5];
        let mut g = h[6];
        let mut hh = h[7];

        for i in 0..64 {
            let s1 = e.rotate_right(6) ^ e.rotate_right(11) ^ e.rotate_right(25);
            let ch = (e & f) ^ ((!e) & g);
            let temp1 = hh
                .wrapping_add(s1)
                .wrapping_add(ch)
                .wrapping_add(SHA256_K[i])
                .wrapping_add(w[i]);
            let s0 = a.rotate_right(2) ^ a.rotate_right(13) ^ a.rotate_right(22);
            let maj = (a & b) ^ (a & c) ^ (b & c);
            let temp2 = s0.wrapping_add(maj);

            hh = g;
            g = f;
            f = e;
            e = d.wrapping_add(temp1);
            d = c;
            c = b;
            b = a;
            a = temp1.wrapping_add(temp2);
        }

        h[0] = h[0].wrapping_add(a);
        h[1] = h[1].wrapping_add(b);
        h[2] = h[2].wrapping_add(c);
        h[3] = h[3].wrapping_add(d);
        h[4] = h[4].wrapping_add(e);
        h[5] = h[5].wrapping_add(f);
        h[6] = h[6].wrapping_add(g);
        h[7] = h[7].wrapping_add(hh);
    }

    let mut out = [0u8; 32];
    for (i, word) in h.iter().enumerate() {
        out[i * 4..i * 4 + 4].copy_from_slice(&word.to_be_bytes());
    }
    out
}
