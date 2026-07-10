"""circle_ai.embeddings — port of CircleAI.Embeddings + CircleAI.Embeddings.Local.

Public surface:
  * text embedding: ITextEmbedder, TextEmbedder, IEmbeddingBackend,
    DeterministicEmbeddingBackend,
  * local store/index: IEmbeddingEncoder, EmbeddingDocument, EmbeddingSearchHit,
    ICircleEmbeddingStore, EmbeddingIndexHit, IEmbeddingIndex,
    InMemoryEmbeddingStore, TurboVecEmbeddingIndex, HnswEmbeddingStore.
"""
from __future__ import annotations

from .local import (
    EmbeddingDocument,
    EmbeddingIndexHit,
    EmbeddingSearchHit,
    HnswEmbeddingStore,
    ICircleEmbeddingStore,
    IEmbeddingEncoder,
    IEmbeddingIndex,
    InMemoryEmbeddingStore,
    TurboVecEmbeddingIndex,
)
from .text_embedder import (
    DeterministicEmbeddingBackend,
    IEmbeddingBackend,
    ITextEmbedder,
    TextEmbedder,
)

__all__ = [
    # text embedding
    "ITextEmbedder",
    "TextEmbedder",
    "IEmbeddingBackend",
    "DeterministicEmbeddingBackend",
    # local store / index
    "IEmbeddingEncoder",
    "EmbeddingDocument",
    "EmbeddingSearchHit",
    "ICircleEmbeddingStore",
    "EmbeddingIndexHit",
    "IEmbeddingIndex",
    "InMemoryEmbeddingStore",
    "TurboVecEmbeddingIndex",
    "HnswEmbeddingStore",
]
