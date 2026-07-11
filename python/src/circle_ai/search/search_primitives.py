# search_primitives.py
#
# Port of CircleAI.Search:
#   • SearchPrimitives.cs — SearchTokenisation.Tokenise, SearchScoring.TermFrequency
#     / SimpleRelevance (BM25-flavoured relevance helpers).
#   • SimdOps.cs / VectorSearch.cs — SimdOps.CosineSimilarity + VectorMath.
#     CosineSimilarity (SIMD-accelerated in C#; a plain scalar loop here — same
#     result, no hardware-vector dependency).
#
# C# is the EXACT spec. The two cosine helpers live in the global namespace in
# C# (no `namespace CircleAI.Search;`), but ship in the CircleAI.Search project,
# so they are re-exported from this package.
#
# Float sites use float32 (struct.pack("<f", x)) so the accumulated dot / norm
# match the C# `float` arithmetic bit-for-bit at the reduction boundary.

from __future__ import annotations

import math
import struct
from typing import List, Sequence

__all__ = [
    "SearchTokenisation",
    "SearchScoring",
    "SimdOps",
    "VectorMath",
    "cosine_similarity",
]

_SPLIT_CHARS = {
    " ",
    "\n",
    "\r",
    "\t",
    ",",
    ".",
    ";",
    ":",
    "(",
    ")",
    "[",
    "]",
    '"',
    "'",
}


def _f32(x: float) -> float:
    return struct.unpack("<f", struct.pack("<f", x))[0]


def _tokenise(text: str) -> List[str]:
    # Mirrors string.Split(chars, RemoveEmptyEntries).Select(Trim().ToLowerInvariant())
    # .Where(len > 0).
    out: List[str] = []
    token = []
    for ch in text:
        if ch in _SPLIT_CHARS:
            if token:
                out.append("".join(token))
                token = []
        else:
            token.append(ch)
    if token:
        out.append("".join(token))
    result: List[str] = []
    for t in out:
        trimmed = t.strip().lower()
        if len(trimmed) > 0:
            result.append(trimmed)
    return result


class SearchTokenisation:
    """Mirrors ``CircleAI.Search.SearchTokenisation`` (static helper)."""

    @staticmethod
    def tokenise(text: str) -> List[str]:
        """Split *text* into lower-cased word tokens (whitespace + punctuation
        delimited, empties removed)."""
        if text is None:
            raise ValueError("text")
        return _tokenise(text)


class SearchScoring:
    """Mirrors ``CircleAI.Search.SearchScoring`` (static helper)."""

    @staticmethod
    def term_frequency(term: str, doc_tokens: Sequence[str]) -> float:
        """Fraction of ``doc_tokens`` equal to *term* (ordinal compare)."""
        if doc_tokens is None:
            raise ValueError("doc_tokens")
        if len(doc_tokens) == 0:
            return 0.0
        c = 0
        for t in doc_tokens:
            if t == term:
                c += 1
        return float(c) / len(doc_tokens)

    @staticmethod
    def simple_relevance(
        query_tokens: Sequence[str], doc_tokens: Sequence[str]
    ) -> float:
        """Summed term-frequency of every query token in the document."""
        if query_tokens is None:
            raise ValueError("query_tokens")
        if doc_tokens is None:
            raise ValueError("doc_tokens")
        if len(query_tokens) == 0 or len(doc_tokens) == 0:
            return 0.0
        score = 0.0
        for q in query_tokens:
            score += SearchScoring.term_frequency(q, doc_tokens)
        return score


def cosine_similarity(a: Sequence[float], b: Sequence[float]) -> float:
    """Cosine similarity of two equal-length non-empty float vectors.

    Scalar port of the SIMD-accelerated C# ``SimdOps.CosineSimilarity`` /
    ``VectorMath.CosineSimilarity``. float32 accumulation matches the C# ``float``
    reduction.
    """
    if len(a) != len(b) or len(a) == 0:
        raise ValueError("Vectors must be the same non-zero length.")
    dot = 0.0
    norm_a = 0.0
    norm_b = 0.0
    for i in range(len(a)):
        ai = _f32(a[i])
        bi = _f32(b[i])
        dot = _f32(dot + _f32(ai * bi))
        norm_a = _f32(norm_a + _f32(ai * ai))
        norm_b = _f32(norm_b + _f32(bi * bi))
    return _f32(dot / _f32(_f32(math.sqrt(norm_a)) * _f32(math.sqrt(norm_b))))


class SimdOps:
    """Mirrors the (global-namespace) ``SimdOps`` type shipped in CircleAI.Search."""

    @staticmethod
    def cosine_similarity(a: Sequence[float], b: Sequence[float]) -> float:
        return cosine_similarity(a, b)


class VectorMath:
    """Mirrors the (global-namespace) ``VectorMath`` type shipped in CircleAI.Search."""

    @staticmethod
    def cosine_similarity(a: Sequence[float], b: Sequence[float]) -> float:
        return cosine_similarity(a, b)
