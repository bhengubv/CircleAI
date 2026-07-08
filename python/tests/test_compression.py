"""test_compression.py

Exercises the TurboQuant codec + the compressed store decorators. Mirrors the
TypeScript compression.test.ts (and the C# TurboQuantCodecTests /
CompressedStoreTests), and pins the cross-language wire format against
ground-truth captured from the C# codec (see the PARITY block). The encoded
payload — the thing that is persisted and shared across devices / languages —
must be BYTE-IDENTICAL with C#.
"""
from __future__ import annotations

import math
import uuid
from datetime import datetime, timezone

import pytest

from circle_ai.memory.compression import (
    BetaLloydMaxCodebook,
    BitPacker,
    COMPRESSED_TAG_KEY,
    CompressedEpisodicMemoryStore,
    CompressedMultimodalMemoryStore,
    EmbeddingPayloadCodec,
    OrthogonalRotation,
    TurboQuantCodec,
)
from circle_ai.memory.episodic_memory import EpisodicMemoryEntry
from circle_ai.memory.in_memory_episodic_store import InMemoryEpisodicStore
from circle_ai.memory.multimodal import (
    InMemoryMultimodalMemoryStore,
    MediaModality,
    MultimodalMemoryEntry,
)


# ── Helpers (mirror the C#/TS test helpers) ──────────────────────────────────


def _imul(x: int, y: int) -> int:
    """32-bit signed multiply, matching JS Math.imul."""
    x &= 0xFFFFFFFF
    y &= 0xFFFFFFFF
    r = (x * y) & 0xFFFFFFFF
    return r


def make_mulberry32(seed: int):
    a = seed & 0xFFFFFFFF

    def _next() -> float:
        nonlocal a
        a = (a + 0x6D2B79F5) & 0xFFFFFFFF
        t = _imul(a ^ (a >> 15), 1 | a)
        t = ((t + _imul(t ^ (t >> 7), 61 | t)) & 0xFFFFFFFF) ^ t
        t &= 0xFFFFFFFF
        return ((t ^ (t >> 14)) & 0xFFFFFFFF) / 4294967296.0

    return _next


def random_unit(dim: int, seed: int) -> list[float]:
    rng = make_mulberry32(seed)
    v = [0.0] * dim
    sum_sq = 0.0
    for i in range(dim):
        v[i] = rng() * 2 - 1
        sum_sq += v[i] * v[i]
    inv = 1.0 / math.sqrt(sum_sq)
    for i in range(dim):
        v[i] *= inv
    return v


def cosine(a, b) -> float:
    dot = 0.0
    mag_a = 0.0
    mag_b = 0.0
    for i in range(len(a)):
        dot += a[i] * b[i]
        mag_a += a[i] * a[i]
        mag_b += b[i] * b[i]
    denom = math.sqrt(mag_a) * math.sqrt(mag_b)
    return 0.0 if denom < 1e-30 else dot / denom


def hexstr(b) -> str:
    return bytes(b).hex()


# ══════════════════════════════════════════════════════════════════════════
# Cross-language parity — ground truth captured from the C# codec.
# If these break, the wire format has diverged from every other SDK language.
# ══════════════════════════════════════════════════════════════════════════


def test_bitpacker_pack_matches_csharp_for_2_3_4_bit_index_arrays():
    assert hexstr(BitPacker.pack([0, 3, 1, 2, 3, 0, 2, 1], 2)) == "9c63"
    assert hexstr(BitPacker.pack([0, 7, 3, 5, 1, 6, 2, 4], 3)) == "f81a8b"
    assert hexstr(BitPacker.pack([15, 0, 8, 7, 1, 14, 9, 6], 4)) == "0f78e169"


def test_beta_lloydmax_codebook_centroids_match_csharp_fp32_exact():
    cb = BetaLloydMaxCodebook.get(2, 8)
    assert list(cb.centroids) == [
        -0.5048246383666992,
        -0.15792210400104523,
        0.15792210400104523,
        0.5048246383666992,
    ]
    cb4 = BetaLloydMaxCodebook.get(4, 16)
    assert list(cb4.centroids) == [
        -0.6039019227027893,
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


def test_encodes_8dim_vector_to_exact_csharp_base64_payload():
    v8 = [0.1, -0.2, 0.3, -0.4, 0.5, -0.6, 0.7, -0.8]
    assert EmbeddingPayloadCodec.encode_base64(v8, 2) == "VFEzAQIAAAAIAAAAEdK2P9B5"
    assert EmbeddingPayloadCodec.encode_base64(v8, 4) == "VFEzAQQAAAAIAAAAEdK2PzPHpV4="
    assert hexstr(EmbeddingPayloadCodec.encode(v8, 2)) == "54513301020000000800000011d2b63fd079"
    assert hexstr(EmbeddingPayloadCodec.encode(v8, 4)) == "54513301040000000800000011d2b63f33c7a55e"


def test_stores_exact_csharp_norm_in_payload():
    v8 = [0.1, -0.2, 0.3, -0.4, 0.5, -0.6, 0.7, -0.8]
    assert TurboQuantCodec.encode(v8, 2).norm == 1.4282857179641724


def test_encodes_tiny_4dim_vector_to_exact_csharp_byte_layout():
    v4 = [1, 2, 3, 4]
    assert hexstr(EmbeddingPayloadCodec.encode(v4, 2)) == "5451330102000000040000006f45af409c"
    assert EmbeddingPayloadCodec.encode_base64(v4, 2) == "VFEzAQIAAAAEAAAAb0WvQJw="
    assert TurboQuantCodec.encode(v4, 2).norm == 5.4772257804870605


def test_rotation_matrix_row0_dim8_matches_csharp_fp32_exact():
    row0 = list(OrthogonalRotation.get_matrix(8)[:8])
    assert row0 == [
        0.32915404438972473,
        -0.15729351341724396,
        -0.6576523184776306,
        0.4990078806877136,
        -0.2985365092754364,
        -0.17185114324092865,
        0.024059195071458817,
        0.2572260797023773,
    ]


# ══════════════════════════════════════════════════════════════════════════
# BitPacker
# ══════════════════════════════════════════════════════════════════════════


@pytest.mark.parametrize("bits", [1, 2, 3, 4, 8])
def test_bitpacker_round_trips_indices(bits: int):
    max_v = (1 << bits) - 1
    rng = make_mulberry32(123 + bits)
    indices = [int(rng() * (max_v + 1)) for _ in range(256)]

    packed = BitPacker.pack(indices, bits)
    unpacked = BitPacker.unpack(packed, len(indices), bits)

    assert len(unpacked) == len(indices)
    for i in range(len(indices)):
        assert unpacked[i] == indices[i]


def test_bitpacker_byte_count_spec():
    indices = [0] * 1536
    assert len(BitPacker.pack(indices, 2)) == 384


def test_bitpacker_rejects_overflowing_index():
    with pytest.raises(ValueError, match="exceeds 2-bit range"):
        BitPacker.pack([4], 2)


def test_bitpacker_rejects_out_of_range_width():
    with pytest.raises(ValueError):
        BitPacker.pack([0], 0)
    with pytest.raises(ValueError):
        BitPacker.pack([0], 17)


# ══════════════════════════════════════════════════════════════════════════
# OrthogonalRotation
# ══════════════════════════════════════════════════════════════════════════


def test_rotation_preserves_l2_norm():
    dim = 64
    v = random_unit(dim, 42)
    r = [0.0] * dim
    OrthogonalRotation.rotate(dim, v, r)
    sq_a = sum(x * x for x in v)
    sq_r = sum(x * x for x in r)
    assert abs(math.sqrt(sq_r) - math.sqrt(sq_a)) < 1e-3


def test_rotate_then_unrotate_recovers_input():
    dim = 64
    v = random_unit(dim, 7)
    r = [0.0] * dim
    v2 = [0.0] * dim
    OrthogonalRotation.rotate(dim, v, r)
    OrthogonalRotation.unrotate(dim, r, v2)
    for i in range(dim):
        assert abs(v2[i] - v[i]) < 1e-3


def test_rotation_is_deterministic_and_cached():
    a = OrthogonalRotation.get_matrix(32)
    b = OrthogonalRotation.get_matrix(32)
    assert a is b  # cached: identical reference


# ══════════════════════════════════════════════════════════════════════════
# BetaLloydMaxCodebook
# ══════════════════════════════════════════════════════════════════════════


@pytest.mark.parametrize("bits,dim", [(1, 16), (2, 64), (3, 128), (4, 256)])
def test_codebook_has_correct_sizes(bits: int, dim: int):
    cb = BetaLloydMaxCodebook.get(bits, dim)
    n = 1 << bits
    assert len(cb.centroids) == n
    assert len(cb.boundaries) == n - 1


def test_codebook_centroids_strictly_monotonic():
    cb = BetaLloydMaxCodebook.get(4, 128)
    for i in range(1, len(cb.centroids)):
        assert cb.centroids[i] > cb.centroids[i - 1]


def test_codebook_bin_for_round_trips_through_boundaries():
    cb = BetaLloydMaxCodebook.get(2, 64)
    for i in range(len(cb.boundaries)):
        just_before = cb.boundaries[i] - 1e-6
        just_after = cb.boundaries[i] + 1e-6
        assert BetaLloydMaxCodebook.bin_for(just_before, cb.boundaries) == i
        assert BetaLloydMaxCodebook.bin_for(just_after, cb.boundaries) == i + 1


# ══════════════════════════════════════════════════════════════════════════
# TurboQuantCodec end-to-end
# ══════════════════════════════════════════════════════════════════════════


@pytest.mark.parametrize(
    "dim,bits,floor",
    [(64, 4, 0.99), (128, 4, 0.99), (256, 3, 0.96), (512, 2, 0.85)],
)
def test_round_trip_preserves_geometry(dim: int, bits: int, floor: float):
    v = random_unit(dim, 42)
    reconstructed = TurboQuantCodec.round_trip(v, bits)
    assert len(reconstructed) == dim
    cos = cosine(v, reconstructed)
    assert cos >= floor, f"dim={dim} bits={bits}: cos {cos} below floor {floor}"


def test_zero_vector_round_trips_to_zeros():
    z = [0.0] * 64
    r = TurboQuantCodec.round_trip(z, 2)
    for x in r:
        assert x == 0


def test_payload_size_matches_spec():
    assert TurboQuantCodec.payload_byte_count(1536, 2) == 384


def test_compression_ratio_at_1536_2bit_exceeds_15x():
    ratio = TurboQuantCodec.compression_ratio(1536, 2)
    assert ratio > 15.0, f"got {ratio}"
    assert ratio == 15.835051546391753


def test_codec_rejects_invalid_bit_widths():
    v = [0.0] * 32
    v[0] = 1.0
    with pytest.raises(ValueError):
        TurboQuantCodec.encode(v, 0)
    with pytest.raises(ValueError):
        TurboQuantCodec.encode(v, 9)


def test_codec_rejects_length_1_vector():
    with pytest.raises(ValueError):
        TurboQuantCodec.encode([1], 2)


def test_encode_is_deterministic_across_runs():
    v = random_unit(128, 7)
    a = TurboQuantCodec.encode(v, 3)
    b = TurboQuantCodec.encode(v, 3)
    assert a.norm == b.norm
    assert list(a.packed_indices) == list(b.packed_indices)


def test_preserves_inner_product_between_correlated_compressed_vectors():
    dim = 128
    a = random_unit(dim, 1)
    b = random_unit(dim, 2)
    blended = [0.7 * a[i] + 0.3 * b[i] for i in range(dim)]
    bn = sum(x * x for x in blended)
    inv_n = 1.0 / math.sqrt(bn)
    blended = [x * inv_n for x in blended]

    true_cos = cosine(a, blended)
    a_hat = TurboQuantCodec.round_trip(a, 4)
    blend_hat = TurboQuantCodec.round_trip(blended, 4)
    recon_cos = cosine(a_hat, blend_hat)
    assert abs(recon_cos - true_cos) <= 0.05, f"true={true_cos} recon={recon_cos}"


# ══════════════════════════════════════════════════════════════════════════
# EmbeddingPayloadCodec
# ══════════════════════════════════════════════════════════════════════════


def test_payload_codec_round_trip_preserves_cosine_4bit():
    v = random_unit(128, 42)
    encoded = EmbeddingPayloadCodec.encode(v, 4)
    decoded = EmbeddingPayloadCodec.decode(encoded)
    assert cosine(v, decoded) >= 0.99


def test_payload_codec_detects_its_own_header():
    encoded = EmbeddingPayloadCodec.encode(random_unit(64, 1), 2)
    assert EmbeddingPayloadCodec.is_encoded(encoded) is True
    assert EmbeddingPayloadCodec.is_encoded(bytes((0, 1, 2))) is False


def test_payload_codec_rejects_too_short_payload():
    with pytest.raises(ValueError, match="(?i)too short"):
        EmbeddingPayloadCodec.decode(bytes((1, 2, 3)))


def test_payload_codec_rejects_payload_without_magic_header():
    bad = bytes(20)  # right length, wrong magic
    with pytest.raises(ValueError, match="(?i)magic"):
        EmbeddingPayloadCodec.decode(bad)


def test_payload_codec_base64_round_trip_preserves_cosine_3bit():
    v = random_unit(64, 7)
    b64 = EmbeddingPayloadCodec.encode_base64(v, 3)
    back = EmbeddingPayloadCodec.decode_base64(b64)
    assert cosine(v, back) >= 0.96


def test_payload_at_2bits_is_smaller_than_fp32():
    v = random_unit(1536, 42)
    encoded = EmbeddingPayloadCodec.encode(v, 2)
    ratio = (len(v) * 4) / len(encoded)
    assert ratio > 12.0, f"got {ratio}"


# ══════════════════════════════════════════════════════════════════════════
# CompressedEpisodicMemoryStore
# ══════════════════════════════════════════════════════════════════════════


def _episodic(**overrides) -> EpisodicMemoryEntry:
    fields = dict(
        id=overrides.get("id", uuid.uuid4()),
        recorded_at_utc=overrides.get(
            "recorded_at_utc", datetime(2026, 1, 1, tzinfo=timezone.utc)
        ),
        user_text=overrides.get("user_text", "u"),
        assistant_text=overrides.get("assistant_text", "a"),
        app_context=overrides.get("app_context"),
        embedding=overrides.get("embedding"),
        tags=overrides.get("tags"),
    )
    return EpisodicMemoryEntry(**fields)


async def test_episodic_stores_embedding_as_compressed_tag_not_float_array():
    inner = InMemoryEpisodicStore()
    outer = CompressedEpisodicMemoryStore(inner, 2)
    await outer.add_async(
        _episodic(user_text="hello", assistant_text="hi", embedding=random_unit(128, 1))
    )

    raw_recent = await inner.get_recent_async(1)
    assert len(raw_recent) == 1
    assert raw_recent[0].embedding is None
    assert raw_recent[0].tags is not None
    assert COMPRESSED_TAG_KEY in raw_recent[0].tags


async def test_episodic_get_recent_rehydrates_embedding():
    inner = InMemoryEpisodicStore()
    outer = CompressedEpisodicMemoryStore(inner, 4)
    original = random_unit(64, 1)
    await outer.add_async(_episodic(embedding=original))

    got = await outer.get_recent_async(1)
    assert len(got) == 1
    assert got[0].embedding is not None
    assert cosine(original, got[0].embedding) >= 0.99


async def test_episodic_search_ranks_by_cosine_through_compression():
    inner = InMemoryEpisodicStore()
    outer = CompressedEpisodicMemoryStore(inner, 4)
    v1 = random_unit(64, 1)
    v2 = random_unit(64, 2)
    await outer.add_async(_episodic(user_text="near", embedding=v1))
    await outer.add_async(_episodic(user_text="far", embedding=v2))

    results = await outer.search_async(v1, 2)
    assert len(results) == 2
    assert results[0].user_text == "near"


async def test_episodic_search_with_null_query_returns_recency():
    inner = InMemoryEpisodicStore()
    outer = CompressedEpisodicMemoryStore(inner, 4)
    await outer.add_async(
        _episodic(
            user_text="old",
            recorded_at_utc=datetime(2026, 1, 1, tzinfo=timezone.utc),
            embedding=random_unit(32, 1),
        )
    )
    await outer.add_async(
        _episodic(
            user_text="new",
            recorded_at_utc=datetime(2026, 6, 1, tzinfo=timezone.utc),
            embedding=random_unit(32, 2),
        )
    )
    results = await outer.search_async(None, 1)
    assert len(results) == 1
    assert results[0].user_text == "new"


async def test_episodic_entry_without_embedding_passes_through_unchanged():
    inner = InMemoryEpisodicStore()
    outer = CompressedEpisodicMemoryStore(inner)
    await outer.add_async(_episodic(user_text="u", assistant_text="a"))
    raw = await inner.get_recent_async(1)
    assert len(raw) == 1
    assert raw[0].embedding is None
    assert raw[0].tags is None or COMPRESSED_TAG_KEY not in raw[0].tags


def test_episodic_rejects_invalid_bit_width():
    with pytest.raises(ValueError):
        CompressedEpisodicMemoryStore(InMemoryEpisodicStore(), 9)


def test_episodic_exposes_compressed_tag_key_constant():
    assert CompressedEpisodicMemoryStore.CompressedTagKey == "x-tq-embedding"


# ══════════════════════════════════════════════════════════════════════════
# CompressedMultimodalMemoryStore
# ══════════════════════════════════════════════════════════════════════════


def _mm(**overrides) -> MultimodalMemoryEntry:
    return MultimodalMemoryEntry(**overrides)


async def test_multimodal_round_trips_embedding_and_metadata():
    inner = InMemoryMultimodalMemoryStore()
    outer = CompressedMultimodalMemoryStore(inner, 4)
    # 4-bit >= 0.99 is a statistical bound; seed 42 clears it comfortably (~0.9953).
    emb = random_unit(128, 42)
    await outer.add_async(
        _mm(
            source_sha256="deadbeef",
            modality=MediaModality.Image,
            caption="a sunny beach",
            embedding=emb,
            width_px=1920,
            height_px=1080,
        )
    )

    got = await outer.get_by_hash_async("deadbeef")
    assert got is not None
    assert got.caption == "a sunny beach"
    assert got.width_px == 1920
    assert got.height_px == 1080
    assert got.embedding is not None
    assert cosine(emb, got.embedding) >= 0.99


async def test_multimodal_inner_store_sees_null_embedding_and_compressed_tag():
    inner = InMemoryMultimodalMemoryStore()
    outer = CompressedMultimodalMemoryStore(inner)
    await outer.add_async(_mm(source_sha256="abc", caption="x", embedding=random_unit(64, 1)))

    raw = await inner.get_by_hash_async("abc")
    assert raw is not None
    assert raw.embedding is None
    assert raw.tags is not None and COMPRESSED_TAG_KEY in raw.tags


async def test_multimodal_search_ranks_by_cosine_through_compression():
    inner = InMemoryMultimodalMemoryStore()
    outer = CompressedMultimodalMemoryStore(inner, 4)
    v1 = random_unit(64, 1)
    v2 = random_unit(64, 2)
    await outer.add_async(_mm(source_sha256="a", caption="near", embedding=v1))
    await outer.add_async(_mm(source_sha256="b", caption="far", embedding=v2))

    results = await outer.search_async(v1, 2)
    assert len(results) == 2
    assert results[0].caption == "near"


async def test_multimodal_reinforce_and_prune_delegate_through_decorator():
    inner = InMemoryMultimodalMemoryStore()
    outer = CompressedMultimodalMemoryStore(inner, 4)
    await outer.add_async(_mm(source_sha256="x", caption="x", embedding=random_unit(32, 1)))
    await outer.reinforce_async("x")
    got = await outer.get_by_hash_async("x")
    assert got.reference_count == 2
    assert await outer.count_async() == 1
