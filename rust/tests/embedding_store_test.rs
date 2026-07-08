//! embedding_store_test.rs
//!
//! Verifies CircleAI.Embeddings.Local.InMemoryEmbeddingStore port: add/search/
//! remove, cosine ranking, TurboQuant-compressed persistence, and the byte layout
//! of the "CELQ" file header (magic + version + bits + dim + count).

use std::collections::HashMap;
use std::path::PathBuf;

use circle_ai::embeddings_local::{
    EmbeddingDocument, EmbeddingStoreError, ICircleEmbeddingStore, IEmbeddingEncoder,
    InMemoryEmbeddingStore,
};

fn temp_path(label: &str) -> PathBuf {
    let mut p = std::env::temp_dir();
    let nonce = uuid::Uuid::new_v4().simple().to_string();
    p.push(format!("circleai-rust-store-{label}-{nonce}.celq"));
    p
}

/// Deterministic encoder: 4-dim, keyed off the first character so tests can steer
/// which documents are "close".
struct FixedEncoder {
    dim: usize,
}
impl IEmbeddingEncoder for FixedEncoder {
    fn dimension(&self) -> usize {
        self.dim
    }
    fn encode(&self, text: &str) -> Result<Vec<f32>, EmbeddingStoreError> {
        // Map on the leading char into one hot-ish axis, plus a small constant so
        // vectors are never all-zero.
        let mut v = vec![0.1f32; self.dim];
        let c = text.chars().next().unwrap_or('a') as usize;
        v[c % self.dim] += 1.0;
        Ok(v)
    }
}

#[test]
fn rejects_bad_bits_per_dim() {
    assert!(InMemoryEmbeddingStore::with_bits(FixedEncoder { dim: 4 }, 0).is_err());
    assert!(InMemoryEmbeddingStore::with_bits(FixedEncoder { dim: 4 }, 9).is_err());
    assert!(InMemoryEmbeddingStore::with_bits(FixedEncoder { dim: 4 }, 4).is_ok());
}

#[test]
fn add_count_remove() {
    let mut store = InMemoryEmbeddingStore::new(FixedEncoder { dim: 8 }).unwrap();
    assert_eq!(store.dimension(), 8);
    assert_eq!(store.count(), 0);
    store.add(EmbeddingDocument::new("a", "apple")).unwrap();
    store.add(EmbeddingDocument::new("b", "banana")).unwrap();
    assert_eq!(store.count(), 2);
    assert!(store.remove("a").unwrap());
    assert!(!store.remove("a").unwrap());
    assert_eq!(store.count(), 1);
}

#[test]
fn add_with_wrong_vector_len_errors() {
    let mut store = InMemoryEmbeddingStore::new(FixedEncoder { dim: 4 }).unwrap();
    let err = store
        .add_with_vector(EmbeddingDocument::new("x", "x"), &[1.0, 2.0])
        .unwrap_err();
    assert!(matches!(err, EmbeddingStoreError::Argument(_)));
}

#[test]
fn search_ranks_closest_first() {
    let mut store = InMemoryEmbeddingStore::with_bits(FixedEncoder { dim: 16 }, 8).unwrap();
    store.add(EmbeddingDocument::new("apple", "apple")).unwrap();
    store.add(EmbeddingDocument::new("banana", "banana")).unwrap();
    store.add(EmbeddingDocument::new("avocado", "avocado")).unwrap();

    // Query "apricot" starts with 'a' → same axis as apple/avocado.
    let hits = store.search("apricot", 3).unwrap();
    assert!(!hits.is_empty());
    // Top hit shares the 'a' axis.
    let top_id = &hits[0].document.id;
    assert!(top_id == "apple" || top_id == "avocado", "top was {top_id}");
    // Scores are descending.
    for w in hits.windows(2) {
        assert!(w[0].score >= w[1].score);
    }
}

#[test]
fn search_by_vector_and_topk_guard() {
    let mut store = InMemoryEmbeddingStore::new(FixedEncoder { dim: 4 }).unwrap();
    store.add(EmbeddingDocument::new("a", "a")).unwrap();
    // Wrong query dim.
    assert!(store.search_vector(&[1.0, 2.0], 1).is_err());
    // topK == 0.
    assert!(store.search_vector(&[0.1, 0.1, 0.1, 1.1], 0).is_err());
    // Valid.
    let hits = store.search_vector(&[0.1, 0.1, 0.1, 1.1], 1).unwrap();
    assert_eq!(hits.len(), 1);
}

#[test]
fn save_then_load_round_trips_documents_and_metadata() {
    let path = temp_path("roundtrip");
    let mut meta = HashMap::new();
    meta.insert("lang".to_string(), "en".to_string());
    meta.insert("src".to_string(), "unit".to_string());

    {
        let mut store = InMemoryEmbeddingStore::with_bits(FixedEncoder { dim: 8 }, 4).unwrap();
        store
            .add(EmbeddingDocument::with_metadata("doc1", "hello world", meta.clone()))
            .unwrap();
        store.add(EmbeddingDocument::new("doc2", "second")).unwrap();
        store.save(&path).unwrap();
    }

    let mut loaded = InMemoryEmbeddingStore::with_bits(FixedEncoder { dim: 8 }, 4).unwrap();
    loaded.load(&path).unwrap();
    assert_eq!(loaded.count(), 2);

    // Search should still find doc1 by its own text.
    let hits = loaded.search("hello world", 2).unwrap();
    let ids: Vec<&str> = hits.iter().map(|h| h.document.id.as_str()).collect();
    assert!(ids.contains(&"doc1"));

    // Metadata survived on doc1.
    let doc1 = hits.iter().find(|h| h.document.id == "doc1").unwrap();
    let md = doc1.document.metadata.as_ref().unwrap();
    assert_eq!(md.get("lang").map(|s| s.as_str()), Some("en"));
    assert_eq!(md.get("src").map(|s| s.as_str()), Some("unit"));

    let _ = std::fs::remove_file(&path);
}

#[test]
fn file_header_matches_celq_byte_layout() {
    let path = temp_path("header");
    {
        let mut store = InMemoryEmbeddingStore::with_bits(FixedEncoder { dim: 8 }, 4).unwrap();
        store.add(EmbeddingDocument::new("a", "alpha")).unwrap();
        store.save(&path).unwrap();
    }
    let bytes = std::fs::read(&path).unwrap();
    // FileMagic = 0x4C455143 written little-endian (matches C# BinaryWriter.Write(int)).
    // 0x4C455143.to_le_bytes() == [0x43, 0x51, 0x45, 0x4C].
    assert_eq!(&bytes[0..4], &0x4C45_5143i32.to_le_bytes());
    assert_eq!(&bytes[0..4], &[0x43, 0x51, 0x45, 0x4C]);
    // version u16 LE == 1.
    assert_eq!(&bytes[4..6], &[0x01, 0x00]);
    // bits u16 LE == 4.
    assert_eq!(&bytes[6..8], &[0x04, 0x00]);
    // dim i32 LE == 8.
    assert_eq!(&bytes[8..12], &[0x08, 0x00, 0x00, 0x00]);
    // count i32 LE == 1.
    assert_eq!(&bytes[12..16], &[0x01, 0x00, 0x00, 0x00]);
    // Next: 7-bit-prefixed id "a" → length byte 0x01, then 'a' (0x61).
    assert_eq!(bytes[16], 0x01);
    assert_eq!(bytes[17], 0x61);
    let _ = std::fs::remove_file(&path);
}

#[test]
fn load_rejects_bits_mismatch() {
    let path = temp_path("bits-mismatch");
    {
        let mut store = InMemoryEmbeddingStore::with_bits(FixedEncoder { dim: 8 }, 4).unwrap();
        store.add(EmbeddingDocument::new("a", "a")).unwrap();
        store.save(&path).unwrap();
    }
    let mut wrong = InMemoryEmbeddingStore::with_bits(FixedEncoder { dim: 8 }, 2).unwrap();
    let err = wrong.load(&path).unwrap_err();
    assert!(matches!(err, EmbeddingStoreError::InvalidData(_)));
    let _ = std::fs::remove_file(&path);
}

#[test]
fn load_missing_file_errors() {
    let mut store = InMemoryEmbeddingStore::new(FixedEncoder { dim: 4 }).unwrap();
    let err = store
        .load(&PathBuf::from("C:/no/such/store.celq"))
        .unwrap_err();
    assert!(matches!(err, EmbeddingStoreError::NotFound(_)));
}
