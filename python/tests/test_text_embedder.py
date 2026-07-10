"""test_text_embedder.py

Verifies CircleAI.Embeddings.TextEmbedder + the injected IEmbeddingBackend
seam. C# is the spec: lazy once-only backend init, IModelManager path
resolution + checksum verification, L2-normalised output, empty-text guard.
"""
from __future__ import annotations

import hashlib
import math

import pytest

from circle_ai.core.model_manager import IModelManager
from circle_ai.embeddings import (
    DeterministicEmbeddingBackend,
    IEmbeddingBackend,
    TextEmbedder,
)


class _FakeManager(IModelManager):
    """Deterministic IModelManager that verifies against a fixed checksum."""

    def __init__(self, good_checksum: bytes) -> None:
        self._good = good_checksum
        self.path_calls = 0

    async def get_model_path_async(self, model_id: str, ct: object = None) -> str:
        self.path_calls += 1
        assert model_id == "embedding"
        return "/models/embedding"

    async def verify_model_async(
        self, model_path: str, expected_checksum: bytes, ct: object = None
    ) -> bool:
        return expected_checksum == self._good


_GOOD = hashlib.sha256(b"ok").digest()


async def test_generate_is_deterministic_and_l2_normalised() -> None:
    emb = TextEmbedder(_FakeManager(_GOOD), _GOOD)
    v1 = await emb.generate_async("the quick brown fox")
    v2 = await emb.generate_async("the quick brown fox")
    assert v1 == v2
    assert math.sqrt(sum(x * x for x in v1)) == pytest.approx(1.0, abs=1e-5)


async def test_distinct_text_distinct_vectors() -> None:
    emb = TextEmbedder(_FakeManager(_GOOD), _GOOD)
    assert await emb.generate_async("alpha") != await emb.generate_async("beta")


async def test_empty_text_rejected() -> None:
    emb = TextEmbedder(_FakeManager(_GOOD), _GOOD)
    with pytest.raises(ValueError):
        await emb.generate_async("   ")


async def test_checksum_failure_raises() -> None:
    emb = TextEmbedder(_FakeManager(_GOOD), b"\x00" * 32)
    with pytest.raises(ValueError) as ei:
        await emb.generate_async("hello")
    assert "checksum" in str(ei.value).lower()


async def test_backend_initialised_once() -> None:
    mgr = _FakeManager(_GOOD)
    emb = TextEmbedder(mgr, _GOOD)
    await emb.generate_async("a")
    await emb.generate_async("b")
    await emb.generate_async("c")
    # Model path resolved exactly once — the backend is cached.
    assert mgr.path_calls == 1


async def test_injected_backend_factory_is_used() -> None:
    captured = {}

    class Spy(IEmbeddingBackend):
        def __init__(self, path: str) -> None:
            captured["path"] = path

        @property
        def dimension(self) -> int:
            return 3

        def embed(self, text: str):
            return [0.0, 0.0, 1.0]

    emb = TextEmbedder(_FakeManager(_GOOD), _GOOD, backend_factory=Spy)
    out = await emb.generate_async("x")
    assert out == [0.0, 0.0, 1.0]
    assert captured["path"] == "/models/embedding"


async def test_dispose_blocks_further_use() -> None:
    emb = TextEmbedder(_FakeManager(_GOOD), _GOOD)
    await emb.generate_async("x")
    emb.dispose()
    with pytest.raises(RuntimeError):
        await emb.generate_async("y")


def test_deterministic_backend_requires_path() -> None:
    with pytest.raises(ValueError):
        DeterministicEmbeddingBackend("")
