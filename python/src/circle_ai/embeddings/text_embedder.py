# embeddings/text_embedder.py
#
# Port of:
#   • CircleAI.Embeddings.ITextEmbedder
#   • CircleAI.Embeddings.TextEmbedder (+ the internal IEmbeddingBackend seam)
#
# The C# production backend is MnnEmbeddingBackend (native MNN). Python has no
# MNN binding, so the backend is injected behind IEmbeddingBackend. The default
# is DeterministicEmbeddingBackend — a hash-seeded, L2-normalised encoder that
# is fully deterministic (same text -> same vector) and honours the whole
# contract. Hosts inject a real backend via the ``backend_factory`` argument.
#
# Orchestration ported faithfully:
#   • lazy, once-only backend init serialised by an asyncio.Lock,
#   • model path resolution + checksum verification via IModelManager,
#   • L2 normalisation so cosine similarity reduces to a dot product.

from __future__ import annotations

import asyncio
import hashlib
import math
import struct
from abc import ABC, abstractmethod
from typing import Callable, List, Optional

from ..core.model_manager import IModelManager

__all__ = [
    "ITextEmbedder",
    "TextEmbedder",
    "IEmbeddingBackend",
    "DeterministicEmbeddingBackend",
]


def _f32(x: float) -> float:
    return struct.unpack("<f", struct.pack("<f", x))[0]


# ─────────────────────────────────────────────────────────────────────────────
# ITextEmbedder — the public contract.
# ─────────────────────────────────────────────────────────────────────────────


class ITextEmbedder(ABC):
    """Produces a dense embedding for a piece of text."""

    @abstractmethod
    async def generate_async(self, text: str, ct: object = None) -> List[float]:
        """Embed *text* into an L2-normalised float vector."""
        raise NotImplementedError


# ─────────────────────────────────────────────────────────────────────────────
# IEmbeddingBackend — internal backend seam (injectable for testability).
# ─────────────────────────────────────────────────────────────────────────────


class IEmbeddingBackend(ABC):
    """Backend that turns text into an L2-normalised vector. Disposable.

    Not thread-safe — :class:`TextEmbedder` serialises callers.
    """

    @property
    @abstractmethod
    def dimension(self) -> int:
        """Number of floats returned by :meth:`embed`."""
        raise NotImplementedError

    @abstractmethod
    def embed(self, text: str) -> List[float]:
        """Embed *text*; return an L2-normalised vector."""
        raise NotImplementedError

    def dispose(self) -> None:
        return None


# ─────────────────────────────────────────────────────────────────────────────
# DeterministicEmbeddingBackend — default network-free backend.
# ─────────────────────────────────────────────────────────────────────────────


class DeterministicEmbeddingBackend(IEmbeddingBackend):
    """Deterministic stand-in for the native MNN embedding backend.

    Derives a stable pseudo-embedding from a SHA-256 stream keyed on the input
    text, then L2-normalises it. Same text always yields the same vector, and
    different texts yield different vectors — enough to exercise the full
    TextEmbedder + embedding-store pipeline without a native model.
    """

    def __init__(self, model_path: str, dimension: int = 64) -> None:
        if model_path is None or model_path.strip() == "":
            raise ValueError("Model path is required.")
        if dimension <= 0:
            raise ValueError("dimension must be > 0")
        self._model_path = model_path
        self._dimension = dimension
        self._disposed = False

    @property
    def dimension(self) -> int:
        return self._dimension

    def embed(self, text: str) -> List[float]:
        if self._disposed:
            raise RuntimeError("DeterministicEmbeddingBackend is disposed")
        out = [0.0] * self._dimension
        # Expand a hash stream to fill `dimension` float32 values in [-1, 1).
        needed = self._dimension * 4
        buf = bytearray()
        counter = 0
        seed = text.encode("utf-8")
        while len(buf) < needed:
            h = hashlib.sha256()
            h.update(seed)
            h.update(counter.to_bytes(4, "little"))
            buf += h.digest()
            counter += 1
        for i in range(self._dimension):
            word = int.from_bytes(buf[i * 4 : i * 4 + 4], "little")
            # Map to [-1, 1).
            out[i] = _f32((word / 4294967296.0) * 2.0 - 1.0)
        self._l2_normalize(out)
        return out

    def dispose(self) -> None:
        self._disposed = True

    @staticmethod
    def _l2_normalize(v: List[float]) -> None:
        norm = 0.0
        for x in v:
            norm += float(x) * float(x)
        norm = math.sqrt(norm)
        if norm < 1e-12:
            return
        scale = _f32(1.0 / norm)
        for i in range(len(v)):
            v[i] = _f32(v[i] * scale)


# ─────────────────────────────────────────────────────────────────────────────
# TextEmbedder — public orchestration shell over IEmbeddingBackend.
# ─────────────────────────────────────────────────────────────────────────────


class TextEmbedder(ITextEmbedder):
    """On-device text embedder backed by an :class:`IEmbeddingBackend`.

    Resolves + verifies the model path through an :class:`IModelManager`, then
    lazily builds the backend on first use. Returns L2-normalised vectors.
    """

    def __init__(
        self,
        model_manager: IModelManager,
        expected_checksum: bytes,
        backend_factory: Optional[Callable[[str], IEmbeddingBackend]] = None,
    ) -> None:
        if model_manager is None:
            raise ValueError("model_manager")
        if expected_checksum is None:
            raise ValueError("expected_checksum")
        self._model_manager = model_manager
        self._expected_checksum = expected_checksum
        self._backend_factory: Callable[[str], IEmbeddingBackend] = (
            backend_factory
            if backend_factory is not None
            else (lambda path: DeterministicEmbeddingBackend(path))
        )
        self._backend: Optional[IEmbeddingBackend] = None
        self._init_gate = asyncio.Lock()
        self._disposed = False

    async def generate_async(self, text: str, ct: object = None) -> List[float]:
        if self._disposed:
            raise RuntimeError("TextEmbedder is disposed")
        if text is None or text.strip() == "":
            raise ValueError("Text cannot be empty.")
        backend = await self._ensure_backend_async(ct)
        return backend.embed(text)

    async def _ensure_backend_async(self, ct: object) -> IEmbeddingBackend:
        if self._backend is not None:
            return self._backend
        async with self._init_gate:
            if self._backend is not None:
                return self._backend
            path = await self._model_manager.get_model_path_async("embedding", ct)
            verified = await self._model_manager.verify_model_async(
                path, self._expected_checksum, ct
            )
            if not verified:
                raise ValueError(
                    "Embedding model checksum verification failed. "
                    "The file may be corrupt or tampered with."
                )
            self._backend = self._backend_factory(path)
            return self._backend

    def dispose(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        if self._backend is not None:
            self._backend.dispose()
