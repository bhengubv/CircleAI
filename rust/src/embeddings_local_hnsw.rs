//! embeddings_local_hnsw — CircleAI.Embeddings.Local.HnswEmbeddingStore (Rust port).
//!
//! `ICircleEmbeddingStore` backed by a fast vector index (the C# reference wraps
//! the TurboVec SIMD-blocked quantised search bridge). The index is the search
//! primitive; this store layers documents + metadata + a `.docs` sidecar +
//! save/load on top. Mirrors `HnswEmbeddingStore`.
//!
//! The concrete SIMD index is injected as the [`IEmbeddingIndex`] seam (defined
//! in [`crate::embeddings_local`]) so the portable core stays free of any native
//! bridge; tests inject a brute-force fake.
//!
//! On-disk format (byte-compatible with the C# `.docs` sidecar, `BinaryWriter`
//! layout): magic `0x53434847` ("HGCS"), `u16` version 1, `i32` dim, `i32`
//! count, then per doc: 7-bit-length-prefixed UTF-8 id + text, `bool` live flag
//! (1 byte), `i32` metadata count, that many (key, value) 7-bit-prefixed
//! strings. The index's own slot data is persisted separately by the injected
//! [`IEmbeddingIndex::save`] at `path` itself.

use std::collections::HashMap;
use std::fs;
use std::path::{Path, PathBuf};

use crate::embeddings_local::{
    EmbeddingDocument, EmbeddingSearchHit, EmbeddingStoreError, IEmbeddingEncoder, IEmbeddingIndex,
};

const DOCS_MAGIC: i32 = 0x5343_4847; // "HGCS" — Hnsw Generic Circle Store
const DOCS_VERSION: u16 = 1;

/// Embedding store backed by an [`IEmbeddingIndex`] (e.g. a TurboVec SIMD index).
/// Same public contract as `InMemoryEmbeddingStore`; the search path is
/// index-accelerated instead of brute-force. Mirrors `HnswEmbeddingStore`.
///
/// `v1` semantics match the C# reference: add-only (call [`remove`](Self::remove)
/// then re-add to replace); `remove` marks the slot dead in the id-lookup so
/// subsequent searches skip it (index compaction is a follow-up).
pub struct HnswEmbeddingStore<E: IEmbeddingEncoder, I: IEmbeddingIndex> {
    encoder: E,
    index: I,
    /// Ordinal internal-id -> document. Index aligns with index slot ids.
    by_id: Vec<EmbeddingDocument>,
    /// External-document-id -> internal-id (live docs only). For O(1) remove.
    id_lookup: HashMap<String, i64>,
}

impl<E: IEmbeddingEncoder, I: IEmbeddingIndex> HnswEmbeddingStore<E, I> {
    /// Constructs the store over an encoder and a pre-built index. The encoder's
    /// dimension must be `> 0`, a multiple of 8 (SIMD alignment), and must match
    /// the index dimension.
    pub fn new(encoder: E, index: I) -> Result<Self, EmbeddingStoreError> {
        let dim = encoder.dimension();
        if dim == 0 || dim % 8 != 0 {
            return Err(EmbeddingStoreError::Argument(format!(
                "Encoder dimension {dim} must be > 0 and a multiple of 8 for turbovec."
            )));
        }
        if index.dimension() != dim {
            return Err(EmbeddingStoreError::Argument(format!(
                "Index dimension {} != encoder dimension {dim}.",
                index.dimension()
            )));
        }
        Ok(Self {
            encoder,
            index,
            by_id: Vec::new(),
            id_lookup: HashMap::new(),
        })
    }

    /// Vector dimension this store was created with.
    pub fn dimension(&self) -> usize {
        self.encoder.dimension()
    }

    /// How many document slots exist (including dead slots).
    pub fn count(&self) -> usize {
        self.by_id.len()
    }

    /// How many documents are currently live (searchable).
    pub fn live_count(&self) -> usize {
        self.id_lookup.len()
    }

    /// Adds a document; the encoder produces the vector. Mirrors the text-taking
    /// `AddAsync`.
    pub fn add(&mut self, document: EmbeddingDocument) -> Result<(), EmbeddingStoreError> {
        let vector = self.encoder.encode(&document.text)?;
        self.add_with_vector(document, &vector)
    }

    /// Adds a document with a caller-supplied vector. Fails if the id already
    /// exists (add-only contract). Mirrors the vector-taking `AddAsync`.
    pub fn add_with_vector(
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
        if self.id_lookup.contains_key(&document.id) {
            return Err(EmbeddingStoreError::Argument(format!(
                "Document id '{}' already exists. Call remove first.",
                document.id
            )));
        }
        let internal_id = self.index.add(vector)?;
        self.id_lookup.insert(document.id.clone(), internal_id);
        self.by_id.push(document);
        Ok(())
    }

    /// Removes a document by id (marks the slot dead). Returns `true` if a live
    /// document was removed. Mirrors `RemoveAsync`.
    pub fn remove(&mut self, id: &str) -> Result<bool, EmbeddingStoreError> {
        if id.trim().is_empty() {
            return Err(EmbeddingStoreError::Argument("id".into()));
        }
        Ok(self.id_lookup.remove(id).is_some())
    }

    /// Searches by text; returns the `top_k` closest live documents. Mirrors the
    /// text-taking `SearchAsync`.
    pub fn search(
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

    /// Searches by a pre-computed query vector. Over-fetches from the index to
    /// compensate for dead slots, then filters. Mirrors the vector-taking
    /// `SearchAsync`.
    pub fn search_vector(
        &self,
        query_vector: &[f32],
        top_k: usize,
    ) -> Result<Vec<EmbeddingSearchHit>, EmbeddingStoreError> {
        if query_vector.len() != self.dimension() {
            return Err(EmbeddingStoreError::Argument(format!(
                "Query length {} != store dimension {}.",
                query_vector.len(),
                self.dimension()
            )));
        }
        if top_k == 0 {
            return Err(EmbeddingStoreError::Argument("topK".into()));
        }

        // Over-fetch to compensate for removed slots; cap to current count.
        let index_count = self.index.count().max(0) as usize;
        let over_fetch = index_count.min((top_k * 2).max(top_k + 10));
        if over_fetch == 0 {
            return Ok(Vec::new());
        }

        let raw_hits = self.index.search(query_vector, over_fetch)?;
        if raw_hits.is_empty() {
            return Ok(Vec::new());
        }

        let mut results: Vec<EmbeddingSearchHit> = Vec::with_capacity(top_k);
        for hit in raw_hits {
            if hit.internal_id < 0 || hit.internal_id as usize >= self.by_id.len() {
                continue;
            }
            let doc = &self.by_id[hit.internal_id as usize];
            if !self.id_lookup.contains_key(&doc.id) {
                continue; // removed
            }
            results.push(EmbeddingSearchHit {
                document: doc.clone(),
                score: hit.score,
            });
            if results.len() == top_k {
                break;
            }
        }
        Ok(results)
    }

    /// Persists the store: the index slot data via [`IEmbeddingIndex::save`] to
    /// `path`, plus the `.docs` sidecar (atomic write-tmp-then-rename). Mirrors
    /// `SaveAsync`.
    pub fn save(&self, path: &Path) -> Result<(), EmbeddingStoreError> {
        if path.as_os_str().is_empty() {
            return Err(EmbeddingStoreError::Argument("path".into()));
        }
        if let Some(dir) = path.parent() {
            if !dir.as_os_str().is_empty() {
                fs::create_dir_all(dir).map_err(|e| EmbeddingStoreError::Io(e.to_string()))?;
            }
        }

        // Persist index slot data through the seam.
        self.index.save(path)?;

        // Persist the doc sidecar.
        let mut w = BinWriter::new();
        w.write_i32(DOCS_MAGIC);
        w.write_u16(DOCS_VERSION);
        w.write_i32(self.dimension() as i32);
        w.write_i32(self.by_id.len() as i32);
        for doc in &self.by_id {
            w.write_string(&doc.id);
            w.write_string(&doc.text);
            w.write_bool(self.id_lookup.contains_key(&doc.id)); // live flag
            let meta_count = doc.metadata.as_ref().map_or(0, |m| m.len());
            w.write_i32(meta_count as i32);
            if let Some(meta) = &doc.metadata {
                for (k, v) in meta {
                    w.write_string(k);
                    w.write_string(v);
                }
            }
        }

        let docs_path = with_docs_suffix(path);
        let tmp = {
            let mut s = docs_path.clone().into_os_string();
            s.push(".tmp");
            PathBuf::from(s)
        };
        fs::write(&tmp, w.into_bytes()).map_err(|e| EmbeddingStoreError::Io(e.to_string()))?;
        if docs_path.exists() {
            fs::remove_file(&docs_path).map_err(|e| EmbeddingStoreError::Io(e.to_string()))?;
        }
        fs::rename(&tmp, &docs_path).map_err(|e| EmbeddingStoreError::Io(e.to_string()))?;
        Ok(())
    }

    /// Reloads the store from `path` (index slot data + `.docs` sidecar),
    /// replacing all in-memory state. Mirrors `LoadAsync`.
    pub fn load(&mut self, path: &Path) -> Result<(), EmbeddingStoreError> {
        if path.as_os_str().is_empty() {
            return Err(EmbeddingStoreError::Argument("path".into()));
        }
        let docs_path = with_docs_suffix(path);
        if !path.exists() {
            return Err(EmbeddingStoreError::NotFound("Index file not found.".into()));
        }
        if !docs_path.exists() {
            return Err(EmbeddingStoreError::NotFound(
                "Docs sidecar not found.".into(),
            ));
        }

        self.index.load(path)?;

        let bytes = fs::read(&docs_path).map_err(|e| EmbeddingStoreError::Io(e.to_string()))?;
        let mut r = BinReader::new(&bytes);
        let magic = r.read_i32()?;
        if magic != DOCS_MAGIC {
            return Err(EmbeddingStoreError::InvalidData(
                "Not an HnswEmbeddingStore docs sidecar.".into(),
            ));
        }
        let version = r.read_u16()?;
        if version != DOCS_VERSION {
            return Err(EmbeddingStoreError::InvalidData(format!(
                "Unsupported docs version {version}."
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

        self.by_id.clear();
        self.id_lookup.clear();
        for i in 0..count {
            let id = r.read_string()?;
            let text = r.read_string()?;
            let live = r.read_bool()?;
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
            let doc = EmbeddingDocument {
                id: id.clone(),
                text,
                metadata,
            };
            self.by_id.push(doc);
            if live {
                self.id_lookup.insert(id, i as i64);
            }
        }
        Ok(())
    }
}

/// `path` + `.docs`.
fn with_docs_suffix(path: &Path) -> PathBuf {
    let mut s = path.as_os_str().to_os_string();
    s.push(".docs");
    PathBuf::from(s)
}

// ─────────────────────────────────────────────────────────────────────────────
// BinaryWriter / BinaryReader — byte-compatible with .NET BinaryWriter/Reader
// (7-bit-encoded string length prefix, little-endian scalars, 1-byte bool).
// Local copies (the ones in `embeddings_local` are private to that module).
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
    fn write_bool(&mut self, v: bool) {
        self.buf.push(if v { 1 } else { 0 });
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
                "Unexpected end of docs sidecar.".into(),
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
    fn read_bool(&mut self) -> Result<bool, EmbeddingStoreError> {
        Ok(self.take(1)?[0] != 0)
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

#[cfg(test)]
mod tests {
    use super::*;
    use crate::embeddings_local::EmbeddingIndexHit;

    /// A trivial brute-force index over stored raw vectors (stands in for the
    /// TurboVec SIMD bridge).
    struct FakeIndex {
        dim: usize,
        vectors: Vec<Vec<f32>>,
    }
    impl FakeIndex {
        fn new(dim: usize) -> Self {
            Self {
                dim,
                vectors: Vec::new(),
            }
        }
    }
    impl IEmbeddingIndex for FakeIndex {
        fn dimension(&self) -> usize {
            self.dim
        }
        fn count(&self) -> i64 {
            self.vectors.len() as i64
        }
        fn add(&mut self, vector: &[f32]) -> Result<i64, EmbeddingStoreError> {
            let id = self.vectors.len() as i64;
            self.vectors.push(vector.to_vec());
            Ok(id)
        }
        fn search(
            &self,
            query_vector: &[f32],
            top_k: usize,
        ) -> Result<Vec<EmbeddingIndexHit>, EmbeddingStoreError> {
            let mut scored: Vec<EmbeddingIndexHit> = self
                .vectors
                .iter()
                .enumerate()
                .map(|(i, v)| {
                    let dot: f32 = v.iter().zip(query_vector).map(|(a, b)| a * b).sum();
                    EmbeddingIndexHit {
                        internal_id: i as i64,
                        score: dot,
                    }
                })
                .collect();
            scored.sort_by(|a, b| {
                b.score.partial_cmp(&a.score).unwrap_or(std::cmp::Ordering::Equal)
            });
            scored.truncate(top_k);
            Ok(scored)
        }
        fn save(&self, path: &Path) -> Result<(), EmbeddingStoreError> {
            // A real index writes its slot data at `path`; the store's `load`
            // existence-check depends on that, so the fake does too.
            fs::write(path, b"fake-index").map_err(|e| EmbeddingStoreError::Io(e.to_string()))
        }
        fn load(&mut self, _path: &Path) -> Result<(), EmbeddingStoreError> {
            Ok(())
        }
    }

    /// Identity encoder: turns "1,0,0,..." style text into a vector — but tests
    /// mostly use `add_with_vector`, so this just needs a dimension.
    struct DimEncoder(usize);
    impl IEmbeddingEncoder for DimEncoder {
        fn dimension(&self) -> usize {
            self.0
        }
        fn encode(&self, _text: &str) -> Result<Vec<f32>, EmbeddingStoreError> {
            Ok(vec![0.0; self.0])
        }
    }

    fn unit(dim: usize, hot: usize) -> Vec<f32> {
        let mut v = vec![0.0; dim];
        v[hot] = 1.0;
        v
    }

    #[test]
    fn rejects_non_multiple_of_8_dim() {
        let store = HnswEmbeddingStore::new(DimEncoder(10), FakeIndex::new(10));
        assert!(store.is_err());
    }

    #[test]
    fn add_search_remove() {
        let dim = 8;
        let mut store = HnswEmbeddingStore::new(DimEncoder(dim), FakeIndex::new(dim)).unwrap();
        store
            .add_with_vector(EmbeddingDocument::new("a", "alpha"), &unit(dim, 0))
            .unwrap();
        store
            .add_with_vector(EmbeddingDocument::new("b", "bravo"), &unit(dim, 1))
            .unwrap();

        let hits = store.search_vector(&unit(dim, 0), 5).unwrap();
        assert_eq!(hits[0].document.id, "a");

        assert!(store.remove("a").unwrap());
        let hits = store.search_vector(&unit(dim, 0), 5).unwrap();
        assert!(hits.iter().all(|h| h.document.id != "a"));
    }

    #[test]
    fn save_and_load_round_trip() {
        let dim = 8;
        let dir = std::env::temp_dir().join(format!(
            "hnsw_test_{}",
            uuid::Uuid::new_v4().as_simple()
        ));
        fs::create_dir_all(&dir).unwrap();
        let path = dir.join("index.turbo");

        let mut store = HnswEmbeddingStore::new(DimEncoder(dim), FakeIndex::new(dim)).unwrap();
        let mut meta = HashMap::new();
        meta.insert("k".to_string(), "v".to_string());
        store
            .add_with_vector(
                EmbeddingDocument::with_metadata("a", "alpha", meta),
                &unit(dim, 0),
            )
            .unwrap();
        store
            .add_with_vector(EmbeddingDocument::new("b", "bravo"), &unit(dim, 1))
            .unwrap();
        store.remove("b").unwrap();
        store.save(&path).unwrap();

        let mut loaded =
            HnswEmbeddingStore::new(DimEncoder(dim), FakeIndex::new(dim)).unwrap();
        loaded.load(&path).unwrap();
        assert_eq!(loaded.count(), 2);
        assert_eq!(loaded.live_count(), 1);
        let doc_a = &loaded.by_id[0];
        assert_eq!(doc_a.id, "a");
        assert_eq!(doc_a.metadata.as_ref().unwrap().get("k").unwrap(), "v");

        let _ = fs::remove_dir_all(&dir);
    }
}
