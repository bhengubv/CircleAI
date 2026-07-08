//! multimodal_test.rs
//!
//! Exercises the multimodal memory pipeline: HeuristicMultimodalCaptioner,
//! InMemoryMultimodalMemoryStore, and the MultimodalMemoryIngester (dedup +
//! caption + persist). Mirrors the TS suite tests/multimodal.test.ts and
//! CircleAI.Tests.MultimodalMemoryTests. Bytes are synthesised inline.

use chrono::Utc;
use circle_ai::brain::BrainError;
use circle_ai::memory::multimodal::{
    CaptionResult, HeuristicMultimodalCaptioner, IMultimodalCaptioner, IMultimodalMemoryStore,
    InMemoryMultimodalMemoryStore, IngestOptions, MediaModality, MultimodalMemoryEntry,
    MultimodalMemoryIngester,
};
use std::collections::HashMap;

// ── Test helpers (mirror the C# FakeJpeg/FakePng/WireIngester) ───────────────

fn fake_jpeg(extra_bytes: usize) -> Vec<u8> {
    let mut buf = vec![0u8; 2 + extra_bytes];
    buf[0] = 0xff;
    buf[1] = 0xd8;
    for (i, b) in buf.iter_mut().enumerate().skip(2) {
        *b = (i % 251) as u8;
    }
    buf
}

fn fake_png(extra_bytes: usize) -> Vec<u8> {
    let mut buf = vec![0u8; 4 + extra_bytes];
    buf[0] = 0x89;
    buf[1] = 0x50;
    buf[2] = 0x4e;
    buf[3] = 0x47;
    for (i, b) in buf.iter_mut().enumerate().skip(4) {
        *b = (i % 251) as u8;
    }
    buf
}

fn wire_ingester(
    custom: Option<Box<dyn IMultimodalCaptioner>>,
) -> MultimodalMemoryIngester {
    let store: Box<dyn IMultimodalMemoryStore> = Box::new(InMemoryMultimodalMemoryStore::new());
    let captioners: Vec<Box<dyn IMultimodalCaptioner>> = match custom {
        Some(c) => vec![c, Box::new(HeuristicMultimodalCaptioner)],
        None => vec![Box::new(HeuristicMultimodalCaptioner)],
    };
    MultimodalMemoryIngester::new(captioners, store).unwrap()
}

/// FakeRichCaptioner — only handles Image, returns a rich caption + embedding.
struct FakeRichCaptioner;
impl IMultimodalCaptioner for FakeRichCaptioner {
    fn can_caption(&self, modality: MediaModality, _mime: Option<&str>) -> bool {
        modality == MediaModality::Image
    }
    fn caption(
        &self,
        _modality: MediaModality,
        _bytes: &[u8],
        _mime: Option<&str>,
    ) -> Result<CaptionResult, BrainError> {
        Ok(CaptionResult {
            caption: "A blue sky with two clouds.".to_string(),
            embedding: Some(vec![0.1, 0.2, 0.3]),
            width_px: Some(1920),
            height_px: Some(1080),
            duration_ms: None,
        })
    }
}

// ══════════════════════════════════════════════════════════════════════════
// HeuristicMultimodalCaptioner
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn always_can_caption_any_modality() {
    let c = HeuristicMultimodalCaptioner;
    assert!(c.can_caption(MediaModality::Image, Some("image/jpeg")));
    assert!(c.can_caption(MediaModality::Audio, None));
    assert!(c.can_caption(MediaModality::Video, Some("video/mp4")));
    assert!(c.can_caption(MediaModality::TextDocument, Some("application/pdf")));
}

#[test]
fn detects_the_jpeg_magic_and_produces_no_embedding() {
    let c = HeuristicMultimodalCaptioner;
    let r = c.caption(MediaModality::Image, &fake_jpeg(100), None).unwrap();
    assert!(r.caption.contains("image/jpeg"));
    assert!(r.embedding.is_none());
}

#[test]
fn detects_png_gif_wav_pdf_magic_bytes() {
    let c = HeuristicMultimodalCaptioner;
    assert!(c
        .caption(MediaModality::Image, &fake_png(100), None)
        .unwrap()
        .caption
        .contains("image/png"));
    assert!(c
        .caption(MediaModality::Image, &[0x47, 0x49, 0x46, 0x38], None)
        .unwrap()
        .caption
        .contains("image/gif"));
    assert!(c
        .caption(MediaModality::Audio, &[0x52, 0x49, 0x46, 0x46], None)
        .unwrap()
        .caption
        .contains("audio/wav"));
    assert!(c
        .caption(MediaModality::TextDocument, &[0x25, 0x50, 0x44, 0x46], None)
        .unwrap()
        .caption
        .contains("application/pdf"));
}

#[test]
fn falls_back_to_octet_stream_for_unknown_magic() {
    let c = HeuristicMultimodalCaptioner;
    let r = c.caption(MediaModality::Audio, &[1, 2, 3, 4], None).unwrap();
    assert!(r.caption.contains("application/octet-stream"));
}

#[test]
fn uses_the_declared_mime_type_when_provided() {
    let c = HeuristicMultimodalCaptioner;
    let r = c
        .caption(MediaModality::Image, &fake_png(100), Some("image/heic"))
        .unwrap();
    assert!(r.caption.contains("image/heic"));
}

#[test]
fn marks_itself_as_a_fallback_and_includes_the_byte_count() {
    let c = HeuristicMultimodalCaptioner;
    let bytes = fake_jpeg(100);
    let r = c.caption(MediaModality::Image, &bytes, None).unwrap();
    assert!(r.caption.contains("no captioner wired"));
    assert!(r.caption.contains(&format!("{} bytes", bytes.len())));
}

#[test]
fn uses_the_right_modality_label_per_media_kind() {
    let c = HeuristicMultimodalCaptioner;
    assert!(c
        .caption(MediaModality::Image, &fake_jpeg(100), None)
        .unwrap()
        .caption
        .starts_with("[Image"));
    assert!(c
        .caption(MediaModality::Audio, &fake_jpeg(100), Some("audio/wav"))
        .unwrap()
        .caption
        .starts_with("[Audio"));
    assert!(c
        .caption(MediaModality::Video, &fake_jpeg(100), Some("video/mp4"))
        .unwrap()
        .caption
        .starts_with("[Video"));
    assert!(c
        .caption(
            MediaModality::TextDocument,
            &fake_jpeg(100),
            Some("application/pdf")
        )
        .unwrap()
        .caption
        .starts_with("[Document"));
}

// ══════════════════════════════════════════════════════════════════════════
// Ingester — happy path
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn first_time_adds_an_entry_and_reports_not_deduplicated() {
    let ingester = wire_ingester(None);
    let bytes = fake_jpeg(100);
    let r = ingester
        .ingest(
            MediaModality::Image,
            &bytes,
            IngestOptions {
                mime_type: Some("image/jpeg".to_string()),
                ..Default::default()
            },
        )
        .unwrap();

    assert!(!r.was_deduplicated);
    assert_eq!(r.entry.source_byte_count, bytes.len() as i64);
    assert_eq!(r.entry.source_mime_type.as_deref(), Some("image/jpeg"));
    assert!(!r.entry.source_sha256.trim().is_empty());
}

#[test]
fn second_time_same_bytes_deduplicates_and_reinforces() {
    let ingester = wire_ingester(None);
    let bytes = fake_jpeg(100);
    let opts = || IngestOptions {
        mime_type: Some("image/jpeg".to_string()),
        ..Default::default()
    };
    let first = ingester
        .ingest(MediaModality::Image, &bytes, opts())
        .unwrap();
    let second = ingester
        .ingest(MediaModality::Image, &bytes, opts())
        .unwrap();

    assert!(!first.was_deduplicated);
    assert!(second.was_deduplicated);
    assert_eq!(first.entry.source_sha256, second.entry.source_sha256);
    assert_eq!(second.entry.reference_count, 2);
}

#[test]
fn different_bytes_produce_distinct_entries() {
    let ingester = wire_ingester(None);
    let ra = ingester
        .ingest(MediaModality::Image, &fake_jpeg(50), IngestOptions::default())
        .unwrap();
    let rb = ingester
        .ingest(MediaModality::Image, &fake_jpeg(60), IngestOptions::default())
        .unwrap();
    assert_ne!(ra.entry.source_sha256, rb.entry.source_sha256);
}

#[test]
fn empty_bytes_error() {
    let ingester = wire_ingester(None);
    assert!(ingester
        .ingest(MediaModality::Image, &[], IngestOptions::default())
        .is_err());
}

#[test]
fn records_source_uri_and_tags_when_provided() {
    let ingester = wire_ingester(None);
    let bytes = fake_png(100);
    let mut tags = HashMap::new();
    tags.insert("location".to_string(), "home".to_string());
    tags.insert("person".to_string(), "alex".to_string());
    let r = ingester
        .ingest(
            MediaModality::Image,
            &bytes,
            IngestOptions {
                mime_type: Some("image/png".to_string()),
                source_uri: Some("file:///photos/IMG_001.png".to_string()),
                tags: Some(tags),
            },
        )
        .unwrap();
    assert_eq!(
        r.entry.source_uri.as_deref(),
        Some("file:///photos/IMG_001.png")
    );
    let t = r.entry.tags.as_ref().unwrap();
    assert_eq!(t.get("location").map(String::as_str), Some("home"));
    assert_eq!(t.get("person").map(String::as_str), Some("alex"));
}

#[test]
fn computes_a_hex_lower_sha256() {
    let ingester = wire_ingester(None);
    let r = ingester
        .ingest(MediaModality::Image, &fake_jpeg(0), IngestOptions::default())
        .unwrap();
    // 64 hex chars, lower-case.
    assert_eq!(r.entry.source_sha256.len(), 64);
    assert!(r
        .entry
        .source_sha256
        .chars()
        .all(|c| c.is_ascii_digit() || ('a'..='f').contains(&c)));
}

/// Verifies the SHA-256 primitive against a KNOWN NIST vector so we know the wire
/// hash matches System.Security.Cryptography.SHA256 / node:crypto exactly.
#[test]
fn sha256_matches_known_vectors() {
    use circle_ai::memory::multimodal::compute_sha256;
    // SHA-256("") — the canonical empty-input digest.
    assert_eq!(
        compute_sha256(b""),
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
    );
    // SHA-256("abc").
    assert_eq!(
        compute_sha256(b"abc"),
        "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
    );
    // SHA-256 of the two-byte JPEG magic 0xFF 0xD8.
    assert_eq!(
        compute_sha256(&[0xff, 0xd8]).len(),
        64
    );
}

// ══════════════════════════════════════════════════════════════════════════
// Captioner selection
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn prefers_the_rich_captioner_over_the_heuristic() {
    let ingester = wire_ingester(Some(Box::new(FakeRichCaptioner)));
    let r = ingester
        .ingest(
            MediaModality::Image,
            &fake_jpeg(100),
            IngestOptions {
                mime_type: Some("image/jpeg".to_string()),
                ..Default::default()
            },
        )
        .unwrap();
    assert_eq!(r.entry.caption, "A blue sky with two clouds.");
    assert!(r.entry.embedding.is_some());
    assert_eq!(r.entry.width_px, Some(1920));
    assert_eq!(r.entry.height_px, Some(1080));
}

#[test]
fn falls_back_to_the_heuristic_when_the_rich_captioner_declines() {
    let ingester = wire_ingester(Some(Box::new(FakeRichCaptioner)));
    let r = ingester
        .ingest(
            MediaModality::Audio,
            &fake_png(100),
            IngestOptions {
                mime_type: Some("audio/wav".to_string()),
                ..Default::default()
            },
        )
        .unwrap();
    assert!(r.entry.caption.contains("no captioner wired"));
    assert!(r.entry.embedding.is_none());
}

#[test]
fn rejects_construction_with_zero_captioners() {
    let store: Box<dyn IMultimodalMemoryStore> = Box::new(InMemoryMultimodalMemoryStore::new());
    assert!(MultimodalMemoryIngester::new(Vec::new(), store).is_err());
}

// ══════════════════════════════════════════════════════════════════════════
// Store: search, prune, recent, reinforce
// ══════════════════════════════════════════════════════════════════════════

fn mm(sha: &str, caption: &str, embedding: Option<Vec<f32>>) -> MultimodalMemoryEntry {
    MultimodalMemoryEntry {
        caption: caption.to_string(),
        embedding,
        ..MultimodalMemoryEntry::with_hash(sha)
    }
}

#[test]
fn search_by_embedding_ranks_by_cosine() {
    let store = InMemoryMultimodalMemoryStore::new();
    store
        .add(mm("near", "near", Some(vec![1.0, 0.1, 0.0])))
        .unwrap();
    store.add(mm("far", "far", Some(vec![0.0, 0.0, 1.0]))).unwrap();

    let ranked = store.search(Some(&[1.0, 0.0, 0.0]), 2).unwrap();
    assert_eq!(ranked[0].source_sha256, "near");
    assert_eq!(ranked[1].source_sha256, "far");
}

#[test]
fn search_with_a_null_query_returns_most_recent() {
    let store = InMemoryMultimodalMemoryStore::new();
    let mut older = mm("older", "older", None);
    older.recorded_at_utc = Utc::now() - chrono::Duration::days(10);
    let mut newer = mm("newer", "newer", None);
    newer.recorded_at_utc = Utc::now();
    store.add(older).unwrap();
    store.add(newer).unwrap();
    let recent = store.search(None, 2).unwrap();
    assert_eq!(recent[0].source_sha256, "newer");
}

#[test]
fn prune_removes_entries_older_than_the_cutoff() {
    let store = InMemoryMultimodalMemoryStore::new();
    let mut old = mm("old", "old", None);
    old.recorded_at_utc = Utc::now() - chrono::Duration::days(10);
    let mut new = mm("new", "new", None);
    new.recorded_at_utc = Utc::now();
    store.add(old).unwrap();
    store.add(new).unwrap();

    let cutoff = Utc::now() - chrono::Duration::days(5);
    let removed = store.prune_older_than(&cutoff).unwrap();
    assert_eq!(removed, 1);
    assert_eq!(store.count().unwrap(), 1);
    assert!(store.get_by_hash("new").unwrap().is_some());
    assert!(store.get_by_hash("old").unwrap().is_none());
}

#[test]
fn reinforce_increments_the_reference_count() {
    let store = InMemoryMultimodalMemoryStore::new();
    store.add(mm("x", "x", None)).unwrap();
    store.reinforce("x").unwrap();
    store.reinforce("x").unwrap();
    let got = store.get_by_hash("x").unwrap().unwrap();
    assert_eq!(got.reference_count, 3); // initial 1 + 2 reinforce
}

#[test]
fn reinforce_on_an_unknown_hash_is_a_noop() {
    let store = InMemoryMultimodalMemoryStore::new();
    store.reinforce("missing").unwrap(); // must not error
    assert_eq!(store.count().unwrap(), 0);
}

#[test]
fn add_without_a_hash_errors() {
    let store = InMemoryMultimodalMemoryStore::new();
    assert!(store.add(mm("", "x", None)).is_err());
}

#[test]
fn hash_lookup_is_case_insensitive() {
    let store = InMemoryMultimodalMemoryStore::new();
    store.add(mm("ABCDEF", "x", None)).unwrap();
    assert!(store.get_by_hash("abcdef").unwrap().is_some());
}
