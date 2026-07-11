"""circle_ai.search — port of the CircleAI.Search assembly.

(3.3.0) Search-relevance helpers: query tokenisation, term-frequency /
simple-relevance scoring, and cosine-similarity vector math (SIMD-accelerated in
C#, scalar float32 here). C# is the exact spec.

Public surface:

  * SearchTokenisation — tokenise(text).
  * SearchScoring      — term_frequency / simple_relevance.
  * SimdOps / VectorMath — cosine_similarity (also the free cosine_similarity fn).
"""
from __future__ import annotations

from .search_primitives import (
    SearchScoring,
    SearchTokenisation,
    SimdOps,
    VectorMath,
    cosine_similarity,
)

__all__ = [
    "SearchTokenisation",
    "SearchScoring",
    "SimdOps",
    "VectorMath",
    "cosine_similarity",
]
