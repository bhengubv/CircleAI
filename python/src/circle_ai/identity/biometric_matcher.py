from __future__ import annotations

import math

from .biometric_profile import BiometricProfile


def cosine_similarity(a: list[float], b: list[float]) -> float:
    """Cosine similarity between two embedding vectors.

    Double-precision accumulator for cross-platform reproducibility; stdlib
    only, no numpy. The cross-port contract is
    ``fixtures/facex_biometric_vectors.json`` — every port must agree with it
    inside each entry's stated tolerance.

    Vectors of different dimensions are REFUSED, not scored. A similarity
    between embeddings of different sizes has no meaning, and answering one
    anyway is a false-match path: this used to hand ``zip`` two unequal lists,
    so ``[1.0]`` against ``[1.0, 0.5]`` was silently truncated to its first
    element and scored 0.894 — comfortably past the 0.85 default threshold.
    C#, Kotlin, TypeScript and HarmonyOS all raise here; the message below is
    word-for-word theirs.

    Returns 0.0 when either vector is empty or of near-zero magnitude, and
    clamps the result so float rounding cannot report a similarity outside
    [-1.0, 1.0].

    Raises:
        ValueError: if ``a`` and ``b`` have different lengths.
    """
    if len(a) != len(b):
        raise ValueError(
            f"Embedding dimension mismatch: a={len(a)}, b={len(b)}. "
            "Both vectors must come from the same model."
        )

    dot = sum(float(x) * float(y) for x, y in zip(a, b))
    mag_a = math.sqrt(sum(float(x) * float(x) for x in a))
    mag_b = math.sqrt(sum(float(y) * float(y) for y in b))

    # Near-zero rather than exactly zero, matching Kotlin/Swift/Go/Rust/C: a
    # vector of 1e-30s has a non-zero magnitude and would divide into noise.
    if mag_a < 1e-10 or mag_b < 1e-10:
        return 0.0

    return max(-1.0, min(1.0, float(dot / (mag_a * mag_b))))


def is_match(live_embedding: list[float], profile: BiometricProfile) -> bool:
    """Return True if the live embedding matches the stored profile.

    Comparison is cosine_similarity >= profile.match_threshold (inclusive).

    Propagates the ValueError from cosine_similarity when the live embedding
    and the enrolled profile have different dimensions — that is a mismatched
    model, not a failed match, and it should be seen rather than read as a
    quiet False.
    """
    return cosine_similarity(live_embedding, profile.embedding_vector) >= profile.match_threshold
