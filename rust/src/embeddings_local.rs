//! embeddings_local — CircleAI.Embeddings.Local (Rust port).
//!
//! Port of:
//!   - `CircleAI.Embeddings.Local.IEmbeddingEncoder` (+ `EmbeddingDocument`,
//!     `EmbeddingSearchHit`)
//!   - `CircleAI.Embeddings.Local.ICircleEmbeddingStore`
//!   - `CircleAI.Embeddings.Local.IEmbeddingIndex` (+ `EmbeddingIndexHit`)
//!   - `CircleAI.Embeddings.Local.InMemoryEmbeddingStore`
//!
//! `InMemoryEmbeddingStore` brute-force cosine-searches over TurboQuant-compressed
//! vectors (reusing [`crate::memory::compression::TurboQuantCodec`]). Its on-disk
//! format is byte-compatible with the C# `BinaryWriter` layout:
//!   magic `0x4C455143` ("CELQ"), `u16` version 1, `u16` bits/dim, `i32` dim,
//!   `i32` count, then per entry: 7-bit-length-prefixed UTF-8 id + text, `i32`
//!   metadata count, that many (key, value) 7-bit-prefixed strings, `f32` norm,
//!   `i32` packed length, raw packed bytes.

use std::collections::HashMap;
use std::fs;
use std::path::Path;

use crate::memory::compression::{TurboQuantCodec, TurboQuantPayload};

/// One document in the store. `id` uniquely identifies it for delete / update.
/// Mirrors `EmbeddingDocument`.
#[derive(Debug, Clone, PartialEq)]
pub struct EmbeddingDocument {
    pub id: String,
    pub text: String,
    pub metadata: Option<HashMap<String, String>>,
}

impl EmbeddingDocument {
    pub fn new(id: impl Into<String>, text: impl Into<String>) -> Self {
        Self {
            id: id.into(),
            text: text.into(),
            metadata: None,
        }
    }

    pub fn with_metadata(
        id: impl Into<String>,
        text: impl Into<String>,
        metadata: HashMap<String, String>,
    ) -> Self {
        Self {
            id: id.into(),
            text: text.into(),
            metadata: Some(metadata),
        }
    }
}

/// One hit from a store search. Higher `score` = closer (cosine similarity).
/// Mirrors `EmbeddingSearchHit`.
#[derive(Debug, Clone, PartialEq)]
pub struct EmbeddingSearchHit {
    pub document: EmbeddingDocument,
    pub score: f32,
}

/// One hit from an index search. `internal_id` is the insertion-order id.
/// Mirrors `EmbeddingIndexHit`.
#[derive(Debug, Clone, Copy, PartialEq)]
pub struct EmbeddingIndexHit {
    pub internal_id: i64,
    pub score: f32,
}

/// Errors surfaced by the embedding store / index.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum EmbeddingStoreError {
    /// `ArgumentException` / `ArgumentOutOfRangeException`.
    Argument(String),
    /// `ObjectDisposedException`.
    Disposed(String),
    /// `InvalidDataException` — corrupt / mismatched file.
    InvalidData(String),
    /// `FileNotFoundException`.
    NotFound(String),
    /// I/O or codec failure.
    Io(String),
}

impl std::fmt::Display for EmbeddingStoreError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            EmbeddingStoreError::Argument(m)
            | EmbeddingStoreError::Disposed(m)
            | EmbeddingStoreError::InvalidData(m)
            | EmbeddingStoreError::NotFound(m)
            | EmbeddingStoreError::Io(m) => f.write_str(m),
        }
    }
}

impl std::error::Error for EmbeddingStoreError {}

/// Translates text into a dense vector. Mirrors `IEmbeddingEncoder` (sync per
/// crate convention).
pub trait IEmbeddingEncoder {
    /// Vector dimension this encoder produces.
    fn dimension(&self) -> usize;

    /// Encode one text into a dense vector.
    fn encode(&self, text: &str) -> Result<Vec<f32>, EmbeddingStoreError>;
}

/// On-device embedding store with a built-in RAG primitive. Mirrors
/// `ICircleEmbeddingStore` (sync per crate convention; `IAsyncDisposable` maps to
/// `Drop`).
pub trait ICircleEmbeddingStore {
    /// Vector dimension this store was created with.
    fn dimension(&self) -> usize;

    /// How many documents are currently in the store.
    fn count(&self) -> usize;

    /// Add (or replace) one document; the encoder produces the vector.
    fn add(&mut self, document: EmbeddingDocument) -> Result<(), EmbeddingStoreError>;

    /// Add a document with a caller-supplied vector.
    fn add_with_vector(
        &mut self,
        document: EmbeddingDocument,
        vector: &[f32],
    ) -> Result<(), EmbeddingStoreError>;

    /// Remove a document by id. Returns `true` if a document was removed.
    fn remove(&mut self, id: &str) -> Result<bool, EmbeddingStoreError>;

    /// Search by text; returns the `top_k` closest documents by cosine similarity.
    fn search(
        &self,
        query_text: &str,
        top_k: usize,
    ) -> Result<Vec<EmbeddingSearchHit>, EmbeddingStoreError>;

    /// Search by a pre-computed query vector.
    fn search_vector(
        &self,
        query_vector: &[f32],
        top_k: usize,
    ) -> Result<Vec<EmbeddingSearchHit>, EmbeddingStoreError>;

    /// Persist the store to `path` (atomic via write-tmp-then-rename).
    fn save(&self, path: &Path) -> Result<(), EmbeddingStoreError>;

    /// Load a previously-saved store, replacing all in-memory state.
    fn load(&mut self, path: &Path) -> Result<(), EmbeddingStoreError>;
}

/// Vector index contract. Mirrors `IEmbeddingIndex` (sync per crate convention).
pub trait IEmbeddingIndex {
    /// Vector dimensionality. Locked at construction.
    fn dimension(&self) -> usize;

    /// How many vectors are currently in the index.
    fn count(&self) -> i64;

    /// Append one vector; returns the internal id assigned.
    fn add(&mut self, vector: &[f32]) -> Result<i64, EmbeddingStoreError>;

    /// Search for the top-`top_k` nearest neighbours.
    fn search(
        &self,
        query_vector: &[f32],
        top_k: usize,
    ) -> Result<Vec<EmbeddingIndexHit>, EmbeddingStoreError>;

    /// Persist the index to `path`.
    fn save(&self, path: &Path) -> Result<(), EmbeddingStoreError>;

    /// Reload from `path`, replacing the in-memory state.
    fn load(&mut self, path: &Path) -> Result<(), EmbeddingStoreError>;
}

// ─────────────────────────────────────────────────────────────────────────────
// InMemoryEmbeddingStore
// ─────────────────────────────────────────────────────────────────────────────

const FILE_MAGIC: i32 = 0x4C45_5143; // "CELQ" little-endian
const FILE_VERSION: u16 = 1;
const DEFAULT_BITS_PER_DIM: u32 = 4;

#[derive(Clone)]
struct Entry {
    document: EmbeddingDocument,
    payload: TurboQuantPayload,
}

/// Default [`ICircleEmbeddingStore`]: brute-force search over TurboQuant-
/// compressed vectors held in memory. Mirrors `InMemoryEmbeddingStore`.
pub struct InMemoryEmbeddingStore<E: IEmbeddingEncoder> {
    encoder: E,
    bits_per_dim: u32,
    entries: HashMap<String, Entry>,
}

impl<E: IEmbeddingEncoder> InMemoryEmbeddingStore<E> {
    /// Construct with a caller-supplied encoder and the default 4 bits/dim.
    pub fn new(encoder: E) -> Result<Self, EmbeddingStoreError> {
        Self::with_bits(encoder, DEFAULT_BITS_PER_DIM)
    }

    /// Construct with a caller-supplied encoder and explicit bits/dim (1..=8).
    pub fn with_bits(encoder: E, bits_per_dim: u32) -> Result<Self, EmbeddingStoreError> {
        if !(1..=8).contains(&bits_per_dim) {
            return Err(EmbeddingStoreError::Argument("Valid range: 1–8.".into()));
        }
        Ok(Self {
            encoder,
            bits_per_dim,
            entries: HashMap::new(),
        })
    }

    fn norm_safe(v: &[f32]) -> f32 {
        let mut sum = 0.0f64;
        for &x in v {
            sum += x as f64 * x as f64;
        }
        sum.sqrt() as f32
    }

    fn search_core(
        &self,
        query_vector: &[f32],
        top_k: usize,
    ) -> Result<Vec<EmbeddingSearchHit>, EmbeddingStoreError> {
        if query_vector.len() != self.dimension() {
            return Err(EmbeddingStoreError::Argument(format!(
                "Vector length {} != store dimension {}.",
                query_vector.len(),
                self.dimension()
            )));
        }
        if top_k == 0 {
            return Err(EmbeddingStoreError::Argument("topK".into()));
        }

        let q_norm = Self::norm_safe(query_vector);
        let mut q = query_vector.to_vec();
        if q_norm > 0.0 {
            for x in q.iter_mut() {
                *x /= q_norm;
            }
        }

        // Brute-force cosine. Decode each entry on demand.
        let mut scored: Vec<(f32, String)> = Vec::new();
        for (id, entry) in &self.entries {
            let decoded = TurboQuantCodec::decode(&entry.payload, self.dimension(), self.bits_per_dim)
                .map_err(|e| EmbeddingStoreError::Io(e.to_string()))?;
            let entry_norm = Self::norm_safe(&decoded);
            if entry_norm <= 0.0 {
                continue;
            }
            let mut dot = 0.0f32;
            for i in 0..self.dimension() {
                dot += q[i] * (decoded[i] / entry_norm);
            }
            scored.push((dot, id.clone()));
        }

        // Order by descending score, tie-break by ordinal id (matches the C#
        // SortedSet comparer semantics for the final ordering).
        scored.sort_by(|a, b| {
            b.0.partial_cmp(&a.0)
                .unwrap_or(std::cmp::Ordering::Equal)
                .then_with(|| a.1.cmp(&b.1))
        });
        scored.truncate(top_k);

        Ok(scored
            .into_iter()
            .map(|(score, id)| EmbeddingSearchHit {
                document: self.entries[&id].document.clone(),
                score,
            })
            .collect())
    }
}

impl<E: IEmbeddingEncoder> ICircleEmbeddingStore for InMemoryEmbeddingStore<E> {
    fn dimension(&self) -> usize {
        self.encoder.dimension()
    }

    fn count(&self) -> usize {
        self.entries.len()
    }

    fn add(&mut self, document: EmbeddingDocument) -> Result<(), EmbeddingStoreError> {
        let vector = self.encoder.encode(&document.text)?;
        self.add_with_vector(document, &vector)
    }

    fn add_with_vector(
        &mut self,
        document: EmbeddingDocument,
        vector: &[f32],
    ) -> Result<(), EmbeddingStoreError> {
        if vector.len() != self.dimension() {
            return Err(EmbeddingStoreError::Argument(format!(
                "Vector length {} != store dimension {}.",
                vector.len(),
                self.dimension()
            )));
        }
        let payload = TurboQuantCodec::encode(vector, self.bits_per_dim)
            .map_err(|e| EmbeddingStoreError::Io(e.to_string()))?;
        self.entries
            .insert(document.id.clone(), Entry { document, payload });
        Ok(())
    }

    fn remove(&mut self, id: &str) -> Result<bool, EmbeddingStoreError> {
        if id.trim().is_empty() {
            return Err(EmbeddingStoreError::Argument("id".into()));
        }
        Ok(self.entries.remove(id).is_some())
    }

    fn search(
        &self,
        query_text: &str,
        top_k: usize,
    ) -> Result<Vec<EmbeddingSearchHit>, EmbeddingStoreError> {
        if query_text.is_empty() {
            return Err(EmbeddingStoreError::Argument("queryText".into()));
        }
        let vector = self.encoder.encode(query_text)?;
        self.search_vector(&vector, top_k)
    }

    fn search_vector(
        &self,
        query_vector: &[f32],
        top_k: usize,
    ) -> Result<Vec<EmbeddingSearchHit>, EmbeddingStoreError> {
        self.search_core(query_vector, top_k)
    }

    fn save(&self, path: &Path) -> Result<(), EmbeddingStoreError> {
        if path.as_os_str().is_empty() {
            return Err(EmbeddingStoreError::Argument("path".into()));
        }
        if let Some(dir) = path.parent() {
            if !dir.as_os_str().is_empty() {
                fs::create_dir_all(dir).map_err(|e| EmbeddingStoreError::Io(e.to_string()))?;
            }
        }

        let mut w = BinWriter::new();
        w.write_i32(FILE_MAGIC);
        w.write_u16(FILE_VERSION);
        w.write_u16(self.bits_per_dim as u16);
        w.write_i32(self.dimension() as i32);
        w.write_i32(self.entries.len() as i32);
        for (id, entry) in &self.entries {
            w.write_string(id);
            w.write_string(&entry.document.text);
            let meta_count = entry.document.metadata.as_ref().map_or(0, |m| m.len());
            w.write_i32(meta_count as i32);
            if let Some(meta) = &entry.document.metadata {
                for (k, v) in meta {
                    w.write_string(k);
                    w.write_string(v);
                }
            }
            w.write_f32(entry.payload.norm);
            w.write_i32(entry.payload.packed_indices.len() as i32);
            w.write_bytes(&entry.payload.packed_indices);
        }

        let tmp = {
            let mut s = path.as_os_str().to_os_string();
            s.push(".tmp");
            std::path::PathBuf::from(s)
        };
        fs::write(&tmp, w.into_bytes()).map_err(|e| EmbeddingStoreError::Io(e.to_string()))?;
        if path.exists() {
            fs::remove_file(path).map_err(|e| EmbeddingStoreError::Io(e.to_string()))?;
        }
        fs::rename(&tmp, path).map_err(|e| EmbeddingStoreError::Io(e.to_string()))?;
        Ok(())
    }

    fn load(&mut self, path: &Path) -> Result<(), EmbeddingStoreError> {
        if path.as_os_str().is_empty() {
            return Err(EmbeddingStoreError::Argument("path".into()));
        }
        if !path.exists() {
            return Err(EmbeddingStoreError::NotFound(
                "Embedding store file not found.".into(),
            ));
        }

        let bytes = fs::read(path).map_err(|e| EmbeddingStoreError::Io(e.to_string()))?;
        let mut r = BinReader::new(&bytes);

        let magic = r.read_i32()?;
        if magic != FILE_MAGIC {
            return Err(EmbeddingStoreError::InvalidData(
                "Not a CircleAI embedding store file.".into(),
            ));
        }
        let version = r.read_u16()?;
        if version != FILE_VERSION {
            return Err(EmbeddingStoreError::InvalidData(format!(
                "Unsupported file version {version}."
            )));
        }
        let file_bits = r.read_u16()?;
        if file_bits as u32 != self.bits_per_dim {
            return Err(EmbeddingStoreError::InvalidData(format!(
                "Bits-per-dim mismatch: store={}, file={}.",
                self.bits_per_dim, file_bits
            )));
        }
        let file_dim = r.read_i32()?;
        if file_dim != self.dimension() as i32 {
            return Err(EmbeddingStoreError::InvalidData(format!(
                "Dimension mismatch: store={}, file={}.",
                self.dimension(),
                file_dim
            )));
        }

        let count = r.read_i32()?;
        self.entries.clear();
        for _ in 0..count {
            let id = r.read_string()?;
            let text = r.read_string()?;
            let meta_count = r.read_i32()?;
            let metadata = if meta_count > 0 {
                let mut m = HashMap::with_capacity(meta_count as usize);
                for _ in 0..meta_count {
                    let k = r.read_string()?;
                    let v = r.read_string()?;
                    m.insert(k, v);
                }
                Some(m)
            } else {
                None
            };
            let norm = r.read_f32()?;
            let packed_len = r.read_i32()?;
            let packed = r.read_bytes(packed_len as usize)?;
            self.entries.insert(
                id.clone(),
                Entry {
                    document: EmbeddingDocument {
                        id,
                        text,
                        metadata,
                    },
                    payload: TurboQuantPayload {
                        norm,
                        packed_indices: packed,
                    },
                },
            );
        }
        Ok(())
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BinaryWriter / BinaryReader — byte-compatible with .NET BinaryWriter/Reader
// (7-bit-encoded string length prefix, little-endian scalars).
// ─────────────────────────────────────────────────────────────────────────────

struct BinWriter {
    buf: Vec<u8>,
}

impl BinWriter {
    fn new() -> Self {
        Self { buf: Vec::new() }
    }
    fn into_bytes(self) -> Vec<u8> {
        self.buf
    }
    fn write_i32(&mut self, v: i32) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }
    fn write_u16(&mut self, v: u16) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }
    fn write_f32(&mut self, v: f32) {
        self.buf.extend_from_slice(&v.to_le_bytes());
    }
    fn write_bytes(&mut self, b: &[u8]) {
        self.buf.extend_from_slice(b);
    }
    /// .NET `BinaryWriter.Write(string)`: 7-bit-encoded UTF-8 byte length, then
    /// the UTF-8 bytes.
    fn write_string(&mut self, s: &str) {
        let bytes = s.as_bytes();
        self.write_7bit_len(bytes.len() as u32);
        self.buf.extend_from_slice(bytes);
    }
    fn write_7bit_len(&mut self, mut value: u32) {
        while value >= 0x80 {
            self.buf.push((value as u8) | 0x80);
            value >>= 7;
        }
        self.buf.push(value as u8);
    }
}

struct BinReader<'a> {
    buf: &'a [u8],
    pos: usize,
}

impl<'a> BinReader<'a> {
    fn new(buf: &'a [u8]) -> Self {
        Self { buf, pos: 0 }
    }
    fn take(&mut self, n: usize) -> Result<&'a [u8], EmbeddingStoreError> {
        if self.pos + n > self.buf.len() {
            return Err(EmbeddingStoreError::InvalidData(
                "Unexpected end of embedding store file.".into(),
            ));
        }
        let s = &self.buf[self.pos..self.pos + n];
        self.pos += n;
        Ok(s)
    }
    fn read_i32(&mut self) -> Result<i32, EmbeddingStoreError> {
        let b = self.take(4)?;
        Ok(i32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    }
    fn read_u16(&mut self) -> Result<u16, EmbeddingStoreError> {
        let b = self.take(2)?;
        Ok(u16::from_le_bytes([b[0], b[1]]))
    }
    fn read_f32(&mut self) -> Result<f32, EmbeddingStoreError> {
        let b = self.take(4)?;
        Ok(f32::from_le_bytes([b[0], b[1], b[2], b[3]]))
    }
    fn read_bytes(&mut self, n: usize) -> Result<Vec<u8>, EmbeddingStoreError> {
        Ok(self.take(n)?.to_vec())
    }
    fn read_7bit_len(&mut self) -> Result<u32, EmbeddingStoreError> {
        let mut result: u32 = 0;
        let mut shift = 0;
        loop {
            if shift >= 35 {
                return Err(EmbeddingStoreError::InvalidData(
                    "Malformed 7-bit length prefix.".into(),
                ));
            }
            let byte = self.take(1)?[0];
            result |= ((byte & 0x7f) as u32) << shift;
            if byte & 0x80 == 0 {
                break;
            }
            shift += 7;
        }
        Ok(result)
    }
    fn read_string(&mut self) -> Result<String, EmbeddingStoreError> {
        let len = self.read_7bit_len()? as usize;
        let bytes = self.take(len)?;
        String::from_utf8(bytes.to_vec())
            .map_err(|_| EmbeddingStoreError::InvalidData("Invalid UTF-8 in string.".into()))
    }
}
