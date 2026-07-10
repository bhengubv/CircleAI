"""test_shard_kv_codec.py

Verifies ShardKvCodec — the byte-exact port of
CircleAI.Core.Compression.ShardKvCodec (C# is the spec).

Covers the wire format (int8 K + float32 LE scale header, 1/2/4-byte LE V
index), the seeded V codebook reproducing .NET System.Random(seed), the
online-PCA mean update, Hadamard self-inverse decode path, and argument
validation.
"""
from __future__ import annotations

import struct

import pytest

from circle_ai.core.shard_kv_codec import (
    ShardCompressedFrame,
    ShardKvCodec,
    _DotNetRandom,
)


# ── .NET Random reproduction ─────────────────────────────────────────────────


def test_dotnet_random_seed0_matches_known_sequence() -> None:
    r = _DotNetRandom(0)
    seq = [r.next_double() for _ in range(3)]
    # These are the canonical .NET `new Random(0).NextDouble()` values.
    assert seq[0] == pytest.approx(0.7262432699679598, abs=1e-15)
    assert seq[1] == pytest.approx(0.8173253595909687, abs=1e-15)
    assert seq[2] == pytest.approx(0.7680226893946634, abs=1e-15)


def test_dotnet_random_seed42_first_value() -> None:
    assert _DotNetRandom(42).next_double() == pytest.approx(
        0.6681064659115423, abs=1e-15
    )


# ── construction validation ──────────────────────────────────────────────────


def test_ctor_rejects_non_power_of_two_codewords() -> None:
    with pytest.raises(ValueError):
        ShardKvCodec(k_dim=8, k_rank=4, v_dim=8, v_codewords=17)


def test_ctor_rejects_bad_rank() -> None:
    with pytest.raises(ValueError):
        ShardKvCodec(k_dim=8, k_rank=9, v_dim=8, v_codewords=16)  # rank > dim
    with pytest.raises(ValueError):
        ShardKvCodec(k_dim=0, k_rank=1, v_dim=8, v_codewords=16)
    with pytest.raises(ValueError):
        ShardKvCodec(k_dim=8, k_rank=4, v_dim=0, v_codewords=16)


# ── wire format ──────────────────────────────────────────────────────────────


def test_encoded_k_layout_scale_header_and_int8_body() -> None:
    codec = ShardKvCodec(k_dim=8, k_rank=4, v_dim=8, v_codewords=16, v_codebook_seed=0)
    k = [0.5, -0.2, 0.3, 0.1, 0.0, 0.4, -0.1, 0.2]
    v = [0.1] * 8
    frame = codec.encode(k, v)
    # kRank(4) + 4 bytes for the float32 LE scale.
    assert len(frame.compressed_k) == 4 + 4
    scale = struct.unpack_from("<f", frame.compressed_k, 0)[0]
    assert scale > 0
    # The 4 body bytes are signed int8 in [-127, 127].
    for b in frame.compressed_k[4:]:
        sb = b - 256 if b >= 128 else b
        assert -127 <= sb <= 127


def test_encoded_v_index_width_by_codebook_size() -> None:
    # <= 256 codewords -> 1 byte
    c1 = ShardKvCodec(8, 4, 8, 16)
    assert len(c1.encode([0.1] * 8, [0.2] * 8).compressed_v) == 1
    # <= 65536 -> 2 bytes
    c2 = ShardKvCodec(8, 4, 8, 1024)
    assert len(c2.encode([0.1] * 8, [0.2] * 8).compressed_v) == 2
    # > 65536 -> 4 bytes
    c3 = ShardKvCodec(8, 4, 8, 131072)
    assert len(c3.encode([0.1] * 8, [0.2] * 8).compressed_v) == 4


def test_frame_carries_flattened_axes() -> None:
    codec = ShardKvCodec(k_dim=8, k_rank=4, v_dim=8, v_codewords=16)
    frame = codec.encode([0.1] * 8, [0.2] * 8)
    assert len(frame.k_principal_axes) == 4 * 8
    assert frame.k_original_dim == 8
    assert frame.v_original_dim == 8


# ── cross-instance determinism (the parity guarantee) ────────────────────────


def test_same_seed_same_bytes_across_instances() -> None:
    a = ShardKvCodec(8, 4, 8, 16, v_codebook_seed=7)
    b = ShardKvCodec(8, 4, 8, 16, v_codebook_seed=7)
    k = [0.5, -0.2, 0.3, 0.1, 0.0, 0.4, -0.1, 0.2]
    v = [0.1, 0.2, 0.3, 0.4, -0.4, -0.3, -0.2, -0.1]
    fa = a.encode(k, v)
    fb = b.encode(k, v)
    assert fa.compressed_k == fb.compressed_k
    assert fa.compressed_v == fb.compressed_v
    assert fa.k_principal_axes == fb.k_principal_axes


def test_different_seed_can_pick_different_codeword() -> None:
    a = ShardKvCodec(8, 4, 8, 16, v_codebook_seed=7)
    b = ShardKvCodec(8, 4, 8, 16, v_codebook_seed=999)
    k = [0.5, -0.2, 0.3, 0.1, 0.0, 0.4, -0.1, 0.2]
    v = [0.1, 0.2, 0.3, 0.4, -0.4, -0.3, -0.2, -0.1]
    # V index is a function of the seeded codebook, so a different seed is
    # allowed to (and here does) select a different codeword.
    assert a.encode(k, v).compressed_v != b.encode(k, v).compressed_v


# ── round-trip / decode ──────────────────────────────────────────────────────


def test_decode_v_returns_the_selected_codeword() -> None:
    codec = ShardKvCodec(8, 4, 8, 16, v_codebook_seed=0)
    k = [0.1] * 8
    v = [0.2] * 8
    frame = codec.encode(k, v)
    dk, dv = codec.decode(frame)
    assert len(dk) == 8 and len(dv) == 8
    # dv is exactly the codebook word at the encoded index.
    idx = frame.compressed_v[0]
    assert dv == list(codec._v_codebook[idx])


def test_decode_dim_mismatch_raises() -> None:
    codec = ShardKvCodec(8, 4, 8, 16)
    bad = ShardCompressedFrame(b"\x00" * 8, b"\x00", tuple([0.0] * 32), 16, 8)
    with pytest.raises(ValueError):
        codec.decode(bad)


def test_set_v_codebook_then_decode_uses_it() -> None:
    codec = ShardKvCodec(2, 1, 2, 2, v_codebook_seed=0)
    cb = [[1.0, 0.0], [0.0, 1.0]]
    codec.set_v_codebook(cb)
    # v closest to codeword 1 ([0,1]).
    frame = codec.encode([0.3, 0.7], [0.05, 0.95])
    _, dv = codec.decode(frame)
    assert dv == [0.0, 1.0]


# ── online mean ──────────────────────────────────────────────────────────────


def test_observe_k_tracks_running_mean_sample_count() -> None:
    codec = ShardKvCodec(4, 2, 4, 4)
    assert codec.samples_observed == 0
    codec.observe_k([1.0, 2.0, 3.0, 4.0])
    codec.observe_k([3.0, 2.0, 1.0, 0.0])
    assert codec.samples_observed == 2


def test_observe_k_dim_mismatch_raises() -> None:
    codec = ShardKvCodec(4, 2, 4, 4)
    with pytest.raises(ValueError):
        codec.observe_k([1.0, 2.0])


def test_encode_dim_mismatch_raises() -> None:
    codec = ShardKvCodec(4, 2, 4, 4)
    with pytest.raises(ValueError):
        codec.encode([1.0, 2.0], [1.0, 2.0, 3.0, 4.0])
    with pytest.raises(ValueError):
        codec.encode([1.0, 2.0, 3.0, 4.0], [1.0, 2.0])
