//! compression_test.rs
//!
//! Exercises the TurboQuant codec + the compressed store decorators. Mirrors the
//! TS suite tests/compression.test.ts and the C# CircleAI.Tests
//! TurboQuantCodecTests + CompressedStoreTests, and pins the cross-language wire
//! format against ground-truth captured from the C# codec. The encoded payload —
//! the thing persisted and shared across devices/languages — MUST be
//! BYTE-IDENTICAL with C#.

use std::sync::Arc;

use chrono::{TimeZone, Utc};
use circle_ai::memory::compression::{
    BetaLloydMaxCodebook, BitPacker, CompressedEpisodicMemoryStore,
    CompressedMultimodalMemoryStore, EmbeddingPayloadCodec, OrthogonalRotation, TurboQuantCodec,
    COMPRESSED_TAG_KEY,
};
use circle_ai::memory::episodic::InMemoryEpisodicStore;
use circle_ai::memory::multimodal::{
    IMultimodalMemoryStore, InMemoryMultimodalMemoryStore, MultimodalMemoryEntry,
};
use circle_ai::memory::EpisodicMemoryEntry;
use uuid::Uuid;

// ── Helpers (mirror the C#/TS test helpers) ─────────────────────────────────

/// Deterministic Mulberry32 PRNG so vectors are reproducible across runs AND
/// match the TS/C# fixtures bit-for-bit.
struct Mulberry32 {
    a: u32,
}
impl Mulberry32 {
    fn new(seed: u32) -> Self {
        Self { a: seed }
    }
    fn next(&mut self) -> f64 {
        // Mirrors the TS mulberry32 exactly (Math.imul, >>> unsigned shifts).
        self.a = self.a.wrapping_add(0x6d2b79f5);
        let mut t = self.a;
        t = (t ^ (t >> 15)).wrapping_mul(1 | t);
        t ^= t.wrapping_add((t ^ (t >> 7)).wrapping_mul(61 | t));
        ((t ^ (t >> 14)) as f64) / 4294967296.0
    }
}

/// Reproduces the TS `randomUnit(dim, seed)` — an L2-normalised f64 vector, then
/// narrowed to f32 (the Rust codec surface takes `&[f32]`, matching the C#
/// `float[]` the fixtures were captured from).
fn random_unit(dim: usize, seed: u32) -> Vec<f32> {
    let mut rng = Mulberry32::new(seed);
    let mut v = vec![0.0f64; dim];
    let mut sum_sq = 0.0f64;
    for x in v.iter_mut() {
        *x = rng.next() * 2.0 - 1.0;
        sum_sq += *x * *x;
    }
    let inv = 1.0 / sum_sq.sqrt();
    v.iter().map(|&x| (x * inv) as f32).collect()
}

fn cosine(a: &[f32], b: &[f32]) -> f64 {
    let mut dot = 0.0f64;
    let mut mag_a = 0.0f64;
    let mut mag_b = 0.0f64;
    for i in 0..a.len() {
        dot += a[i] as f64 * b[i] as f64;
        mag_a += a[i] as f64 * a[i] as f64;
        mag_b += b[i] as f64 * b[i] as f64;
    }
    let denom = mag_a.sqrt() * mag_b.sqrt();
    if denom < 1e-30 {
        0.0
    } else {
        dot / denom
    }
}

fn hex(b: &[u8]) -> String {
    let mut s = String::with_capacity(b.len() * 2);
    for byte in b {
        s.push_str(&format!("{byte:02x}"));
    }
    s
}

// ══════════════════════════════════════════════════════════════════════════
// Cross-language parity — ground truth captured from the C# codec.
// If these break, the wire format has diverged from every other SDK language.
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn bitpacker_pack_matches_csharp_for_2_3_4_bit_index_arrays() {
    assert_eq!(
        hex(&BitPacker::pack(&[0, 3, 1, 2, 3, 0, 2, 1], 2).unwrap()),
        "9c63"
    );
    assert_eq!(
        hex(&BitPacker::pack(&[0, 7, 3, 5, 1, 6, 2, 4], 3).unwrap()),
        "f81a8b"
    );
    assert_eq!(
        hex(&BitPacker::pack(&[15, 0, 8, 7, 1, 14, 9, 6], 4).unwrap()),
        "0f78e169"
    );
}

#[test]
fn beta_lloyd_max_centroids_match_csharp_fp32_exact() {
    let cb = BetaLloydMaxCodebook::get(2, 8).unwrap();
    assert_eq!(
        cb.centroids,
        vec![
            -0.5048246383666992f32,
            -0.15792210400104523,
            0.15792210400104523,
            0.5048246383666992,
        ]
    );
    let cb4 = BetaLloydMaxCodebook::get(4, 16).unwrap();
    assert_eq!(
        cb4.centroids,
        vec![
            -0.6039019227027893f32,
            -0.4742901921272278,
            -0.37855634093284607,
            -0.2978082597255707,
            -0.2253989577293396,
            -0.1580331176519394,
            -0.09372113645076752,
            -0.031065061688423157,
            0.031065061688423157,
            0.09372113645076752,
            0.1580331176519394,
            0.2253989577293396,
            0.2978082597255707,
            0.37855634093284607,
            0.4742901921272278,
            0.6039019227027893,
        ]
    );
}

#[test]
fn encodes_an_8_dim_vector_to_the_exact_csharp_base64_payload() {
    let v8: [f32; 8] = [0.1, -0.2, 0.3, -0.4, 0.5, -0.6, 0.7, -0.8];
    // These are byte-identical to what CircleAI.Memory.Compression emits.
    assert_eq!(
        EmbeddingPayloadCodec::encode_base64(&v8, 2).unwrap(),
        "VFEzAQIAAAAIAAAAEdK2P9B5"
    );
    assert_eq!(
        EmbeddingPayloadCodec::encode_base64(&v8, 4).unwrap(),
        "VFEzAQQAAAAIAAAAEdK2PzPHpV4="
    );
    assert_eq!(
        hex(&EmbeddingPayloadCodec::encode(&v8, 2).unwrap()),
        "54513301020000000800000011d2b63fd079"
    );
    assert_eq!(
        hex(&EmbeddingPayloadCodec::encode(&v8, 4).unwrap()),
        "54513301040000000800000011d2b63f33c7a55e"
    );
}

#[test]
fn stores_the_exact_csharp_norm_in_the_payload() {
    let v8: [f32; 8] = [0.1, -0.2, 0.3, -0.4, 0.5, -0.6, 0.7, -0.8];
    assert_eq!(TurboQuantCodec::encode(&v8, 2).unwrap().norm, 1.4282857179641724);
}

#[test]
fn encodes_a_tiny_4_dim_vector_to_the_exact_csharp_byte_layout() {
    let v4: [f32; 4] = [1.0, 2.0, 3.0, 4.0];
    assert_eq!(
        hex(&EmbeddingPayloadCodec::encode(&v4, 2).unwrap()),
        "5451330102000000040000006f45af409c"
    );
    assert_eq!(
        EmbeddingPayloadCodec::encode_base64(&v4, 2).unwrap(),
        "VFEzAQIAAAAEAAAAb0WvQJw="
    );
    assert_eq!(TurboQuantCodec::encode(&v4, 2).unwrap().norm, 5.4772257804870605);
}

#[test]
fn rotation_matrix_row_0_dim_8_matches_csharp_fp32_exact() {
    let m = OrthogonalRotation::get_matrix(8);
    let row0 = &m[0..8];
    assert_eq!(
        row0,
        &[
            0.32915404438972473f32,
            -0.15729351341724396,
            -0.6576523184776306,
            0.4990078806877136,
            -0.2985365092754364,
            -0.17185114324092865,
            0.024059195071458817,
            0.2572260797023773,
        ]
    );
}

// ══════════════════════════════════════════════════════════════════════════
// BitPacker (mirrors TurboQuantCodecTests.BitPacker_*)
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn bitpacker_round_trips_indices_at_each_width() {
    for bits in [1u32, 2, 3, 4, 8] {
        let max = (1u32 << bits) - 1;
        let mut rng = Mulberry32::new(123 + bits);
        let mut indices = vec![0u16; 256];
        for slot in indices.iter_mut() {
            *slot = (rng.next() * (max as f64 + 1.0)).floor() as u16;
        }

        let packed = BitPacker::pack(&indices, bits).unwrap();
        let unpacked = BitPacker::unpack(&packed, indices.len(), bits).unwrap();

        assert_eq!(unpacked.len(), indices.len());
        assert_eq!(unpacked, indices);
    }
}

#[test]
fn bitpacker_byte_count_matches_spec() {
    let indices = vec![0u16; 1536];
    assert_eq!(BitPacker::pack(&indices, 2).unwrap().len(), 384);
}

#[test]
fn bitpacker_rejects_an_overflowing_index() {
    let err = BitPacker::pack(&[4], 2);
    assert!(err.is_err());
    assert!(err.unwrap_err().to_string().contains("exceeds 2-bit range"));
}

#[test]
fn bitpacker_rejects_an_out_of_range_width() {
    assert!(BitPacker::pack(&[0], 0).is_err());
    assert!(BitPacker::pack(&[0], 17).is_err());
}

// ══════════════════════════════════════════════════════════════════════════
// OrthogonalRotation
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn rotation_preserves_l2_norm() {
    let dim = 64;
    let v = random_unit(dim, 42);
    let mut r = vec![0.0f32; dim];
    OrthogonalRotation::rotate(dim, &v, &mut r);
    let mut sq_a = 0.0f64;
    let mut sq_r = 0.0f64;
    for i in 0..dim {
        sq_a += v[i] as f64 * v[i] as f64;
        sq_r += r[i] as f64 * r[i] as f64;
    }
    assert!((sq_r.sqrt() - sq_a.sqrt()).abs() < 1e-3);
}

#[test]
fn rotate_then_unrotate_recovers_the_input() {
    let dim = 64;
    let v = random_unit(dim, 7);
    let mut r = vec![0.0f32; dim];
    let mut v2 = vec![0.0f32; dim];
    OrthogonalRotation::rotate(dim, &v, &mut r);
    OrthogonalRotation::unrotate(dim, &r, &mut v2);
    for i in 0..dim {
        assert!((v2[i] - v[i]).abs() < 1e-3);
    }
}

// ══════════════════════════════════════════════════════════════════════════
// BetaLloydMaxCodebook
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn codebook_has_correct_sizes() {
    for (bits, dim) in [(1u32, 16usize), (2, 64), (3, 128), (4, 256)] {
        let cb = BetaLloydMaxCodebook::get(bits, dim).unwrap();
        let n = 1usize << bits;
        assert_eq!(cb.centroids.len(), n);
        assert_eq!(cb.boundaries.len(), n - 1);
    }
}

#[test]
fn codebook_centroids_are_strictly_monotonic() {
    let cb = BetaLloydMaxCodebook::get(4, 128).unwrap();
    for i in 1..cb.centroids.len() {
        assert!(cb.centroids[i] > cb.centroids[i - 1]);
    }
}

#[test]
fn codebook_bin_for_round_trips_through_the_boundaries() {
    let cb = BetaLloydMaxCodebook::get(2, 64).unwrap();
    for i in 0..cb.boundaries.len() {
        let just_before = cb.boundaries[i] - 1e-6;
        let just_after = cb.boundaries[i] + 1e-6;
        assert_eq!(BetaLloydMaxCodebook::bin_for(just_before, &cb.boundaries), i as u16);
        assert_eq!(
            BetaLloydMaxCodebook::bin_for(just_after, &cb.boundaries),
            (i + 1) as u16
        );
    }
}

// ══════════════════════════════════════════════════════════════════════════
// TurboQuantCodec end-to-end
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn codec_round_trip_preserves_geometry() {
    for (dim, bits, floor) in [(64usize, 4u32, 0.99f64), (128, 4, 0.99), (256, 3, 0.96), (512, 2, 0.85)] {
        let v = random_unit(dim, 42);
        let reconstructed = TurboQuantCodec::round_trip(&v, bits).unwrap();
        assert_eq!(reconstructed.len(), dim);
        let cos = cosine(&v, &reconstructed);
        assert!(cos >= floor, "dim={dim} bits={bits}: cos {cos} below floor {floor}");
    }
}

#[test]
fn codec_zero_vector_round_trips_to_zeros() {
    let z = vec![0.0f32; 64];
    let r = TurboQuantCodec::round_trip(&z, 2).unwrap();
    for x in r {
        assert_eq!(x, 0.0);
    }
}

#[test]
fn codec_payload_size_matches_spec() {
    assert_eq!(TurboQuantCodec::payload_byte_count(1536, 2), 384);
}

#[test]
fn codec_compression_ratio_at_1536_2bit_exceeds_15x() {
    let ratio = TurboQuantCodec::compression_ratio(1536, 2);
    assert!(ratio > 15.0, "got {ratio}");
    assert_eq!(ratio, 15.835051546391753);
}

#[test]
fn codec_rejects_invalid_bit_widths() {
    let mut v = vec![0.0f32; 32];
    v[0] = 1.0;
    assert!(TurboQuantCodec::encode(&v, 0).is_err());
    assert!(TurboQuantCodec::encode(&v, 9).is_err());
}

#[test]
fn codec_rejects_a_length_1_vector() {
    assert!(TurboQuantCodec::encode(&[1.0], 2).is_err());
}

#[test]
fn codec_encode_is_deterministic_across_runs() {
    let v = random_unit(128, 7);
    let a = TurboQuantCodec::encode(&v, 3).unwrap();
    let b = TurboQuantCodec::encode(&v, 3).unwrap();
    assert_eq!(a.norm, b.norm);
    assert_eq!(a.packed_indices, b.packed_indices);
}

#[test]
fn codec_preserves_inner_product_between_correlated_compressed_vectors() {
    let dim = 128;
    let a = random_unit(dim, 1);
    let b = random_unit(dim, 2);
    let mut blended = vec![0.0f32; dim];
    for i in 0..dim {
        blended[i] = 0.7 * a[i] + 0.3 * b[i];
    }
    let mut bn = 0.0f32;
    for i in 0..dim {
        bn += blended[i] * blended[i];
    }
    let inv_n = 1.0 / bn.sqrt();
    for i in 0..dim {
        blended[i] *= inv_n;
    }

    let true_cos = cosine(&a, &blended);
    let a_hat = TurboQuantCodec::round_trip(&a, 4).unwrap();
    let blend_hat = TurboQuantCodec::round_trip(&blended, 4).unwrap();
    let recon_cos = cosine(&a_hat, &blend_hat);
    assert!(
        (recon_cos - true_cos).abs() <= 0.05,
        "true={true_cos} recon={recon_cos}"
    );
}

// ══════════════════════════════════════════════════════════════════════════
// EmbeddingPayloadCodec
// ══════════════════════════════════════════════════════════════════════════

#[test]
fn payload_round_trip_preserves_cosine_4bit() {
    let v = random_unit(128, 42);
    let encoded = EmbeddingPayloadCodec::encode(&v, 4).unwrap();
    let decoded = EmbeddingPayloadCodec::decode(&encoded).unwrap();
    assert!(cosine(&v, &decoded) >= 0.99);
}

#[test]
fn payload_detects_its_own_header() {
    let encoded = EmbeddingPayloadCodec::encode(&random_unit(64, 1), 2).unwrap();
    assert!(EmbeddingPayloadCodec::is_encoded(&encoded));
    assert!(!EmbeddingPayloadCodec::is_encoded(&[0, 1, 2]));
}

#[test]
fn payload_rejects_a_too_short_payload() {
    let err = EmbeddingPayloadCodec::decode(&[1, 2, 3]);
    assert!(err.is_err());
    assert!(err.unwrap_err().to_string().to_lowercase().contains("too short"));
}

#[test]
fn payload_rejects_a_payload_without_the_magic_header() {
    let bad = vec![0u8; 20]; // right length, wrong magic
    let err = EmbeddingPayloadCodec::decode(&bad);
    assert!(err.is_err());
    assert!(err.unwrap_err().to_string().to_lowercase().contains("magic"));
}

#[test]
fn payload_base64_round_trip_preserves_cosine_3bit() {
    let v = random_unit(64, 7);
    let b64 = EmbeddingPayloadCodec::encode_base64(&v, 3).unwrap();
    let back = EmbeddingPayloadCodec::decode_base64(&b64).unwrap();
    assert!(cosine(&v, &back) >= 0.96);
}

#[test]
fn payload_at_2_bits_is_over_12x_smaller_than_fp32() {
    let v = random_unit(1536, 42);
    let encoded = EmbeddingPayloadCodec::encode(&v, 2).unwrap();
    let ratio = (v.len() * 4) as f64 / encoded.len() as f64;
    assert!(ratio > 12.0, "got {ratio}");
}

// ══════════════════════════════════════════════════════════════════════════
// CompressedEpisodicMemoryStore
// ══════════════════════════════════════════════════════════════════════════

fn episodic(user_text: &str, embedding: Option<Vec<f32>>, recorded: Option<chrono::DateTime<Utc>>) -> EpisodicMemoryEntry {
    EpisodicMemoryEntry {
        id: Uuid::new_v4(),
        recorded_at_utc: recorded
            .unwrap_or_else(|| Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap()),
        user_text: user_text.to_string(),
        assistant_text: "a".to_string(),
        app_context: None,
        embedding,
        tags: None,
    }
}

#[test]
fn episodic_stores_the_embedding_as_a_compressed_tag_not_a_float_array() {
    let inner = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let outer = CompressedEpisodicMemoryStore::new(inner.clone(), 2).unwrap();
    outer
        .add(episodic("hello", Some(random_unit(128, 1)), None))
        .unwrap();

    let raw_recent = inner.get_recent_shared(1).unwrap();
    assert_eq!(raw_recent.len(), 1);
    assert!(raw_recent[0].embedding.is_none());
    let tags = raw_recent[0].tags.as_ref().unwrap();
    assert!(tags.contains_key(COMPRESSED_TAG_KEY));
}

#[test]
fn episodic_get_recent_rehydrates_the_embedding() {
    let inner = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let outer = CompressedEpisodicMemoryStore::new(inner, 4).unwrap();
    let original = random_unit(64, 1);
    outer.add(episodic("x", Some(original.clone()), None)).unwrap();

    let got = outer.get_recent(1).unwrap();
    assert_eq!(got.len(), 1);
    let emb = got[0].embedding.as_ref().unwrap();
    assert!(cosine(&original, emb) >= 0.99);
}

#[test]
fn episodic_search_ranks_by_cosine_through_compression() {
    let inner = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let outer = CompressedEpisodicMemoryStore::new(inner, 4).unwrap();
    let v1 = random_unit(64, 1);
    let v2 = random_unit(64, 2);
    outer.add(episodic("near", Some(v1.clone()), None)).unwrap();
    outer.add(episodic("far", Some(v2), None)).unwrap();

    let results = outer.search(Some(&v1), 2).unwrap();
    assert_eq!(results.len(), 2);
    assert_eq!(results[0].user_text, "near");
}

#[test]
fn episodic_search_with_a_null_query_returns_recency() {
    let inner = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let outer = CompressedEpisodicMemoryStore::new(inner, 4).unwrap();
    outer
        .add(episodic(
            "old",
            Some(random_unit(32, 1)),
            Some(Utc.with_ymd_and_hms(2026, 1, 1, 0, 0, 0).unwrap()),
        ))
        .unwrap();
    outer
        .add(episodic(
            "new",
            Some(random_unit(32, 2)),
            Some(Utc.with_ymd_and_hms(2026, 6, 1, 0, 0, 0).unwrap()),
        ))
        .unwrap();
    let results = outer.search(None, 1).unwrap();
    assert_eq!(results.len(), 1);
    assert_eq!(results[0].user_text, "new");
}

#[test]
fn episodic_an_entry_without_an_embedding_passes_through_unchanged() {
    let inner = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    let outer = CompressedEpisodicMemoryStore::with_default_bits(inner.clone());
    outer.add(episodic("u", None, None)).unwrap();
    let raw = inner.get_recent_shared(1).unwrap();
    assert_eq!(raw.len(), 1);
    assert!(raw[0].embedding.is_none());
    let has_tag = raw[0]
        .tags
        .as_ref()
        .is_some_and(|t| t.contains_key(COMPRESSED_TAG_KEY));
    assert!(!has_tag);
}

#[test]
fn episodic_rejects_an_invalid_bit_width() {
    let inner = Arc::new(InMemoryEpisodicStore::with_default_capacity());
    assert!(CompressedEpisodicMemoryStore::new(inner, 9).is_err());
}

#[test]
fn episodic_exposes_the_compressed_tag_key_constant() {
    assert_eq!(CompressedEpisodicMemoryStore::COMPRESSED_TAG_KEY, "x-tq-embedding");
}

// ══════════════════════════════════════════════════════════════════════════
// CompressedMultimodalMemoryStore
// ══════════════════════════════════════════════════════════════════════════

fn mm(sha: &str, caption: &str, embedding: Option<Vec<f32>>) -> MultimodalMemoryEntry {
    MultimodalMemoryEntry {
        caption: caption.to_string(),
        embedding,
        ..MultimodalMemoryEntry::with_hash(sha)
    }
}

#[test]
fn multimodal_round_trips_the_embedding_and_metadata() {
    let inner = Arc::new(InMemoryMultimodalMemoryStore::new());
    let outer = CompressedMultimodalMemoryStore::new(inner, 4).unwrap();
    let emb = random_unit(128, 42);
    let mut entry = mm("deadbeef", "a sunny beach", Some(emb.clone()));
    entry.width_px = Some(1920);
    entry.height_px = Some(1080);
    outer.add(entry).unwrap();

    let got = outer.get_by_hash("deadbeef").unwrap().unwrap();
    assert_eq!(got.caption, "a sunny beach");
    assert_eq!(got.width_px, Some(1920));
    assert_eq!(got.height_px, Some(1080));
    let g = got.embedding.as_ref().unwrap();
    assert!(cosine(&emb, g) >= 0.99);
}

#[test]
fn multimodal_inner_store_sees_a_null_embedding_and_a_compressed_tag() {
    let inner = Arc::new(InMemoryMultimodalMemoryStore::new());
    let outer = CompressedMultimodalMemoryStore::with_default_bits(inner.clone());
    outer.add(mm("abc", "x", Some(random_unit(64, 1)))).unwrap();

    let raw = inner.get_by_hash("abc").unwrap().unwrap();
    assert!(raw.embedding.is_none());
    assert!(raw
        .tags
        .as_ref()
        .is_some_and(|t| t.contains_key(COMPRESSED_TAG_KEY)));
}

#[test]
fn multimodal_search_ranks_by_cosine_through_compression() {
    let inner = Arc::new(InMemoryMultimodalMemoryStore::new());
    let outer = CompressedMultimodalMemoryStore::new(inner, 4).unwrap();
    let v1 = random_unit(64, 1);
    let v2 = random_unit(64, 2);
    outer.add(mm("a", "near", Some(v1.clone()))).unwrap();
    outer.add(mm("b", "far", Some(v2))).unwrap();

    let results = outer.search(Some(&v1), 2).unwrap();
    assert_eq!(results.len(), 2);
    assert_eq!(results[0].caption, "near");
}

#[test]
fn multimodal_reinforce_and_prune_delegate_through_the_decorator() {
    let inner = Arc::new(InMemoryMultimodalMemoryStore::new());
    let outer = CompressedMultimodalMemoryStore::new(inner, 4).unwrap();
    outer.add(mm("x", "x", Some(random_unit(32, 1)))).unwrap();
    outer.reinforce("x").unwrap();
    let got = outer.get_by_hash("x").unwrap().unwrap();
    assert_eq!(got.reference_count, 2);
    assert_eq!(outer.count().unwrap(), 1);
}
