"""test_embedding_store.py

Verifies the CircleAI.Embeddings.Local ports:
  * InMemoryEmbeddingStore  — brute-force cosine over TurboQuant vectors,
                              CELQ file round-trip.
  * TurboVecEmbeddingIndex  — deterministic in-memory vector index.
  * HnswEmbeddingStore      — add-only store + HGCS docs sidecar round-trip.

C# is the spec; the on-disk magics (0x4C455143 "CELQ", 0x53434847 "HGCS") and
BinaryWriter framing are asserted.
"""
from __future__ import annotations

import os
import struct

import pytest

from circle_ai.embeddings import (
    DeterministicEmbeddingBackend,
    EmbeddingDocument,
    HnswEmbeddingStore,
    IEmbeddingEncoder,
    InMemoryEmbeddingStore,
    TurboVecEmbeddingIndex,
)


class _Encoder(IEmbeddingEncoder):
    def __init__(self, dim: int) -> None:
        self._dim = dim
        self._backend = DeterministicEmbeddingBackend("model", dim)

    @property
    def dimension(self) -> int:
        return self._dim

    async def encode_async(self, text: str, ct: object = None):
        return self._backend.embed(text)


# ── InMemoryEmbeddingStore ───────────────────────────────────────────────────


async def test_inmem_add_count_and_search_exact_match() -> None:
    store = InMemoryEmbeddingStore(_Encoder(16))
    await store.add_async(EmbeddingDocument("a", "hello world"))
    await store.add_async(EmbeddingDocument("b", "totally different text"))
    await store.add_async(EmbeddingDocument("c", "hello world extra"))
    assert store.count == 3
    hits = await store.search_async("hello world", top_k=3)
    # The exact-text doc scores highest. Not exactly 1.0 — vectors are
    # TurboQuant-compressed at 4 bits/dim, so the self-match cosine is close
    # to but below 1.0 (quantisation error). It still ranks first.
    assert hits[0].document.id == "a"
    assert hits[0].score == pytest.approx(1.0, abs=0.02)
    assert hits[0].score >= hits[1].score
    await store.dispose_async()


async def test_inmem_add_replaces_by_id() -> None:
    store = InMemoryEmbeddingStore(_Encoder(16))
    await store.add_async(EmbeddingDocument("a", "first"))
    await store.add_async(EmbeddingDocument("a", "second"))
    assert store.count == 1
    await store.dispose_async()


async def test_inmem_remove() -> None:
    store = InMemoryEmbeddingStore(_Encoder(16))
    await store.add_async(EmbeddingDocument("a", "x"))
    assert await store.remove_async("a") is True
    assert await store.remove_async("a") is False
    assert store.count == 0
    await store.dispose_async()


async def test_inmem_search_with_precomputed_vector() -> None:
    enc = _Encoder(16)
    store = InMemoryEmbeddingStore(enc)
    vec = await enc.encode_async("anchor")
    await store.add_async(EmbeddingDocument("a", "anchor"), vec)
    hits = await store.search_async(vec, top_k=1)
    assert hits[0].document.id == "a"
    await store.dispose_async()


async def test_inmem_topk_limits_results() -> None:
    store = InMemoryEmbeddingStore(_Encoder(16))
    for i in range(5):
        await store.add_async(EmbeddingDocument(f"d{i}", f"text number {i}"))
    hits = await store.search_async("text number 2", top_k=2)
    assert len(hits) == 2
    # Results are ordered by descending score.
    assert hits[0].score >= hits[1].score
    await store.dispose_async()


async def test_inmem_save_load_roundtrip_preserves_docs_and_metadata(tmp_path) -> None:
    enc = _Encoder(16)
    store = InMemoryEmbeddingStore(enc)
    await store.add_async(EmbeddingDocument("a", "hello", {"lang": "en", "src": "test"}))
    await store.add_async(EmbeddingDocument("b", "world"))
    path = str(tmp_path / "store.celq")
    await store.save_async(path)

    store2 = InMemoryEmbeddingStore(enc)
    await store2.load_async(path)
    assert store2.count == 2
    hits = await store2.search_async("hello", top_k=1)
    assert hits[0].document.id == "a"
    assert hits[0].document.metadata == {"lang": "en", "src": "test"}
    await store.dispose_async()
    await store2.dispose_async()


async def test_inmem_file_magic_is_celq(tmp_path) -> None:
    store = InMemoryEmbeddingStore(_Encoder(16))
    await store.add_async(EmbeddingDocument("a", "x"))
    path = str(tmp_path / "s.bin")
    await store.save_async(path)
    with open(path, "rb") as fh:
        head = fh.read(8)
    magic = struct.unpack_from("<i", head, 0)[0]
    version = struct.unpack_from("<H", head, 4)[0]
    assert magic == 0x4C455143  # "CELQ"
    assert version == 1
    await store.dispose_async()


async def test_inmem_load_rejects_bad_magic(tmp_path) -> None:
    path = str(tmp_path / "bad.bin")
    with open(path, "wb") as fh:
        fh.write(struct.pack("<i", 0xDEADBEEF - (1 << 32)) + b"\x00" * 32)
    store = InMemoryEmbeddingStore(_Encoder(16))
    with pytest.raises(ValueError):
        await store.load_async(path)
    await store.dispose_async()


async def test_inmem_bits_per_dim_validation() -> None:
    with pytest.raises(ValueError):
        InMemoryEmbeddingStore(_Encoder(16), bits_per_dim=0)
    with pytest.raises(ValueError):
        InMemoryEmbeddingStore(_Encoder(16), bits_per_dim=9)


async def test_inmem_vector_length_mismatch_raises() -> None:
    store = InMemoryEmbeddingStore(_Encoder(16))
    with pytest.raises(ValueError):
        await store.add_async(EmbeddingDocument("a", "x"), [0.0] * 8)
    await store.dispose_async()


# ── TurboVecEmbeddingIndex ───────────────────────────────────────────────────


async def test_turbovec_add_returns_insertion_ids() -> None:
    idx = TurboVecEmbeddingIndex(16, bit_width=4)
    id0 = await idx.add_async([0.1] * 16)
    id1 = await idx.add_async([0.2] * 16)
    assert (id0, id1) == (0, 1)
    assert idx.count == 2


async def test_turbovec_dimension_must_be_multiple_of_8() -> None:
    with pytest.raises(ValueError):
        TurboVecEmbeddingIndex(15)
    with pytest.raises(ValueError):
        TurboVecEmbeddingIndex(0)


async def test_turbovec_bit_width_validation() -> None:
    with pytest.raises(ValueError):
        TurboVecEmbeddingIndex(16, bit_width=1)
    with pytest.raises(ValueError):
        TurboVecEmbeddingIndex(16, bit_width=5)


async def test_turbovec_search_ranks_by_cosine() -> None:
    idx = TurboVecEmbeddingIndex(8)
    await idx.add_async([1, 0, 0, 0, 0, 0, 0, 0])
    await idx.add_async([0, 1, 0, 0, 0, 0, 0, 0])
    hits = await idx.search_async([1, 0, 0, 0, 0, 0, 0, 0], top_k=2)
    assert hits[0].internal_id == 0
    assert hits[0].score == pytest.approx(1.0, abs=1e-6)


async def test_turbovec_search_empty_returns_empty() -> None:
    idx = TurboVecEmbeddingIndex(8)
    assert await idx.search_async([0.1] * 8, top_k=3) == []


async def test_turbovec_save_load_roundtrip(tmp_path) -> None:
    idx = TurboVecEmbeddingIndex(8)
    await idx.add_async([1, 0, 0, 0, 0, 0, 0, 0])
    await idx.add_async([0, 0, 1, 0, 0, 0, 0, 0])
    path = str(tmp_path / "idx.tvb")
    await idx.save_async(path)

    idx2 = TurboVecEmbeddingIndex(8)
    await idx2.load_async(path)
    assert idx2.count == 2
    hits = await idx2.search_async([0, 0, 1, 0, 0, 0, 0, 0], top_k=1)
    assert hits[0].internal_id == 1


# ── HnswEmbeddingStore ───────────────────────────────────────────────────────


async def test_hnsw_requires_dimension_multiple_of_8() -> None:
    with pytest.raises(ValueError):
        HnswEmbeddingStore(_Encoder(15))


async def test_hnsw_add_search_and_add_duplicate_raises() -> None:
    store = HnswEmbeddingStore(_Encoder(16))
    await store.add_async(EmbeddingDocument("a", "apple"))
    await store.add_async(EmbeddingDocument("b", "banana"))
    hits = await store.search_async("apple", top_k=1)
    assert hits[0].document.id == "a"
    with pytest.raises(RuntimeError):
        await store.add_async(EmbeddingDocument("a", "again"))
    await store.dispose_async()


async def test_hnsw_remove_hides_from_search() -> None:
    store = HnswEmbeddingStore(_Encoder(16))
    await store.add_async(EmbeddingDocument("a", "apple"))
    await store.add_async(EmbeddingDocument("b", "banana bread muffin"))
    assert await store.remove_async("a") is True
    hits = await store.search_async("apple", top_k=5)
    assert all(h.document.id != "a" for h in hits)
    await store.dispose_async()


async def test_hnsw_save_load_roundtrip_with_sidecar(tmp_path) -> None:
    enc = _Encoder(16)
    store = HnswEmbeddingStore(enc)
    await store.add_async(EmbeddingDocument("a", "apple", {"k": "v"}))
    await store.add_async(EmbeddingDocument("b", "banana"))
    await store.remove_async("b")
    path = str(tmp_path / "h.idx")
    await store.save_async(path)
    assert os.path.isfile(path)
    assert os.path.isfile(path + ".docs")

    store2 = HnswEmbeddingStore(enc)
    await store2.load_async(path)
    hits = await store2.search_async("apple", top_k=1)
    assert hits[0].document.id == "a"
    assert hits[0].document.metadata == {"k": "v"}
    # 'b' was removed before save; it must stay hidden after reload.
    assert all(h.document.id != "b" for h in await store2.search_async("banana", top_k=5))
    await store.dispose_async()
    await store2.dispose_async()


async def test_hnsw_docs_sidecar_magic_is_hgcs(tmp_path) -> None:
    store = HnswEmbeddingStore(_Encoder(16))
    await store.add_async(EmbeddingDocument("a", "x"))
    path = str(tmp_path / "h.idx")
    await store.save_async(path)
    with open(path + ".docs", "rb") as fh:
        head = fh.read(6)
    magic = struct.unpack_from("<i", head, 0)[0]
    version = struct.unpack_from("<H", head, 4)[0]
    assert magic == 0x53434847  # "HGCS"
    assert version == 1
    await store.dispose_async()
