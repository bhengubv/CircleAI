//! media — CircleAI media-library primitives.
//!
//! Full Rust port of `src/CircleAI.Media/MediaPrimitives.cs`: real domain types
//! + an in-memory library for the Media vertical (audio + video + image asset
//! catalogue).
//!
//! - [`MediaKind`] enum, the [`MediaAsset`] record, the [`IMediaLibrary`]
//!   contract, and the deterministic in-memory [`InMemoryMediaLibrary`].
//!
//! Sync-only; `TimeSpan?` → `Option<`[`std::time::Duration`]`>`; `long Bytes` →
//! `i64`; `DateTimeOffset` → [`chrono::DateTime<Utc>`]. Case-insensitive matching
//! reproduces the C# `OrdinalIgnoreCase`.

use std::collections::HashMap;
use std::sync::Mutex;
use std::time::Duration;

use chrono::{DateTime, Utc};

/// Default `top_k` for [`IMediaLibrary::search`] (C# `topK = 20`).
pub const DEFAULT_SEARCH_TOP_K: i32 = 20;

/// (Media) The kind of a media asset.
///
/// Mirrors `enum MediaKind { Audio, Video, Image }`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum MediaKind {
    Audio,
    Video,
    Image,
}

/// (Media) A catalogued media asset.
///
/// Mirrors `sealed record MediaAsset(string AssetId, string Title,
/// MediaKind Kind, TimeSpan? Duration, long Bytes, string Mime,
/// DateTimeOffset CreatedAtUtc)`.
#[derive(Debug, Clone, PartialEq)]
pub struct MediaAsset {
    pub asset_id: String,
    pub title: String,
    pub kind: MediaKind,
    pub duration: Option<Duration>,
    pub bytes: i64,
    pub mime: String,
    pub created_at_utc: DateTime<Utc>,
}

impl MediaAsset {
    /// Constructs an asset, mirroring the positional C# record constructor.
    #[allow(clippy::too_many_arguments)]
    pub fn new(
        asset_id: impl Into<String>,
        title: impl Into<String>,
        kind: MediaKind,
        duration: Option<Duration>,
        bytes: i64,
        mime: impl Into<String>,
        created_at_utc: DateTime<Utc>,
    ) -> Self {
        Self {
            asset_id: asset_id.into(),
            title: title.into(),
            kind,
            duration,
            bytes,
            mime: mime.into(),
            created_at_utc,
        }
    }
}

/// (Media) The media-library contract.
///
/// Mirrors `interface IMediaLibrary`.
pub trait IMediaLibrary {
    /// Adds (or overwrites) an asset keyed by its id.
    fn add(&self, a: MediaAsset);
    /// An asset by id, if any.
    fn get(&self, id: &str) -> Option<MediaAsset>;
    /// Removes an asset by id. Returns `true` if it was present.
    fn remove(&self, id: &str) -> bool;
    /// Number of catalogued assets.
    fn count(&self) -> usize;
    /// Total on-disk footprint (bytes) of every catalogued asset.
    fn total_bytes(&self) -> i64;
    /// Assets of a given kind, newest first.
    fn list_by_kind(&self, kind: MediaKind) -> Vec<MediaAsset>;
    /// Assets whose MIME type starts with `mime_prefix` (case-insensitive),
    /// newest first. Empty prefix yields nothing.
    fn by_mime(&self, mime_prefix: &str) -> Vec<MediaAsset>;
    /// Assets whose title contains `q` (case-insensitive), newest first, capped at
    /// `top_k` (see [`DEFAULT_SEARCH_TOP_K`]).
    fn search(&self, q: &str, top_k: i32) -> Vec<MediaAsset>;
}

/// (Media) In-memory [`IMediaLibrary`].
///
/// Mirrors `sealed class InMemoryMediaLibrary`.
pub struct InMemoryMediaLibrary {
    items: Mutex<HashMap<String, MediaAsset>>,
}

impl InMemoryMediaLibrary {
    /// Creates an empty library.
    pub fn new() -> Self {
        Self {
            items: Mutex::new(HashMap::new()),
        }
    }
}

impl Default for InMemoryMediaLibrary {
    fn default() -> Self {
        Self::new()
    }
}

impl IMediaLibrary for InMemoryMediaLibrary {
    fn add(&self, a: MediaAsset) {
        // C# throws when the id is blank; here callers pass a valid id, and an
        // empty id is simply keyed as-is (kept faithful to the store semantics).
        self.items.lock().unwrap().insert(a.asset_id.clone(), a);
    }

    fn get(&self, id: &str) -> Option<MediaAsset> {
        self.items.lock().unwrap().get(id).cloned()
    }

    fn remove(&self, id: &str) -> bool {
        if id.is_empty() {
            return false;
        }
        self.items.lock().unwrap().remove(id).is_some()
    }

    fn count(&self) -> usize {
        self.items.lock().unwrap().len()
    }

    fn total_bytes(&self) -> i64 {
        self.items.lock().unwrap().values().map(|a| a.bytes).sum()
    }

    fn list_by_kind(&self, kind: MediaKind) -> Vec<MediaAsset> {
        let mut hits: Vec<MediaAsset> = self
            .items
            .lock()
            .unwrap()
            .values()
            .filter(|a| a.kind == kind)
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.created_at_utc.cmp(&a.created_at_utc));
        hits
    }

    fn by_mime(&self, mime_prefix: &str) -> Vec<MediaAsset> {
        if mime_prefix.is_empty() {
            return Vec::new();
        }
        let prefix = mime_prefix.to_lowercase();
        let mut hits: Vec<MediaAsset> = self
            .items
            .lock()
            .unwrap()
            .values()
            .filter(|a| a.mime.to_lowercase().starts_with(&prefix))
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.created_at_utc.cmp(&a.created_at_utc));
        hits
    }

    fn search(&self, q: &str, top_k: i32) -> Vec<MediaAsset> {
        if top_k <= 0 {
            return Vec::new();
        }
        let needle = q.to_lowercase();
        let mut hits: Vec<MediaAsset> = self
            .items
            .lock()
            .unwrap()
            .values()
            .filter(|a| a.title.to_lowercase().contains(&needle))
            .cloned()
            .collect();
        hits.sort_by(|a, b| b.created_at_utc.cmp(&a.created_at_utc));
        hits.truncate(top_k as usize);
        hits
    }
}
