//! shard_kv_codec_test.rs
//!
//! Verifies [`circle_ai::model_runtime::ShardKvCodec`] — the byte-exact port of
//! `CircleAI.Core.Compression.ShardKvCodec`. Checks construction guards, the wire
//! format (float32-LE scale header + int8 body, LE codeword index), the
//! deterministic seed codebook, round-trip fidelity, and V exact reconstruction.

use circle_ai::model_runtime::{ShardCompressedFrame, ShardKvCodec};

#[test]
fn rejects_bad_dimensions() {
    assert!(ShardKvCodec::new(0, 1, 4, 4, 0).is_err());
    // kRank > kDim.
    assert!(ShardKvCodec::new(8, 9, 4, 4, 0).is_err());
    // vDim == 0.
    assert!(ShardKvCodec::new(8, 4, 0, 4, 0).is_err());
    // vCodewords not a power of two.
    assert!(ShardKvCodec::new(8, 4, 4, 3, 0).is_err());
    // vCodewords == 1.
    assert!(ShardKvCodec::new(8, 4, 4, 1, 0).is_err());
    // Valid.
    assert!(ShardKvCodec::new(8, 4, 4, 4, 0).is_ok());
}

#[test]
fn encoded_k_wire_format_is_scale_header_plus_int8_body() {
    let mut codec = ShardKvCodec::new(8, 4, 4, 4, 0).unwrap();
    let k = [0.5f32, -0.25, 0.75, 0.1, -0.6, 0.2, 0.9, -0.3];
    let v = [0.1f32, 0.2, 0.3, 0.4];
    let frame = codec.encode(&k, &v).unwrap();

    // encoded_k = 4-byte float32 LE scale + kRank int8 bytes.
    assert_eq!(frame.compressed_k.len(), 4 + 4);
    let mut scale_bytes = [0u8; 4];
    scale_bytes.copy_from_slice(&frame.compressed_k[0..4]);
    let scale = f32::from_le_bytes(scale_bytes);
    assert!(scale > 0.0, "scale should be positive, got {scale}");

    // Frame carries flattened axes of length kRank * kDim.
    assert_eq!(frame.k_principal_axes.len(), 4 * 8);
    assert_eq!(frame.k_original_dim, 8);
    assert_eq!(frame.v_original_dim, 4);
}

#[test]
fn v_index_byte_width_tracks_codebook_size() {
    // 4 codewords → 1 byte index.
    let mut small = ShardKvCodec::new(4, 2, 3, 4, 0).unwrap();
    let f = small.encode(&[0.1, 0.2, 0.3, 0.4], &[0.5, 0.6, 0.7]).unwrap();
    assert_eq!(f.compressed_v.len(), 1);

    // 512 codewords → 2-byte index.
    let mut big = ShardKvCodec::new(4, 2, 3, 512, 0).unwrap();
    let f2 = big.encode(&[0.1, 0.2, 0.3, 0.4], &[0.5, 0.6, 0.7]).unwrap();
    assert_eq!(f2.compressed_v.len(), 2);
}

#[test]
fn v_decodes_to_exact_codeword() {
    // With identity setup, V is VQ'd to the nearest codeword and decode returns
    // that codeword exactly (copy, not lossy).
    let mut codec = ShardKvCodec::new(4, 2, 4, 8, 7).unwrap();
    let k = [1.0f32, 0.0, 0.0, 0.0];
    let v = [0.9f32, -0.9, 0.1, 0.5];
    let frame = codec.encode(&k, &v).unwrap();
    let (_k_out, v_out) = codec.decode(&frame).unwrap();

    // v_out must equal one of the codebook words — specifically the nearest.
    // We can at least assert it round-trips stably: re-encoding v_out picks the
    // same index and decodes to the identical vector.
    let frame2 = codec.encode(&v_out, &v_out).unwrap();
    let (_k2, v_out2) = codec.decode(&frame2).unwrap();
    assert_eq!(v_out, v_out2);
}

#[test]
fn k_round_trips_within_quantisation_error() {
    // No observed samples → centre is zero. Identity-top-rank axes + self-inverse
    // Hadamard means K decode reconstructs the original up to int8 error.
    let k_dim = 8;
    let mut codec = ShardKvCodec::new(k_dim, k_dim, 4, 4, 0).unwrap();
    let k: Vec<f32> = (0..k_dim).map(|i| (i as f32 - 4.0) * 0.1).collect();
    let v = [0.1f32, 0.2, 0.3, 0.4];

    let frame = codec.encode(&k, &v).unwrap();
    let (k_out, _v_out) = codec.decode(&frame).unwrap();

    assert_eq!(k_out.len(), k_dim);
    // int8 quantisation over 8 dims — generous but bounded error.
    for (orig, got) in k.iter().zip(k_out.iter()) {
        assert!(
            (orig - got).abs() < 0.05,
            "K reconstruction drift too large: orig={orig}, got={got}"
        );
    }
}

#[test]
fn observe_k_updates_running_mean_count() {
    let mut codec = ShardKvCodec::new(4, 2, 4, 4, 0).unwrap();
    assert_eq!(codec.samples_observed(), 0);
    codec.observe_k(&[1.0, 2.0, 3.0, 4.0]).unwrap();
    codec.observe_k(&[5.0, 6.0, 7.0, 8.0]).unwrap();
    assert_eq!(codec.samples_observed(), 2);
    // Wrong dim rejected.
    assert!(codec.observe_k(&[1.0, 2.0]).is_err());
}

#[test]
fn set_principal_axes_shape_is_validated() {
    let mut codec = ShardKvCodec::new(4, 2, 4, 4, 0).unwrap();
    // Correct shape (2 x 4).
    let axes = vec![vec![1.0, 0.0, 0.0, 0.0], vec![0.0, 1.0, 0.0, 0.0]];
    assert!(codec.set_principal_axes(&axes).is_ok());
    // Wrong shape.
    let bad = vec![vec![1.0, 0.0, 0.0]];
    assert!(codec.set_principal_axes(&bad).is_err());
}

#[test]
fn set_v_codebook_is_validated() {
    let mut codec = ShardKvCodec::new(4, 2, 3, 4, 0).unwrap();
    let cb = vec![
        vec![0.1, 0.2, 0.3],
        vec![0.4, 0.5, 0.6],
        vec![0.7, 0.8, 0.9],
        vec![1.0, 1.1, 1.2],
    ];
    assert!(codec.set_v_codebook(&cb).is_ok());
    // Wrong count.
    assert!(codec.set_v_codebook(&cb[..3].to_vec()).is_err());
    // Wrong dim.
    let bad = vec![
        vec![0.1, 0.2],
        vec![0.4, 0.5],
        vec![0.7, 0.8],
        vec![1.0, 1.1],
    ];
    assert!(codec.set_v_codebook(&bad).is_err());
}

#[test]
fn seed_codebook_is_deterministic() {
    // Two codecs with the same seed pick identical codewords, so encoding the
    // same V yields identical compressed_v.
    let mut a = ShardKvCodec::new(4, 2, 4, 16, 123).unwrap();
    let mut b = ShardKvCodec::new(4, 2, 4, 16, 123).unwrap();
    let k = [0.3f32, 0.1, -0.2, 0.5];
    let v = [0.6f32, -0.4, 0.2, 0.8];
    let fa = a.encode(&k, &v).unwrap();
    let fb = b.encode(&k, &v).unwrap();
    assert_eq!(fa.compressed_v, fb.compressed_v);
    assert_eq!(fa.compressed_k, fb.compressed_k);
}

#[test]
fn decode_rejects_dim_mismatched_frame() {
    let mut codec = ShardKvCodec::new(4, 2, 4, 4, 0).unwrap();
    let bad_frame = ShardCompressedFrame::new(vec![0u8; 6], vec![0u8; 1], vec![0.0; 8], 8, 4);
    assert!(codec.decode(&bad_frame).is_err());
}
